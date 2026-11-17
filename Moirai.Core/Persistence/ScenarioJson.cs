using System.Text.Json;
using System.Text.Json.Serialization;
using MOIRAI.Scheduling;

namespace MOIRAI.Persistence;

public static class ScenarioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Scenario Clone(Scenario scenario) =>
        JsonSerializer.Deserialize<Scenario>(JsonSerializer.Serialize(scenario, Options), Options)
        ?? throw new InvalidOperationException("Scenario clone round-trip failed.");
}
