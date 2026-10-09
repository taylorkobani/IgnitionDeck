using System.Diagnostics;

namespace IgnitionDeck.Core;

public interface IWorkerProcesses
{
    bool IsMatch(int pid, string appPath, string appName);
    int? FindMatchingPid(string appPath, string appName) => null;
    int Start(string appPath, string appName);
    IReadOnlyList<string> ForceStopWorkers();
}

public sealed class WorkerProcesses : IWorkerProcesses
{
    public int? FindMatchingPid(string appPath, string appName)
    {
        var processes = Process.GetProcessesByName(appName);
        try
        {
            var matches = processes.Where(process => IsMatch(process.Id, appPath, appName)).Select(process => process.Id).ToList();
            if (matches.Count > 1) throw new InvalidOperationException($"Multiple processes already run '{appName}' from '{appPath}'. Refusing another launch.");
            return matches.Count == 0 ? null : matches[0];
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool IsMatch(int pid, string appPath, string appName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(OperationDiagnostics.Execute(
                $"Inspect executable for worker '{appName}', PID {pid}, expected path '{Path.Combine(appPath, appName + ".exe")}'.",
                () => process.MainModule?.FileName),
                Path.Combine(appPath, appName + ".exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // Fail closed: access-denied must not be interpreted as safe-to-delete or safe-to-duplicate.
    }

    public int Start(string appPath, string appName)
    {
        var executable = Path.Combine(appPath, appName + ".exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Worker executable not found.", executable);
        using var process = OperationDiagnostics.Execute($"Start worker executable '{executable}' with working directory '{appPath}'.",
            () => Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = appPath, UseShellExecute = false, CreateNoWindow = true
        })) ?? throw new InvalidOperationException($"Could not start {appName}.");
        return process.Id;
    }

    public IReadOnlyList<string> ForceStopWorkers()
    {
        var results = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!process.ProcessName.StartsWith("VotePool.Peer.Worker", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = process.ProcessName;
                    process.Kill(entireProcessTree: true);
                    results.Add(process.WaitForExit(5000) ? $"Stopped {name}" : $"Still exiting: {name}");
                }
                catch (Exception ex) { results.Add($"PID {process.Id}: {ex.Message}"); }
            }
        }
        return results;
    }
}
