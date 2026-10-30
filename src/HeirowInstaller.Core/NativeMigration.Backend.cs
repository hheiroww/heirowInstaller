using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

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

internal sealed class WindowsNativeMigrationBackend : INativeMigrationBackend, IInnoOwnedPlatformProvider
{
    public IInnoOwnedPlatform OwnedPlatform(NativeMigrationBaseline baseline, string stateRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned Inno retirement requires Windows.");
        return new WindowsInnoOwnedPlatform(baseline.Authorization.OwnedFootprint!, stateRoot);
    }
    public NativeBackendState ObserveDeclared(NativeMigrationBaselineDefinition definition, bool isolated)
    {
        if (definition.Backend == "inno") InnoMigrationPolicy.RequireExecutionImplementation();
        if (isolated) throw new InvalidOperationException("Isolated validation cannot inspect a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        return ObserveWindows(definition, expectedBurnPath: null);
    }
    public NativeBackendState Observe(NativeMigrationBaseline baseline)
    {
        if (baseline.Authorization.Backend == "inno") InnoMigrationPolicy.RequireExecutionImplementation();
        if (baseline.Product.Isolated) throw new InvalidOperationException("Isolated validation cannot inspect a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        return ObserveWindows(baseline.Authorization, baseline.Authorization.Backend == "burn" ? baseline.Product.BackendRegistrations.Single().Values["path"] : null);
    }

    [SupportedOSPlatform("windows")]
    private static NativeBackendState ObserveWindows(NativeMigrationBaselineDefinition definition, string? expectedBurnPath)
    {
        if (definition.Backend == "msi")
            return ObserveMsi(definition.Scope, definition.MsiProductCode!);
        if (definition.Backend == InnoOwnedPolicy.Backend)
        {
            var owned = definition.OwnedFootprint!;
            using var user = WindowsIntegration.Hive("user", owned.Registration.View);
            using var key = user.OpenSubKey(owned.Registration.UninstallKey!);
            foreach (var view in new[] { "registry64", "registry32" })
            {
                using var machine = WindowsIntegration.Hive("machine", view);
                using var other = machine.OpenSubKey(owned.Registration.UninstallKey!);
                if (other is not null) return NativeBackendState.Uncertain;
            }
            return key is null ? NativeBackendState.Absent : NativeBackendState.Installed;
            // This read-only state never authorizes retirement completion; the dedicated ledger does.
        }
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
        => ClassifyMsiContext(scope, CaptureMsiContext(productCode));

    [SupportedOSPlatform("windows")]
    internal static MsiContextObservation CaptureMsiContext(string productCode)
    {
        var code = Guid.Parse(productCode).ToString("B").ToUpperInvariant();
        var instances = new List<MsiContextInstance>();
        var query = MsiQueryProductState(code);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var currentSid = identity.User?.Value;
            if (currentSid is null) return new(query, false, 87, 0, instances);
            var complete = false; uint enumerationError = 0;
            // NULL SID with ALL selects only the current user and machine. Keep every call on this one synchronous thread.
            for (uint index = 0; index < 8; index++)
            {
                var actualCode = new StringBuilder(39); var sid = new StringBuilder(256); uint sidLength = (uint)sid.Capacity;
                var error = MsiEnumProductsEx(code, null, 7, index, actualCode, out var context, sid, ref sidLength);
                if (error == 234 && sidLength is > 0 and < 1024)
                {
                    sid = new StringBuilder((int)sidLength + 1); sidLength = (uint)sid.Capacity;
                    error = MsiEnumProductsEx(code, null, 7, index, actualCode, out context, sid, ref sidLength);
                }
                if (error == 259) { complete = true; break; }
                if (error != 0) { enumerationError = error; break; }
                var exactCode = NativeMigrationValidation.SameGuid(actualCode.ToString(), code);
                var matchesUser = (context is 1 or 2) && sid.ToString().Equals(currentSid, StringComparison.OrdinalIgnoreCase);
                var emptyMachine = context == 4 && sid.Length == 0;
                string? state = null; uint infoError = 87;
                if (exactCode && (matchesUser || emptyMachine))
                {
                    var value = new StringBuilder(32); uint size = (uint)value.Capacity;
                    // INSTALLPROPERTY_PRODUCTSTATE is the literal "State", not "ProductState".
                    infoError = MsiGetProductInfoEx(code, context == 4 ? null : sid.ToString(), context, "State", value, ref size);
                    if (infoError == 234 && size is > 0 and < 128)
                    {
                        value = new StringBuilder((int)size + 1); size = (uint)value.Capacity;
                        infoError = MsiGetProductInfoEx(code, context == 4 ? null : sid.ToString(), context, "State", value, ref size);
                    }
                    if (infoError == 0) state = value.ToString();
                }
                instances.Add(new(context, exactCode, matchesUser, emptyMachine, state, infoError));
            }
            var registrations = new List<bool>();
            foreach (var hiveName in new[] { "user", "machine" })
                foreach (var view in new[] { "registry64", "registry32" })
                {
                    using var hive = WindowsIntegration.Hive(hiveName, view);
                    using var key = hive.OpenSubKey(BackendMaintenance.UninstallRoot + code);
                    registrations.Add(key is not null);
                }
            var physical = CountMsiArpRegistrations(registrations[0], registrations[1], registrations[2], registrations[3]);
            if (MsiQueryProductState(code) != query) return new(query, false, 13, physical, instances);
            return new(query, complete, enumerationError, physical, instances);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return new(query, false, 13, 0, instances); }
    }

    internal static int CountMsiArpRegistrations(bool user64, bool user32, bool machine64, bool machine32) =>
        (user64 || user32 ? 1 : 0) + (machine64 ? 1 : 0) + (machine32 ? 1 : 0);

    internal static NativeBackendState ClassifyMsiContext(string scope, MsiContextObservation observation)
    {
        if (scope is not ("user" or "machine")) throw new ArgumentException("MSI observation requires an exact supported scope.");
        if (!observation.Complete || observation.Error != 0 || observation.ArpRegistrations is < 0 or > 1) return NativeBackendState.Uncertain;
        if (observation.QueryState == -1 && observation.Instances.Count == 0 && observation.ArpRegistrations == 0) return NativeBackendState.Absent;
        if (observation.QueryState != 5 || observation.Instances.Count != 1) return NativeBackendState.Uncertain;
        var instance = observation.Instances[0];
        if (!instance.ExactProductCode || instance.InfoError != 0 || instance.State != "5") return NativeBackendState.Uncertain;
        // ARP is supplementary evidence. A real per-user MSI may register its ARP entry in HKLM.
        return scope == "user" && (instance.Context is 1 or 2) && instance.CurrentUserSidMatches && !instance.MachineSidEmpty ||
            scope == "machine" && instance.Context == 4 && instance.MachineSidEmpty && !instance.CurrentUserSidMatches
            ? NativeBackendState.Installed : NativeBackendState.Uncertain;
    }

    internal static NativeBackendState CombineBurnState(NativeBackendState bundle, IEnumerable<NativeBackendState> children)
    {
        var states = children.ToArray();
        return bundle == NativeBackendState.Installed && states.All(state => state == NativeBackendState.Installed) ? NativeBackendState.Installed
            : bundle == NativeBackendState.Absent && states.All(state => state == NativeBackendState.Absent) ? NativeBackendState.Absent : NativeBackendState.Uncertain;
    }

    public async Task<int> UninstallAsync(NativeMigrationBaseline baseline, string verifiedMedia, InstallOptions options, CancellationToken token)
    {
        if (baseline.Authorization.Backend == "inno") InnoMigrationPolicy.RequireExecutionImplementation();
        if (baseline.Authorization.Backend == InnoOwnedPolicy.Backend) throw new InvalidOperationException("Owned retirement must use its per-resource journal, never external backend execution.");
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
        if (baseline.Authorization.Backend == "inno") InnoMigrationPolicy.RequireExecutionImplementation();
        if (baseline.Authorization.Backend == InnoOwnedPolicy.Backend) throw new InvalidOperationException("Owned restoration must use exact before-images, never setup execution.");
        if (options.Isolated || baseline.Product.Isolated) throw new InvalidOperationException("Isolated validation cannot restore a real legacy backend.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native migration requires Windows.");
        options = BackendOptions(baseline, options);
        if (baseline.Manifest.LegacyMsi is { } msi)
            return await WindowsIntegration.RunMsiAsync("install", PackageSafety.UnderRoot(verifiedMedia, msi.PayloadPath), baseline.Product.MsiProductCode,
                msi, OriginalPlan(baseline), options, token);
        return await WindowsIntegration.RunExecutableAsync("install", baseline.Manifest.LegacyExecutable!, verifiedMedia, baseline.Product.InstallDirectory, options, token);
    }

    private static InstallPlan OriginalPlan(NativeMigrationBaseline baseline) => new() { Manifest = baseline.Manifest,
        InstallDirectory = baseline.Product.InstallDirectory, Scope = baseline.Product.Scope, Features = baseline.Product.Features,
        UseOriginalInstallDirectory = baseline.Product.UseOriginalInstallDirectory };
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
            const BindingFlags invoke = BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding;
            service = locator!.GetType().InvokeMember("ConnectServer", invoke, null, locator, [".", @"root\cimv2"]);
            // Win32_OperatingSystem is a singleton; direct Get avoids forward-only COM enumeration differences between CLRs.
            // Avoid cached dynamic COM bindings that can outlive a final-released WMI object between queries.
            operatingSystem = service!.GetType().InvokeMember("Get", invoke, null, service, ["Win32_OperatingSystem=@"]);
            var value = operatingSystem!.GetType().InvokeMember("LastBootUpTime", BindingFlags.GetProperty, null, operatingSystem, null) as string
                ?? throw new InvalidDataException("Windows did not provide a usable boot identity.");
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string productCode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumProductsExW")]
    private static extern uint MsiEnumProductsEx(string productCode, string? userSid, uint context, uint index,
        StringBuilder installedProductCode, out uint installedContext, StringBuilder installedUserSid, ref uint sidLength);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoExW")]
    private static extern uint MsiGetProductInfoEx(string productCode, string? userSid, uint context, string property, StringBuilder value, ref uint length);
}

internal sealed record MsiContextInstance(uint Context, bool ExactProductCode, bool CurrentUserSidMatches, bool MachineSidEmpty, string? State, uint InfoError);
internal sealed record MsiContextObservation(int QueryState, bool Complete, uint Error, int ArpRegistrations, IReadOnlyList<MsiContextInstance> Instances);
