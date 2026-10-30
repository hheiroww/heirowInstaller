# Optional application updates

When an interactive update check, download, or installation fails, heirowUpdater opens the application's SocketJack download page once and explains the manual installer recovery in the result window. **Open download page** can reopen the page or retry if the browser could not start. FruityLoopsCollab opens its Downloads tab; other applications use their signed package's SocketJack homepage. Browser recovery never includes updater credentials or URL query values. Cancelled and headless operations do not open a browser. The updater continues to require a signed incremental package matching the installed version for file updates.

Every app installer is standalone. Selecting its optional updates feature creates
a small `heirowUpdater.json` in the app's root and provisions the runtime once in
a scope-wide, content-addressed `.updater-runtime` directory. Matching applications
reuse the same immutable executable. The profile binds its version, SHA256 and
size; the bootstrap derives its location and verifies its bytes. The installer
provisions it automatically, without a separate authoring-tool installation.
Leaving the feature off creates no application profile or reference. Existing
signed profiles with no shared binding retain their legacy app-local behavior.

At startup call `SocketJack.HeirowUpdaterBootstrap.StartAfterExit()` from the
dependency-free C# or VB template in `templates/bootstrap`. The SDK equivalent is
`ApplicationUpdater.StartAfterExit(configurationPath)`. Installer-authored main
shortcuts use the resolved shared executable with `launch --config <profile> -- <app arguments>`.
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

New native packages select `updateMode: "files"`. The companion downloads a
compressed `.hupd` transition containing changed files and signed old/new
metadata. It verifies the installed base, publisher, versions, roots and payload,
then runs the already trusted shared runtime as a helper. It waits for the exact
watcher and all application dependencies before applying files. It reconstructs
the full repair cache locally without downloading a Setup executable. Existing
opaque MSI/executable adapters retain their legacy delivery protocol until
migrated. Machine updates request UAC after the user chooses to update. Legacy
scheduler `enable --apply` remains an explicit separate opt-in. See
[incremental updates](incremental-updates.md) for format and crash recovery.

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
    SocketJack.HeirowUpdaterBootstrap.CaptureSession(bearerToken: currentSessionToken),
    applicationDirectory);
```

Capture the immutable snapshot before queueing background work. Capture from the
active in-memory account, then pass that snapshot to `StartAfterExit` or
`CheckForUpdates`. On login changes, capture and submit a replacement; capture
with both credentials empty to explicitly clear an old login. A stale queued
snapshot cannot replace a newer account snapshot. The running owner accepts
replacements through an authenticated same-user local pipe with exact process
identity and replay checks; an obsolete authentication prompt is cancelled.

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

Apps that own their update status UI should use the code-only `SocketJackVersionCatalogClient.CheckAsync` operation described in [the SDK guide](sdk.md#version-checks-without-installer-executables). Set an immediate busy indicator, supply the actual installed version and current in-memory account session, then show the returned `Current`, `UpdateAvailable` or `Ahead` result. This small catalog request does not require or launch an updater/installer executable, and it does not download an installation payload. Account or network errors stay visible as failures. Starting an actual update remains a separate explicit action.

`CheckForUpdates()` starts a check immediately with native progress and a visible
result. A current application reports that it is up to date; a newer release
offers **Update now** and **Postpone 24 hours**. Check failures remain visible so
the user can retry. An existing watcher accepts the request so there is one
long-running companion per app root.

When the app is still running, choosing **Update now** prepares the update, then
offers **Close apps and continue** or **Cancel update** before replacing files.
Normal close requests preserve an application's own save prompts. A kept-open
app returns to retry/cancel. Dismissing a current-version result keeps the
application running and preserves its later automatic check after exit.

Existing integrations can also use the read-only JSON command below when a companion executable is available:

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
lifecycle feature. Shared profiles also contain `sharedRuntime` and new native
profiles select `updateMode: "files"`; neither field contains credentials.

`scripts/Test-AppUpdateLifecycle.ps1` runs the supplied self-contained companion
with generated signed fixtures. It compiles the public app bootstrap, protects
the loopback feed with an ephemeral child-process token, drives the actual
native prompt, and checks feature-off startup, all-instance waiting, persisted
Postpone, manual Update now, reopening, per-app isolation and trusted-helper
installation without a global installer payload.
It also checks external-host identity, repeated manual requests, and real repair
and uninstall while a companion is waiting for an external host.
