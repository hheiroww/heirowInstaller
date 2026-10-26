# Native desktop application

`src/HeirowInstaller.App` is a native Project-Z application. It uses the exact
published `ProjectZ` 2026.10.23.2 package, MonoGame's DX12 backend, Project-Z vector
text and native Project-Z controls. It contains no browser or WPF view. The
Windows desktop framework references satisfy Project-Z's Windows dependencies.

The SocketJack lime and orange palette, compact navigation and precise dark
surfaces follow the heirowSnap desktop application. The default client size is
1180 × 800, with a 980 × 760 minimum viewport. Graphics request 16× anti-aliasing
and 16× anisotropic filtering, with pixel-aligned vector text.

## Screens and actions

- **Install a package** verifies the payload before showing package details,
  the publisher's license when supplied, optional features, destination and
  scope, the complete operation list, progress and completion. A machine install
  relaunches through Windows UAC. Imported MSI/EXE packages with no authored
  install directory preserve the original setup's destination and scope.
- **Installed apps** lists recorded installations and runs repair, update checks
  and uninstall. Uninstall requires a native confirmation. External EXE packages
  expose only maintenance operations that their publisher authored; Windows
  Installed apps is available when no external uninstall command was supplied.
  Updating a selected machine product requests Windows UAC through the current
  trusted installer host and waits for completion. It preserves installed
  features, scope, registration and the original adapter destination. The child
  requires an existing publisher-key pin and checks the signed package, product,
  version and download digest before planning or installing it. Machine
  self-updates use the same pin requirement and release binding.
- Before installation, repair, update or uninstall changes application files, a
  native dialog lists apps using those files. **Close apps and continue** sends
  normal close requests only to the verified affected apps. Their save dialogs
  remain in control. **Cancel installation** stops the operation without
  changing application files. An app that stays open or restarts returns to the
  same dialog with another attempt available. No app is force-terminated.
  Apps that cannot safely receive a close request must be closed manually.
- **Build & publish** builds a verified `.hinst` from a manifest and application
  files and publishes through the shared SocketJack publisher client. Publisher
  credentials come from `SOCKETJACK_PUBLISHER_TOKEN` or `SOCKETJACK_PUBLISHER_COOKIE`; they are not saved in the
  settings or manifests. The publisher client owns upload limits and transport.
  To sign a native Build operation, set `HEIROWINSTALLER_SIGNING_KEY_PATH` to your
  RSA private-key PEM path before launching the desktop app. The environment
  variable is optional; the app has no default signing key and does not save
  the key or its path in desktop settings. Keep signing keys outside payload
  source folders. The package builder rejects including the active signing key
  in a package payload.
  The default output is a standalone `Setup.exe`, built from the running
  self-contained launcher and complete application files. An application project
  path can be entered instead of a folder to publish its runtime before packing.
  `.hinst` output remains available. Browser Sign in and Sign out use an
  in-memory SocketJack session; approval happens in the user's browser and no
  account credential is written to desktop preferences.
- **Updates** runs the separate `heirowUpdater.exe` companion and displays its
  actual results. The complete release places that companion beside the desktop
  executable. Its native controls check or update all products, enable or disable
  real per-user hourly Windows Task Scheduler checks, inspect schedule status,
  and opt into automatic updates that satisfy the updater's signature policy.
  Each app can also select its own optional root companion. Its normal lifecycle
  waits for the app to exit and offers native Update now or Postpone; see
  [optional application updates](app-updates.md).
- **Activity** shows current-session operations and errors. **Settings** saves
  output-folder, launch-after-install and Windows registration preferences under
  the user's local application data directory.

## Launch and maintenance

Planning, maintenance identity checks, application launching and updater verification run on worker
threads. The wizard retains its verified read-only package reader while reviewing
choices; installation independently opens and verifies the actual input again.
Migration and cleanup authorities read immutable signed manifest bytes from that
verified reader. They never take authority from the mutable presentation model.
Progress reports are coalesced into a bounded activity history and update the
existing progress controls. Artwork textures survive layout changes, and each
frame limits background callback dispatch so input and drawing keep running.

