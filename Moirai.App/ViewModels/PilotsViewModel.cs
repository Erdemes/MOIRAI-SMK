using MOIRAI.Domain;
using MOIRAI.Persistence;

namespace MOIRAI.ViewModels;

public sealed class PilotsViewModel : ResourceCrudViewModel<Pilot>
{
    public PilotsViewModel(IRepository repo) : base(repo) { }

    protected override IReadOnlyList<Pilot> Load() => Repo.GetPilots();
    protected override void Add(Pilot entity) => Repo.AddPilot(entity);
    protected override void Update(Pilot entity) => Repo.UpdatePilot(entity);
    protected override void Remove(string id) => Repo.DeletePilot(id);

    protected override bool IsAssignedTo(Campaign campaign, string id) =>
        campaign.AssignedPilotIds.Contains(id);
}
