using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// The proxy: a transparent stdio<->Streamable-HTTP pipe with a durable remote side.
//
// stdout carries JSON-RPC framing ONLY (one stray line silently corrupts the stream Claude
// Desktop parses, so every log below goes to stderr and the only writer to stdio is the
// single outbound pump).
//
// Streamable HTTP sessions are stateful: a reconnect lands on a server that has never seen
// initialize, while Claude Desktop on the other side still believes it initialized long ago.
// So the client's initialize (and notifications/initialized) are cached on the way through and
// replayed after every reconnect, with the replayed response swallowed - the client only ever
// sees the answer to the one initialize it actually sent.
static class Bridge
{
  public static Task<int> Run(Uri url) => new Pump(url).RunAsync();

  sealed class Pump(Uri url)
  {
    sealed class Session
    {
      public required ITransport Transport;        // the connected remote pipe
      public required HttpClientTransport Owner;   // disposed when the session dies
      public readonly SemaphoreSlim SendLock = new(1, 1);
      public readonly ConcurrentDictionary<RequestId, byte> Outstanding = new();
      public readonly TaskCompletionSource Dead = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public ReplayWait? PendingReplay;            // set while an initialize replay is in flight
    }

    sealed record ReplayWait(RequestId ReplayId, TaskCompletionSource Done);

    readonly object _gate = new();
    readonly List<(JsonRpcRequest Request, DateTimeOffset Deadline)> _held = new();
    readonly Channel<JsonRpcMessage> _toStdio = Channel.CreateUnbounded<JsonRpcMessage>();
    readonly CancellationTokenSource _shutdown = new();
    readonly HttpClient _http = Shared.NewHttpClient();
    ClientOAuthOptions _oauth = null!;

    Session? _current;                    // null while disconnected
    StdioServerTransport _stdio = null!;
    JsonRpcRequest? _cachedInit;          // the client's initialize, replayed on each reconnect
    JsonRpcNotification? _cachedInitNotif;
    RequestId? _swapInitId;               // client's initialize id unanswered (first connect only)
    bool _everConnected;
    bool _authDenied;                     // a 401 before first connect: the cached token is dead
    bool _reauthActive;
    Task<AuthorizationResult?>? _authFlight;
    DateTimeOffset _authCooldownUntil = DateTimeOffset.MinValue;
    int _replayCounter;
    int _exitCode;

    public async Task<int> RunAsync()
    {
      if (await new TokenStore(url).GetTokensAsync() is null)
      {
        // Claude Desktop surfaces this stderr line and the fix is one command away.
        Console.Error.WriteLine($"lmfriend: no valid credentials for {url} - run: lmfriend login {url}");
        return 2;
      }

      _oauth = new ClientOAuthOptions
      {
        RedirectUri = new Uri($"http://127.0.0.1:{Setup.PickPort()}/callback"),
        TokenCache = new TokenStore(url),
        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "lmfriend" },
        AuthorizationCallbackHandler = OnOAuthCallback,
      };

