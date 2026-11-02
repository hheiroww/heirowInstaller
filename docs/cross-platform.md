# Linux and macOS

The installer desktop and updater use `ProjectZ.Linux` or `ProjectZ.macOS` on
MonoGame DesktopGL. The Windows builds continue to use `ProjectZ`. All tools use
.NET 10; recipients of a self-contained build do not need the SDK or runtime.
Supported release RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`osx-x64`, and `osx-arm64`. The legacy Windows release script remains x64.

```sh
dotnet build src/HeirowInstaller.App -c Release
dotnet build src/HeirowUpdater -c Release
dotnet run --project tests/HeirowInstaller.CrossPlatform.Tests -c Release
```

An explicit `-r linux-x64` or `-r osx-arm64` also selects the correct desktop
framework and Project-Z package when cross-compiling from Windows. Build each
RID into its own output directory. `-p:InstallerDesktopPlatform=Linux` or
`-p:InstallerDesktopPlatform=macOS` selects a portable desktop build without a RID.

On Linux, a desktop session and OpenGL driver are required. File browsing uses
`zenity`; package and output paths can also be typed into the editor. macOS uses
its system file chooser. Unicode text, clipboard and fonts follow the selected
[Project-Z platform runtime](https://github.com/hheiroww/Project-Z/blob/master/docs/DESKTOPGL.md).

## Native packages

Declare the platform and, for native applications, its architecture in the signed
manifest. An omitted `targetPlatform` retains the Windows default. The engine
rejects mismatched hosts before creating installation state.

```json
{
  "formatVersion": 4,
  "id": "example.application",
  "name": "Example",
  "publisher": "Example publisher",
  "version": "1.0.0",
  "targetPlatform": "linux",
  "targetRuntime": "linux-x64",
  "entryPoint": "Example",
  "scope": "user",
  "payload": [
    { "source": "Example", "path": "Example", "unixMode": 493 }
  ],
  "shortcuts": [
    { "name": "Example", "target": "Example", "location": "startMenu", "argumentList": [] }
  ]
}
```

Supply the application's actual legal documents as described in
[legal-documents.md](legal-documents.md). Use `targetPlatform: "macos"` and an
`osx-*` runtime for a Mac. `targetRuntime` is optional for architecture-neutral
payloads. Format 4 prevents older engines from ignoring signed runtime and Unix
permission requirements. Windows packages without these new fields retain their
existing package format.

`unixMode` is a decimal permissions mask: 493 is `0755`, 420 is `0644`. Only owner
read/write/execute and group/other read/execute bits are accepted. Packing on Unix
captures these bits from the source; when packing Unix files on Windows, the
entry point and updater default to 0755 and other files to 0644. Explicitly mark
additional executable tools. Extraction, transactions, backups, repair and
signed `.hupd` updates preserve the permissions. Symlinks are rejected; stage
real files when a source application bundle contains links.

| Location | Linux | macOS |
| --- | --- | --- |
| User applications | `$XDG_DATA_HOME/Programs/<id>` or `~/.local/share/Programs/<id>` | `~/Applications/<id>` |
| User receipts | `$XDG_STATE_HOME/SocketJack/heirowInstaller/state` or `~/.local/state/SocketJack/heirowInstaller/state` | `~/Library/Application Support/SocketJack/heirowInstaller/state` |
| Machine applications | `/opt/<id>` | `/Applications/<id>` |
| Machine receipts | `/var/lib/heirowInstaller/state` | `/Library/Application Support/SocketJack/heirowInstaller/state` |

`{Home}` and `{UserData}` are available in authored installation paths. Machine
operations require root and a state namespace with root-owned ancestors without
shared write permissions. Use the CLI with `sudo`; the desktop elevation worker
continues to use Windows UAC only.

Linux shortcuts are `.desktop` entries. macOS shortcuts are local `.app` launchers
with a quoted executable and explicit working directory. Portable shortcuts use
`argumentList`, with each item passed as one literal argument. Uninstall restores
previous launcher files and preserves unowned application data.

## Build setup archives

Run PowerShell 7 on the target operating system:

```sh
pwsh -File scripts/Build-Portable.ps1 -Runtime linux-x64
pwsh -File scripts/Build-Portable.ps1 -Runtime osx-arm64
```

Each build produces a self-contained setup archive, a platform-specific `.hinst`,
and SHA-256 sums under `artifacts/portable/<version>/<rid>`. Linux starts
`heirowInstaller-Setup`; macOS starts `heirowInstaller Setup.app`. The setup loads
`Application.hinst` beside the Linux host or in the Mac bundle's Resources folder.
The Mac app receives an ad-hoc signature; Developer ID signing and notarization
for distribution require the publisher's Apple identity and a separate release.
Do not append `.hinst` data to a signed Mach-O executable.

For application installers, `pack` creates portable `.hinst` files on any host.
Linux also supports `build --runtime linux-x64 ... --output Example-Setup.run`
with a matching self-contained Linux launcher. macOS uses the separate-payload
bundle layout above. `scripts/Build.ps1` remains the Windows signed release path.

## Updates and platform boundaries

Use `executablePath: "heirowUpdater"` for a portable companion. Signed shared
runtimes and exact-base file updates use the same package verification and
receipt ownership as Windows. The portable archive builder enables automatic
updates only when both `-UpdateUrl` and `-SigningKey` are supplied. Use a dedicated
platform/architecture feed; publishing an archive does not configure a hosted feed.

The native updater prompts run on the main UI thread on Linux and macOS. Stop and
prompt cancellation use application-scoped files instead of Windows named events.
Running native application images must exit before maintenance; portable prompts
require manual closure. Windows Restart Manager plugin/resource discovery,
automatic close/force-close controls, live credential relay to an already running
observer, and Task Scheduler remain Windows integrations. Close the existing
portable updater before changing its application login. Browser approval works
for the current session; 30-day DPAPI remembered accounts remain Windows-only.

MSI, imported EXE backends, registry/environment/service modules, prerequisites,
native Windows migration, component installation roots, and affected plugin
resources are rejected for non-Windows manifests. Existing Windows installers
keep these features. Native CI tests portable installation, execution, repair,
signed updates, launchers, and removal on Windows, Linux, and macOS.
