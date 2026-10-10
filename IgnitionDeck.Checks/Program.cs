using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using IgnitionDeck.Core;

namespace IgnitionDeck.Checks;

internal static class Program
{
    private const string AppName = "VotePool.Peer.Worker.TestWorker";
    private static int _passed;

    private static int Main()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "IgnitionDeck-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            RunChecks(sandbox);
            RunRecoveryChecks(sandbox);
            Console.WriteLine($"PASS: {_passed} isolated checks; no real worker, registry, scheduled-task, or production LaunchPad operations.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(sandbox, true); }
    }

    private static void RunChecks(string sandbox)
    {
        var root = Path.Combine(sandbox, "LaunchPad");
        var settings = Path.Combine(sandbox, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { LaunchPadRoot = root, Unrelated = "retained" }));
        var fake = new FakeProcesses();
        var manager = new PeerManager(settings, fake);
        manager.Initialize();
        var appSettings = ApplicationSettings.Load(settings);
        Check("App settings load root and default polling", appSettings.LaunchPadRoot == root && appSettings.PollingIntervalSeconds == 5);
        appSettings.SavePollingInterval(12);
        manager.SaveLaunchPadRoot(root);
        Check("Root save preserves polling", ApplicationSettings.Load(settings).PollingIntervalSeconds == 12);
        appSettings.SavePollingInterval(9);
        Check("Polling save preserves root", new PeerManager(settings, fake).LaunchPadRoot == root);
        Throws<ArgumentOutOfRangeException>("Invalid polling rejected", () => appSettings.SavePollingInterval(0));
        ApplicationSettings.ValidateRecoveryFile(settings);
        var missingRootSettings = Path.Combine(sandbox, "missing-root-settings.json");
        File.WriteAllText(missingRootSettings, "{\"PollingIntervalSeconds\":5}");
        Throws<InvalidDataException>("Recovery rejects app settings without root", () => ApplicationSettings.ValidateRecoveryFile(missingRootSettings));
        Check("Initial XML contracts", XDocument.Load(Path.Combine(root, "ExecutionProfile.xml")).Root!.Name == "ExecutionProfile"
            && XDocument.Load(Path.Combine(root, "AppRepo.xml")).Root!.Name == "AppRepo");
        Check("Environment aliases", PeerManager.NormalizeEnvironment("Development") == "Dev" && PeerManager.NormalizeEnvironment("Production") == "Prod");
        Throws<ArgumentException>("Unknown environment rejected", () => manager.GetRevisions("QA"));

        var source = Path.Combine(sandbox, "Peer Build 42 [20260712]");
        Directory.CreateDirectory(Path.Combine(source, AppName));
        Directory.CreateDirectory(Path.Combine(source, "Infrastructure"));
        File.WriteAllText(Path.Combine(source, AppName, AppName + ".exe"), "fake executable, never executed");
        Directory.CreateDirectory(Path.Combine(source, AppName, "empty-folder"));
        File.WriteAllText(Path.Combine(source, "Infrastructure", "peersettings.json"), JsonSerializer.Serialize(new
        {
            Peer = new
            {
                ServiceMap = new Dictionary<string, string> { [AppName] = "TestWorker" },
                Services = new { TestWorker = new { RunState = "Running", Pid = 999 } }
            },
            Unrelated = "retained"
        }));
        File.WriteAllText(Path.Combine(source, "Infrastructure", "peersettings.Development.json"), "{}");
        File.WriteAllText(Path.Combine(source, "Infrastructure", "peersettings.Production.json"), "{}");
        var imported = manager.ImportBuild(source, replace: false);
        var build = manager.GetBuilds().Single();
        Check("Build import and empty folders", build.Name == Path.GetFileName(source) && Directory.Exists(Path.Combine(imported, AppName, "empty-folder")));
        Check("App folder index", XDocument.Load(Path.Combine(root, "Builds", "AppFolders.xml")).Root!.Element("name")!.Attribute("value")!.Value == AppName);
        Check("Expected service repository", manager.GetProfiles("Dev").Single().Service == "TestWorker");
        Throws<IOException>("Replacement requires approval", () => manager.ImportBuild(source, replace: false));
        Throws<InvalidOperationException>("Self-import rejected", () => manager.ImportBuild(imported, replace: true));
        Throws<ArgumentException>("Traversal build delete rejected", () => manager.DeleteBuild(".."));

