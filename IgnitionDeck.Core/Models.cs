namespace IgnitionDeck.Core;

public enum RunState { Shutdown, Running, Paused }
public sealed record BuildEntry(string Name, string FullPath, DateTime Updated);
public sealed record RevisionEntry(string Environment, string Name, int ActiveApps)
{
    public string Usage => ActiveApps > 0 ? $"In use ({ActiveApps})" : "Unused";
}
public sealed record ReplicaEntry(string Name, string AppPath, string RevisionPath, RunState State, int? Pid, bool IsLive)
{
    public string Status => IsLive ? State == RunState.Shutdown ? "Shutting down…" : "OK" : State == RunState.Shutdown ? "Stopped" : "Terminated";
}
public sealed record ProfileEntry(string Name, string ServiceMap, string Environment, string Revision, RunState State, int? Pid);
public sealed record ProfileStatus(ProfileEntry? Entry, string Service, string Revision, int? Pid, string Status, string AppPath, string ErrorPath, bool HasErrors);
public sealed record RestoreProgress(int Completed, int Total, string Message);
public sealed record RevisionRequest(string Environment, IReadOnlyList<string> Apps);
