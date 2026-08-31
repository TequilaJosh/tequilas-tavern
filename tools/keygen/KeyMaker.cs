using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TequilasTavern.Keygen;

/// <summary>License signing / verification. Mirrors the app's LicenseService format.</summary>
public static class KeyMaker
{
    public const string Prefix = "TT1";   // must match LicenseService.KeyPrefix in the app

    /// <summary>Create a new RSA keypair. Returns (privateXml, publicXml).</summary>
    public static (string priv, string pub) GenerateKeypair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ToXmlString(true), rsa.ToXmlString(false));
    }

    /// <summary>Mint a signed license token from the private key XML.</summary>
    public static string Issue(string privateXml, string name, string email, string order,
        int days, int max)
    {
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? exp = days > 0 ? DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds() : null;
        if (max < 1) max = 1;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload
        {
            n = name, e = email, o = order, iat = iat, exp = exp, max = max,
        });

        using var rsa = RSA.Create();
        rsa.FromXmlString(privateXml);
        var sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{Prefix}.{B64Url(payload)}.{B64Url(sig)}";
    }

    /// <summary>Verify a token against the public key XML; returns the decoded payload JSON.</summary>
    public static bool Verify(string publicXml, string token, out string payloadJson)
    {
        payloadJson = "";
        try
        {
            var parts = token.Trim().Split('.');
            if (parts.Length != 3 || parts[0] != Prefix) return false;
            var payload = FromB64Url(parts[1]);
            var sig = FromB64Url(parts[2]);
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicXml);
            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return false;
            payloadJson = Encoding.UTF8.GetString(payload);
            return true;
        }
        catch { return false; }
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    private sealed class Payload
    {
        public string n { get; set; } = "";
        public string e { get; set; } = "";
        public string o { get; set; } = "";
        public long iat { get; set; }
        public long? exp { get; set; }
        public int max { get; set; } = 2;
    }
}
