using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace IgnitionDeck.Core;

public enum RecoveryTaskState { NotRegistered, Enabled, Disabled, NeedsRepair }
public sealed record RecoveryTaskStatus(RecoveryTaskState State, string Description);

/// <summary>Task Scheduler integration. Mutation is used only by the elevated setup command.</summary>
public static class RecoveryStartupTask
{
    public const string TaskName = "IgnitionDeck.Recovery";
    public const string LegacyTaskName = "VotePool.Peer.Boot";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string Arguments(string settingsPath) => $"--restore-running --settings \"{Path.GetFullPath(settingsPath)}\"";

    public static string CreateXml(string executable, string settingsPath)
    {
        executable = Path.GetFullPath(executable);
        settingsPath = Path.GetFullPath(settingsPath);
        if (executable.StartsWith(@"\\", StringComparison.Ordinal) || settingsPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Install IgnitionDeck and its settings on a local disk for SYSTEM startup recovery.");
        var n = TaskNamespace;
        return new XDocument(new XElement(n + "Task", new XAttribute("version", "1.2"),
            new XElement(n + "RegistrationInfo", new XElement(n + "Description", "Restore persisted Running VotePool workers without Windows logon.")),
            new XElement(n + "Triggers", new XElement(n + "BootTrigger", new XElement(n + "Enabled", true), new XElement(n + "Delay", "PT30S"))),
            new XElement(n + "Principals", new XElement(n + "Principal", new XAttribute("id", "Recovery"),
                new XElement(n + "UserId", "S-1-5-18"), new XElement(n + "RunLevel", "HighestAvailable"))),
            new XElement(n + "Settings", new XElement(n + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(n + "DisallowStartIfOnBatteries", false), new XElement(n + "StopIfGoingOnBatteries", false),
                new XElement(n + "AllowHardTerminate", false), new XElement(n + "StartWhenAvailable", true),
                new XElement(n + "Enabled", true), new XElement(n + "ExecutionTimeLimit", "PT10M"),
                new XElement(n + "RestartOnFailure", new XElement(n + "Interval", "PT1M"), new XElement(n + "Count", 3))),
            new XElement(n + "Actions", new XAttribute("Context", "Recovery"), new XElement(n + "Exec",
                new XElement(n + "Command", executable), new XElement(n + "Arguments", Arguments(settingsPath)),
                new XElement(n + "WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
    }

    public static RecoveryTaskStatus Inspect(string executable, string settingsPath) => WithFolder(folder =>
    {
        object? task = FindTask(folder, TaskName);
        if (task is null) return new RecoveryTaskStatus(RecoveryTaskState.NotRegistered, "Automatic recovery is not registered.");
        try
        {
            dynamic registered = task;
            var xml = XDocument.Parse((string)registered.Xml);
            var action = xml.Root?.Element(TaskNamespace + "Actions")?.Element(TaskNamespace + "Exec");
            var principal = xml.Root?.Element(TaskNamespace + "Principals")?.Element(TaskNamespace + "Principal");
            var correct = string.Equals((string?)action?.Element(TaskNamespace + "Command"), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                && string.Equals((string?)action?.Element(TaskNamespace + "Arguments"), Arguments(settingsPath), StringComparison.OrdinalIgnoreCase)
                && (string?)principal?.Element(TaskNamespace + "UserId") == "S-1-5-18"
                && xml.Root?.Element(TaskNamespace + "Triggers")?.Element(TaskNamespace + "BootTrigger") is not null;
            if (!correct) return new RecoveryTaskStatus(RecoveryTaskState.NeedsRepair, "Recovery task exists but its executable/settings/account/trigger differ. Repair it from this installation.");
            var enabled = (bool)registered.Enabled;
            return new RecoveryTaskStatus(enabled ? RecoveryTaskState.Enabled : RecoveryTaskState.Disabled,
                enabled ? "Enabled: starts as SYSTEM at Windows startup, without logon." : "Automatic recovery is disabled.");
        }
        finally { Release(task); }
    });

    public static void Enable(string executable, string settingsPath)
    {
        if (!File.Exists(executable) || !File.Exists(settingsPath)) throw new FileNotFoundException("Executable and settings must exist before registering recovery.");
        var xml = CreateXml(executable, settingsPath);
        WithFolder(folder =>
        {
            object registered = folder.RegisterTask(TaskName, xml, 6, "SYSTEM", null, 5, null);
            try
            {
                object? legacy = FindTask(folder, LegacyTaskName);
                if (legacy is not null)
                {
                    try { ((dynamic)legacy).Enabled = false; }
                    catch
                    {
                        // Do not leave two independent recovery launchers enabled.
                        ((dynamic)registered).Enabled = false;
                        throw;
                    }
                    finally { Release(legacy); }
                }
            }
            finally { Release(registered); }
            return true;
        });
    }

    public static void Disable() => WithFolder(folder =>
    {
        object? task = FindTask(folder, TaskName);
        if (task is null) return true;
        try { ((dynamic)task).Enabled = false; }
        finally { Release(task); }
        return true;
    });

    private static object? FindTask(dynamic folder, string name)
    {
        // GetTask throws for normal absence, and .NET maps HRESULT 0x80070002
        // to FileNotFoundException rather than COMException. Enumerate instead
        // so opening Settings does not throw when recovery is not registered.
        object tasks = folder.GetTasks(1); // Include hidden tasks.
        try
        {
            dynamic collection = tasks;
            var count = (int)collection.Count;
            for (var index = 1; index <= count; index++)
            {
                object task = collection.Item(index);
                var keep = false;
                try
                {
                    keep = string.Equals((string)((dynamic)task).Name, name, StringComparison.OrdinalIgnoreCase);
                    if (keep) return task;
                }
                finally { if (!keep) Release(task); }
            }
            return null;
        }
        finally { Release(tasks); }
    }
    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        object service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Cannot connect to Task Scheduler.");
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            return action(folder);
        }
        finally { if (folder is not null) Release(folder); Release(service); }
    }
    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
