using System.Diagnostics;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed class SetupDownloadProgress(Action<InstallProgress> publish) : IProgress<InstallProgress>
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private TimeSpan lastReported;
    private TimeSpan sampleTime;
    private long sampleBytes;
    private bool sampleStarted;
    private double bytesPerSecond;

    public void Report(InstallProgress value)
    {
        if (value.CompletedBytes is not { } received || value.TotalBytes is not > 0)
        {
            publish(value);
            return;
        }
        var total = value.TotalBytes.Value;
        var now = elapsed.Elapsed;
        if (received > 0 && !sampleStarted)
        {
            sampleStarted = true;
            sampleTime = now;
            sampleBytes = received;
        }
        else if (received < total && sampleStarted)
        {
            var interval = (now - sampleTime).TotalSeconds;
            if ((now - lastReported).TotalMilliseconds < 100) return;
            if (received > sampleBytes && interval > 0)
            {
                var instant = (received - sampleBytes) / interval;
                // Weight new throughput by elapsed time rather than packet count.
                var weight = 1 - Math.Exp(-interval / 4);
                bytesPerSecond = bytesPerSecond > 0 ? bytesPerSecond + weight * (instant - bytesPerSecond) : instant;
                sampleTime = now;
                sampleBytes = received;
            }
        }
        lastReported = now;
        var detail = $"{received:N0} of {total:N0} bytes · {(double)received / total:P0}";
        if (received == total) detail += " · Verifying download";
        else if (now.TotalSeconds >= 1 && bytesPerSecond > 0)
        {
            var remainingSeconds = Math.Ceiling((total - received) / bytesPerSecond);
            detail += remainingSeconds >= TimeSpan.FromDays(1).TotalSeconds ? " · More than a day remaining"
                : $" · About {FormatRemaining(TimeSpan.FromSeconds(Math.Max(1, remainingSeconds)))} remaining";
        }
        else detail += " · Estimating time remaining";
        publish(value with { Message = detail });
    }

    private static string FormatRemaining(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}m"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s" : $"{duration.Seconds}s";
}