        var revisionPath = manager.CreateRevision(build.Name, [AppName], "Development");
        var revision = Path.GetFileName(revisionPath);
        Check("Revision naming", revision == "Revision dev-42-[1]");
        Check("Development config isolation", File.Exists(Path.Combine(revisionPath, "Infrastructure", "peersettings.Development.json"))
            && !File.Exists(Path.Combine(revisionPath, "Infrastructure", "peersettings.Production.json")));
        var prodPath = manager.CreateRevision(build.Name, [AppName], "Prod");
        Check("Production config isolation", File.Exists(Path.Combine(prodPath, "Infrastructure", "peersettings.Production.json"))
            && !File.Exists(Path.Combine(prodPath, "Infrastructure", "peersettings.Development.json")));
        Throws<ArgumentException>("Empty revision selection rejected", () => manager.CreateRevision(build.Name, [], "Dev"));
        var replica = manager.GetReplicas("Dev", revision).Single();
        Check("New revisions clear source state/PID", replica.State == RunState.Shutdown && replica.Pid is null);
        Check("Refresh never starts workers", fake.Starts == 0 && manager.GetRevisions("Dev").Single().ActiveApps == 0);

        var configFile = Path.Combine(revisionPath, "Infrastructure", "peersettings.json");
        var infrastructurePath = Path.GetDirectoryName(configFile)!;
        // Match a configuration reader that permits reads/writes but not atomic replacement.
        using (var reader = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var pending = Task.Run(() => manager.SetState(replica, RunState.Running));
            try
            {
                Check("Replacement reaches staged file with a shared reader", SpinWait.SpinUntil(
                    () => Directory.GetFiles(infrastructurePath, "*.tmp").Length > 0 || pending.IsCompleted, TimeSpan.FromSeconds(2)) && !pending.IsCompleted);
                Thread.Sleep(100);
                Check("Blocked replacement never launches early", !pending.IsCompleted && fake.Starts == 0);
            }
            finally { reader.Dispose(); pending.GetAwaiter().GetResult(); }
        }
        Check("Replacement retries succeed after reader releases", fake.Starts == 1 && manager.GetReplicas("Dev", revision).Single().IsLive);
        manager.SetState(replica, RunState.Running);
        replica = manager.GetReplicas("Dev", revision).Single();
        Check("Running launch and PID", replica.IsLive && replica.Pid is not null && fake.Starts == 1);
        Check("Running XML profile", manager.GetProfiles("Dev").Single().Entry!.State == RunState.Running);
        RunPollingChecks(manager, fake, revision, configFile, Path.Combine(root, "ExecutionProfile.xml"));
        Check("Unrelated JSON and exact ServiceMap retained", Config(revisionPath)["Unrelated"]!.GetValue<string>() == "retained"
            && Config(revisionPath)["Peer"]!["ServiceMap"]![AppName]!.GetValue<string>() == "TestWorker");
        var intactConfig = File.ReadAllText(configFile);
        var intactProfile = File.ReadAllText(Path.Combine(root, "ExecutionProfile.xml"));
        using (var reader = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Exception? failure = null;
            try { manager.SetState(replica, RunState.Paused); }
            catch (Exception ex) { failure = ex; }
            Check("Persistent sharing denial still fails", failure is IOException or UnauthorizedAccessException);
            var details = OperationDiagnostics.Describe(failure!);
            Check("File denial identifies replacement operation and path", details.Contains("Atomically replace destination") && details.Contains(configFile));
            Check("File denial includes original type and HRESULT", details.Contains(failure!.GetType().Name) && details.Contains("HRESULT 0x"));
            Check("Failed replacement never truncates original config", File.ReadAllText(configFile) == intactConfig);
            Check("Failed replacement leaves profile intact", File.ReadAllText(Path.Combine(root, "ExecutionProfile.xml")) == intactProfile);
            Check("Failed replacement removes staged file", Directory.GetFiles(infrastructurePath, "*.tmp").Length == 0);
        }
        Throws<InvalidOperationException>("Live revision delete blocked", () => manager.DeleteRevision("Dev", revision));
        Throws<InvalidOperationException>("Live root switch blocked", () => manager.SaveLaunchPadRoot(Path.Combine(sandbox, "OtherRoot")));
        manager.SetState(replica, RunState.Paused);
        replica = manager.GetReplicas("Dev", revision).Single();
        Check("Pause retains matching PID", replica.State == RunState.Paused && replica.IsLive && fake.Starts == 1);
        manager.SetRevisionState("Dev", revision, RunState.Running);
        Check("Bulk launch preserves pause", manager.GetReplicas("Dev", revision).Single().State == RunState.Paused);
        manager.Restore();
        Check("Restore adopts live PID without duplicates", fake.Starts == 1);

