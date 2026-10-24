<p align="center"><img src="assets/heirowInstaller-256.png" width="128" alt="heirowInstaller" /></p>

# heirowInstaller

Native Windows installers, full-package updates, and publishing, built with [Project-Z](https://github.com/hheiroww/Project-Z).

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
| `heirowUpdater` | Scheduled checks, full-package updates, and a command-line companion |
| `HeirowUpdater.SocketJack` | Reusable updater SDK using the public SocketJack networking library |

The public repository contains the installer tooling and public publisher identity. The SocketJack.com hosting application is maintained separately in its private repository.

## Build and package

Windows x64 and the .NET 10 SDK are required to build. Released setup executables are self-contained.

```powershell
dotnet build heirowInstaller.sln -c Release
pwsh -File scripts/Test.ps1
pwsh -File scripts/Build.ps1
```

Create a native package from the included feature-selection example:

```powershell
heirowInstaller.Cli pack examples/hello/installer.json --source examples/hello/payload --output hello.hinst
heirowInstaller.Cli verify hello.hinst
heirowInstaller.Cli install hello.hinst
heirowInstaller.Cli repair heirowinstaller.hello
heirowInstaller.Cli uninstall heirowinstaller.hello
```

`bundle` combines a package with the published Project-Z launcher to produce one setup EXE. `import-msi` and `import-exe` preserve an existing installer's backend. See [compatibility](docs/compatibility.md), [desktop UI](docs/ui.md), and each executable's `--help` for the complete command contract.

## Application updates

heirowUpdater uses **SocketJack.Net.HttpClient** from the published `SocketJack` 2026.10.24 package for release checks and downloads. It validates every redirect, verifies the downloaded SHA-256, and pins the package publisher key recorded at installation. Signed embedded packages are installed as data by the current trusted engine; a package signature does not authenticate an arbitrary EXE prefix.

```powershell
heirowUpdater check --all
heirowUpdater update --all
heirowUpdater enable --interval-minutes 60
heirowUpdater enable --interval-minutes 60 --apply
heirowUpdater status
heirowUpdater disable
```

`enable` schedules per-user checks at sign-in and periodically. `--apply` opts into installation of updates with a pinned publisher key. Machine-wide updates can require an administrator handoff; background checks report that required action. Applications can also supply a feed/profile directly without an installation receipt.

The desktop Updates page exposes these same companion operations. Account-protected publishers can supply credential environment-variable names; values stay outside manifests and source control and are sent only to the configured HTTPS origin.

## Publish

The SocketJack.com publisher accepts immutable `.hinst` versions through bounded, hash-verified chunks. Sign in with an authorized publisher account in the browser, or use the CLI with a credential environment variable:

```powershell
heirowInstaller.Cli publish hello.hinst --publisher https://socketjack.com --cookie-env SOCKETJACK_PUBLISH_COOKIE
```

Supply the Cookie header through that environment variable; never put its value in a command, manifest, or repository. New publisher releases produce an update feed and a download receipt.

## Publisher identity

The [SocketJack publisher profile](publishers/socketjack.com.json) identifies its HTTPS endpoints and RSA public-key fingerprint. Signing keys and account credentials are stored outside source control. A package signature establishes publisher identity only when its key fingerprint is checked against a trusted source. This is separate from Windows Authenticode signing.

## License

MIT for heirowInstaller source. Bundled frameworks and imported product installers retain their respective licenses and notices. The new heirowInstaller logo is included in `assets/`.
