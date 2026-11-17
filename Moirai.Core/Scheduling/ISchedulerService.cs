namespace MOIRAI.Scheduling;

public interface ISchedulerService
{
    Schedule Run(Scenario scenario);

    Schedule Run(Scenario scenario, ISortieYieldModel yieldModel);

    Schedule Run(Scenario scenario, ISortieYieldModel yieldModel, IFailureModel failureModel);
}
