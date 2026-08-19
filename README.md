# lmfriend

*Your LAN's MCP friend.* 🌐

Claude Desktop can only spawn local subprocesses for MCP, and its cloud-side remote
connectors can't see your home network. **lmfriend** is the little bridge that fixes
that: it speaks stdio on one side (where Claude Desktop lives) and Streamable HTTP + OAuth
on the other (where your LAN servers live), and it refuses, politely and at length, to die.

```
Claude Desktop ──stdio──> lmfriend proxy ──HTTPS + OAuth──> MCP server on your LAN
```

One self-contained binary, no .NET runtime required, ships as a single file.

✨ ─────────────────────────────────────────────────────────────── ✨

## Quick start

Build it:

```sh
mise run publish        # AOT binary -> bin/Release/net10.0/linux-x64/publish/lmfriend
cp bin/Release/net10.0/linux-x64/publish/lmfriend ~/.local/bin/
```

Then point Claude Desktop at a server and log in, in one motion:

```sh
lmfriend setup lab=https://mcpbox.lan:9009/mcp
```

That's the whole ceremony. `setup` edits `claude_desktop_config.json` for you (after
backing it up to `.bak`), wires in a `proxy` entry, runs the OAuth login in your
browser, and reminds you to fully restart Claude Desktop. After that, the server
just appears in your tool list like it always belonged there.

> 💡 **RESTART MEANS RESTART**
>
> Claude Desktop reads its config only at launch. Quit it fully (check the system
> tray!) and reopen, or your shiny new server stays invisible and everyone is sad.

## The three commands

| Command | What it does |
|---|---|
| `lmfriend setup <name>=<url> [...] [--config <path>]` | Registers servers in Claude Desktop's config **and** logs you in to each |
| `lmfriend login <url>` | Re-runs just the OAuth browser flow for one server |
| `lmfriend proxy <url>` | The actual bridge. *You never run this* - Claude Desktop spawns it |

Multiple servers? One line:

```sh
lmfriend setup lab=https://mcpbox.lan:9009/mcp media=https://plexbox.lan:8443/mcp
```

## Why it doesn't fall over

A proxy process is spawned **once per Claude Desktop launch**, so dying is not an
option - a dead proxy is a dead server entry until you restart the app. So lmfriend
treats the network as a hostile, intermittent medium:

- **Server rebooted mid-`tools/call`?** Reconnects forever with capped backoff and
  jitter, replays the `initialize` handshake onto the fresh session, and carries on.
  Claude Desktop never notices.
- **Request in flight when the connection dropped?** It gets a synthesized JSON-RPC
  error instead of hanging until the heat death of the universe.
- **Token expired mid-session?** A browser window opens once (never a tab-storm),
  you re-approve, things resume. Ignore the tab and the proxy waits patiently rather
  than dying.
- **No valid credentials at startup?** Fails fast (about 13 milliseconds fast) with
  the exact fix: `lmfriend login <url>`.
- **Claude Desktop closes the pipe** (EOF on stdin)? *That's* the one clean exit.
  Everything else is a flesh wound.

🌿○◦●◦○◦●◦○◦●◦○◦●◦○◦●◦○◦●◦○◦●◦○◦●◦○◦●◦○◦●🌿

## Good to know

- **Self-signed certificates are fine.** LAN servers are self-signed roughly always;
  refusing them would be friction with no security win. lmfriend accepts them everywhere.
- **OAuth is Dynamic Client Registration only.** If a server can't do DCR, lmfriend
  says so plainly instead of growing a pile of `--client-id` flags.
- **Tokens live on disk** in `~/.config/lmfriend/` (`%LOCALAPPDATA%\lmfriend\` on
  Windows), one file per server, `chmod 600`, written atomically. Each file carries
  the whole token bundle *including* your DCR client registration, so you register
  once ever and don't litter the auth server with a new client per run.
- **The proxy never writes anything but JSON-RPC to stdout.** All logging goes to
  stderr, where Claude Desktop surfaces it when something needs your attention.

## Developing

```sh
mise run build      # warnings are errors, 2-space indents, absolutely no mercy
mise run publish    # the AOT binary, zero-warning bar
mise run test       # mock-server harness: drives the binary end to end
mise run clean
```

The test harness (`tests/`) spins up a fake Streamable-HTTP server in Python and
puts the real published binary through the whole gauntlet: round-trips, stdout
purity, kill-the-server-mid-session, handshake replay after "reboot", orphan
request errors, clean EOF exit. Ten checks, all green before anything ships.

*Carefully crafted at the Poggers Institute.* (ﾉ◕ヮ◕)ﾉ*:･ﾟ✧
