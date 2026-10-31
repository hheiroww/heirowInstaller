# Application setup wizard

A bundled setup executable, or a desktop invocation bound to a package, opens a wizard for that application. The verified manifest supplies its name, version, publisher, description and optional payload artwork. It contains welcome, feature selection, destination, review, installation progress and completion steps. Other installed apps, build/publish tools, publisher sign-in and global update controls are not part of this flow.

Launching heirowInstaller without a package retains the authoring workspace. Opening a package from that workspace retains its existing navigation.

The workspace's updater actions use the SDK's verified shared/app-local resolver. Missing or invalid updater configuration makes the actions unavailable; an invalid shared profile never falls back to an arbitrary sibling executable. App-root and shared-runtime signature/hash checks stay in the SDK.

Explicit repair/uninstall invocations, including a cached app host, open an app-specific **Review → Progress → Finish** wizard. It binds the receipt, scope, version, install root, publisher pin and cached-package digest before review, then rejects a changed receipt/package before maintenance begins. Repair offers the same optional Open-app choice; uninstall hides it. Choosing an installed app from the authoring workspace retains that workspace.

Maintenance accepts the existing per-product cached GUI or an exact content-bound GUI prefix validated by `SharedInstallerHost.Validate`. A pooled raw GUI has no app package overlay; the selected receipt and signed cached package still bind the maintenance operation to one application.

The completion page has a **Finish** button and an **Open application** choice when the installed entry point exists. The original wizard launches only its receipt-bound installed application after successful installation. When configured, it uses the validated app-root updater profile and the SDK's installed/shared updater resolver. The elevated worker never launches the application.

## Administrator access

The original wizard stays open at its existing integrity level. Windows displays its normal human UAC prompt for the same setup image. The child enters an internal worker route before DPI setup, WinForms dialogs or Project-Z window construction; it creates no native installer window. Elevated repair, uninstall and desktop updates use the same route.

The worker independently reopens the unchanged package, verifies its digest, application/version and required publisher pin, replans it, and verifies the reviewed install/state roots before calling the existing Core engine. MSI execution is silent behind the wizard. Legacy executable packages need an explicitly authored `silentInstallArguments` contract; a bound wizard rejects packages that would fall back to another interactive installer.

A bundled worker also checks its own verified footer. It accepts only that product and publisher pin, including newer releases signed by the same publisher. An unsigned bundled host accepts only its own unchanged package. The raw authoring application remains able to install other products. Completion is checked against the engine's committed receipt and cached package; a signed MSI's authored default location may resolve after Windows Installer finishes.

Progress, results and close-application requests return through a random, single-instance named pipe. Its protected ACL allows only the invoking Windows user and denies network/anonymous callers. The endpoints check the actual peer process, creation time, exact executable path and image digest, plus a random per-operation nonce. The explicit user-SID ACL intentionally supports the same account across medium/high integrity; a different administrator account is not granted access. The client uses anonymous impersonation SQOS, and neither side impersonates the other.

Messages have a 256 KiB bound and a fixed typed command set. Install options use an explicit allowlist; no ambient environment values, publisher/session credentials or Core transaction/recovery contexts are read into the channel. Explicit backend arguments retain their existing contract. No PowerShell helper, service, scheduled task or security-setting change is involved. Windows documents the [pipe ACL/access model](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights) and [named-pipe impersonation levels](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client).

The original wizard shows **Close apps and continue**, **Check again**, **Cancel**, and, for eligible owned images, **Force close listed apps**. Consent requests use Core's bounded verified affected-process discovery. Graceful closure lets save dialogs refuse and returns to a responsive prompt within a bounded attempt. Force requires a second unsaved-work confirmation, stays bound to the exact displayed PID/start/path set, and revalidates each original process object before terminating that one process. It never targets plugin hosts, wait-only dependencies, services, unrelated processes or descendants. Check again remains active while waiting; Cancel/disconnect promptly signals the existing engine cancellation token and never grants termination authority. The engine's transaction and backend cancellation boundaries remain in effect.

## Developer acceptance

Run `scripts/Test-AppWizard.ps1 -LauncherPath <new-single-file-App.exe> -CliPath <verified-CLI.exe>`. It builds a benign application probe and a new compiled setup in a fresh ignored artifact directory, then runs both Finish choices plus repair/uninstall with isolated state and no OS registration. The fixture renders offscreen without activation, uses internal scene pointer events, and sends no input to the user's windows. It checks app-only controls/artwork/title, stage frames, real file-work progress, live worker window count, wrong nonce/digest/install root/state root/parent identity and oversized-message rejection, Cancel/no mutation, save refusal, graceful close, optional-file selection and actual installed-app launch. Maintenance also rejects a changed fixture-only cached package before starting a worker. Cleanup uses the normal isolated uninstall command; no real installed application is changed.

The isolated worker tests deliberately run without elevation. They do not establish cross-UAC runtime acceptance. That final boundary requires a human-approved normal Windows UAC run of the same compiled setup, checking one visible wizard, continued progress and original-window close/save choices. Security prompts must not be automated.

Signed fixture cases generate disposable RSA objects in memory without exporting a private PEM, verify a newer same-product/same-publisher package, and reject different-product and swapped-publisher packages before creating install/state directories. They never use a production signing key.

`scripts/Test-NativeDesktop.ps1` retains the toolkit's six-tab/96-frame black-flash regression. Its byte-identical host copy and owned app probes are nonactivating/offscreen, and a malformed fixture-root updater profile verifies the new shared resolver fails closed before any global update command can run. No user preference or real updater profile is rewritten.
