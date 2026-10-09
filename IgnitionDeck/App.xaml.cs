using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace IgnitionDeck;

public partial class App : Application
{
    internal static string SettingsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "peersettings.json");
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Share the legacy manager's mutex to prevent competing XML/JSON writers.
        _instanceMutex = new Mutex(true, "Global\\VotePool.Peer.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            _instanceMutex.Dispose();
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            if (_ownsMutex)
            {
                _instanceMutex.ReleaseMutex();
                _ownsMutex = false;
            }
            _instanceMutex.Dispose();
        };
        _window.Activate();
    }

    internal static bool IsBootRunning()
    {
        var processes = Process.GetProcessesByName("VotePool.Peer.Boot");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
