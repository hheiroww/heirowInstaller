using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace HeirowInstaller.App;

internal sealed partial class InstallerGame
{
    internal FrameMeasurements? Performance { get; set; }
}

internal sealed class FrameMeasurements
{
    internal readonly ConcurrentQueue<double> Updates = new(), Draws = new(), DrawIntervals = new();
    private long previousDraw;
    internal long BeginUpdate() => Stopwatch.GetTimestamp();
    internal void EndUpdate(long started) => Updates.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    internal long BeginDraw()
    {
        var now = Stopwatch.GetTimestamp();
        if (previousDraw != 0) DrawIntervals.Enqueue(Stopwatch.GetElapsedTime(previousDraw, now).TotalMilliseconds);
        previousDraw = now;
        return now;
    }
    internal void EndDraw(long started) => Draws.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}

// Uses the real scene, controller and progress channel without global input,
// authentication, installation, registry writes or changes to the supplied package.
internal static class UiResponsivenessSmoke
{
    internal static async Task RunAsync(InstallerGame game, AppArguments args)
    {
        var output = args.PerformanceSmokeDirectory!;
        var timings = new FrameMeasurements();
        var navigation = new List<double>();
        var navigationCompletion = new List<double>();
        if (!args.Isolated || !args.NoRegister || Directory.Exists(output))
        {
            await game.OnUiAsync(() => { Environment.ExitCode = 2; game.Exit(); });
            return;
        }
        try
        {
            Directory.CreateDirectory(output);
            var startupFramesRendered = game.FramesRendered;
            if (startupFramesRendered < 1) throw new InvalidOperationException("Package verification began before the first rendered window frame.");
            await game.MainScene.Controller.OpenPackageAsync(args.PackagePath!);
            await Wait(() => game.MainScene.Controller.Manifest is not null && !game.MainScene.Controller.Busy);
            await Task.Delay(1500);
            await game.OnUiAsync(() => game.Performance = timings);
            // Exercise forward navigation and signed-package planning before Review.
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await game.OnUiAsync(() => game.MainScene.Controller.Stage = SetupStage.Package);
                foreach (var stage in new[] { SetupStage.Features, SetupStage.Destination, SetupStage.Review })
                {
                    var watch = Stopwatch.StartNew();
                    await game.OnUiAsync(() => game.MainScene.Controller.NextStage());
                    navigation.Add(watch.Elapsed.TotalMilliseconds);
                    await Wait(() => game.MainScene.Controller.Stage == stage && !game.MainScene.Controller.Busy);
                    navigationCompletion.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
            await game.OnUiAsync(() => { game.MainScene.Controller.Stage = SetupStage.Progress; game.MainScene.Controller.Notify(); });
            await Task.Delay(500);
            var before = await Ui(() => new { game.MainScene.RebuildCount, game.MainScene.ArtworkDecodeCount });
            var reporter = await Ui(() => game.MainScene.Controller.Reporter());
            var flood = Stopwatch.StartNew();
            await Task.Run(() => { for (var i = 1; i <= 100000; i++) reporter.Report(new("verify", "Checking application files", i, 100000)); });
            await game.OnUiAsync(() => { });
            var progressMilliseconds = flood.Elapsed.TotalMilliseconds;
            await Task.Delay(500);
            var after = await Ui(() => new { game.MainScene.RebuildCount, game.MainScene.ArtworkDecodeCount, game.MainScene.Controller.Progress });
            await game.OnUiAsync(() => game.Performance = null);
            static object Stats(IEnumerable<double> samples)
            {
                var values = samples.Order().ToArray();
                return new { count = values.Length, maxMilliseconds = values.Length == 0 ? 0 : values[^1],
                    p95Milliseconds = values.Length == 0 ? 0 : values[(int)Math.Floor((values.Length - 1) * .95)] };
            }
            var result = new { completed = true, isolated = true, installed = false, globalInput = false, packageChanged = false,
                startupFramesRendered, aaSamples = 16, filtering = "Anisotropic16x", update = Stats(timings.Updates), draw = Stats(timings.Draws),
                drawIntervals = Stats(timings.DrawIntervals), navigationDispatchMilliseconds = navigation, navigationCompletionMilliseconds = navigationCompletion,
                progressReports = 100000, progressMilliseconds, progressRebuilds = after.RebuildCount - before.RebuildCount,
                progressArtworkDecodes = after.ArtworkDecodeCount - before.ArtworkDecodeCount, finalProgress = after.Progress };
            await File.WriteAllTextAsync(Path.Combine(output, "ui-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            await game.CaptureAsync(Path.Combine(output, "progress.png"));
            await game.OnUiAsync(() => game.Exit());
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "error.json"), JsonSerializer.Serialize(new { error = error.ToString() }));
            await game.OnUiAsync(() => { Environment.ExitCode = 1; game.Exit(); });
        }
        async Task<T> Ui<T>(Func<T> read) { T value = default!; await game.OnUiAsync(() => value = read()); return value; }
        async Task Wait(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (!await Ui(condition))
            {
                if (deadline.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("The performance fixture did not reach the next stage.");
                await Task.Delay(25);
            }
        }
    }
}
