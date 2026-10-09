using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IgnitionDeck.Core;

/// <summary>UI-independent Peer operations. Call serially; the desktop coordinator serializes callers.</summary>
public sealed class PeerManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly IWorkerProcesses _processes;
    private readonly Dictionary<string, ReplicaEntry> _observed = new(StringComparer.OrdinalIgnoreCase);
    public string LaunchPadRoot { get; private set; }
    public string BootPath { get; private set; }
    private string BuildsRoot => Path.Combine(LaunchPadRoot, "Builds");
    private string RevisionsRoot => Path.Combine(LaunchPadRoot, "Revisions");
    private string ProfilePath => Path.Combine(LaunchPadRoot, "ExecutionProfile.xml");
    private string RepoPath => Path.Combine(LaunchPadRoot, "AppRepo.xml");

    public PeerManager(string settingsPath, IWorkerProcesses? processes = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        _processes = processes ?? new WorkerProcesses();
        var peer = ReadSettings()["Peer"];
        LaunchPadRoot = Path.GetFullPath(peer?["LaunchPadRoot"]?.GetValue<string>() ?? @"C:\LaunchPad");
        BootPath = peer?["BootPath"]?.GetValue<string>() ?? string.Empty;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(BuildsRoot);
        Directory.CreateDirectory(RevisionsRoot);
        if (!File.Exists(ProfilePath)) SaveXml(ProfilePath, new XDocument(new XElement("ExecutionProfile")));
        if (!File.Exists(RepoPath)) SaveXml(RepoPath, new XDocument(new XElement("AppRepo")));
    }

    public void SaveLaunchPadRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("LaunchPad folder is required.");
        var normalized = Path.GetFullPath(path);
        Directory.CreateDirectory(normalized);
        // Do not lose pending shutdown observation when switching roots.
        if (_observed.Values.Any(entry => entry.Pid is int pid && _processes.IsMatch(pid, entry.AppPath, entry.Name)))
            throw new InvalidOperationException("Stop managed workers before switching LaunchPad roots.");
        if (GetRevisions("Dev").Concat(GetRevisions("Prod")).Any(revision => revision.ActiveApps > 0))
            throw new InvalidOperationException("Stop live and draining workers before switching LaunchPad roots.");
        SaveSetting("LaunchPadRoot", normalized);
        LaunchPadRoot = normalized;
        _observed.Clear();
        Initialize();
    }

    public void SaveBootPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        StartupRegistration.Register(normalized);
        SaveSetting("BootPath", normalized);
        BootPath = normalized;
    }

    private JsonObject ReadSettings() => File.Exists(_settingsPath) ? ReadJson(_settingsPath) : new JsonObject();
    public void EnsureRecoverySettings() => SaveSetting("LaunchPadRoot", LaunchPadRoot);
    private void SaveSetting(string key, string value)
    {
        var root = ReadSettings();
        if (root["Peer"] is not JsonObject) root["Peer"] = new JsonObject();
        root["Peer"]![key] = value;
        SaveJson(_settingsPath, root);
    }

    public IReadOnlyList<BuildEntry> GetBuilds() => Directory.GetDirectories(BuildsRoot)
        .Select(path => new DirectoryInfo(path))
        .Where(dir => dir.Name.StartsWith("Peer Build", StringComparison.OrdinalIgnoreCase) && dir.LastWriteTime >= DateTime.Now.AddMonths(-3))
        .OrderByDescending(dir => dir.LastWriteTime).Take(20)
        .Select(dir => new BuildEntry(dir.Name, dir.FullName, dir.LastWriteTime)).ToList();

    public string ImportBuild(string source, bool replace)
    {
        source = Path.GetFullPath(source);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        ValidateSegment(name);
        if (!name.StartsWith("Peer Build", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Build folder name must start with 'Peer Build'.");
        var target = Child(BuildsRoot, name);
        if (IsWithin(source, LaunchPadRoot) || IsWithin(LaunchPadRoot, source))
            throw new InvalidOperationException("Import from outside LaunchPad to avoid copying or deleting managed files.");
        if (Directory.Exists(target) && !replace) throw new IOException("Build already exists. Confirm replacement first.");
        // Stage the copy before replacing a build so failed copies do not delete the original.
        var staging = Path.Combine(BuildsRoot, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, staging);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        UpdateBuildIndexes(name, target);
        return target;
    }

    public void DeleteBuild(string name) => Directory.Delete(Child(BuildsRoot, name), true);
    public IReadOnlyList<string> GetBuildApps(string buildName) => AppDirectories(Child(BuildsRoot, buildName)).Select(Path.GetFileName).OfType<string>().ToList();

    public string CreateRevision(string buildName, IReadOnlyList<string> apps, string environment)
    {
        if (apps.Count == 0) throw new ArgumentException("Select at least one app.");
        var env = NormalizeEnvironment(environment);
        var source = Child(BuildsRoot, buildName);
        var envRoot = Child(RevisionsRoot, env);
        Directory.CreateDirectory(envRoot);
        var match = Regex.Match(buildName, @"Peer\s+Build\s+(?<id>\d+)", RegexOptions.IgnoreCase);
        var buildId = match.Success ? match.Groups["id"].Value : "1";
        var nextId = Directory.GetDirectories(envRoot).Select(path => Regex.Match(Path.GetFileName(path), @"\[(?<id>\d+)\]"))
            .Where(result => result.Success).Select(result => int.Parse(result.Groups["id"].Value)).DefaultIfEmpty(0).Max() + 1;
        var target = Child(envRoot, $"Revision {env.ToLowerInvariant()}-{buildId}-[{nextId}]");
        try
        {
            Directory.CreateDirectory(target);
            foreach (var app in apps.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (app.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase)) continue;
                CopyDirectory(Child(source, app), Child(target, app));
            }
            var infrastructure = Child(source, "Infrastructure");
            if (!Directory.Exists(infrastructure)) throw new DirectoryNotFoundException("Build is missing Infrastructure.");
            var targetInfrastructure = Child(target, "Infrastructure");
            Directory.CreateDirectory(targetInfrastructure);
            foreach (var file in new[] { "peersettings.json", env == "Prod" ? "peersettings.Production.json" : "peersettings.Development.json" })
            {
                var config = Child(infrastructure, file);
                if (File.Exists(config)) File.Copy(config, Child(targetInfrastructure, file));
            }
            // New revisions must not inherit PIDs or run states from the source build.
            var configPath = ConfigPath(target);
            var root = ReadJson(configPath);
            if (root["Peer"]?["Services"] is not JsonObject services) throw new InvalidDataException("Missing Peer:Services.");
            foreach (var service in services.Select(pair => pair.Value).OfType<JsonObject>())
            {
                service["RunState"] = "Shutdown";
                service.Remove("Pid");
            }
            SaveJson(configPath, root);
            return target;
        }
        catch { if (Directory.Exists(target)) Directory.Delete(target, true); throw; }
    }

    public IReadOnlyList<RevisionEntry> GetRevisions(string environment)
    {
        var env = NormalizeEnvironment(environment);
        var root = Child(RevisionsRoot, env);
        if (!Directory.Exists(root)) return [];
        // Parse before reporting unused: corrupt profiles must block destructive operations.
        var profiles = ReadProfiles();
        return Directory.GetDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new RevisionEntry(env, Path.GetFileName(path), CountLiveApps(env, Path.GetFileName(path), profiles))).ToList();
    }

    private int CountLiveApps(string env, string revision, IReadOnlyList<ProfileEntry> profiles)
    {
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in GetReplicas(env, revision))
            if (entry.IsLive) livePaths.Add(entry.AppPath);
        foreach (var profile in profiles.Where(p => NormalizeEnvironment(p.Environment) == env && p.Revision.Equals(revision, StringComparison.OrdinalIgnoreCase)))
        {
            var path = ProfileAppPath(profile);
            if (profile.Pid is int pid && _processes.IsMatch(pid, path, profile.Name)) livePaths.Add(path);
        }
        return livePaths.Count;
    }

    public void DeleteRevision(string environment, string name)
    {
        var env = NormalizeEnvironment(environment);
        if (CountLiveApps(env, name, ReadProfiles()) > 0) throw new InvalidOperationException("Cannot delete a live or draining revision.");
        Directory.Delete(RevisionPath(env, name), true);
        var document = LoadXml(ProfilePath, "ExecutionProfile");
        document.Root!.Elements("App").Where(app => NormalizeEnvironment(RequiredAttribute(app, "Environment")) == env
            && RequiredAttribute(app, "RevisionFolder").Equals(name, StringComparison.OrdinalIgnoreCase)).Remove();
        SaveXml(ProfilePath, document);
    }

    public int FlushUnused(string environment)
    {
        var unused = GetRevisions(environment).Where(revision => revision.ActiveApps == 0).ToList();
        foreach (var revision in unused) DeleteRevision(revision.Environment, revision.Name);
        return unused.Count;
    }

    public IReadOnlyList<ReplicaEntry> GetReplicas(string environment, string revision)
    {
        var path = RevisionPath(environment, revision);
        if (!Directory.Exists(path)) return [];
        var apps = AppDirectories(path).ToList();
        if (apps.Count == 0) return [];
        var config = ReadJson(ConfigPath(path));
        return apps.Select(appPath =>
        {
            var name = Path.GetFileName(appPath);
            var service = ServiceSection(config, name);
            var state = ParseState(service["RunState"]?.GetValue<string>() ?? "Shutdown");
            var pid = ReadPid(service["Pid"]);
            var live = pid is int id && _processes.IsMatch(id, appPath, name);
            return new ReplicaEntry(name, appPath, path, state, pid, live);
        }).ToList();
    }

    public void SetState(ReplicaEntry entry, RunState state)
    {
        EnsureReplicaPath(entry);
        var configPath = ConfigPath(entry.RevisionPath);
        var root = ReadJson(configPath);
        var service = ServiceSection(root, entry.Name); // Validate before launching anything.
        var pid = ReadPid(service["Pid"]);
        var live = pid is int id && OperationDiagnostics.Execute($"Check worker '{entry.Name}', PID {id}, app path '{entry.AppPath}'.",
            () => _processes.IsMatch(id, entry.AppPath, entry.Name));
        if (!live)
        {
            pid = OperationDiagnostics.Execute($"Find existing worker '{entry.Name}' by executable path '{entry.AppPath}'.",
                () => _processes.FindMatchingPid(entry.AppPath, entry.Name));
            live = pid is not null;
        }
        var previousConfig = root.ToJsonString(JsonOptions);
        // Persist the desired state before start (Paused restore must not briefly run work).
        service["RunState"] = state.ToString();
        if (!live) service.Remove("Pid");
        SaveJson(configPath, root);
        try
        {
            if (state != RunState.Shutdown && !live)
                pid = OperationDiagnostics.Execute($"Start worker '{entry.Name}' from '{entry.AppPath}'.", () => _processes.Start(entry.AppPath, entry.Name));
            else if (!live) pid = null;
            // Keep a draining PID in JSON until exit so it still locks revision deletion.
            if (pid is int activePid) service["Pid"] = activePid;
            else service.Remove("Pid");
            SaveJson(configPath, root);
            WriteProfile(entry, state, pid);
            if (pid is not null) _observed[entry.AppPath] = entry with { State = state, Pid = pid, IsLive = true };
            else _observed.Remove(entry.AppPath);
        }
        catch (Exception original)
        {
            // If a process started, keep its desired state/PID to avoid an untracked live worker.
            try
            {
                if (pid is null || !(_processes.IsMatch(pid.Value, entry.AppPath, entry.Name)))
                    AtomicWrite(configPath, previousConfig);
            }
            catch (Exception rollback)
            {
                // A rollback/inspection failure must not hide the original launch/update failure.
                original.Data["IgnitionDeck.RollbackFailure"] = rollback;
            }
            throw;
        }
    }

    public void SetRevisionState(string environment, string revision, RunState state)
    {
        var failures = new List<string>();
        var exceptions = new List<Exception>();
        foreach (var entry in GetReplicas(environment, revision))
        {
            if (state == RunState.Running && entry.State == RunState.Paused) continue;
            try { SetState(entry, state); }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {OperationDiagnostics.Describe(ex)}");
                exceptions.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, failures), new AggregateException(exceptions));
    }

    public IReadOnlyList<string> Restore(IProgress<RestoreProgress>? progress = null)
    {
        var entries = ReadProfiles();
        var failures = new List<string>();
        var completed = 0;
        foreach (var profile in entries)
        {
            try
            {
                var path = ProfileAppPath(profile);
                var entry = new ReplicaEntry(profile.Name, path, RevisionPath(profile.Environment, profile.Revision), profile.State, profile.Pid, false);
                // Prefer a matching profile PID if Boot updated XML but config is stale.
                if (profile.Pid is int pid && OperationDiagnostics.Execute($"Inspect restore worker '{profile.Name}', PID {pid}, app path '{path}'.",
                    () => _processes.IsMatch(pid, path, profile.Name)))
                {
                    var root = ReadJson(ConfigPath(entry.RevisionPath));
                    ServiceSection(root, entry.Name)["Pid"] = pid;
                    SaveJson(ConfigPath(entry.RevisionPath), root);
                }
                SetState(entry, profile.State);
            }
            catch (Exception ex) { failures.Add($"{profile.Environment}/{profile.Revision}/{profile.Name}: {OperationDiagnostics.Describe(ex)}"); }
            progress?.Report(new RestoreProgress(++completed, entries.Count, $"Restoring {profile.Name} ({completed}/{entries.Count})"));
        }
        return failures;
    }

    public async Task<RecoveryPass> RecoverRunningAsync(TimeSpan observationPeriod, Func<TimeSpan, Task>? delay = null)
    {
        var entries = ReadProfiles().Where(entry => entry.State == RunState.Running).ToList();
        var failures = new List<string>();
        var candidates = new List<ReplicaEntry>();
        foreach (var profile in entries)
        {
            try
            {
                var path = ProfileAppPath(profile);
                var entry = new ReplicaEntry(profile.Name, path, RevisionPath(profile.Environment, profile.Revision), RunState.Running, profile.Pid, false);
                SetState(entry, RunState.Running);
                candidates.Add(entry);
            }
            catch (Exception ex) { failures.Add($"{profile.Environment}/{profile.Revision}/{profile.Name}: {OperationDiagnostics.Describe(ex)}"); }
        }
        if (candidates.Count > 0 && observationPeriod > TimeSpan.Zero)
            await (delay ?? (duration => Task.Delay(duration)))(observationPeriod).ConfigureAwait(false);
        var verified = 0;
        foreach (var entry in candidates)
        {
            try
            {
                var service = ServiceSection(ReadJson(ConfigPath(entry.RevisionPath)), entry.Name);
                var pid = ReadPid(service["Pid"]);
                if (pid is int id && _processes.IsMatch(id, entry.AppPath, entry.Name)) verified++;
                else failures.Add($"{entry.AppPath}: Worker exited or could not be verified during startup observation. Running intent is retained for retry.");
            }
            catch (Exception ex) { failures.Add($"{entry.AppPath}: {OperationDiagnostics.Describe(ex)}"); }
        }
        return new RecoveryPass(entries.Count, verified, failures);
    }

    public void ReconcileExitedProcesses()
    {
        foreach (var entry in _observed.Values.ToList())
        {
            if (entry.Pid is int pid && _processes.IsMatch(pid, entry.AppPath, entry.Name)) continue;
            SetState(entry, RunState.Shutdown);
        }
    }

    public IReadOnlyList<string> ForceStopWorkers()
    {
        var result = _processes.ForceStopWorkers();
        // Preserve execution intent, as legacy Force Shutdown does, for explicit restore.
        _observed.Clear();
        return result;
    }

    public IReadOnlyList<ProfileStatus> GetProfiles(string environment)
    {
        var env = NormalizeEnvironment(environment);
        var profiles = ReadProfiles().Where(entry => NormalizeEnvironment(entry.Environment) == env).ToList();
        var rows = new List<ProfileStatus>();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in profiles)
        {
            var path = ProfileAppPath(entry);
            var live = entry.Pid is int pid && _processes.IsMatch(pid, path, entry.Name);
            var display = string.IsNullOrWhiteSpace(entry.ServiceMap) ? entry.Name : entry.ServiceMap;
            if (live) active.Add(display);
            var error = Path.Combine(path, "logs", "errors", "error.txt");
            rows.Add(new ProfileStatus(entry, display, entry.Revision, entry.Pid, live ? entry.State.ToString() : "Terminated", path, error,
                File.Exists(error) && new FileInfo(error).Length > 0));
        }
        var repo = LoadXml(RepoPath, "AppRepo");
        foreach (var service in repo.Root!.Element("LatestBuild")?.Elements("Service") ?? [])
        {
            var name = RequiredAttribute(service, "Name");
            if (!active.Contains(name)) rows.Add(new ProfileStatus(null, name, "Missing", null, "Missing", "", "", false));
        }
        return rows;
    }

    public IReadOnlyList<string> GetLogs(string appPath)
    {
        EnsureManagedAppPath(appPath);
        var logs = Path.Combine(appPath, "logs");
        if (!Directory.Exists(logs) && Directory.Exists(appPath))
            logs = Directory.GetDirectories(appPath, "logs", SearchOption.AllDirectories).OrderBy(path => path.Length).FirstOrDefault() ?? logs;
        if (!Directory.Exists(logs)) return [];
        var errors = Path.Combine(logs, "errors");
        return Directory.GetFiles(logs, "*", SearchOption.AllDirectories).Where(path => !IsWithin(path, errors))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string ArchiveError(string path)
    {
        if (!IsWithin(path, RevisionsRoot)) throw new ArgumentException("Error file is outside LaunchPad revisions.");
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(directory, stem + Path.GetExtension(path));
        var counter = 2;
        while (File.Exists(target)) target = Path.Combine(directory, $"{stem}-{counter++}{Path.GetExtension(path)}");
        File.Move(path, target);
        return target;
    }

    private IReadOnlyList<ProfileEntry> ReadProfiles() => LoadXml(ProfilePath, "ExecutionProfile").Root!.Elements("App")
        .Select(app => new ProfileEntry(RequiredAttribute(app, "Name"), (string?)app.Attribute("ServiceMap") ?? "",
            NormalizeEnvironment(RequiredAttribute(app, "Environment")), RequiredAttribute(app, "RevisionFolder"),
            ParseState(RequiredAttribute(app, "RunState")), int.TryParse((string?)app.Attribute("Pid"), out var pid) ? pid : null))
        .Where(entry => entry.State != RunState.Shutdown).OrderBy(entry => entry.Environment).ThenBy(entry => entry.Revision).ThenBy(entry => entry.Name).ToList();

    private void WriteProfile(ReplicaEntry entry, RunState state, int? pid)
    {
        var document = LoadXml(ProfilePath, "ExecutionProfile");
        var revision = new DirectoryInfo(entry.RevisionPath);
        var environment = NormalizeEnvironment(revision.Parent!.Name);
        var existing = document.Root!.Elements("App").Where(app => RequiredAttribute(app, "Name").Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
            && NormalizeEnvironment(RequiredAttribute(app, "Environment")) == environment
            && RequiredAttribute(app, "RevisionFolder").Equals(revision.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        existing.Remove();
        if (state != RunState.Shutdown)
        {
            var app = new XElement("App", new XAttribute("Name", entry.Name), new XAttribute("ServiceMap", entry.Name.Split('.').Last()),
                new XAttribute("Environment", environment), new XAttribute("RevisionFolder", revision.Name), new XAttribute("RunState", state));
            if (pid is int id) app.SetAttributeValue("Pid", id);
            document.Root.Add(app);
        }
        SaveXml(ProfilePath, document);
    }

    private void UpdateBuildIndexes(string name, string path)
    {
        var keyMatch = Regex.Match(name, @"\[(?<key>[^\]]+)\]");
        var key = keyMatch.Success ? keyMatch.Groups["key"].Value : name;
        var indexPath = Path.Combine(BuildsRoot, "AppFolders.xml");
        var index = File.Exists(indexPath) ? LoadXml(indexPath, "apps") : new XDocument(new XElement("apps"));
        var existing = index.Root!.Elements("name").Select(element => (string?)element.Attribute("value")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var app in AppDirectories(path).Select(Path.GetFileName))
            if (app is not null && existing.Add(app)) index.Root.Add(new XElement("name", new XAttribute("value", app), new XAttribute("buildDateKey", key)));
        SaveXml(indexPath, index);
        var config = Path.Combine(path, "Infrastructure", "peersettings.json");
        if (!File.Exists(config)) config = Path.Combine(path, "Infrastructure", "appsettings.json");
        var latest = new XElement("LatestBuild", new XAttribute("Name", name), new XAttribute("ImportedAtUtc", DateTime.UtcNow.ToString("O")));
        if (File.Exists(config) && ReadJson(config)["Peer"]?["Services"] is JsonObject services)
            foreach (var service in services.Where(pair => pair.Value is JsonObject).OrderBy(pair => pair.Key))
                latest.Add(new XElement("Service", new XAttribute("Name", service.Key)));
        SaveXml(RepoPath, new XDocument(new XElement("AppRepo", latest)));
    }

    private static JsonObject ServiceSection(JsonObject root, string name)
    {
        var suffix = name.Split('.').Last();
        var mapped = root["Peer"]?["ServiceMap"]?[name]?.GetValue<string>();
        if (mapped is not null && mapped != suffix) throw new InvalidDataException($"ServiceMap for {name} must equal '{suffix}'.");
        return root["Peer"]?["Services"]?[suffix] as JsonObject ?? throw new InvalidDataException($"No Peer:Services:{suffix} for {name}.");
    }

    private string RevisionPath(string env, string name) => Child(Child(RevisionsRoot, NormalizeEnvironment(env)), name);
    private string ProfileAppPath(ProfileEntry entry) => Child(RevisionPath(entry.Environment, entry.Revision), entry.Name);
    private void EnsureManagedAppPath(string path)
    {
        if (!IsWithin(path, RevisionsRoot)) throw new ArgumentException("App path is outside LaunchPad revisions.");
    }
    private void EnsureReplicaPath(ReplicaEntry entry)
    {
        EnsureManagedAppPath(entry.AppPath);
        if (!Path.GetFullPath(Child(entry.RevisionPath, entry.Name)).Equals(Path.GetFullPath(entry.AppPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid replica path.");
    }
    public static string NormalizeEnvironment(string env) => env.ToLowerInvariant() switch
    {
        "dev" or "development" => "Dev", "prod" or "production" => "Prod", _ => throw new ArgumentException("Unknown environment.")
    };
    private static RunState ParseState(string state) => Enum.TryParse<RunState>(state, true, out var result) && Enum.IsDefined(result)
        ? result : throw new InvalidDataException($"Invalid RunState '{state}'.");
    private static int? ReadPid(JsonNode? node) => node is not null && int.TryParse(node.ToString(), out var value) ? value : null;
    private static JsonObject ReadJson(string path) => OperationDiagnostics.Execute($"Read JSON file '{path}'.",
        () => JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"Invalid JSON object: {path}"));
    private static XDocument LoadXml(string path, string root)
    {
        var document = XDocument.Load(path);
        if (document.Root?.Name != root) throw new InvalidDataException($"Expected {root} root in {path}.");
        return document;
    }
    private static string RequiredAttribute(XElement element, string attribute)
    {
        var value = ((string?)element.Attribute(attribute))?.Trim();
        if (string.IsNullOrEmpty(value)) throw new InvalidDataException($"Missing {attribute} attribute.");
        ValidateSegment(value);
        return value;
    }
    private static IEnumerable<string> AppDirectories(string root) => Directory.GetDirectories(root).Where(path => !Path.GetFileName(path).Equals("Infrastructure", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
    private static string ConfigPath(string revision)
    {
        foreach (var name in new[] { "peersettings.json", "appsettings.json" })
        {
            var path = Path.Combine(revision, "Infrastructure", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"No run-state config in {revision}\\Infrastructure.");
    }
    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid folder/file name.");
    }
    private static string Child(string parent, string name)
    {
        ValidateSegment(name);
        var path = Path.Combine(parent, name);
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Managed paths cannot be symbolic links or junctions.");
        return path;
    }
    private static bool IsWithin(string path, string parent)
    {
        var normalized = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return normalized.Equals(root, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Builds must not contain symbolic links or junctions.");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Builds must not contain symbolic links.");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
    private static void SaveJson(string path, JsonObject root) => AtomicWrite(path, root.ToJsonString(JsonOptions));
    private static void SaveXml(string path, XDocument document) => AtomicWrite(path, document.ToString());
    private static void AtomicWrite(string path, string content)
    {
        OperationDiagnostics.Execute($"Create configuration directory '{Path.GetDirectoryName(path)}'.",
            () => Directory.CreateDirectory(Path.GetDirectoryName(path)!));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? failure = null;
        try
        {
            OperationDiagnostics.Execute($"Write staged file '{temporary}' for destination '{path}'.", () => File.WriteAllText(temporary, content));
            OperationDiagnostics.Execute($"Atomically replace destination '{path}' from '{temporary}'. A reader without delete-sharing or a non-replaceable file can block replacement.", () =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(temporary, path, overwrite: true); return; }
                    catch (Exception ex) when (attempt < 6 && IsRetryableReplacementFailure(ex))
                    {
                        // Configuration reload readers may briefly deny delete-sharing. Never truncate
                        // the destination or change permissions to work around a persistent denial.
                        Thread.Sleep(Math.Min(40 << attempt, 240));
                    }
                }
            });
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup)
            {
                if (failure is null)
                {
                    OperationDiagnostics.Annotate(cleanup, $"Clean up staged configuration file '{temporary}'.");
                    throw;
                }
                failure.Data["IgnitionDeck.CleanupFailure"] = cleanup;
            }
        }
    }

    private static bool IsRetryableReplacementFailure(Exception exception)
    {
        var windowsError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException
            && windowsError is 5 or 32 or 33;
    }
}
