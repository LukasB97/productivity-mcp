# Changelog

All notable changes to Productivity MCP are documented here. The project follows semantic versioning from its first public release.

## Unreleased

- Resolve calendar mutations before writing; reject ambiguous event IDs and support optional `calendarId` scoping.
- Use CalendarList permissions for calendar preflight and prefer writable accounts for shared calendars.
- Preserve nullable fields required by MCP result schemas and reserve stdout for protocol traffic.
- Validate nested email bodies, attachments, recipients, query conditions and null collections.
- Add explicit staged account reconnection, verify granted scopes, preserve existing access on cancellation, and keep MCP authentication noninteractive.
- Show independent account/service errors and a reconnect action in a wrapping, scrollable configuration layout; move secondary settings into an expander.
- Bound email rendering and calendar query output, and avoid overlapping final image pages.
- Include the prior single-instance app and separate read/compose email-format fixes in the next release.
- Restore portable dependency lockfiles; update ProtectedData to 10.0.12.
- Add regression tests, synthetic UI preview, local verification/packaging scripts, quickstart and release instructions.
- Make hosted workflows manual-only; pushes, pull requests and tags do not schedule runners.

## 0.1.1

- Replace broad Google Calendar access with event access and read-only calendar-list access.

## 0.1.0

- Add provider-neutral MCP tools for Google Calendar, Google Tasks, and Gmail.
- Add multi-account OAuth setup through an Avalonia desktop app with tray status and optional autostart.
- Add Google Meet creation, structured errors, time-zone-aware calendar operations, email drafts, attachments, and four email read formats.
- Protect local OAuth tokens through the operating-system credential store and migrate legacy plaintext tokens.
- Publish self-contained builds for Windows, Linux, and macOS.
