using System.Text.Json;
using Avalonia.Platform;

namespace MOIRAI.Views;

public sealed class TurkeyMap
{
    public sealed record Province(string Name, List<(double Lon, double Lat)[]> Rings, (double Lon, double Lat) Centroid);

    public IReadOnlyList<Province> Provinces { get; }
    public double LonMin { get; }
    public double LonMax { get; }
    public double LatMin { get; }
    public double LatMax { get; }

    private static TurkeyMap? _cached;

    public static TurkeyMap Load() => _cached ??= Parse();

    private TurkeyMap(List<Province> provinces, double lonMin, double lonMax, double latMin, double latMax)
    {
        Provinces = provinces;
        LonMin = lonMin; LonMax = lonMax; LatMin = latMin; LatMax = latMax;
    }

    private static TurkeyMap Parse()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Moirai.App/Assets/turkey-provinces.json"));
        using var doc = JsonDocument.Parse(stream);

        double lonMin = double.MaxValue, lonMax = double.MinValue, latMin = double.MaxValue, latMax = double.MinValue;
        var provinces = new List<Province>();

        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            string name = f.GetProperty("properties").GetProperty("name").GetString() ?? "";
            var geom = f.GetProperty("geometry");
            string type = geom.GetProperty("type").GetString() ?? "";
            var coords = geom.GetProperty("coordinates");

            var rings = new List<(double, double)[]>();
            if (type == "Polygon")
                AddPolygon(coords, rings);
            else if (type == "MultiPolygon")
                foreach (var poly in coords.EnumerateArray())
                    AddPolygon(poly, rings);

            if (rings.Count == 0) continue;

            var main = rings.OrderByDescending(r => r.Length).First();
            double cx = main.Average(p => p.Item1), cy = main.Average(p => p.Item2);

            foreach (var ring in rings)
                foreach (var (lon, lat) in ring)
                {
                    if (lon < lonMin) lonMin = lon; if (lon > lonMax) lonMax = lon;
                    if (lat < latMin) latMin = lat; if (lat > latMax) latMax = lat;
                }

            provinces.Add(new Province(name, rings, (cx, cy)));
        }

        return new TurkeyMap(provinces, lonMin, lonMax, latMin, latMax);
    }

    private static void AddPolygon(JsonElement polygon, List<(double, double)[]> into)
    {
        foreach (var ring in polygon.EnumerateArray())
        {
            var pts = new List<(double, double)>();
            int i = 0, n = ring.GetArrayLength();
            foreach (var pt in ring.EnumerateArray())
            {
                bool keep = n <= 12 || i % 2 == 0 || i == n - 1;
                if (keep)
                {
                    double lon = pt[0].GetDouble(), lat = pt[1].GetDouble();
                    pts.Add((lon, lat));
                }
                i++;
            }
            if (pts.Count >= 3) into.Add(pts.ToArray());
        }
    }

    private static readonly Dictionary<string, (double Lon, double Lat)> Gazetteer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ankara"] = (32.86, 39.93),
        ["akıncı"] = (32.565, 40.078),
        ["mürted"] = (32.565, 40.078),
        ["konya"] = (32.49, 37.87),
        ["merzifon"] = (35.522, 40.829),
        ["amasya"] = (35.83, 40.65),
        ["eskişehir"] = (30.52, 39.78),
        ["balıkesir"] = (27.89, 39.65),
        ["bandırma"] = (27.98, 40.32),
        ["izmir"] = (27.01, 38.51),
        ["çiğli"] = (27.01, 38.49),
        ["antalya"] = (30.79, 36.90),
        ["dalaman"] = (28.79, 36.71),
        ["diyarbakır"] = (40.20, 37.89),
        ["malatya"] = (38.09, 38.43),
        ["erhaç"] = (38.09, 38.43),
        ["kayseri"] = (35.49, 38.77),
        ["adana"] = (35.43, 37.00),
        ["incirlik"] = (35.43, 37.00),
        ["çorlu"] = (27.91, 41.14),
        ["erzurum"] = (41.17, 39.95),
        ["van"] = (43.32, 38.47),
        ["samsun"] = (36.33, 41.28),
        ["sivas"] = (37.01, 39.81),
        ["batman"] = (41.12, 37.93),
        ["isparta"] = (30.55, 37.79),
    };

    public readonly struct Projection
    {
        private readonly double _lonMin, _latMax, _kx, _scale, _offX, _offY;

        public Projection(TurkeyMap map, double w, double h, double pad)
        {
            _lonMin = map.LonMin; _latMax = map.LatMax;
            double midLat = (map.LatMin + map.LatMax) / 2;
            _kx = Math.Cos(midLat * Math.PI / 180);
            double geoW = (map.LonMax - map.LonMin) * _kx, geoH = map.LatMax - map.LatMin;
            _scale = Math.Min((w - 2 * pad) / geoW, (h - 2 * pad) / geoH);
            _offX = (w - geoW * _scale) / 2;
            _offY = (h - geoH * _scale) / 2;
        }

        public (double X, double Y) ToScreen(double lon, double lat) =>
            (_offX + (lon - _lonMin) * _kx * _scale, _offY + (_latMax - lat) * _scale);

        public (double Lon, double Lat) ToGeo(double x, double y) =>
            (_lonMin + (x - _offX) / (_kx * _scale), _latMax - (y - _offY) / _scale);
    }

    public (double Lon, double Lat)? Locate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim();
        if (Gazetteer.TryGetValue(key, out var g)) return g;
        var prov = Provinces.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        return prov is null ? null : prov.Centroid;
    }
}
