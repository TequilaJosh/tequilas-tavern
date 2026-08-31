using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace TequilasTavern.LicenseApi;

/// <summary>
/// Automated issuance: mints a signed key server-side, records it, and emails it to the buyer.
/// Call this from your Etsy "new paid order" automation (Zapier / Make / a poller):
///
///   POST /api/issue
///   Header: x-admin-secret: &lt;IssueSecret&gt;
///   Body:   { "name": "...", "email": "buyer@x.com", "order": "1234", "max": 2, "days": 0 }
///
/// Required app settings (Key Vault references recommended for the secrets):
///   IssueSecret            – shared secret your automation sends in the x-admin-secret header
///   LicensePrivateKeyXml   – the PRIVATE signing key (mirrors tools/keygen/license_private.xml)
///   SqlConnectionString    – (already set) Azure SQL
///   Smtp__Host, Smtp__Port, Smtp__User, Smtp__Pass, Smtp__From, Smtp__FromName
///                          – your email provider (SendGrid / Gmail app password / Azure ACS SMTP)
/// </summary>
public class IssueFunction
{
    private const string KeyPrefix = "TT1";
    private readonly ILogger _log;

    public IssueFunction(ILoggerFactory lf) => _log = lf.CreateLogger<IssueFunction>();

    [Function("issue")]
    public async Task<IActionResult> Issue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "issue")] HttpRequest req)
    {
        // --- auth: shared secret in a header (constant-time compare) ---
        var expected = Environment.GetEnvironmentVariable("IssueSecret") ?? "";
        req.Headers.TryGetValue("x-admin-secret", out var provided);
        if (expected.Length == 0 || !FixedEquals(expected, provided.ToString()))
            return new UnauthorizedResult();

        var privateXml = Environment.GetEnvironmentVariable("LicensePrivateKeyXml") ?? "";
        var conn = Environment.GetEnvironmentVariable("SqlConnectionString") ?? "";
        if (privateXml.Length == 0 || conn.Length == 0)
            return new ObjectResult(new { ok = false, reason = "server not configured" }) { StatusCode = 500 };

        // --- read order details ---
        string name, email, order; int max, days;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var r = doc.RootElement;
            string S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string p, int def) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
            name = S("name").Trim(); email = S("email").Trim(); order = S("order").Trim();
            max = I("max", 2); days = I("days", 0);
        }
        catch { return new BadRequestObjectResult(new { ok = false, reason = "bad body" }); }

        if (email.Length == 0)
            return new BadRequestObjectResult(new { ok = false, reason = "missing email" });
        if (max < 1) max = 1;

        // --- mint the signed key ---
        var key = Sign(privateXml, name, email, order, days, max);
        var keyId = Sha256Hex(key);

        // --- pre-register in the DB so it's tracked immediately (idempotent per order) ---
        try
        {
            await using var cn = new SqlConnection(conn);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.Licenses WHERE KeyId=@k)
                  INSERT INTO dbo.Licenses (KeyId,Name,Email,OrderRef,MaxActivations,IssuedUtc)
                  VALUES (@k,@n,@e,@o,@max,SYSUTCDATETIME());", cn);
            cmd.Parameters.AddWithValue("@k", keyId);
            cmd.Parameters.AddWithValue("@n", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@e", (object?)email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@o", (object?)order ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@max", max);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "issue: DB insert failed");
            return new ObjectResult(new { ok = false, reason = "db" }) { StatusCode = 500 };
        }

        // --- email the buyer ---
        string? emailError = null;
        try { await SendEmail(email, name, key, max, days); }
        catch (Exception ex) { emailError = ex.Message; _log.LogError(ex, "issue: email failed"); }

        // Return the key too, so the automation can log it / retry email if needed.
        return new OkObjectResult(new { ok = true, key, emailed = emailError == null, emailError });
    }

    // ---- email ----
    private static async Task SendEmail(string to, string name, string key, int max, int days)
    {
        var host = Environment.GetEnvironmentVariable("Smtp__Host") ?? "";
        int port = int.TryParse(Environment.GetEnvironmentVariable("Smtp__Port"), out var p) ? p : 587;
        var user = Environment.GetEnvironmentVariable("Smtp__User") ?? "";
        var pass = Environment.GetEnvironmentVariable("Smtp__Pass") ?? "";
        var from = Environment.GetEnvironmentVariable("Smtp__From") ?? user;
        var fromName = Environment.GetEnvironmentVariable("Smtp__FromName") ?? "Tequilas' Tavern";
        if (host.Length == 0 || from.Length == 0)
            throw new InvalidOperationException("SMTP not configured (Smtp__Host / Smtp__From).");

        var greeting = string.IsNullOrWhiteSpace(name) ? "Hi," : $"Hi {name},";
        var limit = days > 0 ? $"valid for {days} days" : "a perpetual license";
        var body =
            $"{greeting}\n\n" +
            "Thanks for buying Tequilas' Tavern! Here is your license key:\n\n" +
            $"    {key}\n\n" +
            $"It's {limit} and can be activated on up to {max} device(s).\n\n" +
            "To activate: open the app, paste the key into the activation window, and click Activate.\n\n" +
            "Keep this email — you'll need the key if you reinstall.\n\n" +
            "Enjoy!\n";

        using var msg = new MailMessage
        {
            From = new MailAddress(from, fromName),
            Subject = "Your Tequilas' Tavern license key",
            Body = body,
        };
        msg.To.Add(to);

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = user.Length > 0 ? new NetworkCredential(user, pass) : null,
        };
        await client.SendMailAsync(msg);
    }

    // ---- signing (mirrors the keygen & the app's verifier) ----
    private static string Sign(string privateXml, string name, string email, string order, int days, int max)
    {
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? exp = days > 0 ? DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds() : null;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            n = name, e = email, o = order, iat, exp, max,
        });
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateXml);
        var sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{KeyPrefix}.{B64Url(payload)}.{B64Url(sig)}";
    }

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
