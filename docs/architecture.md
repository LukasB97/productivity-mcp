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
                                           +-- local configuration and OAuth tokens
```

## Projects

`ProductivityMcp.Core` defines calendar, task, and email models, validation, capability declarations, provider interfaces, and structured operation errors. It has no dependency on MCP or Google SDKs.

`ProductivityMcp.Email` owns MIME parsing and composition through MimeKit, HTML/Markdown conversion, and isolated email image rendering through Playwright. Provider SDK types and MIME trees never cross the public MCP boundary.

`ProductivityMcp.Providers.Google` adapts Google Calendar, Google Tasks, and Gmail. It owns OAuth, Gmail search translation, pagination, provider error translation, time-zone conversion, and routing across connected Google accounts.

`ProductivityMcp.Server` is a thin stdio MCP host. Tool classes validate input, call provider interfaces, and return structured content. Expected failures have stable machine-readable error codes. Logs go to stderr because stdout belongs to the MCP protocol.

`ProductivityMcp.App` is the Avalonia configuration and tray application. It imports OAuth desktop credentials, starts the browser authorization flow, manages connected accounts, and configures optional platform autostart. It does not host the MCP server or duplicate calendar and task features.

`ProductivityMcp.Tests` covers provider-neutral validation, MCP result contracts, Google mapping and time behavior, and configuration-app logic without contacting Google.

## Runtime flow

1. The desktop app imports a Google OAuth desktop-client JSON file and stores it in the local application-data directory.
2. Google authorization writes one refresh token with the account's selected scopes to its account-specific directory. Adding Gmail requests the combined selected scopes in a staging directory and verifies the account identity before replacing a working token. The account catalog keeps only the local account key, email address, and enabled-service flags.
3. An MCP client launches `ProductivityMcp.Server` as a child process.
4. The server discovers connected accounts, creates Google providers on demand, and routes operations using direct provider resource ids.
5. Provider failures are translated into stable `OperationError` values before the MCP adapter serializes them.

## Important tradeoffs

Resource ids are passed through unchanged. Update and delete operations search connected accounts because their public MCP signatures contain an event or task id but no account id. This keeps tool calls small at the cost of extra provider requests.

Calendar local times are resolved in the selected calendar's IANA time zone. Times that are missing or duplicated during daylight-saving transitions are rejected, asking the caller to provide an explicit offset rather than guessing.

The OAuth token store is deliberately simple and local. It supports a small personal server well, but packaged multi-user or managed deployments should replace it with platform-protected secret storage.

Email search cursors contain only provider continuation tokens plus a hash of the original query. They are rejected if reused with a different query. Message and thread IDs remain direct provider IDs. Email image rendering disables JavaScript and local-file access; remote images are opt-in and private or loopback network destinations are blocked.
