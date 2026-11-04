namespace PHMTRANS;

public static class ValueSelection
{
    public const string AllToken = "*";

    public static (IReadOnlyList<string> Values, bool ExplicitAll, string? Error) Parse(
        IEnumerable<string?>? raw,
        IReadOnlyList<string>? known,
        string unknownMessage)
    {
        var tokens = new List<string>();
        foreach (var item in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            // Čárka je oddělovač jen u uzavřeného seznamu. Název odběratele ji může obsahovat.
            var parts = known is null
                ? [item.Trim()]
                : item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
            {
                if (part.Length == 0 || tokens.Contains(part))
                    continue;
                tokens.Add(part);
            }
        }

        if (tokens.Count == 0)
            return ([], false, null);

        if (tokens.Contains(AllToken) || tokens.Contains("Vše"))
            return ([], true, null);

        if (known is not null)
        {
            foreach (var token in tokens)
            {
                if (!known.Contains(token))
                    return ([], false, unknownMessage);
            }
        }

        return (tokens, false, null);
    }
}
