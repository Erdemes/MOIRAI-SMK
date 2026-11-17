using MOIRAI.Scheduling.Revising;

namespace MOIRAI.ViewModels;

internal static class EngineOptions
{
    public static RevisingOptions Advanced(bool useSearch, int searchParallelism = 0) => new()
    {
        EnableLocalRepair = true,
        EnableTightnessOrder = true,
        EnableScarcityCrew = true,
        EnableBottleneckSteer = true,
        EnableRestStagger = true,
        EnableGlobalSearch = useSearch,
        SearchParallelism = searchParallelism,
    };

    public static string Label(bool useSearch) => useSearch ? "advanced engine" : "smart engine";
}
