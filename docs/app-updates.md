# Optional application updates

Every app installer is standalone. Selecting its optional updates feature installs
`heirowUpdater.exe` and the generated `heirowUpdater.json` in the app's root.
The companion contains its own verification and installation engine; it does not
require a globally installed heirowInstaller or CLI. Leaving the feature off
installs neither file and does not change ordinary app startup.

At startup call `SocketJack.HeirowUpdaterBootstrap.StartAfterExit()` from the
dependency-free C# or VB template in `templates/bootstrap`. The SDK equivalent is
`ApplicationUpdater.StartAfterExit(configurationPath)`. Installer-authored main
shortcuts also use `heirowUpdater.exe launch --config <profile> -- <app arguments>`.
The launcher opens the app once; a direct bootstrap and launcher can coexist.
Each native prompt runs as a short-lived trusted UI process; the single lifecycle
owner remains active between manual checks.

One companion owns each canonical app root. It binds the exact app PID and UTC
creation time and waits until all instances of that app executable have exited.
It then checks the publisher's feed through SocketJack. A newer release opens a
native Project-Z prompt with **Update now** and **Postpone 24 hours**. Closing the
prompt postpones. A postponed choice is stored per user and app root; it never
permits automatic installation. Reopening an app during a prompt or download
defers file replacement until all instances close again.

The companion downloads the complete installer, verifies its feed digest,
installed publisher pin, product, version and payload, then copies its currently
trusted updater outside the app root. That helper waits for both the watcher and
all app instances before applying the package as data. A downloaded EXE prefix
is never executed. Machine updates request UAC only after the user chooses to
update. Legacy scheduler `enable --apply` remains an explicit separate opt-in.

Repair, uninstall, and updates-feature removal signal a root-specific maintenance
event. The companion closes its prompt or cancels its wait without terminating
the app or host. A staged helper retains that signal during startup and checks
the installed receipt inside the installation lock, so maintenance cannot leave
a waiting updater that later recreates a removed app. `heirowUpdater.exe stop
--config heirowUpdater.json` requests the same polite stop.

## App and plugin integration

The template methods are optional and never throw into the app:

```csharp
SocketJack.HeirowUpdaterBootstrap.StartAfterExit();
SocketJack.HeirowUpdaterBootstrap.CheckForUpdates();
```

For a protected SocketJack feed, pass the active app session in memory:

```csharp
SocketJack.HeirowUpdaterBootstrap.StartAfterExit(
    bearerToken: currentSessionToken,
    appRoot: applicationDirectory);
```

The optional `sessionCookie` argument accepts a complete Cookie header value.
Credentials are set only on the child process's `HEIROWUPDATER_TOKEN` or
`HEIROWUPDATER_COOKIE` environment entries. They are never placed in command-line
arguments, profiles, global environment variables or desktop preferences.
Profiles name the corresponding environment variables and exact credential
origin. The transport sends credentials only to that origin.

For profiles with an `authApplication` registered by SocketJack, an expired
session (HTTP 401) offers a native **Sign in** action and browser approval. The
companion obtains a fresh PKCE-bound session in memory and retries the request.
HTTP 403 remains an access or entitlement error; signing in does not grant
administrator status or bypass a publisher's access rules.

Plugins pass their known installation root through `appRoot`. The exact supplied
host PID and creation time remain bound, and the updater also waits for all
instances of that canonical host executable. A host path grants no execution or
update authority; it adds a wait condition. This prevents replacing plugin files
while an FL Studio host still has them loaded.
When a plugin joins an already running standalone app's watcher, it registers
the host's verified identity with that app root. The existing owner and its
trusted update helper include that host and all its instances in their waits.

## Manual checks

`CheckForUpdates()` starts a check immediately and opens the same native prompt.
When the app is still running, choosing Update now prepares the update and waits
for the app or plugin host to close. An existing watcher accepts the request so
there is one long-running companion per app root.

For an app-owned status label, use the read-only JSON command:

```text
heirowUpdater.exe check-for-updates --config heirowUpdater.json --json --no-prompt
```

It returns `status` (`current` or `available`), `productId`, `name`,
`currentVersion`, `latestVersion`, `available` and `deferredUntilExit`. Errors
return a nonzero exit code. This command never installs an update.

The generated profile includes `formatVersion`, `productId`, `name`,
`currentVersion`, `feedUrl`, absolute `installDirectory` and `stateRoot`, `scope`,
`publisherKeySha256`, selected `features`, relative `entryPoint`, `mode`,
`credentialOrigin` and optional cookie/token environment-variable names.
Production feeds and credential origins use HTTPS. Explicit isolated validation
can use loopback HTTP. An installed publisher pin is required for the optional
lifecycle feature.

`scripts/Test-AppUpdateLifecycle.ps1` runs the supplied self-contained companion
with generated signed fixtures. It compiles the public app bootstrap, protects
the loopback feed with an ephemeral child-process token, drives the actual
native prompt, and checks feature-off startup, all-instance waiting, persisted
Postpone, manual Update now, reopening, per-app isolation and trusted-helper
installation without a global installer payload.
It also checks external-host identity, repeated manual requests, and real repair
and uninstall while a companion is waiting for an external host.
