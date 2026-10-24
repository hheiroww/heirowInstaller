using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

return await InstallerCli.RunAsync(args);

internal static class InstallerCli
{
    private static readonly string Help = """
heirowInstaller — modular Windows installation and publishing

  pack <manifest.json> --source <directory> --output <package.hinst> [--sign-key <private.pem>]
  bundle <package.hinst> --launcher <single-file.exe> --output <setup.exe>
  inspect <package.hinst|setup.exe>
  verify <package> [--trust-key <public-key-sha256>] [--require-signature]
  extract <package> --output <directory>
  plan|install <package> [--target <directory>] [--features core,tools] [--scope user|machine]
  repair|uninstall <product-id> [--scope user|machine]
  list [--state-root <directory>]
  check-update|update <product-id> [--feed <https-url>]
  import-msi <original.msi> --output <package.hinst> [--id <id>] [--name <name>] [--version <version>] [--publisher <name>]
  import-exe <original.exe> --id <id> --name <name> --version <version> --publisher <publisher> --install-args <json-array> --output <package.hinst>
  publish <package.hinst> [--publisher <https-origin>] [--token-env <name>|--cookie-env <name>]
  publisher-status|catalog [--publisher <https-origin>]

Maintenance options: --silent, --no-launch, --launch, --log <path>, --state-root <directory>,
  --allow-downgrade, --no-register, --trust-key <sha256>, --require-signature.
Validation options: --isolated (no OS integrations or external installers), --allow-loopback.
MSI compatibility retains original MSI tables and custom actions. Native authoring supports
  files, features, shortcuts, registry, environment, services, prerequisites, and explicit modules.
All machine-scope work requires an elevated process. Commands return 0 on success, 1 on failure,
  2 for invalid arguments, 130 for cancellation, and 3010 when a reboot is required.
""";

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            var parsed = new Arguments(args);
            if (parsed.Has("help") || parsed.Positionals.FirstOrDefault() is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
            if (parsed.Get("wait-for-pid") is { } wait)
            {
                if (!int.TryParse(wait, out var pid) || pid == System.Environment.ProcessId) throw new ArgumentException("--wait-for-pid requires a different process ID.");
                try { using var process = Process.GetProcessById(pid); await process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromMinutes(5), cancellation.Token); }
                catch (ArgumentException) { }
            }
            var ownPath = System.Environment.ProcessPath!;
            var command = parsed.Positionals.FirstOrDefault();
            string? bundled = null;
            if (command is null && File.Exists(ownPath) && PackageReader.HasEmbeddedPackage(ownPath))
            { bundled = ownPath; command ??= "install"; }
            if (command is null) { Console.WriteLine(Help); return 0; }
            var options = Options(parsed);
            var progress = new InlineProgress(value =>
            {
                if (!options.Silent) Console.Error.WriteLine($"[{value.Phase}] {value.Message} ({value.Completed}/{value.Total})");
            });
            string Subject() => bundled ?? parsed.Positionals.ElementAtOrDefault(1) ?? throw new ArgumentException($"{command} requires a package path or product ID.");
            switch (command.ToLowerInvariant())
            {
                case "pack":
                    Print(PackageBuilder.Build(Subject(), parsed.Require("source"), parsed.Require("output"), new() { SigningKeyPath = parsed.Get("sign-key") })); break;
                case "bundle":
                    BundleBuilder.Build(parsed.Require("launcher"), Subject(), parsed.Require("output"));
                    Print(new { output = Path.GetFullPath(parsed.Require("output")), sha256 = PackageSafety.HashFile(parsed.Require("output")) }); break;
                case "inspect":
                    using (var package = PackageReader.Open(Subject())) Print(new { manifest = package.Manifest, signed = package.Signature is not null, publisherKeySha256 = package.PublisherKeySha256 }); break;
                case "verify":
                    using (var package = PackageReader.Open(Subject()))
                    {
                        package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
                        await package.VerifyAsync(progress, cancellation.Token);
                        Print(new { valid = true, productId = package.Manifest.Id, version = package.Manifest.Version, signed = package.Signature is not null, publisherKeySha256 = package.PublisherKeySha256 });
                    }
                    break;
                case "extract":
                    using (var package = PackageReader.Open(Subject()))
                    {
                        package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
                        await package.ExtractVerifiedAsync(parsed.Require("output"), null, progress, cancellation.Token);
                    }
                    break;
                case "plan":
                    using (var package = PackageReader.Open(Subject())) Print(new InstallerEngine().Plan(package, options)); break;
                case "install":
                    using (var package = PackageReader.Open(Subject()))
                    {
                        var result = await new InstallerEngine().InstallAsync(package, options, progress, cancellation.Token);
                        Print(result); return result.RebootRequired ? 3010 : 0;
                    }
                case "repair":
                {
                    var result = await new InstallerEngine().RepairAsync(Subject(), options, progress, cancellation.Token);
                    Print(result); return result.RebootRequired ? 3010 : 0;
                }
                case "uninstall":
                {
                    var result = await new InstallerEngine().UninstallAsync(Subject(), options, progress, cancellation.Token);
                    Print(result); return result.RebootRequired ? 3010 : 0;
                }
                case "list": Print(InstallerEngine.ListInstalled(options.StateRoot)); break;
                case "check-update":
                case "update":
                {
                    var id = Subject();
                    var product = InstallerEngine.ListInstalled(options.StateRoot).SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                                  ?? throw new InvalidOperationException("Product is not installed: " + id);
                    var feed = parsed.Get("feed") ?? product.UpdateUrl ?? throw new InvalidOperationException("Installed product has no update feed.");
                    using var client = new UpdateClient(parsed.Has("allow-loopback"), transport: UpdateTransport(parsed));
                    var check = await client.CheckAsync(feed, product.Version, product.Id, cancellation.Token);
                    if (command == "check-update" || !check.Available) { Print(check); break; }
                    var download = await client.DownloadAsync(check.Feed, parsed.Get("download-directory") ?? Path.Combine(Path.GetTempPath(), "heirowInstaller", "updates"), progress, cancellation.Token);
                    options.Scope = product.Scope;
                    if (!product.UseOriginalInstallDirectory) options.TargetDirectory ??= product.InstallDirectory;
                    options.Features ??= product.Features;
                    var result = await client.ApplyAsync(download, check.Feed, options, progress, cancellation.Token);
                    Print(result); return result.RebootRequired ? 3010 : 0;
                }
                case "import-msi":
                    Print(LegacyImporter.ImportMsi(Subject(), parsed.Require("output"), parsed.Get("id"), parsed.Get("name"), parsed.Get("version"), parsed.Get("publisher"), parsed.Get("scope") ?? "machine", new() { SigningKeyPath = parsed.Get("sign-key") })); break;
                case "import-exe":
                    Print(ImportExecutable(Subject(), parsed)); break;
                case "publish":
                    using (var package = PackageReader.Open(Subject()))
                    {
                        package.VerifySignature(options.TrustedPublisherKeySha256, options.RequireSignature);
                        await package.VerifyAsync(progress, cancellation.Token);
                    }
                    using (var publisher = Publisher(parsed)) Print(await publisher.PublishAsync(Subject(), cancellation.Token)); break;
                case "publisher-status":
                    using (var publisher = Publisher(parsed)) Print(await publisher.GetStatusAsync(cancellation.Token)); break;
                case "catalog":
                    using (var publisher = Publisher(parsed)) Print(await publisher.GetCatalogAsync(cancellation.Token)); break;
                default: throw new ArgumentException("Unknown command: " + command + ". Use --help.");
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operation cancelled; native transactional changes were rolled back. External prerequisites retain their own state."); return 130; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static InstallOptions Options(Arguments args) => new()
    {
        TargetDirectory = args.Get("target"), StateRoot = args.Get("state-root"), Scope = args.Get("scope"),
        Features = args.Get("features")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
        AllowDowngrade = args.Has("allow-downgrade"), RegisterProduct = !args.Has("no-register"),
        Silent = args.Has("silent"), Launch = args.Has("launch") && !args.Has("no-launch"), LogPath = args.Get("log"),
        RequireSignature = args.Has("require-signature"), TrustedPublisherKeySha256 = args.Get("trust-key"),
        Isolated = args.Has("isolated"), InstallerPath = AppHostPath(), LegacyArguments = args.BackendArguments.ToList()
    };
    private static string? AppHostPath()
    {
        var process = System.Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process).StartsWith("heirowInstaller", StringComparison.OrdinalIgnoreCase)) return process;
        var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "heirowInstaller.Cli.exe" : "heirowInstaller.Cli");
        return File.Exists(apphost) ? apphost : null;
    }
    private static PublisherClient Publisher(Arguments args)
    {
        string? Secret(string option)
        {
            var variable = args.Get(option);
            if (variable is null) return null;
            var value = System.Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The configured publisher credential environment variable is empty.");
            return value;
        }
        return new(new Uri(args.Get("publisher") ?? "https://socketjack.com"), Secret("token-env"), Secret("cookie-env"), args.Has("allow-loopback"));
    }
    private static SocketJackUpdateTransport UpdateTransport(Arguments args)
    {
        string? Secret(string option)
        {
            if (args.Get(option) is not { } name) return null;
            return System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
                : throw new ArgumentException("A configured updater credential environment variable is empty.");
        }
        return new(new Uri(args.Get("credential-origin") ?? "https://socketjack.com"), Secret("cookie-env"), Secret("token-env"));
    }
    private static PackageManifest ImportExecutable(string input, Arguments args)
    {
        input = Path.GetFullPath(input);
        if (!input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("import-exe requires an .exe installer.");
        List<string> ArgumentsArray(string option, bool required = false) => JsonSerializer.Deserialize<List<string>>(required ? args.Require(option) : args.Get(option) ?? "[]", InstallerJson.Options) ?? throw new ArgumentException("Installer arguments must be JSON arrays.");
        var manifest = new PackageManifest
        {
            Id = args.Require("id"), Name = args.Require("name"), Version = args.Require("version"), Publisher = args.Require("publisher"), Scope = args.Get("scope") ?? "machine",
            Payload = [new() { Source = Path.GetFileName(input), Path = Path.GetFileName(input) }],
            LegacyExecutable = new() { PayloadPath = Path.GetFileName(input), InstallArguments = ArgumentsArray("install-args", true), SilentInstallArguments = args.Get("silent-install-args") is not null ? ArgumentsArray("silent-install-args") : null, InstallDirectoryArgument = args.Get("directory-argument"), RepairArguments = ArgumentsArray("repair-args"), UninstallArguments = ArgumentsArray("uninstall-args"), UninstallExecutable = args.Get("uninstall-executable") }
        };
        var temporary = Path.Combine(Path.GetTempPath(), "heirowInstaller-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var path = Path.Combine(temporary, "manifest.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            return PackageBuilder.Build(path, Path.GetDirectoryName(input)!, args.Require("output"), new() { SigningKeyPath = args.Get("sign-key") });
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, InstallerJson.Options));
    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress> { public void Report(InstallProgress value) => report(value); }
    private sealed class Arguments
    {
        private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = [];
        public List<string> BackendArguments { get; } = [];
        public Arguments(string[] args)
        {
            var flags = new HashSet<string>(["help", "silent", "launch", "no-launch", "no-register", "allow-downgrade", "isolated", "require-signature", "allow-loopback", "json"], StringComparer.OrdinalIgnoreCase);
            var values = new HashSet<string>(["source", "output", "sign-key", "launcher", "trust-key", "target", "state-root", "features", "scope", "log", "feed", "download-directory", "id", "name", "version", "publisher", "token-env", "cookie-env", "credential-origin", "install-args", "silent-install-args", "directory-argument", "repair-args", "uninstall-args", "uninstall-executable", "wait-for-pid"], StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is "-h" or "/?") { options["help"] = null; continue; }
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Positionals.Add(args[i]); continue; }
                var key = args[i][2..];
                if (key.Equals("backend-arg", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { BackendArguments.Add(args[++i]); continue; }
                if (flags.Contains(key)) options[key] = null;
                else if (values.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[key] = args[++i];
                else throw new ArgumentException("Unknown option or missing value: --" + key);
            }
        }
        public string? Get(string key) => options.GetValueOrDefault(key);
        public bool Has(string key) => options.ContainsKey(key);
        public string Require(string key) => Get(key) ?? throw new ArgumentException("Required option: --" + key);
    }
}
