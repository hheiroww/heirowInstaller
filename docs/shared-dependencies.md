# Shared application dependencies

Format 5 adds signed, immutable dependency packages to native application installs.
Every selected dependency downloads and verifies during setup, before the target
application closes and before **Finish** becomes available. First use does not
silently download a missing feature. Older package formats retain their original
payload and update contracts.

```json
{
  "formatVersion": 5,
  "targetRuntime": "win-x64",
  "updateUrl": "https://publisher.example/app/update.json",
  "sharedPackages": [
    {
      "id": "Publisher.Runtime",
      "version": "1.0.0",
      "targetRuntime": "win-x64",
      "downloadUrl": "https://publisher.example/runtime/1.0.0.hinst",
      "sha256": "EXACT_ARCHIVE_SHA256",
      "size": 123456,
      "publisherKeySha256": "EXACT_APPLICATION_PUBLISHER_SHA256"
    }
  ]
}
```

The parent package requires a signature. Each dependency is a normal signed
`.hinst` ZIP containing only its manifest, signature and declared payload files.
Its ID, version and runtime must match the parent declaration. The publisher pin
must match the application's signer. Dependency packages cannot execute registry,
service, prerequisite or extension operations, redirect application installation
roots, or request nested dependencies. Their payload is scoped by the parent;
the same inert archive can be reused for user and machine installation scopes.

Downloads and redirects stay on the application's signed update origin (or its
signed homepage origin when no update feed exists). Production downloads use
HTTPS. An explicitly supplied `InstallOptions.SharedPackageTransport` supports a
credential-aware caller; it never bypasses origin, size, signature or content
checks. HTTP is available only for isolated loopback acceptance.

Windows stores dependencies in `%LOCALAPPDATA%/SocketJack/Shared` for user installs
and `%ProgramFiles%/SocketJack/Shared` for machine installs. The machine namespace
requires protected administrator/SYSTEM ownership and ACLs. Files live under
`packages/<publisher-prefix>/<archive-sha256>/files`; full publisher and package
identities are still checked. The shorter content path avoids repeating long
package names in Windows extraction paths. Dependency provisioning is serialized
per archive. Downloads and extraction check free space and report progress.

Installed applications receive a generated `.socketjack-shared.json` containing
their signed manifest digest and exact dependency locations. The scope-wide
reference has the same bytes. Both activate in the application's file transaction
and roll back together. These files are derived metadata, not independent trust
authority: launchers must bind their contents to the signed installed package
before selecting executable or DLL paths. Immutable dependency files are never
overwritten; another application can retain an older version while an update
selects a new one.

## Downloaded updater hosts

A small standalone updater host can also live in an optional dependency instead
of being embedded in every application's package:

```json
{
  "updateCompanion": {
    "feature": "updates",
    "runtimeMode": "shared",
    "updateMode": "files",
    "updateProtocol": "hfiles1",
    "sharedPackageId": "Publisher.UpdaterHost",
    "executablePath": "heirowUpdater.exe",
    "configurationPath": "heirowUpdater.json",
    "mode": "after-exit"
  }
}
```

The referenced dependency must belong to the same optional feature. Its signed
inner payload supplies the exact host hash and size; the host's numeric file
version supplies the existing shared-updater version binding. The parent payload
must not contain a second executable at that path. The builder selects `hfiles1`
when `sharedPackageId` is supplied; omitted legacy protocols retain their original
behavior. Generated profiles cannot override the signed protocol.

Provision only a standalone host here. A framework-dependent apphost copied
without its DLL, runtime configuration and dependencies cannot run. The setup
NativeAOT bootstrap provisions the maintenance host and its separate canonical
.NET runtime. The parent application's signed dependency list also declares that
exact closure under the updater feature, so selecting updates provisions every
required archive during setup. Installed updater bootstraps are signed
`offlineOnly: true`: they verify already installed host/runtime bytes and fail
with a repair instruction if anything is missing. General managed dependencies need `.deps.json`-
compatible probing layouts and a private .NET host configured before managed
`Main`; changing the global `PATH` or scanning arbitrary DLL folders is not a
substitute. Preserve isolated renderer/plugin load contexts and shared contract
assemblies when adapting applications.

## Framework-dependent application hosts

`applicationLaunch` binds the actual application-owned apphost, runtime
configuration and exact probing packages in the signed parent manifest:

