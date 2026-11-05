using System.Security.Cryptography;
using HeirowInstaller.Core;
using HeirowUpdater;
using HeirowUpdater.SocketJack;

internal static partial class UpdaterProgram
{
    private static async Task<int> ApplyFilesAsync(Arguments args, CancellationToken token, Action<string, string> boundPaths)
    {
        var root = Path.GetFullPath(args.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("apply-files requires a prepared signed directory update."));
        PackageSafety.EnsureNoReparsePoints(root);
        var profilePath = Path.GetFullPath(args.Require("lifecycle-config"));
        var configuration = ApplicationUpdater.ReadConfiguration(profilePath, requireEntryPoint: false);
        var id = args.Require("product-id"); var key = args.Require("trust-key"); var expectedVersion = args.Require("expected-version");
        if (configuration.SharedRuntime is null || configuration.ProductId != id || !PackageSafety.HashEquals(configuration.PublisherKeySha256, key) ||
            configuration.Scope != args.Require("scope") || configuration.Isolated != args.Has("isolated") ||
            !Path.GetFullPath(args.Require("target")).Equals(configuration.InstallDirectory, InstallerPlatform.PathComparison) ||
            !Path.GetFullPath(args.Require("state-root")).Equals(Path.GetFullPath(configuration.StateRoot), InstallerPlatform.PathComparison))
            throw new InvalidDataException("The selected-file handoff differs from its signed installed application.");
        var trustedRuntime = ApplicationUpdater.ResolveExecutable(configuration);
        if (!BootstrapProcessTrust.IsTrustedUpdaterProcess(configuration, trustedRuntime))
            throw new InvalidDataException("Selected-file updates must run through the verified installed updater.");
        var installed = InstallerEngine.ListInstalled(configuration.StateRoot).Single(product => product.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && product.Scope == configuration.Scope);
        var options = new InstallOptions
        {
            StateRoot = configuration.StateRoot, TargetDirectory = configuration.InstallDirectory, Scope = configuration.Scope,
            TrustedPublisherKeySha256 = key, RequireSignature = true, Isolated = configuration.Isolated, RegisterProduct = installed.RegisteredProduct,
            CompanionStopAlreadyHandled = true, Silent = true, Launch = false, LogPath = args.Get("log")
        };
        using (var prepared = PreparedFileUpdate.Open(root, installed, options))
            if (prepared.Metadata.Manifest.Version != expectedVersion || !PackageSafety.HashEquals(prepared.Metadata.ManifestSha256, args.Require("target-manifest-sha256")))
                throw new CryptographicException("The prepared file stage differs from the handoff target.");
        var stateDirectory = ApplicationUpdater.StateDirectory(configuration);
        var statusPath = BoundHandoffPath(args.Require("status-file"), stateDirectory, "status.json");
        var readyPath = BoundHandoffPath(args.Require("lifecycle-ready"), stateDirectory, "ready.json");
        var statePath = Path.GetFullPath(args.Require("lifecycle-state"));
        if (!statePath.Equals(Path.Combine(stateDirectory, "state.json"), InstallerPlatform.PathComparison))
            throw new InvalidDataException("The selected-file lifecycle state belongs to another installation.");
        boundPaths(statusPath, statePath);
        using var maintenance = new ApplicationStopMonitor(configuration, resetForOwner: false, token); token = maintenance.Token;
        await UpdaterSchedule.WriteJsonAsync(readyPath, new { status = "ready", helperProcessId = Environment.ProcessId }, token);
        var dependency = await WaitForIncrementalOwnerAsync(args, trustedRuntime, token);
        var runningTarget = RunningApplicationTarget.FromConfiguration(configuration).WithWaitOnlyExecutables(ApplicationHostRegistry.Paths(configuration)
            .Concat(args.Get("application-host") is { } host ? [host] : []))
            .WithWaitOnlyProcesses(dependency is null ? [] : [dependency]);
        options.RunningApplicationsPrompt = args.Has("no-prompt") ? null :
            (request, cancellation) => NativeUpdatePrompt.ShowRunningApplicationsAsync(request, configuration, profilePath, cancellation);
        var target = UpdaterTarget.FromInstalled(installed, configuration.StateRoot);
        await using var progress = new IncrementalProgress(statusPath);
        using var sessions = await TargetUpdateSessions.CreateAsync([target], new ScheduleConfiguration(), configuration.Isolated,
            interactive: !args.Has("no-prompt"), progress, token);
        var result = await sessions[target].RunAsync(client => client.ApplyPreparedFilesAsync(target, root, options, progress, token, runningTarget), token);
        await progress.CompleteAsync();
        await ApplicationUpdater.CompleteHandoffAsync(statePath, CancellationToken.None);
        await UpdaterSchedule.WriteJsonAsync(statusPath, new { status = "updated", result }, CancellationToken.None);
        if (!args.Has("background")) Print(result);
        return result.RebootRequired ? 3010 : 0;
    }
}
