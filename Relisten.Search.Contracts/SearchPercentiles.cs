namespace Relisten.Search;

/// <summary>Accumulates the distribution of an index build without retaining its documents.</summary>
public sealed class SearchPercentiles
{
    private readonly Dictionary<string, Dictionary<double, long>> _histograms = [];

    public void Observe(string kind, double value)
    {
        if (!_histograms.TryGetValue(kind, out var counts))
        {
            counts = [];
            _histograms.Add(kind, counts);
        }

        counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    public Dictionary<string, Dictionary<double, double>> Calculate() =>
        _histograms.ToDictionary(pair => pair.Key, pair => Calculate(pair.Value));

    private static Dictionary<double, double> Calculate(Dictionary<double, long> counts)
    {
        var total = counts.Values.Sum();
        var result = new Dictionary<double, double>();
        long before = 0;
        foreach (var (value, count) in counts.OrderBy(pair => pair.Key))
        {
            // Identical positive counts share midpoint rank. Zero is no signal.
            result[value] = value <= 0 ? 0 : (before + count / 2d) / total;
            before += count;
        }
        return result;
    }
}
