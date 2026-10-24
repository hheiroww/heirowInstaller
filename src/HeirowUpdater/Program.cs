using System.Text.Json;
using System.Diagnostics;
using HeirowInstaller.Core;
using HeirowUpdater;
using HeirowUpdater.SocketJack;

return await UpdaterProgram.RunAsync(args);

internal static class UpdaterProgram
{
    private const string Help = """
heirowUpdater — application updates over the SocketJack network library

  check <product-id>|--all [--state-root <directory>] [--json]
  update <product-id>|--all [--trust-key <sha256>] [--allow-unsigned]
  check|update --feed <https-url> --product-id <id> --current-version <version>
  check|update --profile <application-profile.json>
  watch --all [--interval-minutes 60] [--apply]
  enable [--interval-minutes 60] [--apply]
  disable
  status [--json]
  launch --config <app-root/heirowUpdater.json> -- [application arguments]
  after-exit --config <app-root/heirowUpdater.json> --application-pid <pid> --application-start <UTC ticks>
  check-for-updates --config <app-root/heirowUpdater.json> [--json --no-prompt]
  stop --config <app-root/heirowUpdater.json>

Options: --target <directory>, --scope user|machine, --state-root <directory>,
  --download-directory <directory>, --log <path>, --silent, --json,
  --token-env <environment-variable-name>, --cookie-env <environment-variable-name>,
  --credential-origin <https-origin> (default https://socketjack.com),
  --allow-loopback and --isolated for local acceptance only.

Checks do not install updates. Enable schedules per-user checks at sign-in and every hour.
--apply opts into background installation and requires a pinned publisher key by default.
--allow-unsigned explicitly permits hash-verified packages without a publisher signature.
Credentials remain in environment variables and are sent only to their exact configured origin.
""";
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        string? helperStatusPath = null;
        string? helperLifecycleStatePath = null;
        try
        {
            var parsed = new Arguments(args);
            var command = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
            if (command is null || command == "help" || parsed.Has("help")) { Console.WriteLine(Help); return 0; }
            if (command == "show-prompt") return await NativeUpdatePrompt.RunChildAsync(parsed, cancellation.Token);
            if (command == "apply-bundle") { helperStatusPath = parsed.Get("status-file"); helperLifecycleStatePath = parsed.Get("lifecycle-state"); return await ApplyBundleAsync(parsed, cancellation.Token); }
            if (command is "launch" or "after-exit" or "check-for-updates" or "repair" or "uninstall" or "stop")
                return await ApplicationCommands.RunAsync(command, parsed, cancellation.Token);
            var configuration = parsed.Get("config") is { } configPath
                ? UpdaterSchedule.ReadConfiguration(configPath) ?? (command is "enable" or "status" or "disable" ? new ScheduleConfiguration() : throw new FileNotFoundException("Updater configuration does not exist."))
                : new ScheduleConfiguration();
            Merge(configuration, parsed);
            switch (command)
            {
                case "enable": await UpdaterSchedule.EnableAsync(configuration, cancellation.Token, parsed.Get("task-name"), parsed.Get("config")); Print(await UpdaterSchedule.StatusAsync(cancellation.Token, parsed.Get("task-name"), parsed.Get("config"))); return 0;
                case "disable": await UpdaterSchedule.DisableAsync(cancellation.Token, parsed.Get("task-name")); Print(await UpdaterSchedule.StatusAsync(cancellation.Token, parsed.Get("task-name"), parsed.Get("config"))); return 0;
                case "status": Print(await UpdaterSchedule.StatusAsync(cancellation.Token, parsed.Get("task-name"), parsed.Get("config"))); return 0;
                case "check": case "update": case "watch": break;
                default: throw new ArgumentException("Unknown updater command. Use --help.");
            }
            string? Secret(string? name)
            {
                if (name is null) return null;
                return System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
                    : throw new InvalidOperationException("A configured updater credential environment variable is empty.");
            }
            using var updater = new SocketJackUpdater(parsed.Has("allow-loopback"), new Uri(configuration.CredentialOrigin),
                Secret(configuration.CookieEnvironmentVariable), Secret(configuration.TokenEnvironmentVariable));
            var policy = new UpdaterPolicy { AllowUnsigned = configuration.AllowUnsigned, TrustedPublisherKeySha256 = configuration.TrustedPublisherKeySha256,
                DownloadDirectory = parsed.Get("download-directory"), Isolated = parsed.Has("isolated"), LogPath = parsed.Get("log") };
            var progress = new InlineProgress(value => { if (!parsed.Has("silent") && !parsed.Has("background") && !parsed.Has("json")) Console.Error.WriteLine($"[{value.Phase}] {value.Message}"); });
            string? lastMeaningfulState = null;
            var appliedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                var targets = Targets(parsed, configuration);
                var results = new List<object>(); var failed = false;
                foreach (var target in targets)
                {
                    if (appliedVersions.TryGetValue(target.ProductId, out var appliedVersion) && PackageVersion.Parse(appliedVersion).CompareTo(PackageVersion.Parse(target.CurrentVersion)) > 0)
                        target.CurrentVersion = appliedVersion;
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var previousVersion = target.CurrentVersion;
                        var check = await updater.CheckAsync(target, cancellation.Token);
                        InstallResult? applied = null;
                        var apply = command == "update" || ((command == "watch" || parsed.Has("background")) && configuration.Apply);
                        if (apply && check.Available && IsCurrentApplication(target))
                        {
                            using var current = Process.GetCurrentProcess();
                            var pid = parsed.Get("application-pid") is { } suppliedPid ? int.Parse(suppliedPid, System.Globalization.CultureInfo.InvariantCulture) : current.Id;
                            var ticks = parsed.Get("application-start") is { } suppliedStart ? long.Parse(suppliedStart, System.Globalization.CultureInfo.InvariantCulture) : current.StartTime.ToUniversalTime().Ticks;
                            var handoff = await updater.PrepareInstallerHandoffAsync(target, check, pid, ticks, policy, progress, cancellation.Token);
                            using var helper = Process.Start(handoff) ?? throw new InvalidOperationException("The trusted update helper did not start.");
                            results.Add(new { productId = target.ProductId, installDirectory = target.InstallDirectory, status = "update-staged", requiresApplicationExit = true, helperProcessId = helper.Id });
                            await UpdaterSchedule.WriteJsonAsync(parsed.Get("status-file") ?? UpdaterSchedule.LastCheckPath, new { checkedAt = DateTimeOffset.UtcNow, transport = "SocketJack", products = results }, cancellation.Token);
                            if (!parsed.Has("background")) Print(results);
                            return 0;
                        }
                        if (apply && check.Available) applied = await updater.ApplyAsync(target, check, policy, progress, cancellation.Token);
                        if (applied is not null) appliedVersions[target.ProductId] = target.CurrentVersion;
                        var needsElevation = check.Available && applied is null && target.Scope == "machine" && !InstallerEngine.IsElevated;
                        results.Add(new { productId = target.ProductId, name = target.Name, currentVersion = previousVersion,
                            latestVersion = check.Feed.Version, available = check.Available && applied is null, requiresUserAction = needsElevation,
                            status = applied is not null ? "updated" : needsElevation ? "requires-elevation" : check.Available ? "update-available" : "current", result = applied });
                    }
                    catch (UpdateRequiresElevationException) when (command == "update" && !parsed.Has("background") && !parsed.Has("no-elevate") && !parsed.Has("elevated-child"))
                    {
                        var code = await ElevateAsync(args, cancellation.Token);
                        results.Add(new { productId = target.ProductId, status = code == 0 ? "completed-as-administrator" : "elevated-update-failed", requiresUserAction = code != 0 });
                        Print(results); return code;
                    }
                    catch (UpdateRequiresElevationException ex)
                    {
                        results.Add(new { productId = target.ProductId, status = "requires-elevation", available = true, requiresUserAction = true, error = ex.Message });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed = true; results.Add(new { productId = target.ProductId, name = target.Name, currentVersion = target.CurrentVersion, available = false, status = "error", error = ex.Message });
                    }
                }
                var status = JsonSerializer.Serialize(results, InstallerJson.Options);
                await UpdaterSchedule.WriteJsonAsync(parsed.Get("status-file") ?? UpdaterSchedule.LastCheckPath, new { checkedAt = DateTimeOffset.UtcNow, transport = "SocketJack", products = results }, cancellation.Token);
                if (!parsed.Has("background") && (command != "watch" || lastMeaningfulState != status)) Console.WriteLine(status);
                lastMeaningfulState = status;
                if (command != "watch") return failed ? 1 : 0;
                await Task.Delay(TimeSpan.FromMinutes(configuration.IntervalMinutes), cancellation.Token);
            } while (true);
        }
        catch (OperationCanceledException)
        {
            if (helperLifecycleStatePath is not null) await ApplicationUpdater.CompleteHandoffAsync(helperLifecycleStatePath, CancellationToken.None);
            if (helperStatusPath is not null) await UpdaterSchedule.WriteJsonAsync(helperStatusPath, new { status = "stopped" }, CancellationToken.None);
            return 130;
        }
        catch (Exception ex)
        {
            if (helperStatusPath is not null)
            {
                try { await UpdaterSchedule.WriteJsonAsync(helperStatusPath, new { status = "error", error = ex.Message }, CancellationToken.None); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else
            {
                Console.Error.WriteLine(ex.Message);
                if (args.Contains("--json", StringComparer.OrdinalIgnoreCase)) Print(new { status = "error", error = ex.Message });
            }
            return ex is ArgumentException ? 2 : 1;
        }
    }
    private static IReadOnlyList<UpdaterTarget> Targets(Arguments args, ScheduleConfiguration configuration)
    {
        if (args.Get("profile") is { } profile)
        {
            PackageSafety.EnsureNoReparsePoints(profile);
            return [RefreshInstalled(JsonSerializer.Deserialize<UpdaterTarget>(File.ReadAllText(profile), InstallerJson.Options) ?? throw new InvalidDataException("Application updater profile is empty."))];
        }
        if (args.Get("feed") is { } feed)
            return [RefreshInstalled(new() { ProductId = args.Require("product-id"), Name = args.Get("product-id")!, CurrentVersion = args.Require("current-version"),
                FeedUrl = feed, InstallDirectory = args.Get("target"), StateRoot = configuration.StateRoot, Scope = args.Get("scope") ?? "user", PublisherKeySha256 = configuration.TrustedPublisherKeySha256 })];
        var installed = InstallerEngine.ListInstalled(configuration.StateRoot).Where(x => !string.IsNullOrWhiteSpace(x.UpdateUrl));
        if (args.Get("scope") is { } scope) installed = installed.Where(x => x.Scope == scope);
        if (!args.Has("all"))
        {
            var id = args.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("Select a product ID, --all, --profile, or a standalone --feed.");
            installed = installed.Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (!installed.Any()) throw new InvalidOperationException("The selected application is not installed or has no update feed.");
        }
        return installed.OrderBy(x => x.Scope == "user" ? 0 : 1).Select(x => UpdaterTarget.FromInstalled(x, configuration.StateRoot)).ToList();
    }
    private static UpdaterTarget RefreshInstalled(UpdaterTarget target)
    {
        var installed = InstallerEngine.ListInstalled(target.StateRoot).FirstOrDefault(x => x.Id.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == target.Scope);
        if (installed is not null)
        {
            if (PackageVersion.Parse(installed.Version).CompareTo(PackageVersion.Parse(target.CurrentVersion)) > 0) target.CurrentVersion = installed.Version;
            target.PublisherKeySha256 ??= installed.PublisherKeySha256;
            target.InstallDirectory ??= installed.UseOriginalInstallDirectory ? null : installed.InstallDirectory;
            target.Features ??= installed.Features;
        }
        return target;
    }
    private static void Merge(ScheduleConfiguration config, Arguments args)
    {
        if (args.Get("interval-minutes") is { } interval)
        {
            if (!int.TryParse(interval, out var value) || value is < 15 or > 1440) throw new ArgumentException("Update check interval must be 15-1440 minutes.");
            config.IntervalMinutes = value;
        }
        config.Apply |= args.Has("apply"); config.AllowUnsigned |= args.Has("allow-unsigned");
        config.StateRoot = args.Get("state-root") ?? config.StateRoot;
        config.TrustedPublisherKeySha256 = args.Get("trust-key") ?? config.TrustedPublisherKeySha256;
        config.CookieEnvironmentVariable = args.Get("cookie-env") ?? config.CookieEnvironmentVariable;
        config.TokenEnvironmentVariable = args.Get("token-env") ?? config.TokenEnvironmentVariable;
        config.CredentialOrigin = args.Get("credential-origin") ?? config.CredentialOrigin;
    }
    private static async Task<int> ElevateAsync(string[] arguments, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Administrator handoff requires Windows.");
        var executable = UpdaterHostResolver.FindExecutable() ?? throw new FileNotFoundException("The updater apphost is missing; run the published heirowUpdater executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--scope"); start.ArgumentList.Add("machine");
        start.ArgumentList.Add("--elevated-child");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Administrator update handoff did not start.");
        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }
    private static bool IsCurrentApplication(UpdaterTarget target)
    {
        if (target.InstallDirectory is null) return false;
        return UpdaterHostResolver.IsProcessInsideDirectory(target.InstallDirectory);
    }
    private static async Task<int> ApplyBundleAsync(Arguments args, CancellationToken token)
    {
        var path = args.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException("apply-bundle requires a package path.");
        var expectedId = args.Require("product-id"); var expectedVersion = args.Require("expected-version");
        var lifecycle = args.Get("lifecycle-config") is { } lifecyclePath
            ? ApplicationUpdater.ReadConfiguration(lifecyclePath, args.Has("allow-loopback")) : null;
        using var lifecycleCancellation = lifecycle is not null
            ? new ApplicationStopMonitor(lifecycle.InstallDirectory, resetForOwner: false, token) : null;
        if (lifecycleCancellation is not null) token = lifecycleCancellation.Token;
        if (lifecycle is not null)
        {
            if (!lifecycle.ProductId.Equals(expectedId, StringComparison.OrdinalIgnoreCase) || !PackageSafety.HashEquals(lifecycle.PublisherKeySha256, args.Require("trust-key")) ||
                !Path.GetFullPath(args.Require("target")).Equals(lifecycle.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(args.Require("state-root")).Equals(Path.GetFullPath(lifecycle.StateRoot), StringComparison.OrdinalIgnoreCase) || args.Require("scope") != lifecycle.Scope)
                throw new InvalidDataException("The app-local update handoff does not match its installed publisher and product.");
            if (args.Get("lifecycle-ready") is { } readyPath)
                await UpdaterSchedule.WriteJsonAsync(readyPath, new { status = "ready", helperProcessId = System.Environment.ProcessId }, CancellationToken.None);
        }
        if (args.Get("wait-pid") is { } waiting)
        {
            var pid = int.Parse(waiting, System.Globalization.CultureInfo.InvariantCulture);
            var ticks = long.Parse(args.Require("wait-start"), System.Globalization.CultureInfo.InvariantCulture);
            if (pid <= 0 || pid == System.Environment.ProcessId || ticks <= 0) throw new ArgumentException("Invalid update handoff process identity.");
            try
            {
                using var previous = Process.GetProcessById(pid);
                if (previous.StartTime.ToUniversalTime().Ticks == ticks)
                    await previous.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(2), token);
            }
            catch (ArgumentException) { }
        }
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(path), args.Require("sha256"))) throw new System.Security.Cryptography.CryptographicException("Staged update hash mismatch.");
        if (lifecycle is not null)
        {
            await ApplicationProcesses.WaitForExitAsync(PackageSafety.UnderRoot(lifecycle.InstallDirectory, lifecycle.EntryPoint), token, args.Get("application-host"), () => ApplicationHostRegistry.Paths(lifecycle));
        }
        using var package = PackageReader.Open(path);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(path), args.Require("sha256"))) throw new System.Security.Cryptography.CryptographicException("Staged update changed while waiting for the application to close.");
        var key = args.Get("trust-key");
        package.VerifySignature(key, key is not null || !args.Has("allow-unsigned"));
        if (!package.Manifest.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != expectedVersion)
            throw new InvalidDataException("Staged update package identity mismatch.");
        await package.VerifyAsync(null, token);
        var prior = InstallerEngine.ListInstalled(args.Get("state-root")).FirstOrDefault(x => x.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase) && x.Scope == (args.Get("scope") ?? "user"));
        if (lifecycleCancellation?.StopRequested == true) throw new OperationCanceledException("Application maintenance stopped this update.", token);
        var result = await new InstallerEngine().InstallAsync(package, new()
        {
            TargetDirectory = args.Get("target"), StateRoot = args.Get("state-root"), Scope = args.Get("scope"),
            TrustedPublisherKeySha256 = key, RequireSignature = key is not null || !args.Has("allow-unsigned"),
            Features = prior?.Features, Silent = true, Launch = false, InstallerPath = args.Get("maintenance-host"),
            RegisterProduct = prior?.RegisteredProduct ?? true, Isolated = args.Has("isolated"), LogPath = args.Get("log"),
            CompanionStopAlreadyHandled = lifecycle is not null
        }, null, token);
        if (args.Get("lifecycle-state") is { } lifecycleState) await ApplicationUpdater.CompleteHandoffAsync(lifecycleState, CancellationToken.None);
        if (args.Get("status-file") is { } statusPath) await UpdaterSchedule.WriteJsonAsync(statusPath, new { status = "updated", result }, CancellationToken.None);
        if (!args.Has("background")) Print(result);
        return result.RebootRequired ? 3010 : 0;
    }
    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, InstallerJson.Options));
    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress> { public void Report(InstallProgress value) => report(value); }
    internal sealed class Arguments
    {
        private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = [];
        public List<string> ApplicationArguments { get; } = [];
        public Arguments(string[] args)
        {
            var flags = new HashSet<string>(["all", "apply", "allow-unsigned", "allow-loopback", "isolated", "json", "silent", "help", "background", "no-elevate", "elevated-child", "no-prompt"], StringComparer.OrdinalIgnoreCase);
            var values = new HashSet<string>(["state-root", "trust-key", "target", "scope", "feed", "product-id", "current-version", "profile", "download-directory", "interval-minutes", "log", "token-env", "cookie-env", "credential-origin", "config", "status-file", "task-name", "application-pid", "application-start", "application-host", "sha256", "expected-version", "wait-pid", "wait-start", "maintenance-host", "lifecycle-config", "lifecycle-state", "lifecycle-ready", "prompt-request", "prompt-choice", "capture", "prompt-report"], StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--") { ApplicationArguments.AddRange(args.Skip(i + 1)); break; }
                if (args[i] is "-h" or "/?") { options["help"] = null; continue; }
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Positionals.Add(args[i]); continue; }
                var key = args[i][2..];
                if (flags.Contains(key)) options[key] = null;
                else if (values.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[key] = args[++i];
                else throw new ArgumentException("Unknown option or missing value: --" + key);
            }
        }
        public bool Has(string key) => options.ContainsKey(key);
        public string? Get(string key) => options.GetValueOrDefault(key);
        public string Require(string key) => Get(key) ?? throw new ArgumentException("Required option: --" + key);
    }
}
