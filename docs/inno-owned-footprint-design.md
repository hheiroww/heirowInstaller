# Publisher-attested Inno owned-footprint source implementation

This worktree contains **uncompiled and untested source** for the distinct
`inno-owned-footprint` backend. No application catalog, historical baseline,
binary release or toolkit version is enrolled or published. Original generated-
DAT execution (`inno`) remains unsupported.

## Authority and enrollment

The inline `nativeMigration.baselines[].ownedFootprint` catalog is an explicit
complete **ownership attestation from the already trusted publisher**, covered
by the native target's manifest signature. It is not independently extracted
compiled-setup proof. Creation refuses an unsigned target or a signing key
different from the catalog's publisher; full-package and HUPD metadata readers
verify that binding before returning usable authority.

Migration separately verifies the installed publisher pin, original signed
wrapper/manifest digest, required setup payload hash/size, product, version,
scope, literal AppId and recorded root. No ARP command, mutable live inventory,
unsigned receipt, setup log or repaired DAT supplies ownership.

Enrollment requires all three explicit assertions, a review digest and a complete
bounded lineage containing the exact original setup:

- `authority`: `same-publisher-complete-ownership-v1`
- `coverage`: `complete-published-appid-lineage-v1`
- `wrapperPolicy`: `verified-app-local-wrapper-sdk-identity-v1`

These signed assertions are publisher claims. The engine does not independently
prove their completeness. The draft producer leaves them unset. Unknown lineage,
custom/shared actions, COM/services/tasks/environment changes or unexplained
outside-root behavior cannot be enrolled. Inno's [append behavior](https://jrsoftware.org/ishelp/topic_appendnotes.htm)
requires review of historical AppId coverage. Actual application catalogs and
their evidence need separate review before signing; this implementation enrolls
none.

## Strict bounded contract

Catalog model types reject unknown JSON fields; owned target JSON rejects
duplicate members. Paths are canonical literal relative paths. Each inline
catalog is at most 2 MiB; the complete manifest is at most 4 MiB.

| Record | Binding and initial limits |
| --- | --- |
| Baseline/review | Exact product/version, original manifest and publisher SHA-256, setup path/hash/size (at most 2 GiB), review SHA-256, and 1–128 distinct reviewed setup/version/recipe records. |
| Root | Literal `Programs/...` child of current-user LocalAppData, matching receipt and original root template. Default per-user installer state and current-user app-root ownership required. Network/reparse paths and custom legacy destinations refuse. |
| Program footprint | 1–4096 exact hashed files, at most 2 GiB each and 8 GiB total. Current files and the original entry point remain required. Optional historical files require an exact earlier reviewed lineage setup SHA-256 and exact bytes when present. At most 4096 structural directories remain preserved with shared parents. |
| Generated metadata | One exact app-root `uninsNNN.exe`/`.dat` and optional `.msg` set, at most 64 MiB each. Opaque backups only; no action/path parsing or execution. Additional uninstall sets refuse. |
| Inno key | Exact user hive/view/AppId registration, at most 64 complete named string/DWORD predicates, no child keys. Literal root substitution, date, nonnegative DWORD and wrapper-visibility predicates only; root/version/uninstall filename mandatory. |
| Launch links | At most eight exact current/historical current-user Programs/Desktop paths, optional presence and 64 KiB maximum. Historical bindings must name a reviewed lineage setup. Independently verified raw target, empty arguments, app working directory/icon and icon index zero. |
| Preserved data | At most 64 explicit file/directory namespaces disjoint from retired/target paths. Unlisted descendants of declared data directories remain untouched. Other unexplained files/directories refuse. |
| Wrapper state | Original setup plus optional app-local updater only: exact receipt feature/files, verified updater, bound generated profile, original visibility snapshot, cached maintenance host and SDK ARP identity. Other wrapper modules/component roots refuse. |

The transition stays per-user; ambiguous machine registration refuses. The old
cached host must remain inside its product cache and match the original signed
wrapper. A registered transition also requires a current standalone maintenance
host, so native maintenance cannot retain a host lacking its format. Internal
synthetic platforms are not public identity/isolation bypasses.

The target subset is app-local required launch files, optional shared updater
and SDK ARP only. Target shortcuts, registry/environment entries, services,
prerequisites, modules and external component roots reject at creation.
**Usability limit:** old launch links retire. Actual desktop-app packages still
need reviewed equivalent native shortcuts, or an explicitly accepted package
configuration, before release.

## Incomplete draft producer

`InnoOwnedFootprintDraft.CreateAsync` verifies the supplied original signature
and reads a frozen publish folder on a worker with progress/cancellation. Keep
the package reader open until awaiting it completes. It drafts file/directory
hashes and original manifest/setup identity.

