using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IgnitionDeck.UI.Controls;

public sealed record PeerTableColumn(string Header, double Width, bool Stretch = false);

/// <summary>A compact native table with a frozen header and shared column sizing.</summary>
public sealed class PeerTable : Grid
{
    private const double ScrollbarSpace = 16;
    private readonly IReadOnlyList<PeerTableColumn> _columns;
    private readonly Grid _layout = new();
    private readonly Grid _header;
    private readonly StackPanel _rows = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly double _minimumWidth;
    private int _rowCount;

    public PeerTable(params PeerTableColumn[] columns)
    {
        _columns = columns;
        _minimumWidth = columns.Sum(column => column.Width) + ScrollbarSpace;
        _layout.Width = _minimumWidth;
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _header = CreateRow();
        _header.HorizontalAlignment = HorizontalAlignment.Left;
        _header.Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
        for (var index = 0; index < columns.Length; index++)
        {
            var label = new TextBlock
            {
                Text = columns[index].Header, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 10, 10, 10), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, index);
            _header.Children.Add(label);
        }
        _layout.Children.Add(_header);
        var rowsScroll = new ScrollViewer
        {
            Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        AutomationProperties.SetName(rowsScroll, "Table rows");
        Grid.SetRow(rowsScroll, 1);
        _layout.Children.Add(rowsScroll);
        var horizontalScroll = new ScrollViewer
        {
            Content = _layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled
        };
        AutomationProperties.SetName(horizontalScroll, "Table horizontal scroll");
        Children.Add(horizontalScroll);
        SizeChanged += (_, _) => ResizeColumns();
        ResizeColumns();
    }

    private Grid CreateRow()
    {
        var row = new Grid { MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var column in _columns)
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.Stretch ? new GridLength(1, GridUnitType.Star) : new GridLength(column.Width),
                MinWidth = column.Width
            });
        return row;
    }

    public void AddRow(bool hasErrors, params FrameworkElement[] cells)
    {
        if (cells.Length != _columns.Count) throw new ArgumentException("Each table row must match the column count.");
        var row = CreateRow();
        if (hasErrors)
            row.Background = Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"] as Brush;
        else if (_rowCount % 2 == 1)
            row.Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
        for (var index = 0; index < cells.Length; index++)
        {
            var cell = cells[index];
            cell.Margin = new Thickness(10, 4, 10, 4);
            cell.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cell, index);
            row.Children.Add(cell);
        }
        _rows.Children.Add(new Border
        {
            Child = row, BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush
        });
        _rowCount++;
    }

    public void ShowEmpty(string message)
    {
        if (_rowCount > 0) return;
        _rows.Children.Add(new TextBlock { Text = message, Margin = new Thickness(10, 16, 10, 16), TextWrapping = TextWrapping.Wrap });
    }

    public static TextBlock TextCell(string value, string column)
    {
        var text = new TextBlock
        {
            Text = value, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = true
        };
        ToolTipService.SetToolTip(text, value);
        AutomationProperties.SetName(text, $"{column}: {value}");
        return text;
    }

    private void ResizeColumns()
    {
        _layout.Width = Math.Max(_minimumWidth, ActualWidth);
        // Reserve the same scrollbar space in header and body so columns stay aligned.
        _header.Width = _layout.Width - ScrollbarSpace;
        _rows.Width = _layout.Width - ScrollbarSpace;
    }
}
