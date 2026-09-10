# Contributing

Thank you for considering a contribution to Productivity MCP.

## Before you start

Open an issue before making a large change so the design can be discussed before implementation. Small bug fixes, tests, and documentation corrections can go straight to a pull request.

Never include Google credentials, OAuth tokens, account data, calendar data, or task data in an issue, test fixture, commit, screenshot, or log excerpt. Use invented values in tests and redact logs before sharing them.

## Development setup

Install the .NET SDK selected by `global.json`, then run:

```powershell
dotnet restore --locked-mode
dotnet build ProductivityMcp.sln -c Release --no-restore
dotnet test ProductivityMcp.sln -c Release --no-build --no-restore
dotnet format ProductivityMcp.sln --verify-no-changes --no-restore
```

The automated tests use fakes and temporary files. They do not need a Google account or network access after package restore.

## Design boundaries

- `ProductivityMcp.Core` owns provider-neutral models, validation, and provider interfaces.
- Provider projects translate external APIs into the core contract and stable operation errors.
- `ProductivityMcp.Server` exposes thin MCP adapters and keeps stdout reserved for the protocol.
- `ProductivityMcp.App` owns interactive local setup and must share configuration paths with the server.

Keep provider-specific SDK types out of the core and server projects. Return expected external failures as `OperationError` values; let unexpected programming errors remain visible. New behavior should include focused tests at the lowest useful layer.

## Pull requests

Keep each pull request focused. Explain the user-visible behavior, note any security or compatibility implications, and list the verification you ran. Update the README or architecture document when a change affects setup, tools, data storage, or project boundaries.

By participating in this project, you agree to communicate respectfully and keep technical discussion focused on the work.
