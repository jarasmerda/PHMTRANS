namespace PHMTRANS.Dispatch;

public static class KmSources
{
    public const string Matrix = "Matrix";
    public const string Manual = "Manual";

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Trim().Equals(Matrix, StringComparison.OrdinalIgnoreCase))
            return Matrix;
        if (value.Trim().Equals(Manual, StringComparison.OrdinalIgnoreCase))
            return Manual;
        return null;
    }
}

public static class PlacePair
{
    public const int MaxLength = 160;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var collapsed = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToLowerInvariant();
    }

    public static (string From, string To)? Canonical(string? left, string? right)
    {
        var from = Normalize(left);
        var to = Normalize(right);
        if (from.Length == 0 || to.Length == 0)
            return null;
        if (from.Length > MaxLength || to.Length > MaxLength)
            return null;

        return string.CompareOrdinal(from, to) <= 0 ? (from, to) : (to, from);
    }

    public static string Key(string from, string to) => from + "\u001f" + to;
}

public sealed record DistanceQuery(string? From, string? To);

public sealed record DistanceHit(string From, string To, decimal? Km);

public sealed record DistanceWrite(string FromPlace, string ToPlace, decimal Km);

public readonly record struct DistanceDecision(decimal? Kilometers, string? Source, bool Upsert);

public static class DistanceChoice
{
    public static DistanceDecision Resolve(decimal? kilometers, string? requestedSource, decimal? known)
    {
        if (kilometers is null)
            return new(null, null, false);

        var requested = KmSources.Normalize(requestedSource);
        if (requested == KmSources.Matrix && known is not null)
            return new(known, KmSources.Matrix, false);

        if (requested == KmSources.Manual || known is null || known.Value != kilometers.Value)
            return new(kilometers, KmSources.Manual, true);

        return new(kilometers, KmSources.Matrix, false);
    }
}

public sealed record DistanceEntry(string FromPlace, string ToPlace, decimal Km, DateTimeOffset UpdatedAt, string? UpdatedBy);

public sealed class DistanceBook
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DistanceEntry> _rows = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, decimal> Map()
    {
        lock (_gate)
            return _rows.ToDictionary(pair => pair.Key, pair => pair.Value.Km, StringComparer.Ordinal);
    }

    public DistanceEntry? Find(string? from, string? to)
    {
        var canonical = PlacePair.Canonical(from, to);
        if (canonical is null)
            return null;

        lock (_gate)
            return _rows.TryGetValue(PlacePair.Key(canonical.Value.From, canonical.Value.To), out var entry) ? entry : null;
    }

    public IReadOnlyList<DistanceHit> Lookup(IReadOnlyList<DistanceQuery>? pairs)
    {
        if (pairs is null || pairs.Count == 0)
            return [];

        lock (_gate)
        {
            var hits = new List<DistanceHit>(pairs.Count);
            foreach (var pair in pairs)
            {
                var from = pair.From?.Trim() ?? "";
                var to = pair.To?.Trim() ?? "";
                var canonical = PlacePair.Canonical(from, to);
                decimal? km = null;
                if (canonical is not null && _rows.TryGetValue(PlacePair.Key(canonical.Value.From, canonical.Value.To), out var found))
                    km = found.Km;
                hits.Add(new DistanceHit(from, to, km));
            }

            return hits;
        }
    }

    public void Apply(IReadOnlyList<DistanceWrite>? writes, DateTimeOffset updatedAt, string? updatedBy)
    {
        if (writes is null || writes.Count == 0)
            return;

        lock (_gate)
        {
            foreach (var write in writes)
            {
                var canonical = PlacePair.Canonical(write.FromPlace, write.ToPlace);
                if (canonical is null)
                    continue;
                _rows[PlacePair.Key(canonical.Value.From, canonical.Value.To)] = new DistanceEntry(
                    canonical.Value.From,
                    canonical.Value.To,
                    write.Km,
                    updatedAt,
                    string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim());
            }
        }
    }
}
