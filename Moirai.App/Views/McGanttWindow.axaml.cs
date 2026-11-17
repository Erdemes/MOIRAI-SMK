using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MOIRAI.Views;

public partial class McGanttWindow : Window
{
    private bool _syncingScroll;

    public McGanttWindow()
    {
        InitializeComponent();
        GanttZoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoom();

        GanttScroll.ScrollChanged += (_, e) => SyncX(GanttScroll, TpScroll, e);
        TpScroll.ScrollChanged += (_, e) => SyncX(TpScroll, GanttScroll, e);
    }

    private void ApplyZoom()
    {
        GanttScaler.LayoutTransform = new ScaleTransform(GanttZoom.Value, GanttZoom.Value);
        TpScaler.LayoutTransform = new ScaleTransform(GanttZoom.Value, 1);
    }

    private void SyncX(ScrollViewer source, ScrollViewer target, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || Math.Abs(e.OffsetDelta.X) < 0.5) return;
        _syncingScroll = true;
        try
        {
            if (Math.Abs(target.Offset.X - source.Offset.X) > 0.5)
                target.Offset = new Vector(source.Offset.X, target.Offset.Y);
        }
        finally
        {
            _syncingScroll = false;
        }
    }
}
