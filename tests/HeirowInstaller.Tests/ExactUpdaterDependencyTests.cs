using System.Diagnostics;
using HeirowInstaller.Core;

internal sealed partial class AcceptanceSuite
{
    private async Task ExactUpdaterDependencies()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("exact-updater-dependencies");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ManualCheckApplicationProbe", "bin", configuration, "net10.0", "ManualCheckApplicationProbe.exe"));
        var processes = new List<(Process Process, string Root)>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var run = Path.Combine(area, "probe-" + i); Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run, ".manual-check-probe"), "heirowInstaller manual feedback acceptance\n");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--run-directory"); start.ArgumentList.Add(run);
                var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned dependency probe.");
                processes.Add((process, run));
                var watch = Stopwatch.StartNew(); while (!File.Exists(Path.Combine(run, "ready.json"))) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Owned dependency probe did not become ready."); await Task.Delay(20); }
            }
            var first = processes[0].Process; var second = processes[1].Process;
            var root = Path.Combine(area, "app"); Directory.CreateDirectory(root);
            var manifest = Manifest("test.exact-updater", "1");
            var identity = new RunningApplication(first.Id, first.StartTime.ToUniversalTime().Ticks, executable, "Owned updater dependency", executable, "manual", true);
            var target = RunningApplicationTarget.FromManifest(manifest, root).WithWaitOnlyProcesses(new[] { identity });
            var found = await RunningApplications.InspectAsync(target);
            Equal(1, found.Count); Equal(first.Id, found[0].ProcessId); True(!found[0].CanRequestClose);
            True(!found.Any(x => x.ProcessId == second.Id));
            await Throws<RunningApplicationsException>(() => RunningApplications.EnsureClosedAsync(target));
            True(!first.HasExited && !second.HasExited);
            var stale = RunningApplicationTarget.FromManifest(manifest, root).WithWaitOnlyProcesses(new[] { identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 } });
            Equal(0, (await RunningApplications.InspectAsync(stale)).Count);
            File.WriteAllText(Path.Combine(processes[0].Root, "exit.flag"), "exit"); await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, (await RunningApplications.InspectAsync(target)).Count); True(!second.HasExited);
        }
        finally
        {
            foreach (var (process, directory) in processes)
            {
                File.WriteAllText(Path.Combine(directory, "exit.flag"), "exit");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); process.Dispose();
            }
        }
    }
}
