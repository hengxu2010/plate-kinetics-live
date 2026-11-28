// GitHub Releases auto-updater shared by Plate Kinetics Analysis, Plate Kinetics Live (BMG), and Plate Kinetics Live (Archimed).
// Checks the latest published GitHub Release asynchronously, compares semantic versions, downloads the Setup.exe asset,
// verifies SHA-256 when published in release notes or a .sha256 / SHA256SUMS.txt asset, and launches silent update (/S /RELAUNCH).
// Pure ASCII source (Chinese texts as \u escapes).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SAAControl
{
    public sealed class UpdateResult
    {
        public bool Available;
        public bool Manual;
        public string Version = "";
        public string CurrentVersion = "";
        public string Notes = "";
        public string ReleaseUrl = "";
        public string AssetUrl = "";
        public string AssetName = "";
        public string Sha256 = "";
        public string Error = "";
    }

    public static class Updater
    {
        public const string Owner = "hengxu2010";

        static Updater()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
        }

        /// <summary>GitHub repository (owner/repo) for a given program.
        /// Each program follows its own release tag series (including Live installed in Analysis-only mode,
        /// which upgrades in place via its own installer with /ANALYSISONLY).</summary>
        public static string RepoFor(string appName, bool analysisOnly)
        {
            if (string.Equals(appName, "PlateKineticsAnalysis", StringComparison.OrdinalIgnoreCase))
                return Owner + "/plate-kinetics-analysis";
            if (string.Equals(appName, "PlateKineticsLive-Archimed", StringComparison.OrdinalIgnoreCase))
                return Owner + "/plate-kinetics-live-archimed";
            return Owner + "/plate-kinetics-live";
        }

        /// <summary>Expected Setup installer filename for this program.</summary>
        public static string SetupAssetPrefix(string appName, bool analysisOnly)
        {
            if (string.Equals(appName, "PlateKineticsAnalysis", StringComparison.OrdinalIgnoreCase))
                return "PlateKineticsAnalysis-Setup.exe";
            if (string.Equals(appName, "PlateKineticsLive-Archimed", StringComparison.OrdinalIgnoreCase))
                return "PlateKineticsLive-Archimed-Setup.exe";
            return "PlateKineticsLive-Setup.exe";
        }

        /// <summary>Parse a tag or version string like "v3.18.0" or "2.9.0.0" into a normalized 4-part Version.</summary>
        public static Version ParseVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = Regex.Match(raw.Trim(), @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            int a = int.Parse(m.Groups[1].Value);
            int b = int.Parse(m.Groups[2].Value);
            int c = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            int d = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
            return new Version(a, b, c, d);
        }

        /// <summary>True when candidateVersion is strictly newer than currentVersion.</summary>
        public static bool IsNewer(string candidateVersion, string currentVersion)
        {
            Version cand = ParseVersion(candidateVersion);
            Version cur = ParseVersion(currentVersion);
            return cand != null && cur != null && cand > cur;
        }

        /// <summary>Extract a 64-hex SHA-256 digest for assetName from a SHA256SUMS text or release notes body.</summary>
        public static string ExtractSha256(string text, string assetName)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string cleanName = Path.GetFileName(assetName ?? "").Trim();
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length < 64) continue;
                var m = Regex.Match(line, @"\b([0-9a-fA-F]{64})\b");
                if (!m.Success) continue;
                if (cleanName.Length == 0 || line.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return m.Groups[1].Value.ToLowerInvariant();
            }
            // Fallback: a single-line .sha256 file containing just the hash
            var single = Regex.Match(text.Trim(), @"^([0-9a-fA-F]{64})$");
            return single.Success ? single.Groups[1].Value.ToLowerInvariant() : "";
        }

        /// <summary>Pick the best matching Setup.exe asset and optional checksum asset from GitHub release assets.</summary>
        public static void PickAssets(IList<object> assets, string expectedName, out string setupUrl, out string setupName, out string shaUrl)
        {
            setupUrl = "";
            setupName = "";
            shaUrl = "";
            if (assets == null) return;
            string exactLower = (expectedName ?? "").ToLowerInvariant();
            string fallbackUrl = "", fallbackName = "";
            foreach (object obj in assets)
            {
                if (!(obj is Dictionary<string, object> a)) continue;
                string name = a.TryGetValue("name", out object n) ? n as string : "";
                string dl = a.TryGetValue("browser_download_url", out object u) ? u as string : "";
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                string lower = name.ToLowerInvariant();
                if (lower == exactLower)
                {
                    setupUrl = dl;
                    setupName = name;
                }
                else if (lower.EndsWith("-setup.exe") && lower.IndexOf(exactLower.Replace("-setup.exe", ""), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fallbackUrl = dl;
                    fallbackName = name;
                }
                else if (lower == exactLower + ".sha256" || lower == "sha256sums.txt" || lower.EndsWith(".sha256"))
                {
                    if (string.IsNullOrEmpty(shaUrl) || lower == exactLower + ".sha256") shaUrl = dl;
                }
            }
            if (string.IsNullOrEmpty(setupUrl) && !string.IsNullOrEmpty(fallbackUrl))
            {
                setupUrl = fallbackUrl;
                setupName = fallbackName;
            }
        }

        static string StateFile(string appName)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, InstallShared.DisplayName(appName), "updater.json");
        }

        public static string ReadDismissedVersion(string appName)
        {
            try
            {
                string file = StateFile(appName);
                if (!File.Exists(file)) return "";
                if (Json.Parse(File.ReadAllText(file, Encoding.UTF8)) is Dictionary<string, object> o && o.TryGetValue("dismissed", out object v))
                    return v as string ?? "";
            }
            catch (Exception) { }
            return "";
        }

        public static void DismissVersion(string appName, string version)
        {
            try
            {
                string file = StateFile(appName);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Json.Serialize(new Dictionary<string, object> { ["dismissed"] = version ?? "" }), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>Query GitHub Releases latest endpoint for updates.</summary>
        public static async Task<UpdateResult> CheckAsync(string appName, string currentVersion, bool analysisOnly, bool manual)
        {
            string repo = RepoFor(appName, analysisOnly);
            string expected = SetupAssetPrefix(appName, analysisOnly);
            var res = new UpdateResult { Manual = manual, CurrentVersion = currentVersion ?? "" };
            string apiUrl = "https://api.github.com/repos/" + repo + "/releases/latest";
            try
            {
                string body = await FetchTextAsync(apiUrl, 12000);
                if (!(Json.Parse(body) is Dictionary<string, object> rel))
                {
                    res.Error = "invalid GitHub release response";
                    return res;
                }
                if (rel.TryGetValue("draft", out object d) && d is bool draft && draft) return res;
                if (rel.TryGetValue("prerelease", out object p) && p is bool pre && pre) return res;
                string tag = rel.TryGetValue("tag_name", out object tn) ? tn as string : "";
                string htmlUrl = rel.TryGetValue("html_url", out object hu) ? hu as string : ("https://github.com/" + repo + "/releases");
                string notes = rel.TryGetValue("body", out object bd) ? bd as string : "";
                string cleanVer = (tag ?? "").TrimStart('v', 'V');
                res.Version = cleanVer;
                res.ReleaseUrl = htmlUrl;
                res.Notes = notes ?? "";
                if (!IsNewer(cleanVer, currentVersion)) return res;
                if (!manual && string.Equals(ReadDismissedVersion(appName), cleanVer, StringComparison.OrdinalIgnoreCase)) return res;

                var assets = rel.TryGetValue("assets", out object a) ? a as List<object> : null;
                PickAssets(assets, expected, out string setupUrl, out string setupName, out string shaUrl);
                res.AssetUrl = setupUrl;
                res.AssetName = setupName;
                res.Sha256 = ExtractSha256(notes, setupName.Length > 0 ? setupName : expected);
                if (string.IsNullOrEmpty(res.Sha256) && !string.IsNullOrEmpty(shaUrl))
                {
                    try { res.Sha256 = ExtractSha256(await FetchTextAsync(shaUrl, 8000), setupName.Length > 0 ? setupName : expected); }
                    catch (Exception) { }
                }
                res.Available = true;
                return res;
            }
            catch (WebException web)
            {
                if (web.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.NotFound)
                {
                    // Repository not published yet or no releases yet: treat silently as up to date
                    return res;
                }
                res.Error = web.Message;
                return res;
            }
            catch (Exception error)
            {
                res.Error = error.Message;
                return res;
            }
        }

        static async Task<string> FetchTextAsync(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "PlateKinetics-Updater/1.0";
            req.Accept = "application/vnd.github+json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)await req.GetResponseAsync())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return await reader.ReadToEndAsync();
        }

        /// <summary>Download the Setup.exe asset, verify PE header & optional SHA-256, and launch silent upgrade.</summary>
        public static async Task<string> DownloadAndInstallAsync(string appName, string version, string assetUrl, string expectedSha256,
                                                                 bool analysisOnly, Action<int> onProgress)
        {
            if (string.IsNullOrWhiteSpace(assetUrl) || !assetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "invalid download URL";
            string safeVer = Regex.Replace(version ?? "latest", @"[^0-9A-Za-z._-]", "");
            string tempFile = Path.Combine(Path.GetTempPath(), appName + "-update-" + safeVer + ".exe");
            string partFile = tempFile + ".part";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(assetUrl);
                req.Method = "GET";
                req.UserAgent = "PlateKinetics-Updater/1.0";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;
                using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                using (var input = resp.GetResponseStream())
                using (var output = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    long total = resp.ContentLength;
                    byte[] buf = new byte[32768];
                    long done = 0;
                    int lastPct = -1;
                    int n;
                    while ((n = await input.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await output.WriteAsync(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (total > 0 && onProgress != null)
                        {
                            int pct = (int)Math.Min(100, done * 100 / total);
                            if (pct != lastPct) { lastPct = pct; onProgress(pct); }
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actualHash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (done < 100 * 1024) return "downloaded installer is incomplete";
                    if (!string.IsNullOrWhiteSpace(expectedSha256) && !string.Equals(actualHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        return "SHA-256 mismatch (expected " + expectedSha256.Substring(0, Math.Min(12, expectedSha256.Length)) + ", got " + actualHash.Substring(0, 12) + ")";
                }
                // Check PE MZ header
                using (var check = File.OpenRead(partFile))
                {
                    if (check.ReadByte() != 0x4D || check.ReadByte() != 0x5A) return "downloaded file is not a Windows executable";
                }
                if (File.Exists(tempFile)) File.Delete(tempFile);
                File.Move(partFile, tempFile);

                string args = "/S /RELAUNCH" + (analysisOnly ? " /ANALYSISONLY" : "");
                var psi = new ProcessStartInfo(tempFile, args) { UseShellExecute = true };
                Process.Start(psi);
                return null;
            }
            catch (Exception error)
            {
                try { if (File.Exists(partFile)) File.Delete(partFile); } catch (Exception) { }
                return error.Message;
            }
        }
    }
}
