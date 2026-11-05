# Signed directory comparison updates

`hfiles1` extends the existing exact-base `hupd1` protocol. An updated client explicitly requests `updateProtocol=hfiles1`; older clients and publishers retain `hupd1` behavior. A selected-file feed has `packageType: "hfiles"`, `manifestUrl`, `filesUrl`, and the SHA256 of the exact signed target manifest in `targetManifestSha256`.

The manifest endpoint returns JSON `{ "manifestBase64": "...", "signatureBase64": "..." }`, preserving the publisher's original manifest and RSA-PSS signature bytes. The client pins the installed publisher, product, scope and target version, hashes actual owned files asynchronously, and requests only missing or different selected payload files. It can skip intermediate versions and repair damaged owned files. New destinations blocked by an unowned file stop preparation. User files outside package ownership remain untouched.

The files endpoint accepts JSON `{ "targetManifestSha256": "...", "paths": ["relative/file.dll"] }`. Its response is a compressed ZIP containing the exact original `manifest.json`, `signature.json`, and `payload/<path>` entries for the requested paths only. Per-file signed hashes and sizes authenticate the ZIP contents. The client rejects extra, missing, duplicate, noncanonical, traversal, linked and mismatched files. Metadata envelopes are bounded to 6 MiB, request bodies to 4 MiB, compressed ZIPs to 2 GiB, and expanded selected content to 16 GiB. Authenticated transports implement `IFileUpdateTransport`; POST redirects are refused.

```csharp
using var updater = new UpdateClient(transport: authenticatedTransport,
    requireIncrementalUpdates: true, fileSelectionUpdates: true);
using var prepared = await updater.PrepareFileUpdateAsync(feed, installed,
    ownedStagingDirectory, options, progress, cancellationToken);
var result = await new InstallerEngine().ApplyPreparedFileUpdateAsync(
    prepared, options, progress, cancellationToken);
```

Preparation leaves the application running. The apply path verifies staged files and acquires shared dependencies and prerequisites before asking the existing exact-process guard to close affected applications. Under the product lock it rechecks the receipt, before-images and all file sources, then replaces only different files through the durable transaction journal. Unchanged file bytes and timestamps remain intact. Rollback and restart recovery retain the original verified before-images, including locally modified owned files. Interrupted external OS/module callbacks retain the existing trusted-repair boundary.

The installed repair record is a small signed `.hmeta` ZIP under the product's metadata cache. It never reconstructs or recompresses a complete `.hinst`. `PackageReader.OpenMetadata` supports signed metadata inspection; requesting absent payload bytes explicitly requires online repair. Original full repair caches remain supported. Existing MSI and executable backends must migrate to native ownership before using this protocol.

A prepared stage has a lease, the original signed metadata, an exact base receipt, an observed before-image inventory and selected ZIP/files. `PreparedFileUpdate.Open` rebinds a helper handoff to the current installed identity; stored paths do not grant new destination authority. Stages are retained for retry/recovery and are not broadly deleted by this API. Callers own the staging directory and must account for its storage and approved cleanup.

The isolated acceptance suite takes one fresh owned output directory:

```powershell
dotnet run --project tests/HeirowInstaller.FileSelection.Tests -c Release -- C:\owned-test-output\files
```

It checks actual-file diffing, skipped releases, missing/corrupt owned files, unchanged timestamps, preserved user data, hostile ZIPs, cancellation, rollback/retry, and process crashes during application files, metadata and receipt commit. It does not update a real installed application.

Every selected shared-package archive is prefetched before a stage becomes ready,
using the original caller's live authenticated transport. Exact verified archives
already in the shared store are reused without another staging copy; other
archives are hash/signature/payload verified under
`<preparedRoot>/dependencies/<archiveSha256>.hinst`. The helper derives this source
directory from its verified prepared stage, opens staged sources as the original
user, rechecks their hashes, then provisions the immutable store. A missing or
changed staged dependency fails before application closure. No dependency HTTP
request or saved credential is needed after the initiating session exits.

Legacy readers that support format 4 need an explicit bridge release before a
format-5 target. The bridge retains a physical self-contained managed updater as ordinary
payload and declares `updateCompanion.updateProtocol: "hfiles1"`. This additive
network capability is permitted in format 4; shared packages, application-launch
bindings and privilege declarations continue to require format 5. An older
installer ignores the unknown capability while verifying the original signature.
The new updater restores a missing capability from that pinned signed bridge
manifest when the old generated profile omitted it. An explicit mismatched
profile value still fails validation.

Publishers must offer the immutable bridge to legacy protocol requests with the
normal signed full-package or supported `hupd1` coverage, and offer format-5 file
targets only after clients negotiate `hfiles1`. Publishing a format-5 target alone
does not establish legacy coverage. `scripts/Test-LegacyBridge.ps1` builds the
exact locally available legacy Core commit into an owned directory and checks
its real signature verification, isolated install of the native updater into the
old shared slot, omitted profile field, rejection of a direct format-5 package,
and the subsequent changed-file transition. It never runs the fixture application
or requests administrator approval. With `-ExecuteSelfContainedUpdater`, it also
starts the actual old-installed updater with global runtime lookup disabled and
runs its real file-update helper. A physical thin native updater by itself does
not establish cold-machine bridge readiness: its maintenance host and private
runtime must already exist before offline launch. The self-contained bridge
avoids that dependency and stages the modern target's shared startup packages
before the final thin updater becomes active.
