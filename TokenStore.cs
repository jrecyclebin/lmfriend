using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;

// One token file per server URL, shared by login (writer) and proxy (reader). The file name
// is a hash of the normalized URL - both commands must derive byte-identical hashes or proxy
// mode "loses" the tokens login just saved, so normalization is defined here, exactly once.
sealed class TokenStore : ITokenCache
{
  readonly string _path;

  public TokenStore(Uri server)
  {
    _path = Path.Combine(StoreDir(),
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(server)))).ToLowerInvariant() + ".json");
  }

  // Scheme and host lowercased, default port dropped, trailing slash stripped, fragment
  // dropped. Anything else stays part of the server's identity.
  public static string Normalize(Uri uri)
  {
    var s = new StringBuilder()
      .Append(uri.Scheme.ToLowerInvariant()).Append("://").Append(uri.IdnHost.ToLowerInvariant());
    if (!uri.IsDefaultPort) s.Append(':').Append(uri.Port);
    s.Append(uri.AbsolutePath.TrimEnd('/'));
    s.Append(uri.Query);
    return s.ToString();
  }

  public static string StoreDir()
  {
    if (OperatingSystem.IsWindows())
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lmfriend");
    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var baseDir = string.IsNullOrEmpty(xdg)
      ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
      : xdg;
    return Path.Combine(baseDir, "lmfriend");
  }

  public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default)
  {
    try
    {
      return ValueTask.FromResult(
        JsonSerializer.Deserialize(File.ReadAllText(_path), TokenJsonContext.Default.TokenContainer));
    }
    catch
    {
      // Missing, corrupt or unreadable all mean the same thing: there are no tokens.
      return ValueTask.FromResult<TokenContainer?>(null);
    }
  }

  public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
  {
    Directory.CreateDirectory(StoreDir());
    // Temp file + rename: two proxies pointed at the same URL may write concurrently (refresh
    // rotation), and a half-written token file is worse than a stale one.
    var tmp = $"{_path}.{Environment.ProcessId}.tmp";
    var json = JsonSerializer.Serialize(tokens, TokenJsonContext.Default.TokenContainer);
    await File.WriteAllTextAsync(tmp, json, cancellationToken);
    File.Move(tmp, _path, overwrite: true);
    // These are live refresh tokens. Windows gets the per-user ACL on LOCALAPPDATA for free;
    // elsewhere we tighten it ourselves.
    if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
  }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TokenContainer))]
partial class TokenJsonContext : JsonSerializerContext;
