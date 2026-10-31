# Applications using installed files

Interactive installation, repair and removal show the applications currently using affected files before changing application files. **Close apps and continue** sends a normal window close request; save dialogs remain in control of the application. **Check again** and **Cancel installation** stay available while waiting. The entire graceful attempt, including fresh discovery, has an eight-second deadline. A restarted application needs another consent decision.

**Force close listed apps** is available only for verified application entry-point images, including a selected signed external payload copy of that same entry point. It first requires a second confirmation that unsaved work can be lost. The confirmation is bound to the complete displayed identity set and is discarded when that set changes. Windowless instances of those exact owned images can be eligible. Plugin hosts such as FL Studio, unrelated helpers or model workers, wait-only dependencies, services, system images and inaccessible identities never gain force authority. Before terminating each approved process, Core holds one process handle and revalidates its PID, UTC creation time, canonical executable path, current session, exact authored image/resource, and exclusions. It terminates that one process object only, without descendants or name-based matching.

The engine checks before creating installation state, after verification/extraction, and before backend execution. A quiet interval and another resource inspection detect restarted instances. Each discovery call has a three-second deadline; a single shared read-only worker prevents retries from accumulating stuck native queries. A timeout returns an actionable prompt, retaining only a previously displayed snapshot when available. Force revalidates that exact snapshot without depending on another potentially blocked Restart Manager scan. Cancel promptly cancels the caller and every pending graceful attempt; a late discovery result cannot close anything. Cancellation never authorizes termination or service shutdown. Windows Restart Manager is used only to query exact file users; its shutdown and restart APIs are never called.

The application entry point and executable resources inside the installation folder are inspected automatically. An external plugin requires an exact signed resource declaration. For example:

```json
{
  "affectedResources": [
    {
      "path": "%CommonProgramFiles%\\VST3\\Example\\Example.vst3",
      "feature": "plugin"
    },
    {
      "path": "{app}\\plugins\\Example.clap"
    }
  ]
}
```

Each declaration names a file, including the actual binary inside a directory-style VST3 bundle. `feature` is optional; selected features control which declared resources apply. Supported substitutions are `{app}`, `%CommonProgramFiles%`, `%CommonProgramFiles(x86)%`, `%ProgramFiles%`, `%ProgramFiles(x86)%` and `%LocalAppData%`. Windows folder substitutions use known folders rather than arbitrary environment-variable values. Absolute local paths are also supported. Directories, wildcard process names, UNC paths, alternate data streams, traversal, reparse points and Windows system resources are rejected. Permitted file extensions are `.exe`, `.dll`, `.vst3` and `.clap`; a manifest can declare at most 256 exact resources.

A process is identified by PID, UTC creation ticks and canonical executable path. An external GUI host additionally needs proof that it has the declared plugin module loaded before a graceful close is offered. Merely passing a PID, matching an executable name, or registering an updater host path cannot authorize closure. Uninspectable instances, services, Explorer and system processes require manual closure. These restrictions also prevent the installer from forcibly closing unrelated background model workers.

The generated updater profile stores selected resolved paths in `affectedResourceFiles`. Older profiles default to an empty list. When the SocketJack SDK reads a profile, it derives permitted resources again from the verified signed package associated with the installation receipt. Changing the writable profile cannot authorize closure of another application, and older builders that omitted this profile field can still carry the signed declarations in the package.

## SDK integration

Use the same asynchronous callback for the installer and updater. The callback is runtime state and is never serialized:

```csharp
options.RunningApplicationsPrompt = async (request, cancellationToken) =>
{
    // Present request.Applications and request.Message; keep Cancel available.
    // CanRequestClose and CanForceClose are separate. Force requires a second
    // unsaved-work confirmation bound to these exact identities.
    return await ShowCloseAppsOrCancelAsync(request, cancellationToken);
};
```

`InstallOptions.RunningApplicationsPrompt` applies to engine operations. `ApplicationLifecycleOptions.RunningApplicationsPrompt` applies after **Update now**, including an application that reopened during download. The normal automatic after-exit watcher still waits until the application exits before checking for updates.

`RunningApplicationTarget.FromPlan`, `FromManifest` and `FromConfiguration` construct shared targets. `RunningApplications.InspectAsync` provides a bounded snapshot for native interfaces and throws `TimeoutException` on a discovery deadline. `EnsureClosedAsync` owns consent, fresh identity validation, closure and bounded waiting. `WithWaitOnlyExecutables` and `WithWaitOnlyProcesses` add dependencies requiring manual closure. `Cancel` throws `OperationCanceledException`; callers should return to their cancelled state without beginning maintenance. Existing `CloseAppsAndContinue` and `Cancel` enum values remain unchanged; `CheckAgain` and `ForceCloseListedApps` extend the callback/IPC contract. Older serialized identities default `CanForceClose` to false. Callbacks can inspect `InspectionTimedOut` when Windows discovery stalls.

With no callback, running applications cause an immediate actionable `RunningApplicationsException`. Noninteractive commands never wait indefinitely, prompt, or close applications. The updater's `NoPrompt` mode also suppresses the consent callback. Choosing **Update now** does not imply consent to close a newly opened application.

Trusted update helpers use compact random staging names inside the existing staging directory. Scope, permissions, publisher verification and in-memory credentials remain unchanged. The helper executable's Windows launch path is checked before copying it; SocketJack handoffs check before downloading as well. If the configured root itself leaves too little room, `PathTooLongException` asks for a shorter state root or download directory instead of reporting a missing helper.

## Isolated acceptance

The dedicated probe is a freshly compiled, marker-bound desktop application. It supports normal closure, a refusing save confirmation, a deliberately non-pumping window, a windowless mode, exact identity receipts, autonomous expiry, and a byte-verified copy of its own harmless assembly as a test plugin. Tests explicitly terminate only newly created approved fixture identities when exercising Force. Cleanup uses a fixture-owned release marker. Tests never close an existing application.

```powershell
dotnet publish tests/HeirowInstaller.Tests/Fixtures/RunningApplicationProbe/RunningApplicationProbe.csproj -c Release -r win-x64 --self-contained false -o artifacts/running-application-probe -m:1 /nr:false /p:UseSharedCompilation=false
dotnet build tests/HeirowInstaller.ProcessSafety.Tests/HeirowInstaller.ProcessSafety.Tests.csproj -c Release -m:1 /nr:false /p:UseSharedCompilation=false
dotnet tests/HeirowInstaller.ProcessSafety.Tests/bin/Release/net10.0-windows/HeirowInstaller.ProcessSafety.Tests.dll artifacts/running-application-probe
```

These checks cover path validation, feature selection, exact identities, unrelated same-name processes, noninteractive behavior, cancellation before state creation, save confirmation refusal, bounded non-pumping/windowless force, stale creation/path/PID consent, canceled force, a stuck coalesced discovery with no late close, replacement instances, an app opening during extraction, wait-only/service/helper exclusions, and an actual external plugin module.
