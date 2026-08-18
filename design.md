# Build: `lmfriend` — a stdio↔HTTP MCP bridge (LAN MCP friend) for Claude Desktop

Build a self-contained C# CLI that lets Claude Desktop talk to MCP servers hosted on our LAN.
Claude Desktop's `claude_desktop_config.json` only accepts stdio subprocesses, and its built-in
remote connectors dial out from Anthropic's cloud (so they can't reach our network). This tool is
the local bridge, in the spirit of the npm `mcp-remote` package.

## Stack

- .NET 10, C#, one project, `net10.0`
- `ModelContextProtocol.Core` **2.2.0** (official MCP C# SDK) — use it for the HTTP transport and
  OAuth. Note: `ClientOAuthProvider` and `InMemoryTokenCache` are `internal` in this package, so
  OAuth is configured only by handing a `ClientOAuthOptions` to `HttpClientTransportOptions.OAuth`.
  We never construct the provider ourselves.
- `PublishAot=true`, `SelfContained=true`, `TreatWarningsAsErrors=true`. **Verified 2026-08-18**:
  publishes clean with zero warnings against the full API surface we intend to use. If a later
  change introduces blocking AOT warnings, stop and report back rather than working around them —
  we'd rather reconsider than accumulate hacks.
- 2-space indentation, no tabs.

## Non-goals

Don't build these. If you think one is needed, say so and wait.

- No SSE-only transport fallback. Our servers speak Streamable HTTP.
- No plugin system, no config schema versioning, no telemetry.
- No startup caching / assembly pre-extraction scheme. AOT handles startup.
- No abstraction layers "for testability" that aren't earning their keep.
- No replay of session state beyond the `initialize` handshake (see Durability). Rebuilding
  subscriptions and log levels across a reconnect slides straight back into reimplementing the
  protocol, which is the thing we're explicitly not doing.

## TLS

Accept self-signed certificates unconditionally. This project exists for LAN-hosted MCP servers,
which are self-signed roughly always; a cert prompt here would be friction with no security win.

Mechanically: build one `HttpClient` over an `HttpClientHandler` with
`ServerCertificateCustomValidationCallback` returning true, and pass that same instance to
`HttpClientTransport`. **Open item:** confirm the SDK threads that `HttpClient` down into the
OAuth provider it builds internally — if the token/discovery calls go out on a different client,
they'll reject the cert and we'll need another hook. First thing to test against a real server.

## Shape

Flat layout, four files max, all at the project root:

```
Program.cs      // CLI parsing + dispatch
Setup.cs        // config file editing + interactive OAuth login
Bridge.cs       // proxy mode: the message pump
TokenStore.cs   // ITokenCache implementation, on disk
```

Prefer one long readable method over five small indirect ones. Comments should be sparse — a short
line summarizing the next chunk, with detail only where the reasoning isn't obvious from the code.

## Commands

### `lmfriend setup <name>=<url> [<name>=<url> ...] [--config <path>]`

1. Resolve the config path. Default per-platform:
   - Windows: `%APPDATA%\Claude\claude_desktop_config.json`
   - macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
   - Linux: `~/.config/Claude/claude_desktop_config.json`
2. **Back it up** to `claude_desktop_config.json.bak` before touching it.
3. Read-modify-write with `JsonNode`, preserving every key you didn't set. Other tools write to this
   file; clobbering someone's existing servers is the worst failure mode here. If the file is absent,
   create a minimal one. If it's present but unparseable, abort with a clear error — do not overwrite.
4. For each supplied server, upsert into `mcpServers`:
   ```json
   "<name>": {
     "command": "<absolute path to this executable>",
     "args": ["proxy", "<url>"]
   }
   ```
   Use `Environment.ProcessPath` for the absolute path.
5. Then run the OAuth login for each URL (see below), so proxy mode never has to.
6. Print a reminder that Claude Desktop must be fully restarted to pick up changes.

The user is not expected to ever open this file by hand. That's the whole point of the command.

### `lmfriend login <url>`

Run the interactive OAuth flow for one server and persist the tokens. `setup` calls this internally;
it's also exposed standalone so the user can re-auth without re-running setup.

- Configure `HttpClientTransportOptions.OAuth` with a `ClientOAuthOptions`, supplying our
  `TokenStore` as `TokenCache` and an `AuthorizationCallbackHandler` (note:
  `AuthorizationRedirectDelegate` is deprecated and warns as MCP9007 — don't use it).
- **Dynamic Client Registration only.** Set `DynamicClientRegistration` with a `ClientName` of
  `lmfriend`. There is no manual `--client-id` / `--scope` surface, and we're not adding one until a
  server forces the issue. If DCR fails, say so plainly — that's a server that needs a real answer,
  not a workaround.
- Persist the DCR result via `DynamicClientRegistrationOptions.ResponseDelegate`, alongside the
  tokens. Without this we register a brand new client on every single run, which litters the auth
  server with orphaned registrations and loses us the credential the refresh flow depends on.
- The callback handler launches the system browser (`ProcessStartInfo` with `UseShellExecute = true`)
  and runs an `HttpListener` on a loopback port to catch the redirect. Serve a plain "you can close
  this tab" HTML response so the user gets feedback.
- This is the one place a long wait is correct — the user is sitting at a browser. Allow ~5 minutes
  with a Ctrl-C escape, and print progress to the console.
- On success, open a connection and send one `initialize` to confirm the token actually works, then
  exit 0. Don't report success on a token we've never exercised.

### `lmfriend proxy <url>`

The stdio bridge. Claude Desktop spawns this.

- **Never write to stdout.** Anything but JSON-RPC framing on stdout silently corrupts the stream and
  produces a baffling failure. Route all logging to stderr via an `ILoggerFactory` configured for
  stderr only. Add a comment saying why.
- Pump at the transport layer, not the protocol layer. Get an `ITransport` on each side and shuttle
  `JsonRpcMessage` values between them:
  - stdio side: `StdioServerTransport`
  - remote side: `HttpClientTransport(...).ConnectAsync()`
  - Two tasks reading each `MessageReader` and calling `SendMessageAsync` on the other.
  Do **not** build an `McpClient`/`McpServer` pair and forward tools/resources/prompts individually —
  that's lossy and needs a code change for every new protocol feature. The point is a transparent pipe.
- **Startup auth fails fast.** Load tokens from `TokenStore`. If absent, or if the refresh fails,
  exit non-zero within a couple of seconds with a stderr message like
  `no valid credentials for <url> — run: lmfriend login <url>`. Do not block waiting for a browser
  flow here: Claude Desktop times out `initialize` and kills the process, so a hang just becomes an
  opaque failure. A fast, loud failure is the better outcome, and Claude Desktop puts the error
  right in front of the user with the fix one command away.
  (This is the one case that still exits non-zero. Everything after startup is covered by
  Durability, below.)

## Durability

Once the pump is running, the proxy does not die. Not on a dropped connection, not on an expired
token, not on the LAN box rebooting halfway through a `tools/call`. It waits, it reconnects, it
re-auths, and it keeps the stdio side alive throughout. Claude Desktop only ever spawns this thing
once per launch, so every process death is a dead server entry in the user's UI until they restart
the app — the bar for dying is correspondingly high.

**Reconnecting is not transparent, and this is the part that bites.** Streamable HTTP sessions are
stateful: the server hands out an `Mcp-Session-Id`, and a reconnect gets a fresh session that has
never seen `initialize`. Meanwhile Claude Desktop, on the far side of the pipe, still believes it's
initialized and will cheerfully fire `tools/call` at a server that considers it a stranger. So:

- **Cache the client's `initialize` request and `notifications/initialized`** on the way through, and
  replay them on every reconnect before letting anything else across. Use string ids in our own
  namespace (`lmfriend-init-N`) so we can't collide with a client id, and **swallow the replayed
  response** rather than forwarding a second `initialize` result to a client that only asked once.
- **Track outstanding request ids.** Anything forwarded but not yet answered is a request Claude
  Desktop is still blocking on. On a drop, synthesize a JSON-RPC error for each orphan instead of
  leaving it to time out.
- Reconnect with capped exponential backoff and jitter (~1s up to ~30s), forever. Client requests
  arriving while we're disconnected get held briefly (~10s, matching the startup grace) and then
  answered with an error rather than queued indefinitely.
- Log every reconnect to stderr. Silent to the client, visible to us.

The one clean exit: **EOF on stdin, or SIGTERM.** That's Claude Desktop deliberately closing us
down, not a failure to survive. Tear both transports down and exit 0. Durability means outliving
the network and the auth server, not outliving our own parent.

**Token expiry mid-session never kills the process either.** On a 401 or a failed refresh:

- Answer the pending request with a JSON-RPC error saying credentials expired and a browser window
  has been opened, so the user gets a reason instead of a hang.
- Kick off the browser auth flow in the background and keep running.
- **Debounce it.** One auth attempt in flight at a time, with a cooldown after a timeout. Otherwise a
  user who ignores the tab collects a fresh one on every tool call, and four configured servers means
  four tabs racing each other for focus.
- If the auth window times out, drop it and carry on. Try again on the next call that needs auth, and
  keep trying, indefinitely. An un-authed proxy sitting there patiently is strictly better than a
  dead one.

## TokenStore

Implement `ITokenCache` (the SDK default caches in memory, which would force re-auth on every
Claude Desktop restart).

- One file per server, in `%LOCALAPPDATA%\lmfriend\` / `~/.config/lmfriend/`, named by a hex
  SHA-256 of the normalized server URL. Use an absolute path — the cwd Claude Desktop gives a spawned
  process is not what you'd guess.
- **Normalization is load-bearing**: lowercase the scheme and host, drop a default port (443/80),
  strip any trailing slash, drop the fragment. `login` and `proxy` must derive byte-identical hashes
  or you get "no valid credentials" immediately after a successful login, which is a genuinely
  maddening afternoon. Define it once, in one helper, used by both.
- Stores the DCR client credentials next to the tokens (see `login`).
- **Atomic writes**: temp file + rename, and tolerate reading a stale token. Two proxy processes can
  point at the same URL under different config names, and with refresh-token rotation one process's
  refresh invalidates the other's. We accept the occasional redundant re-auth; we don't accept a
  half-written token file.
- Unix: `chmod 600` the token files — these are live refresh tokens. Windows: rely on the default
  per-user ACL on `%LOCALAPPDATA%` and say so in a comment. Hand-rolling ACLs pulls in a Windows-only
  assembly to re-derive a permission the OS already gave us.
- Corrupt or unreadable file is treated as "no tokens", not a crash.

## Acceptance

Before declaring done, verify:

1. `dotnet publish` with AOT produces a single binary with no warnings. ✅ done 2026-08-18
2. `setup` against a config file containing a pre-existing unrelated server leaves that server and
   any unknown top-level keys **semantically** intact — every untouched key deep-compares equal
   before and after. (Not byte-intact: a `JsonNode` round trip reformats whitespace and escaping
   across the whole document, and splicing text to avoid that would be a cure worse than the disease.)
3. `login` completes against a real OAuth-protected server via DCR and writes a token file.
4. `proxy` with no cached token exits non-zero in under 3s with the actionable message.
5. `proxy` with a valid token successfully proxies `initialize` and `tools/list` from a real client.
6. Nothing appears on stdout during `proxy` except JSON-RPC.
7. Kill the remote server mid-session and bring it back: the proxy survives, replays the handshake,
   and the next `tools/list` succeeds without Claude Desktop being restarted.
8. Expire/revoke a token mid-session: the proxy survives, the pending call returns a readable error,
   and a browser opens exactly once — not once per subsequent call.

## Round-trip fidelity — settled

The transparent-pipe design rests on `JsonRpcMessage` not dropping data it doesn't recognize. Checked
once, as a throwaway probe, rather than as a gate on every proxy spawn — the property can't change
between runs. **Result (SDK 2.2.0, AOT binary): lossless.** Unknown sibling keys inside `params`,
`_meta` blocks, vendor extensions in `capabilities`, an unregistered method name, a 2^53+1 integer,
and negative zero all survived serialize→deserialize→serialize unchanged. `Params` is held as an
opaque node, which is exactly the property we needed.

## Working agreement

Push back if a requirement here looks wrong or is more complex than the problem warrants — I'd rather
hear it than get a faithful implementation of a bad idea. Ask before adding a dependency.
