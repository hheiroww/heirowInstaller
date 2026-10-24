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
- **Build & publish** builds a verified `.hinst` from a manifest and application
  files and publishes through the shared SocketJack publisher client. Publisher
  credentials come from `SOCKETJACK_PUBLISHER_TOKEN` or `SOCKETJACK_PUBLISHER_COOKIE`; they are not saved in the
  settings or manifests. The publisher client owns upload limits and transport.
- **Updates** runs the separate `heirowUpdater.exe` companion and displays its
  actual results. The complete release places that companion beside the desktop
  executable. Its native controls check or update all products, enable or disable
  real per-user hourly Windows Task Scheduler checks, inspect schedule status,
  and opt into automatic updates that satisfy the updater's signature policy.
- **Activity** shows current-session operations and errors. **Settings** saves
  output-folder, launch-after-install and Windows registration preferences under
  the user's local application data directory.

## Launch and maintenance

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
`--wait-pid` with `--wait-start` waits for the exact previous process identity.
The original FruityLoopsCollab wait/defer contract and explicit Inno directory/log
arguments are supported by compatible adapter packages. Self-updates copy the
currently installed trusted helper outside the application's folder, verify the
downloaded package and publisher key, install its signed payload as data, and
relaunch the updated entry point. The downloaded EXE prefix never runs.

For framework development only, pass `-p:UseLocalProjectZ=true` and, if needed,
`-p:ProjectZRoot=...`. Release builds use the pinned public package.

## Native acceptance

```powershell
heirowInstaller.exe --capture C:\Temp\heirowInstaller.png
heirowInstaller.exe --ui-smoke C:\Temp\heirowInstaller-acceptance
```

The smoke mode drives native controls through Project-Z's injected pointer
events. It builds and opens an isolated package, accepts a license, excludes an
optional feature, edits the target, installs, corrupts and repairs a file, tests
uninstall cancellation and confirmation, resizes the native viewport, and saves
screenshots and a JSON receipt. Its separate state root and isolated engine
options prevent Windows registry, shortcut and service changes. It does not
authenticate to a publisher or send a production update request.
