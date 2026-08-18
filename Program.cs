// lmfriend: a stdio<->HTTP bridge so Claude Desktop can reach MCP servers on our LAN.
// Commands: setup (edit claude_desktop_config.json + log in), login (OAuth only), proxy (the bridge).
using Microsoft.Extensions.Logging;

if (args.Length == 0)
{
  PrintUsage();
  return 1;
}

try
{
  switch (args[0])
  {
    case "setup":
      return await Setup.Run(args[1..]);
    case "login":
      if (args.Length == 2 && IsHttpUrl(args[1], out var loginUrl)) return await Setup.Login(loginUrl!);
      return BadArgs("login <http-url>");
    case "proxy":
      if (args.Length == 2 && IsHttpUrl(args[1], out var proxyUrl)) return await Bridge.Run(proxyUrl!);
      return BadArgs("proxy <http-url>");
    default:
      return BadArgs(args[0]);
  }
}
catch (Exception e)
{
  Console.Error.WriteLine($"lmfriend: {e.Message}");
  return 1;
}

static bool IsHttpUrl(string s, out Uri? uri)
  => Uri.TryCreate(s, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

static int BadArgs(string what)
{
  Console.Error.WriteLine($"lmfriend: bad arguments: {what}");
  PrintUsage();
  return 1;
}

static void PrintUsage() => Console.Error.WriteLine("""
  lmfriend - bridges Claude Desktop to MCP servers on the LAN.
    lmfriend setup <name>=<url> [...] [--config <path>]   register servers in claude_desktop_config.json and log in
    lmfriend login <url>                                  (re-)authenticate with one server
    lmfriend proxy <url>                                  stdio<->HTTP bridge (Claude Desktop runs this)
  """);

// Helpers shared by the interactive commands and the proxy.
static class Shared
{
  // LAN-hosted MCP servers are self-signed roughly always; a cert prompt would be pure friction.
  public static HttpClient NewHttpClient()
  {
    var handler = new HttpClientHandler
    {
      ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    };
    return new HttpClient(handler);
  }

  public static readonly ILoggerFactory Logs = new StderrLoggerFactory();

  // The MCP SDK only accepts an ILoggerFactory. This one writes to stderr exclusively: stdout
  // belongs to JSON-RPC framing, and one stray line there silently corrupts the stream.
  sealed class StderrLoggerFactory : ILoggerFactory
  {
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
  }

  sealed class StderrLogger(string category) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter)
    {
      if (!IsEnabled(logLevel)) return;
      var line = $"lmfriend/{category}: {formatter(state, exception)}";
      Console.Error.WriteLine(exception is null ? line : $"{line} ({exception.GetType().Name}: {exception.Message})");
    }
  }
}
