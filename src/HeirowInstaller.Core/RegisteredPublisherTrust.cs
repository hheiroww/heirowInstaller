using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("HeirowInstaller.Trust.Tests")]

namespace HeirowInstaller.Core;

/// <summary>Reviewed installer-owned trust anchors. Package metadata and adjacent files cannot register a publisher.</summary>
internal sealed class RegisteredPublisherTrust
{
    private const string SocketJackPin = "eab5ba01a94557088fd7b23d9569b2f289368964e9d4922a79970ecb13409e22";
    private static readonly Lazy<RegisteredPublisherTrust> registered = new(ReadEmbedded);
    private readonly FrozenDictionary<string, string> products;
    internal static RegisteredPublisherTrust Default => registered.Value;

    // Additive internal injection for isolated synthetic acceptance, never serialized or selected by CLI/package data.
    internal RegisteredPublisherTrust(IReadOnlyDictionary<string, string> products)
    {
        foreach (var pair in products) { PackageSafety.ValidateId(pair.Key); if (!PackageSafety.HashPattern().IsMatch(pair.Value)) throw new InvalidDataException("Invalid registered publisher fingerprint."); }
        this.products = products.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }
    internal bool TryResolve(string productId, out string? pin) => products.TryGetValue(productId, out pin);

    private static RegisteredPublisherTrust ReadEmbedded()
    {
        var assembly = typeof(RegisteredPublisherTrust).Assembly;
        using var stream = assembly.GetManifestResourceStream("HeirowInstaller.Publishers.socketjack.com.json") ?? throw new InvalidDataException("The registered publisher identity is missing.");
        if (stream.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid registered publisher resource.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); IncrementalPackageIO.ValidateJson(bytes);
        using var document = JsonDocument.Parse(bytes); var value = document.RootElement;
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("id").GetString() != "socketjack.com" ||
            value.GetProperty("publicKeyFile").GetString() != "socketjack.com.public.pem" || value.GetProperty("signatureAlgorithm").GetString() != "RSA-PSS-SHA256" ||
            !string.Equals(value.GetProperty("publicKeySha256").GetString(), SocketJackPin, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The registered SocketJack publisher differs from the reviewed trust anchor.");
        using var publicKey = assembly.GetManifestResourceStream("HeirowInstaller.Publishers.socketjack.com.public.pem") ?? throw new InvalidDataException("The registered public key is missing.");
        using var text = new StreamReader(publicKey); var pem = text.ReadToEnd();
        if (pem.Length > 16384 || !pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidDataException("A registered publisher resource must contain only its public key.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048 || !PackageSafety.HashEquals(Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), SocketJackPin))
            throw new CryptographicException("The registered public key fingerprint differs from the reviewed identity.");
        return new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["heirowSnap"] = SocketJackPin, ["FruityLoopsCollab"] = SocketJackPin, ["heirowWebUI"] = SocketJackPin,
            ["heirowinstaller"] = SocketJackPin, ["heirowupdater"] = SocketJackPin
        });
    }
}