Authority, coverage, review, AppId, lineage and integration/metadata declarations
remain incomplete; creation/publication refuses the draft. Frozen local files
are evidence inputs, not authenticated ownership or compiled-setup proof. This
API offers no automatic enrollment or signer.

## Complete backups and per-resource recovery

Inspection, backup, retirement, rollback and owned commit protection run off the
caller's UI thread with progress. Existing close-apps/cancel behavior remains.
Normal sharing rules refuse locked files: no forced process closure, permission/
read-only overrides, or delayed deletion. Explicit retry after closure/restart
revalidates the full ledger and resources.

The product lock, original receipt and verified original recovery media remain.
A current-user protected state directory holds backups and a current-user DPAPI
authenticated ledger with UI forbidden. Its binding includes catalog, original/
target manifests, original package/receipt hashes, current SID, app/state roots.

Creation identities are durable before large backup/restore writes. All backups
are complete and reverified before mutation. Per-action intents precede exact
hash/identity and typed metadata checks. The adapter removes only attested file/
link leaves and the fully backed-up exact Inno key. It never executes setup,
uninstaller or DAT, sweeps a root, or removes a shared parent.

A complete protected per-resource retirement marker is required before native
writes. ARP absence, a changed boot, or the external backend's `removed` phase
cannot replace it. Partial retirement and `repair-required` states retain their
own rollback obligation.

Before native commit, rollback restores exact bytes and typed integrations,
refusing conflicts/tampering. Program bytes return before the old Inno key is
advertised. Original updater/profile, hidden visibility and SDK ARP state return
from before-images without running repair/setup/DAT.

Native file recovery uses absence as the before-image of retired app/updater
files; their original bytes live in the owned ledger. The old visibility
snapshot is detached from the native receipt. Protected SDK ARP before/after
images cover obsolete values and partial native writes, even before an external
file-journal event. Only this restricted target subset permits the recovery
callback; other file/metadata uncertainty still refuses.

A late failure after native receipt/transaction commit finalizes only after
verifying the signed installed target, files and SDK state. The native marker
forbids any legacy revival. After that marker, bounded cleanup removes only
backup leaves matching their authenticated recorded identities, sizes and hashes.
Its durable intent and per-file completion flags support interruption/resume.
Locked, changed, replaced and unknown objects remain untouched; cleanup errors
report pending staging without changing the committed phase or restoring legacy.

The complete outer journal retains a cached migration-target path hint; retries
verify its original target signature/digest, original signed wrapper, receipt
binding and authenticated commit ledger. The hint supplies no authority. This
allows later native versions that omit the old declaration to retry cleanup.
The completed branch of install/repair retries instead of immediately skipping,
and native uninstall retries under the product lock before removing its receipt.
Known original-media intents retry too. Missing or changed verification context
preserves staging and reports pending cleanup. Retries require the installed
native receipt and its cached signed context. Rerunning the same native installer
or Repair can retry before that context is removed. If pending staging remains
when uninstall removes that context, it is preserved with its authenticated
journal and available evidence; later removal requires manual review. There is
no post-uninstall orphan sweep. While installed, small authenticated ledger/
receipt metadata and signed repair inputs remain. No recursive, shared-parent,
user-data or general cache sweep is performed.

## Validation boundary

The frozen v3 Core and NativeMigration.Tests sources compiled under a private
review stamp with zero errors and two nullable-owner warnings. The generated-
media policy and owned-footprint filtered fixture groups each passed their
expected single runner case (1/1 each). This source follow-up explicitly rejects
missing or non-SID owners at the two reported checks. Core compiled successfully
with those guards and no warnings under the private review stamp. Tests were
not repeated. Further tests and acceptance verification were skipped at the
user's request for this publication.

The focused fixtures use synthetic signed packages, marked temp files,
in-memory registry/link metadata and test-only authenticated state. They cover
backup/removal/registry/restore boundaries,
partial failure, cancellation, locks, tampering/conflicts, wrapper updates/
visibility, SDK metadata, late receipt/commit recovery, committed cleanup
interruption/conflicts and completed-journal maintenance retries. Real DPAPI, ACL,
shell-link, Inno/app and subsequent native-HUPD acceptance remain unverified.
The focused results do not establish existing MSI/Burn compatibility.

No real application catalog is signed or retired. The existing 2026.10.28.0
release assets remain untouched; this source targets toolkit 2026.10.29.0.

Actual application enrollment is also blocked by missing typed predicates for
current-user names and reviewed finite task-string choices. A personal literal
or an observed before-image cannot replace signed ownership authority. A later
narrow extension must bind the username to the same verified Windows user/SID
and compare task values with explicitly reviewed bounded string choices (or
exact choice tuples), without arbitrary expressions, patterns or commands.

Primary API semantics: [current-user DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata),
[link loading](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nf-objidl-ipersistfile-load),
and [raw link target inspection](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-getpath).
