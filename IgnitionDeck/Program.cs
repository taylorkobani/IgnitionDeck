using System.Diagnostics;
using System.Runtime.CompilerServices;
using IgnitionDeck.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace IgnitionDeck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var settings = ApplicationSettings.DefaultFilePath;
            var command = string.Empty;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--settings":
                        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--settings requires a file path.");
                        settings = Path.GetFullPath(args[index]);
                        break;
                    case "--restore-running":
                    case "--enable-recovery":
                    case "--disable-recovery":
                        if (command.Length > 0) throw new ArgumentException("Only one recovery/setup command is allowed.");
                        command = args[index];
                        break;
                    case "--no-restore": break; // Backward compatibility: UI startup is now always read-only.
                    default: throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }
            // Branch before touching App, Application.Start, ComWrappers, or a dispatcher.
            if (command == "--restore-running")
            {
                ApplicationSettings.ValidateRecoveryFile(settings);
                var manager = new PeerManager(settings);
                using var coordination = RecoveryRunner.AcquireCoordination(manager.LaunchPadRoot);
                return new RecoveryRunner(manager).RunAsync().GetAwaiter().GetResult().ExitCode;
            }
            if (command is "--enable-recovery" or "--disable-recovery")
            {
                if (command == "--enable-recovery") RecoveryStartupTask.Enable(Environment.ProcessPath!, settings);
                else RecoveryStartupTask.Disable();
                return 0;
            }
            StartDesktop(settings);
            return 0;
        }
        catch (Exception ex)
        {
            WriteFatal(ex);
            return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartDesktop(string settings)
    {
        App.SettingsPath = settings;
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    private static void WriteFatal(Exception exception)
    {
        var message = OperationDiagnostics.Describe(exception);
        Console.Error.WriteLine(message);
        Debug.WriteLine(exception);
        // Settings may not be readable, so use a fixed machine-wide diagnostic location.
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IgnitionDeck", "Recovery");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "command-errors.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { /* Exit code remains nonzero even if machine-wide logging is unavailable. */ }
    }
}
