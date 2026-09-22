# Release-readiness check — 2026-09-22

Scope: prepare the repository and the next release candidate, **not publish a release**.
The current development version is `0.1.2-dev`; the published `v0.1.1` remains unchanged.

## Verified locally on Windows x64

- Locked dependency restore and full Release build: successful, no warnings or errors.
- Complete non-live suite: **88 passed, 0 failed, 0 skipped**, including Chromium
  rendering, process-based single-instance tests and an isolated stdio MCP test.
- The stdio test discovers all **27 tools**, checks optional schema fields, exercises
  invalid email input, reloads a changed account catalog and confirms missing-token
  access returns an authentication error without starting browser sign-in.
- Formatting and `git diff --check`: successful.
- NuGet vulnerability audit including transitive dependencies: no known vulnerable
  packages reported by the configured NuGet source on this date.
- Gitleaks 8.30.1: no findings in the scanned Git history or staged changes. This is
  a scanner result, not a guarantee that every possible secret has been detected.
- Synthetic Avalonia preview: checked at default and 520-pixel minimum width,
  including long account names, mixed service states, expanded resource lists and
  scrolling. No real account data appeared in the preview.
- Local self-contained Windows archive: license, README, quickstart, app/server and
  Playwright driver included; private account/token files absent. The extracted
  server passed the same stdio MCP smoke test.
- Independent Astra source review: identified issues were corrected and retested;
  the final focused review reported no further concrete blocker in these changes.

## Explicitly not verified here

- A fresh live Google consent flow, real mail sending or real calendar changes.
  This pass intentionally left connected accounts and personal data untouched.
- Native macOS and Linux execution. Run the documented local suite and archive
  smoke checks on those systems before advertising a new build as tested there.
- Google OAuth approval, signing and macOS notarization. These are separate from
  repository readiness; the existing portable packages are unsigned.

## Cost and publication controls

GitHub Actions was disabled at repository level. Workflow definitions now accept
manual dispatch only, with no push, pull-request or tag triggers. No hosted runner
was started for this check. No release, tag, merge or repository visibility change
was performed. Private vulnerability reporting is enabled.

Use [release preparation](releasing.md) for the local commands and the explicit
publication gate. Test results above describe this check, not a perpetual guarantee
for later commits or dependency updates.