        fake.Live.Clear();
        fake.BeforeStart = () => throw new Win32Exception(5);
        var deniedRestore = manager.Restore();
        Check("Restore denial identifies executable launch and app path", deniedRestore.Single().Contains("Start worker") && deniedRestore.Single().Contains(replica.AppPath));
        Check("Restore denial preserves Win32 error", deniedRestore.Single().Contains("Win32Exception") && deniedRestore.Single().Contains("Win32 5"));
        Check("Denied restore does not count a worker start", fake.Starts == 1);
        fake.BeforeStart = () => Check("Paused desired state persisted before launch", Config(revisionPath)["Peer"]!["Services"]!["TestWorker"]!["RunState"]!.GetValue<string>() == "Paused");
        Check("Paused stale profile restore", manager.Restore().Count == 0 && fake.Starts == 2 && manager.GetReplicas("Dev", revision).Single().State == RunState.Paused);
        fake.BeforeStart = null;
        replica = manager.GetReplicas("Dev", revision).Single();
        manager.SetState(replica, RunState.Shutdown);
        replica = manager.GetReplicas("Dev", revision).Single();
        Check("Graceful shutdown retains draining PID", replica.IsLive && replica.State == RunState.Shutdown && replica.Pid is not null);
        Check("Shutdown removed from Boot profile", XDocument.Load(Path.Combine(root, "ExecutionProfile.xml")).Root!.Elements("App").Count() == 0);
        Throws<InvalidOperationException>("Draining delete blocked", () => manager.DeleteRevision("Dev", revision));
        var restartedManager = new PeerManager(settings, fake);
        restartedManager.Initialize();
        Throws<InvalidOperationException>("Draining delete blocked after manager restart", () => restartedManager.DeleteRevision("Dev", revision));
        fake.Live.Clear();
        manager.ReconcileExitedProcesses();
        Check("Exited worker clears PID", manager.GetReplicas("Dev", revision).Single().Pid is null);

        fake.BeforeStart = () => throw new Win32Exception(5);
        InvalidOperationException? bulkFailure = null;
        try { manager.SetRevisionState("Dev", revision, RunState.Running); }
        catch (InvalidOperationException ex) { bulkFailure = ex; }
        Check("Bulk summary retains original exceptions", bulkFailure?.InnerException is AggregateException aggregate
            && aggregate.InnerExceptions.Single() is Win32Exception native && native.NativeErrorCode == 5);
        Check("Bulk summary identifies launch operation", bulkFailure!.Message.Contains("Start worker") && bulkFailure.Message.Contains(replica.AppPath));

        var originalAttributes = File.GetAttributes(configFile);
        fake.BeforeStart = () =>
        {
            File.SetAttributes(configFile, originalAttributes | FileAttributes.ReadOnly);
            throw new Win32Exception(5);
        };
        Exception? preserved = null;
        try { manager.SetState(replica, RunState.Running); }
        catch (Exception ex) { preserved = ex; }
        finally { File.SetAttributes(configFile, originalAttributes); fake.BeforeStart = null; }
        Check("Rollback denial does not replace original launch exception", preserved is Win32Exception launch && launch.NativeErrorCode == 5);
        Check("Secondary rollback error remains inspectable", preserved!.Data["IgnitionDeck.RollbackFailure"] is Exception);
        manager.SetState(replica, RunState.Shutdown);

