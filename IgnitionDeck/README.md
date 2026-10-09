# IgnitionDeck

Native WinUI 3 replacement for the VotePool.Peer WinForms manager. Uses left navigation for Execution Profiles, Builds, Revisions, Replicas, and Settings. Independently deployed workers remain unchanged. The retired WinForms manager and Boot projects have been removed from this repository.

## List layout

Execution Profiles opens with **Overview** and **Details** sub-tabs. Overview shows Prod and Dev pie charts: healthy running services in green, running services with errors in red, and missing services in gray. Error status uses the same nonempty active error log indicator as Details, not an application health probe. Counts use distinct service names; live paused workers count as running/present, while terminated or expected-but-absent services count as missing. Errors on a live instance take precedence over healthy instances of the same service; stale-instance errors do not mark a live healthy instance as errored. Empty environments show a neutral chart. Details retains the original table, environment filter, and log/folder actions. These are explicit-refresh snapshots, not continuous monitoring.

Profiles, Builds, Revisions, and Replicas use compact native tables rather than stacked cards, matching the preferred at-a-glance layout of VotePool.Peer. Tables have aligned columns, frozen headers, alternating row shading, inline actions, error-row highlighting, and horizontal/vertical scrolling. Long values are truncated with full-value tooltips. Settings remains a form. The tables use built-in WinUI controls without an external DataGrid package; sorting, column resizing, and spreadsheet-style editing are not provided.

## Build and run

Open `VotePool.sln` in Visual Studio 2026 with Windows application development/WinUI tooling installed. Select **x64**, build **IgnitionDeck**, and choose it as the startup project when desired. The project uses .NET 10 and pins Windows App SDK 1.7.250909003 (the installed/cached version used for this migration, not a claim about the latest release).

Use Visual Studio MSBuild, not SDK-only `dotnet build`, for the WinUI app because PRI generation requires Visual Studio Appx tasks:

```powershell
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild Peer\IgnitionDeck\IgnitionDeck.csproj /restore /p:Platform=x64 /v:minimal
```

The management library can be built with `dotnet build Peer\IgnitionDeck.Core\IgnitionDeck.Core.csproj`.

IgnitionDeck's application manifest requires Administrator privileges. Windows requests UAC approval (or Administrator credentials) when launched from a non-elevated desktop. Run Visual Studio as Administrator when debugging. Workers launched by the elevated manager inherit its elevated security context. The SYSTEM recovery task is already elevated and does not need an interactive UAC prompt.

## Deployment

This is an unpackaged x64 desktop app with Windows App SDK self-contained deployment. Publish the entire output folder, not just IgnitionDeck.exe:

```powershell
& $msbuild Peer\IgnitionDeck\IgnitionDeck.csproj /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:SelfContained=true /p:PublishDir=C:\IgnitionDeckDeploy\
```

Do not use single-file publishing or trimming for this WinUI app. Verify deployment on a clean Windows 10 19041+ / Windows 11 machine before replacing the legacy manager. ClickOnce settings from WinForms are not carried over; distribution/signing/MSIX packaging is a separate deployment choice.

The published folder must contain `IgnitionDeck.pri` alongside `IgnitionDeck.exe`; it contains the application's compiled WinUI resources. The project explicitly includes it during folder publishing. Missing this file can crash desktop startup in `Microsoft.UI.Xaml.dll`, even when headless recovery works. Validate a published desktop launch with sandbox settings as well as headless mode.

Place the existing manager's `peersettings.json` beside IgnitionDeck.exe to retain LaunchPadRoot. The desktop default is `C:\LaunchPad`; no production settings or secrets are included automatically. Install the entire published folder in a stable local machine-accessible directory, not a debug output folder, network share, or user-only/OneDrive location. SYSTEM must be able to read the settings and worker executables and write LaunchPad data. The UI runs elevated but does not automatically change ACLs.

## Automatic reboot recovery

