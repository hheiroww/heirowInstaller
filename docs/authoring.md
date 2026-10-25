# Application installer authoring

A distributed application setup can be one `.exe` containing its installer runtime and verified payload. The recipient does not need an installed copy of heirowInstaller, the .NET SDK, or the WiX compiler. The application's own native prerequisites still need to be embedded or authored explicitly.

The application entry point is protected by a running-application consent step. Declare exact `affectedResources` for external plugin binaries so their hosts can be listed safely before maintenance; see [running applications](running-applications.md) for authoring and SDK callbacks.

Use the portable builder's `heirowInstaller.Cli.exe` and `heirowInstaller.exe` together, or pass the published launcher explicitly. The builder checks the launcher's .NET single-file manifest and embedded runtime; a framework-dependent apphost is rejected.

```powershell
heirowInstaller.Cli.exe build installer.json --source .\publish --output .\dist\MyApp-Setup.exe
```

`--source` takes complete published application files, including their native dependencies. For a .NET application project, the builder can publish its application runtime before packaging:

```powershell
heirowInstaller.Cli.exe build installer.json --project .\MyApp\MyApp.csproj `
  --runtime win-x64 --output .\dist\MyApp-Setup.exe `
  --launcher .\tools\heirowInstaller.exe --sign-key $env:APPLICATION_SIGNING_KEY
```

The project route runs `dotnet publish --self-contained true`, bundles managed/native runtime content, then includes the complete output in the package. `--framework` selects a target in a multi-targeted project. The native folder route also works with applications built outside .NET; include every file needed by that application. The setup launcher does not install heirowInstaller as a separate product.

`scripts/New-Installer.ps1` exposes these commands with `-SourceDirectory` or `-ApplicationProject`. A `.hinst` output remains useful for managed distribution; an `.exe` is the standalone recipient-facing setup. `pack` and `bundle` remain available separately.

## Existing WiX source and extensions

The explicit build-time compiler backend delegates original WiX authoring to the selected official WiX compiler or project SDK. It preserves the actual extension, localization, custom-action, merge-module, patch, and Burn build pipeline. It does not claim an independent reimplementation of the WiX source language.

```powershell
heirowInstaller.Cli.exe compile .\Package.wxs --output .\dist\Product.msi `
  --wix .\tools\wix.exe --arch x64 `
  --extension .\tools\WixToolset.Util.wixext.dll `
  --define ProductVersion=1.2.3 --bind-path .\publish

heirowInstaller.Cli.exe compile .\Setup.wixproj --output .\dist\Product.msi `
  --compiler-arg -m:1 --compiler-arg -nr:false

heirowInstaller.Cli.exe compile .\Module.wxs --output .\dist\Common.msm --wix .\tools\wix.exe
heirowInstaller.Cli.exe compile .\Patch.wxs --output .\dist\Update.msp --wix .\tools\wix.exe
```

`.wxs` options include repeated `--source-file`, `--extension`, `--define Name=Value`, `--bind-path`, `--include`, `--localization`, and `--compiler-arg`. Additional arguments are passed separately without shell parsing. These can select WiX's target/update bind paths, cabinets, signing hooks, or other supported compiler options. Compiler failure preserves an existing output artifact.

`.wixproj` builds use `dotnet build` and the project's declared `WixToolset.Sdk` version. Keep its sources, `PackageReference` extensions, bind paths, localization, and custom-action project references in the project. `--compiler-arg` forwards MSBuild properties and targets. `--msbuild C:\...\MSBuild.exe` selects full-framework MSBuild for an existing legacy project. Source files are not rewritten or automatically converted between WiX versions.

