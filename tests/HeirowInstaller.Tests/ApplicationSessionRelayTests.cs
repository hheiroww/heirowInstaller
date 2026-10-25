using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task SessionRelay()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var self = Process.GetCurrentProcess();
        var image = self.MainModule!.FileName; var ticks = self.StartTime.ToUniversalTime().Ticks;
        Equal(ticks, ApplicationSessionRelay.CheckProcess(self.Id, ticks, image));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationSessionRelay.CheckProcess(self.Id, ticks + 1, image)));
        await Throws<InvalidDataException>(() => Task.Run(() => ApplicationSessionRelay.CheckProcess(self.Id, ticks, image + ".wrong")));
        var binding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var empty = new ApplicationSessionSnapshot(null, null, 0, Guid.NewGuid().ToString("N"));
        var sessions = new ApplicationSessionStore(empty); var delivered = 0; string? active = null;
        var rejections = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var owner = new ApplicationSessionRelay(binding, image, (snapshot, request, _) =>
        {
            Interlocked.Increment(ref delivered);
            var accepted = sessions.Replace(snapshot);
            if (request is not null) active ??= request;
            return Task.FromResult(new ApplicationSessionRelay.Reply("", request is null ? null : active, accepted));
        }, rejections.Enqueue);
        async Task<ApplicationSessionRelay.Reply> Send(ApplicationSessionSnapshot snapshot, bool manual)
        {
            try { return await ApplicationSessionRelay.SendAsync(binding, image, snapshot, manual, CancellationToken.None); }
            catch (Exception error) { throw new InvalidOperationException("Relay fixture rejected an expected valid exchange: " + string.Join(',', rejections), error); }
        }
        var first = new ApplicationSessionSnapshot("wshare_hash=fixture-relay-first");
        var reply = await Send(first, false);
        True(reply.SessionAccepted && reply.ManualRequestId is null); Equal(first.Id, sessions.Read().Snapshot.Id);
        var next = new ApplicationSessionSnapshot("wshare_hash=fixture-relay-next");
        var manual = await Send(next, true);
        var repeated = await Send(new ApplicationSessionSnapshot("wshare_hash=fixture-relay-next"), true);
        Equal(manual.ManualRequestId, repeated.ManualRequestId); True(Guid.TryParseExact(manual.ManualRequestId, "N", out _));
        var delayed = await Send(first, false);
        True(!delayed.SessionAccepted && sessions.Read().Snapshot.Cookie == next.Cookie);
        var signedOut = await Send(new ApplicationSessionSnapshot(), false);
        True(signedOut.SessionAccepted && sessions.Read().Snapshot.Cookie is null && sessions.Read().Snapshot.BearerToken is null);
        using (var delayedReader = new NamedPipeClientStream(".", ApplicationSessionRelay.PipeName(binding), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await delayedReader.ConnectAsync(2000); var hello = await ApplicationSessionRelay.ReadAsync<JsonElement>(delayedReader, CancellationToken.None);
            var requestId = Guid.NewGuid().ToString("N"); var capture = new ApplicationSessionSnapshot();
            await ApplicationSessionRelay.WriteAsync(delayedReader, new
            {
                FormatVersion = 1, Binding = binding, Challenge = hello.GetProperty("Challenge").GetString(), RequestId = requestId, Manual = false,
                ClientPid = self.Id, ClientStart = ticks, CapturedAt = capture.CapturedAtUtcTicks, SnapshotId = capture.Id,
                SourcePid = capture.SourceProcessId, SourceStart = capture.SourceStartTicks, Sequence = capture.Sequence
            }, CancellationToken.None);
            await Task.Delay(150); // Old Disconnect() discarded the acknowledgement before a delayed client could read it.
            var acknowledgement = await ApplicationSessionRelay.ReadAsync<ApplicationSessionRelay.Reply>(delayedReader, CancellationToken.None);
            Equal(requestId, acknowledgement.RequestId);
            await ApplicationSessionRelay.WriteAsync(delayedReader, new { RequestId = requestId }, CancellationToken.None);
        }
        var healthyCount = delivered;

        async Task Reject(Action<Dictionary<string, object?>> mutate)
        {
            using var client = new NamedPipeClientStream(".", ApplicationSessionRelay.PipeName(binding), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(2000);
            var hello = await ApplicationSessionRelay.ReadAsync<JsonElement>(client, CancellationToken.None);
            var request = new Dictionary<string, object?>
            {
                ["FormatVersion"] = 1, ["Binding"] = binding, ["Challenge"] = hello.GetProperty("Challenge").GetString(),
                ["RequestId"] = Guid.NewGuid().ToString("N"), ["Manual"] = true, ["ClientPid"] = self.Id, ["ClientStart"] = ticks,
                ["CapturedAt"] = DateTimeOffset.UtcNow.UtcTicks, ["SnapshotId"] = Guid.NewGuid().ToString("N"),
                ["SourcePid"] = self.Id, ["SourceStart"] = ticks, ["Sequence"] = 10L, ["Cookie"] = "wshare_hash=fixture-rejected-relay"
            };
            mutate(request);
            await ApplicationSessionRelay.WriteAsync(client, request, CancellationToken.None);
            await Throws<IOException>(() => ApplicationSessionRelay.ReadAsync<ApplicationSessionRelay.Reply>(client, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(7)));
            Equal(healthyCount, delivered);
        }
        await Reject(request => request["Challenge"] = new string('0', 64));
        await Reject(request => request["Binding"] = new string('0', 64));
        await Reject(request => request["ClientStart"] = ticks + 1);
        await Reject(request => request["RequestId"] = "not-a-request-nonce");
        await Reject(request => request["RequestId"] = manual.RequestId);
        await Reject(request => request["CapturedAt"] = DateTimeOffset.UtcNow.AddMinutes(-11).UtcTicks);
        await Reject(request => request["Sequence"] = 0);
        await Reject(request => request["Cookie"] = "fixture\r\ninjected-header");
        using (var oversized = new NamedPipeClientStream(".", ApplicationSessionRelay.PipeName(binding), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await oversized.ConnectAsync(2000); _ = await ApplicationSessionRelay.ReadAsync<JsonElement>(oversized, CancellationToken.None);
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, ApplicationSessionRelay.MaximumMessageBytes + 1);
            await oversized.WriteAsync(prefix);
            await Throws<IOException>(() => ApplicationSessionRelay.ReadAsync<JsonElement>(oversized, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(7)));
        }
        Equal(healthyCount, delivered);
        using (var partial = new NamedPipeClientStream(".", ApplicationSessionRelay.PipeName(binding), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await partial.ConnectAsync(2000); _ = await ApplicationSessionRelay.ReadAsync<JsonElement>(partial, CancellationToken.None);
            await partial.WriteAsync(new byte[] { 1, 0 });
            await Throws<IOException>(() => ApplicationSessionRelay.ReadAsync<JsonElement>(partial, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(7)));
        }
        True((await Send(new ApplicationSessionSnapshot(), false)).SessionAccepted);

        // A squatting server's incorrect creation time is rejected before the client writes any credential payload.
        var fakeBinding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var fake = new NamedPipeServerStream(ApplicationSessionRelay.PipeName(fakeBinding), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        var receivedSecretBytes = -1;
        var fakeWorker = Task.Run(async () =>
        {
            await fake.WaitForConnectionAsync();
            await ApplicationSessionRelay.WriteAsync(fake, new { FormatVersion = 1, Binding = fakeBinding, Challenge = new string('a', 64),
                ServerPid = self.Id, ServerStart = ticks + 1, ClientPid = self.Id, ClientStart = ticks }, CancellationToken.None);
            receivedSecretBytes = await fake.ReadAsync(new byte[1]);
        });
        await Throws<InvalidDataException>(() => ApplicationSessionRelay.SendAsync(fakeBinding, image, first, true, CancellationToken.None));
        await fakeWorker.WaitAsync(TimeSpan.FromSeconds(5)); Equal(0, receivedSecretBytes);
        await Throws<InvalidDataException>(() => ApplicationSessionRelay.SendAsync(binding, image + ".wrong", first, false, CancellationToken.None));
    }
}
