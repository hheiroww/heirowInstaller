using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml;
using HeirowInstaller.Core;
using Microsoft.Win32;

if (args.FirstOrDefault() == "--owner-handshake-child") { Environment.ExitCode = await UpdaterOwnerHandshakeChecks.ChildAsync(args); return; }
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These checks inspect Windows tokens and PE resources.");
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply one owned output directory.");
string root = Path.GetFullPath(args[0]); if (Directory.Exists(root)) throw new IOException("Use fresh privileges test output."); Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); assertions++; Console.WriteLine("PASS " + message); }
void Reject<T>(Action action, string message) where T : Exception
{ try { action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message); }
byte[] Executable(string xml)
{
    var manifest = Encoding.UTF8.GetBytes(xml); int resourceLength = 88 + manifest.Length, rawLength = (resourceLength + 511) / 512 * 512;
    var bytes = new byte[512 + rawLength]; var span = bytes.AsSpan();
    void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; U32(60, 128); "PE\0\0"u8.CopyTo(span[128..]);
    U16(132, 0x8664); U16(134, 1); U16(148, 240); U16(150, 0x22);
    const int optional = 152; U16(optional, 0x20b); U32(optional + 20, 0x1000); U64(optional + 24, 0x140000000); U32(optional + 32, 0x1000); U32(optional + 36, 512);
    U16(optional + 40, 6); U16(optional + 48, 6); U32(optional + 56, 0x2000); U32(optional + 60, 512); U16(optional + 68, 3);
    U64(optional + 72, 0x100000); U64(optional + 80, 0x1000); U64(optional + 88, 0x100000); U64(optional + 96, 0x1000); U32(optional + 108, 16);
    U32(optional + 128, 0x1000); U32(optional + 132, (uint)resourceLength);
    const int section = 392; ".rsrc\0\0\0"u8.CopyTo(span[section..]); U32(section + 8, (uint)resourceLength); U32(section + 12, 0x1000); U32(section + 16, (uint)rawLength); U32(section + 20, 512); U32(section + 36, 0x40000040);
    U16(512 + 14, 1); U32(512 + 16, 24); U32(512 + 20, 0x80000018);
    U16(512 + 24 + 14, 1); U32(512 + 40, 1); U32(512 + 44, 0x80000030);
    U16(512 + 48 + 14, 1); U32(512 + 64, 0x409); U32(512 + 68, 72);
    U32(512 + 72, 0x1000 + 88); U32(512 + 76, (uint)manifest.Length); manifest.CopyTo(span[(512 + 88)..]); return bytes;
}
string Manifest(string level) => "<assembly xmlns='urn:schemas-microsoft-com:asm.v1'><trustInfo xmlns='urn:schemas-microsoft-com:asm.v3'><security><requestedPrivileges><requestedExecutionLevel level='" + level + "' uiAccess='false'/></requestedPrivileges></security></trustInfo></assembly>";
string Save(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
var invoke = Save("asInvoker.exe", Executable(Manifest("asInvoker")));
var highest = Save("highestAvailable.exe", Executable(Manifest("highestAvailable")));
var admin = Save("requireAdministrator.exe", Executable(Manifest("requireAdministrator")));
Check(WindowsExecutablePrivileges.Read(invoke) == WindowsExecutionLevel.AsInvoker, "PE manifest parser recognizes asInvoker without loading the program");
Check(WindowsExecutablePrivileges.Read(highest) == WindowsExecutionLevel.HighestAvailable, "PE manifest parser keeps highestAvailable distinct from required administration");
Check(WindowsExecutablePrivileges.Read(admin) == WindowsExecutionLevel.RequireAdministrator, "PE manifest parser recognizes requireAdministrator");
Check(WindowsExecutablePrivileges.Read(Save("data.bin", [1, 2, 3])) == WindowsExecutionLevel.AsInvoker, "ordinary non-PE data does not acquire an elevation requirement");
Reject<InvalidDataException>(() => WindowsExecutablePrivileges.Read(Save("duplicate.exe", Executable("<assembly><requestedExecutionLevel level='asInvoker'/><requestedExecutionLevel level='requireAdministrator'/></assembly>"))), "duplicate execution-level declarations are rejected");
Reject<InvalidDataException>(() => WindowsExecutablePrivileges.Read(Save("unknown.exe", Executable(Manifest("unknown")))), "unknown execution levels are rejected");
Reject<XmlException>(() => WindowsExecutablePrivileges.Read(Save("malformed.exe", Executable("<assembly><unclosed>"))), "malformed manifest XML is rejected");
Reject<XmlException>(() => WindowsExecutablePrivileges.Read(Save("dtd.exe", Executable("<!DOCTYPE assembly [<!ENTITY level 'requireAdministrator'>]><assembly><requestedExecutionLevel level='&level;'/></assembly>"))), "DTD and entity declarations are rejected before expansion");
var invalidRva = Executable(Manifest("asInvoker")); BinaryPrimitives.WriteUInt32LittleEndian(invalidRva.AsSpan(512 + 72), 0x9000);
Reject<InvalidDataException>(() => WindowsExecutablePrivileges.Read(Save("outside-section.exe", invalidRva)), "manifest resource addresses cannot leave declared PE sections");
var cycle = Executable(Manifest("asInvoker")); BinaryPrimitives.WriteUInt32LittleEndian(cycle.AsSpan(512 + 20), 0x80000000);
Reject<InvalidDataException>(() => WindowsExecutablePrivileges.Read(Save("resource-cycle.exe", cycle)), "cyclic resource directories are bounded and rejected");

using var key = RSA.Create(2048); string keyPath = Save("synthetic-fixture-key.pem", Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
string pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
InstallPlan Plan(string name, bool setupAdministrator, bool appAdministrator, bool actualAdminManifest = false, bool? privilegedPrerequisiteMissing = null)
{
    string work = Path.Combine(root, name), source = Path.Combine(work, "source"); Directory.CreateDirectory(source);
    File.Copy(invoke, Path.Combine(source, "Launcher.exe")); File.Copy(actualAdminManifest ? admin : invoke, Path.Combine(source, "Actual.exe")); File.WriteAllText(Path.Combine(source, "Actual.runtimeconfig.json"), "{}");
    var manifest = new PackageManifest { FormatVersion = 5, Id = "privileges." + name, Version = "1.0.0", Name = "Privileges fixture", Publisher = "Fixture", EntryPoint = "Launcher.exe", Scope = "user", TargetRuntime = InstallerPlatform.RuntimeIdentifier,
        RequiresAdministrator = setupAdministrator, UpdateUrl = "https://fixture.invalid/update.json", LegalDocuments = LegalFixture.For("privileges." + name),
        SharedPackages = [new() { Id = "dotnet.runtime", Version = "1.0.0", TargetRuntime = InstallerPlatform.RuntimeIdentifier, DownloadUrl = "https://fixture.invalid/runtime.hinst", Sha256 = new string('a', 64), PublisherKeySha256 = pin, Size = 128 }],
        ApplicationLaunch = new() { ExecutablePath = "Actual.exe", RuntimeConfigPath = "Actual.runtimeconfig.json", RuntimePackageId = "dotnet.runtime", RequiresAdministrator = appAdministrator } };
    if (privilegedPrerequisiteMissing is { } missing)
    {
        string detectedFile = Path.Combine(work, "prerequisite-present.marker");
        if (!missing) File.WriteAllText(detectedFile, "Fixture detection marker only");
        manifest.Prerequisites.Add(new() { Id = "fixture.runtime", Name = "Fixture prerequisite, never executed", RequiresAdministrator = true, DetectFile = detectedFile,
            DownloadUrl = "https://fixture.invalid/prerequisite.exe", Sha256 = new string('b', 64) });
    }
    string manifestPath = Path.Combine(work, "manifest.json"), packagePath = Path.Combine(work, "fixture.hinst"); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = keyPath });
    using var package = PackageReader.Open(packagePath);
    return new InstallerEngine().Plan(package, new() { Isolated = true, RegisterProduct = false, TargetDirectory = Path.Combine(work, "installed"), StateRoot = Path.Combine(work, "state"), TrustedPublisherKeySha256 = pin, RequireSignature = true });
}
var normal = Plan("normal", false, false);
Check(normal.Scope == "user" && !normal.RequiresElevation && !normal.Manifest.ApplicationLaunch!.RequiresAdministrator, "ordinary application setup defaults to per-user without elevation");
var setup = Plan("setup", true, false);
Check(setup.Scope == "user" && setup.RequiresElevation && !setup.Manifest.ApplicationLaunch!.RequiresAdministrator, "signed setup elevation does not change per-user scope or elevate application launch");
var application = Plan("application", false, true);
Check(application.Scope == "user" && application.RequiresElevation && !application.Manifest.RequiresAdministrator && application.Manifest.ApplicationLaunch!.RequiresAdministrator, "application administrator requirement requests setup approval while preserving per-user scope and distinct source flags");
var detected = Plan("detected", false, false, actualAdminManifest: true);
Check(detected.Manifest.ApplicationLaunch!.RequiresAdministrator && !detected.Manifest.RequiresAdministrator && detected.RequiresElevation && detected.Scope == "user", "authoring detects the actual application's PE requirement and requests setup approval without changing scope");
var missingPrerequisite = Plan("missing-prerequisite", false, false, privilegedPrerequisiteMissing: true);
Check(missingPrerequisite.Scope == "user" && missingPrerequisite.RequiresElevation, "a missing privileged prerequisite requests approval without changing per-user scope");
var existingPrerequisite = Plan("present-prerequisite", false, false, privilegedPrerequisiteMissing: false);
Check(existingPrerequisite.Scope == "user" && !existingPrerequisite.RequiresElevation, "an already detected privileged prerequisite avoids unnecessary setup elevation");

