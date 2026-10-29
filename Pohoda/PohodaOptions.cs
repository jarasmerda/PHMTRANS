namespace PHMTRANS.Pohoda;

public sealed class PohodaOptions
{
    public const string SectionName = "Pohoda";

    public string BaseUrl { get; set; } = "http://185.219.164.45:444";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Ico { get; set; } = "";

    public string Application { get; set; } = "PHMTRANS";

    public string CodePrefix { get; set; } = "PE";

    public int CodeDigits { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 180;
}
