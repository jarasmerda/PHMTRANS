namespace PHMTRANS.Dispatch;

public static class StatusSelection
{
    public static (IReadOnlyList<string> Values, string? Error) Parse(
        IEnumerable<string?>? raw,
        IReadOnlyList<string> known,
        string unknownMessage)
    {
        var values = new List<string>();
        foreach (var item in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            foreach (var part in item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!known.Contains(part))
                    return ([], unknownMessage);
                if (!values.Contains(part))
                    values.Add(part);
            }
        }

        return (values, null);
    }
}
