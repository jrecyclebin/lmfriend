using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Config-file editing plus the interactive OAuth login. `setup` writes lmfriend into
// claude_desktop_config.json for each server and then logs in; `login` also runs standalone
// so the user can re-auth without touching the config.
static class Setup
{
  public static async Task<int> Run(string[] args)
  {
    var servers = new List<(string Name, Uri Url)>();
    string? configPath = null;
    for (var i = 0; i < args.Length; i++)
    {
      if (args[i] == "--config" && ++i < args.Length)
      {
        configPath = args[i];
        continue;
      }
      var eq = args[i].IndexOf('=');
      if (eq <= 0 || !Uri.TryCreate(args[i][(eq + 1)..], UriKind.Absolute, out var serverUrl)
          || serverUrl.Scheme is not ("http" or "https"))
        return Fail($"expected <name>=<http-url>, got '{args[i]}'");
      servers.Add((args[i][..eq], serverUrl));
    }
    configPath ??= DefaultConfigPath();
    if (servers.Count == 0) return Fail("no <name>=<url> pairs given");

    // Other tools write to this file too. Back it up, touch only our own keys, and refuse
    // outright to overwrite a file we can't parse.
    Console.WriteLine($"editing {configPath}");
    JsonNode root;
    if (File.Exists(configPath))
    {
      File.Copy(configPath, configPath + ".bak", overwrite: true);
      try
      {
        root = JsonNode.Parse(File.ReadAllText(configPath))!;
      }
      catch (JsonException e)
      {
        return Fail($"can't parse {configPath} ({e.Message}) - leaving it alone");
      }
    }
    else root = new JsonObject();

    if (root["mcpServers"] is not JsonObject mcpServers)
    {
      if (root["mcpServers"] is not null)
        return Fail("mcpServers in the config isn't an object - leaving the file alone");
      root["mcpServers"] = mcpServers = new JsonObject();
    }

    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("can't determine my own executable path");
    foreach (var (name, serverUrl) in servers)
    {
      mcpServers[name] = new JsonObject
      {
        ["command"] = exe,
        ["args"] = new JsonArray("proxy", serverUrl.ToString()),
      };
      Console.WriteLine($"registered '{name}' -> {serverUrl}");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    var failures = 0;
    foreach (var (name, serverUrl) in servers)
    {
      Console.WriteLine($"logging in to '{name}'...");
      if (await Login(serverUrl) != 0) failures++;
    }

    Console.WriteLine("done - fully restart Claude Desktop to pick up the changes.");
    return failures == 0 ? 0 : 1;
  }

  public static async Task<int> Login(Uri url)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; timeout.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
      using var http = Shared.NewHttpClient();
      var oauth = new ClientOAuthOptions
      {
        RedirectUri = new Uri($"http://127.0.0.1:{PickPort()}/callback"),
        TokenCache = new TokenStore(url),
        // Dynamic Client Registration only - a server without DCR needs a real answer, not a workaround.
        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "lmfriend" },
        AuthorizationCallbackHandler = async (ctx, ct) =>
          await BrowserAuthAsync(ctx.AuthorizationUri, ctx.RedirectUri, Console.Out, TimeSpan.FromMinutes(5), timeout.Token),
      };
      var transport = new HttpClientTransport(
        new HttpClientTransportOptions { Endpoint = url, TransportMode = HttpTransportMode.StreamableHttp, OAuth = oauth },
        http, Shared.Logs, ownsHttpClient: false);

