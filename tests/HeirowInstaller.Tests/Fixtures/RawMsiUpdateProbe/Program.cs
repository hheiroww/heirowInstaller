using System.Text.Json;
using HeirowInstaller.Core;

if (!OperatingSystem.IsWindows() || args.Length != 4)
{
    Console.Error.WriteLine("Usage: RawMsiUpdateProbe <inspected-fixture.msi> <unique-product-id> <target-directory> <log-path>");
    return 64;
}

try
{
    var installer = Path.GetFullPath(args[0]);
    var target = Path.GetFullPath(args[2]);
    var log = Path.GetFullPath(args[3]);
    var feed = new UpdateFeed
    {
        ProductId = args[1], Version = "1.0.0", PackageType = "mSi",
        InstallerUrl = "https://updates.example.invalid/inspected-fixture.msi",
        Sha256 = PackageSafety.HashFile(installer), Size = new FileInfo(installer).Length,
        Arguments = ["INSTALLFOLDER=" + target, "ENCODER_EMPTY=", "ENCODER_QUOTES=Acme \"Widgets\"", "/l*v", log]
    };
    using var client = new UpdateClient();
    var result = await client.ApplyAsync(installer, feed, new InstallOptions
    {
        Silent = true, TargetDirectory = target,
        StateRoot = Path.Combine(Path.GetDirectoryName(log)!, "raw-update-state")
    });
    Console.WriteLine(JsonSerializer.Serialize(new { productId = result.ProductId, version = result.Version, installDirectory = result.InstallDirectory }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
