namespace Auralistix.Server.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "Auralistix.Server";
    public string Audience { get; set; } = "Auralistix.Desktop";
    public string SigningKey { get; set; } = "Auralistix.Local.Development.Signing.Key.2026.Change.Me";
    public int ExpirationDays { get; set; } = 30;
}
