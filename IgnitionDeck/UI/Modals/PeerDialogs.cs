using IgnitionDeck.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IgnitionDeck.UI.Modals;

public sealed class MessageDialog : ContentDialog
{
    public MessageDialog(string title, string message, bool confirm = false)
    {
        Title = title;
        Content = new ScrollViewer
        {
            MaxHeight = 450,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 540, IsTextSelectionEnabled = true }
        };
        CloseButtonText = confirm ? "Cancel" : "Close";
        PrimaryButtonText = confirm ? "Confirm" : string.Empty;
        DefaultButton = ContentDialogButton.Close;
    }
}

public sealed class RevisionDialog : ContentDialog
{
    private readonly ComboBox _environment = new() { ItemsSource = new[] { "Development", "Production" }, SelectedIndex = 0 };
    private readonly List<(string Name, ToggleSwitch Toggle)> _apps = [];
    public RevisionRequest Request => new(_environment.SelectedItem?.ToString() ?? "Development", _apps.Where(app => app.Toggle.IsOn).Select(app => app.Name).ToList());

    public RevisionDialog(IReadOnlyList<string> apps)
    {
        Title = "Create revision";
        PrimaryButtonText = "Create";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        var content = new StackPanel { Spacing = 12, MinWidth = 450 };
        content.Children.Add(new TextBlock { Text = "Environment" });
        content.Children.Add(_environment);
        content.Children.Add(new TextBlock { Text = "Infrastructure is included automatically for the selected environment.", TextWrapping = TextWrapping.Wrap });
        var all = new ToggleSwitch { Header = "Select all apps", IsOn = apps.Count > 0 };
        content.Children.Add(all);
        var appPanel = new StackPanel { Spacing = 8 };
        foreach (var name in apps)
        {
            var toggle = new ToggleSwitch { Header = name, IsOn = true };
            toggle.Toggled += (_, _) => IsPrimaryButtonEnabled = _apps.Any(app => app.Toggle.IsOn);
            _apps.Add((name, toggle));
            appPanel.Children.Add(toggle);
        }
        all.Toggled += (_, _) => { foreach (var app in _apps) app.Toggle.IsOn = all.IsOn; };
        content.Children.Add(new ScrollViewer { Content = appPanel, MaxHeight = 340 });
        Content = content;
        IsPrimaryButtonEnabled = apps.Count > 0;
    }
}

public sealed class LogsDialog : ContentDialog
{
    public LogsDialog(string title, IReadOnlyList<string> files, bool archive = false)
    {
        Title = title;
        CloseButtonText = "Close";
        PrimaryButtonText = archive ? "Archive error file" : string.Empty;
        DefaultButton = ContentDialogButton.Close;
        IsPrimaryButtonEnabled = archive && files.Count > 0;
        var text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), MinWidth = 520, Height = 400
        };
        var selection = new ComboBox { ItemsSource = files, HorizontalAlignment = HorizontalAlignment.Stretch };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(selection);
        content.Children.Add(text);
        Content = content;
        selection.SelectionChanged += async (_, _) =>
        {
            var selected = selection.SelectedItem as string;
            if (selected is null) return;
            text.Text = "Loading…";
            try
            {
                var value = await Task.Run(() => PeerManager.ReadLog(selected));
                if (selection.SelectedItem as string == selected) text.Text = value;
            }
            catch (Exception ex) { text.Text = $"Could not read log: {ex.Message}"; }
        };
        if (files.Count > 0) selection.SelectedIndex = 0;
        else text.Text = "No log files available.";
    }
}
