using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HeirowInstaller.Core;

/// <summary>Host paths and identity shared by the installer, CLI and companion.</summary>
public static class InstallerPlatform
{
    public static string Current => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";
    public static string RuntimeIdentifier => (Current == "windows" ? "win" : Current == "macos" ? "osx" : Current) + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : "";
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string GetFolderPath(Environment.SpecialFolder folder) => OperatingSystem.IsWindows() && InstallerUserContext.Current is { } original
        ? original.GetFolderPath(folder) : Environment.GetFolderPath(folder);
    public static string ExpandEnvironmentVariables(string value) => OperatingSystem.IsWindows() && InstallerUserContext.Current is { } original
        ? original.ExpandEnvironmentVariables(value) : Environment.ExpandEnvironmentVariables(value);
    public static string UserHome => GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string UserData => OperatingSystem.IsWindows() ? GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(UserHome, "Library", "Application Support") : Xdg("XDG_DATA_HOME", ".local/share");
    public static string UserState => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? UserData : Xdg("XDG_STATE_HOME", ".local/state");
    public static bool IsElevated => OperatingSystem.IsWindows()
        ? new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
        : (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && GetEffectiveUserId() == 0;

    public static string DefaultStateRoot(string scope) => scope == "machine"
        ? OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "heirowInstaller", "state")
            : OperatingSystem.IsMacOS() ? "/Library/Application Support/SocketJack/heirowInstaller/state" : "/var/lib/heirowInstaller/state"
        : Path.Combine(UserState, "SocketJack", "heirowInstaller", "state");

    public static string DefaultInstallDirectory(string id, string scope) => OperatingSystem.IsWindows()
        ? Path.Combine(scope == "machine" ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : Path.Combine(UserData, "Programs"), id)
        : OperatingSystem.IsMacOS() ? Path.Combine(scope == "machine" ? "/Applications" : Path.Combine(UserHome, "Applications"), id)
        : scope == "machine" ? Path.Combine("/opt", id) : Path.Combine(UserData, "Programs", id);

    private static string Xdg(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
        ? Path.GetFullPath(configured) : Path.Combine(UserHome, fallback);

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
}
