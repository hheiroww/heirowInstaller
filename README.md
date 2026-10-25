<p align="center"><img src="assets/heirowInstaller-256.png" width="128" alt="heirowInstaller" /></p>

# heirowInstaller

Native Windows installers, signed file updates, and publishing, built with [Project-Z](https://github.com/hheiroww/Project-Z).

heirowInstaller combines a Project-Z desktop setup experience with a reusable .NET installation engine, command-line tooling, and a SocketJack.com publisher. The interface follows heirowSnap's desktop layout and SocketJack's charcoal, lime, and orange palette.

[Product and publisher](https://socketjack.com/heirowInstaller/) · [Releases](https://github.com/hheiroww/heirowInstaller/releases) · [Compatibility](docs/compatibility.md)

![heirowInstaller native desktop](assets/screenshots/desktop.png)

## Projects

| Project | Purpose |
| --- | --- |
| `HeirowInstaller.App` | Project-Z native setup wizard and package workspace |
| `HeirowInstaller.Core` | Package format, verification, installation, receipts, repair, removal, and updates |
| `HeirowInstaller.Cli` | Automation and unattended installation |
| `HeirowInstaller.Publishing` | HTTPS publisher client with credentials supplied at runtime |
| `heirowUpdater` | Shared optional updater with native Update now and Postpone prompts |
| `HeirowUpdater.SocketJack` | Reusable updater SDK using the public SocketJack networking library |

The public repository contains the installer tooling and public publisher identity. The SocketJack.com hosting application is maintained separately in its private repository.

## Build and package

Windows x64 and the .NET 10 SDK are required to build the tooling. Each released application setup is a self-contained executable; its recipient does not need heirowInstaller, the SDK, or a WiX compiler installed.

A compiled application setup opens that application's wizard, with feature selection, verified installation progress, and a final **Open application** choice. Its elevated worker has no second interface. The unbundled desktop tool provides the package-authoring workspace. See [the setup wizard](docs/app-setup-wizard.md).

When an app uses files being installed, repaired, or removed, the native interface offers **Close apps and continue** or **Cancel installation**. Closing uses the application's normal window request, allowing it to ask about unsaved work. Apps that remain open return to a responsive retry/cancel prompt. [Running-app integration](docs/running-applications.md) includes signed plugin resources and the noninteractive command-line contract.

```powershell
dotnet build heirowInstaller.sln -c Release
pwsh -File scripts/Test.ps1
pwsh -File scripts/Build.ps1
```

Build one distributable setup from published application files:

```powershell
heirowInstaller.Cli build installer.json --source .\publish --output .\dist\MyApp-Setup.exe
```

Use `--project .\MyApp\MyApp.csproj` instead of `--source` to publish a .NET application's self-contained runtime before packaging. Add `--launcher` when the published launcher is outside the builder's directory. See [application authoring](docs/authoring.md) for optional updates, compiler options, and prerequisites.

The included feature-selection example also supports separate package operations:

```powershell
heirowInstaller.Cli pack examples/hello/installer.json --source examples/hello/payload --output hello.hinst
heirowInstaller.Cli verify hello.hinst
heirowInstaller.Cli install hello.hinst
heirowInstaller.Cli repair heirowinstaller.hello
heirowInstaller.Cli uninstall heirowinstaller.hello
```

`bundle` combines a package with the published Project-Z launcher to produce one setup EXE. `import-msi` and `import-exe` preserve an existing installer's backend. `compile` builds existing `.wxs` and `.wixproj` sources through an explicitly selected official WiX compiler or SDK, including extensions, custom actions, merge modules, patches, and Burn chains. WiX is an optional build-time dependency; its selected version retains its own license and acceptance requirements. See [compatibility](docs/compatibility.md), [desktop UI](docs/ui.md), and each executable's `--help` for the complete command contract.

MSI repair and uninstall reuse the recorded directory when it was explicitly selected. Repair uses verified cached MSI media; uninstall targets the original ProductCode. Installations that kept the MSI's authored destination retain that behavior during maintenance. Native migration requires a recorded explicit destination and an exact signed baseline, so fallback paths are never adopted.

For a signed release, add `--sign-key C:\Keys\publisher.private.pem` to `pack`. Keep the private key outside the payload directory. The desktop builder also accepts its path through `HEIROWINSTALLER_SIGNING_KEY_PATH`; the path is never saved in project or desktop settings. SocketJack.com publication requires an approved publisher signature and an authorized account.

## Application updates

Selecting an application's optional updates feature generates a small `heirowUpdater.json` at its root and provisions one immutable updater runtime in a shared directory for its installation scope. Applications using the same runtime reuse its bytes. The companion opens with the app and waits until all its instances exit before checking. A newer release opens a native Project-Z prompt offering **Update now** or **Postpone 24 hours**. Manual checks show progress and a visible result. Choosing **Update now** asks before closing a running app or plugin host and allows cancellation before replacing files. Leaving the feature off creates no application profile or runtime reference.

```csharp
SocketJack.HeirowUpdaterBootstrap.StartAfterExit();
SocketJack.HeirowUpdaterBootstrap.CheckForUpdates();
```

Dependency-free C# and VB bootstraps are included in `templates/bootstrap`. The reusable SDK exposes `ApplicationUpdater`; see [application integration](docs/app-updates.md) for plugin hosts, protected feeds, child-process credentials, and status queries.

heirowUpdater uses **SocketJack.Net.HttpClient** from the published `SocketJack` 2026.10.24 package for release checks and downloads. It validates redirects, downloaded SHA-256, and the publisher pin recorded at installation. New native packages use compressed `.hupd` updates containing only changed files and signed old/new metadata. The trusted engine applies each owned file, preserves unchanged files, and reconstructs its repair package locally. It never downloads a Setup executable as a file-mode fallback. Existing MSI/executable adapters retain their original protocol until their ownership is migrated. See [signed file updates and interrupted-work cleanup](docs/incremental-updates.md).

The setup provisions its updater automatically; users do not install the authoring tool. The common maintenance executable is also pooled without retaining a separate full Setup per application. Older app-local profiles remain readable for migration.

Advanced command-line and explicitly scheduled checks remain available:

```powershell
heirowUpdater check --all
heirowUpdater update --all
heirowUpdater enable --interval-minutes 60
heirowUpdater enable --interval-minutes 60 --apply
heirowUpdater status
heirowUpdater disable
```

`enable` schedules per-user checks at sign-in and periodically. `--apply` opts into installation of updates with a pinned publisher key. Machine-wide updates can require an administrator handoff; background checks report that required action. Applications can also supply a feed/profile directly without an installation receipt.

The desktop Updates page exposes these companion operations. Apps pass their current login through an immutable in-memory snapshot captured before background work. Login changes replace the observer's credentials and sign-out clears them. Credentials stay outside manifests and source control and are sent only to the signed HTTPS origin. Registered SocketJack applications can request browser approval when their current session is rejected.

The companion's published single-file host discovery is fixed in 2026.10.24.3. To upgrade heirowInstaller itself from .1 or .2, use **Installed apps → Update** in the desktop app or download the latest Setup once.

## Publish

The SocketJack.com publisher accepts immutable standalone `.exe` installers and `.hinst` packages through bounded, hash-verified chunks. Standalone publication verifies the approved launcher and signed embedded payload. Sign in from the desktop publisher with an authorized account using browser approval, or use the CLI with a credential environment variable:

```powershell
heirowInstaller.Cli publish MyApp-Setup.exe --publisher https://socketjack.com --cookie-env SOCKETJACK_PUBLISH_COOKIE
```

Supply the Cookie header through that environment variable; never put its value in a command, manifest, or repository. New publisher releases produce an update feed and a download receipt.

## Publisher identity

The [SocketJack publisher profile](publishers/socketjack.com.json) identifies its HTTPS endpoints and RSA public-key fingerprint. Signing keys and account credentials are stored outside source control. A package signature establishes publisher identity only when its key fingerprint is checked against a trusted source. This is separate from Windows Authenticode signing.

## License

MIT for heirowInstaller source. Bundled frameworks and imported product installers retain their respective licenses and notices. The new heirowInstaller logo is included in `assets/`.
