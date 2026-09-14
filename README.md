# Productivity MCP

Productivity MCP is a local Model Context Protocol server that gives MCP clients structured access to Google Calendar, Google Tasks, and Gmail. A small Avalonia desktop app handles OAuth setup, connected services, tray status, and optional autostart on Windows, macOS, and Linux.

The project targets .NET 10. It is an early-stage personal tool: the tool contract is tested, but there are no versioned releases yet.

## Features

- Query, create, update, and delete Google Calendar events.
- List, create, update, complete, and delete Google Tasks.
- Search, read, send, forward, label, archive, and trash Gmail messages; manage threads, attachments, and drafts.
- Read mail as plain text, HTML, Markdown, or locally rendered images.
- Connect more than one Google account and route direct provider ids to the right account.
- Create or remove native Google Meet links on calendar events.
- Return structured, machine-readable errors for validation, authentication, permissions, conflicts, rate limits, and provider outages.
- Interpret all-day dates, fixed instants, and calendar-local times without silently guessing across daylight-saving transitions.

## MCP tools

```text
calendar.list()
calendar.events.query({ query })
calendar.events.create({ calendarId, event })
calendar.events.update({ eventId, patch })
calendar.events.delete({ eventId })

tasks.lists.list()
tasks.list({ taskListId })
tasks.create({ taskListId, task })
tasks.update({ taskId, patch })
tasks.complete({ taskId })
tasks.delete({ taskId })

email.accounts.list()
email.messages.query({ query })
email.messages.get({ account, messageIds, format, loadRemoteImages? })
email.messages.send({ account, message })
email.messages.forward({ account, messageId, to, cc?, bcc?, body? })
email.messages.update({ account, messageIds, patch })
email.messages.archive({ account, messageIds })
email.messages.trash({ account, messageIds })
email.threads.get({ account, threadIds, format, loadRemoteImages? })
email.attachments.get({ account, messageId, attachmentId })
email.drafts.list({ account, pageSize?, cursor? })
email.drafts.create({ account, draft })
email.drafts.update({ account, draftId, patch })
email.drafts.send({ account, draftId })
email.labels.list({ account })
email.labels.create({ account, name })
```

List methods return direct Google ids, display names, and provider metadata. Calendar entries also include their IANA `timeZone`. Google Tasks does not expose the default task list, so its `isDefault` value is `null`.

Calendar event times accept an all-day date in `yyyy-MM-dd`, an RFC 3339 timestamp with an explicit offset, or a local timestamp such as `2026-12-10T14:00:00`. Local timestamps use the selected calendar's time zone. All-day end dates and query upper bounds are exclusive. Google Tasks due dates are date-only and use `yyyy-MM-dd`.

Set `event.videoMeeting` to `true` when creating an event to request a native Google Meet link. In a patch, `true` creates a link, `false` removes it, and omission leaves it unchanged. Calendars without Google Meet support return an `unsupported` error without changing the event.

## Prerequisites

- The .NET 10 SDK selected by [`global.json`](global.json).
- A Google Cloud project with the Google Calendar API, Google Tasks API, and Gmail API enabled.
- OAuth 2.0 credentials created as a Desktop app.
- An MCP client that can launch a local stdio server.

## Build and test

```powershell
dotnet restore --locked-mode
dotnet build ProductivityMcp.sln -c Release --no-restore
dotnet test ProductivityMcp.sln -c Release --no-build --no-restore
dotnet format ProductivityMcp.sln --verify-no-changes --no-restore
```

The test suite uses fakes and temporary directories. It does not contact Google. CI runs the same build and tests on Windows, macOS, and Linux.

## Google setup

1. In Google Cloud Console, enable the Google Calendar API, Google Tasks API, and Gmail API.
2. Configure the OAuth consent screen for the accounts that will use the server.
3. Create an OAuth client with the Desktop app application type and download its JSON file.
4. Start the configuration app with `dotnet run --project src/ProductivityMcp.App`.
5. Select the downloaded JSON file, sign in through the browser, and grant the requested Calendar and Tasks scopes.
6. Connect E-Mail for an existing account or add an E-Mail-only account. Gmail uses `gmail.modify`; an existing account is asked for all selected scopes again. Authorization is staged, so declining it does not replace a working token.

The app can add multiple accounts, show the enabled service state, disable Gmail locally, remove individual accounts after confirmation, and start itself with the operating system. Image output uses Playwright Chromium, which the app installs only when requested. Plain, HTML, and Markdown reading work without it. Its current interface is German; provider and MCP errors are English.

By default, credentials, account metadata, and OAuth tokens live under the platform application-data directory in a `ProductivityMcp` folder. Override individual paths with:

| Variable | Purpose |
| --- | --- |
| `PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS` | Google OAuth desktop-client JSON file |
| `PRODUCTIVITY_MCP_GOOGLE_TOKENS` | Root directory for account token stores |
| `PRODUCTIVITY_MCP_GOOGLE_ACCOUNTS` | Connected-account catalog JSON file |

These files contain sensitive local data and must not be committed or shared. OAuth tokens currently use a file-based store rather than the operating-system keychain; see [Security](SECURITY.md) before redistributing the app.

## Connect an MCP client

Build the server first, then configure the MCP client to launch it over stdio. The exact settings file belongs to the client, but a typical entry looks like this:

```json
{
  "mcpServers": {
    "productivity": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/productivity-mcp/src/ProductivityMcp.Server/bin/Release/net10.0/ProductivityMcp.Server.dll"
      ]
    }
  }
}
```

Use an absolute path and adapt path separators for the operating system. The server writes protocol messages to stdout and logs to stderr, so the client should keep both streams separate.

For a portable folder instead of a development build:

```powershell
dotnet publish src/ProductivityMcp.Server -c Release -r win-x64 --self-contained true -o artifacts/server
dotnet publish src/ProductivityMcp.App -c Release -r win-x64 --self-contained true -o artifacts/app
```

Replace `win-x64` with the needed runtime identifier, such as `linux-x64` or `osx-arm64`. Framework-dependent publishing is also supported by setting `--self-contained false`.

## Project structure

```text
src/ProductivityMcp.Core              Provider-neutral models and contracts
src/ProductivityMcp.Email             MIME, format conversion, and isolated rendering
src/ProductivityMcp.Providers.Google  Google OAuth and API adapters
src/ProductivityMcp.Server            stdio MCP host and tool adapters
src/ProductivityMcp.App               Avalonia setup and tray application
tests/ProductivityMcp.Tests           Unit and contract tests
```

The detailed dependency boundaries, runtime flow, and tradeoffs are documented in [Architecture](docs/architecture.md).

## Current limitations

- Google is currently the only provider; the public contracts remain provider-neutral.
- Update and delete calls identify resources by direct provider id, so the server may search connected accounts to locate them.
- Live-Google checks are intentionally manual and limited to clearly marked data in the tester's own account.
- OAuth tokens are stored in local files and depend on host filesystem permissions for protection.

## Contributing and security

See [Contributing](CONTRIBUTING.md) for the development workflow and project boundaries. Report vulnerabilities through the private process in [Security](SECURITY.md).

Licensed under the [MIT License](LICENSE).
