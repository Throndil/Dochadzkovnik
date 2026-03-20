using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;


namespace API.Services;



public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendPasswordResetAsync(string toEmail, string toName, string resetLink)
    {
        var host = _config["Email:Host"];
        if (string.IsNullOrEmpty(host))
        {
            _logger.LogWarning("Email not configured – password reset link: {Link}", resetLink);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            _config["Email:FromName"] ?? "Dochadzkovnik",
            _config["Email:From"] ?? _config["Email:Username"]!));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = "Obnovenie hesla – Dochadzkovnik";

        message.Body = new TextPart("html")
        {
            Text = $"""
                <p>Dobrý deň, {toName}.</p>
                <p>Dostali sme žiadosť o obnovenie hesla pre váš administrátorský účet.</p>
                <p>
                  <a href="{resetLink}"
                     style="background:#f59e0b;color:#fff;padding:10px 20px;border-radius:6px;text-decoration:none;font-weight:bold;">
                    Obnoviť heslo
                  </a>
                </p>
                <p>Odkaz je platný 24 hodín. Ak ste o obnovenie hesla nežiadali, tento e-mail ignorujte.</p>
                """
        };

        using var smtp = new SmtpClient();
        var port = int.TryParse(_config["Email:Port"], out var p) ? p : 587;
        await smtp.ConnectAsync(host, port, SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(_config["Email:Username"], _config["Email:Password"]);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }
}