In **Settings**, choose **Enable automatic recovery**. Since the app is already elevated, task setup does not require another UAC prompt. The app creates `IgnitionDeck.Recovery` itself; users do not need to create a task manually. The task runs as SYSTEM at Windows startup, with a 30-second delay, without Windows logon. Enabling disables the existing `VotePool.Peer.Boot` scheduled task if present; the old task is retained for rollback.

The same executable runs `--restore-running --settings "absolute-path-to-peersettings.json"` without initializing WinUI. It restores only explicitly persisted Running entries, leaves Paused/Shutdown workers stopped, adopts matching existing processes, retries failures, and checks short process survival. It is not continuous supervision or an application-level health check. Task Scheduler also retries failed invocations. A missing settings file is rejected rather than falling back to the default root.

Settings shows task registration and the last recovery summary. **Enable automatic recovery** is available when recovery is not configured, disabled, or needs repair. It creates or updates the task, including repairing registration and updating paths after relocation, and is disabled when recovery is already enabled or its state cannot be verified. **Disable automatic recovery** stops future automatic recovery but does not stop workers or re-enable legacy Boot. Logs and `last-recovery.json` are under `<LaunchPadRoot>\logs\recovery`. Exit codes: `0` success (including an empty profile), `1` recovery failures, `2` command/setup failure. Early command failures are logged best-effort to `%ProgramData%\IgnitionDeck\Recovery\command-errors.log`.

Before production cutover, enable from the deployed folder and test a reboot without logging on. Verify Running workers, Paused exclusion, settings permissions, task results, and recovery logs. Development validation does not register or replace production tasks.

## Safe test launch

```powershell
.\IgnitionDeck.exe --settings C:\Sandbox\peersettings.json --no-restore
```

The settings file can contain `{ "Peer": { "LaunchPadRoot": "C:\\Sandbox\\LaunchPad" } }`. Desktop startup never launches workers; `--no-restore` is accepted for backward compatibility. Explicit Restore remains available and restores Running and Paused intent, preserving pause before launch. The app shares the legacy single-instance mutex, so do not run both managers together.

## Operational differences

- There is no periodic UI refresh. Data refreshes on tab/filter selection, after management actions, or through Refresh. Unexpected worker exits and external changes become visible on the next refresh. Refresh is read-only and never automatically restarts a stale worker; use Restore or Running explicitly.
- JSON retains a Shutdown PID until the process exits so draining revisions remain locked. ExecutionProfile removes shutdown entries, preserving Boot compatibility.
- Service keys/mappings must match each worker's last project-name segment. Invalid mappings fail visibly; no fuzzy matching or silently rewritten ServiceMap values.
- Worker executable must match its app folder name. Arbitrary fallback EXEs are not launched.
- Settings/data parse failures surface instead of treating corrupted profiles as empty before deletion.
- Live workers block root switching. Closing the UI does not terminate workers; force stop is separately confirmed and retains execution intent.
- Native dialogs and app-selection ToggleSwitch controls replace WinForms dialogs/checkboxes.

## Access-denied diagnostics

Launch/restore failures identify the failing operation, its configuration/executable path or PID, the original exception type, and HRESULT/Win32 error. Bulk Launch also retains the original exceptions under its inner AggregateException for debugger inspection. A rollback failure no longer masks the original launch error.

Windows can return access denied when atomically replacing a file that a reader opened without delete-sharing, even when the user has Full Control and the app is elevated. File replacement now makes bounded retries (about one second total) for access-denied/sharing/lock errors. It does not truncate the destination, relax permissions, or force-close other processes. Persistent failures remain visible with their exact destination path. A `Start worker executable` failure is distinct from an `Atomically replace destination` failure; use the reported operation to guide further investigation rather than assuming elevation is required.

See [MIGRATION.md](MIGRATION.md) for the acceptance inventory and interactive checks still required before cutover.
