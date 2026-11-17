using System.Text.Json;
using Avalonia.Platform;

namespace MOIRAI.Views;

public sealed class AirportWeather
{
    public sealed record Stats(string Icao, double[] MonthlyRates, double Persistence);

    private readonly Dictionary<string, Stats> _byIcao;
    private AirportWeather(Dictionary<string, Stats> byIcao) => _byIcao = byIcao;

    private static AirportWeather? _cached;

    public static AirportWeather Load() => _cached ??= Parse();

    public Stats? For(string? icao) =>
        !string.IsNullOrWhiteSpace(icao) && _byIcao.TryGetValue(icao.Trim().ToUpperInvariant(), out var s) ? s : null;

    private static AirportWeather Parse()
    {
        var map = new Dictionary<string, Stats>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Moirai.App/Assets/turkey-airport-weather.json"));
            using var doc = JsonDocument.Parse(stream);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("icao", out var ic) || ic.GetString() is not { } icao) continue;
                if (!e.TryGetProperty("monthlyFlyableRate", out var mr) || mr.ValueKind != JsonValueKind.Array) continue;
                var rates = mr.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), 0, 1) : 0.5)
                    .ToArray();
                if (rates.Length != 12) continue;
                double phi = e.TryGetProperty("phi", out var p) && p.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(p.GetDouble(), 0, 0.98) : 0.5;
                map[icao.Trim().ToUpperInvariant()] = new Stats(icao.Trim().ToUpperInvariant(), rates, phi);
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception) { }
        return new AirportWeather(map);
    }
}