using var self = Process.GetCurrentProcess(); long ticks = self.StartTime.ToUniversalTime().Ticks;
Reject<InvalidDataException>(() => InstallerUserContext.OpenForProcess(self.Id, ticks + 1), "original-user contexts reject a reused or mismatched process start identity");
Reject<InvalidDataException>(() => InstallerUserContext.OpenForProcess(0, ticks), "original-user contexts reject an invalid process identity");
Check(InstallerUserContext.Current == null, "original-user context begins outside any ambient scope");
using (var original = InstallerUserContext.OpenForProcess(self.Id, ticks))
{
    using var identity = WindowsIdentity.GetCurrent();
    Check(original.UserSid == identity.User?.Value && !original.IsDifferentAccount, "actual current-process token supplies the original SID and account identity");
    foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        Check(Path.GetFullPath(original.GetFolderPath(folder)).Equals(Path.GetFullPath(Environment.GetFolderPath(folder)), StringComparison.OrdinalIgnoreCase), "original token resolves the current user's actual known folder");
    Check(original.ExpandEnvironmentVariables("%USERPROFILE%") == original.GetFolderPath(Environment.SpecialFolder.UserProfile), "environment expansion uses the token's original user profile");
    Check(original.ExpandEnvironmentVariables("%MISSING_PRIVILEGES_FIXTURE_VARIABLE%") == "%MISSING_PRIVILEGES_FIXTURE_VARIABLE%", "unknown original-user environment variables remain unchanged");
    using (original.Enter())
    {
        Check(ReferenceEquals(InstallerUserContext.Current, original) && InstallerPlatform.UserData == original.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ambient original-user context routes installer folder lookup");
        await Task.Yield();
        Check(await Task.Run(() => ReferenceEquals(InstallerUserContext.Current, original)), "original-user context follows asynchronous installer work");
        using (original.Enter()) Check(ReferenceEquals(InstallerUserContext.Current, original), "nested original-user scopes retain identity");
        Check(ReferenceEquals(InstallerUserContext.Current, original), "nested context disposal restores its predecessor");
        var method = typeof(InstallerUserContext).GetMethod("GetUserEnvironment", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var currentHive = Registry.CurrentUser.OpenSubKey("Environment", false);
        var actual = method.Invoke(original, ["TEMP"]) as string;
        Check(actual == currentHive?.GetValue("TEMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, "HKU SID routing reads the original user's environment without writes");
    }
    Check(InstallerUserContext.Current == null, "ambient original-user context is restored after disposal");
}
await UpdaterOwnerHandshakeChecks.RunAsync(root, Check);
await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, assertions, actualSameAccount = true, credentialedElevationTested = false, privilegedWrites = false, executedHandshakeChildren = true, retained = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {assertions} privilege, actual-token and real pipe-child checks. No privileged write was performed. Retained outputs: {root}");
