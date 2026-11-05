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
without its DLL, runtime configuration and dependencies cannot run. A NativeAOT
bootstrap can fetch the remaining signed host/runtime packages and start their
verified managed entrypoint. General managed dependencies need `.deps.json`-
compatible probing layouts and a private .NET host configured before managed
`Main`; changing the global `PATH` or scanning arbitrary DLL folders is not a
substitute. Preserve isolated renderer/plugin load contexts and shared contract
assemblies when adapting applications.

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
