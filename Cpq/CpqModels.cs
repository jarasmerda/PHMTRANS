namespace PHMTRANS.Cpq;

public sealed record CpqSelection(Dictionary<int, string> Attributes);

public sealed record AttrValue(string Value);

public sealed record NextCodeResponse(string Code);

public sealed record PohodaCheckResponse(bool Exists, string Message, string? Code = null);

public sealed record ConfigResponse(string ApiBase);