      Console.WriteLine($"logging in to {url} - a browser window will open shortly.");
      // CreateAsync runs the real initialize handshake: success means we've exercised the token.
      // Two budget traps in the SDK default: InitializationTimeout (60s) starts BEFORE the
      // first request, so it fires while the user is still reading the browser page; and the
      // server/discover probe (5s) can die mid-OAuth and send a second, un-authed initialize.
      // Pin an initialize-capable protocol version so there's exactly one request doing the
      // OAuth dance, and give it the same human-paced budget as the rest of the flow.
      var options = new McpClientOptions
      {
        ClientInfo = new Implementation { Name = "lmfriend", Version = "1.0.0" },
        ProtocolVersion = "2025-06-18",
        InitializationTimeout = TimeSpan.FromMinutes(5),
      };
      await using var client = await McpClient.CreateAsync(transport, options, Shared.Logs, timeout.Token);
      Console.WriteLine($"authenticated with {client.ServerInfo.Name} {client.ServerInfo.Version} - tokens saved.");
      return 0;
    }
    catch (OperationCanceledException)
    {
      Console.WriteLine("login aborted or timed out.");
      return 1;
    }
    catch (Exception e)
    {
      Console.WriteLine($"authentication failed: {OneLine(e)}");
      Console.WriteLine("If that server doesn't support OAuth dynamic client registration, lmfriend can't log in to it (yet).");
      return 1;
    }
    finally
    {
      Console.CancelKeyPress -= onCancel;
    }
  }

  // Dynamic client registration bakes our redirect URI into the registered client, so prefer
  // the same small port range every time: a stable port keeps re-auth matching the original
  // registration instead of failing with a redirect_uri mismatch.
  public static int PickPort()
  {
    for (var port = 48231; port <= 48239; port++)
      if (PortFree(port)) return port;
    var scratch = new TcpListener(IPAddress.Loopback, 0);
    scratch.Start();
    var picked = ((IPEndPoint)scratch.LocalEndpoint).Port;
    scratch.Stop();
    return picked;
  }

  static bool PortFree(int port)
  {
    try
    {
      var l = new TcpListener(IPAddress.Loopback, port);
      l.Start();
      l.Stop();
      return true;
    }
    catch (SocketException)
    {
      return false;
    }
  }

  // Waits for the OAuth redirect on a loopback socket, then hands the code back to the SDK.
  // A raw TcpListener rather than HttpListener: HttpListener needs admin rights / URL ACLs on
  // Windows, and a browser needs nothing here beyond one minimal response.
  public static async Task<AuthorizationResult> BrowserAuthAsync(
    Uri authorizationUri, Uri redirectUri, TextWriter log, TimeSpan timeout, CancellationToken ct)
  {
    using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timer.CancelAfter(timeout);
    var linked = timer.Token;

    var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
    listener.Start();
    try
    {
      log.WriteLine("opening the browser to log in - approve it there, or press Ctrl-C to bail.");
      try { Process.Start(new ProcessStartInfo(authorizationUri.ToString()) { UseShellExecute = true }); }
      catch { log.WriteLine($"couldn't open a browser automatically - open this URL yourself:\n  {authorizationUri}"); }

      // Tolerate a few stray hits (favicon, probes) until one arrives carrying the OAuth response.
      for (var attempt = 0; attempt < 4; attempt++)
      {
        using var browser = await listener.AcceptTcpClientAsync(linked);
        var query = await ReadRequestQuery(browser, linked);
        var code = query.GetValueOrDefault("code");
        var error = query.GetValueOrDefault("error");
        if (code is null && error is null)
        {
          await Reply(browser, "404 Not Found", "nothing here", linked);
          continue;
        }
        if (error is not null)
        {
          var why = query.GetValueOrDefault("error_description") ?? error;
          await Reply(browser, "200 OK", Page("Login failed", why), linked);
          throw new InvalidOperationException($"authorization server said: {why}");
        }
        log.WriteLine("login approved - finishing up...");
        await Reply(browser, "200 OK", Page("You're logged in", "You can close this tab and head back."), linked);
        return new AuthorizationResult
        {
          Code = code,
          State = query.GetValueOrDefault("state"),
          Iss = query.GetValueOrDefault("iss"),
        };
      }
      throw new InvalidOperationException("browser kept connecting without delivering an OAuth response");
    }
    finally
    {
      listener.Stop();
    }
  }

  static async Task<Dictionary<string, string>> ReadRequestQuery(TcpClient browser, CancellationToken ct)
  {
    var stream = browser.GetStream();
    var buffer = new byte[8192];
    var text = new StringBuilder();
    while (text.Length < 32768)
    {
      if (text.ToString().Contains("\r\n\r\n")) break;
      var read = await stream.ReadAsync(buffer, ct);
      if (read == 0) break;
      text.Append(Encoding.ASCII.GetString(buffer, 0, read));
    }
    var requestLine = text.ToString().Split("\r\n", 2)[0];  // "GET /callback?code=...&state=... HTTP/1.1"
    var parts = requestLine.Split(' ');

    var query = new Dictionary<string, string>();
    if (parts.Length < 2) return query;
    var q = parts[1].IndexOf('?');
    if (q < 0) return query;
    foreach (var pair in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      var kv = pair.Split('=', 2);
      query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
    }
    return query;
  }

  static async Task Reply(TcpClient browser, string status, string html, CancellationToken ct)
  {
    var body = Encoding.UTF8.GetBytes(html);
    var head = Encoding.ASCII.GetBytes(
      $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
    var stream = browser.GetStream();
    await stream.WriteAsync(head, ct);
    await stream.WriteAsync(body, ct);
  }

  static string Page(string title, string message) => $"""
    <!doctype html><title>{title} - lmfriend</title>
    <body style="font-family:system-ui;margin:4rem auto;max-width:32rem;text-align:center">
    <h1>{title}</h1><p>{message}</p></body>
    """;

  static string DefaultConfigPath()
  {
    const string FileName = "claude_desktop_config.json";

    if (OperatingSystem.IsWindows())
    {
      var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", FileName);

      // The Store build is packaged, so its %APPDATA% writes are redirected into a
      // per-package container that outside processes don't see. Look there first.
      var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages");
      if (Directory.Exists(packages))
      {
        foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*"))
        {
          var packaged = Path.Combine(dir, "LocalCache", "Roaming", "Claude", FileName);
          if (File.Exists(packaged))
            return packaged;
        }
      }

      return roaming;
    }

    if (OperatingSystem.IsMacOS())
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "Claude", FileName);

    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var baseDir = string.IsNullOrEmpty(xdg)
      ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
      : xdg;
    return Path.Combine(baseDir, "Claude", FileName);
  }

  static int Fail(string message)
  {
    Console.Error.WriteLine($"lmfriend: {message}");
    return 1;
  }

  internal static string OneLine(Exception e) => e.Message.Split('\n')[0].Trim();
}
