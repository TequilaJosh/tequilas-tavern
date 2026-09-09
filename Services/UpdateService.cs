using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace GameTracker.Services
{
    /// <summary>
    /// Checks the GitHub Releases API for a newer version and, if the user agrees,
    /// downloads the installer asset and launches it.
    /// </summary>
    public static class UpdateService
    {
        private const string Owner = "TequilaJosh";
        private const string Repo = "tequilas-tavern";   // this app's own releases repo

        private static readonly string ReleasesListApi =
            $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
        private static readonly string ReleasesPage =
            $"https://github.com/{Owner}/{Repo}/releases/latest";

        /// <summary>Invoked just before the app restarts to apply an update — lets the app
        /// record which windows are open so they can reopen afterward.</summary>
        public static Action? CaptureStateBeforeRestart;

        /// <param name="silent">
        /// When true (startup check), stays quiet if already up to date or on error.
        /// When false (manual check), reports those outcomes to the user.
        /// </param>
        public static async Task CheckForUpdatesAsync(bool silent = true)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GameTracker-Updater");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var json = await http.GetStringAsync(ReleasesListApi);
                var current = CurrentVersion();

                // Parse the release list (skip drafts/pre-releases), newest first.
                var releases = new List<(Version ver, string tag, string body, JObject obj)>();
                foreach (var r in JArray.Parse(json).OfType<JObject>())
                {
                    if ((bool?)r["draft"] == true || (bool?)r["prerelease"] == true) continue;
                    var t = (string?)r["tag_name"] ?? string.Empty;
                    var v = ParseVersion(t);
                    if (v != null) releases.Add((v, t, ((string?)r["body"] ?? string.Empty).Trim(), r));
                }
                releases.Sort((a, b) => b.ver.CompareTo(a.ver));

                if (releases.Count == 0 || releases[0].ver <= current)
                {
                    if (!silent)
                        Views.TavernDialog.Show($"You're on the latest version (v{current.ToString(3)}).",
                            "Tequilas' Tavern", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var latestRel = releases[0];
                var release = latestRel.obj;

                // Everything the user hasn't seen yet: release notes for each version above theirs.
                var entries = releases
                    .Where(x => x.ver > current)
                    .Select(x => (x.tag, x.body))
                    .ToList();

                bool doUpdate = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new Views.UpdateAvailableWindow(current.ToString(3), latestRel.tag, entries);
                    var owner = Application.Current.MainWindow;
                    if (owner != null && owner.IsLoaded && owner.IsVisible) dlg.Owner = owner;
                    doUpdate = dlg.ShowDialog() == true;
                });
                if (!doUpdate) return;

                var assets = (JArray?)release["assets"] ?? new JArray();
                var asset = assets
                    .FirstOrDefault(a => ((string?)a["name"] ?? "")
                        .EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                var downloadUrl = (string?)asset?["browser_download_url"];
                var assetName = (string?)asset?["name"];

                if (downloadUrl == null || assetName == null)
                {
                    // No installer attached — just open the releases page in the browser.
                    OpenInBrowser(ReleasesPage);
                    return;
                }

                // Only ever download from GitHub over HTTPS. The asset URL comes from the
                // API response, but pinning host + scheme means a tampered/unexpected URL
                // can't redirect the installer fetch to an attacker-controlled server.
                if (!IsTrustedGitHubHttps(downloadUrl))
                {
                    OpenInBrowser(ReleasesPage);
                    return;
                }

                // Strip any directory component from the asset name before using it as a
                // local path (defence against a crafted release asset name).
                var safeName = Path.GetFileName(assetName);
                var destination = Path.Combine(Path.GetTempPath(), safeName);

                // Optional integrity check: if the release publishes "<asset>.sha256", we
                // verify the download against it and refuse to run a mismatched installer.
                var expectedSha = await TryGetPublishedSha256(http, assets, assetName);

                var splash = new Views.UpdatingWindow();
                splash.Show();
                try
                {
                    using (var response = await http.GetAsync(downloadUrl,
                               HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var total = response.Content.Headers.ContentLength ?? -1;

                        await using var input = await response.Content.ReadAsStreamAsync();
                        await using var fs = File.Create(destination);

                        var buffer = new byte[81920];
                        long readSoFar = 0;
                        int read;
                        if (total <= 0) splash.SetIndeterminate("Downloading the update…");
                        while ((read = await input.ReadAsync(buffer)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read));
                            readSoFar += read;
                            if (total > 0)
                            {
                                splash.SetProgress((double)readSoFar / total * 100);
                                splash.SetStatus(
                                    $"Downloading the update…  {readSoFar / 1048576} / {total / 1048576} MB");
                            }
                        }
                    }

                    // Refuse to launch an installer whose hash doesn't match the one the
                    // release published (tamper / partial-download protection).
                    if (expectedSha != null)
                    {
                        var actual = await ComputeSha256(destination);
                        if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(destination); } catch { }
                            splash.Close();
                            Views.TavernDialog.Show(
                                "The downloaded update failed its integrity check and was discarded. " +
                                "Please download it manually from the releases page.",
                                "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                            OpenInBrowser(ReleasesPage);
                            return;
                        }
                    }

                    splash.SetIndeterminate("Installing… the app will reopen automatically.");
                    await Task.Delay(500); // let the message render

                    // Remember which windows are open so they reopen after the restart.
                    try { CaptureStateBeforeRestart?.Invoke(); } catch { /* best-effort */ }

                    // Run the installer silently (no wizard), then exit so it can replace the
                    // running files. The installer relaunches the app when it finishes.
                    Process.Start(new ProcessStartInfo(destination)
                    {
                        UseShellExecute = true,
                        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    });
                    Application.Current.Shutdown();
                }
                catch
                {
                    splash.Close();
                    throw;
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                    Views.TavernDialog.Show($"Couldn't check for updates:\n{ex.Message}",
                        "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Version CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static Version? ParseVersion(string tag)
        {
            // Accept tags like "v1.2.3", "1.2.3", or "v1.2.3-beta" (pre-release suffix ignored).
            var cleaned = tag.TrimStart('v', 'V').Trim().Split('-', '+')[0];
            if (!Version.TryParse(cleaned, out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        private static void OpenInBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        // The installer may only be fetched from GitHub itself, over HTTPS. GitHub serves
        // release assets from github.com and the objects.githubusercontent.com CDN.
        private static bool IsTrustedGitHubHttps(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = uri.Host;
            return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        // If the release includes a "<asset>.sha256" companion asset, fetch and return the
        // 64-hex digest so the download can be verified before it runs. Null = none published.
        private static async Task<string?> TryGetPublishedSha256(HttpClient http, JArray assets, string assetName)
        {
            try
            {
                var shaAsset = assets.FirstOrDefault(a =>
                {
                    var n = (string?)a["name"] ?? "";
                    return n.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase)
                        || n.Equals(assetName + ".sha256sum", StringComparison.OrdinalIgnoreCase);
                });
                var url = (string?)shaAsset?["browser_download_url"];
                if (url == null || !IsTrustedGitHubHttps(url)) return null;

                var text = await http.GetStringAsync(url);
                // Accept "abc123…  filename" (sha256sum format) or a bare digest.
                var token = text.Split(new[] { ' ', '\t', '\r', '\n', '*' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
            }
            catch { return null; }
        }

        private static async Task<string> ComputeSha256(string path)
        {
            await using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs);
            return Convert.ToHexString(hash);   // uppercase hex; compared case-insensitively
        }
    }
}
