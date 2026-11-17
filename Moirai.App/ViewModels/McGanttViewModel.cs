using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed class McGanttViewModel
{
    public string Title { get; }
    public List<GanttLegendItem> LegendItems { get; }
    public ResultFilterModel Filter { get; }

    public McGanttViewModel(string title, Scenario scenario, Schedule schedule, List<GanttLegendItem> legendItems)
    {
        Title = title;
        LegendItems = legendItems;
        Filter = new ResultFilterModel(scenario, schedule);
    }
}
