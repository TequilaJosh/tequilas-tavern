using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameTracker.Services
{
    /// <summary>
    /// Offline license gate. Each key is a payload (buyer name/email/order + optional
    /// expiry) signed with the developer's private RSA key; the app verifies it with the
    /// embedded public key, so keys can't be forged or generated without that private key.
    ///
    /// On activation the key is bound to this machine (a hardware fingerprint) and the
    /// record is stored with per-user DPAPI encryption — so an activated install can't be
    /// copied to another machine, and the stored record can't be lifted to another account.
    /// A determined attacker can still defeat any purely-offline scheme; this is a
    /// deterrent against casual copying, not a guarantee.
    /// </summary>
    public static class LicenseService
    {
        public const string KeyPrefix = "TT1";   // must match the keygen tool's Prefix

        // Your deployed license API (Azure Function), e.g. "https://tt-license.azurewebsites.net/api".
        // Leave EMPTY to run fully offline (signature + machine binding only, no server checks).
        // When set, activation is enforced online (activation limits + revocation); launches still
        // work offline afterward using the stored, machine-bound record.
        public const string ApiBaseUrl = "";

        private static bool OnlineEnabled =>
            ApiBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tequilas Tavern");
        private static readonly string LicenseFile = Path.Combine(Folder, "license.dat");

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TequilasTavern/license/v1");

        public sealed class LicenseInfo
        {
            public string Name { get; init; } = "";
            public string Email { get; init; } = "";
            public string Order { get; init; } = "";
            public int Max { get; init; } = 2;
            public DateTimeOffset Issued { get; init; }
            public DateTimeOffset? Expires { get; init; }
        }

        /// <summary>True if a valid, machine-matched, unexpired activation is stored.</summary>
        public static bool IsActivated(out LicenseInfo? info)
        {
            info = null;
            try
            {
                if (!File.Exists(LicenseFile)) return false;
                var record = Unprotect(File.ReadAllBytes(LicenseFile));
                if (record == null) return false;   // wrong user/machine, or corrupt

                var o = JObject.Parse(record);
                var token = (string?)o["token"];
                var boundHw = (string?)o["hwid"];
                if (string.IsNullOrEmpty(token) || boundHw != HardwareId())
                    return false;

                return Verify(token, out info);      // re-check signature + expiry every launch
            }
            catch { return false; }
        }

        /// <summary>
        /// Validate a pasted key and, if good, bind it to this machine and persist it.
        /// When an API is configured, activation is enforced online (limit + revocation);
        /// otherwise it's offline (signature + machine binding only). Returns a tuple of
        /// (error, info): error is null on success, else a short human-readable reason.
        /// </summary>
        public static async Task<(string? error, LicenseInfo? info)> ActivateAsync(string? key)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0) return ("Enter your license key.", null);

            if (!Verify(key, out var info))
                return ("That license key isn't valid.", null);

            if (info!.Expires is { } exp && exp < DateTimeOffset.UtcNow)
                return ($"That license expired on {exp.LocalDateTime:d}.", null);

            if (OnlineEnabled)
            {
                var reason = await CallActivateAsync(key);
                if (reason != null) return (reason, null);
            }

            try
            {
                Directory.CreateDirectory(Folder);
                var record = JsonConvert.SerializeObject(new
                {
                    token = key,
                    hwid = HardwareId(),
                    act = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
                File.WriteAllBytes(LicenseFile, Protect(record));
                return (null, info);
            }
            catch (Exception ex) { return ("Couldn't save the activation: " + ex.Message, null); }
        }

        // POST /activate. Returns null on success, or a user-facing reason on refusal / no network.
        private static async Task<string?> CallActivateAsync(string key)
        {
            try
            {
                var body = new JObject
                {
                    ["key"] = key,
                    ["hwid"] = HardwareId(),
                    ["machine"] = Environment.MachineName,
                }.ToString(Formatting.None);

                using var resp = await Http.PostAsync(ApiBaseUrl.TrimEnd('/') + "/activate",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode) return null;

                var text = await resp.Content.ReadAsStringAsync();
                var reason = TryReason(text);
                return reason switch
                {
                    "limit" => "This license has reached its activation limit (too many devices). Deactivate it on another device or contact support.",
                    "revoked" => "This license has been revoked. Please contact support.",
                    "expired" => "This license has expired.",
                    "invalid" => "That license key isn't valid.",
                    _ => "Activation was refused by the server. Please try again or contact support.",
                };
            }
            catch
            {
                return "Couldn't reach the activation server. Check your internet connection and try again.";
            }
        }

        /// <summary>
        /// Best-effort background recheck (revocation). Safe to fire-and-forget at startup: if the
        /// server says this key is revoked, the local activation is removed so the next launch gates.
        /// Does nothing offline or when no API is configured.
        /// </summary>
        public static async Task RevalidateAsync()
        {
            if (!OnlineEnabled) return;
            try
            {
                if (!File.Exists(LicenseFile)) return;
                var record = Unprotect(File.ReadAllBytes(LicenseFile));
                if (record == null) return;
                var token = (string?)JObject.Parse(record)["token"];
                if (string.IsNullOrEmpty(token)) return;

                var body = new JObject { ["key"] = token, ["hwid"] = HardwareId() }.ToString(Formatting.None);
                using var resp = await Http.PostAsync(ApiBaseUrl.TrimEnd('/') + "/validate",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                var o = JObject.Parse(text);
                // Only act on an explicit revocation — never lock out on a transient server hiccup.
                if ((bool?)o["revoked"] == true) Deactivate();
            }
            catch { /* network/transient — leave the local activation intact */ }
        }

        private static string? TryReason(string json)
        {
            try { return (string?)JObject.Parse(json)["reason"]; } catch { return null; }
        }

        /// <summary>Verify a key's signature and expiry (does not touch stored state).</summary>
        public static bool Verify(string key, out LicenseInfo? info)
        {
            info = null;
            try
            {
                var parts = key.Split('.');
                if (parts.Length != 3 || parts[0] != KeyPrefix) return false;

                var payload = FromB64Url(parts[1]);
                var sig = FromB64Url(parts[2]);

                using var rsa = RSA.Create();
                rsa.FromXmlString(PublicKeyXml.Value);
                if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return false;

                var o = JObject.Parse(Encoding.UTF8.GetString(payload));
                long iat = (long?)o["iat"] ?? 0;
                long? exp = (long?)o["exp"];
                int max = (int?)o["max"] ?? 2;
                info = new LicenseInfo
                {
                    Name = (string?)o["n"] ?? "",
                    Email = (string?)o["e"] ?? "",
                    Order = (string?)o["o"] ?? "",
                    Max = max <= 0 ? 2 : max,
                    Issued = DateTimeOffset.FromUnixTimeSeconds(iat),
                    Expires = exp.HasValue ? DateTimeOffset.FromUnixTimeSeconds(exp.Value) : null,
                };

                if (info.Expires is { } e && e < DateTimeOffset.UtcNow) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Remove the stored activation (e.g. a "deactivate" button).</summary>
        public static void Deactivate()
        {
            try { if (File.Exists(LicenseFile)) File.Delete(LicenseFile); } catch { }
        }

        // ---- machine fingerprint ----
        // Windows MachineGuid is stable across reboots and reinstalls of the app. We hash it
        // (with app entropy) so the stored value isn't the raw system id.
        private static string HardwareId()
        {
            string raw;
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                raw = k?.GetValue("MachineGuid") as string ?? "";
            }
            catch { raw = ""; }
            if (string.IsNullOrEmpty(raw)) raw = Environment.MachineName;   // fallback

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("tt-hwid:" + raw));
            return Convert.ToHexString(bytes)[..32];
        }

        // ---- embedded public key ----
        private static readonly Lazy<string> PublicKeyXml = new(() =>
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("license_public.xml", StringComparison.OrdinalIgnoreCase));
            if (name == null) throw new InvalidOperationException("Embedded license public key missing.");
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        });

        // ---- DPAPI (per-user) protection of the stored activation record ----
        private static byte[] Protect(string plain) =>
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);

        private static string? Unprotect(byte[] blob)
        {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        private static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }
    }
}
