using System.Globalization;

namespace MOIRAI.ViewModels;

public static class HolidayList
{
    public static string Format(IEnumerable<DateOnly> dates) =>
        string.Join(", ", dates.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")));

    public static HashSet<DateOnly> Parse(string raw)
    {
        var set = new HashSet<DateOnly>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (DateOnly.TryParse(part, out var d))
                set.Add(d);
        return set;
    }

    public static (string Merged, int Found) MergeCsv(string existing, string csvText)
    {
        var dates = Parse(existing);
        int found = 0;
        var tokens = csvText.Split(new[] { ',', ';', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
            if (TryParseDate(token.Trim('"'), out var d))
            {
                dates.Add(d);
                found++;
            }
        return (Format(dates), found);
    }

    private static bool TryParseDate(string s, out DateOnly date)
    {
        string[] formats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy" };
        foreach (var f in formats)
            if (DateOnly.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;
        return DateOnly.TryParse(s, CultureInfo.InvariantCulture, out date);
    }
}
