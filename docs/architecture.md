# Architecture

Productivity MCP separates its stable domain contract from provider-specific transport code and the two local entry points.

```text
MCP client  --stdio-->  ProductivityMcp.Server
                                |       |
                                v       v
                   ProductivityMcp.Core  ProductivityMcp.Providers.Google  -->  Google Calendar/Tasks APIs
                                ^            ProductivityMcp.Providers.Imap    -->  IMAP/SMTP servers
                                ^       ^
                                |       |
                                +-- ProductivityMcp.App
                                           |
                                           +-- local configuration, protected OAuth tokens and mail passwords
```

## Projects

`ProductivityMcp.Core` defines calendar, task, and email models, validation, capability declarations, provider interfaces, and structured operation errors. It has no dependency on MCP or Google SDKs.

`ProductivityMcp.Email` owns MIME parsing and composition through MimeKit, HTML/Markdown conversion, and isolated email image rendering through Playwright. Provider SDK types and MIME trees never cross the public MCP boundary.

`ProductivityMcp.Providers.Google` adapts Google Calendar and Google Tasks. It owns OAuth, pagination, provider error translation, time-zone conversion, and routing across connected Google accounts. It deliberately requests no Gmail scope: Gmail scopes are classified as restricted and would require an annual third-party security assessment.

`ProductivityMcp.Providers.Imap` implements email over IMAP and SMTP with MailKit. Every MCP call opens its own authenticated connection. On Gmail (detected by the `X-GM-EXT-1` capability) it searches "All Mail" with Gmail's search syntax (`X-GM-RAW`), and uses Gmail message, thread, and label extensions; ids are the same hexadecimal ids the Gmail API uses. Other servers use standard IMAP SEARCH, special-use folders (with localized-name fallback), and ids that encode folder, UIDVALIDITY, and UID. Threads on other servers are grouped by the root `Message-ID` from `References`. Query cursors store an exclusive UID bound per folder, so new mail never shifts pages. Server settings come from a built-in provider table, then Mozilla's autoconfig database; plain-text connections are never offered.

`ProductivityMcp.Security` holds the AES-256-GCM file protection shared by Google tokens and mail passwords.

`ProductivityMcp.Server` is a thin stdio MCP host. Tool classes validate input, call provider interfaces, and return structured content. Expected failures have stable machine-readable error codes. Logs go to stderr because stdout belongs to the MCP protocol.

`ProductivityMcp.App` is the Avalonia configuration and tray application. It imports OAuth desktop credentials, starts the browser authorization flow, verifies and stores mailbox sign-ins, manages connected accounts, and configures optional platform autostart. It does not host the MCP server or duplicate calendar and task features.

`ProductivityMcp.Tests` covers provider-neutral validation, MCP result contracts, Google mapping and time behavior, and configuration-app logic without contacting Google.

## Runtime flow

1. Official builds load the project's bundled Google OAuth desktop-client configuration. Source builds can import their own file into the local application-data directory, which takes precedence.
2. Only explicit app sign-in starts browser authorization. New/reconnected Google accounts request the calendar and task scopes in a staging directory, verify account identity and granted permissions, then atomically replace the saved token. Cancellation or declined permissions leave the previous token intact. The encryption key is held by the operating-system credential store. The account catalog keeps only the local account key and email address. Mailboxes are stored separately with their server settings; the password is saved only after IMAP and SMTP sign-in succeeded. The app shows a Google account and a mailbox with the same address as one entry. Ordinary status checks and MCP calls never open a browser.
3. An MCP client launches `ProductivityMcp.Server` as a child process.
4. The server discovers connected accounts, creates Google and IMAP providers on demand, and routes calendar and task operations by direct provider resource ids and email operations by account address.
5. Provider failures are translated into stable `OperationError` values before the MCP adapter serializes them.

## Important tradeoffs

Resource ids are passed through unchanged. Calendar mutations resolve across all applicable accounts before issuing a write. Event IDs are scoped to a calendar: multiple distinct matches return a conflict; the optional `calendarId` parameter selects one explicitly. Shared-calendar matches are deduplicated and a writable account is preferred. Failed lookups block mutation rather than hiding uncertainty. Task mutations retain their provider-ID lookup contract.

Calendar local times are resolved in the selected calendar's IANA time zone. Times that are missing or duplicated during daylight-saving transitions are rejected, asking the caller to provide an explicit offset rather than guessing.

The OAuth token and mail password store is local and platform-protected. It uses AES-256-GCM with a random master key protected by Windows DPAPI, macOS Keychain, or Linux Secret Service. Existing plaintext token files are migrated after their first successful read.

Email search cursors contain only per-account continuation state (folder UIDVALIDITY and UID bounds) plus a hash of the original query. They are rejected if reused with a different query or after a folder's UIDVALIDITY changed. Standard IMAP compares dates by day, so top-level `after`/`before` are widened on the server and checked exactly against the internal date. Email image rendering uses a sandboxed isolated Chromium context with JavaScript, service workers, downloads and local-file requests disabled. Remote images are opt-in; private or loopback network destinations are blocked. Input, remote image requests, page count and aggregate PNG size are bounded. Calendar queries also enforce a result bound instead of collecting indefinitely.
