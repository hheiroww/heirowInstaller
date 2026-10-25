using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

namespace HeirowUpdater;

internal static class ApplicationCommands
{
    internal static async Task<int> RunAsync(string command, UpdaterProgram.Arguments args, CancellationToken token)
    {
        if (command is "repair" or "uninstall") return await MaintainAsync(command, args, token);
        var configurationPath = Path.GetFullPath(args.Require("config"));
        var configuration = ApplicationUpdater.ReadConfiguration(configurationPath, args.Has("allow-loopback"), requireEntryPoint: command is not ("after-exit" or "stop"));
        if (command == "stop")
        {
            var found = EventWaitHandle.TryOpenExisting(UpdateCompanionControl.GetStopEventName(configuration.InstallDirectory), out var signal);
            if (found && signal is not null) { using (signal) signal.Set(); }
            Console.WriteLine(JsonSerializer.Serialize(new { status = found ? "stop-requested" : "not-watching", productId = configuration.ProductId }, InstallerJson.Options));
            return 0;
        }
        string? Acceptance(string key, string variable) => args.Get(key) ?? (configuration.Isolated ? System.Environment.GetEnvironmentVariable(variable) : null);
        var acceptanceChoice = Acceptance("prompt-choice", "HEIROWUPDATER_TEST_CHOICE");
        var capture = Acceptance("capture", "HEIROWUPDATER_TEST_CAPTURE");
        var promptReport = Acceptance("prompt-report", "HEIROWUPDATER_TEST_PROMPT_REPORT");
        string? PromptChoice()
        {
            var file = configuration.Isolated ? System.Environment.GetEnvironmentVariable("HEIROWUPDATER_TEST_CHOICE_FILE") : null;
            return file is not null && File.Exists(file) ? File.ReadAllText(file).Trim() : acceptanceChoice;
        }
        if (acceptanceChoice is not null && !configuration.Isolated) throw new ArgumentException("Automated prompt choices require an explicit isolated acceptance fixture.");
        if (command == "launch")
        {
            var root = configuration.InstallDirectory;
            var start = new ProcessStartInfo(PackageSafety.UnderRoot(root, configuration.EntryPoint)) { UseShellExecute = false, WorkingDirectory = root };
            foreach (var argument in args.ApplicationArguments) start.ArgumentList.Add(argument);
            using var application = Process.Start(start) ?? throw new InvalidOperationException("The application did not start.");
            var watcher = new ProcessStartInfo(Path.Combine(root, "heirowUpdater.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            watcher.ArgumentList.Add("after-exit");
            void Value(string name, string value) { watcher.ArgumentList.Add(name); watcher.ArgumentList.Add(value); }
            Value("--config", configurationPath); Value("--application-pid", application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Value("--application-start", application.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var flag in new[] { "allow-loopback", "isolated" }) if (args.Has(flag)) watcher.ArgumentList.Add("--" + flag);
            foreach (var option in new[] { "status-file", "prompt-choice", "capture", "prompt-report" }) if (args.Get(option) is { } value) Value("--" + option, value);
            using var companion = Process.Start(watcher) ?? throw new InvalidOperationException("The optional application updater did not start.");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "launched", applicationPid = application.Id, applicationStart = application.StartTime.ToUniversalTime().Ticks, companionPid = companion.Id }, InstallerJson.Options));
            return 0;
        }
        int? pid = args.Get("application-pid") is { } pidText ? int.Parse(pidText, System.Globalization.CultureInfo.InvariantCulture) : null;
        long? ticks = args.Get("application-start") is { } ticksText ? long.Parse(ticksText, System.Globalization.CultureInfo.InvariantCulture) : null;
        var options = new ApplicationLifecycleOptions { AllowLoopback = args.Has("allow-loopback"), NoPrompt = args.Has("no-prompt"),
            ApplicationHost = args.Get("application-host"), StatusPath = Acceptance("status-file", "HEIROWUPDATER_TEST_STATUS_FILE"), DownloadDirectory = args.Get("download-directory"),
            RunningApplicationsPrompt = (request, cancellation) => NativeUpdatePrompt.ShowRunningApplicationsAsync(request, configuration, configurationPath, cancellation),
            AuthenticateAsync = async (profile, cancellation) =>
            {
                if (!await NativeUpdatePrompt.SignInAsync(profile, configurationPath, cancellation)) return null;
                using var approval = await PublisherSignInClient.BeginAsync(new Uri(profile.CredentialOrigin), cancellation, profile.Isolated, profile.AuthApplication!);
                Process.Start(new ProcessStartInfo(approval.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                using var session = await approval.CompleteAsync(cancellation);
                return session.AccessToken;
            } };
        try
        {
            using var result = await ApplicationUpdater.RunAsync(configurationPath, pid, ticks, command == "check-for-updates", options,
                (context, cancellation) => NativeUpdatePrompt.ShowAsync(context, PromptChoice(), capture, promptReport, cancellation, configurationPath), token);
            if (result.Handoff is not null)
            {
                using var helper = Process.Start(result.Handoff) ?? throw new InvalidOperationException("Windows did not start the trusted application update helper.");
                try
                {
                    // Retain the original named-event handle until the copied helper
                    // acknowledges its own observer, including cold bundle extraction.
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                    while (!File.Exists(result.HandoffReadyPath))
                    {
                        if (helper.HasExited) throw new InvalidOperationException("The trusted update helper exited before accepting maintenance cancellation.");
                        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The trusted update helper did not become ready in time.");
                        await Task.Delay(100, token);
                    }
                    if (result.StopMonitor?.StopRequested == true)
                    {
                        result.Status = "stopped";
                        if (options.StatusPath is { } statusPath) await UpdaterSchedule.WriteJsonAsync(statusPath, result, CancellationToken.None);
                    }
                    else await ApplicationUpdater.MarkHandoffAsync(result.LifecycleStatePath!, helper, token);
                }
                catch { result.StopMonitor?.RequestStop(); throw; }
            }
            Console.WriteLine(JsonSerializer.Serialize(result, InstallerJson.Options));
            return 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (options.StatusPath is { } statusPath)
                await UpdaterSchedule.WriteJsonAsync(statusPath, new { status = "error", productId = configuration.ProductId, error = error.Message }, CancellationToken.None);
            if (!options.NoPrompt && !configuration.Isolated) await NativeUpdatePrompt.ShowFailureAsync(configuration, configurationPath, error, token);
            throw;
        }
    }

    private static async Task<int> MaintainAsync(string command, UpdaterProgram.Arguments args, CancellationToken token)
    {
        var id = args.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("Choose the installed application ID.");
        var product = InstallerEngine.ListInstalled(args.Get("state-root")).FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The application is not installed.");
        if (product.Scope == "machine" && !product.Isolated && !InstallerEngine.IsElevated)
        {
            var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(command); start.ArgumentList.Add(id);
            foreach (var flag in new[] { "silent", "no-prompt" }) if (args.Has(flag)) start.ArgumentList.Add("--" + flag);
            if (args.Get("state-root") is { } root) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(root); }
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start application maintenance.");
            await child.WaitForExitAsync(token); return child.ExitCode;
        }
        var identity = NativeUpdatePrompt.MaintenanceIdentity(product, args.Get("state-root"));
        var options = new InstallOptions { StateRoot = args.Get("state-root"), Scope = product.Scope, Isolated = product.Isolated, Silent = args.Has("silent"), InstallerPath = System.Environment.ProcessPath,
            RunningApplicationsPrompt = args.Has("silent") || args.Has("no-prompt") ? null
                : (request, cancellation) => NativeUpdatePrompt.ShowRunningApplicationsAsync(request, identity, null, cancellation) };
        var result = command == "repair" ? await new InstallerEngine().RepairAsync(id, options, cancellationToken: token) : await new InstallerEngine().UninstallAsync(id, options, cancellationToken: token);
        return result.RebootRequired ? 3010 : 0;
    }
}
