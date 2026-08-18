// THROWAWAY PROBE - deleted before real work starts.
// Two questions this answers: (1) does the AOT publish come out clean, and
// (2) does JsonRpcMessage survive a serialize->deserialize round trip without
// dropping unknown/unusual fields? If (2) fails, the transparent-pipe design is dead.
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

var opts = McpJsonUtilities.DefaultOptions;
int failures = 0;
if (args.Length > 99) await TouchAotSurface(args); // never true; keeps the AOT probe reachable

// Each case is a raw JSON-RPC frame as it would arrive on the wire.
var cases = new (string Name, string Json)[]
{
  ("request with exotic params", """
  {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
    "name":"weird","arguments":{"nested":{"deep":{"deeper":[1,2,{"x":null}]}},
    "bigint":9007199254740993,"float":1.7976931348623157e308,"negzero":-0.0,
    "unicode":"nihongo 日本語 and a \" quote","empty_obj":{},"empty_arr":[],
    "unknown_future_field":true},"_meta":{"progressToken":"abc","vendorExt":{"a":1}},
    "totallyUnknownSibling":["preserve","me"]}}
  """),
  ("request with string id", """
  {"jsonrpc":"2.0","id":"lmfriend-init-1","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{"experimental":{"vendor.feature":{"on":true}}},"clientInfo":{"name":"x","version":"1"}}}
  """),
  ("notification, no params", """
  {"jsonrpc":"2.0","method":"notifications/initialized"}
  """),
  ("response with exotic result", """
  {"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"hi"},{"type":"future_block","payload":{"k":[1,null,false]}}],"isError":false,"_meta":{"z":1}}}
  """),
  ("error with data payload", """
  {"jsonrpc":"2.0","id":8,"error":{"code":-32001,"message":"nope","data":{"detail":{"trace":["a","b"]}}}}
  """),
  ("unknown method entirely", """
  {"jsonrpc":"2.0","id":9,"method":"vendor/notYetInvented","params":{"anything":"goes"}}
  """),
};

foreach (var (name, json) in cases)
{
  try
  {
    var msg = JsonSerializer.Deserialize(json, McpJsonUtilities.GetTypeInfo<JsonRpcMessage>(opts));
    if (msg is null) { Console.WriteLine($"FAIL {name}: deserialized to null"); failures++; continue; }
    var back = JsonSerializer.Serialize(msg, McpJsonUtilities.GetTypeInfo<JsonRpcMessage>(opts));

    var before = JsonNode.Parse(json);
    var after = JsonNode.Parse(back);
    var diffs = new List<string>();
    Compare("$", before, after, diffs);

    if (diffs.Count == 0) Console.WriteLine($"PASS {name}  [{msg.GetType().Name}]");
    else
    {
      failures++;
      Console.WriteLine($"FAIL {name}  [{msg.GetType().Name}]");
      foreach (var d in diffs) Console.WriteLine($"       {d}");
      Console.WriteLine($"       after: {back}");
    }
  }
  catch (Exception ex)
  {
    failures++;
    Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
  }
}

Console.WriteLine(failures == 0
  ? "\nALL PASS - JsonRpcMessage is lossless; transparent pipe is viable."
  : $"\n{failures} FAILURE(S) - the pipe would drop data. STOP AND REPORT.");
return failures == 0 ? 0 : 1;

// Structural compare: every value present in `a` must survive into `b`.
static void Compare(string path, JsonNode? a, JsonNode? b, List<string> diffs)
{
  if (a is null && b is null) return;
  if (a is null || b is null) { diffs.Add($"{path}: {(a is null ? "gained" : "LOST")} (a={a?.ToJsonString()} b={b?.ToJsonString()})"); return; }

  if (a is JsonObject ao)
  {
    if (b is not JsonObject bo) { diffs.Add($"{path}: object became {b.GetType().Name}"); return; }
    foreach (var kv in ao)
    {
      if (!bo.ContainsKey(kv.Key)) { diffs.Add($"{path}.{kv.Key}: DROPPED"); continue; }
      Compare($"{path}.{kv.Key}", kv.Value, bo[kv.Key], diffs);
    }
  }
  else if (a is JsonArray aa)
  {
    if (b is not JsonArray ba) { diffs.Add($"{path}: array became {b.GetType().Name}"); return; }
    if (aa.Count != ba.Count) { diffs.Add($"{path}: length {aa.Count} -> {ba.Count}"); return; }
    for (int i = 0; i < aa.Count; i++) Compare($"{path}[{i}]", aa[i], ba[i], diffs);
  }
  else
  {
    var sa = a.ToJsonString();
    var sb = b.ToJsonString();
    if (sa != sb) diffs.Add($"{path}: {sa} -> {sb}");
  }
}

// Root every API the real tool will use, so the ILC analyzer actually walks those
// paths. Never executed - reachability is the point, not the call.
static async Task TouchAotSurface(string[] args)
{
  var lf = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
  var uri = new Uri(args[0]);

  var handler = new System.Net.Http.HttpClientHandler();
  handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
  var http = new HttpClient(handler);

  var oauth = new ModelContextProtocol.Authentication.ClientOAuthOptions
  {
    RedirectUri = new Uri("http://localhost:0/callback"),
    TokenCache = new ProbeTokenCache(),
    DynamicClientRegistration = new ModelContextProtocol.Authentication.DynamicClientRegistrationOptions
    {
      ClientName = "lmfriend",
    },
    AuthorizationCallbackHandler = (ctx, ct) => throw new NotImplementedException(),
  };

  var remote = new ModelContextProtocol.Client.HttpClientTransport(
    new ModelContextProtocol.Client.HttpClientTransportOptions { Endpoint = uri, OAuth = oauth },
    http, lf, true);
  await using var session = await remote.ConnectAsync();
  _ = session.SessionId;
  await session.SendMessageAsync(new JsonRpcNotification { Method = "x" });
  await foreach (var m in session.MessageReader.ReadAllAsync()) _ = m;

  await using var stdio = new ModelContextProtocol.Server.StdioServerTransport("lmfriend", lf);

  var listener = new System.Net.HttpListener();
  listener.Prefixes.Add("http://127.0.0.1:1/");
  listener.Start();
  var ctxh = await listener.GetContextAsync();
  _ = ctxh.Request.Url;
  ctxh.Response.Close();

  System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("x") { UseShellExecute = true });
  _ = System.Environment.ProcessPath;
  _ = System.Security.Cryptography.SHA256.HashData(new byte[1]);
  if (!OperatingSystem.IsWindows()) File.SetUnixFileMode("x", UnixFileMode.UserRead | UnixFileMode.UserWrite);
  _ = JsonNode.Parse("{}");
}

// The real TokenStore will look like this; rooted so ILC sees the interface impl.
sealed class ProbeTokenCache : ModelContextProtocol.Authentication.ITokenCache
{
  public ValueTask<ModelContextProtocol.Authentication.TokenContainer?> GetTokensAsync(CancellationToken ct = default) => default;
  public ValueTask StoreTokensAsync(ModelContextProtocol.Authentication.TokenContainer tokens, CancellationToken ct = default) => default;
}
