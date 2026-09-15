# Security policy

## Reporting a vulnerability

Please report security vulnerabilities privately through GitHub's private vulnerability reporting feature for this repository. Do not open a public issue with exploit details, credentials, tokens, account identifiers, or personal calendar, task, or email data.

Include the affected revision, impact, reproduction steps, and any suggested mitigation. You can expect an acknowledgement within seven days. A fix timeline will depend on severity and complexity.

## Supported versions

Security fixes are made on the latest published release and the default branch.

## Local data model

Productivity MCP runs locally. The Google OAuth desktop-client configuration, encrypted refresh tokens, and connected-account catalog are stored in the user's application-data directory unless their paths are overridden with environment variables. User tokens are never included in releases and must never be committed. The server communicates with MCP clients over stdio and reserves stdout for protocol messages.

OAuth token files are encrypted with AES-256-GCM. Their random master key is protected by Windows DPAPI for the current user, macOS Keychain, or Linux Secret Service; Linux additionally requires `secret-tool`. Directories and encrypted files are restricted to the current user on Unix. Legacy plaintext token files are rewritten in encrypted form after a successful read.

The OAuth desktop client id and client secret identify the public application; they cannot authenticate as a user and are not treated as user secrets. Official release builds receive this configuration from a protected GitHub Actions secret. Source builds can use their own Google Cloud project without modifying source code.

Downloaded attachments are written to the app's local download directory with collision-resistant names. Email image rendering uses a fresh browser context with JavaScript and local-file access disabled. Loading remote images is off by default; enabling it can disclose the user's IP address and message-open timing to external senders.