Output extensions select real `.msi`, `.msm`, `.msp`, `.wixlib`, or authored Burn `.exe` artifacts. An MSM is a merge module, intended for a consuming MSI. An MSP needs matching baseline/update MSI authoring and an installed baseline product. A Burn chain can still download external packages if its original authoring requests downloads; author embedded payloads when an offline setup is required. See the official [compiler reference](https://docs.firegiant.com/wix/tools/wixexe/), [project SDK](https://docs.firegiant.com/wix/tools/msbuild/), [extensions](https://docs.firegiant.com/wix/tools/wixext/), and [patch authoring](https://docs.firegiant.com/wix/tools/patches/).

WiX is optional and used only while building. No WiX executable or NuGet SDK is copied into heirowInstaller or an app merely by enabling this backend. The selected tool must already be installed, and any compiled custom-action binaries keep their own redistribution terms.

## WiX licensing and version selection

heirowInstaller source remains MIT. WiX source is MS-RL; the WiX project explains that generated installers are normally outside the MS-RL merely from being generated. The acceptance fixtures use the explicitly pinned official `wix` and `WixToolset.Sdk` **5.0.2** releases. That establishes tested backend behavior, rather than silently changing an author's selected compiler. [WiX licensing](https://docs.firegiant.com/wix/), [official 5.0.2 package](https://www.nuget.org/packages/wix/5.0.2).

WiX v6 introduced the Open Source Maintenance Fee; current v7 also requires explicit EULA acceptance. The published terms require organizations exceeding $10,000 annual revenue, as defined there, to satisfy the maintenance fee. heirowInstaller does not accept an EULA or determine sponsorship status on the author's behalf. A user selecting v7 supplies their applicable acceptance through their project or an explicit compiler argument. FireGiant commercial extensions can have separate license-file requirements. [Maintenance fee and EULA](https://docs.firegiant.com/wix/osmf/), [commercial build-tool licensing](https://docs.firegiant.com/heatwave/build-tools/firegiant-licensing/).

For a local pinned 5.0.2 test tool, the .NET SDK can install into a project-private tools directory:

```powershell
dotnet tool install wix --tool-path .\tools\wix5 --version 5.0.2 --allow-roll-forward
```

Do not commit signing keys, package caches, generated installers, or local compiler output. The builder rejects its explicitly supplied private signing-key path when source collection would include it. It does not guess which unrelated application data files are secrets.

## Optional shared updater

The signed installer carries the optional companion, then provisions it once in its scope's shared runtime directory. A selected package generates a small `heirowUpdater.json` at the application root containing its verified identity, absolute root/state directory, relative entry point, features, publisher pin, feed and shared runtime content binding. Credential settings contain environment-variable names or a registered auth application ID; they contain no tokens or cookies.

```json
{
  "id": "example.app",
  "name": "Example App",
  "version": "1.2.3",
  "publisher": "Example Publisher",
  "entryPoint": "App/Example.exe",
  "updateUrl": "https://example.com/app/update.json",
  "features": [{ "id": "updates", "name": "Application updates", "default": false }],
  "updateCompanion": {
    "feature": "updates",
    "executablePath": "heirowUpdater.exe",
    "configurationPath": "heirowUpdater.json",
    "mode": "after-exit",
    "runtimeMode": "shared",
    "updateMode": "files"
  },
  "payload": []
}
```

```powershell
heirowInstaller.Cli.exe build installer.json --source .\publish `
  --updater .\tools\heirowUpdater.exe --sign-key $env:APPLICATION_SIGNING_KEY `
  --output .\dist\Example-Setup.exe
```

`--updater` injects the published self-contained companion and assigns it to the optional feature. `--updater-default` selects it by default while preserving the user's ability to deselect it. Existing declared payloads must assign the updater executable to the same feature. Generated configuration is owned transactionally: repair regenerates it, feature removal/uninstall removes it, and an original pre-existing file is restored.

Native-package main-application shortcuts and explicit post-install launch resolve the shared executable and pass `launch --config <app-root-config>`. The companion starts with the app and waits for its actual processes to exit before checking. Runtime prompts offer Update now or Postpone; the bootstrap also covers direct app launches. No global authoring tool is required. Before maintenance, the engine signals that application's stop event and waits for its own observer lease, preserving other applications using the same runtime. New builders select shared mode by default and file mode for native payloads; format 2 makes these semantics explicit. Opaque MSI/EXE backends must retain `updateMode: "legacy"` until their ownership is migrated.

For MSI/EXE adapters, original application files and MSI features remain owned by their original installer. The outer updater feature must not be passed as a fabricated MSI `ADDLOCAL` feature. Author the exact install directory/property, or resolve an existing MSI's reported install location; an unknown backend destination is rejected before an updater can be copied. The relative entry point may name an application installed inside `App/` by that original backend.

When the updater is selected, Windows Settings uses the cached heirowInstaller maintenance executable for the complete product. The engine snapshots and hides only the original backend's exact uninstall registration, then restores its visibility when the updater is deselected. Repair and updates maintain that ownership if the backend rewrites its registration. MSI identity comes from its verified database's ProductCode. An EXE backend must explicitly author its uninstall identity; unknown identities cannot select the updater:

```json
"maintenanceRegistration": {
  "hive": "user",
  "view": "registry64",
  "uninstallKey": "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Example_is1"
}
```

Put this object inside `legacyExecutable`. Alternatively, author `bundleUpgradeCode` with the fixed Burn UpgradeCode instead of `uninstallKey`; it must match exactly one installed bundle in the specified hive and view. The hive must match the installation scope. The original backend's cached uninstaller is retained. A backend with a fixed directory and no authored `installDirectoryArgument` cannot accept a different target for its companion.

An MSI's application installation context and its uninstall registration can differ on a Windows configuration. If the exact registration is in HKLM, managing the optional companion requires machine maintenance scope with protected state, even when the MSI reports a per-user application. A user receipt is never permitted to restore a machine registry snapshot.

Directly running an original backend uninstaller can bypass the Settings entry. Staged helpers recheck the original application and MSI registration under the installer lock and refuse to reinstall an externally removed app. Run `uninstall <product-id>` through its cached maintenance host to remove stale companion files and receipts after such an external removal.

Original MSI shortcuts remain authored by the MSI. Integrate the lifecycle SDK or bootstrap with that application's normal startup to cover those shortcuts and direct launches. An external installer manages its own transaction: if it succeeds and a later companion step fails, the original application can remain installed. Its durable verified installation media is retained for Windows Installer repair; the outer engine rolls back only its own companion changes.

`scripts/Convert-PublishedInstallers.ps1 -Updater <published-heirowUpdater.exe>` applies the same companion contract to an audited installer inventory. Each product must name its actual `installDirectory`, `entryPoint` and feed; an EXE also supplies the exact `maintenanceRegistration` described above. The converter copies only the backend and companion into its package source, preserves the verified original digest, and emits a signed standalone setup. Product release pipelines must publish the new setup and advance its feed whenever the app version changes. Preserve any original MSI endpoints still required by installed older clients.

## Reproducible acceptance

`scripts/Test-WixAuthoring.ps1` builds real `.wxs` MSI/MSM/MSP and `.wixproj` outputs through the selected official tools. It uses unique per-user products, constrains every component below its fixture root, then exercises install, a merged module, an authored custom action, a WiX Util extension action, repair, patch application, and uninstall. Generated artifacts and logs stay under ignored `artifacts/wix-authoring/`.

The Core suite covers native rollback, signatures, path safety, machine-state protections, and app-local updater profile ownership. Published standalone installer and updater lifecycle acceptance exercise the actual self-contained release binaries separately.
