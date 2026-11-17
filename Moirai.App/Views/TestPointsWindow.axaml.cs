using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MOIRAI.Views;

public partial class TestPointsWindow : Window
{
    public TestPointsWindow()
    {
        InitializeComponent();
        Zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoom();
    }

    private void ApplyZoom() =>
        Scaler.LayoutTransform = new ScaleTransform(Zoom.Value, 1);
}