```json
{
  "applicationLaunch": {
    "executablePath": "App/Example.App.exe",
    "runtimeConfigPath": "App/Example.runtimeconfig.json",
    "runtimePackageId": "socketjack.dotnet.desktop.10",
    "probingPackageIds": ["socketjack.dotnet.desktop.10", "nuget.Example.Library"]
  }
}
```

The original entry filename becomes a small signed native launcher. It verifies
the declared application files and already installed dependency archives, selects
the private `DOTNET_ROOT` for its child process, and launches the distinct renamed
apphost. Setup generates the owned `runtimeconfig.dev.json` probing roots before
managed startup. A missing dependency causes a repair message; application launch
does not download files. The actual child image is an explicit running-application
closure target. An application's signed `requiresAdministrator` is separate from
its setup scope and prerequisite elevation requirements.

Application/updater cache locations are hints, not executable authority: every
selected archive and extracted file must still match the signed descriptor.
Launch can read a complete matching user or machine cache without provisioning
or changing it. App probing paths must match the selected verified cache. Machine
application launches do not by themselves request elevation; setup scope,
prerequisites and the actual application's signed privilege requirement remain
separate decisions.

Resource consumers can call `SharedPackageResources.ResolveFileAsync` or
`ResolveDirectoryAsync` with their product ID, package ID and trusted publisher
pin. Specify the expected install root/scope when more than one installation can
exist. These APIs derive paths from signed installed package authority and verify
archive identity and requested file bytes; they do not trust writable binding
paths or fetch missing resources. Legacy installs with no declared package return
null so an existing local provider can remain compatible. Keep configured models,
user data, plugin contracts and isolated renderer contexts in their existing
providers until an explicit adapter supports the shared layout.

## Authoring and size acceptance

`scripts/Build-SharedApplication.ps1` accepts a reviewed loose,
framework-dependent native layout and an existing signed maintenance package.
Prefer `-RuntimePackagePath` and `-RuntimePackageUrl` referencing the same canonical
runtime archive for every compatible product. Alternatively provide the exact
redistributable `-DotNetRuntimeDirectory` and `-DotNetRuntimeVersion`; the script
copies only the standard runtime layout, not SDKs. All wrappers should share one
explicit task-owned `-DependencyCacheDirectory`.

Build the maintenance package first with `Build-SharedToolkit.ps1`, supplying
`-CliPath`, `-RuntimePackagePath`, `-RuntimePackageUrl`, and the established
`-SigningKey`. The new recipe defaults `-ProjectZPackageVersion` to exact
`[2026.11.4.1]`; every publish receives that same override. Its three native
apphosts remain framework-dependent and share ordinary maintenance DLLs once.
It rejects bundled private .NET runtime files. The signed runtime archive stays
separate. `Build-WebInstaller.ps1` receives the same runtime path/URL and binds
both packages before opening the GUI with process-local `DOTNET_ROOT`.

A native-only launcher patch can reuse those exact signed maintenance and runtime
archives. Pass an explicit native `-Version` to `Build-Bootstrap.ps1`; this does
not change the managed projects' version or rebuild them. `-CompilerDirectory`
selects an existing absolute task-owned AOT cache while `-OutputDirectory` selects
fresh launcher output. Existing generic and bound output files are preserved.
When binding a descriptor, `-GenericLauncherPath` and `-CliPath` reuse verified
launcher and CLI files without a source CLI/Core build. The CLI path must be an
existing absolute executable or command script; failure never falls back to a
source build. Record and verify these input files' hashes outside source control.

Native PE version, descriptor version and each package version are independent
bindings: a `2026.11.5.1` standalone entry can select maintenance `2026.11.4.1`
and its unchanged private runtime. Preserve each archive's original signed
identity, hash and download URL. Native-only releases use a dedicated tag such as
`bootstrap-v2026.11.5.1`; the ordinary `v*` workflow builds the full installer.
The source offer must distinguish the new native source revision/build version
from the reused maintenance revision and include their matching public notices.

For a .NET 10 app, the common recipe reuses its exact main runtime archive for
maintenance. For an app on another runtime family, supply the separate
`-MaintenanceRuntimePackagePath` and `-MaintenanceRuntimePackageUrl`; its own
`-RuntimePackagePath` still selects its application runtime. Compatible roles
collapse only when their archive ID, version and hash agree. Maintenance URLs
included in the signed app dependency list must use its signed update origin.

