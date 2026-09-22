# Local verification and release preparation

GitHub Actions is disabled for this repository. Both workflow files are
**manual-only**: pushes, pull requests and tags do not schedule hosted runners.
Do not re-enable or dispatch them without an explicit cost decision. Nothing in
the local scripts creates a tag, uploads an asset or publishes a release.

## Verify locally

Use PowerShell 7 and the SDK pinned in `global.json`:

```powershell
./scripts/verify.ps1 -InstallRenderer
```

The renderer download is only needed once per Playwright version. Subsequent
runs can omit `-InstallRenderer`. A text-only development check can use
`-SkipRenderer`; record that image tests were omitted. Linux may need Chromium
system dependencies. Live tests are excluded in all these commands.

For manual GUI checks without exposing real data:

```powershell
dotnet run --project tests/ProductivityMcp.UiPreview -c Release --no-build
dotnet run --project tests/ProductivityMcp.UiPreview -c Release --no-build -- --compact
```

This preview uses invented accounts, no account configuration and no OAuth flow.
Check minimum/default widths, long names, expanded resource lists, account error
states, scrolling and keyboard focus. Preview buttons use a fake setup service;
they are not an authentication end-to-end test.

## Build an archive without publishing

```powershell
./scripts/package.ps1 -Runtime win-x64
```

Other supported runtime identifiers are `linux-x64`, `osx-x64`, and `osx-arm64`.
An official desktop OAuth client may be supplied with `-OAuthClientPath`; never
use a user-token file. The script builds self-contained app/server directories,
includes the license and documentation, and writes an archive plus SHA-256 checksum
under `artifacts/packages`. Runtime-specific dependency graphs stay in `obj`;
tracked lockfiles are checked for accidental changes. Run a normal locked restore
again before resuming source builds after RID-specific publishing.

Cross-publishing is not a substitute for execution tests on the target OS. Before
publicly distributing a new release, run the local checks and an archive smoke
test on each supported platform. Do not mark an untested platform as verified.

## Explicit publication gate

Only after a maintainer approves publication:

1. Review `CHANGELOG.md`, dependency/security results and unresolved release notes.
2. Change the development version suffix to the intended release version and commit.
3. Verify from that clean commit; keep user tokens and real-account data out of all assets.
4. Check Google OAuth verification/test-user restrictions separately. Gmail scopes
   require Google's corresponding verification process.
5. Smoke-test the packaged app and stdio server on the advertised platforms.
6. Create the version tag and GitHub release explicitly, uploading archives and checksums.

There is no automatic publication step in `scripts/package.ps1`. Code signing and
macOS notarization are not currently provided; describe the archives as unsigned
portable builds rather than signed installers.
