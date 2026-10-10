# IgnitionDeck

**A Windows-native control centre for managing and monitoring multiple application worker instances on a single machine.**

IgnitionDeck helps startups and small teams operate growing background workloads from one place. Organise workers into development and production environments, create revisions, run multiple replicas, inspect logs, and restore intended workloads after a reboot—without introducing a full-scale orchestration platform.

> **Scope:** IgnitionDeck is designed for **single-host worker management**. It provides explicit controls and refreshable status views; it does not currently offer automatic horizontal scaling, distributed multi-host orchestration, or continuous health monitoring.

## Features

- **Execution profiles:** See services in **Dev** and **Prod**, including revisions, process IDs, running state, missing instances, and error indicators.
- **Builds and revisions:** Import builds, create environment-specific revisions, inspect usage, and protect revisions still in use from deletion.
- **Replicas and lifecycle controls:** Operate multiple worker instances through **Running**, **Paused**, and **Shutdown** states.
- **Operational visibility:** Review status dashboards and detail tables, open logs, inspect errors, and navigate to worker folders.
- **Automatic reboot recovery:** Optionally register a Windows startup task that restores previously **Running** workers without an interactive sign-in. Paused and Shutdown workers stay stopped.
- **One management interface:** Bring worker operations, environments, builds, revisions, replicas, and recovery settings together in a WinUI 3 desktop application.

## Typical workflow

1. **Import a build** containing one or more worker applications.
2. **Create a revision** for the Dev or Prod environment.
3. **Configure and launch replicas** for the required workload.
4. **Review execution profiles** for status, process details, and errors; open logs when investigating problems.
5. **Enable reboot recovery** if workers should resume their persisted Running state when Windows starts.

Management actions are explicit. Execution Profiles and Replicas poll service status while active at a configurable interval (default: 5 seconds), pausing during operations, refreshes, and modal dialogs. Set the interval in **Settings**; machine-wide app settings are stored separately in `%PROGRAMDATA%\IgnitionDeck\settings.json`, not in worker `peersettings.json`. Execution Profiles has no Refresh button; other pages retain it. UI status also refreshes when navigating, changing filters, or performing actions; it is **not streamed in real time**. Reboot recovery is a bounded startup operation, not a continuously running supervisor or an application-readiness check.

## Requirements

- **OS:** Windows 10 (build 19041 or later) or Windows 11, x64
- **UI:** WinUI 3 with Windows App SDK
- **Runtime:** .NET 10
- **Deployment:** Unpackaged, self-contained Windows desktop application
- **Permissions:** The application manifest requires Administrator privileges. Workers launched by the manager inherit its elevated context.

To build the desktop application, install Visual Studio with Windows/WinUI development tools and the .NET 10 SDK. Use **Visual Studio MSBuild**, not SDK-only `dotnet build`, for the WinUI project because its resource-generation targets require Visual Studio tooling.

## Build and run

From the repository root, open **Visual Studio Developer PowerShell** and run:

```powershell
MSBuild .\IgnitionDeck\IgnitionDeck.csproj /restore /p:Platform=x64
```

Alternatively, open `IgnitionDeck/IgnitionDeck.csproj` in Visual Studio, select **x64**, and run the app as Administrator.

LaunchPad root and polling are stored in `%PROGRAMDATA%\IgnitionDeck\settings.json`, separately from worker configuration. Set the working data directory in **Settings**. For a sandboxed first launch, create `settings.json` containing:

```json
{
  "LaunchPadRoot": "C:\\Sandbox\\LaunchPad",
  "PollingIntervalSeconds": 5
}
```

Then start the built executable from its output directory with an explicit settings path:

```powershell
.\IgnitionDeck.exe --settings C:\Sandbox\settings.json --no-restore
```

`--no-restore` is retained for backward compatibility; desktop startup does not automatically launch workers. The default LaunchPad root without a custom setting is `C:\LaunchPad`. Use a sandbox rather than production data when evaluating the application.

## Publish and deploy

From Visual Studio Developer PowerShell:

```powershell
MSBuild .\IgnitionDeck\IgnitionDeck.csproj /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:SelfContained=true /p:PublishDir=C:\IgnitionDeckDeploy\
```

Deploy the **entire published folder**, including `IgnitionDeck.pri`; copying `IgnitionDeck.exe` alone is insufficient. Place it in a stable local directory where the SYSTEM account can access the executable, settings, and worker data if reboot recovery is enabled.

For reboot recovery, launch the installed application as Administrator and select **Enable automatic recovery** in Settings. This registers the `IgnitionDeck.Recovery` Windows scheduled task under SYSTEM. Test the reboot path and permissions before relying on it in production. Recovery status and logs are written to `<LaunchPadRoot>\logs\recovery`.

## Repository structure

| Path | Purpose |
| --- | --- |
| [`IgnitionDeck/`](IgnitionDeck/) | WinUI 3 desktop application and detailed technical documentation |
| [`IgnitionDeck.Core/`](IgnitionDeck.Core/) | Worker management, process operations, and reboot recovery logic |
| [`IgnitionDeck.Checks/`](IgnitionDeck.Checks/) | Isolated validation and regression checks |

Run the isolated checks with:

```powershell
dotnet run --project .\IgnitionDeck.Checks\IgnitionDeck.Checks.csproj
```

## More information

- [Detailed setup, deployment, recovery, and operational behaviour](IgnitionDeck/README.md)
- [Migration notes and acceptance checklist](IgnitionDeck/MIGRATION.md)
- [License file](LICENSE.txt)

**Before production use:** Validate clean-machine deployment, real worker lifecycle operations, and reboot recovery under SYSTEM in your own environment.
