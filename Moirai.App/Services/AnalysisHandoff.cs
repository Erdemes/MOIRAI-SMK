namespace MOIRAI.Services;

public sealed class AnalysisHandoff
{
    public event Action? AnalysisRequested;

    public void Request() => AnalysisRequested?.Invoke();
}