        var appPath = replica.AppPath;
        var logs = Path.Combine(appPath, "logs");
        Directory.CreateDirectory(Path.Combine(logs, "errors"));
        var error = Path.Combine(logs, "errors", "error.txt");
        File.WriteAllText(error, "test error");
        File.WriteAllText(Path.Combine(logs, "worker.log"), "test log");
        Check("Log browser excludes errors", manager.GetLogs(appPath).Count == 1 && PeerManager.ReadLog(manager.GetLogs(appPath)[0]) == "test log");
        var archived = manager.ArchiveError(error);
        Check("Error archive", File.Exists(archived) && !File.Exists(error) && PeerManager.ReadLog(archived) == "test error");
        File.WriteAllText(error, "second error");
        var secondArchive = manager.ArchiveError(error);
        Check("Archive avoids collisions", secondArchive != archived && File.Exists(secondArchive));

        var invalidConfig = Config(revisionPath);
        invalidConfig["Peer"]!["ServiceMap"]![AppName] = "WrongMapping";
        File.WriteAllText(Path.Combine(revisionPath, "Infrastructure", "peersettings.json"), invalidConfig.ToJsonString());
        var starts = fake.Starts;
        Throws<InvalidDataException>("Invalid ServiceMap rejected before launch", () => manager.SetState(replica, RunState.Running));
        Check("Invalid mapping did not launch", starts == fake.Starts);
        invalidConfig["Peer"]!["ServiceMap"]![AppName] = "TestWorker";
        File.WriteAllText(Path.Combine(revisionPath, "Infrastructure", "peersettings.json"), invalidConfig.ToJsonString());
        manager.SetState(replica, RunState.Running);
        var profileFile = Path.Combine(root, "ExecutionProfile.xml");
        var profileText = File.ReadAllText(profileFile);
        manager.ForceStopWorkers();
        Check("Force stop retains restore intent", File.ReadAllText(profileFile) == profileText && fake.Live.Count == 0);
        Check("Stale refresh is read-only", manager.GetReplicas("Dev", revision).Single().State == RunState.Running && fake.Starts == starts + 1);
        File.WriteAllText(profileFile, "corrupt XML");
        Throws<XmlException>("Corrupt profile blocks flush", () => manager.FlushUnused("Dev"));
        Check("Corrupt profile did not delete revision", Directory.Exists(revisionPath));
        File.WriteAllText(profileFile, profileText);
        var profileDocument = XDocument.Load(profileFile);
        profileDocument.Root!.Element("App")!.SetAttributeValue("RevisionFolder", "..\\escape");
        profileDocument.Save(profileFile);
        Throws<ArgumentException>("Profile traversal rejected", () => manager.Restore());
        File.WriteAllText(profileFile, "<ExecutionProfile />");
        manager.DeleteRevision("Dev", revision);
        Check("Unused revision delete", !Directory.Exists(revisionPath));
        Check("Flush unused production", manager.FlushUnused("Prod") == 1 && !Directory.Exists(prodPath));
        manager.SaveLaunchPadRoot(Path.Combine(sandbox, "FinalRoot"));
        Check("Settings preserve unrelated properties", JsonNode.Parse(File.ReadAllText(settings))!["Unrelated"]!.GetValue<string>() == "retained");
        Check("No real processes invoked", fake.Starts == 3);
    }

    private static void RunRecoveryChecks(string sandbox)
    {
        var root = Path.Combine(sandbox, "RecoveryLaunchPad");
        var settings = Path.Combine(sandbox, "recovery-settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { LaunchPadRoot = root }));
        var fake = new FakeProcesses();
        var manager = new PeerManager(settings, fake);
        manager.Initialize();
        var revision = "Revision prod-1-[1]";
        var path = Path.Combine(root, "Revisions", "Prod", revision);
        var infrastructure = Path.Combine(path, "Infrastructure");
        Directory.CreateDirectory(infrastructure);
        var pausedName = "VotePool.Peer.Worker.PausedWorker";
        foreach (var name in new[] { AppName, pausedName }) Directory.CreateDirectory(Path.Combine(path, name));
        File.WriteAllText(Path.Combine(infrastructure, "peersettings.json"), JsonSerializer.Serialize(new
        {
            Peer = new { Services = new { TestWorker = new { RunState = "Running" }, PausedWorker = new { RunState = "Paused" } } }
        }));
        var profilePath = Path.Combine(root, "ExecutionProfile.xml");
        void WriteProfile(bool missingState = false) => new XDocument(new XElement("ExecutionProfile",
            new XElement("App", new XAttribute("Name", AppName), new XAttribute("Environment", "Prod"), new XAttribute("RevisionFolder", revision),
                missingState ? null : new XAttribute("RunState", "Running")),
            new XElement("App", new XAttribute("Name", pausedName), new XAttribute("Environment", "Prod"), new XAttribute("RevisionFolder", revision), new XAttribute("RunState", "Paused")))).Save(profilePath);
        WriteProfile();
        var fast = new RecoveryOptions(Attempts: 3, RetrySeconds: 0, ObservationSeconds: 1);
        var runner = new RecoveryRunner(manager, _ => Task.CompletedTask);
        var result = runner.RunAsync(fast).GetAwaiter().GetResult();
        Check("Recovery starts only Running workers", result.ExitCode == 0 && result.Expected == 1 && result.Verified == 1 && fake.Starts == 1);
        Check("Recovery leaves Paused intent unchanged", Config(path)["Peer"]!["Services"]!["PausedWorker"]!["RunState"]!.GetValue<string>() == "Paused");
        Check("Recovery writes persistent status and log", File.Exists(result.LogPath) && File.Exists(RecoveryRunner.StatusPath(root)) && RecoveryRunner.ReadSummary(root).Contains("verified 1/1"));
        var originalStarts = fake.Starts;
        WriteProfile();
        var stale = Config(path);
        stale["Peer"]!["Services"]!["TestWorker"]!["Pid"] = 999999;
        File.WriteAllText(Path.Combine(infrastructure, "peersettings.json"), stale.ToJsonString());
        result = runner.RunAsync(fast).GetAwaiter().GetResult();
        Check("Recovery adopts matching executable with stale config/profile PIDs", result.ExitCode == 0 && fake.Starts == originalStarts);

        fake.Live.Clear();
        var failures = 0;
        fake.BeforeStart = () => { if (++failures == 1) throw new Win32Exception(5); };
        result = runner.RunAsync(fast).GetAwaiter().GetResult();
        Check("Recovery retries a failed startup", result.ExitCode == 0 && result.Attempts == 2 && fake.Starts == originalStarts + 1);
        Check("Recovery logs failed attempts", File.ReadAllText(result.LogPath).Contains("Win32 5"));
        fake.BeforeStart = null;

        fake.Live.Clear();
        var exits = 0;
        var exiting = new RecoveryRunner(manager, _ => { if (++exits % 2 == 1) fake.Live.Clear(); return Task.CompletedTask; });
        result = exiting.RunAsync(new RecoveryOptions(Attempts: 2, RetrySeconds: 0, ObservationSeconds: 1)).GetAwaiter().GetResult();
        Check("Early exits cause bounded failure instead of false success", result.ExitCode != 0 && result.Attempts == 2 && result.Verified == 0);
        Check("Early exit retains Running profile for later recovery", XDocument.Load(profilePath).Root!.Elements("App").Any(app => (string?)app.Attribute("Name") == AppName && (string?)app.Attribute("RunState") == "Running"));

        fake.Live.Clear();
        var startsBeforeCorruption = fake.Starts;
        WriteProfile(missingState: true);
        result = runner.RunAsync(new RecoveryOptions(Attempts: 1, RetrySeconds: 0, ObservationSeconds: 0)).GetAwaiter().GetResult();
        Check("Missing persisted RunState fails closed", result.ExitCode != 0 && fake.Starts == startsBeforeCorruption);
        File.WriteAllText(profilePath, "corrupt XML");
        result = runner.RunAsync(new RecoveryOptions(Attempts: 1, RetrySeconds: 0, ObservationSeconds: 0)).GetAwaiter().GetResult();
        Check("Corrupt recovery profile returns nonzero and logs failure", result.ExitCode != 0 && File.ReadAllText(result.LogPath).Contains("XmlException"));
        File.WriteAllText(profilePath, "<ExecutionProfile />");
        result = runner.RunAsync(fast).GetAwaiter().GetResult();
        Check("Empty profile is a successful no-op", result.ExitCode == 0 && result.Expected == 0 && fake.Starts == startsBeforeCorruption);
        using (var coordination = RecoveryRunner.AcquireCoordination(root))
            Throws<IOException>("Concurrent recovery/mutation blocked", () => { using var second = RecoveryRunner.AcquireCoordination(root); });

        XNamespace n = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var executable = Path.Combine(sandbox, "Installed & Local", "IgnitionDeck.exe");
        var xml = XDocument.Parse(RecoveryStartupTask.CreateXml(executable, settings));
        Check("Startup definition uses SYSTEM service-account logon", xml.Root!.Element(n + "Principals")!.Element(n + "Principal")!.Element(n + "UserId")!.Value == "S-1-5-18"
            && xml.Root.Element(n + "Principals")!.Element(n + "Principal")!.Element(n + "RunLevel")!.Value == "HighestAvailable");
        Check("Startup definition uses BootTrigger rather than logon", xml.Root.Element(n + "Triggers")!.Element(n + "BootTrigger") is not null && xml.Descendants(n + "LogonTrigger").Count() == 0);
        Check("Startup action calls same executable in headless mode", xml.Root.Element(n + "Actions")!.Element(n + "Exec")!.Element(n + "Command")!.Value == executable
            && xml.Root.Element(n + "Actions")!.Element(n + "Exec")!.Element(n + "Arguments")!.Value == RecoveryStartupTask.Arguments(settings));
        Check("Startup task allows battery operation and ignores overlapping starts", xml.Root.Element(n + "Settings")!.Element(n + "DisallowStartIfOnBatteries")!.Value == "false"
            && xml.Root.Element(n + "Settings")!.Element(n + "MultipleInstancesPolicy")!.Value == "IgnoreNew");
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable for in-memory validation.");
        object scheduler = Activator.CreateInstance(schedulerType)!;
        object? definition = null;
        try
        {
            ((dynamic)scheduler).Connect();
            definition = ((dynamic)scheduler).NewTask(0);
            ((dynamic)definition).XmlText = xml.ToString();
            Check("Windows Task Scheduler accepts recovery XML in memory", ((dynamic)definition).XmlText is string);
        }
        finally
        {
            if (definition is not null) Marshal.FinalReleaseComObject(definition);
            Marshal.FinalReleaseComObject(scheduler);
        }
        Throws<ArgumentException>("Network executable registration rejected", () => RecoveryStartupTask.CreateXml(@"\\server\share\IgnitionDeck.exe", settings));
        var missingTaskExceptions = 0;
        void ObserveException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException) missingTaskExceptions++;
        }
        AppDomain.CurrentDomain.FirstChanceException += ObserveException;
        try
        {
            var status = RecoveryStartupTask.Inspect(executable, settings);
            Check("Read-only recovery inspection returns task status", Enum.IsDefined(status.State));
            Check("Task inspection does not throw first-chance missing-file exceptions", missingTaskExceptions == 0);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= ObserveException; }
    }

    private static void RunPollingChecks(PeerManager manager, FakeProcesses fake, string revision, string configFile, string profileFile)
    {
        var replicas = manager.GetReplicas("Dev", revision);
        var profiles = manager.GetProfiles("Dev");
        var replica = replicas.Single();
        var pid = replica.Pid!.Value;
        var replacementPid = pid + 10000;
        var starts = fake.Starts;
        var originalConfig = File.ReadAllText(configFile);
        var originalProfile = File.ReadAllText(profileFile);
        try
        {
            Check("Unchanged replica polls compare equal", replicas.SequenceEqual(manager.GetReplicas("Dev", revision)));
            Check("Unchanged profile polls compare equal", profiles.SequenceEqual(manager.GetProfiles("Dev")));

            fake.Live.Remove(pid);
            var exitedReplicas = manager.GetReplicas("Dev", revision);
            var exitedProfiles = manager.GetProfiles("Dev");
            Check("Replica poll detects worker exit", !replicas.SequenceEqual(exitedReplicas)
                && exitedReplicas.Single().Status == "Terminated" && exitedReplicas.Single().Pid == pid);
            Check("Profile poll detects worker exit", !profiles.SequenceEqual(exitedProfiles)
                && exitedProfiles.Single(profile => profile.Entry is not null).Status == "Terminated"
                && exitedProfiles.Any(profile => profile.Status == "Missing"));
            Check("Repeated exited polls compare equal", exitedReplicas.SequenceEqual(manager.GetReplicas("Dev", revision))
                && exitedProfiles.SequenceEqual(manager.GetProfiles("Dev")));
            Check("Exit polling preserves execution intent and PID", File.ReadAllText(configFile) == originalConfig
                && File.ReadAllText(profileFile) == originalProfile);

            fake.Live[pid] = replica.AppPath;
            Check("Poll detects worker becoming live again", replicas.SequenceEqual(manager.GetReplicas("Dev", revision))
                && profiles.SequenceEqual(manager.GetProfiles("Dev")));

            var config = JsonNode.Parse(originalConfig)!.AsObject();
            var service = config["Peer"]!["Services"]!["TestWorker"]!;
            service["Pid"] = replacementPid;
            File.WriteAllText(configFile, config.ToJsonString());
            var document = XDocument.Parse(originalProfile);
            var app = document.Root!.Elements("App").Single();
            app.SetAttributeValue("Pid", replacementPid);
            document.Save(profileFile);
            fake.Live.Remove(pid);
            fake.Live[replacementPid] = replica.AppPath;
            var replacedReplicas = manager.GetReplicas("Dev", revision);
            var replacedProfiles = manager.GetProfiles("Dev");
            Check("Replica poll detects external PID change", !replicas.SequenceEqual(replacedReplicas)
                && replacedReplicas.Single().Pid == replacementPid && replacedReplicas.Single().IsLive);
            Check("Profile poll detects external PID change", !profiles.SequenceEqual(replacedProfiles)
                && replacedProfiles.Single().Pid == replacementPid && replacedProfiles.Single().Status == "Running");

            service["RunState"] = "Paused";
            File.WriteAllText(configFile, config.ToJsonString());
            app.SetAttributeValue("RunState", "Paused");
            document.Save(profileFile);
            var pausedConfig = File.ReadAllText(configFile);
            var pausedProfile = File.ReadAllText(profileFile);
            var pausedReplicas = manager.GetReplicas("Dev", revision);
            var pausedProfiles = manager.GetProfiles("Dev");
            Check("Replica poll detects external run state change", !replacedReplicas.SequenceEqual(pausedReplicas)
                && pausedReplicas.Single().State == RunState.Paused);
            Check("Profile poll detects external status change", !replacedProfiles.SequenceEqual(pausedProfiles)
                && pausedProfiles.Single().Status == "Paused");
            Check("Polling never launches workers", fake.Starts == starts);
            Check("Polling never writes configuration or profiles", File.ReadAllText(configFile) == pausedConfig
                && File.ReadAllText(profileFile) == pausedProfile);
        }
        finally
        {
            File.WriteAllText(configFile, originalConfig);
            File.WriteAllText(profileFile, originalProfile);
            fake.Live.Remove(replacementPid);
            fake.Live[pid] = replica.AppPath;
        }
    }

    private static JsonObject Config(string revision) => JsonNode.Parse(File.ReadAllText(Path.Combine(revision, "Infrastructure", "peersettings.json")))!.AsObject();
    private static void Check(string name, bool value)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Throws<T>(string name, Action action) where T : Exception
    {
        try { action(); }
        catch (T) { Check(name, true); return; }
        throw new InvalidOperationException($"FAIL: {name}: expected {typeof(T).Name}");
    }

    private sealed class FakeProcesses : IWorkerProcesses
    {
        public int Starts { get; private set; }
        public Dictionary<int, string> Live { get; } = [];
        public Action? BeforeStart { get; set; }
        public bool IsMatch(int pid, string appPath, string appName) => Live.TryGetValue(pid, out var path) && path == appPath;
        public int? FindMatchingPid(string appPath, string appName) => Live.Where(pair => pair.Value == appPath).Select(pair => (int?)pair.Key).SingleOrDefault();
        public int Start(string appPath, string appName)
        {
            BeforeStart?.Invoke();
            var pid = 1000 + ++Starts;
            Live[pid] = appPath;
            return pid;
        }
        public IReadOnlyList<string> ForceStopWorkers() { Live.Clear(); return ["Fake workers stopped"]; }
    }
}
