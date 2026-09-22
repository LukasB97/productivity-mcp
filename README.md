# Productivity MCP

Productivity MCP is a local Model Context Protocol server that gives MCP clients structured access to Google Calendar, Google Tasks, and Gmail. A small Avalonia desktop app handles OAuth setup, connected services, tray status, and optional autostart on Windows, macOS, and Linux.

The project targets .NET 10. Versioned, self-contained builds are published for Windows, macOS, and Linux.

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
calendar.events.update({ eventId, patch, calendarId? })
calendar.events.delete({ eventId, calendarId? })

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

List methods return direct Google ids, display names, and provider metadata. Calendar entries also include their IANA `timeZone` and `accessRole`. Google Tasks does not expose the default task list, so its `isDefault` value is `null`.

Event IDs are unique within a calendar, not across calendars. Updates/deletes resolve the target before writing: an ambiguous ID returns `conflict` without changes. Supply the event's `calendarId` to disambiguate. A shared calendar appearing in several accounts is one target; an account with write access is selected.

Calendar event times accept an all-day date in `yyyy-MM-dd`, an RFC 3339 timestamp with an explicit offset, or a local timestamp such as `2026-12-10T14:00:00`. Local timestamps use the selected calendar's time zone. All-day end dates and query upper bounds are exclusive. Google Tasks due dates are date-only and use `yyyy-MM-dd`.

Set `event.videoMeeting` to `true` when creating an event to request a native Google Meet link. In a patch, `true` creates a link, `false` removes it, and omission leaves it unchanged. Calendars without Google Meet support return an `unsupported` error without changing the event.

## Prerequisites

- The .NET 10 SDK selected by [`global.json`](global.json) when building from source.
- An MCP client that can launch a local stdio server.

## Build and test

```powershell
dotnet restore --locked-mode
dotnet build ProductivityMcp.sln -c Release --no-restore
pwsh src/ProductivityMcp.App/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test --project tests/ProductivityMcp.Tests/ProductivityMcp.Tests.csproj -c Release --no-build --no-restore --filter "TestCategory!=Live"
dotnet format ProductivityMcp.sln --verify-no-changes --no-restore
```

The non-live test suite uses synthetic data and temporary directories; it does not contact Google. Renderer tests require the one-time Chromium download above (and Chromium's system dependencies on Linux). Use `./scripts/verify.ps1` for subsequent local verification, or `-SkipRenderer` for an explicitly partial, text-only check.

GitHub Actions is disabled and all workflows are manual-only to avoid hosted-runner costs. No workflow runs on pushes, pull requests or tags. See [local release preparation](docs/releasing.md) for packaging and platform checks.

The [latest readiness check](docs/release-readiness.md) records actual verification results and the checks still required before distribution.

## Install and connect Google

Official archives are available under [GitHub Releases](https://github.com/LukasB97/productivity-mcp/releases). They contain the configuration app and MCP server in separate folders and include the project's public Google OAuth desktop-client configuration. They never contain a user's Google credentials or tokens.

1. Download and extract the archive for your operating system.
2. Start the app from the `app` folder.
3. Choose Calendar/Tasks or Gmail and complete Google's authorization in your browser.
4. Configure your MCP client to launch the executable in the archive's `server` folder.

See [Quickstart and troubleshooting](docs/QUICKSTART.md) for executable names, client configuration and platform prerequisites. Archives are unsigned portable builds, not installers. Availability of an archive is not a claim of Google OAuth approval; access may still be restricted to approved test accounts.

When building from source, create your own Google Cloud configuration:

1. In Google Cloud Console, enable the Google Calendar API, Google Tasks API, and Gmail API.
2. Configure the OAuth consent screen for the accounts that will use the server.
3. Create an OAuth client with the Desktop app application type and download its JSON file.
4. Start the configuration app with `dotnet run --project src/ProductivityMcp.App`.
5. Select the downloaded JSON file, sign in through the browser, and grant the requested Calendar and Tasks scopes.
6. Connect E-Mail for an existing account or add an E-Mail-only account. Gmail uses `gmail.modify`; an existing account is asked for all selected scopes again. Authorization is staged, so declining it does not replace a working token.

The app shows service states per account, provides a reconnect action, and distinguishes expired authorization from a failed network check. Reconnection verifies identity and granted scopes before replacing the saved token. The MCP server never opens a browser; only an explicit sign-in in the app does so. The tray icon and optional autostart belong to the configuration app, not the client-owned MCP process.

Image output uses Playwright Chromium, which the app installs only when requested. Plain, HTML, and Markdown reading work without it. Its current interface is German and uses a consistent light theme; provider and MCP errors are English.

By default, credentials, account metadata, and OAuth tokens live under the platform application-data directory in a `ProductivityMcp` folder. Override individual paths with:

| Variable | Purpose |
| --- | --- |
| `PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS` | Google OAuth desktop-client JSON file |
| `PRODUCTIVITY_MCP_GOOGLE_TOKENS` | Root directory for account token stores |
| `PRODUCTIVITY_MCP_GOOGLE_ACCOUNTS` | Connected-account catalog JSON file |

The OAuth client file identifies the application and is not a user credential. OAuth tokens are sensitive: Productivity MCP encrypts them with a random key protected by Windows DPAPI, macOS Keychain, or Linux Secret Service. Existing plaintext token files are migrated in place the first time they are read. See [Security](SECURITY.md) for details.

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
./scripts/package.ps1 -Runtime win-x64
```

Replace `win-x64` with the needed runtime identifier, such as `linux-x64` or `osx-arm64`. This local-only script keeps RID-specific restore changes out of checked-in lockfiles and includes the license, quickstart and checksums. It does not publish anything. An OAuth client is bundled only when explicitly supplied with `-OAuthClientPath`.

## Project structure

```text
src/ProductivityMcp.Core              Provider-neutral models and contracts
src/ProductivityMcp.Email             MIME, format conversion, and isolated rendering
src/ProductivityMcp.Providers.Google  Google OAuth and API adapters
src/ProductivityMcp.Server            stdio MCP host and tool adapters
src/ProductivityMcp.App               Avalonia setup and tray application
tests/ProductivityMcp.Tests           Unit and contract tests
tests/ProductivityMcp.UiPreview       Synthetic-data desktop layout preview
```

The detailed dependency boundaries, runtime flow, and tradeoffs are documented in [Architecture](docs/architecture.md).

## Current limitations

- Google is currently the only provider; the public contracts remain provider-neutral.
- Update and delete calls identify resources by direct provider id, so the server may search connected accounts to locate them.
- Calendar queries reject results above 5,000 events; narrow the calendar/time range rather than receiving silent truncation.
- Email image output is limited to 20 pages and 20 MiB of PNGs per message. Oversized input returns `unsupported`; use a text format instead. At most 16 external image requests are attempted, and only with `loadRemoteImages: true`.
- Live-Google checks are intentionally manual and limited to clearly marked data in the tester's own account.
- Linux requires a working Secret Service implementation and the `secret-tool` command for OAuth token protection.

## Contributing and security

See [Contributing](CONTRIBUTING.md) for the development workflow and project boundaries. Report vulnerabilities through the private process in [Security](SECURITY.md).

Licensed under the [MIT License](LICENSE).
