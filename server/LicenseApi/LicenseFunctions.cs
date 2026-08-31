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
/// Two anonymous HTTPS endpoints the desktop app calls:
///   POST /api/activate  { key, hwid, machine }  -> registers/binds a machine, enforces the limit
///   POST /api/validate  { key, hwid }           -> quick "still valid / revoked?" recheck
/// The RSA signature is the first gate (forged keys never touch the DB); the DB enforces
/// activation limits and revocation. Config comes from app settings:
///   SqlConnectionString   – Azure SQL connection string (server-side only)
///   LicensePublicKeyXml   – the same public key embedded in the app (Assets/license_public.xml)
/// </summary>
public class LicenseFunctions
{
    private const string KeyPrefix = "TT1";
    private readonly ILogger _log;
    private readonly string _conn;
    private readonly string _publicKeyXml;

    public LicenseFunctions(ILoggerFactory lf)
    {
        _log = lf.CreateLogger<LicenseFunctions>();
        _conn = Environment.GetEnvironmentVariable("SqlConnectionString") ?? "";
        _publicKeyXml = Environment.GetEnvironmentVariable("LicensePublicKeyXml") ?? "";
    }

    [Function("activate")]
    public async Task<IActionResult> Activate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "activate")] HttpRequest req)
    {
        var (key, hwid, machine) = await ReadBody(req);
        if (key.Length == 0 || hwid.Length == 0)
            return new BadRequestObjectResult(new { ok = false, reason = "missing key or hardware id" });

        if (!TryVerify(key, out var lic))
            return new ObjectResult(new { ok = false, reason = "invalid" }) { StatusCode = 403 };

        if (lic!.Expires is { } exp && exp < DateTimeOffset.UtcNow)
            return new ObjectResult(new { ok = false, reason = "expired" }) { StatusCode = 403 };

        var keyId = Sha256Hex(key);

        try
        {
            await using var cn = new SqlConnection(_conn);
            await cn.OpenAsync();
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

            // Self-register the key on first sighting.
            await Exec(cn, tx,
                @"IF NOT EXISTS (SELECT 1 FROM dbo.Licenses WHERE KeyId=@k)
                  INSERT INTO dbo.Licenses (KeyId,Name,Email,OrderRef,MaxActivations,IssuedUtc)
                  VALUES (@k,@n,@e,@o,@max,@iat);",
                ("@k", keyId), ("@n", lic.Name), ("@e", lic.Email), ("@o", lic.Order),
                ("@max", lic.Max), ("@iat", lic.Issued.UtcDateTime));

            // Read state under a lock so concurrent activations can't both slip past the cap.
            bool revoked; int max;
            await using (var rc = await Query(cn, tx,
                "SELECT IsRevoked, MaxActivations FROM dbo.Licenses WITH (UPDLOCK, HOLDLOCK) WHERE KeyId=@k",
                ("@k", keyId)))
            {
                if (!await rc.ReadAsync())
                    return new ObjectResult(new { ok = false, reason = "invalid" }) { StatusCode = 403 };
                revoked = rc.GetBoolean(0);
                max = rc.GetInt32(1);
            }

            if (revoked)
            {
                await tx.CommitAsync();
                return new ObjectResult(new { ok = false, reason = "revoked" }) { StatusCode = 403 };
            }

            int total, mine;
            await using (var cc = await Query(cn, tx,
                @"SELECT COUNT(*) , SUM(CASE WHEN HardwareId=@h THEN 1 ELSE 0 END)
                  FROM dbo.Activations WHERE KeyId=@k", ("@k", keyId), ("@h", hwid)))
            {
                await cc.ReadAsync();
                total = cc.GetInt32(0);
                mine = cc.IsDBNull(1) ? 0 : cc.GetInt32(1);
            }

            if (mine > 0)
            {
                await Exec(cn, tx,
                    "UPDATE dbo.Activations SET LastSeenUtc=SYSUTCDATETIME(), MachineName=@m WHERE KeyId=@k AND HardwareId=@h",
                    ("@k", keyId), ("@h", hwid), ("@m", machine));
            }
            else if (total >= max)
            {
                await tx.CommitAsync();
                return new ObjectResult(new { ok = false, reason = "limit", max }) { StatusCode = 409 };
            }
            else
            {
                await Exec(cn, tx,
                    "INSERT INTO dbo.Activations (KeyId,HardwareId,MachineName) VALUES (@k,@h,@m)",
                    ("@k", keyId), ("@h", hwid), ("@m", machine));
            }

            await tx.CommitAsync();
            return new OkObjectResult(new
            {
                ok = true,
                name = lic.Name,
                email = lic.Email,
                expires = lic.Expires?.ToUnixTimeSeconds(),
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "activate failed");
            return new ObjectResult(new { ok = false, reason = "server" }) { StatusCode = 500 };
        }
    }

    [Function("validate")]
    public async Task<IActionResult> Validate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "validate")] HttpRequest req)
    {
        var (key, hwid, _) = await ReadBody(req);
        if (key.Length == 0 || !TryVerify(key, out var lic))
            return new ObjectResult(new { ok = false, reason = "invalid" }) { StatusCode = 403 };
        if (lic!.Expires is { } exp && exp < DateTimeOffset.UtcNow)
            return new ObjectResult(new { ok = false, reason = "expired" }) { StatusCode = 403 };

        var keyId = Sha256Hex(key);
        try
        {
            await using var cn = new SqlConnection(_conn);
            await cn.OpenAsync();

            bool revoked = false, known = false, boundHere = false;
            await using (var rc = await Query(cn, null,
                @"SELECT l.IsRevoked,
                         CASE WHEN a.Id IS NULL THEN 0 ELSE 1 END AS BoundHere
                  FROM dbo.Licenses l
                  LEFT JOIN dbo.Activations a ON a.KeyId=l.KeyId AND a.HardwareId=@h
                  WHERE l.KeyId=@k", ("@k", keyId), ("@h", hwid)))
            {
                if (await rc.ReadAsync())
                {
                    known = true;
                    revoked = rc.GetBoolean(0);
                    boundHere = rc.GetInt32(1) == 1;
                }
            }

            if (known && boundHere)
                await Exec(cn, null,
                    "UPDATE dbo.Activations SET LastSeenUtc=SYSUTCDATETIME() WHERE KeyId=@k AND HardwareId=@h",
                    ("@k", keyId), ("@h", hwid));

            return new OkObjectResult(new { ok = known && !revoked && boundHere, revoked });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "validate failed");
            return new ObjectResult(new { ok = false, reason = "server" }) { StatusCode = 500 };
        }
    }

    // ---- request body ----
    private static async Task<(string key, string hwid, string machine)> ReadBody(HttpRequest req)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var r = doc.RootElement;
            string Get(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            return (Get("key").Trim(), Get("hwid").Trim(), Get("machine").Trim());
        }
        catch { return ("", "", ""); }
    }

    // ---- signature verification (mirrors the app & keygen) ----
    private sealed record Lic(string Name, string Email, string Order, int Max,
                              DateTimeOffset Issued, DateTimeOffset? Expires);

    private bool TryVerify(string key, out Lic? lic)
    {
        lic = null;
        try
        {
            var parts = key.Split('.');
            if (parts.Length != 3 || parts[0] != KeyPrefix) return false;
            var payload = FromB64Url(parts[1]);
            var sig = FromB64Url(parts[2]);

            using var rsa = RSA.Create();
            rsa.FromXmlString(_publicKeyXml);
            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return false;

            using var doc = JsonDocument.Parse(payload);
            var o = doc.RootElement;
            string S(string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long L(string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            long? N(string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            int max = o.TryGetProperty("max", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetInt32() : 2;

            var exp = N("exp");
            lic = new Lic(S("n"), S("e"), S("o"), max <= 0 ? 2 : max,
                DateTimeOffset.FromUnixTimeSeconds(L("iat")),
                exp.HasValue ? DateTimeOffset.FromUnixTimeSeconds(exp.Value) : null);
            return true;
        }
        catch { return false; }
    }

    // ---- small SQL helpers ----
    private static async Task Exec(SqlConnection cn, SqlTransaction? tx, string sql,
        params (string, object?)[] ps)
    {
        await using var cmd = new SqlCommand(sql, cn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<SqlDataReader> Query(SqlConnection cn, SqlTransaction? tx, string sql,
        params (string, object?)[] ps)
    {
        var cmd = new SqlCommand(sql, cn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteReaderAsync();
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
