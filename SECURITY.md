# Security policy

## Reporting a vulnerability

Please report security vulnerabilities privately through GitHub's private vulnerability reporting feature for this repository. Do not open a public issue with exploit details, credentials, tokens, account identifiers, or personal calendar and task data.

Include the affected revision, impact, reproduction steps, and any suggested mitigation. You can expect an acknowledgement within seven days. A fix timeline will depend on severity and complexity.

## Supported versions

Security fixes are made on the latest revision of the default branch. This project does not currently publish versioned releases.

## Local data model

Productivity MCP runs locally. Google OAuth desktop credentials, refresh tokens, and the connected-account catalog are stored in the user's application-data directory unless their paths are overridden with environment variables. They are never intended to be committed to this repository. The server communicates with MCP clients over stdio and reserves stdout for protocol messages.

Anyone redistributing the application should review file permissions and platform credential-storage expectations for their target environment. The current token store is file based and is not a replacement for an operating-system keychain.
