# Modular installer and updater SDK

The public .NET projects can be referenced independently. The native UI is a consumer of the same Core API used by the CLI.

```csharp
using HeirowInstaller.Core;

using var package = PackageReader.Open("example.hinst");
package.VerifySignature(trustedPublicKeySha256, requireSignature: true);
await package.VerifyAsync();
var engine = new InstallerEngine();
var plan = engine.Plan(package, new InstallOptions { Scope = "user" });
await engine.InstallAsync(package, new InstallOptions
{
    TrustedPublisherKeySha256 = trustedPublicKeySha256,
    RequireSignature = true,
    Scope = "user"
});
```

Manifests support files, selectable features, registry entries, shortcuts, environment values, services, prerequisites, and existing MSI/EXE backends. Payload paths are relative, case-insensitive Windows destinations. The builder records each payload's SHA-256 and length. Installations retain receipts and their repair source. Imported backends preserve their authored installation rules; native operations use the transaction and rollback engine.

Selected Windows services and machine registry entries require machine installation scope, which keeps their maintenance state under protected administrator-owned storage. Excluded optional machine features do not require that scope. User receipts cannot restore machine integration snapshots.

Implement `IInstallerModule` for additional installation behavior and register instances explicitly with `new InstallerEngine(modules)`. A module supplies a plan, installation, and removal routine. Enlist rollback before each mutation and return a durable `ModuleSnapshot` containing the original state. Native helpers in `InstallerModuleContext` resolve paths under the target and participate in the active transaction. Modules are trusted application code; packages do not auto-load arbitrary extension DLLs.

For application updates, reference `HeirowUpdater.SocketJack`:

```csharp
using HeirowUpdater.SocketJack;

using var updater = new SocketJackUpdater();
var target = UpdaterTarget.FromInstalled(installedProduct);
var check = await updater.CheckAsync(target);
if (check.Available)
    await updater.ApplyAsync(target, check, new UpdaterPolicy());
```

`SocketJackUpdater` uses the actual public SocketJack HTTP client. Core's `IUpdateTransport` keeps transport separate from package verification and installation. Every redirect is checked before another request is issued. Credentials are restricted to an explicit origin; package signatures are checked against the installed publisher-key pin. An embedded signed package is treated as data by the existing trusted engine, so signing a package cannot authorize a substituted executable prefix.

The standalone `heirowUpdater` executable supports application profile JSON and feed/product/current-version arguments for software that does not use Core receipts. Run its `--help` for the current contract.
