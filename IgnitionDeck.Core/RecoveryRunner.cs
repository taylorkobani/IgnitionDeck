using System.Text.Json;

namespace IgnitionDeck.Core;

public sealed record RecoveryPass(int Expected, int Verified, IReadOnlyList<string> Failures);
public sealed record RecoveryOptions(int Attempts = 4, int RetrySeconds = 15, int ObservationSeconds = 3);
public sealed record RecoveryReport(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, int Attempts, int Expected, int Verified, int ExitCode, IReadOnlyList<string> Failures, string LogPath);

/// <summary>Bounded one-shot reboot recovery, not a continuously running service.</summary>
public sealed class RecoveryRunner
{
    private readonly PeerManager _manager;
    private readonly Func<TimeSpan, Task> _delay;
    public RecoveryRunner(PeerManager manager, Func<TimeSpan, Task>? delay = null)
    {
        _manager = manager;
        _delay = delay ?? (duration => Task.Delay(duration));
    }

    public static string LogDirectory(string launchPadRoot) => Path.Combine(launchPadRoot, "logs", "recovery");
    public static string StatusPath(string launchPadRoot) => Path.Combine(LogDirectory(launchPadRoot), "last-recovery.json");

    public static FileStream AcquireCoordination(string launchPadRoot)
    {
        Directory.CreateDirectory(launchPadRoot);
        return OperationDiagnostics.Execute($"Acquire recovery/management lock in '{launchPadRoot}'. Another recovery or management operation may still be running.",
            () => new FileStream(Path.Combine(launchPadRoot, ".ignitiondeck-management.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public async Task<RecoveryReport> RunAsync(RecoveryOptions? options = null)
    {
        options ??= new RecoveryOptions();
        if (options.Attempts is < 1 or > 10 || options.RetrySeconds is < 0 or > 120 || options.ObservationSeconds is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(options));
        var started = DateTimeOffset.UtcNow;
        var directory = LogDirectory(_manager.LaunchPadRoot);
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, $"recovery-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        void Write(string message) => File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        Write("Headless Running-only recovery started. Paused and Shutdown workers are not started.");
        var pass = new RecoveryPass(0, 0, []);
        var attempts = 0;
        for (var attempt = 1; attempt <= options.Attempts; attempt++)
        {
            attempts = attempt;
            try
            {
                pass = await _manager.RecoverRunningAsync(TimeSpan.FromSeconds(options.ObservationSeconds), _delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never silently report success for a missing or corrupt profile/configuration.
                pass = new RecoveryPass(0, 0, [OperationDiagnostics.Describe(ex)]);
            }
            Write($"Attempt {attempt}/{options.Attempts}: verified {pass.Verified}/{pass.Expected} workers; failures {pass.Failures.Count}.");
            foreach (var failure in pass.Failures) Write(failure);
            if (pass.Failures.Count == 0) break;
            if (attempt < options.Attempts) await _delay(TimeSpan.FromSeconds(options.RetrySeconds)).ConfigureAwait(false);
        }
        var report = new RecoveryReport(started, DateTimeOffset.UtcNow, attempts, pass.Expected, pass.Verified, pass.Failures.Count == 0 ? 0 : 1, pass.Failures, log);
        Write($"Recovery completed with exit code {report.ExitCode}. Process survival is not an application-level health check.");
        var status = StatusPath(_manager.LaunchPadRoot);
        var temporary = status + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, status, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return report;
    }

    public static string ReadSummary(string launchPadRoot)
    {
        var path = StatusPath(launchPadRoot);
        if (!File.Exists(path)) return "No headless recovery result recorded yet.";
        var report = JsonSerializer.Deserialize<RecoveryReport>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid recovery status.");
        return $"Last recovery: {report.CompletedUtc.LocalDateTime:g}; verified {report.Verified}/{report.Expected}; attempts {report.Attempts}; exit code {report.ExitCode}.\nLog: {report.LogPath}";
    }
}
