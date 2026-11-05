# Administrator requirements

Native packages default to per-user installation without administrator approval.
Machine scope, signed setup `requiresAdministrator`, a required application's
`applicationLaunch.requiresAdministrator`, or an undetected selected privileged
prerequisite requests approval. Setup approval does not change the chosen scope.
Authoring reads the actual PE execution-level resource without loading the
executable. `highestAvailable` remains distinct from `requireAdministrator`.
These declarations require manifest format 5.

The native installer keeps the original user's setup window open. When the
signed descriptor declares an administrator requirement it prepares one verified
worker after the first frame, before downloading application payloads. The idle
worker receives keepalives and makes no installation changes. Install consumes
that same worker; cancellation closes the idle connection. Human Windows consent
is never simulated or bypassed.

The elevated native bootstrap provisions the signed host in the protected machine
namespace. Loose user-writable framework-dependent apphosts cannot be elevated
directly. The legacy path accepts only a sealed self-contained single-file host.
The worker independently verifies native descriptor publisher/product authority,
package signatures, target identity, paths, exact peer PID/creation time and a
one-use nonce before applying changes. The medium parent can identify the worker
over its pipe and cannot impersonate its administrator token.

The original coordinator's actual Windows token supplies user folders,
environment, HKCU and remembered sign-in. Per-user native files and integration
run under that token; selected privileged prerequisites run separately through
administrator authority. Machine operations use the protected machine namespace;
their user registry/environment integration still executes as the original user.
The original medium process opens normal applications. Opaque MSI/EXE backends
reject different-account elevation because their internal user-profile behavior
cannot be rebound safely; migrate them to native ownership.

File helpers use a one-shot pipe served by the actual SDK caller. Kernel pipe PID
and process creation time bind `--owner-pid`/`--owner-start` to the initiating user;
an independently supplied wait-only process grants no user-token authority.
The proof also binds product, install/state roots, scope, target manifest digest
and prepared stage. The helper verifies the owner's signed image and captures its
token before acknowledging readiness. Applications that start a prepared
`ProcessStartInfo` themselves must await
`UpdaterOwnerHandshake.WaitForAcceptanceAsync(start, token)` before exiting.
Elevated helpers restore remembered credentials under the original token and do
not launch browser approval as administrator.

`HeirowInstaller.Privileges.Tests` checks PE parser hostility, scope requirements,
actual same-account tokens/known folders/HKU reads, asynchronous context restoration,
and real pipe-child identity/nonce/target rejection. The isolated native wizard
checks a real idle prepared worker, reuse, cancel, graceful app closure and
progress. These checks do not establish credentialed UAC or different-account
desktop acceptance; those require an interactive Windows session.

An installed application with a signed administrator requirement requests Windows
approval through its native launcher. The same path handles error 740 only after
the ordinary launch target has passed file and dependency verification. The
original native launcher remains alive and authenticates the exact elevated
child over a nonce-bound pipe. The child verifies the kernel server PID, process
creation time, native image and signed descriptor before obtaining that user's
token; command-line paths cannot select another profile or dependency cache.

Application promotion performs no network requests. It opens source application
files and cached archives for read-only access under the original token, checks
their signed hashes, and publishes only verified bytes into protected machine
storage. It extracts the pinned runtime archives and copies exactly the signed
application inventory. Generated probing metadata selects the protected runtime
roots; user probing files and extra sibling DLLs are excluded. Cache contents
and access policies are checked again before launch. Failed staging remains for
review, and setup repair supplies missing dependencies.

The elevated application's working directory and `APP_INSTALL_ROOT` refer to its
protected resources. `SOCKETJACK_ORIGINAL_INSTALL_ROOT` records the original
location; it grants no source-loading authority. Original-token known folders
supply `USERPROFILE`, `APPDATA` and `LOCALAPPDATA`; temporary files use protected
storage. The launch environment contains a pinned runtime and Windows system
paths, with inherited runtime hooks, profiler settings and session secrets
removed. An administrator application still runs under the approved Windows
identity; profile paths do not impersonate that user for the application's own
registry or DPAPI operations. The application's design must account for those
Windows identity requirements.

`HeirowInstaller.Bootstrap.Tests` covers promotion byte policy, bounded handoff
frames, tampering, generated probing paths, exact inventory and environment
injection rejection without invoking UAC or writing protected machine storage.
