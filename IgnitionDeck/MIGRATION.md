# VotePool.Peer → IgnitionDeck

## Approach
Side-by-side native WinUI 3 application; Boot executable and worker projects are unchanged. No database changes or EF migrations. Management logic lives in a UI-independent library under Peer. Native dialogs live in UI/Modals and are presented by MainLayout through LayoutModalCoordinatorService.

## Acceptance inventory
- Execution Profiles: Dev/Prod filtering, service/revision/PID/liveness, missing expected services from AppRepo.xml, error indication, shared-read log browsing, error archive, Explorer folder action.
- Builds: import Peer Build folders, recent 20 builds from last three months, replace confirmation, AppFolders.xml and AppRepo.xml maintenance, delete confirmation, revision creation with selected apps and automatic Infrastructure inclusion.
- Revisions: Dev/Prod folders, existing naming convention, environment-specific Infrastructure files, usage counts, refuse deletion of live revisions, flush unused revisions, remove associated XML entries.
- Replicas: environment/revision selection, app folders, Running/Paused/Shutdown controls, PID and stale/draining status, launch-all preserves paused apps, retire-all requests graceful shutdown.
- Settings: LaunchPad root, folder picker, automatic recovery enable (also updates/repairs registration) and disable with setup-only elevation, last recovery status/logs, explicit restore progress, confirmed force-stop of VotePool.Peer.Worker processes.
- Lifecycle: shared Global\\VotePool.Peer.SingleInstance mutex, legacy Boot-in-progress guard, desktop startup does not launch workers, manager exit does not kill workers. Headless SYSTEM startup restores only Running intent without logon.

## Compatibility and safety
Keep C:\\LaunchPad desktop default and Builds, Revisions/Dev, Revisions/Prod, ExecutionProfile.xml, AppRepo.xml, Builds/AppFolders.xml contracts. Copy the existing manager settings beside the deployed executable; no secrets are copied automatically. Integrated recovery uses the explicit settings path registered by IgnitionDeck. Enabling it disables the old Boot scheduled task; retain Boot for rollback and never enable both recovery tasks simultaneously.

Worker control writes Peer:Services:<service>:RunState/Pid. Preserve existing ServiceMap values and reject mappings that do not equal the final project-name segment rather than inventing or rewriting mappings. Paused workers retain their live PID. Shutdown is cooperative, not Kill. Confirm destructive operations. Read-only refresh never starts workers or changes files; explicit restore handles stale entries. Errors must be surfaced, not silently converted to an empty profile (especially before deleting revisions).

## Validation
Build the new app and management library. Run isolated filesystem checks in a temporary LaunchPad without touching C:\\LaunchPad, launching workers, changing the registry, or creating scheduled tasks. Interactive acceptance must additionally cover native navigation/pickers/dialogs, real worker pause/drain/restore, Boot/elevation, and deployment on a clean Windows machine. Keep the legacy app until those checks pass.

### Completed validation
- Visual Studio Debug build succeeds, including the compact native table layouts.
- Release self-contained folder publish succeeds and includes IgnitionDeck.exe.
- All 78 isolated checks pass (`dotnet run --project Peer/IgnitionDeck.Checks/IgnitionDeck.Checks.csproj`). Coverage includes management/sharing/diagnostics, Running-only recovery, stale-PID adoption, retries, early-exit intent retention, fail-closed profile handling, recovery logs/status, write coordination, and SYSTEM startup definition. Windows Task Scheduler accepts the XML through its in-memory parser. Processes are faked; no real workers, registry changes, task registrations, or production LaunchPad operations are performed.
- Actual headless executable smoke test exits successfully without a window and writes an empty-profile recovery report. Desktop sandbox opens without automatic restore/Boot-path dialogs; UI Automation finds all four recovery Settings actions.
- Sandboxed Windows UI Automation checks pass for all four table screens and Settings, aligned profile header/data columns, vertically scrollable rows with frozen headers, and inline replica Running/Paused/Shutdown controls. Test launches use separate settings and `--no-restore`.
- The legacy Peer project remains unchanged; accidental validation-source inclusions were removed.

Clean-machine deployment, real worker operations, native picker/dialog workflows, one-time UAC/task registration and legacy handover, and a SYSTEM reboot without Windows logon still require manual acceptance. Deploy the complete self-contained folder locally, enable recovery from Settings, then inspect task results and `<LaunchPadRoot>\logs\recovery`. Process survival checks do not prove application readiness. See README for setup and rollback behavior.

### Access-denied investigation
An isolated Windows replacement test reproduced Win32 5 with a reader that allows read/write sharing but denies delete-sharing; replacement succeeds when the reader closes. This is an evidence-supported failure mode, not confirmation that every production Launch/Restore denial has this cause. The updated application retries transient replacement errors and reports specific operations/paths/PIDs for persistent failures. Retry once with the rebuilt app and inspect the new context if the problem persists; no production files, workers, or ACLs were changed by this investigation.

## Legacy Projects Removal
The legacy WinForms manager and Peer.Boot source projects are deleted from the repository and solution. Worker projects remain unchanged. This deletion does not disable deployed tasks or remove Git history. Existing installations will still have the legacy scheduled-task detection/handover in place.
