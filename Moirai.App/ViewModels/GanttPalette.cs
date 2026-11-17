using Avalonia.Media;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public static class GanttPalette
{
    public static readonly Color[] Palette =
    {
        Color.Parse("#5B8DEF"),
        Color.Parse("#F25C54"),
        Color.Parse("#2EC4B6"),
        Color.Parse("#FFBF1F"),
        Color.Parse("#B388FF"),
        Color.Parse("#7AC74F"),
        Color.Parse("#FF7AB6"),
        Color.Parse("#FF8C42"),
        Color.Parse("#4DD0E1"),
        Color.Parse("#D4E157"),
    };

    public static readonly Color WeekendShade = Color.FromArgb(34, 255, 255, 255);
    public static readonly Color HolidayShade = Color.FromArgb(84, 167, 139, 250);
    public static readonly Color NotFlyableShade = Color.FromArgb(66, 244, 63, 94);
    public static readonly Color MaintenanceBand = Color.FromArgb(56, 148, 163, 184);
    public static readonly Color LeaveBand = Color.FromArgb(44, 234, 179, 8);
    public static readonly Color DeploymentBand = Color.FromArgb(40, 124, 92, 255);

    public static Dictionary<string, Color> CampaignColors(Scenario sc) =>
        sc.Campaigns.Select((c, i) => (c.Id, color: Palette[i % Palette.Length]))
                    .ToDictionary(t => t.Id, t => t.color);

    public static Dictionary<string, Color> GroupColors(Scenario sc)
    {
        var map = new Dictionary<string, Color>();
        for (int i = 0; i < sc.CampaignGroups.Count; i++)
        {
            var g = sc.CampaignGroups[i];
            map[g.Id] = !string.IsNullOrWhiteSpace(g.Color) && Color.TryParse(g.Color, out var c)
                ? c
                : Palette[i % Palette.Length];
        }
        return map;
    }

    public static List<GanttLegendItem> Legend(Scenario sc)
    {
        var items = new List<GanttLegendItem>
        {
            new("Weekend", WeekendShade),
            new("Holiday", HolidayShade),
            new("Not flyable", NotFlyableShade),
            new("Maintenance", MaintenanceBand),
            new("Leave", LeaveBand),
        };
        if (sc.Locations.Count > 0)
            items.Add(new GanttLegendItem("Deployed", DeploymentBand));

        var colors = CampaignColors(sc);
        DateOnly CampKey(Domain.Campaign c) => CampaignDeadlines.EffectiveMilestone(sc, c) ?? DateOnly.MaxValue;
        var handled = new HashSet<string>();

        foreach (var g in sc.CampaignGroups.OrderBy(g => g.TargetEndDate ?? DateOnly.MaxValue))
        {
            var children = sc.Campaigns.Where(c => c.GroupId == g.Id).OrderBy(CampKey).ToList();
            if (children.Count == 0) continue;
            items.Add(new GanttLegendItem($"◈ {g.Name}", default, isHeader: true));
            foreach (var c in children)
            {
                items.Add(new GanttLegendItem(c.Name, colors[c.Id]));
                handled.Add(c.Id);
            }
        }
        foreach (var c in sc.Campaigns.Where(c => !handled.Contains(c.Id)))
            items.Add(new GanttLegendItem(c.Name, colors[c.Id]));
        return items;
    }
}

public sealed class GanttLegendItem
{
    public GanttLegendItem(string label, Color color, bool isHeader = false)
    {
        Label = label;
        Swatch = new SolidColorBrush(color);
        IsHeader = isHeader;
    }

    public string Label { get; }
    public IBrush Swatch { get; }
    public bool IsHeader { get; }

    public bool IsSwatchVisible => !IsHeader;
}