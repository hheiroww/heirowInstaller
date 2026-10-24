<p align="center"><img src="assets/heirowInstaller-256.png" width="128" alt="heirowInstaller" /></p>

# heirowInstaller

Native Windows installers, full-package updates, and publishing, built with [Project-Z](https://github.com/hheiroww/Project-Z).

heirowInstaller combines a Project-Z desktop setup experience with a reusable .NET installation engine, command-line tooling, and a SocketJack.com publisher. The interface follows heirowSnap's desktop layout and SocketJack's charcoal, lime, and orange palette.

[Product and publisher](https://socketjack.com/heirowInstaller/) · [Releases](https://github.com/hheiroww/heirowInstaller/releases) · [Compatibility](docs/compatibility.md)

## Projects

| Project | Purpose |
| --- | --- |
| `HeirowInstaller.App` | Project-Z native setup wizard and package workspace |
| `HeirowInstaller.Core` | Package format, verification, installation, receipts, repair, removal, and updates |
| `HeirowInstaller.Cli` | Automation and unattended installation |
| `HeirowInstaller.Publishing` | HTTPS publisher client with credentials supplied at runtime |

The public repository contains the installer tooling and public publisher identity. The SocketJack.com hosting application is maintained separately in its private repository.

## Publisher identity

The [SocketJack publisher profile](publishers/socketjack.com.json) identifies its HTTPS endpoints and RSA public-key fingerprint. Signing keys and account credentials are stored outside source control. A package signature establishes publisher identity only when its key fingerprint is checked against a trusted source. This is separate from Windows Authenticode signing.

## License

MIT for heirowInstaller source. Bundled frameworks and imported product installers retain their respective licenses and notices. The new heirowInstaller logo is included in `assets/`.
