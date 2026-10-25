# Applications using installed files

Interactive installation, repair and removal show the applications currently using affected files before changing application files. The user can choose **Close apps and continue** or **Cancel installation**. Close sends a normal window close request. Save dialogs remain in control of the application. After five seconds, an application that refuses closure returns to the prompt with retry and cancel available. A restarted application needs another consent decision.

The engine checks before creating installation state, after verification/extraction, and before backend execution. A quiet interval and another resource inspection detect restarted instances. Cancellation never authorizes termination or service shutdown. Windows Restart Manager is used only to query exact file users; its shutdown and restart APIs are never called.

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

A process is identified by PID, UTC creation ticks and canonical executable path. An external GUI host additionally needs proof that it has the declared plugin module loaded. Merely passing a PID, matching an executable name, or registering an updater host path cannot authorize closure. Uninspectable instances and background, service, Explorer or system processes require manual closure. These restrictions also prevent the installer from forcibly closing background model workers.

The generated updater profile stores selected resolved paths in `affectedResourceFiles`. Older profiles default to an empty list. When the SocketJack SDK reads a profile, it derives permitted resources again from the verified signed package associated with the installation receipt. Changing the writable profile cannot authorize closure of another application, and older builders that omitted this profile field can still carry the signed declarations in the package.

## SDK integration

Use the same asynchronous callback for the installer and updater. The callback is runtime state and is never serialized:

```csharp
options.RunningApplicationsPrompt = async (request, cancellationToken) =>
{
    // Present request.Applications and request.Message; keep Cancel available.
    // Each identity includes CanRequestClose so manual-only entries can be explained.
    return await ShowCloseAppsOrCancelAsync(request, cancellationToken);
};
```

`InstallOptions.RunningApplicationsPrompt` applies to engine operations. `ApplicationLifecycleOptions.RunningApplicationsPrompt` applies after **Update now**, including an application that reopened during download. The normal automatic after-exit watcher still waits until the application exits before checking for updates.

`RunningApplicationTarget.FromPlan`, `FromManifest` and `FromConfiguration` construct shared targets. `RunningApplications.InspectAsync` provides a snapshot for native interfaces; `EnsureClosedAsync` owns consent, fresh identity validation, normal closure and bounded waiting. `WithWaitOnlyExecutables` adds registered host dependencies requiring manual closure unless independent verified resource evidence already grants closure. `Cancel` throws `OperationCanceledException`; callers should return to their cancelled state without beginning maintenance.

With no callback, running applications cause an immediate actionable `RunningApplicationsException`. Noninteractive commands never wait indefinitely, prompt, or close applications. The updater's `NoPrompt` mode also suppresses the consent callback. Choosing **Update now** does not imply consent to close a newly opened application.

Trusted update helpers use compact random staging names inside the existing staging directory. Scope, permissions, publisher verification and in-memory credentials remain unchanged. The helper executable's Windows launch path is checked before copying it; SocketJack handoffs check before downloading as well. If the configured root itself leaves too little room, `PathTooLongException` asks for a shorter state root or download directory instead of reporting a missing helper.

## Isolated acceptance

The dedicated probe is a freshly compiled desktop application. It supports a normal close, a refusing save confirmation, exact identity receipts, an autonomous expiry, and a byte-verified copy of its own harmless assembly as a test plugin. Tests never terminate it forcibly and never close an existing application.

```powershell
dotnet publish tests/HeirowInstaller.Tests/Fixtures/RunningApplicationProbe/RunningApplicationProbe.csproj -c Release -r win-x64 --self-contained false -o artifacts/running-application-probe -m:1 /nr:false /p:UseSharedCompilation=false
dotnet build tests/HeirowInstaller.ProcessSafety.Tests/HeirowInstaller.ProcessSafety.Tests.csproj -c Release -m:1 /nr:false /p:UseSharedCompilation=false
dotnet tests/HeirowInstaller.ProcessSafety.Tests/bin/Release/net10.0-windows/HeirowInstaller.ProcessSafety.Tests.dll artifacts/running-application-probe
```

These checks cover path validation, feature selection, exact identities, unrelated same-name processes, noninteractive behavior, cancellation before state creation, save confirmation refusal, cancellation while waiting, replacement instances, an app opening during extraction, wait-only hosts, and an actual external plugin module.
