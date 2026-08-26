# Productivity MCP

A small local MCP server for Google Calendar and Google Tasks, built with C# and .NET 10. An Avalonia desktop app manages the local Google connection on Windows, macOS, and Linux.

## Tools

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
```

List methods return direct Google provider ids together with human-readable names and provider metadata. Calendar entries also include their IANA `timeZone`. Google Tasks does not expose which task list is the default, so `isDefault` is `null` there.

Calendar event times accept `yyyy-MM-dd` for all-day events, RFC 3339 timestamps with an explicit offset for fixed instants, or local timestamps such as `2026-12-10T14:00:00`, which are interpreted in the selected calendar's default time zone. Google Tasks due dates are date-only and should be supplied as `yyyy-MM-dd`.

## Google setup

1. Enable the Google Calendar API and Google Tasks API in a Google Cloud project.
2. Create OAuth credentials for a desktop application.
3. Download the credentials JSON to `%APPDATA%\ProductivityMcp\credentials.json`, or set `PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS` to its path.
4. Open the configuration app and connect one or more Google accounts. The browser-based OAuth flow stores separate refresh tokens under `%APPDATA%\ProductivityMcp\tokens` by default.

Override the token directory with `PRODUCTIVITY_MCP_GOOGLE_TOKENS` and the account catalog with `PRODUCTIVITY_MCP_GOOGLE_ACCOUNTS`.

## Build and run

```powershell
dotnet build ProductivityMcp.sln
dotnet run --project src/ProductivityMcp.Server
dotnet run --project src/ProductivityMcp.App
```

The server uses stdio. Logs are written to stderr so stdout remains reserved for MCP messages.

## Configuration app

The desktop app uses the same credential and token paths as the MCP server. It can:

- validate and import Google OAuth desktop credentials;
- add multiple Google accounts through the browser-based authorization flow;
- display every connected account together with its calendars and task lists;
- remove an individual account after confirmation.

The app does not duplicate Calendar or Tasks features. MCP clients continue to start the background server directly and route operations across all connected accounts.

Publish a small Windows x64 single-file build that requires .NET 10:

```powershell
dotnet publish src/ProductivityMcp.App -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

For a build that does not require a separately installed .NET runtime, change `--self-contained` to `true`.
