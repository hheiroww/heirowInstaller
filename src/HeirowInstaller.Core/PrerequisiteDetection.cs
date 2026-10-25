using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RegistryView = HeirowInstaller.Core.PrerequisiteRegistryView;
using RegistryValueKind = HeirowInstaller.Core.PrerequisiteRegistryValueKind;

[assembly: InternalsVisibleTo("HeirowInstaller.Prerequisites.Tests")]

namespace HeirowInstaller.Core;

/// <summary>Opt-in fixed Windows runtime detection. No registry paths or commands can be authored.</summary>
public sealed class PrerequisiteDetectionDefinition
{
    public string Kind { get; set; } = "";
    public string Architecture { get; set; } = "x64";
    public string MinimumVersion { get; set; } = "";
}

/// <summary>Detects the fixed Windows runtime registrations without running commands or author-supplied registry queries.</summary>
public static class PrerequisiteDetection
{
    static readonly Regex StableVersion = new("^[0-9]+(?:\\.[0-9]+){2,3}$", RegexOptions.CultureInvariant);

    public static void Validate(PrerequisiteDetectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind is not ("dotnet-desktop" or "vc-runtime") || definition.Architecture != "x64")
            throw new InvalidDataException("Prerequisite detection supports dotnet-desktop or vc-runtime with x64 architecture.");
        if (!TryVersion(definition.MinimumVersion, out var minimum) || minimum.Major < 1 || definition.Kind == "vc-runtime" && minimum.Major != 14)
            throw new InvalidDataException("Prerequisite detection needs a stable numeric minimum version; the VC runtime must remain in major version 14.");
    }

    public static bool IsSatisfied(PrerequisiteDetectionDefinition definition) => Detect(definition).Satisfied;

    /// <summary>A read-only detection result; it never starts, downloads or modifies a runtime installer.</summary>
    public static PrerequisiteDetectionResult Detect(PrerequisiteDetectionDefinition definition) => Detect(definition, WindowsPrerequisiteRegistry.Instance);

    internal static PrerequisiteDetectionResult Detect(PrerequisiteDetectionDefinition definition, IPrerequisiteRegistry reader)
    {
        Validate(definition); ArgumentNullException.ThrowIfNull(reader); TryVersion(definition.MinimumVersion, out var minimum);
        var location = definition.Kind == "dotnet-desktop" ? PrerequisiteRegistryLocation.DotNetDesktopX64 : PrerequisiteRegistryLocation.VisualCppX64;
        Version? newest = null;
        // .NET's shared-framework registration convention uses the 32-bit view
        // even for x64 runtimes. Check both supported views without relying on
        // the bitness of the installer process or spelling Wow6432Node twice.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            IReadOnlyDictionary<string, PrerequisiteRegistryValue>? values;
            try { values = reader.Read(location, view); }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException or PlatformNotSupportedException) { continue; }
            if (values is null || values.Count > 2048) continue;
            if (location == PrerequisiteRegistryLocation.DotNetDesktopX64)
            {
                foreach (var value in values)
                    if (Installed(value.Value) && TryVersion(value.Key, out var version) && Meets(version, minimum) && (newest is null || version > newest)) newest = version;
            }
            else if (values.TryGetValue("Installed", out var installed) && Installed(installed) && VcVersion(values, out var version)
                && Meets(version, minimum) && (newest is null || version > newest)) newest = version;
        }
        return new(newest is not null, newest?.ToString());
    }

    static bool Meets(Version installed, Version minimum) => installed.Major == minimum.Major && installed >= minimum;
    static bool Installed(PrerequisiteRegistryValue? value) => value?.Kind == RegistryValueKind.DWord && value.Value is int number && number == 1;
    static bool VcVersion(IReadOnlyDictionary<string, PrerequisiteRegistryValue> values, out Version version)
    {
        if (values.TryGetValue("Version", out var text) && text.Kind == RegistryValueKind.String && text.Value is string value)
        {
            if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
            if (TryVersion(value, out version)) return true;
        }
        int[] parts = new int[4]; var names = new[] { "Major", "Minor", "Bld", "Rbld" };
        for (int index = 0; index < names.Length; index++)
        {
            if (!values.TryGetValue(names[index], out var item) || item.Kind != RegistryValueKind.DWord || item.Value is not int part || part < 0)
            { version = new Version(0, 0, 0, 0); return false; }
            parts[index] = part;
        }
        version = new(parts[0], parts[1], parts[2], parts[3]); return true;
    }
    static bool TryVersion(string? value, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (value is null || value.Length > 64 || !StableVersion.IsMatch(value) || !Version.TryParse(value, out var parsed)) return false;
        // Three- and four-component spellings of the same runtime version are equivalent.
        version = new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)); return true;
    }

    sealed class WindowsPrerequisiteRegistry : IPrerequisiteRegistry
    {
        public static WindowsPrerequisiteRegistry Instance { get; } = new();
        public IReadOnlyDictionary<string, PrerequisiteRegistryValue>? Read(PrerequisiteRegistryLocation location, RegistryView view)
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem) return null;
            return ReadWindows(location, view);
        }
        [SupportedOSPlatform("windows")]
        static IReadOnlyDictionary<string, PrerequisiteRegistryValue>? ReadWindows(PrerequisiteRegistryLocation location, RegistryView view)
        {
            var path = location switch
            {
                PrerequisiteRegistryLocation.DotNetDesktopX64 => @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
                PrerequisiteRegistryLocation.VisualCppX64 => @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
                _ => throw new InvalidDataException("Unknown fixed prerequisite registry location.")
            };
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view == RegistryView.Registry32 ? Microsoft.Win32.RegistryView.Registry32 : Microsoft.Win32.RegistryView.Registry64);
            using var key = hive.OpenSubKey(path, writable: false);
            if (key is null) return null;
            var names = key.GetValueNames(); if (names.Length > 2048) return null;
            var values = new Dictionary<string, PrerequisiteRegistryValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (name.Length > 64) continue;
                try
                {
                    var kind = key.GetValueKind(name) switch
                    {
                        Microsoft.Win32.RegistryValueKind.DWord => RegistryValueKind.DWord,
                        Microsoft.Win32.RegistryValueKind.String => RegistryValueKind.String,
                        _ => RegistryValueKind.Unknown
                    };
                    values[name] = new(kind, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                }
                catch (IOException) { } // A concurrent runtime update may remove an individual value.
            }
            return values;
        }
    }
}

public sealed record PrerequisiteDetectionResult(bool Satisfied, string? InstalledVersion);
internal enum PrerequisiteRegistryLocation { DotNetDesktopX64, VisualCppX64 }
internal enum PrerequisiteRegistryView { Registry32, Registry64 }
internal enum PrerequisiteRegistryValueKind { Unknown, DWord, String, QWord, Binary }
internal sealed record PrerequisiteRegistryValue(RegistryValueKind Kind, object? Value);
internal interface IPrerequisiteRegistry
{
    IReadOnlyDictionary<string, PrerequisiteRegistryValue>? Read(PrerequisiteRegistryLocation location, RegistryView view);
}