`scripts/Test-UiResponsiveness.ps1` measures a real Project-Z wizard against a
supplied signed package, including repeated planning and 100,000 progress
reports. It uses an isolated window outside the desktop, never injects global
input, and never installs the supplied application. Its receipt records update
and draw times, navigation latency, progress rebuilds and artwork decodes with
16× AA and 16× AF enabled. Output stays in a fresh local artifact directory.

```powershell
heirowInstaller.exe application.hinst
heirowInstaller.exe --install application.hinst
heirowInstaller.exe --install application.hinst --silent --target C:\Apps\Example
heirowInstaller.exe repair product-id --scope user
heirowInstaller.exe uninstall product-id --scope user
```

A bundled setup detects its appended package when no package argument is passed.
The same executable handles Windows Installed apps maintenance commands.
`--silent`, `--quiet`, `/quiet`, `/qn`, `/SILENT` and `/VERYSILENT` suppress the GUI.
`--wait-pid` with `--wait-start` identifies a previous process dependency. The
native window opens immediately and offers a visible choice. A supplied PID
alone never grants permission to close an app: Core must independently verify
that the app is using affected files. Other dependencies can only be closed
manually, checked again, or canceled. A stale creation-time identity is ignored.
Silent operations fail promptly with an actionable error while an affected app
or requested dependency remains open; they never close applications themselves.
The original FruityLoopsCollab wait/defer contract and explicit Inno directory/log
arguments are supported by compatible adapter packages. Self-updates copy the
currently installed trusted helper outside the application's folder, verify the
downloaded package and publisher key, install its signed payload as data, and
relaunch the updated entry point. The downloaded EXE prefix never runs.
Internal update handoffs use `--expected-product-id`, `--expected-version` and
`--expected-sha256` to bind the child to the checked release. Product and version
must be supplied together. `--no-register` preserves a product that was installed
without a separate heirowInstaller Windows registration.
Trusted native self-update and administrator handoffs keep the interactive app
closure dialog available. They return success only after installation completes;
Cancel returns Windows installer cancellation code 1602. Isolated self-update
helpers stay beneath their supplied state root.

For framework development only, pass `-p:UseLocalProjectZ=true` and, if needed,
`-p:ProjectZRoot=...`. Release builds use the pinned public package.

## Native acceptance

```powershell
heirowInstaller.exe --capture C:\Temp\heirowInstaller.png
./scripts/Test-NativeDesktop.ps1 -LauncherPath C:\Build\heirowInstaller.exe
```

The smoke mode drives native controls through Project-Z's injected pointer
events. It records 96 presented frames: the first four consecutive frames during
cold and repeated visits to all six tabs, using both the normal fixed-step loop
and one native update per presentation. The latter prevents readback-induced
catch-up ticks from concealing an empty transition frame. Every frame must keep
the sidebar and header visible. Page replacement happens before the native layout tick,
so a new page is registered and laid out before it is drawn. The scene builds
the replacement tree before disposing the old page. It builds and opens an isolated package, accepts a license, excludes an
optional feature, edits the target, installs, corrupts and repairs a file, tests
uninstall cancellation and confirmation, resizes the native viewport, and saves
screenshots and a JSON receipt. The runner builds a dedicated Windows Forms
acceptance app, supplies it through `HEIROWINSTALLER_SMOKE_APPLICATION`, and tests
the real native close-apps dialog. Cancel preserves the running app, payload and
receipt; Close requests a normal exit before repair; a simulated save dialog
keeps the app open, blocks changes, returns to Retry, and still permits Cancel.
The fixture is excluded from production packages. Its separate state root and isolated engine
options prevent Windows registry, shortcut and service changes. It does not
authenticate to a publisher or send a production update request.
