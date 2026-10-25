using System.Text.Json;

namespace HeirowInstaller.Publishing;

public sealed partial class PublisherClient
{
    /// <summary>Waits with this connection's existing publisher account credentials, without granting approval rights.</summary>
    public async Task<PublishedRelease> WaitForActivationAsync(PublishedRelease receipt, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(15); var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromHours(1) || interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentException("Invalid activation wait limits.");
        if (string.IsNullOrEmpty(receipt.Status)) return receipt;
        if (!Guid.TryParseExact(receipt.ReceiptId, "N", out _)) throw new InvalidDataException("The publication receipt ID is invalid.");
        Validate(receipt);
        if (receipt.Status == "activated") return receipt;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(limit);
        var current = receipt;
        try
        {
            while (current.Status == "pending")
            {
                await Task.Delay(interval, deadline.Token);
                using var response = await client.GetAsync(new Uri(origin, "api/installer/publisher/receipts/" + receipt.ReceiptId), deadline.Token);
                await EnsureSuccessAsync(response, deadline.Token);
                var next = JsonSerializer.Deserialize<PublishedRelease>(await ReadBoundedAsync(response, deadline.Token), Json) ?? throw new InvalidDataException("The publication receipt is missing.");
                if (next.ReceiptId != receipt.ReceiptId || next.ProductId != receipt.ProductId || next.Version != receipt.Version
                    || next.UploadedPackageSha256 != receipt.UploadedPackageSha256 || next.UploadedSize != receipt.UploadedSize
                    || receipt.UploadedPackageSha256 == null && (next.Sha256 != receipt.Sha256 || next.Size != receipt.Size)) throw new InvalidDataException("The activation receipt changed publication identity.");
                Validate(next);
                current = next;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The publication remains pending; activation was not confirmed."); }
        return current.Status == "activated" ? current : throw new InvalidOperationException("The publisher rejected package activation.");
        static void Validate(PublishedRelease value)
        {
            if (value.Status is not ("pending" or "activated" or "rejected") || value.Status == "activated" &&
                (value.Size <= 0 || value.Sha256 is not { Length: 64 } || !value.Sha256.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid activation receipt.");
        }
    }
}
