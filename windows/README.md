# MMail pour Windows

Native Windows version of MMail (WinUI 3, .NET 10), with the same features as the
macOS app on the `facteur` branch: IMAP/SMTP mail (Gmail with an app password),
a local SQLite cache with full-text search, the Gmail-style list in the Arcos look
(warm paper `#F5F4ED`, clay `#D97757`) with **Pinned / Unread / Everything else**
sections in All, and a local MCP server so Claude can drive the app with the same
22 tools as the Mac version.

## Install

Download `MMail-Setup.exe` from the
[`facteur-windows-latest` prerelease](../../releases/tag/facteur-windows-latest)
and run it. It installs MMail for your user only (no administrator prompt) in
`%LOCALAPPDATA%\Programs\MMail`, with a Start menu entry, an optional desktop
icon and an uninstaller; running a newer setup updates in place. The installer is
not signed, so SmartScreen may say "Windows protected your PC": click *More info*,
then *Run anyway*. A portable `MMail-Windows-x64.zip` is published next to it.
Windows 10 1809 or later; the reader uses the WebView2 runtime that ships with
Windows 11 and Edge.

The first launch asks for an account: **Sign in with Google** opens Google's page in
the browser, and MMail then reads and sends Gmail over IMAP/SMTP with OAuth (XOAUTH2).
Other options, behind "Autre messagerie": a Gmail
[app password](https://myaccount.google.com/apppasswords), or any IMAP/SMTP server.
Google refresh tokens and passwords are kept in the Windows Credential Manager
(`MMail:<account>.imap`), mail and settings in `%LOCALAPPDATA%\MMail`. If MMail fails
at launch, `%LOCALAPPDATA%\MMail\startup.log` says where.

Google sign-in needs an OAuth client of type *Desktop app* in Google Cloud (Google Auth
Platform, audience External, published so tokens don't expire after 7 days). Its ID
and secret go in the repository secrets `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`;
the workflow builds them in. Locally, set `MMAIL_GOOGLE_CLIENT_ID` /
`MMAIL_GOOGLE_CLIENT_SECRET`, or pass `-p:GoogleClientId=… -p:GoogleClientSecret=…`.

## Claude

Settings → **Connect Claude Desktop** adds MMail to
`%APPDATA%\Claude\claude_desktop_config.json`:

```json
{ "mcpServers": { "mmail": { "command": "C:\\…\\MMail\\mmail-mcp.exe" } } }
```

For Claude Code: `claude mcp add mmail --scope user -- "C:\…\MMail\mmail-mcp.exe"`.

`mmail-mcp.exe` does no mail work itself: it relays Claude's stdio to the running
app over a named pipe only your Windows user can open (`\\.\pipe\mmail-agent-<user>`),
starting MMail minimized if needed. Every tool acts on the window's own state, so
what Claude does shows up on screen, with the same toasts and Undo.

Tools: `list_accounts`, `list_folders`, `list_messages`, `search_messages`,
`get_message`, `get_attachment`, `save_attachment`, `list_contacts`, `list_labels`,
`show_message`, `set_flags`, `triage`, `move_message`, `snooze_message`,
`label_message`, `compose_message`, `get_compose`, `update_compose`,
`add_attachment`, `remove_attachment`, `save_draft`, `send_message`.

## Keyboard

| Key | Action | Key | Action |
|---|---|---|---|
| `C` | Compose | `R` / `A` / `F` | Reply / Reply all / Forward |
| `/` | Search | `E` | Archive |
| `J` `K` / arrows | Next / previous | `#` or `Del` | Delete |
| `Enter` | Open | `U` | Mark read, or pin / unpin when read |
| `Esc` | Back | `S` / `Z` | Star / Snooze until tomorrow |
| `Ctrl+Enter` | Send | | |

## Layout

```
windows/
  src/MMail.Core/     mail (MailKit), SQLite cache, list logic, MCP server and tools — no UI, runs anywhere
  src/MMail.App/      the WinUI 3 app (MMail.exe)
  src/MMail.Mcp/      mmail-mcp.exe, the stdio ↔ pipe bridge Claude launches
  installer/          Inno Setup script for MMail-Setup.exe
  tests/              core tests (fake mail server), run on Linux and Windows
```

`MailController` is the counterpart of `AppModel` on the Mac: one object holding
the state and every action, used by both the window and the MCP tools.

## Build

```
dotnet test tests/MMail.Core.Tests
dotnet publish src/MMail.App/MMail.App.csproj -c Release -r win-x64 -p:Platform=x64 -o out/MMail
dotnet publish src/MMail.Mcp/MMail.Mcp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/MMail
iscc installer/MMail.iss    # Inno Setup 6: out/MMail-Setup.exe
```

The core and its tests build on any OS; the app needs Windows (or the **Windows**
GitHub Actions workflow, which runs on every push touching `windows/`). In Visual
Studio, open `MMail.Windows.sln` and pick the x64 platform.

## Not ported yet

Home dashboard (weather, journal, to-dos), command palette, reply templates,
scheduled send, mail rules, blocked senders / VIPs, rich-text compose, new-mail
notifications.
