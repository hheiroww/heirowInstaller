# Importing known legacy staging safely

`legacy-staging-cleanup` is a built-in native installer module for a publisher's one-time import of a **reviewed, exact inventory** left by its older installer. It runs under the normal product operation lock and file journal. It does not search for backups by age or glob, infer ownership from a directory name, or clean current application files.

The declaration must be inside the RSA-signed package manifest and the installer must have an expected publisher pin from the caller or the existing verified product receipt. A valid self-signature alone does not authorize this module. Its context is constructed from the signed manifest bytes, selected scope/root and verified previous receipt. Module configuration, receipt snapshots and interrupted journals cannot construct this authority.

The publisher first reviews every file, hidden file and directory. For a completed backup, the publisher verifies that every file matches its exact completed installation receipt and records the SHA256 of that receipt and original manifest. These hashes are attestations made by the new signed package; the runtime does not read an arbitrary receipt path or the publisher's working directory. A directory without this proof is classified as unproven incoming staging and retained in quarantine.

Add the module to `modules` in the native package manifest. This example contains invented fixture data; replace every leaf, proof and file entry with the reviewed product inventory before signing:

```json
{
  "legacy-staging-cleanup": {
    "formatVersion": 1,
    "trees": [
      {
        "leaf": "App.backup-20000101-000000-0123456789abcdef0123456789abcdef",
        "kind": "completed-backup",
        "completedReceiptId": "installed-20000101-000000-0123456789abcdef0123456789abcdef",
        "completedReceiptSha256": "1111111111111111111111111111111111111111111111111111111111111111",
        "sourceManifestSha256": "2222222222222222222222222222222222222222222222222222222222222222",
        "files": [
          {
            "path": "example.txt",
            "size": 3,
            "sha256": "3333333333333333333333333333333333333333333333333333333333333333"
          }
        ]
      }
    ]
  }
}
```

An unproven tree uses `kind: "unproven-incoming"`, an exact `App.incoming-YYYYMMDD-HHMMSS-<32 hex GUID>` leaf and the same complete file inventory. It must omit all completed-receipt proof fields. There is no configurable root, destination, recursive delete action or ownership override. An optional `feature` must reference a declared feature. Limits are 16 unique trees, 4096 files total and 2 MiB of configuration. File paths are product-relative and use the ordinary package path validation. Directories are derived exactly from the file parents; an additional empty directory is unknown data and preserves the whole tree.

Before mutation, the module compares the entire hidden-inclusive inventory, rejects reparse points, hard links, read-only/encrypted items and undeclared NTFS streams, queries Restart Manager for active resource users in batches of at most 256, and acquires all source file/directory handles. Files are held without sharing through size/hash verification, durable backup and unlink. Normal Windows [FileDispositionInfo](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info) performs deletion; [FileStreamInfo](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_stream_info) is queried on held handles to preserve unknown named streams. No POSIX semantics, ACL/attribute changes, privileged helper, process termination or read-only override is used. An unavailable query, busy handle or unsupported deletion preserves data; a failure after work starts rolls back the entire transaction. Extended Windows drive/UNC paths retain their canonical identity; UNC runtime acceptance requires a separately available owned test share.

Windows Restart Manager can reject registration of a resource path longer than its supported bounds even when extended held-file access succeeds. In that case the module preserves the whole source tree and creates no import ledger or quarantine. The long-path acceptance records the actual native error code; it does not treat an unavailable activity query as proof that a file is inactive.

Completed backups are removed file by file after their durable backups and exact recoverable journal operations exist. Unproven incoming files are copied, verified and journaled under:

```
<StateRoot>/.legacy-staging/<ProductId>/quarantine/<inventory SHA256>/<exact leaf>/
```

This location must be outside the application root and product uninstall cache. Only the declared source files and empty declared directories are removed. A deterministic ledger under `.legacy-staging/<ProductId>/imports/` uses the SHA256 of the lowercase exact leaf as its filename; its content binds the product, publisher pin, full inventory/provenance digest and disposition. Existing unknown quarantine or ledger content is preserved. A previous ledger makes that leaf a one-time import; a reappearing source or a later signed redeclaration with different inventory/provenance is preserved for review. Normal repair/uninstall does not delete quarantine or import ledgers. Restoration is a separate reviewed operation and must refuse an occupied original destination.

Recovery targets are derived afresh from the caller's verified signed package and previous receipt, including the exact original file hashes/sizes, exact quarantine files and deterministic ledger. Journal paths cannot expand that allowlist. Publisher, product, root, manifest and baseline bindings are checked before replay; changed recovery targets preserve the journal/backups for trusted repair. An old matching receipt during same-version repair does not prove that cleanup finished: only entries actually touched by the journal require their complete deterministic ledger, source removal and quarantine verification before crash backups can be discarded. Cleanup-created files and empty directories also use held guarded deletion during rollback, so a named stream added after a crash is preserved with recovery evidence. Existing backend-managed application files are not imported into native file ownership by this module.

Run the standalone acceptance with a fresh output directory. It creates only small generated signed packages, console child processes and owned temporary trees; it never cleans a real product installation:

```powershell
dotnet run --project tests/HeirowInstaller.LegacyCleanup.Tests -- --output artifacts/legacy-cleanup-acceptance-new
```

The suite covers successful removal/quarantine, preservation of unknown/changed/hidden/missing/active/read-only/link/stream data, long paths, held-handle races, invalid/unsigned/unpinned/publisher-mismatched declarations, rollback, abrupt-process journal recovery, same-version repair interruption, streams added after a crash, engine integration and uninstall persistence.
