using MOIRAI.Domain;

namespace MOIRAI.Scheduling;

internal readonly record struct PersonIndex(int Value);

internal sealed class CrewLedger
{
    private readonly ScenarioConfig _cfg;
    private readonly int[] _yearOfDay;
    private readonly int _nDays;

    private readonly double[][] _flightHrs;
    private readonly double[][] _dutyHrs;
    private readonly double[][] _flightDutyHrs;
    private readonly int[][] _sortieCnt;
    private readonly int[][] _rehCnt;

    private readonly bool[][] _leave;
    private readonly bool[,,] _block;

    public CrewLedger(IReadOnlyList<Resource> people, IReadOnlyList<DateOnly> days, int blocksPerDay, ScenarioConfig cfg)
    {
        _cfg = cfg;
        int persons = people.Count, nDays = days.Count;
        _nDays = nDays;
        _yearOfDay = new int[nDays];
        for (int d = 0; d < nDays; d++) _yearOfDay[d] = days[d].Year;

        _flightHrs = NewMatrix(persons, nDays);
        _dutyHrs = NewMatrix(persons, nDays);
        _flightDutyHrs = NewMatrix(persons, nDays);
        _sortieCnt = NewIntMatrix(persons, nDays);
        _rehCnt = NewIntMatrix(persons, nDays);

        _leave = new bool[persons][];
        for (int p = 0; p < persons; p++)
        {
            _leave[p] = new bool[nDays];
            for (int d = 0; d < nDays; d++) _leave[p][d] = people[p].IsOnLeave(days[d]);
        }
        _block = new bool[persons, blocksPerDay, nDays];
    }

    private static double[][] NewMatrix(int n, int m)
    {
        var a = new double[n][];
        for (int i = 0; i < n; i++) a[i] = new double[m];
        return a;
    }

    private static int[][] NewIntMatrix(int n, int m)
    {
        var a = new int[n][];
        for (int i = 0; i < n; i++) a[i] = new int[m];
        return a;
    }

    public bool IsOnLeave(PersonIndex p, int day) => _leave[p.Value][day];

    public void AddLeave(PersonIndex p, int fromDay, int toDay)
    {
        var row = _leave[p.Value];
        for (int d = Math.Max(0, fromDay); d <= toDay && d < row.Length; d++) row[d] = true;
    }
    public bool IsBlockBusy(PersonIndex p, int block0, int day) => _block[p.Value, block0, day];
    public void OccupyBlock(PersonIndex p, int block0, int day) => _block[p.Value, block0, day] = true;

    public void FreeBlock(PersonIndex p, int block0, int day) => _block[p.Value, block0, day] = false;

    public void AddSortie(IReadOnlyList<PersonIndex> crew, int day, double fH, double dH, double fdH)
    {
        foreach (var pi in crew)
        {
            int p = pi.Value;
            _sortieCnt[p][day]++;
            _flightHrs[p][day] += fH;
            _dutyHrs[p][day] += dH;
            _flightDutyHrs[p][day] += fdH;
        }
    }

    public void RemoveSortie(IReadOnlyList<PersonIndex> crew, int day, double fH, double dH, double fdH)
    {
        foreach (var pi in crew)
        {
            int p = pi.Value;
            _sortieCnt[p][day]--;
            _flightHrs[p][day] -= fH;
            _dutyHrs[p][day] -= dH;
            _flightDutyHrs[p][day] -= fdH;
        }
    }

    public void AddRehearsal(IReadOnlyList<PersonIndex> crew, int day, double rH)
    {
        foreach (var pi in crew)
        {
            _rehCnt[pi.Value][day]++;
            _dutyHrs[pi.Value][day] += rH;
        }
    }

    public void RemoveRehearsal(IReadOnlyList<PersonIndex> crew, int day, double rH)
    {
        foreach (var pi in crew)
        {
            _rehCnt[pi.Value][day]--;
            _dutyHrs[pi.Value][day] -= rH;
        }
    }

