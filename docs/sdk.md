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

## Version checks without installer executables

Reference `src/HeirowUpdater.SocketJack/HeirowUpdater.SocketJack.csproj` to check version metadata through the SocketJack networking library. This operation needs no installed or running `heirowUpdater` or `heirowInstaller`, no application profile, and no cached package or installer receipt. The calling app supplies its actual installed version and its current ordinary account session. Show the app's busy indicator before awaiting the call, and cancel or ignore its result if that account changes.

The SDK defaults to the exact `SocketJackVersion` range `[2026.10.24]`. A consuming solution can pass a different shared `SocketJackVersion` globally or through its SDK project reference's `AdditionalProperties` to match its other SocketJack references. An explicitly supplied `SocketJackFacadeProject` selects that existing source project instead of the package; a missing project is an error. Keep machine-specific paths in local build inputs. The default public build has no private source dependency.

```csharp
using HeirowUpdater.SocketJack;

var client = new SocketJackVersionCatalogClient(new Uri("https://socketjack.com"));
var session = new ApplicationSessionSnapshot(sessionCookie: currentSessionCookie,
    bearerToken: currentAccessToken);
var result = await client.CheckAsync("example.app", actualInstalledVersion,
    session, channel: "beta", cancellationToken: cancellationToken);
// result.State: Current, UpdateAvailable, or Ahead. Ahead never offers a downgrade.
```

The `session` argument is optional. Omitting it sends no credentials and does not reuse another call's credentials or read environment variables. Only its cookie and bearer header values are used; process/session provenance is not sent to the server. Credentials are restricted to the supplied publisher origin. Checking performs no sign-in, package download, application closure, or installation.

`CheckAsync` requests `GET /api/installer/catalog/{productId}/versions?channel=public|beta&limit=1`. The five-second default deadline includes network setup, headers and body; pass `timeout` to the constructor to select a different positive deadline up to one minute. Cancellation remains `OperationCanceledException`; deadline expiry is `TimeoutException`. Both channels retain the publisher's existing account and eligibility policy. HTTP errors including 400, 401, 403, 404 and 503 are `HttpRequestException` with `StatusCode`, never an up-to-date result. Redirects and unsolicited 304 responses are rejected. There is no client cache or automatic retry.

`GetVersionsAsync(productId, session, channel, limit: 20, beforeVersion: null, cancellationToken)` returns `InstallerVersionCatalogPage` from `HeirowInstaller.Core`. The wire contract is:

```json
{
  "schemaVersion": 1,
  "productId": "example.app",
  "channel": "public",
  "latestVersion": "2.0",
  "versions": ["2.0", "1.9"],
  "nextBeforeVersion": "1.9"
}
```

History contains only existing accounted published versions visible in that channel, bounded by the active published pointer. `latestVersion` is independent of the current history page. The first page must begin with that active version; subsequent pages may be empty after the oldest version. Versions are distinct and descending under `PackageVersion` comparison. Pass `nextBeforeVersion` back as the exclusive `beforeVersion` selector to obtain another page; null ends pagination. Page sizes are 1–100 and responses are limited to 64 KiB. The typed client validates schema, product, channel, version syntax, ordering, publication bounds and cursor binding before returning a result. An installed version ahead of the active pointer never produces a downgrade offer. The deadline also bounds transports and body reads that ignore cancellation, and late response resources are disposed.

`HeirowInstaller.Core.VersionCatalogClient` exposes the same comparison and paging without an account-session parameter, accepting an optional `IUpdateTransport` for other embedding scenarios. The SocketJack wrapper supplies the origin-scoped transport. The existing `UpdateClient` and `SocketJackUpdater` feed/download/apply APIs remain available for the separate, explicitly requested installation step.

The standalone `heirowUpdater` executable also supports application profile JSON and feed/product/current-version arguments. Run its `--help` for that separate contract.
