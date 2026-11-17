using System.Collections.ObjectModel;
using MOIRAI.Persistence;
using MOIRAI.ViewModels;

namespace MOIRAI.Services;

public sealed class ScenarioListStore
{
    public ObservableCollection<ScenarioItem> Items { get; } = new();

    public void Rebuild(IRepository repo)
    {
        Items.Clear();
        var all = repo.GetScenarios();
        var byParent = all
            .Where(s => !string.IsNullOrEmpty(s.ParentScenarioId))
            .ToLookup(s => s.ParentScenarioId!);
        var seen = new HashSet<string>();

        void Add(Scheduling.Scenario sc, int depth)
        {
            if (!seen.Add(sc.Id)) return;
            Items.Add(new ScenarioItem(sc, depth));
            foreach (var child in byParent[sc.Id]) Add(child, depth + 1);
        }

        foreach (var sc in all)
            if (string.IsNullOrEmpty(sc.ParentScenarioId) || all.All(p => p.Id != sc.ParentScenarioId))
                Add(sc, 0);
    }

    public void RefreshStatus(string scenarioId) =>
        Items.FirstOrDefault(i => i.Model.Id == scenarioId)?.RefreshStatus();

    public event Action? ScenariosChanged;

    public void NotifyScenariosChanged() => ScenariosChanged?.Invoke();
}
