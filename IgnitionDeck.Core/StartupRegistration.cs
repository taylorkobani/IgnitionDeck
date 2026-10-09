using System.Diagnostics;
using Microsoft.Win32;

namespace IgnitionDeck.Core;

public static class StartupRegistration
{
    private const string TaskName = "VotePool.Peer.Boot";

    public static void Register(string bootPath)
    {
        if (!File.Exists(bootPath) || !string.Equals(Path.GetExtension(bootPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an existing Boot executable.", bootPath);
        Execute(["/Create", "/TN", TaskName, "/TR", $"\"{Path.GetFullPath(bootPath)}\"", "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F"]);
        RemoveLegacyRunRegistration();
    }

    public static void Remove()
    {
        // Query first; a missing task is already removed. Do not hide access-denied errors.
        var query = Execute(["/Query", "/TN", TaskName], allowFailure: true);
        if (query.ExitCode != 0 && !query.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            && !query.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(query.Output);
        if (query.ExitCode == 0) Execute(["/Delete", "/TN", TaskName, "/F"]);
        RemoveLegacyRunRegistration();
    }

    private static (int ExitCode, string Output) Execute(string[] arguments, bool allowFailure = false)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start schtasks.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"Startup task operation failed. Run IgnitionDeck as Administrator and retry. {output}");
        return (process.ExitCode, output);
    }

    private static void RemoveLegacyRunRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("VotePool.Peer", false);
        key?.DeleteValue(TaskName, false);
    }
}