      // EOF on stdin, Ctrl-C or SIGTERM: Claude Desktop deliberately closing us down - the
      // one clean exit. Everything else we try to survive.
      Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
      using var sigterm = OperatingSystem.IsWindows() ? null :
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Shutdown(); });

      _stdio = new StdioServerTransport("lmfriend", Shared.Logs);
      var stdioWriter = Task.Run(StdioWriterLoop);
      var reaper = Task.Run(ReaperLoop);
      var stdinReader = Task.Run(StdinReaderLoop);

      await ConnectLoop();

      _toStdio.Writer.TryComplete();
      if (Volatile.Read(ref _current) is { } s) KillSession(s, "shutting down");
      foreach (var t in new[] { stdioWriter, reaper, stdinReader })
        try { await t; } catch { }
      await _stdio.DisposeAsync();
      return _exitCode;
    }

    void Shutdown() => _shutdown.Cancel();

    // The ONLY place messages go out to Claude Desktop.
    async Task StdioWriterLoop()
    {
      try
      {
        await foreach (var msg in _toStdio.Reader.ReadAllAsync(_shutdown.Token))
          await _stdio.SendMessageAsync(msg, _shutdown.Token);
      }
      catch (OperationCanceledException) { }
      catch (Exception e)
      {
        Console.Error.WriteLine($"lmfriend: stdout write failed ({Setup.OneLine(e)}) - our parent is gone, exiting.");
        Shutdown();
      }
    }

    // Reads Claude Desktop: caches the handshake for replays, forwards or holds the rest.
    async Task StdinReaderLoop()
    {
      try
      {
        await foreach (var msg in _stdio.MessageReader.ReadAllAsync(_shutdown.Token))
        {
          if (msg is JsonRpcRequest { Method: "initialize" } init)
            lock (_gate) _cachedInit = new JsonRpcRequest { Method = init.Method, Params = init.Params?.DeepClone() };
          else if (msg is JsonRpcNotification { Method: "notifications/initialized" } n)
            lock (_gate) _cachedInitNotif = new JsonRpcNotification { Method = n.Method, Params = n.Params?.DeepClone() };

          if (await TrySendRemote(msg)) continue;

          // No connection right now: requests wait briefly (the reaper answers them if the
          // wait outlives its budget); notifications and stray responses have no waiter, so
          // we just log and drop them.
          if (msg is JsonRpcRequest r)
            lock (_gate) _held.Add((r, DateTimeOffset.UtcNow.AddSeconds(10)));
          else
            Console.Error.WriteLine($"lmfriend: disconnected - dropped {Describe(msg)}");
        }
      }
      catch (OperationCanceledException) { }
      catch (Exception e) { Console.Error.WriteLine($"lmfriend: stdin read failed ({Setup.OneLine(e)})"); }
      Shutdown();
    }

    // Answers held requests whose grace period ran out while we were disconnected.
    async Task ReaperLoop()
    {
      while (!_shutdown.IsCancellationRequested)
      {
        try { await Task.Delay(500, _shutdown.Token); }
        catch (OperationCanceledException) { break; }
        List<JsonRpcRequest> expired;
        lock (_gate)
        {
          var now = DateTimeOffset.UtcNow;
          expired = _held.Where(h => h.Deadline < now).Select(h => h.Request).ToList();
          _held.RemoveAll(h => h.Deadline < now);
        }
        foreach (var req in expired)
          _toStdio.Writer.TryWrite(ErrorFor(req.Id,
            $"no connection to {url} right now - lmfriend is still reconnecting, try again shortly."));
      }
    }

    // Owns the remote side: connect, replay the handshake, live until the session dies, repeat forever.
    async Task ConnectLoop()
    {
      var backoff = 1000;
      while (!_shutdown.IsCancellationRequested)
      {
        Session? s = null;
        try
        {
          var transport = new HttpClientTransport(new HttpClientTransportOptions
          {
            Endpoint = url,
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = _oauth,
            // We own reconnects at the transport level; the SDK's built-in ones just try to
            // resume a session that's already gone.
            MaxReconnectionAttempts = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
          }, _http, Shared.Logs, ownsHttpClient: false);

          var remote = await transport.ConnectAsync(_shutdown.Token);
          s = new Session { Transport = remote, Owner = transport };
          _ = Task.Run(() => RemoteReaderLoop(s));
          await ReplayHandshake(s);

          lock (_gate) _current = s;
          Volatile.Write(ref _everConnected, true);
          Console.Error.WriteLine($"lmfriend: connected to {url} (session {remote.SessionId ?? "-"})");
          FlushHeld();
          backoff = 1000;
          await s.Dead.Task.WaitAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
          if (s is not null) KillSession(s, "shutdown");
          break;
        }
        catch (Exception e)
        {
          if (s is not null) KillSession(s, Setup.OneLine(e));
          if (Volatile.Read(ref _authDenied) && !Volatile.Read(ref _everConnected))
          {
            Console.Error.WriteLine($"lmfriend: no valid credentials for {url} - run: lmfriend login {url}");
            _exitCode = 2;
            break;
          }
          Console.Error.WriteLine($"lmfriend: connect to {url} failed ({Setup.OneLine(e)}) - retrying in {backoff / 1000.0:0.#}s");
          try { await Task.Delay((int)(backoff * (0.8 + 0.4 * Random.Shared.NextDouble())), _shutdown.Token); }
          catch (OperationCanceledException) { break; }
          backoff = Math.Min(backoff * 2, 30_000);
        }
      }
    }

    // After every (re)connect, replay the client's initialize with an id from our own
    // namespace so it can't collide with a client id. The response is swallowed, except on
    // the very first connect, where the client's original initialize is still waiting - then
    // we swap ids and hand the replayed result to the client.
    async Task ReplayHandshake(Session s)
    {
      JsonRpcRequest? init;
      JsonRpcNotification? notif;
      lock (_gate)
      {
        init = _cachedInit;
        notif = _cachedInitNotif;
        _swapInitId = _held.FirstOrDefault(h => h.Request.Method == "initialize").Request?.Id;
      }
      if (init is null) return;  // server came up before initialize ever arrived: live traffic does the handshake

      var wait = new ReplayWait(new RequestId($"lmfriend-init-{++_replayCounter}"),
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      s.PendingReplay = wait;
      try
      {
        await s.Transport.SendMessageAsync(
          new JsonRpcRequest { Id = wait.ReplayId, Method = "initialize", Params = init.Params?.DeepClone() },
          _shutdown.Token);
        await wait.Done.Task.WaitAsync(TimeSpan.FromSeconds(15), _shutdown.Token);
        if (notif is not null)
          await s.Transport.SendMessageAsync(
            new JsonRpcNotification { Method = notif.Method, Params = notif.Params?.DeepClone() }, _shutdown.Token);
      }
      finally
      {
        s.PendingReplay = null;
      }
    }

    async Task RemoteReaderLoop(Session s)
    {
      var death = "server closed the stream";
      try
      {
        await foreach (var msg in s.Transport.MessageReader.ReadAllAsync(_shutdown.Token))
        {
          if (TryHandleReplay(s, msg)) continue;
          if (msg is JsonRpcMessageWithId { } withId && msg is not JsonRpcRequest)
            s.Outstanding.TryRemove(withId.Id, out _);
          _toStdio.Writer.TryWrite(msg);
        }
      }
      catch (OperationCanceledException) { death = "shutting down"; }
      catch (Exception e) { death = Setup.OneLine(e); }

      s.PendingReplay?.Done.TrySetException(new IOException("connection dropped mid-handshake"));
      KillSession(s, death);
    }

    bool TryHandleReplay(Session s, JsonRpcMessage msg)
    {
      if (s.PendingReplay is not { } wait || msg is not JsonRpcMessageWithId withId || !withId.Id.Equals(wait.ReplayId))
        return false;
      if (msg is JsonRpcError err)
      {
        wait.Done.TrySetException(new InvalidOperationException($"server rejected initialize: {err.Error?.Message}"));
        return true;
      }
      if (_swapInitId is { } clientId && msg is JsonRpcResponse resp)
      {
        JsonRpcRequest? original = null;
        lock (_gate)
        {
          var i = _held.FindIndex(h => h.Request.Id.Equals(clientId));
          if (i >= 0) { original = _held[i].Request; _held.RemoveAt(i); }
        }
        if (original is not null)
          _toStdio.Writer.TryWrite(new JsonRpcResponse { Id = original.Id, Result = resp.Result });
        _swapInitId = null;
      }
      wait.Done.TrySetResult();
      return true;
    }

    async Task<bool> TrySendRemote(JsonRpcMessage msg)
    {
      var s = Volatile.Read(ref _current);
      if (s is null) return false;
      if (msg is JsonRpcRequest req) s.Outstanding[req.Id] = 0;
      try
      {
        await s.SendLock.WaitAsync(_shutdown.Token);
        try { await s.Transport.SendMessageAsync(msg, _shutdown.Token); }
        finally { s.SendLock.Release(); }
        return true;
      }
      catch (OperationCanceledException) { return false; }
      catch (Exception e)
      {
        KillSession(s, Setup.OneLine(e));
        return false;
      }
    }

    void FlushHeld()
    {
      List<JsonRpcRequest> due;
      lock (_gate) { due = _held.ConvertAll(h => h.Request); _held.Clear(); }
      _ = Task.Run(async () =>
      {
        foreach (var req in due)
          if (!await TrySendRemote(req))
            lock (_gate) _held.Add((req, DateTimeOffset.UtcNow.AddSeconds(10)));
      });
    }

    // Ends a session exactly once: anything Claude Desktop is still blocking on gets a
    // synthesized error instead of a timeout, then we go around the reconnect loop.
    void KillSession(Session s, string reason)
    {
      bool wasCurrent;
      lock (_gate) { wasCurrent = ReferenceEquals(_current, s); if (wasCurrent) _current = null; }
      s.Dead.TrySetResult();
      _ = Task.Run(async () => { try { await s.Owner.DisposeAsync(); } catch { } });
      if (!wasCurrent) return;

      Console.Error.WriteLine($"lmfriend: connection to {url} lost ({reason}) - reconnecting...");
      var message = Volatile.Read(ref _reauthActive)
        ? $"credentials for {url} expired; a browser window has been opened to re-authenticate."
        : $"lost the connection to {url}; lmfriend is reconnecting - try again in a moment.";
      foreach (var id in s.Outstanding.Keys)
      {
        s.Outstanding.TryRemove(id, out _);
        _toStdio.Writer.TryWrite(ErrorFor(id, message));
      }
    }

    // The SDK calls this when the server 401s and the refresh token can't save us.
    async Task<AuthorizationResult?> OnOAuthCallback(AuthorizationCallbackContext ctx, CancellationToken ct)
    {
      if (!Volatile.Read(ref _everConnected))
      {
        // Never pop a browser from a fresh proxy spawn: fail fast so Claude Desktop shows
        // the actionable message instead of hanging on initialize.
        Volatile.Write(ref _authDenied, true);
        throw new InvalidOperationException("stored credentials were rejected by the server");
      }

      Task<AuthorizationResult?> flight;
      lock (_gate)
      {
        if (DateTimeOffset.UtcNow < _authCooldownUntil)
          throw new InvalidOperationException("re-auth is cooling down after a failed attempt");
        if (_authFlight is { IsCompleted: false })
          flight = _authFlight;                    // debounce: one browser at a time, however many calls 401
        else
        {
          Volatile.Write(ref _reauthActive, true);
          flight = _authFlight = Reauthorize(ctx);
        }
      }
      return await flight;
    }

    async Task<AuthorizationResult?> Reauthorize(AuthorizationCallbackContext ctx)
    {
      try
      {
        Console.Error.WriteLine($"lmfriend: credentials for {url} were rejected - opening a browser to re-authenticate...");
        return await Setup.BrowserAuthAsync(ctx.AuthorizationUri, ctx.RedirectUri, Console.Error,
          TimeSpan.FromMinutes(2), CancellationToken.None);
      }
      catch
      {
        Console.Error.WriteLine("lmfriend: re-auth didn't finish - will offer the browser again on a later call.");
        lock (_gate) _authCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        throw;
      }
      finally
      {
        Volatile.Write(ref _reauthActive, false);
      }
    }

    static JsonRpcError ErrorFor(RequestId id, string message) => new()
    {
      Id = id,
      Error = new JsonRpcErrorDetail { Code = -32001, Message = message },
    };

    static string Describe(JsonRpcMessage msg) => msg switch
    {
      JsonRpcRequest r => $"request {r.Method}",
      JsonRpcNotification n => $"notification {n.Method}",
      _ => "a client response",
    };
  }
}
