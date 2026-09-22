# Architecture

Productivity MCP separates its stable domain contract from Google-specific transport code and the two local entry points.

```text
MCP client  --stdio-->  ProductivityMcp.Server
                                |       |
                                v       v
                   ProductivityMcp.Core  ProductivityMcp.Providers.Google  -->  Google APIs
                                ^       ^
                                |       |
                                +-- ProductivityMcp.App
                                           |
                                           +-- local configuration and protected OAuth tokens
```

## Projects

`ProductivityMcp.Core` defines calendar, task, and email models, validation, capability declarations, provider interfaces, and structured operation errors. It has no dependency on MCP or Google SDKs.

`ProductivityMcp.Email` owns MIME parsing and composition through MimeKit, HTML/Markdown conversion, and isolated email image rendering through Playwright. Provider SDK types and MIME trees never cross the public MCP boundary.

`ProductivityMcp.Providers.Google` adapts Google Calendar, Google Tasks, and Gmail. It owns OAuth, Gmail search translation, pagination, provider error translation, time-zone conversion, and routing across connected Google accounts.

`ProductivityMcp.Server` is a thin stdio MCP host. Tool classes validate input, call provider interfaces, and return structured content. Expected failures have stable machine-readable error codes. Logs go to stderr because stdout belongs to the MCP protocol.

`ProductivityMcp.App` is the Avalonia configuration and tray application. It imports OAuth desktop credentials, starts the browser authorization flow, manages connected accounts, and configures optional platform autostart. It does not host the MCP server or duplicate calendar and task features.

`ProductivityMcp.Tests` covers provider-neutral validation, MCP result contracts, Google mapping and time behavior, and configuration-app logic without contacting Google.

## Runtime flow

1. Official builds load the project's bundled Google OAuth desktop-client configuration. Source builds can import their own file into the local application-data directory, which takes precedence.
2. Only explicit app sign-in starts browser authorization. New/reconnected accounts request all selected scopes in a staging directory, verify account identity and granted permissions, then atomically replace the saved token. Cancellation or declined permissions leave the previous token intact. The encryption key is held by the operating-system credential store. The account catalog keeps only the local account key, email address, and enabled-service flags. Ordinary status checks and MCP calls never open a browser.
3. An MCP client launches `ProductivityMcp.Server` as a child process.
4. The server discovers connected accounts, creates Google providers on demand, and routes operations using direct provider resource ids.
5. Provider failures are translated into stable `OperationError` values before the MCP adapter serializes them.

## Important tradeoffs

Resource ids are passed through unchanged. Calendar mutations resolve across all applicable accounts before issuing a write. Event IDs are scoped to a calendar: multiple distinct matches return a conflict; the optional `calendarId` parameter selects one explicitly. Shared-calendar matches are deduplicated and a writable account is preferred. Failed lookups block mutation rather than hiding uncertainty. Task mutations retain their provider-ID lookup contract.

Calendar local times are resolved in the selected calendar's IANA time zone. Times that are missing or duplicated during daylight-saving transitions are rejected, asking the caller to provide an explicit offset rather than guessing.

The OAuth token store is local and platform-protected. It uses AES-256-GCM with a random master key protected by Windows DPAPI, macOS Keychain, or Linux Secret Service. Existing plaintext token files are migrated after their first successful read.

Email search cursors contain only provider continuation tokens plus a hash of the original query. They are rejected if reused with a different query. Message and thread IDs remain direct provider IDs. Email image rendering uses a sandboxed isolated Chromium context with JavaScript, service workers, downloads and local-file requests disabled. Remote images are opt-in; private or loopback network destinations are blocked. Input, remote image requests, page count and aggregate PNG size are bounded. Calendar queries also enforce a result bound instead of collecting indefinitely.
