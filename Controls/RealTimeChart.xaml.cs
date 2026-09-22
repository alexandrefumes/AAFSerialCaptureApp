using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Canvas = System.Windows.Controls.Canvas;
using TextBlock = System.Windows.Controls.TextBlock;
using AAFSerialCaptureApp.Models;

namespace AAFSerialCaptureApp.Controls;

/// <summary>Lightweight self-scaling multi-series line chart, redrawn on demand from value snapshots.</summary>
public partial class RealTimeChart : System.Windows.Controls.UserControl
{
    /// <summary>Max samples kept on screen per series (adjustable via the window-size slider); also limits how
    /// much history the model copies per tick.</summary>
    public static int MaxVisiblePoints { get; set; } = 1000;

    /// <summary>Moving-average window used when smoothing is enabled (mimics an oscilloscope's "high-res"/averaging mode).</summary>
    private const int SmoothingWindow = 9;

    /// <summary>Minimum vertical range shown, as a fraction of the signal's magnitude, so a near-flat DC signal isn't
    /// zoomed in until its own noise looks like a wild AC oscillation.</summary>
    private const double MinRangeFraction = 0.05;

    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xDE)),
        new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
        new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)),
        new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),
        new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD)),
        new SolidColorBrush(Color.FromRgb(0x16, 0xA0, 0x85))
    };

    private readonly Dictionary<string, Polyline> _lines = new();
    private readonly Dictionary<string, LegendEntry> _legendEntries = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<LegendEntry> _legend = new();
    private double _lastWidth = -1, _lastHeight = -1;

    // Live state, frozen while zoomed: last full series/totals given to Render(), used as the source for zoom slicing.
    private IReadOnlyDictionary<string, double[]>? _lastSeries;
    private Dictionary<string, int>? _lastTotalCounts;
    private IReadOnlyList<(int Number, int Index)>? _lastBoundaries;
    private bool _lastSmooth = true;

    // Zoom window, expressed as a fraction [0,1] of _lastSeries; null = live/unzoomed.
    private double? _zoomFracStart, _zoomFracEnd;
    private Dictionary<string, int> _zoomIdxStart = new();

    private bool _isSelecting;
    private double _selectionStartX;

    /// <summary>Max points visible before dot markers are hidden to avoid clutter.</summary>
    private const int PointMarkerThreshold = 150;

    public RealTimeChart()
    {
        InitializeComponent();
        LegendItems.ItemsSource = _legend;
        PlotArea.SizeChanged += PlotArea_SizeChanged;
    }

    /// <summary>Re-renders with the last known data once the control gets a real size (e.g. its tab becomes selected).</summary>
    private void PlotArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastSeries == null) return;
        RenderCore(_zoomFracStart.HasValue ? BuildZoomedSnapshot() : _lastSeries, _lastSmooth);
    }

    private sealed class LegendEntry : INotifyPropertyChanged
    {
        public Brush? Color { get; init; }

        private string? _text;
        public string? Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Redraws every series (each independently scaled with padding so a steady DC-like signal looks flat/smooth).
    /// While zoomed into a selected time window, live updates are ignored until the zoom is reset/widened.
    /// <paramref name="totalCounts"/>/<paramref name="boundaries"/> are optional and enable the capture-divider lines.</summary>
    public void Render(IReadOnlyDictionary<string, double[]> series, bool smooth = true,
        Dictionary<string, int>? totalCounts = null, IReadOnlyList<(int Number, int Index)>? boundaries = null)
    {
        _lastSmooth = smooth;
        if (_zoomFracStart == null)
        {
            _lastSeries = series;
            _lastTotalCounts = totalCounts;
        }
        _lastBoundaries = boundaries;

        if (_zoomFracStart.HasValue)
        {
            RebuildZoomedSeries();
            RenderCore(BuildZoomedSnapshot(), smooth);
        }
        else
        {
            RenderCore(series, smooth);
        }
    }

    private Dictionary<string, double[]> BuildZoomedSnapshot()
    {
        var result = new Dictionary<string, double[]>();
        if (_lastSeries == null) return result;
        foreach (var name in _lastSeries.Keys)
        {
            result[name] = _zoomSlice.TryGetValue(name, out var slice) ? slice : _lastSeries[name];
        }
        return result;
    }

    private readonly Dictionary<string, double[]> _zoomSlice = new();

    private void RebuildZoomedSeries()
    {
        _zoomSlice.Clear();
        _zoomIdxStart.Clear();
        if (_lastSeries == null || _zoomFracStart == null || _zoomFracEnd == null) return;

        foreach (var (name, data) in _lastSeries)
        {
            if (data.Length == 0) { _zoomSlice[name] = data; _zoomIdxStart[name] = 0; continue; }
            int idxStart = (int)(_zoomFracStart.Value * (data.Length - 1));
            int idxEnd = (int)(_zoomFracEnd.Value * (data.Length - 1));
            idxEnd = Math.Max(idxEnd, idxStart + 1);
            idxEnd = Math.Min(idxEnd, data.Length - 1);
            _zoomSlice[name] = data[idxStart..(idxEnd + 1)];
            _zoomIdxStart[name] = idxStart;
        }
    }

    private void RenderCore(IReadOnlyDictionary<string, double[]> series, bool smooth)
    {
        if (PlotArea.ActualWidth <= 0 || PlotArea.ActualHeight <= 0) return;

        double width = PlotArea.ActualWidth;
        double height = PlotArea.ActualHeight;

        var names = series.Keys.OrderBy(n => n).ToList();

        // Drop series that no longer exist (channel disappeared).
        foreach (var stale in _lines.Keys.Except(names).ToList())
        {
            PlotCanvas.Children.Remove(_lines[stale]);
            _lines.Remove(stale);
            if (_legendEntries.Remove(stale, out var staleEntry)) _legend.Remove(staleEntry);
        }

        PointsCanvas.Children.Clear();

        int firstStart = 0, firstCount = 0, firstOffset = 0;
        double firstXStep = width;
        bool haveFirst = false;

        for (int c = 0; c < names.Count; c++)
        {
            var name = names[c];
            var data = series[name];
            var brush = Palette[c % Palette.Length];

            if (!_lines.TryGetValue(name, out var polyline))
            {
                polyline = new Polyline { Stroke = brush, StrokeThickness = 1.5, SnapsToDevicePixels = true };
                RenderOptions.SetEdgeMode(polyline, EdgeMode.Aliased);
                _lines[name] = polyline;
                PlotCanvas.Children.Add(polyline);
            }
            if (!_legendEntries.TryGetValue(name, out var legendEntry))
            {
                legendEntry = new LegendEntry { Color = brush };
                _legendEntries[name] = legendEntry;
                _legend.Add(legendEntry);
            }

            if (data.Length == 0)
            {
                polyline.Points = new PointCollection();
                legendEntry.Text = ChannelLabels.Describe(name);
                continue;
            }

            int start = Math.Max(0, data.Length - MaxVisiblePoints);
            int count = data.Length - start;

            double rawMin = double.MaxValue, rawMax = double.MinValue;
            for (int i = start; i < data.Length; i++)
            {
                var v = data[i];
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            var plotData = smooth ? Smooth(data, start, count, SmoothingWindow) : data;
            double min = rawMin, max = rawMax;
            if (smooth)
            {
                min = double.MaxValue; max = double.MinValue;
                for (int i = 0; i < count; i++)
                {
                    var v = plotData[start + i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            // Pad the range so a steady (DC-like) signal reads as a calm line instead of the
            // display auto-zooming into its own small noise and making it look like AC ripple.
            double magnitude = Math.Max(Math.Abs(min), Math.Abs(max));
            double minRange = Math.Max(magnitude * MinRangeFraction, 1e-6);
            double range = Math.Max(max - min, minRange);
            double mid = (min + max) / 2;
            double padded = range * 1.2;
            min = mid - padded / 2;
            max = mid + padded / 2;

            double xStep = count > 1 ? width / (count - 1) : width;
            var points = new PointCollection(count);
            for (int i = 0; i < count; i++)
            {
                double value = plotData[start + i];
                double x = i * xStep;
                double y = height - (value - min) / (max - min) * height;
                points.Add(new Point(x, y));

                // Only draw individual dots when there are few enough points to not look like a smear.
                if (count <= PointMarkerThreshold)
                {
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = brush };
                    Canvas.SetLeft(dot, x - 2.5);
                    Canvas.SetTop(dot, y - 2.5);
                    PointsCanvas.Children.Add(dot);
                }
            }
            points.Freeze();
            polyline.Points = points;

            legendEntry.Text = $"{ChannelLabels.Describe(name)} (últ={data[^1]:0.##}, mín={rawMin:0.##}, máx={rawMax:0.##})";

            if (!haveFirst)
            {
                haveFirst = true;
                firstStart = start;
                firstCount = count;
                firstXStep = xStep;
                firstOffset = ComputeAbsoluteOffset(name, data.Length);
            }
        }

        if (width != _lastWidth || height != _lastHeight)
        {
            _lastWidth = width;
            _lastHeight = height;
            DrawGridLines(width, height);
        }

        DrawBoundaries(height, firstStart, firstCount, firstXStep, firstOffset);
    }

    /// <summary>Absolute (all-time) sample index corresponding to index 0 of the given rendered array for <paramref name="name"/>.</summary>
    private int ComputeAbsoluteOffset(string name, int renderedLength)
    {
        if (_lastTotalCounts == null || _lastSeries == null || !_lastSeries.TryGetValue(name, out var fullWindow))
            return 0;

        int offset = _lastTotalCounts.TryGetValue(name, out var total) ? total - fullWindow.Length : 0;
        if (_zoomFracStart.HasValue && _zoomIdxStart.TryGetValue(name, out var zStart))
            offset += zStart;
        return offset;
    }

    private void DrawBoundaries(double height, int start, int count, double xStep, int offset)
    {
        BoundaryCanvas.Children.Clear();
        if (_lastBoundaries == null || count <= 0) return;

        foreach (var (number, index) in _lastBoundaries)
        {
            int rel = index - offset - start;
            if (rel < 0 || rel >= count) continue;

            double x = rel * xStep;
            BoundaryCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = height,
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            });

            var label = new TextBlock
            {
                Text = $"Captura {number}",
                FontSize = 9,
                Foreground = Brushes.DimGray,
                Background = Brushes.White
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            BoundaryCanvas.Children.Add(label);
        }
    }

    /// <summary>Centered moving average over [start, start+count), used only for the on-screen line (stats stay raw).</summary>
    private static double[] Smooth(double[] data, int start, int count, int window)
    {
        var result = new double[data.Length];
        int half = window / 2;
        for (int i = start; i < start + count; i++)
        {
            int lo = Math.Max(start, i - half);
            int hi = Math.Min(start + count - 1, i + half);
            double sum = 0;
            for (int j = lo; j <= hi; j++) sum += data[j];
            result[i] = sum / (hi - lo + 1);
        }
        return result;
    }

    private void DrawGridLines(double width, double height)
    {
        GridCanvas.Children.Clear();
        const int horizontalLines = 4;
        for (int i = 1; i < horizontalLines; i++)
        {
            double y = height * i / horizontalLines;
            var l = new Line
            {
                X1 = 0,
                Y1 = y,
                X2 = width,
                Y2 = y,
                Stroke = Brushes.LightGray,
                StrokeThickness = 0.5,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            GridCanvas.Children.Add(l);
        }
    }

    public void Clear()
    {
        foreach (var line in _lines.Values) PlotCanvas.Children.Remove(line);
        _lines.Clear();
        _legendEntries.Clear();
        _legend.Clear();
        GridCanvas.Children.Clear();
        BoundaryCanvas.Children.Clear();
        PointsCanvas.Children.Clear();
        _lastWidth = _lastHeight = -1;
        _lastSeries = null;
        _lastTotalCounts = null;
        _lastBoundaries = null;
        _zoomFracStart = _zoomFracEnd = null;
        ResetZoomButton.Visibility = Visibility.Collapsed;
    }

    private void PlotArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSelecting = true;
        _selectionStartX = e.GetPosition(PlotArea).X;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Margin = new Thickness(_selectionStartX, 0, 0, 0);
        SelectionRectangle.Height = PlotArea.ActualHeight;
        SelectionRectangle.Visibility = Visibility.Visible;
        PlotArea.CaptureMouse();
    }

    private void PlotArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting) return;
        double x = e.GetPosition(PlotArea).X;
        double left = Math.Min(_selectionStartX, x);
        double width = Math.Abs(x - _selectionStartX);
        SelectionRectangle.Margin = new Thickness(left, 0, 0, 0);
        SelectionRectangle.Width = width;
    }

    private void PlotArea_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        PlotArea.ReleaseMouseCapture();
        SelectionRectangle.Visibility = Visibility.Collapsed;

        double endX = e.GetPosition(PlotArea).X;
        double left = Math.Min(_selectionStartX, endX);
        double right = Math.Max(_selectionStartX, endX);
        double plotWidth = PlotArea.ActualWidth;
        if (right - left < 4 || plotWidth <= 0 || _lastSeries == null) return;

        // Selection fractions are always relative to the currently frozen/live _lastSeries,
        // so re-selecting while already zoomed narrows further instead of jumping back to live data.
        double baseStart = _zoomFracStart ?? 0;
        double baseEnd = _zoomFracEnd ?? 1;
        double baseSpan = baseEnd - baseStart;

        double fracStart = baseStart + Math.Clamp(left / plotWidth, 0, 1) * baseSpan;
        double fracEnd = baseStart + Math.Clamp(right / plotWidth, 0, 1) * baseSpan;

        _zoomFracStart = fracStart;
        _zoomFracEnd = fracEnd;
        RebuildZoomedSeries();
        ResetZoomButton.Visibility = Visibility.Visible;
        RenderCore(BuildZoomedSnapshot(), _lastSmooth);
    }

    private void PlotArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        PlotArea.ReleaseMouseCapture();
        SelectionRectangle.Visibility = Visibility.Collapsed;
    }

    /// <summary>Right-click zooms out one step (doubles the visible span, centered); resets to live once wide enough.</summary>
    private void PlotArea_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_zoomFracStart == null || _zoomFracEnd == null || _lastSeries == null) return;

        double center = (_zoomFracStart.Value + _zoomFracEnd.Value) / 2;
        double halfSpan = (_zoomFracEnd.Value - _zoomFracStart.Value);
        double newStart = Math.Clamp(center - halfSpan, 0, 1);
        double newEnd = Math.Clamp(center + halfSpan, 0, 1);

        if (newEnd - newStart >= 0.98)
        {
            _zoomFracStart = _zoomFracEnd = null;
            ResetZoomButton.Visibility = Visibility.Collapsed;
            RenderCore(_lastSeries, _lastSmooth);
        }
        else
        {
            _zoomFracStart = newStart;
            _zoomFracEnd = newEnd;
            RebuildZoomedSeries();
            RenderCore(BuildZoomedSnapshot(), _lastSmooth);
        }
        e.Handled = true;
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        _zoomFracStart = _zoomFracEnd = null;
        ResetZoomButton.Visibility = Visibility.Collapsed;
        if (_lastSeries != null) RenderCore(_lastSeries, _lastSmooth);
    }
}
