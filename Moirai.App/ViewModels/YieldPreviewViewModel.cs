using MOIRAI.Domain;

namespace MOIRAI.ViewModels;

public sealed class YieldPreviewViewModel
{
    public required Campaign Campaign { get; init; }

    public string Title => $"Test efficiency preview · {Campaign.Name}";

    public string Summary
    {
        get
        {
            int raw = Campaign.RequiredSorties();
            double inflation = Campaign.ExpectedInflation();
            int expected = Campaign.TestPointsPerSortie <= 0
                ? 0
                : (int)Math.Ceiling(Campaign.TotalTestPoints * inflation / Campaign.TestPointsPerSortie);
            return $"{Campaign.TotalTestPoints:0.##} test points · {Campaign.TestPointsPerSortie:0.##}/sortie · " +
                   $"raw plan {raw} sorties · with efficiency losses ~{expected} sorties (×{inflation:0.##})";
        }
    }

    public string Formula =>
        "p(retest) = floor + (start − floor) · " +
        (Campaign.RetestCurve == MOIRAI.Domain.RetestModel.PowerLaw
            ? "(1 + CL1 Test points / scale)^(−β)"
            : "e^(−CL1 Test points / scale)") +
        "        expected workload ×  =  1 / ((1 − p̄) · (1 − abort))";
}
