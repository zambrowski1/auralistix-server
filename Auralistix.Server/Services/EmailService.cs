using System.Net;
using System.Net.Mail;
using System.Text;
using Auralistix.Server.Options;
using Microsoft.Extensions.Options;

namespace Auralistix.Server.Services;

public sealed record EmailDispatchInfo(string Mode, string? PreviewPath);

public interface IEmailSender
{
    Task<EmailDispatchInfo> SendConfirmationEmailAsync(
        string recipientEmail,
        string recipientName,
        string confirmationUrl,
        CancellationToken cancellationToken);
}

public sealed class EmailSender(
    IWebHostEnvironment environment,
    IOptions<SmtpOptions> smtpOptions,
    IOptions<CommunityOptions> communityOptions) : IEmailSender
{
    private readonly SmtpOptions _smtpOptions = smtpOptions.Value;
    private readonly CommunityOptions _communityOptions = communityOptions.Value;
    private readonly string _contentRootPath = environment.ContentRootPath;

    public async Task<EmailDispatchInfo> SendConfirmationEmailAsync(
        string recipientEmail,
        string recipientName,
        string confirmationUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_smtpOptions.Host))
            return await WritePreviewEmailAsync(recipientEmail, recipientName, confirmationUrl, cancellationToken);

        using var message = new MailMessage
        {
            From = new MailAddress(_smtpOptions.FromAddress, _smtpOptions.FromName),
            Subject = "Auralistix Community: confirm your email",
            IsBodyHtml = true,
            Body = BuildHtmlBody(recipientName, confirmationUrl),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        message.To.Add(recipientEmail);
        if (!string.IsNullOrWhiteSpace(_smtpOptions.ReplyToAddress))
            message.ReplyToList.Add(new MailAddress(_smtpOptions.ReplyToAddress, _smtpOptions.FromName));

        using var client = new SmtpClient(_smtpOptions.Host, _smtpOptions.Port)
        {
            EnableSsl = _smtpOptions.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(_smtpOptions.Username))
        {
            client.Credentials = new NetworkCredential(
                _smtpOptions.Username,
                _smtpOptions.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
        return new EmailDispatchInfo("smtp", null);
    }

    private async Task<EmailDispatchInfo> WritePreviewEmailAsync(
        string recipientEmail,
        string recipientName,
        string confirmationUrl,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(_contentRootPath, _communityOptions.StorageRoot, "outbox"));
        Directory.CreateDirectory(root);

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.txt";
        var fullPath = Path.Combine(root, fileName);
        var content = $"""
            To: {recipientEmail}
            Subject: Auralistix Community: confirm your email

            Hi {recipientName},

            Please confirm your email by opening this link:
            {confirmationUrl}

            This preview file was generated because SMTP is not configured.
            """;

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        return new EmailDispatchInfo("file-preview", fullPath);
    }

    private static string BuildHtmlBody(string recipientName, string confirmationUrl)
    {
        var safeName = WebUtility.HtmlEncode(recipientName);
        var safeUrl = WebUtility.HtmlEncode(confirmationUrl);
        return $"""
            <html>
            <body style="font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;padding:24px;">
                <div style="max-width:620px;margin:0 auto;background:#111827;border:1px solid #334155;border-radius:18px;padding:28px;">
                    <h1 style="margin:0 0 12px;font-size:24px;">Auralistix Community</h1>
                    <p style="margin:0 0 16px;">Hi {safeName}, confirm your email to activate your account.</p>
                    <a href="{safeUrl}"
                       style="display:inline-block;padding:12px 18px;background:#38bdf8;color:#082f49;text-decoration:none;border-radius:12px;font-weight:700;">
                        Confirm email
                    </a>
                    <p style="margin:16px 0 0;color:#94a3b8;">If the button does not work, copy this link into your browser:</p>
                    <p style="word-break:break-all;color:#e2e8f0;">{safeUrl}</p>
                </div>
            </body>
            </html>
            """;
    }
}
