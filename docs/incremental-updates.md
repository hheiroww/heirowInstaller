# Signed file updates

A new target package must include both actual legal documents. An existing policy-free signed base remains valid; the existing signed target manifest carries the new Privacy Policy, Terms and Conditions, URLs and content integrity. No extra HUPD entries are introduced. [Legal document requirements](legal-documents.md).

New native packages can opt into `updateMode: "files"`. Their updater requests
`fromVersion=<installed version>&updateProtocol=hupd1` on the configured HTTPS
feed, preserving its other query parameters. A conflicting or duplicated
selector is rejected. File mode accepts an available `.hupd` transition and
refuses an installer download as a fallback. Older profiles with no mode keep
their existing update protocol.

A missing exact-base transition is a publisher coverage failure, including when
the installation originally migrated from an older installer. The SDK reports
`MissingIncrementalUpdateException` with the installed and offered versions;
malformed feeds still raise `InvalidDataException`. No Setup is executed to
bypass the file-update policy. Provide a signed transition for the exact base,
or a retained route through intermediate releases, before activating a target.

SocketJack stages a full release when existing native file-update versions lack
a signed route to it. Upload the missing `.hupd` files, then publish the same
full package again. A receipt with `updateActivationPending: true` confirms
staging, not activation; activation waits explain this required action immediately.
Retain every distributed native baseline, including manually installed builds,
in the publisher's version inventory. An unregistered local build cannot be
included in the server's coverage check.

Signed toolkit builds require `scripts/Build.ps1 -PreviousPackage <signed-base>`.
The parameter accepts multiple baseline packages and emits a separately named
transition for each. `-FirstRelease` is reserved for an initial release with no
native baseline. The release workflow downloads the previous signed package
and refuses publication without a built `.hupd`; the publisher separately checks
routes from all its registered native file-update versions.

The first installation or an explicitly authored migration still uses a full
installer. Opaque MSI/executable backends cannot produce or consume file deltas:
their backend registration and ownership must first be migrated to a native
package. Full manifests using shared runtimes, file mode, or external component
roots use package format 2 so an old installer cannot silently ignore those
semantics. Existing signed format 1 packages remain readable without changing
their serialized manifest bytes or signatures.

## Authoring

```csharp
var transition = await IncrementalUpdateBuilder.BuildAsync(
    "previous.hinst", "next.hinst", "product-1-to-2.hupd",
    new PackageBuildOptions { SigningKeyPath = publisherKeyPath }, cancellationToken);
```

Both full packages must be signed by the same pinned RSA publisher. The build
verifies their original signatures and every payload hash before comparing
files. The target must have the same product/scope and a newer version.
Component-root definitions and existing path-to-root assignments cannot silently
relocate. An authored move uses distinct removed/created archive paths.

The `.hupd` ZIP has these entries:

| Entry | Contents |
| --- | --- |
| `update.json` | Product, base/target versions, exact base/target manifest SHA256, publisher key fingerprint, complete file transition metadata |
| `update-signature.json` | RSA-PSS-SHA256 signature over the original `update.json` bytes |
| `base/manifest.json`, `base/signature.json` | Original signed full base metadata |
| `target/manifest.json`, `target/signature.json` | Original signed full target metadata |
| `payload/<archive path>` | Compressed bytes for created or changed files only |

Each transition item declares `create`, `change`, or `remove`, its path, old/new
SHA256 and sizes as applicable, and old/new component-root identifiers when
present. Removed and unchanged files have no payload entry. A full Setup image
is never embedded as an update backend.

The reader rejects duplicate/case-colliding entries and metadata properties,
traversal, noncanonical paths, symlink/reparse entries, undeclared payload,
incomplete operations, mismatched sizes/digests, and different publisher keys.
Limits are 4 MiB per full manifest, 64 KiB per signature, 8 MiB for transition
metadata, 100,000 ZIP entries, and 16 GiB expanded data. Every copied stream is
bounded by its signed length and SHA256.

## Feed

An incremental feed uses `packageType: "hupd"`. Its existing `installerUrl`,
`sha256`, and `size` describe the incremental ZIP. It also supplies:

```json
{
  "baseVersion": "1.0.0",
  "baseManifestSha256": "<SHA256 of the exact signed base manifest bytes>",
  "targetManifestSha256": "<SHA256 of the exact signed target manifest bytes>"
}
```

These fields are nullable and omitted from older feeds. The client compares all
three against the signed transition and its installed receipt. It never sends
browser cookies or bearer tokens in the selector query. Transport credentials
remain scoped to the existing authenticated transport.

## Application and repair

`UpdateClient.ApplyAsync` verifies the downloaded ZIP digest, all signatures,
the installed publisher/version/ownership, and the base repair manifest. Every
owned selected base file must still match its signed size and SHA256. An
unowned file blocking a new destination stops the update.

The client reconstructs a full `.hinst` locally. Unchanged owned files come from
their verified installation destinations; changed/created files come from the
incremental ZIP. Bytes for an unchanged optional feature never installed on the
machine come from the verified old full repair cache. Shared updater payloads
also use that signed cache because they are not app-root owned files.

Reconstruction retains the target's original manifest and signature bytes and
verifies the resulting complete package. It does not require a private signing
key or a network installer. The normal installer engine then applies the
selected files transactionally and stores this complete package for repair and
later feature changes. Unchanged owned files keep their bytes and timestamps.

## Interrupted work

`FileTransactionRecoveryContext.ForInstall` derives a recovery allowlist from
the verified package/plan and validated prior receipt. The incremental factory
can derive the same binding before reconstruction, allowing recovery of a
partially applied base. The engine must call `ValidateInstalledBase` after
acquiring its product lock for an exact-base update.

Durable file transactions use an exact GUID child of `state/.transactions`, an
ownership marker, an exclusive lease, flushed backup files, and a flushed
append-only journal written before each mutation. Recovery compares the marker
with the caller's product/root/publisher/manifest binding. Destinations and
backup hashes are checked against the caller's signed/receipt-derived allowlist;
journal paths and completion flags never grant authority. Running application
guards still apply before rollback.

Reconstruction work is isolated under `state/.updates/<product>/<GUID>`.
Incomplete downloads have separate marked/leased `.hupd-downloads` children.
Active work and ready packages waiting for a helper are retained. Inactive
incomplete downloads, consumed owned downloads, and recovered reconstruction or
extraction workspaces can be removed. Reparse points, unmarked directories,
foreign files, and unrelated `App.backup` names are preserved.

Ordinary in-process exceptions use the existing rollback stack. After a process
crash, only independently verified file changes are automatically restored.
Arbitrary OS/module callbacks or unverified original metadata cannot be
serialized safely: their journal/backups remain and recovery requests a trusted
repair. A modified recovery target or tampered backup also stops automatic
recovery without discarding the original evidence.

## Isolated acceptance

```powershell
dotnet run --project tests/HeirowInstaller.Incremental.Tests -c Release
```

The independent suite uses generated signing keys, fake transport responses,
isolated installation roots, and owned console children that exit abruptly.
It covers compression/changed-only payload, signature/base/hash tamper,
create/change/remove, repair without a signing key, unchanged timestamps,
initial/install/update restart recovery, lease and reparse safety, and protected
foreign backup names. It does not install real applications or change services.
