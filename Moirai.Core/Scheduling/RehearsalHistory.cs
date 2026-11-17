using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal static class RehearsalHistory
{
    public static bool Matches(Rehearsal rehearsal, IEnumerable<string> crew, int sortieNo,
        DateOnly sortieDay, Func<DateOnly, bool> isWorkday, ScenarioConfig config,
        string locationId, DateOnly? departure = null, int lookback = 0)
    {
        if (rehearsal.SortieNo != sortieNo || rehearsal.Date >= sortieDay
            || !rehearsal.CrewIds.ToHashSet().SetEquals(crew)) return false;
        var end = departure ?? sortieDay;
        if (rehearsal.Date >= end || rehearsal.LocationId != (departure.HasValue ? "" : locationId))
            return false;
        int gap = 0, maxGap = departure.HasValue ? lookback : config.MaxRehearsalGapDays;
        for (var day = rehearsal.Date; day < end; day = day.AddDays(1))
            if ((!config.RehearsalOnWorkdaysOnly || isWorkday(day)) && ++gap > maxGap)
                return false;
        return gap > 0;
    }

    public static Rehearsal Copy(Rehearsal source, IEnumerable<Sortie>? actuals = null)
    {
        var sortie = actuals?.FirstOrDefault(s => s.CampaignId == source.CampaignId
            && s.SortieNo == source.SortieNo && s.PilotId == source.PilotId);
        return new Rehearsal {
            CampaignId = source.CampaignId, Date = source.Date, Block = source.Block,
            SortieNo = source.SortieNo, SortieDate = source.SortieDate, PilotId = source.PilotId,
            AdditionalPilotIds = new(source.AdditionalPilotIds.Count > 0 ? source.AdditionalPilotIds : sortie?.AdditionalPilotIds ?? new()),
            SupportPilotIds = new(source.SupportPilotIds.Count > 0 ? source.SupportPilotIds : sortie?.SupportPilotIds ?? new()),
            EngineerIds = new(source.EngineerIds), SimulatorId = source.SimulatorId,
            LocationId = source.LocationId, IsActual = source.IsActual
        };
    }
}
