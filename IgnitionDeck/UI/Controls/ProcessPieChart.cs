using IgnitionDeck.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace IgnitionDeck.UI.Controls;

internal sealed class ProcessPieChart : UserControl
{
    public ProcessPieChart(string environment, IReadOnlyList<ProfileStatus> profiles)
    {
        var services = profiles.GroupBy(profile => profile.Service, StringComparer.OrdinalIgnoreCase).ToList();
        var liveServices = services.Select(group => group.Where(profile => profile.Entry is not null && profile.Status is "Running" or "Paused").ToList()).ToList();
        var healthy = liveServices.Count(group => group.Count > 0 && !group.Any(profile => profile.HasErrors));
        var errors = liveServices.Count(group => group.Any(profile => profile.HasErrors));
        var missing = liveServices.Count(group => group.Count == 0);
        var green = new SolidColorBrush(Colors.SeaGreen);
        var red = new SolidColorBrush(Colors.IndianRed);
        var gray = new SolidColorBrush(Colors.Gray);
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24), MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = environment, FontSize = 24, FontWeight = FontWeights.SemiBold });
        var canvas = new Canvas { Width = 240, Height = 240 };
        AutomationProperties.SetName(canvas, $"{environment} processes: {healthy} healthy running, {errors} running with errors, {missing} missing");
        var categories = new (int Count, Brush Color)[] { (healthy, green), (errors, red), (missing, gray) };
        var populated = categories.Where(category => category.Count > 0).ToList();
        if (populated.Count <= 1)
        {
            canvas.Children.Add(new Ellipse { Width = 240, Height = 240, Fill = populated.Count == 0 ? gray : populated[0].Color });
        }
        else
        {
            var start = 0.0;
            foreach (var category in populated)
            {
                var fraction = (double)category.Count / services.Count;
                AddSlice(canvas, start, fraction, category.Color);
                start += fraction;
            }
        }
        panel.Children.Add(canvas);
        panel.Children.Add(Legend($"Healthy running: {healthy}", green));
        panel.Children.Add(Legend($"Running with errors: {errors}", red));
        panel.Children.Add(Legend($"Missing: {missing}", gray));
        panel.Children.Add(new TextBlock { Text = services.Count == 0 ? "No expected services or execution profiles." : $"Total services: {services.Count}", TextWrapping = TextWrapping.Wrap });
        Content = panel;
    }

    private static StackPanel Legend(string text, Brush brush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 16 });
        return row;
    }

    private static void AddSlice(Canvas canvas, double start, double fraction, Brush brush)
    {
        static Point Edge(double turn) => new(120 + 120 * Math.Sin(turn * Math.Tau), 120 - 120 * Math.Cos(turn * Math.Tau));
        var figure = new PathFigure { StartPoint = new Point(120, 120), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = Edge(start) });
        figure.Segments.Add(new ArcSegment { Point = Edge(start + fraction), Size = new Size(120, 120), IsLargeArc = fraction > 0.5, SweepDirection = SweepDirection.Clockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        canvas.Children.Add(new Path { Data = geometry, Fill = brush });
    }
}
