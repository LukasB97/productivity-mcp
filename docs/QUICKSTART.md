# Start here

Productivity MCP runs locally. The configuration app connects your Google accounts and mailboxes;
your AI client starts the separate MCP server. Leaving the app open is not required.

## Connect an account

1. Extract the complete archive to a stable directory. Keep the `app` and `server`
   directories intact; do not copy just the executable.
2. Start `app/ProductivityMcp.App.exe` on Windows, or `app/ProductivityMcp.App` on
   macOS/Linux. These are unsigned portable builds, not installers or macOS app bundles.
3. If prompted for an OAuth file, import your Google **Desktop app** client JSON.
   Source/local packages do not bundle a client unless the packager explicitly supplies one.
4. Choose **Google: Kalender und Aufgaben** and sign in through your browser, and/or
   **E-Mail-Postfach verbinden** with your address and password. Gmail needs an
   [app password](https://myaccount.google.com/apppasswords) (2-Step Verification
   required); your normal Google password is rejected. Common providers are detected
   automatically; otherwise enter the IMAP/SMTP settings from your provider. Some
   providers (GMX, web.de) require enabling IMAP in their web settings first.
5. Check each service's status. **Verbindung prüfen** checks existing access;
   **Bei Google erneut anmelden** starts a new sign-in, **Mail-Passwort erneuern**
   replaces a rejected password. Cancellation keeps the previous token.

The app's interface is German. Each account card shows whether Calendar/Tasks and
email are connected independently. Expand the resource row to see calendar and
task-list names. **Einstellungen** contains optional app autostart and the email
image renderer. **E-Mail trennen** removes the stored mail password locally; revoke a
Gmail app password in your Google account to cut access entirely. Disconnecting removes
the local account token, not your Google data.

## Configure the AI client

Use your client's local stdio MCP configuration. For example, on Windows:

```json
{
  "mcpServers": {
    "productivity": {
      "command": "C:\\Tools\\ProductivityMcp\\server\\ProductivityMcp.Server.exe"
    }
  }
}
```

On macOS/Linux use an absolute path to `server/ProductivityMcp.Server` without
`.exe`. Reload the client's MCP connection after changing its configuration. Start
with `calendar.list()` or `email.accounts.list()`; no calendar/mail changes are
needed to verify access. Keep stderr separate from the stdout protocol stream.

## Platform notes and troubleshooting

- Linux token protection requires a running Secret Service and `secret-tool`
  (for example, the `libsecret-tools` package on Debian/Ubuntu). Chromium also
  needs its normal system libraries; install them using Playwright's documented
  `install-deps chromium` procedure if its startup reports missing libraries.
- macOS uses Keychain; Windows uses DPAPI. Do not copy encrypted user tokens between
  users or machines. Sign in again instead.
- Google verification is separate from installing this software. If Google blocks
  the project's OAuth client, use an approved test account or your own registered
  desktop client. A GitHub release does not imply Google approval.
- `authentication_error`: reconnect that account in the app. A network outage
  instead reports a failed check; retry **Verbindung prüfen** when connectivity returns.
- `permission_denied`: check both granted OAuth permissions and the selected
  calendar's `accessRole`. Read access does not imply permission to create events.
- `Transport closed`: check that the configured executable exists and inspect the
  AI client's server stderr log. The tray icon shows the configuration app, not
  the status of every MCP client process. The server does not keep its own log files.
- For email `format: "image"`, install the renderer from **Einstellungen** first.
  It is an additional download, not part of the archive. Remote images are blocked
  by default. Text formats do not require Chromium.

Never share account files, tokens, real message content, or unredacted logs in an issue.