    public bool CanSimSession(PersonIndex pi, int day, double dH, ScenarioConfig? limits = null)
    {
        var cfg = limits ?? _cfg;
        int p = pi.Value;
        if (_dutyHrs[p][day] + dH > cfg.MaxDutyHoursPerDay) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 7, dH, cfg.MaxDutyHoursPer7Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 14, dH, cfg.MaxDutyHoursPer14Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 28, dH, cfg.MaxDutyHoursPer28Days)) return false;
        return RestOkAfterAdd(p, day, dH, cfg);
    }

    public void AddSimSession(IReadOnlyList<PersonIndex> crew, int day, double dH)
    {
        foreach (var pi in crew) _dutyHrs[pi.Value][day] += dH;
    }

    public void RemoveSimSession(IReadOnlyList<PersonIndex> crew, int day, double dH)
    {
        foreach (var pi in crew) _dutyHrs[pi.Value][day] -= dH;
    }

    public double RecentDuty(PersonIndex p, int day) =>
        WindowSum(_dutyHrs[p.Value], day, Math.Max(1, _cfg.LoadBalanceWindowDays));

    public bool CanSortie(PersonIndex pi, int day, double fH, double dH, double fdH, ScenarioConfig? limits = null)
    {
        var cfg = limits ?? _cfg;
        int p = pi.Value;
        if (_sortieCnt[p][day] >= cfg.MaxSortiesPerPersonPerDay) return false;
        if (ConsecutiveFlyingDays(p, day) >= cfg.MaxConsecutiveFlyingDays) return false;

        if (_flightHrs[p][day] + fH > cfg.MaxFlightHoursPerDay) return false;
        if (ExceedsWindow(_flightHrs[p], day, 7, fH, cfg.MaxFlightHoursPer7Days)) return false;
        if (ExceedsWindow(_flightHrs[p], day, 28, fH, cfg.MaxFlightHoursPer28Days)) return false;
        if (YearSum(_flightHrs[p], day) + fH > cfg.MaxFlightHoursPerYear) return false;

        if (_dutyHrs[p][day] + dH > cfg.MaxDutyHoursPerDay) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 7, dH, cfg.MaxDutyHoursPer7Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 14, dH, cfg.MaxDutyHoursPer14Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 28, dH, cfg.MaxDutyHoursPer28Days)) return false;

        if (!RestOkAfterAdd(p, day, dH, cfg)) return false;

        if (double.IsFinite(cfg.MaxFlightDutyHoursPerDay) &&
            _flightDutyHrs[p][day] + fdH > cfg.MaxFlightDutyHoursPerDay) return false;

        return true;
    }

    public string? ExplainSortie(PersonIndex pi, int day, double fH, double dH, double fdH, ScenarioConfig? limits = null)
    {
        var cfg = limits ?? _cfg;
        int p = pi.Value;
        if (_sortieCnt[p][day] >= cfg.MaxSortiesPerPersonPerDay)
            return $"sorties/day {_sortieCnt[p][day]}/{cfg.MaxSortiesPerPersonPerDay}";
        int streak = ConsecutiveFlyingDays(p, day);
        if (streak >= cfg.MaxConsecutiveFlyingDays)
            return $"consecutive days {streak}/{cfg.MaxConsecutiveFlyingDays}";

        string? Over(string what, double used, double add, double limit) =>
            used + add > limit + 1e-9 ? $"{what} {used:0.#}+{add:0.#} > {limit:0.#} h" : null;

        return Over("daily flight", _flightHrs[p][day], fH, cfg.MaxFlightHoursPerDay)
            ?? Over("7-day flight", WindowSum(_flightHrs[p], day, 7), fH, cfg.MaxFlightHoursPer7Days)
            ?? Over("28-day flight", WindowSum(_flightHrs[p], day, 28), fH, cfg.MaxFlightHoursPer28Days)
            ?? Over("yearly flight", YearSum(_flightHrs[p], day), fH, cfg.MaxFlightHoursPerYear)
            ?? Over("daily duty", _dutyHrs[p][day], dH, cfg.MaxDutyHoursPerDay)
            ?? Over("7-day duty", WindowSum(_dutyHrs[p], day, 7), dH, cfg.MaxDutyHoursPer7Days)
            ?? Over("14-day duty", WindowSum(_dutyHrs[p], day, 14), dH, cfg.MaxDutyHoursPer14Days)
            ?? Over("28-day duty", WindowSum(_dutyHrs[p], day, 28), dH, cfg.MaxDutyHoursPer28Days)
            ?? (day - 1 >= 0 && _dutyHrs[p][day - 1] > 0 && 24 - _dutyHrs[p][day - 1] < cfg.MinRestHours
                ? $"rest {24 - _dutyHrs[p][day - 1]:0.#} h < {cfg.MinRestHours:0.#} h"
                : null)
            ?? (double.IsFinite(cfg.MaxFlightDutyHoursPerDay)
                ? Over("daily flight-duty", _flightDutyHrs[p][day], fdH, cfg.MaxFlightDutyHoursPerDay)
                : null);
    }

    public double FlightHoursOn(PersonIndex p, int day) => _flightHrs[p.Value][day];

    public double DutyHoursOn(PersonIndex p, int day) => _dutyHrs[p.Value][day];

    public bool CanRehearse(PersonIndex pi, int day, double rH, ScenarioConfig? limits = null)
    {
        var cfg = limits ?? _cfg;
        int p = pi.Value;
        if (_rehCnt[p][day] >= cfg.MaxRehearsalsPerPersonPerDay) return false;

        if (_dutyHrs[p][day] + rH > cfg.MaxDutyHoursPerDay) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 7, rH, cfg.MaxDutyHoursPer7Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 14, rH, cfg.MaxDutyHoursPer14Days)) return false;
        if (ExceedsWindow(_dutyHrs[p], day, 28, rH, cfg.MaxDutyHoursPer28Days)) return false;

        return RestOkAfterAdd(p, day, rH, cfg);
    }

    private bool RestOkAfterAdd(int p, int day, double addDuty, ScenarioConfig cfg)
    {
        double newToday = _dutyHrs[p][day] + addDuty;
        if (day - 1 >= 0 && _dutyHrs[p][day - 1] > 0 && 24 - _dutyHrs[p][day - 1] < cfg.MinRestHours)
            return false;
        if (day + 1 < _nDays && _dutyHrs[p][day + 1] > 0 && 24 - newToday < cfg.MinRestHours)
            return false;
        return true;
    }

    public int ConsecutiveFlyingDays(PersonIndex p, int day) => ConsecutiveFlyingDays(p.Value, day);

    private int ConsecutiveFlyingDays(int p, int day)
    {
        int c = 0;
        for (int k = day - 1; k >= 0 && _flightHrs[p][k] > 0; k--) c++;
        return c;
    }

    private static double WindowSum(double[] arr, int day, int win)
    {
        double sum = 0;
        for (int k = Math.Max(0, day - win + 1); k <= day; k++) sum += arr[k];
        return sum;
    }

    private static bool ExceedsWindow(double[] hours, int day, int width, double added, double limit)
    {
        double sum = WindowSum(hours, day, width);
        for (int end = day; end < hours.Length && end < day + width; end++)
        {
            if (end > day)
            {
                sum += hours[end];
                if (end >= width) sum -= hours[end - width];
            }
            if (sum + added > limit + 1e-9) return true;
        }
        return false;
    }

    private double YearSum(double[] arr, int day)
    {
        double sum = 0;
        int year = _yearOfDay[day];
        for (int k = 0; k < arr.Length; k++)
            if (_yearOfDay[k] == year) sum += arr[k];
        return sum;
    }
}
