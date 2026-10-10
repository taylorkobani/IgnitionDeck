using System.Diagnostics;
using IgnitionDeck.Core;
using IgnitionDeck.Services;
using IgnitionDeck.UI.Controls;
using IgnitionDeck.UI.Modals;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace IgnitionDeck.UI;

public sealed class MainLayout : UserControl
{
    private readonly Window _window;
    private PeerManager _manager = null!;
    private ApplicationSettings _applicationSettings = null!;
    private readonly LayoutModalCoordinatorService _modals = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DispatcherQueueTimer _servicePollTimer;
    private readonly NavigationView _navigation = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, OpenPaneLength = 240 };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly Grid _contentHost = new();
    private readonly Grid _profileDetailsHost = new();
    private readonly Grid _profileOverviewHost = new();
    private readonly TabView _profileTabs = new() { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
    private readonly ScrollViewer _settingsScroll;
    private readonly TextBlock _heading = new() { FontSize = 28, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, IsIndeterminate = true };
    private readonly ComboBox _environment = new() { ItemsSource = new[] { "Dev", "Prod" }, SelectedIndex = 0, Width = 130, Header = "Environment" };
    private readonly ComboBox _profileEnvironment = new() { ItemsSource = new[] { "Dev", "Prod" }, SelectedIndex = 0, Width = 130, Header = "Environment", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _revision = new() { Width = 310, Header = "Revision" };
    private readonly TextBox _rootPath = new() { Header = "LaunchPad root", MinWidth = 500 };
    private RecoveryTaskState? _recoveryTaskState;
    private string _page = "Execution Profiles";
    private bool _refreshing;
    private bool _initialized;
    private bool _closed;
    private bool _polling;
    private long _operationVersion;
    private IReadOnlyList<ProfileStatus> _profiles = [];
    private IReadOnlyList<ProfileStatus> _overviewProd = [];
    private IReadOnlyList<ProfileStatus> _overviewDev = [];
    private IReadOnlyList<ReplicaEntry> _replicas = [];
    private IReadOnlyList<RevisionEntry> _revisions = [];
    private IReadOnlyList<BuildEntry> _builds = [];
    private string EnvironmentName => (_page == "Execution Profiles" ? _profileEnvironment : _environment).SelectedItem?.ToString() ?? "Dev";
    private string? RevisionName => _revision.SelectedItem as string;

    public MainLayout(Window window)
    {
        _window = window;
        _servicePollTimer = DispatcherQueue.CreateTimer();
        _servicePollTimer.Interval = TimeSpan.FromSeconds(ApplicationSettings.DefaultPollingIntervalSeconds);
        _servicePollTimer.IsRepeating = true;
        _servicePollTimer.Tick += OnServicePollTick;
        _profileTabs.TabItems.Add(new TabViewItem { Header = "Overview", IsClosable = false, Content = _profileOverviewHost });
        var details = new Grid();
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        details.Children.Add(_profileEnvironment);
        Grid.SetRow(_profileDetailsHost, 1);
        details.Children.Add(_profileDetailsHost);
        _profileTabs.TabItems.Add(new TabViewItem { Header = "Details", IsClosable = false, Content = details });
        _profileTabs.SelectedIndex = 0;
        _profileTabs.SelectionChanged += async (_, args) =>
        {
            if (!args.AddedItems.OfType<TabViewItem>().Any()) return;
            if (_initialized && !_refreshing && _page == "Execution Profiles") await ExecuteAsync(() => RefreshAsync(force: true));
        };
        foreach (var (name, symbol) in new[] { ("Execution Profiles", Symbol.AllApps), ("Builds", Symbol.Download), ("Revisions", Symbol.Library), ("Replicas", Symbol.Play), ("Settings", Symbol.Setting) })
            _navigation.MenuItems.Add(new NavigationViewItem { Content = name, Tag = name, Icon = new SymbolIcon(symbol) });
        _navigation.PaneHeader = new TextBlock { Text = "IgnitionDeck", FontSize = 22, Margin = new Thickness(16, 24, 16, 16) };
        var main = new Grid { Padding = new Thickness(24) };
        _toolbar.Margin = new Thickness(0, 16, 0, 0);
        _progress.Margin = new Thickness(0, 16, 0, 0);
        _contentHost.Margin = new Thickness(0, 16, 0, 0);
        _status.Margin = new Thickness(0, 16, 0, 0);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            main.RowDefinitions.Add(new RowDefinition { Height = height });
        main.Children.Add(_heading);
        Grid.SetRow(_toolbar, 1); main.Children.Add(_toolbar);
        Grid.SetRow(_progress, 2); main.Children.Add(_progress);
        _settingsScroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_contentHost, 3); main.Children.Add(_contentHost);
        Grid.SetRow(_status, 4); main.Children.Add(_status);
        _navigation.Content = main;
        Content = _navigation;
        _navigation.SelectedItem = _navigation.MenuItems[0];
        _navigation.SelectionChanged += async (_, args) =>
        {
            _page = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Execution Profiles";
            if (_initialized) await ExecuteAsync(() => RefreshAsync(force: true));
        };
        _environment.SelectionChanged += async (_, _) =>
        {
            if (_initialized && !_refreshing) await ExecuteAsync(() => RefreshAsync(force: true));
        };
        _profileEnvironment.SelectionChanged += async (_, _) =>
        {
            if (_initialized && !_refreshing && _page == "Execution Profiles") await ExecuteAsync(() => RefreshAsync(force: true));
        };
        _revision.SelectionChanged += async (_, _) =>
        {
            if (_initialized && !_refreshing) await ExecuteAsync(() => RefreshAsync(force: true));
        };
        Loaded += OnLoaded;
        _window.AppWindow.Closing += (_, args) =>
        {
            if (_operations.CurrentCount != 0) return;
            args.Cancel = true;
            _status.Text = "Wait for the current operation to finish before closing IgnitionDeck.";
        };
    }

    public void Stop()
    {
        _closed = true;
        _servicePollTimer.Stop();
        _servicePollTimer.Tick -= OnServicePollTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized || _closed) return;
        _modals.XamlRoot = XamlRoot;
        if (App.IsBootRunning())
        {
            await NotifyAsync("Boot in progress", "Booting services in progress. Run IgnitionDeck when booting is complete.");
            _window.Close();
            return;
        }
        await ExecuteAsync(async () =>
        {
            _applicationSettings = await Task.Run(() => ApplicationSettings.Load(App.SettingsPath));
            _manager = new PeerManager(_applicationSettings);
            await Task.Run(_manager.Initialize);
            _servicePollTimer.Interval = TimeSpan.FromSeconds(_applicationSettings.PollingIntervalSeconds);
            _initialized = true;
            await RefreshAsync(force: true);
        });
        if (_initialized && !_closed) _servicePollTimer.Start();
    }

    private async void OnServicePollTick(DispatcherQueueTimer sender, object args)
    {
        if (_closed || !_initialized || _polling || _refreshing || _operations.CurrentCount == 0 || _modals.IsShowing
            || _page is not "Execution Profiles" and not "Replicas") return;
        var page = _page;
        var environment = EnvironmentName;
        var revision = RevisionName;
        var profileTab = _profileTabs.SelectedIndex;
        var operationVersion = _operationVersion;
        bool IsCurrent() => !_closed && !_refreshing && _operations.CurrentCount != 0 && !_modals.IsShowing
            && _operationVersion == operationVersion && _page == page && EnvironmentName == environment
            && RevisionName == revision && _profileTabs.SelectedIndex == profileTab;

        _polling = true;
        try
        {
            if (page == "Execution Profiles")
            {
                if (profileTab == 0)
                {
                    var overview = await Task.Run(() => (Prod: _manager.GetProfiles("Prod"), Dev: _manager.GetProfiles("Dev")));
                    if (IsCurrent()) UpdateProfileOverview(overview.Prod, overview.Dev, force: false);
                }
                else
                {
                    var profiles = await Task.Run(() => _manager.GetProfiles(environment));
                    if (IsCurrent() && !_profiles.SequenceEqual(profiles)) { _profiles = profiles; RenderProfiles(); }
                }
            }
            else
            {
                IReadOnlyList<ReplicaEntry> replicas = revision is null ? [] : await Task.Run(() => _manager.GetReplicas(environment, revision));
                if (IsCurrent() && !_replicas.SequenceEqual(replicas)) { _replicas = replicas; RenderReplicas(); }
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                var message = "Service status polling failed: " + OperationDiagnostics.Describe(ex);
                if (_status.Text != message) _status.Text = message;
            }
        }
        finally { _polling = false; }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_closed || _operations.CurrentCount == 0) return;
        await _operations.WaitAsync();
        _operationVersion++;
        try
        {
            _navigation.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            await action();
        }
        catch (Exception ex)
        {
            var message = OperationDiagnostics.Describe(ex);
            _status.Text = message;
            if (!_closed) await NotifyAsync("Operation failed", message);
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            _progress.IsIndeterminate = true;
            _navigation.IsEnabled = true;
            _operations.Release();
        }
    }

    private Task ExecuteAsync(Action action) => ExecuteAsync(async () =>
    {
        await Task.Run(() => { using var coordination = RecoveryRunner.AcquireCoordination(_manager.LaunchPadRoot); action(); });
        await RefreshAsync(force: true);
    });
    private Task NotifyAsync(string title, string message) => _modals.ShowAsync(new MessageDialog(title, message));
    private async Task<bool> ConfirmAsync(string title, string message) => await _modals.ShowAsync(new MessageDialog(title, message, confirm: true)) == ContentDialogResult.Primary;
    private Button CreateAction(string text, Func<Task> action, bool enabled = true)
    {
        var button = new Button { Content = text, IsEnabled = enabled, Padding = new Thickness(10, 4, 10, 4), MinHeight = 30 };
        button.Click += async (_, _) =>
        {
            if (_operations.CurrentCount == 0 || _modals.IsShowing) return;
            try { await action(); }
            catch (Exception ex) { await NotifyAsync("Operation failed", OperationDiagnostics.Describe(ex)); }
        };
        return button;
    }
    private void AddAction(StackPanel panel, string text, Func<Task> action, bool enabled = true) => panel.Children.Add(CreateAction(text, action, enabled));

    private async Task RefreshAsync(bool force)
    {
        _refreshing = true;
        try
        {
            _heading.Text = _page;
            if (force)
            {
                _toolbar.Children.Clear();
                if (_page is not "Builds" and not "Settings" and not "Execution Profiles") _toolbar.Children.Add(_environment);
                if (_page == "Replicas") _toolbar.Children.Add(_revision);
                if (_page != "Execution Profiles") AddAction(_toolbar, "Refresh", () => ExecuteAsync(() => RefreshAsync(force: true)));
                if (_page == "Builds") AddAction(_toolbar, "Import build", ImportAsync);
                if (_page == "Revisions") AddAction(_toolbar, "Flush unused", async () =>
                {
                    var environment = EnvironmentName;
                    if (await ConfirmAsync("Flush unused revisions", $"Delete all unused revisions in {environment}? Live and draining revisions are protected."))
                        await ExecuteAsync(() => { var count = _manager.FlushUnused(environment); _status.DispatcherQueue.TryEnqueue(() => _status.Text = $"Deleted {count} revisions."); });
                });
                if (_page == "Replicas")
                {
                    AddAction(_toolbar, "Launch all", () => BulkStateAsync(RunState.Running));
                    AddAction(_toolbar, "Retire revision", () => BulkStateAsync(RunState.Shutdown));
                }
                _toolbar.Visibility = _toolbar.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            var env = EnvironmentName;
            switch (_page)
            {
                case "Execution Profiles":
                    if (!_contentHost.Children.Contains(_profileTabs))
                    {
                        _contentHost.Children.Clear();
                        _contentHost.Children.Add(_profileTabs);
                    }
                    if (_profileTabs.SelectedIndex == 0)
                    {
                        var overview = await Task.Run(() => (Prod: _manager.GetProfiles("Prod"), Dev: _manager.GetProfiles("Dev")));
                        UpdateProfileOverview(overview.Prod, overview.Dev, force);
                    }
                    else
                    {
                        var profiles = await Task.Run(() => _manager.GetProfiles(env));
                        if (force || !_profiles.SequenceEqual(profiles)) { _profiles = profiles; RenderProfiles(); }
                    }
                    break;
                case "Builds":
                    var builds = await Task.Run(_manager.GetBuilds);
                    if (force || !_builds.SequenceEqual(builds)) { _builds = builds; RenderBuilds(); }
                    break;
                case "Revisions":
                    var revisions = await Task.Run(() => _manager.GetRevisions(env));
                    if (force || !_revisions.SequenceEqual(revisions)) { _revisions = revisions; RenderRevisions(); }
                    break;
                case "Replicas":
                    var available = await Task.Run(() => _manager.GetRevisions(env));
                    var names = available.Select(entry => entry.Name).ToList();
                    var previous = RevisionName;
                    if (_revision.ItemsSource is not IReadOnlyList<string> old || !old.SequenceEqual(names))
                    {
                        _revision.ItemsSource = names;
                        _revision.SelectedItem = previous is not null && names.Contains(previous) ? previous : names.FirstOrDefault();
                    }
                    var selected = RevisionName;
                    var replicas = selected is null ? [] : await Task.Run(() => _manager.GetReplicas(env, selected));
                    if (force || !_replicas.SequenceEqual(replicas)) { _replicas = replicas; RenderReplicas(); }
                    break;
                case "Settings":
                    if (force)
                    {
                        try
                        {
                            var registration = await Task.Run(() => RecoveryStartupTask.Inspect(Environment.ProcessPath!, App.SettingsPath));
                            _recoveryTaskState = registration.State;
                        }
                        catch (Exception ex)
                        {
                            _recoveryTaskState = null;
                            _status.Text = "Cannot inspect startup registration: " + OperationDiagnostics.Describe(ex);
                        }
                        RenderSettings();
                    }
                    break;
            }
        }
        finally { _refreshing = false; }
    }

    private void UpdateProfileOverview(IReadOnlyList<ProfileStatus> prod, IReadOnlyList<ProfileStatus> dev, bool force)
    {
        if (!force && _profileOverviewHost.Children.Count > 0 && _overviewProd.SequenceEqual(prod) && _overviewDev.SequenceEqual(dev)) return;
        _overviewProd = prod;
        _overviewDev = dev;
        var charts = new Grid();
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        charts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        charts.Children.Add(CreateProfileOverviewPanel("Prod", prod));
        var devPanel = CreateProfileOverviewPanel("Dev", dev);
        Grid.SetColumn(devPanel, 1);
        charts.Children.Add(devPanel);
        charts.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < 616;
            charts.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            charts.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
            charts.RowSpacing = stacked ? 24 : 0;
            Grid.SetColumn(devPanel, stacked ? 0 : 1);
            Grid.SetRow(devPanel, stacked ? 1 : 0);
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(charts);
        _profileOverviewHost.Children.Clear();
        _profileOverviewHost.Children.Add(new ScrollViewer
        {
            Content = panel,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
    }

    private ProcessPieChart CreateProfileOverviewPanel(string environment, IReadOnlyList<ProfileStatus> profiles)
    {
        var details = CreateAction("Details", () => ExecuteAsync(async () =>
        {
            // Change both selections within one operation to avoid duplicate refreshes.
            _profileEnvironment.SelectedItem = environment;
            _profileTabs.SelectedIndex = 1;
            await RefreshAsync(force: true);
        }));
        return new ProcessPieChart(environment, profiles, details);
    }

    private PeerTable CreateTable(params PeerTableColumn[] columns)
    {
        var table = new PeerTable(columns);
        var host = _page == "Execution Profiles" ? _profileDetailsHost : _contentHost;
        host.Children.Clear();
        host.Children.Add(table);
        return table;
    }
    private static StackPanel Actions(StackPanel row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(panel);
        return panel;
    }

    private void RenderProfiles()
    {
        var table = CreateTable(new("App / Service", 180, Stretch: true), new("Revision", 180), new("PID", 70),
            new("Status", 100), new("Errors", 100), new("Logs", 80), new("Folder", 90));
        foreach (var profile in _profiles)
        {
            var available = profile.Entry is not null;
            var errors = CreateAction(profile.HasErrors ? "View" : "None", async () =>
            {
                var result = await _modals.ShowAsync(new LogsDialog($"Errors — {profile.Service}", [profile.ErrorPath], archive: true));
                if (result == ContentDialogResult.Primary && await ConfirmAsync("Archive error", "Move the active error file to a timestamped archive?"))
                    await ExecuteAsync(() => { _manager.ArchiveError(profile.ErrorPath); });
            }, available && profile.HasErrors);
            var logs = CreateAction("View", async () =>
            {
                var files = await Task.Run(() => _manager.GetLogs(profile.AppPath));
                await _modals.ShowAsync(new LogsDialog($"Logs — {profile.Service}", files));
            }, available);
            table.AddRow(profile.HasErrors, PeerTable.TextCell(profile.Service, "App / Service"), PeerTable.TextCell(profile.Revision, "Revision"),
                PeerTable.TextCell(profile.Pid?.ToString() ?? "—", "PID"), PeerTable.TextCell(profile.Status, "Status"),
                errors, logs, CreateAction("Open", () => OpenFolderAsync(profile.AppPath), available));
        }
        table.ShowEmpty("No execution profiles or expected services for this environment.");
    }

    private void RenderBuilds()
    {
        var table = CreateTable(new("Build", 300, Stretch: true), new("Updated", 170), new("Add revision", 140), new("Delete", 100));
        foreach (var build in _builds)
        {
            var addRevision = CreateAction("Add revision", async () =>
            {
                var apps = await Task.Run(() => _manager.GetBuildApps(build.Name));
                var dialog = new RevisionDialog(apps);
                if (await _modals.ShowAsync(dialog) == ContentDialogResult.Primary)
                {
                    var request = dialog.Request;
                    await ExecuteAsync(() => { _manager.CreateRevision(build.Name, request.Apps, request.Environment); });
                    _status.Text = "Revision created.";
                }
            });
            var delete = CreateAction("Delete", async () =>
            {
                if (await ConfirmAsync("Delete build", $"Permanently delete {build.Name}?")) await ExecuteAsync(() => _manager.DeleteBuild(build.Name));
            });
            table.AddRow(false, PeerTable.TextCell(build.Name, "Build"), PeerTable.TextCell(build.Updated.ToString("yyyy-MM-dd HH:mm"), "Updated"), addRevision, delete);
        }
        table.ShowEmpty("No builds in the last three months. Import a Peer Build folder to begin.");
    }

    private void RenderRevisions()
    {
        var table = CreateTable(new("Revision", 300, Stretch: true), new("Usage", 170), new("Active apps", 120), new("Delete", 100));
        foreach (var revision in _revisions)
        {
            var delete = CreateAction(revision.ActiveApps > 0 ? "Locked" : "Delete", async () =>
            {
                if (await ConfirmAsync("Delete revision", $"Permanently delete {revision.Name}?")) await ExecuteAsync(() => _manager.DeleteRevision(revision.Environment, revision.Name));
            }, revision.ActiveApps == 0);
            table.AddRow(false, PeerTable.TextCell(revision.Name, "Revision"), PeerTable.TextCell(revision.Usage, "Usage"),
                PeerTable.TextCell(revision.ActiveApps.ToString(), "Active apps"), delete);
        }
        table.ShowEmpty("No revisions in this environment.");
    }

    private void RenderReplicas()
    {
        var table = CreateTable(new("App", 250, Stretch: true), new("Run state", 100), new("PID", 70), new("Status", 130),
            new("Folder", 90), new("Worker controls", 280));
        foreach (var replica in _replicas)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var state in Enum.GetValues<RunState>())
                AddAction(actions, state.ToString(), async () =>
                {
                    if (await ConfirmAsync($"Set {state}", $"Set {replica.Name} to {state}? Shutdown requests cooperative drain; it does not force-kill."))
                        await ExecuteAsync(() => _manager.SetState(replica, state));
                }, !(replica.IsLive && replica.State == RunState.Shutdown));
            table.AddRow(false, PeerTable.TextCell(replica.Name, "App"), PeerTable.TextCell(replica.State.ToString(), "Run state"),
                PeerTable.TextCell(replica.Pid?.ToString() ?? "—", "PID"), PeerTable.TextCell(replica.Status, "Status"),
                CreateAction("Open", () => OpenFolderAsync(replica.AppPath)), actions);
        }
        table.ShowEmpty("Select a revision containing worker apps.");
    }

    private void RenderSettings()
    {
        _contentHost.Children.Clear();
        _contentHost.Children.Add(_settingsScroll);
        _body.Children.Clear();
        _rootPath.Text = _manager.LaunchPadRoot;
        _body.Children.Add(_rootPath);
        var roots = Actions(_body);
        AddAction(roots, "Browse folder", async () => { var path = await PickFolderAsync(); if (path is not null) _rootPath.Text = path; });
        AddAction(roots, "Save LaunchPad root", async () =>
        {
            var path = _rootPath.Text;
            if (_manager.IsCurrentLaunchPadRoot(path)
                || await ConfirmAsync("Change LaunchPad", "Switch the managed LaunchPad root? Existing files are not moved. Automatic recovery reads this same settings file at startup."))
                await ExecuteAsync(() => _manager.SaveLaunchPadRoot(path));
        });
        var pollingInterval = new NumberBox
        {
            Header = "Polling interval (seconds)",
            Minimum = ApplicationSettings.MinPollingIntervalSeconds,
            Maximum = ApplicationSettings.MaxPollingIntervalSeconds,
            Value = _applicationSettings.PollingIntervalSeconds,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _body.Children.Add(pollingInterval);
        var savingPollingInterval = false;
        pollingInterval.ValueChanged += async (_, args) =>
        {
            if (savingPollingInterval || args.NewValue == _applicationSettings.PollingIntervalSeconds) return;
            savingPollingInterval = true;
            pollingInterval.IsEnabled = false;
            try
            {
                await ExecuteAsync(async () =>
                {
                    var seconds = args.NewValue;
                    if (!double.IsFinite(seconds) || seconds != Math.Truncate(seconds)
                        || seconds < ApplicationSettings.MinPollingIntervalSeconds || seconds > ApplicationSettings.MaxPollingIntervalSeconds)
                        throw new ArgumentException("Polling interval must be a whole number from 1 to 3,600 seconds.");
                    await Task.Run(() => _applicationSettings.SavePollingInterval((int)seconds));
                    _servicePollTimer.Stop();
                    _servicePollTimer.Interval = TimeSpan.FromSeconds(_applicationSettings.PollingIntervalSeconds);
                    if (!_closed) _servicePollTimer.Start();
                });
            }
            finally
            {
                pollingInterval.Value = _applicationSettings.PollingIntervalSeconds;
                pollingInterval.IsEnabled = true;
                savingPollingInterval = false;
            }
        };
        _body.Children.Add(new TextBlock { Text = "Automatic reboot recovery", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var registrationState = _recoveryTaskState switch
        {
            RecoveryTaskState.Enabled => "Enabled",
            RecoveryTaskState.Disabled or RecoveryTaskState.NeedsRepair => "Disabled",
            RecoveryTaskState.NotRegistered => "Not configured",
            _ => "Unknown — unable to verify"
        };
        _body.Children.Add(new TextBlock { Text = $"Automatic recovery: {registrationState}", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var recovery = Actions(_body);
        AddAction(recovery, "Enable automatic recovery", () => ConfigureRecoveryAsync(enable: true), enabled: _recoveryTaskState is RecoveryTaskState.NotRegistered or RecoveryTaskState.Disabled or RecoveryTaskState.NeedsRepair);
        AddAction(recovery, "Disable automatic recovery", () => ConfigureRecoveryAsync(enable: false), enabled: _recoveryTaskState is RecoveryTaskState.Enabled);
        AddAction(recovery, "Open recovery log", () => ExecuteAsync(async () =>
        {
            var report = await Task.Run(() =>
            {
                var path = RecoveryRunner.StatusPath(_manager.LaunchPadRoot);
                if (!File.Exists(path)) throw new FileNotFoundException("No recovery log recorded yet.");
                return System.Text.Json.JsonSerializer.Deserialize<RecoveryReport>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Invalid recovery status.");
            });
            if (!File.Exists(report.LogPath)) throw new FileNotFoundException("Recovery log file was not found.", report.LogPath);
            using var process = Process.Start(new ProcessStartInfo(report.LogPath) { UseShellExecute = true });
        }));
        var controls = Actions(_body);
        AddAction(controls, "Restore all processes", () => ExecuteAsync(async () => { await RestoreAsync(); await RefreshAsync(force: true); }));
        AddAction(controls, "Force shutdown workers", async () =>
        {
            if (await ConfirmAsync("SERIOUS WARNING — Force shutdown", "Force-kill ALL VotePool.Peer.Worker processes on this machine, including other LaunchPads? In-flight work may be lost. Execution intent is retained for explicit restore."))
                await ExecuteAsync(async () =>
                {
                    var result = await Task.Run(() => { using var coordination = RecoveryRunner.AcquireCoordination(_manager.LaunchPadRoot); return _manager.ForceStopWorkers(); });
                    await NotifyAsync("Force shutdown result", result.Count == 0 ? "No worker processes found." : string.Join("\n", result));
                    await RefreshAsync(force: true);
                });
        });
    }

    private async Task RestoreAsync()
    {
        _progress.IsIndeterminate = false;
        _progress.Maximum = 1;
        _progress.Value = 0;
        var progress = new Progress<RestoreProgress>(value =>
        {
            _progress.Maximum = Math.Max(1, value.Total);
            _progress.Value = value.Completed;
            _status.Text = value.Message;
        });
        var failures = await Task.Run(() => { using var coordination = RecoveryRunner.AcquireCoordination(_manager.LaunchPadRoot); return _manager.Restore(progress); });
        _status.Text = failures.Count == 0 ? "Restore complete." : $"Restore incomplete: {failures.Count} failures.";
        if (failures.Count > 0) await NotifyAsync("Restore incomplete", string.Join("\n", failures));
    }

    private async Task ConfigureRecoveryAsync(bool enable)
    {
        var message = enable
            ? $"Register '{Environment.ProcessPath}' to restore Running workers as SYSTEM after reboot, without Windows logon? This disables the legacy Boot task. Use a stable local installation, not a debug, OneDrive or network folder. A UAC prompt may appear."
            : "Disable IgnitionDeck's reboot recovery? Running workers will not be stopped. The legacy Boot task will not be re-enabled automatically.";
        if (!await ConfirmAsync(enable ? "Enable automatic recovery" : "Disable automatic recovery", message)) return;
        await ExecuteAsync(async () =>
        {
            if (enable) await Task.Run(() => { using var coordination = RecoveryRunner.AcquireCoordination(_manager.LaunchPadRoot); _manager.EnsureRecoverySettings(); });
            await RecoverySetupService.ConfigureAsync(enable, App.SettingsPath);
            await RefreshAsync(force: true);
            _status.Text = enable ? "Automatic recovery enabled." : "Automatic recovery disabled. Existing workers were not stopped.";
        });
    }

    private async Task BulkStateAsync(RunState state)
    {
        var revision = RevisionName;
        var env = EnvironmentName;
        if (revision is null) { await NotifyAsync("Select a revision", "Select a revision first."); return; }
        if (await ConfirmAsync(state == RunState.Running ? "Launch revision" : "Retire revision", state == RunState.Running ? "Launch all apps? Paused apps remain paused." : "Request graceful shutdown of every app in this revision?"))
            await ExecuteAsync(() => _manager.SetRevisionState(env, revision, state));
    }

    private async Task ImportAsync()
    {
        var path = await PickFolderAsync();
        if (path is null) return;
        if (await ConfirmAsync("Import build", "Import this Peer Build folder? An existing build with the same name will be replaced."))
            await ExecuteAsync(() => { _manager.ImportBuild(path, replace: true); });
    }
    private Task<string?> PickFolderAsync() => DesktopFolderPicker.PickAsync(WindowNative.GetWindowHandle(_window));
    private static Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(path);
        using var process = Process.Start(info);
        return Task.CompletedTask;
    }
}