`scripts/Build-DotNetPackage.ps1` prepares a canonical runtime archive. It invokes
`Prepare-DotNetRuntime.ps1`, reads the exact patch from Microsoft's official
`release-metadata/<major>.0/releases.json`, verifies each Core/Desktop ZIP with
the metadata's SHA-512, and merges only `dotnet.exe`, `host/fxr`, the matching
`shared` framework folders and notices into one source directory. Apphost packs
and SDKs are excluded. The signed package ID is
`socketjack.dotnet.desktop.<major>` with the exact patch version and target RID.
Pass the expected publisher key pin explicitly. Existing archives are verified
and reused; the source/metadata/download receipts stay outside the payload. The
scripts retain outputs, require 2 GiB free and never install a global runtime or
delete staging files. The source receipt also records the official global Desktop
installer URL/SHA-512 for native plugin prerequisite authoring without downloading
an extra installer by default.

The verified authoring cache keys a canonical inventory and publisher. Rebuilding
identical files with a new RSA-PSS signature would produce a different archive
hash, so cached archives are reused only after their signature, identity and all
payload hashes have been checked. Cache reuse across different products preserves
the same archive hash and installed store directory.

Only explicitly allowed public NuGet packages, with public NuGet provenance and
bytes matching the dependency lock/cache, move into genuine `.deps.json` probing
layouts. Private overrides stay with the application.
Authoring uses the selected .NET SDK's NuGet verifier to bind the package's
signature-aware content hash to `.deps.json`, validate signed archive integrity,
and compare expanded assets with their original ZIP entries. This follows the
[NuGet metadata content-hash contract](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File);
the raw signed ZIP digest can differ from that content hash.
Native NuGet assets remain app-local until a reviewed native/renderer/plugin
provider supports relocation; normal managed probing alone does not establish
compatibility with absolute native load paths. Resource partitions must
declare `public` or `protected` visibility; publication tooling must retain that
access policy. Matching duplicate resource aliases can share a pack; differing
bytes remain separate. Embedded prerequisites require a pinned HTTPS download map
and keep their detection/elevation metadata. A native plugin host may still need
global .NET/Desktop and VC prerequisites even when its standalone app uses a
private runtime.

The final web setup enforces less than 20,000,000 bytes for the setup executable
and less than 100,000,000 bytes for the compressed app payload. The generated size
report lists shared downloads separately. These budgets do not imply a clean
installation downloads less than 100 MB. Review `shared-publication-plan.json`
before publication and perform clean install, warm second-product reuse, offline
launch, update, repair and existing-install migration acceptance with real apps.

`scripts/Test-SharedAuthoring.ps1` exercises cross-product archive reuse, supplied
runtime reuse, public-library partitioning, protected resource aliases and private
file retention with synthetic inputs. It requires built CLI/native bootstrap and
an apphost fixture, writes to a caller-supplied new output directory, and executes
no fixture application. It complements the Core and bootstrap suites; synthetic
size results are not real application package acceptance.

The common recipe preserves the supplied sealed SecurityLoader module verbatim.
Its reviewed `publicPayload`, encrypted component and bootstrap trust files remain
local with unchanged bytes. A reviewed public DLL cannot be silently moved to a
shared NuGet pack, and private DLLs are never added to the module's public closure.
Resource relocation touching that closure is rejected. Missing reviewed paths or
a pinned original apphost block packaging before publication. In particular,
a sealing pipeline that only audits a single bundled executable must author and
review a loose framework-dependent bridge before using this recipe; the generic
partitioner cannot infer that authorization or replace the private sealing audit.

## Repair and retained files

Verified archives remain available for offline repair. An interrupted download or
extraction can reuse the task-owned staging paths; activated corrupt dependencies
fail closed and remain available for diagnosis. Uninstall removes only its own
reference, retaining shared files and older versions. There is no automatic shared
package garbage collection. Cleanup requires an exact reviewed path inventory and
approval; other applications, rollback media, models and runtime holds remain
protected.

The isolated `HeirowInstaller.SharedPackages.Tests` suite checks sharing across
applications, side-by-side versions, transactional rollback, reference ownership,
corrupt downloads, immutable file corruption, cross-origin redirects, format
compatibility, and downloaded updater source/protocol bindings. Its apphost fixture
tests binding rather than standalone execution; release acceptance must also launch
the actual published bootstrap, application, worker and plugin hosts.
