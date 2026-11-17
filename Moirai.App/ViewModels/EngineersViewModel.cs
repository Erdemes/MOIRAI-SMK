using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.ViewModels;

public sealed class EngineersViewModel : ResourceCrudViewModel<Engineer>
{
    public EngineersViewModel(IRepository repo) : base(repo) { }

    protected override IReadOnlyList<Engineer> Load() => Repo.GetEngineers();
    protected override void Add(Engineer entity) => Repo.AddEngineer(entity);
    protected override void Update(Engineer entity) => Repo.UpdateEngineer(entity);
    protected override void Remove(string id) => Repo.DeleteEngineer(id);

    protected override bool IsAssignedTo(Campaign campaign, string id) =>
        campaign.AssignedEngineerIds.Contains(id);
}
