# Reporting security issues

Report security issues privately through the repository's GitHub security reporting feature when available. Do not include account credentials, production configuration, personal files, or private installer payloads in public issues.

## Trust boundaries

Packages contain executable software. SHA-256 verifies integrity; an embedded public key alone does not identify a publisher. Pin the publisher's public-key fingerprint through a trusted channel when requiring signatures. Windows Authenticode signing uses a separate certificate.

HTTPS publisher credentials are supplied through the process environment or a session cookie and never belong in package manifests. The publisher client refuses cross-origin HTTP redirects to avoid forwarding credentials.

Use a dedicated deployment account and limit package publication permission. Publisher uploads must verify all payload hashes before becoming visible, reject conflicting immutable versions, and keep product download access consistent with the hosting application's account policy.

Source publication excludes signing private keys, live credentials, runtime state, product payloads, and hosting application source.
