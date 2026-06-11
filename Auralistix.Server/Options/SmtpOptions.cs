namespace Auralistix.Server.Options;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "noreply@auralistix.local";
    public string FromName { get; set; } = "Auralistix Community";
    public string ReplyToAddress { get; set; } = string.Empty;
}
