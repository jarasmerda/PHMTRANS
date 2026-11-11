namespace PHMTRANS.Pohoda;

public sealed class PohodaOptions
{
    public const string SectionName = "Pohoda";

    /// <summary>mServer na SRV-POHODA ve vnitřní síti. Port 444 nemá být z internetu dostupný.</summary>
    public string BaseUrl { get; set; } = "http://192.168.1.54:444";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Volitelný účet jen pro zápis. Když chybí, Live použije Username a Password.</summary>
    public string WriteUsername { get; set; } = "";

    public string WritePassword { get; set; } = "";

    /// <summary>Off frontu neodesílá, DryRun jen uloží XML, Live posílá do mServeru. Výchozí je Live.</summary>
    public string SyncMode { get; set; } = "Live";

    public string Ico { get; set; } = "";

    public string Application { get; set; } = "PHMTRANS";

    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Firemní databáze Pohody s tabulkou dbo.AD. Čte se trojdílným jménem [databáze].dbo.AD, jen SELECT.
    /// Dispečink, CRM a přihlášení zůstávají v databázi connection stringu (Smerco_26950430_2026).
    /// </summary>
    public string AddressBookDatabase { get; set; } = "StwPh_26950430_2026";

    /// <summary>
    /// Sloupec dbo.AD s marží zákazníka v procentech (Pohoda: Cena → Výchozí nastavení → Marže).
    /// Dispečink z ní počítá Prodej Kč/km = Náklad Kč/km × (1 + marže / 100). Prázdné = nepoužívá se.
    /// </summary>
    public string CustomerMarginColumn { get; set; } = "";

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
