# Original generated-media Inno policy (unsupported)

These notes cover only the internal `inno` generated-media policy and pure fixtures.
**Ordinary package creation, reading and publishing reject a native target with
`backend: "inno"`; generated-media execution, observation and restoration are
unavailable.** The policy helper is separate from production package acceptance.
The real backend refuses before staging, prerequisites, journal creation or
registry/process actions. There is no flag that enables it. The existing MSI and
Burn execution paths keep their contracts. These changes are uncompiled and the
new fixture cases have not run. Any eventual binary needs a new toolkit version;
the published 2026.10.28.0 tool tuple and signed artifacts remain immutable.

The separately implemented, uncompiled publisher-attested
[`inno-owned-footprint` source path](inno-owned-footprint-design.md) does not
execute a generated log. No application is enrolled in that mode.

## Original generated-media authority

The executable mode requires a declaration in the **original signed** wrapper's
`legacyExecutable.innoUninstallMedia`, not a declaration added retrospectively
to the target or an edited installed receipt:

```json
{
  "provenance": "original-signed-generated-media-v1",
  "appId": "example.inno",
  "setupVersion": "6.6.0",
  "installDirectory": "C:\\Example\\InnoProduct",
  "executablePath": "generated/unins000.exe",
  "dataPath": "generated/unins000.dat",
  "messagesPath": "generated/unins000.msg"
}
```

All declared media must be required entries in that same original authenticated
payload. Their original hashes/sizes are the byte authority. The declaration is
an original publisher attestation of generated-media origin, exact root and
complete set; filenames, PE signing alone, a checksum of today's files, registry
values, a successful setup exit or a private JSON receipt do not substitute for
it. A missing MSG is permitted only when that original declaration excludes it.
The data is specific to the exact recorded installation root. These restrictive
preconditions are intentional; a generic published setup does not automatically
satisfy them.

The proposed native target would separately name `backend: "inno"` and repeat only the exact
identity in `inno.appId` and `inno.registration` (hive, view, full uninstall key).
The key must be precisely `AppId_is1`, matching the original signed maintenance
definition. Existing signature, manifest digest, product, version, scope and
root selection remain required. MSI/Burn fields cannot be mixed into this mode.
This is an internal contract proposal; production validation rejects it until
the actual retirement and recovery backend is implemented and validated.
The internal helper only checks proposed model bindings. Before any future
production integration, its caller must verify the original signed package
under the recorded publisher key; a model by itself is not a signature. There
is no production call from migration planning to this helper at this checkpoint.

Old wrappers containing only a setup EXE and an updater do not attest generated
EXE/DAT/MSG bytes. They fail closed. They cannot be retrofitted into this mode,
and cannot be native HUPD bases.

## Why a repair is not currently accepted

Inno's default [UninstallLogMode is append](https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm).
A new install can inherit actions in an old uninstall log with the same
[AppId](https://jrsoftware.org/ishelp/topic_setup_appid.htm). Merely running the
verified original setup again and hashing the resulting DAT does not establish
the provenance of all destructive actions. The original may also retain a newer
uninstaller. No repair-to-proof producer is implemented or enrolled here.

[SignedUninstaller](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm)
protects the EXE, not the complete generated data log. Signed uninstallers also
use a separate language MSG. The [uninstaller exit-code contract](https://jrsoftware.org/ishelp/topic_uninstexitcodes.htm)
states that every nonzero exit is incomplete and a temporary clone can still
be running after the initial process exits. MSI-specific reboot codes cannot be
silently transferred to an eventual Inno implementation.

## Required executable backend before activation

The existing migration operation lock, original receipt snapshot, verified
original setup, durable media creation intents and recovery journal must remain
in force. A disposable, separately journaled execution copy is needed because
Inno can mutate/delete its own EXE/DAT/MSG; the original recovery media must never
be consumed by that self-cleanup. Before execution, the live generated set must
match the original attested bytes/root/identity, including absence of unexpected
sidecars. A supported DAT reader must independently bind its actual AppId/root
and reject unsupported formats or contradicting action semantics; a provenance
label is not that implementation. Never execute an ARP UninstallString. After execution, require bounded
conclusive retirement of the exact owned Inno registration and files, including
its clone/reboot boundary, before native ownership and the existing SDK ARP
identity commit. No generic registry deletion or process-name matching is allowed.

Failure/interruption must preserve media, settings, other products and the
journal. Restoration must use the original signed setup and establish its
actual restored identity; restoration success does not retroactively attest a
new generated DAT. A fresh native full is the first possible HUPD baseline.
These are implementation requirements, not results claimed by this checkpoint.

## Separate publisher-attested owned-footprint mode

The [bounded catalog, producer and recovery implementation](inno-owned-footprint-design.md)
is source-only and unvalidated. Its authority is an explicit complete ownership
attestation from the already trusted publisher, inline in the signed target;
it makes no claim of independent compiled-setup extraction proof. The original-
DAT policy above does not solve migration of blob-only installations.

For old blob-only wrappers, this distinct mode requires an explicit
publisher-signed complete retirement catalog. It must bind the original setup
and signed manifest, existing publisher, product, version, scope, root and Inno
identity. It must attest every owned installed file and every integration:
shortcuts, exact registration values, COM/type-library entries, services, shared
file accounting, tasks, environment and any install/uninstall custom actions.
Unlisted user data is preserved. Shared, outside-root or unknown actions that
cannot be independently justified cause refusal. An inventory of files currently
present, a setup log, or a newly hashed mutable DAT is insufficient authority.

Only after validating that complete catalog can the separate journaled engine
stage durable file/integration backups, journal exact reversible actions, retire
owned items and the exact Inno registration, then commit native ownership. Such
registration removal would be part of complete attested retirement, not proof
of it. Rollback and reboot/interruption reconciliation must be explicit.

Only a complete enrolled catalog is accepted for `inno-owned-footprint`. Its
draft producer deliberately leaves attestation and review incomplete. It does
not emulate custom actions, shared integrations or arbitrary outside-root work.
No application-specific catalogs, logs or private code belong in the toolkit.

## Generated-media validation requirements (not executed)

1. Static review of the original-source authority and declaration/baseline bindings;
   inspect the allowlisted diff and all outgoing history before any public push.
2. Under the coordinator's later CPU slot, compile Core and the existing migration
   fixture project using a new version. Run the filtered Inno policy fixture, then
   the complete unchanged MSI/Burn suite. Confirm old JSON omits unset Inno fields.
3. Policy negatives: blob-only original, live-hash/repair claims, missing/duplicate/
   optional/unhashed/foreign-root media, wrong AppId/key/hive/view, mixed backend
   fields and unsupported original-DAT or incomplete owned-footprint targets.
   Confirm the real backend stops
   before process, registration or staging work even with a valid policy fixture.
4. Before enrolling executable retirement, add synthetic crash-state cases for
   disposable execution-media ownership and tampering, failed/late clone exit,
   reboot, journal interruption/resume, original-media preservation, rollback,
   changed registrations, settings and unrelated products.
5. Only after that reviewed implementation and passing fixtures: an explicitly
   owned real Inno baseline with authentic complete generated-media provenance,
   actual retirement/native install/repair/recovery and a later real native HUPD.
   A fake backend or isolated policy fixture cannot establish that acceptance.
