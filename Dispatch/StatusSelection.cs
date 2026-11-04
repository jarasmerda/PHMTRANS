using PHMTRANS;

namespace PHMTRANS.Dispatch;

public static class StatusSelection
{
    public static (IReadOnlyList<string> Values, string? Error) Parse(
        IEnumerable<string?>? raw,
        IReadOnlyList<string> known,
        string unknownMessage)
    {
        var parsed = ValueSelection.Parse(raw, known, unknownMessage);
        if (parsed.Error is not null || parsed.ExplicitAll)
            return ([], parsed.Error);

        return (parsed.Values, null);
    }

    /// <summary>
    /// Prázdný dotaz znamená všechny stavy kromě <paramref name="excludedByDefault"/>.
    /// Hvězdička nebo „Vše“ znamená úplně všechno a po obnovení stránky se nesmí ztratit.
    /// </summary>
    public static (IReadOnlyList<string> Values, bool Restrict, string? Error) ParseList(
        IEnumerable<string?>? raw,
        IReadOnlyList<string> known,
        string unknownMessage,
        string excludedByDefault)
    {
        var parsed = ValueSelection.Parse(raw, known, unknownMessage);
        if (parsed.Error is not null)
            return ([], false, parsed.Error);
        if (parsed.ExplicitAll)
            return ([], false, null);
        if (parsed.Values.Count == 0)
            return (known.Where(value => value != excludedByDefault).ToArray(), true, null);

        return (parsed.Values, true, null);
    }
}
