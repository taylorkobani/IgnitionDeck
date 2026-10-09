using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using IgnitionDeck.Core;

namespace IgnitionDeck.Services;

/// <summary>Elevates only the short-lived task setup command, not the manager UI.</summary>
public static class RecoverySetupService
{
    public static async Task ConfigureAsync(bool enable, string settingsPath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve IgnitionDeck executable.");
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            await Task.Run(() =>
            {
                if (enable) RecoveryStartupTask.Enable(executable, settingsPath);
                else RecoveryStartupTask.Disable();
            });
            return;
        }
        var info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(enable ? "--enable-recovery" : "--disable-recovery");
        info.ArgumentList.Add("--settings");
        info.ArgumentList.Add(settingsPath);
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Automatic recovery setup was cancelled at the UAC prompt.", ex);
        }
        using (process)
        {
            if (process is null) throw new InvalidOperationException("Could not start elevated recovery setup.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Recovery task setup failed (exit {process.ExitCode}). Inspect %ProgramData%\\IgnitionDeck\\Recovery\\command-errors.log for details.");
        }
    }
}
