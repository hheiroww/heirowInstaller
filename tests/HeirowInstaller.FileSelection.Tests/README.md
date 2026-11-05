# File selection acceptance

Run the complete suite with one fresh task-owned output directory:

```powershell
dotnet run --project tests/HeirowInstaller.FileSelection.Tests -c Release -- C:\owned\file-selection
```

The focused scope-default regression can run independently:

```powershell
dotnet run --project tests/HeirowInstaller.FileSelection.Tests -c Release -- --scope-default-proof C:\owned\scope-default
```

It installs a signed format-4 machine-scoped bridge, then stages, reopens and applies a signed format-5 hfiles target whose default scope is user. The actual engine must retain the existing receipt, machine scope, target and state, generate shared owner references/probing/updater configuration from that installed scope, and perform no additional network access after preparation. The same target must produce a fresh user installation plan without elevation.

The fixture source-links the production bootstrap engine and models. It primes a second valid tiny cache, verifies that generated machine probing rejects that wrong cache, then verifies offline fallback to the installation's bound cache despite the application's user-default descriptor. No application or helper process is started.

All installation options use `Isolated=true` and `RegisterProduct=false`; state, targets, shared caches and updater files remain beneath the supplied output directory. The helper image is a small versioned test PE and the runtime package contains an inert marker. These checks establish engine identity/cache policy, not standalone updater execution, usable .NET runtime closure, OS machine ACLs, UAC, installed production application behavior or UI prompt behavior. The separate legacy bridge/runtime acceptance covers executable closure.

The focused fixture requires 64 MiB free and retains its synthetic signing key, signed inputs and receipts for review. It refuses an existing output directory and performs no cleanup.
