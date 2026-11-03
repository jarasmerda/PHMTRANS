namespace PHMTRANS.Pohoda;

public sealed class PohodaOptions
{
    public const string SectionName = "Pohoda";

    public string BaseUrl { get; set; } = "http://185.219.164.45:444";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Volitelný účet jen pro zápis. Když chybí, Live použije Username a Password.</summary>
    public string WriteUsername { get; set; } = "";

    public string WritePassword { get; set; } = "";

    /// <summary>Off frontu neodesílá, DryRun jen uloží XML, Live posílá do mServeru. Výchozí je Live.</summary>
    public string SyncMode { get; set; } = "Live";

    public string Ico { get; set; } = "";

    public string Application { get; set; } = "PHMTRANS";

    public string CodePrefix { get; set; } = "PE";

    public int CodeDigits { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 180;

    public string EffectiveSyncMode()
    {
        if (string.Equals(SyncMode, "Off", StringComparison.OrdinalIgnoreCase))
            return "Off";
        if (string.Equals(SyncMode, "DryRun", StringComparison.OrdinalIgnoreCase))
            return "DryRun";
        return "Live";
    }

    public (string Username, string Password) ResolveWriteCredentials()
    {
        if (!string.IsNullOrWhiteSpace(WriteUsername) && !string.IsNullOrWhiteSpace(WritePassword))
            return (WriteUsername.Trim(), WritePassword);
        return (Username.Trim(), Password);
    }
}
