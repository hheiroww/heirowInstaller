using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HeirowInstaller.Core;

internal enum NativeBackendState { Absent, Installed, Uncertain }

internal interface INativeMigrationBackend
{
    NativeBackendState ObserveDeclared(NativeMigrationBaselineDefinition definition, bool isolated);
    NativeBackendState Observe(NativeMigrationBaseline baseline);
    Task<string> BootIdentityAsync(CancellationToken token);
    Task<int> UninstallAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token);
    Task<int> RestoreAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token);
}

internal sealed class WindowsNativeMigrationBackend : INativeMigrationBackend
{
    public NativeBackendState ObserveDeclared(NativeMigrationBaselineDefinition definition, bool isolated)
    {
        if (isolated) throw new InvalidOperationException("Isolated validation cannot inspect a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        return ObserveWindows(definition, expectedBurnPath: null);
    }
    public NativeBackendState Observe(NativeMigrationBaseline baseline)
    {
        if (baseline.Product.Isolated) throw new InvalidOperationException("Isolated validation cannot inspect a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        return ObserveWindows(baseline.Authorization, baseline.Authorization.Backend == "burn" ? baseline.Product.BackendRegistrations.Single().Values["path"] : null);
    }

    [SupportedOSPlatform("windows")]
    private static NativeBackendState ObserveWindows(NativeMigrationBaselineDefinition definition, string? expectedBurnPath)
    {
        if (definition.Backend == "msi")
            return ObserveMsi(definition.Scope, definition.MsiProductCode!);
        var registration = definition.BurnRegistration!;
        using (var hive = WindowsIntegration.Hive(registration.Hive, registration.View))
        using (var root = hive.OpenSubKey(BackendMaintenance.UninstallRoot.TrimEnd('\\')))
        {
            var matches = new List<string>();
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                using var key = root!.OpenSubKey(name);
                var value = key?.GetValue("BundleUpgradeCode");
                var codes = value is string[] multiple ? multiple : value is string single ? [single] : [];
                if (codes.Any(code => NativeMigrationValidation.SameGuid(code, registration.BundleUpgradeCode)))
                    matches.Add(BackendMaintenance.UninstallRoot + name);
            }
            if (matches.Count == 0)
            {
                using var exact = expectedBurnPath is null ? null : hive.OpenSubKey(expectedBurnPath);
                return CombineBurnState(exact is null ? NativeBackendState.Absent : NativeBackendState.Uncertain,
                    definition.BurnMsiProductCodes!.Select(code => ObserveMsi(definition.Scope, code)));
            }
            var bundleState = matches.Count == 1 && (expectedBurnPath is null || matches[0].Equals(expectedBurnPath, StringComparison.OrdinalIgnoreCase))
                ? NativeBackendState.Installed : NativeBackendState.Uncertain;
            return CombineBurnState(bundleState, definition.BurnMsiProductCodes!.Select(code => ObserveMsi(definition.Scope, code)));
        }
    }

    [SupportedOSPlatform("windows")]
    private static NativeBackendState ObserveMsi(string scope, string productCode)
    {
        var code = Guid.Parse(productCode).ToString("B").ToUpperInvariant();
        var registrations = 0;
        foreach (var view in new[] { "registry64", "registry32" })
        {
            using var hive = WindowsIntegration.Hive(scope, view);
            using var key = hive.OpenSubKey(BackendMaintenance.UninstallRoot + code);
            if (key is not null) registrations++;
        }
        var state = MsiQueryProductState(code);
        // ABSENT means installed for another user; only UNKNOWN plus no registration proves absence.
        return state == 5 && registrations == 1 ? NativeBackendState.Installed : state == -1 && registrations == 0 ? NativeBackendState.Absent : NativeBackendState.Uncertain;
    }

    internal static NativeBackendState CombineBurnState(NativeBackendState bundle, IEnumerable<NativeBackendState> children)
    {
        var states = children.ToArray();
        return bundle == NativeBackendState.Installed && states.All(state => state == NativeBackendState.Installed) ? NativeBackendState.Installed
            : bundle == NativeBackendState.Absent && states.All(state => state == NativeBackendState.Absent) ? NativeBackendState.Absent : NativeBackendState.Uncertain;
    }

    public async Task<int> UninstallAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
    {
        if (options.Isolated || baseline.Product.Isolated) throw new InvalidOperationException("Isolated validation cannot retire a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        var plan = OriginalPlan(baseline);
        options = BackendOptions(baseline, options);
        if (baseline.Manifest.LegacyMsi is { } msi)
        {
            var source = PackageSafety.UnderRoot(verifiedMedia, msi.PayloadPath);
            if (!NativeMigrationValidation.SameGuid(WindowsIntegration.GetMsiProperty(source, "ProductCode"), baseline.Authorization.MsiProductCode))
                throw new InvalidDataException("The verified MSI database has a different ProductCode.");
            return await WindowsIntegration.RunMsiAsync("uninstall", source, baseline.Product.MsiProductCode, msi, plan, options, token);
        }
        // Always execute the original signed Burn payload, never a mutable installed UninstallString or executable.
        var executable = NativeMigrationValidation.Clone(baseline.Manifest.LegacyExecutable!);
        executable.UninstallExecutable = null;
        return await WindowsIntegration.RunExecutableAsync("uninstall", executable, verifiedMedia, baseline.Product.InstallDirectory, options, token);
    }

    public async Task<int> RestoreAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
    {
        if (options.Isolated || baseline.Product.Isolated) throw new InvalidOperationException("Isolated validation cannot restore a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        options = BackendOptions(baseline, options);
        if (baseline.Manifest.LegacyMsi is { } msi)
            return await WindowsIntegration.RunMsiAsync("install", PackageSafety.UnderRoot(verifiedMedia, msi.PayloadPath), baseline.Product.MsiProductCode,
                msi, OriginalPlan(baseline), options, token);
        return await WindowsIntegration.RunExecutableAsync("install", baseline.Manifest.LegacyExecutable!, verifiedMedia, baseline.Product.InstallDirectory, options, token);
    }

    private static InstallPlan OriginalPlan(NativeMigrationBaseline baseline) => new() { Manifest = baseline.Manifest,
        InstallDirectory = baseline.Product.InstallDirectory, Scope = baseline.Product.Scope, Features = baseline.Product.Features };
    private static InstallOptions BackendOptions(NativeMigrationBaseline baseline, InstallOptions caller) => new()
    {
        Scope = baseline.Product.Scope, TargetDirectory = baseline.Product.InstallDirectory, Silent = caller.Silent,
        LogPath = caller.LogPath, Features = baseline.Product.Features.ToList(), RequireSignature = true,
        TrustedPublisherKeySha256 = baseline.Product.PublisherKeySha256
        // Native migration never forwards unrelated caller-supplied legacy arguments to the original signed backend.
    };

    public async Task<string> BootIdentityAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        // WMI's documented LastBootUpTime is stable across process restarts; a wall-clock/uptime estimate is not sufficient.
        return await Task.Run(ReadBootIdentity, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8), token);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadBootIdentity()
    {
        object? locator = null, service = null, operatingSystem = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            // Win32_OperatingSystem is a singleton; direct Get avoids forward-only COM enumeration differences between CLRs.
            operatingSystem = ((dynamic)service).Get("Win32_OperatingSystem=@");
            string value = ((dynamic)operatingSystem).LastBootUpTime;
            return NormalizeBootIdentity(value);
        }
        finally
        {
            foreach (var value in new[] { operatingSystem, service, locator }) if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    internal static string NormalizeBootIdentity(string value)
    {
        if (value.Length != 25 || value[21] is not ('+' or '-') ||
            !DateTime.TryParseExact(value[..21], "yyyyMMddHHmmss.ffffff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local) ||
            !int.TryParse(value.AsSpan(22), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes > 840)
            throw new InvalidDataException("Windows did not provide a usable boot identity.");
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromMinutes(value[21] == '-' ? -minutes : minutes))
            .UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string productCode);
}
