using System.Text;
using MOIRAI.Scheduling;

namespace MOIRAI.Analysis;

public static class ScenarioComparison
{
    public sealed class Cell
    {
        public required string Label { get; init; }
        public required CampaignStatus Status { get; init; }
        public DateOnly? EndDate { get; init; }

        public string Text =>
            $"{(EndDate.HasValue ? EndDate.Value.ToString("yyyy-MM-dd") : "—")} ({Status})";
    }

    public sealed class Row
    {
        public required string CampaignName { get; init; }
        public required List<Cell> Cells { get; init; }
    }

    public sealed class Result
    {
        public required List<string> Labels { get; init; }
        public required List<Row> Rows { get; init; }
        public required List<int> MetCounts { get; init; }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Campaign | " + string.Join(" | ", Labels));
            foreach (var row in Rows)
                sb.AppendLine(row.CampaignName + " | " + string.Join(" | ", row.Cells.Select(c => c.Text)));
            sb.AppendLine("Met | " + string.Join(" | ", MetCounts));
            return sb.ToString();
        }
    }

    public static Result Compare(IReadOnlyList<Scenario> scenarios, IReadOnlyList<string> labels)
    {
        if (scenarios.Count == 0 || scenarios.Count != labels.Count)
            throw new ArgumentException("Provide one label per scenario (at least one).");

        var schedules = scenarios.Select(s => new Scheduler().Run(s)).ToList();

        var rows = new List<Row>();
        foreach (var camp in scenarios[0].Campaigns)
        {
            var cells = new List<Cell>();
            for (int k = 0; k < scenarios.Count; k++)
            {
                var c = scenarios[k].Campaigns.FirstOrDefault(x => x.Id == camp.Id) ?? camp;
                schedules[k].CampaignEndDates.TryGetValue(camp.Id, out var end);
                cells.Add(new Cell
                {
                    Label = labels[k],
                    Status = schedules[k].StatusOf(scenarios[k], c),
                    EndDate = end,
                });
            }
            rows.Add(new Row { CampaignName = camp.Name, Cells = cells });
        }

        var met = schedules
            .Select((sch, k) => scenarios[k].Campaigns.Count(c => sch.StatusOf(scenarios[k], c) == CampaignStatus.Met))
            .ToList();

        return new Result { Labels = labels.ToList(), Rows = rows, MetCounts = met };
    }
}
