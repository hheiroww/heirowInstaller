using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.BootstrapContract;

public sealed record WebBootstrapBinding(byte[] DescriptorBytes, string PublisherKeySha256, long LauncherLength);

/// <summary>A fixed native launcher followed by an RSA-signed product descriptor. Final Windows signing remains separate.</summary>
public static class WebBootstrapBindings
{
    public const int MaximumDescriptorBytes = 1024 * 1024;
    public const int MaximumEnvelopeBytes = 2 * 1024 * 1024;
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("HINSTBOOTSTRAP01");

    public static WebBootstrapBinding Read(string path, string? trustedPublisherKeySha256 = null)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (end, _, _) = PeLayout(input);
        Span<byte> footer = stackalloc byte[24];
        long footerEnd = -1;
        // Authenticode aligns its certificate table to eight bytes after the signed descriptor.
        for (var padding = 0; padding < 8 && end - padding >= footer.Length; padding++)
        {
            input.Position = end - padding - footer.Length; input.ReadExactly(footer);
            if (footer[8..].SequenceEqual(Marker)) { footerEnd = end - padding; break; }
        }
        if (footerEnd < 0) throw new InvalidDataException("The native installer has no signed product descriptor.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(footer);
        if (length is <= 0 or > MaximumEnvelopeBytes || length > footerEnd - footer.Length - 64)
            throw new InvalidDataException("Invalid native product descriptor length.");
        var prefixLength = footerEnd - footer.Length - length;
        input.Position = prefixLength;
        var envelope = new byte[(int)length]; input.ReadExactly(envelope);
        using var document = JsonDocument.Parse(envelope); UniqueProperties(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3)
            throw new InvalidDataException("Invalid native product signature envelope.");
        var bytes = Convert.FromBase64String(root.GetProperty("descriptorBase64").GetString()!);
        if (bytes.Length is <= 0 or > MaximumDescriptorBytes) throw new InvalidDataException("The native product descriptor exceeds its limit.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(root.GetProperty("publicKey").GetString()!);
        var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        if (rsa.KeySize < 2048 || trustedPublisherKeySha256 is not null && !pin.Equals(trustedPublisherKeySha256, StringComparison.OrdinalIgnoreCase) ||
            !rsa.VerifyData(bytes, Convert.FromBase64String(root.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The native installer product signature is invalid.");
        using var descriptor = JsonDocument.Parse(bytes); UniqueProperties(descriptor.RootElement);
        if (descriptor.RootElement.GetProperty("formatVersion").GetInt32() != 1 ||
            !pin.Equals(descriptor.RootElement.GetProperty("publisherKeySha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The native installer descriptor has a different publisher binding.");
        return new(bytes, pin, prefixLength);
    }

    public static async Task WriteAsync(string launcherPath, ReadOnlyMemory<byte> descriptorBytes, string outputPath, RSA signingKey, CancellationToken cancellationToken = default)
    {
        if (descriptorBytes.Length is <= 0 or > MaximumDescriptorBytes || signingKey.KeySize < 2048) throw new InvalidDataException("Invalid product descriptor or publisher signing key.");
        using var descriptor = JsonDocument.Parse(descriptorBytes); UniqueProperties(descriptor.RootElement);
        var pin = Convert.ToHexString(SHA256.HashData(signingKey.ExportSubjectPublicKeyInfo()));
        if (descriptor.RootElement.GetProperty("formatVersion").GetInt32() != 1 || !pin.Equals(descriptor.RootElement.GetProperty("publisherKeySha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The descriptor publisher differs from the supplied signing key.");
        using var envelope = new MemoryStream();
        using (var writer = new Utf8JsonWriter(envelope))
        {
            writer.WriteStartObject(); writer.WriteBase64String("descriptorBase64", descriptorBytes.Span);
            writer.WriteString("publicKey", signingKey.ExportSubjectPublicKeyInfoPem());
            writer.WriteBase64String("signature", signingKey.SignData(descriptorBytes.Span, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); writer.WriteEndObject();
        }
        await using var input = new FileStream(launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        var (logicalEnd, _, _) = PeLayout(input);
        if (logicalEnd != input.Length) throw new InvalidDataException("Bind the unsigned generic launcher before final Windows signing.");
        if (input.Length + envelope.Length + 24 >= 20_000_000) throw new InvalidDataException("The completed native installer must be smaller than 20,000,000 bytes.");
        input.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken); envelope.Position = 0; await envelope.CopyToAsync(output, cancellationToken);
        var footer = new byte[24]; BinaryPrimitives.WriteInt64LittleEndian(footer, envelope.Length); Marker.CopyTo(footer, 8);
        await output.WriteAsync(footer, cancellationToken); await output.FlushAsync(cancellationToken);
    }

    /// <summary>Normalizes only the Windows checksum and certificate directory, whose bytes change during final signing.</summary>
    public static string LauncherSha256(string path, long? launcherLength = null)
    {
        using var input = File.OpenRead(path); var (_, checksum, certificate) = PeLayout(input);
        var length = launcherLength ?? input.Length;
        if (length <= certificate + 8 || length > input.Length) throw new InvalidDataException("Invalid native launcher extent.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        input.Position = 0; var buffer = new byte[131072]; long offset = 0;
        while (offset < length)
        {
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - offset)); if (count == 0) throw new EndOfStreamException();
            foreach (var range in new[] { (Offset: checksum, Length: 4), (Offset: certificate, Length: 8) })
            {
                var start = Math.Max(offset, range.Offset); var end = Math.Min(offset + count, range.Offset + range.Length);
                if (end > start) buffer.AsSpan((int)(start - offset), (int)(end - start)).Clear();
            }
            hash.AppendData(buffer, 0, count); offset += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static (long End, long Checksum, long Certificate) PeLayout(Stream input)
    {
        if (input.Length < 128) throw new InvalidDataException("A native Windows launcher is required.");
        Span<byte> dos = stackalloc byte[64]; input.Position = 0; input.ReadExactly(dos);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidDataException("Invalid Windows launcher header.");
        var pe = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
        if (pe < 64 || pe > 1024 * 1024 || pe > input.Length - 264) throw new InvalidDataException("Invalid Windows launcher header offset.");
        Span<byte> header = stackalloc byte[24]; input.Position = pe; input.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8)) throw new InvalidDataException("Invalid Windows launcher signature.");
        var optionalLength = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
        Span<byte> magic = stackalloc byte[2]; input.ReadExactly(magic);
        var directory = BinaryPrimitives.ReadUInt16LittleEndian(magic) switch { 0x10b => 96, 0x20b => 112, _ => throw new InvalidDataException("Unsupported Windows launcher format.") };
        if (optionalLength < directory + 40 || pe + 24L + optionalLength > input.Length) throw new InvalidDataException("Truncated Windows optional header.");
        var certificate = pe + 24L + directory + 32;
        Span<byte> security = stackalloc byte[8]; input.Position = certificate; input.ReadExactly(security);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(security); var size = BinaryPrimitives.ReadUInt32LittleEndian(security[4..]);
        if ((offset == 0) != (size == 0) || offset != 0 && (offset < pe + 24 + optionalLength || (long)offset + size != input.Length || offset % 8 != 0))
            throw new InvalidDataException("Invalid Windows certificate extent.");
        return (offset == 0 ? input.Length : offset, pe + 24L + 64, certificate);
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate bootstrap metadata property."); UniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
}
