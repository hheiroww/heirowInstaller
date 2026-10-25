# Retiring a recorded MSI or Burn installer

A format 3 native package may authorize an exact transition from previously published MSI or Burn packages. It includes the complete native payload and an optional `nativeMigration` declaration. The first native migration uses a newly built standalone setup; subsequent native updates can use `.hupd` files.

One immutable target can list up to 32 published baselines:

```json
{
  "formatVersion": 3,
  "nativeMigration": {
    "baselines": [
      {
        "fromVersion": "1.0",
        "fromManifestSha256": "<SHA256 of the original manifest.json bytes>",
        "backend": "msi",
        "scope": "machine",
        "msiProductCode": "{11111111-1111-1111-1111-111111111111}"
      },
      {
        "fromVersion": "1.1",
        "fromManifestSha256": "<SHA256 of the other original manifest.json bytes>",
        "backend": "burn",
        "scope": "machine",
        "burnRegistration": {
          "hive": "machine",
          "view": "registry64",
          "bundleUpgradeCode": "{22222222-2222-2222-2222-222222222222}"
        },
        "burnMsiProductCodes": [
          "{33333333-3333-3333-3333-333333333333}"
        ]
      }
    ]
  }
}
```

Replace the example identities with the original published identities. Hash the exact uncompressed `manifest.json` entry from the original signed `.hinst`, without reformatting or reserializing it. Include every application MSI child owned by a Burn chain. `burnMsiProductCodes: []` explicitly declares a Burn chain with no MSI children; null, omission, duplicates and invalid GUIDs are rejected. Shared prerequisites are not application children and remain under their own installer ownership.

The engine selects exactly one baseline using the validated receipt's version and scope plus its cached signed manifest digest. The original and target must share their product identity and pinned publisher. Migration preserves the recorded installation root and scope. A version or installer identity that is not listed cannot migrate. Existing native repair ignores the legacy list. A clean installation is permitted only when its target is empty and every declared legacy identity is absent; a registry discovery never substitutes for a protected heirowInstaller receipt.

Without explicit scope or custom state-root options, migration looks only in the two ordinary heirowInstaller receipt namespaces. It follows one matching recorded scope and root; two matches require an explicit scope. Empty-target `--isolated` validation exercises only the native payload and avoids real backend queries. It cannot retire a real legacy installation.

The product's existing operation lock stays held across retirement and native installation. Before invoking the original backend, the engine verifies and stages its signed payload and saves the original receipt under the scoped state root's `.native-migrations` directory. It executes the verified original MSI or Burn payload with its signed arguments; it does not execute a mutable registry uninstall command, remove Windows Installer registrations itself, or forward arbitrary legacy CLI arguments.

Preparation writes its bounded ownership journal and exclusive lease before large writes, then records each new file's identity before copying bytes. A retry reuses verified completed files. An interrupted partial file may be removed only while its original file identity remains held and its content matches the authenticated source prefix. Normal cancellation cleans those verified staged files. Unknown, replaced, linked, encrypted or busy files are preserved. Recovery targets the exact recorded directory and inventory; it does not search by age or filename pattern. Recovery staging accepts up to 4,096 declared payload files.

An exit code of 3010 or 1641 stops native writes. The user must restart Windows and rerun the same setup. An interrupted external operation with an unknown result also requires restart and reconciliation of the exact OS registrations. The boot boundary uses Windows' documented `LastBootUpTime`, normalized from DMTF's UTC offset; timezone formatting alone cannot count as a reboot. Missing or ambiguous registration state requires repair. A missing Burn bundle with a remaining application MSI is ambiguous and cannot proceed.

If native installation fails, its file transaction rolls back first. Only a conclusive file rollback permits restoration from the retained original signed media. A failed or reboot-requiring restoration preserves the journal and original receipt. Separate repair and uninstall operations stop while an incomplete migration needs the exact target setup to resume. Completion removes only verified staged media and retains the small completion record and the existing signed repair cache.

External MSI/Burn custom actions remain external transactions. The engine cannot promise atomic reversal of arbitrary vendor custom actions or changes outside its declared ownership. Unknown files, pending OS state and changed recovery data are preserved for repair. Real application/backend migration still requires acceptance against the published installer baselines; the isolated migration suite never executes MSI or Burn.

Microsoft documents the relevant [MSI product states](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msiqueryproductstatea), [restart behavior](https://learn.microsoft.com/en-us/windows/win32/msi/system-reboots), and [WMI datetime format](https://learn.microsoft.com/en-us/windows/win32/wmisdk/cim-datetime).
