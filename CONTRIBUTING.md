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
pwsh src/ProductivityMcp.App/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test --project tests/ProductivityMcp.Tests/ProductivityMcp.Tests.csproj -c Release --no-build --no-restore --filter "TestCategory!=Live"
dotnet format ProductivityMcp.sln --verify-no-changes --no-restore
```

Non-live tests use fakes, a local MCP child process and temporary files. They need no Google account. Renderer tests also need Playwright Chromium: its first installation downloads browser binaries, and Linux requires Chromium system libraries. After those prerequisites, tests do not need external services.

`scripts/verify.ps1` runs the same checks locally; `-InstallRenderer` installs Chromium and `-SkipRenderer` intentionally omits its tests. Never enable live tests in a routine check. For deliberate own-account tests, set both `PRODUCTIVITY_MCP_LIVE_TESTS=1` and `PRODUCTIVITY_MCP_LIVE_ACCOUNT` to an already-connected test mailbox. They send only to that address and modify only their generated data. They do not start interactive sign-in.

GitHub workflows are manual-only and Actions is disabled to prevent runner charges. Do not enable or dispatch hosted workflows without maintainer approval. See [release preparation](docs/releasing.md) for local packaging and the synthetic UI preview.

## Design boundaries

- `ProductivityMcp.Core` owns provider-neutral models, validation, and provider interfaces.
- Provider projects translate external APIs into the core contract and stable operation errors.
- `ProductivityMcp.Server` exposes thin MCP adapters and keeps stdout reserved for the protocol.
- `ProductivityMcp.App` owns interactive local setup and must share configuration paths with the server.

Keep provider-specific SDK types out of the core and server projects. Return expected external failures as `OperationError` values; let unexpected programming errors remain visible. New behavior should include focused tests at the lowest useful layer.

## Pull requests

Keep each pull request focused. Explain the user-visible behavior, note any security or compatibility implications, and list the verification you ran. Update the README or architecture document when a change affects setup, tools, data storage, or project boundaries.

By participating in this project, you agree to communicate respectfully and keep technical discussion focused on the work.
