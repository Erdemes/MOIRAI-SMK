using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MOIRAI.Views;

public sealed class LocationPickerControl : Control
{
    private const double Pad = 18;

    public static readonly StyledProperty<double> PinLonProperty =
        AvaloniaProperty.Register<LocationPickerControl, double>(
            nameof(PinLon), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> PinLatProperty =
        AvaloniaProperty.Register<LocationPickerControl, double>(
            nameof(PinLat), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> PinIcaoProperty =
        AvaloniaProperty.Register<LocationPickerControl, string>(
            nameof(PinIcao), defaultValue: "", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> HasPinProperty =
        AvaloniaProperty.Register<LocationPickerControl, bool>(
            nameof(HasPin), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public double PinLon { get => GetValue(PinLonProperty); set => SetValue(PinLonProperty, value); }
    public double PinLat { get => GetValue(PinLatProperty); set => SetValue(PinLatProperty, value); }
    public string PinIcao { get => GetValue(PinIcaoProperty); set => SetValue(PinIcaoProperty, value); }
    public bool HasPin { get => GetValue(HasPinProperty); set => SetValue(HasPinProperty, value); }

    static LocationPickerControl()
    {
        AffectsRender<LocationPickerControl>(PinLonProperty, PinLatProperty, HasPinProperty);
    }

    private static readonly Color Sea = Color.Parse("#21344E");
    private static readonly Color Land = Color.Parse("#34434F");
    private static readonly IBrush Hint = new SolidColorBrush(Color.Parse("#C7CDD6"));
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#9AA7BD"));
    private static readonly IBrush DotText = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly Color Accent = Color.Parse("#8B78FF");
    private static readonly IBrush TipBg = new SolidColorBrush(Color.Parse("#1A2233"), 0.92);
    private static readonly IPen TipBorder = new Pen(new SolidColorBrush(Accent, 0.8), 1);

    private StreamGeometry? _geo;
    private Size _geoSize;
    private bool _dragging;
    private TurkeyAirports.Airport? _hovered;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        SetPinFromPoint(e.GetPosition(this));
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging) { SetPinFromPoint(p); return; }

        var hit = AirportAt(p);
        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private TurkeyMap.Projection? Proj()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 20 || h < 20) return null;
        return new TurkeyMap.Projection(TurkeyMap.Load(), w, h, Pad);
    }

    private TurkeyAirports.Airport? AirportAt(Point p)
    {
        if (Proj() is not { } proj) return null;
        TurkeyAirports.Airport? best = null;
        double bestD = 9 * 9;
        foreach (var a in TurkeyAirports.Load().Airports)
        {
            var (x, y) = proj.ToScreen(a.Lon, a.Lat);
            double d = (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
            if (d <= bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private void SetPinFromPoint(Point p)
    {
        if (TurkeyAirports.Load().Nearest(NearestLon(p), NearestLat(p)) is not { } a) return;
        PinLon = a.Lon;
        PinLat = a.Lat;
        PinIcao = a.Icao;
        HasPin = true;
        InvalidateVisual();
    }

    private double NearestLon(Point p) => Proj() is { } proj ? proj.ToGeo(p.X, p.Y).Lon : 0;
    private double NearestLat(Point p) => Proj() is { } proj ? proj.ToGeo(p.X, p.Y).Lat : 0;

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        if (Proj() is not { } proj) return;
        Point P(double lon, double lat) { var (x, y) = proj.ToScreen(lon, lat); return new Point(x, y); }

        ctx.FillRectangle(new SolidColorBrush(Sea), new Rect(0, 0, W, H));

        if (_geo is null || _geoSize != Bounds.Size)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                foreach (var prov in TurkeyMap.Load().Provinces)
                    foreach (var ring in prov.Rings)
                    {
                        c.BeginFigure(P(ring[0].Lon, ring[0].Lat), true);
                        for (int i = 1; i < ring.Length; i++) c.LineTo(P(ring[i].Lon, ring[i].Lat));
                        c.EndFigure(true);
                    }
            _geo = g;
            _geoSize = Bounds.Size;
        }
        var borderPen = new Pen(new SolidColorBrush(Color.Parse("#8893A6"), 0.45), 0.7);
        ctx.DrawGeometry(new SolidColorBrush(Land), borderPen, _geo);

        foreach (var a in TurkeyAirports.Load().Airports)
        {
            var c = P(a.Lon, a.Lat);
            bool isPin = HasPin && string.Equals(a.Icao, PinIcao, StringComparison.OrdinalIgnoreCase);
            if (isPin) continue;
            ctx.DrawEllipse(Dot, null, c, 2.4, 2.4);
        }

        if (_hovered is { } hv && !(HasPin && string.Equals(hv.Icao, PinIcao, StringComparison.OrdinalIgnoreCase)))
        {
            var c = P(hv.Lon, hv.Lat);
            ctx.DrawEllipse(new SolidColorBrush(Accent), new Pen(Brushes.White, 1), c, 3.5, 3.5);
            Tooltip(ctx, $"{hv.Icao} · {hv.Name}", c, W);
        }

        if (HasPin)
        {
            var pin = P(PinLon, PinLat);
            ctx.DrawEllipse(new SolidColorBrush(Accent, 0.30), null, pin, 13, 13);
            ctx.DrawEllipse(new SolidColorBrush(Accent), new Pen(Brushes.White, 1.5), pin, 6, 6);
            ctx.DrawLine(new Pen(new SolidColorBrush(Accent), 2), pin, new Point(pin.X, pin.Y + 16));
            string label = string.IsNullOrEmpty(PinIcao)
                ? $"{Math.Abs(PinLat):0.000}°N {Math.Abs(PinLon):0.000}°E"
                : $"{PinIcao} · {Math.Abs(PinLat):0.000}°N {Math.Abs(PinLon):0.000}°E";
            Tooltip(ctx, label, new Point(pin.X, pin.Y - 8), W);
        }
        else
        {
            Text(ctx, "Hover an airfield to see its ICAO", new Point(W * 0.5, 16), 12, Hint, center: true);
        }
    }

    private static void Tooltip(DrawingContext ctx, string s, Point at, double w)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, DotText);
        double pad = 5, bw = ft.Width + 2 * pad, bh = ft.Height + 2 * pad;
        double x = Math.Clamp(at.X - bw / 2, 2, w - bw - 2);
        double y = at.Y - bh - 8;
        if (y < 2) y = at.Y + 10;
        var rect = new Rect(x, y, bw, bh);
        ctx.DrawRectangle(TipBg, TipBorder, rect, 4, 4);
        ctx.DrawText(ft, new Point(x + pad, y + pad));
    }

    private static void Text(DrawingContext ctx, string s, Point at, double size, IBrush brush, bool center = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        if (center) at = new Point(at.X - ft.Width / 2, at.Y);
        ctx.DrawText(ft, at);
    }
}
