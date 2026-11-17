using System.Text.Json;
using Avalonia.Platform;

namespace MOIRAI.Views;

public sealed class TurkeyAirports
{
    public sealed record Airport(string Icao, string Name, double Lon, double Lat, int RunwayFt);

    public IReadOnlyList<Airport> Airports { get; }
    private TurkeyAirports(IReadOnlyList<Airport> airports) => Airports = airports;

    private static TurkeyAirports? _cached;

    public static TurkeyAirports Load() => _cached ??= Parse();

    private static TurkeyAirports Parse()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Moirai.App/Assets/turkey-airports.json"));
        using var doc = JsonDocument.Parse(stream);
        var list = new List<Airport>();
        foreach (var e in doc.RootElement.EnumerateArray())
            list.Add(new Airport(
                e.GetProperty("icao").GetString() ?? "",
                e.GetProperty("name").GetString() ?? "",
                e.GetProperty("lon").GetDouble(),
                e.GetProperty("lat").GetDouble(),
                e.GetProperty("ft").GetInt32()));
        return new TurkeyAirports(list);
    }

    public Airport? Nearest(double lon, double lat)
    {
        Airport? best = null;
        double bestD = double.MaxValue;
        foreach (var a in Airports)
        {
            double dx = a.Lon - lon, dy = a.Lat - lat;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }
}
