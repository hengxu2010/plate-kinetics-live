// The updater shared by Plate Kinetics Analysis, Plate Kinetics Live (BMG) and Plate Kinetics Live (Archimed): it asks
// GitHub for the latest published release of the program's own repository, compares the versions and - when the user
// says so - downloads the release's Setup.exe and starts it silently (/S /RELAUNCH).
//
// What it trusts, and nothing else:
// - the latest release as api.github.com tells it (https; a redirect is not followed);
// - an installer at https://github.com/<owner>/<repository>/releases/download/... of this program's own repository,
//   fetched through redirects that stay with GitHub (github.com, *.githubusercontent.com; followed by hand, 5 at most);
// - whose SHA-256 the release itself publishes (SHA256SUMS.txt, <installer>.sha256, or the release notes). Without one
//   the new version is announced, but it is not installed from here. The hash is checked on the file as it lies on the
//   disk, and the file is held (nobody can write to it, replace or delete it) from that check until the installer runs.
// The page never chooses what is downloaded: the window installs only what its own check found (MainWindow.cs).
//
// Every network step runs on the thread pool, with a deadline that aborts the request: HttpWebRequest's own Timeout
// covers neither the name lookup nor the proxy detection, and both run on the thread that starts the request. A PC
// without internet sees no stall, and a check always answers.
//
// The check at the start of the window is a setting (updater.json, beside the user's other files of the program): on
// for the analysis programs, off for the recorder - an instrument PC connects nowhere unless its user asks.
// Pure ASCII source (Chinese texts as \u escapes; each stands beside its English).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAControl
{
    public sealed class UpdateResult
    {
        public bool Available;
        public bool Manual;
        /// <summary>The release has this program's installer at an address that is trusted, and publishes its SHA-256:
        /// it can be installed from the program. False: the version is only announced (its release page has it).</summary>
        public bool CanInstall;
        public string Version = "";
        public string CurrentVersion = "";
        public string Notes = "";
        public string ReleaseUrl = "";
        public string AssetUrl = "";
        public string AssetName = "";
        public string Sha256 = "";
        public string Error = "";
        /// <summary>The error in Chinese; "" where only Windows' own words are there (they are in Windows' language).</summary>
        public string ErrorZh = "";
    }

    /// <summary>Why an update is not installed, in English and in Chinese (MessageZh "" where only Windows' own words
    /// are there).</summary>
    public sealed class UpdateProblem
    {
        public readonly string Message, MessageZh;

        public UpdateProblem(string message, string messageZh = "")
        {
            Message = message ?? "";
            MessageZh = messageZh ?? "";
        }
    }

    public static class Updater
    {
        public const string Owner = "hengxu2010";
        const string UserAgent = "PlateKinetics-Updater/1.0";

        // how much is read from the network at most: the release, a checksum file, the installer
        public const int MaxReleaseBytes = 2 << 20, MaxChecksumBytes = 256 << 10;
        public const long MaxInstallerBytes = 300L << 20;
        const long MinInstallerBytes = 100 << 10;
        const int MaxRedirects = 5;
        // the deadlines (seconds): the check, the checksum file, the download without a byte and as a whole
        const int CheckSeconds = 15, ChecksumSeconds = 10, DownloadIdleSeconds = 30, DownloadSeconds = 20 * 60;

        /// <summary>The argument the installer starts the program with again after a silent update that failed:
        /// --update-failed=&lt;its exit code&gt;.</summary>
        public const string UpdateFailedOption = "--update-failed";

        /// <summary>The exit code of the installer that started this program again after a silent update had failed;
        /// null: none (or the window has told the page). Set at the program's start from its arguments.</summary>
        public static int? FailedUpdate;

        static Updater()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
        }

        /// <summary>A failure the updater words itself: in English (Message) and in Chinese.</summary>
        sealed class UpdaterException : Exception
        {
            public readonly string Zh;
            public UpdaterException(string en, string zh) : base(en) { Zh = zh; }
        }

        static UpdateProblem ProblemOf(Exception error) => new UpdateProblem(error.Message, (error as UpdaterException)?.Zh);

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

        /// <summary>The page that lists a repository's releases.</summary>
        public static string ReleasesPage(string repo) => "https://github.com/" + repo + "/releases";

        /// <summary>Parse a tag or version string like "v3.18.0" or "2.9.0.0" into a normalized 4-part Version.</summary>
        public static Version ParseVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = Regex.Match(raw.Trim(), @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            // (a number too long for an int is no version: null, not an error)
            int c = 0, d = 0;
            if (!int.TryParse(m.Groups[1].Value, out int a) || !int.TryParse(m.Groups[2].Value, out int b)) return null;
            if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, out c)) return null;
            if (m.Groups[4].Success && !int.TryParse(m.Groups[4].Value, out d)) return null;
            return new Version(a, b, c, d);
        }

        /// <summary>True when candidateVersion is strictly newer than currentVersion.</summary>
        public static bool IsNewer(string candidateVersion, string currentVersion)
        {
            Version cand = ParseVersion(candidateVersion);
            Version cur = ParseVersion(currentVersion);
            return cand != null && cur != null && cand > cur;
        }

        /// <summary>A program's version as the releases are tagged: three parts, and the fourth too when it is not 0 - a
        /// release tagged x.y.z.w must compare equal once it is installed, or it would be offered again for ever.</summary>
        public static string VersionText(Version v)
        {
            if (v == null) return "";
            string text = v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            return v.Revision > 0 ? text + "." + v.Revision : text;
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

        /// <summary>A checksum file as text. Such a file is written on Windows, where each tool chooses the encoding
        /// itself - PowerShell's ">" and Out-File write UTF-16, others UTF-8 with a byte order mark in front: the mark
        /// says which, and it is not part of the text. Without one the file is UTF-8.</summary>
        public static string ChecksumText(byte[] file)
        {
            if (file == null) return "";
            using (var reader = new StreamReader(new MemoryStream(file), Encoding.UTF8, true))
                return reader.ReadToEnd().TrimStart('\uFEFF');
        }

        /// <summary>64 hexadecimal digits.</summary>
        public static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        /// <summary>This program's installer among the files of a release, and the release's checksum file. Only exact
        /// names count: the installer's own (a name that merely contains it is another program's -
        /// "PlateKineticsLive-Archimed-Setup.exe" is not the BMG program's installer), and for the checksums
        /// "&lt;installer&gt;.sha256" (preferred) or "SHA256SUMS.txt".</summary>
        public static void PickAssets(IList<object> assets, string expectedName, out string setupUrl, out string setupName, out string shaUrl)
        {
            setupUrl = "";
            setupName = "";
            shaUrl = "";
            if (assets == null) return;
            string exactLower = (expectedName ?? "").ToLowerInvariant();
            bool own = false;                      // the installer's own checksum file was found
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
                else if (lower == exactLower + ".sha256")
                {
                    shaUrl = dl;
                    own = true;
                }
                else if (lower == "sha256sums.txt" && !own) shaUrl = dl;
            }
        }

        // ---- which addresses are trusted ----

        /// <summary>An address as text, read strictly: GitHub's addresses are plain ASCII, so a blank, a control
        /// character or a backslash in one is somebody's trick. null: not an absolute address.</summary>
        static Uri Address(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            foreach (char c in url)
                if (c <= ' ' || c > '~' || c == '\\') return null;
            return Uri.TryCreate(url, UriKind.Absolute, out Uri address) ? address : null;
        }

        /// <summary>https to a named host, on the usual port, with no user name in front of the host.</summary>
        static bool PlainHttps(Uri address) =>
            address != null && address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps && address.HostNameType == UriHostNameType.Dns
            && address.UserInfo.Length == 0 && address.IsDefaultPort;

        static bool IsGitHub(Uri address) => PlainHttps(address) && address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>The address of a file of a release of this repository ("owner/name") as GitHub gives it:
        /// https://github.com/&lt;owner&gt;/&lt;name&gt;/releases/download/&lt;tag&gt;/&lt;file&gt;. Only such an address is
        /// downloaded from (the installer, its checksum file).</summary>
        public static bool IsTrustedAssetUrl(string url, string repo) => TrustedAsset(url, repo, out _);

        static bool TrustedAsset(string url, string repo, out Uri address)
        {
            address = Address(url);
            if (string.IsNullOrEmpty(repo) || !IsGitHub(address)) return false;
            // the path as it is sent: /owner/name/releases/download/tag/file (GitHub reads owner and name in any case)
            string[] steps = address.AbsolutePath.Split('/'), names = repo.Split('/');
            if (names.Length != 2 || steps.Length < 7 || steps[0].Length != 0) return false;
            if (!steps[1].Equals(names[0], StringComparison.OrdinalIgnoreCase) || !steps[2].Equals(names[1], StringComparison.OrdinalIgnoreCase)) return false;
            if (steps[3] != "releases" || steps[4] != "download") return false;
            // a "." or ".." step - written out or as %2e - is no part of such an address
            for (int i = 5; i < steps.Length; i++)
            {
                string step = Uri.UnescapeDataString(steps[i]);
                if (step.Length == 0 || step == "." || step == ".." || step.IndexOf('/') >= 0 || step.IndexOf('\\') >= 0) return false;
            }
            return true;
        }

        /// <summary>An address a download may be redirected to (and may start at): https at github.com or at a host of
        /// githubusercontent.com, where GitHub keeps the files of its releases.</summary>
        public static bool IsTrustedRedirectHop(string url) => TrustedHop(Address(url));

        static bool TrustedHop(Uri address) =>
            IsGitHub(address) || (PlainHttps(address) && address.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        /// <summary>The page of a release as GitHub names it, when that is a page of this repository at github.com; else
        /// the repository's list of releases.</summary>
        static string ReleasePage(string htmlUrl, string repo)
        {
            Uri address = Address(htmlUrl);
            return IsGitHub(address) && address.AbsolutePath.StartsWith("/" + repo + "/", StringComparison.OrdinalIgnoreCase)
                ? address.AbsoluteUri : ReleasesPage(repo);
        }

        // ---- the updater's own file: {"dismissed": the version the user said "Later" to, "auto": true|false} ----

        /// <summary>Tests: the folder used instead of the user's local application data.</summary>
        public static string StateRoot;

        static readonly object StateGate = new object();

        /// <summary>%LOCALAPPDATA%\&lt;the program's name&gt;: updater.json, and the downloads in updates\.</summary>
        static string StateFolder(string appName) =>
            Path.Combine(StateRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallShared.DisplayName(appName));

        static string StateFile(string appName) => Path.Combine(StateFolder(appName), "updater.json");

        /// <summary>The file as it is (a file of an earlier version has only "dismissed"); empty when there is none or
        /// it cannot be read.</summary>
        static Dictionary<string, object> ReadState(string appName)
        {
            try
            {
                string file = StateFile(appName);
                if (File.Exists(file) && Json.Parse(File.ReadAllText(file, Encoding.UTF8)) is Dictionary<string, object> state) return state;
            }
            catch (Exception) { }
            return new Dictionary<string, object>();
        }

        /// <summary>One value changed, the others kept; written whole (a temporary file takes the file's place).</summary>
        static void WriteState(string appName, string key, object value)
        {
            lock (StateGate)
            {
                try
                {
                    var state = ReadState(appName);
                    state[key] = value;
                    string file = StateFile(appName), temp = file + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(temp, Json.Serialize(state), new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(temp, file, null);
                    else File.Move(temp, file);
                }
                catch (Exception error) { Log.Warning("the updater's settings could not be saved (" + error.Message + ")"); }
            }
        }

        public static string ReadDismissedVersion(string appName) =>
            ReadState(appName).TryGetValue("dismissed", out object v) ? v as string ?? "" : "";

        public static void DismissVersion(string appName, string version)
        {
            if (version != null && version.Length > 64) return;    // (no version is that long: the file is not the page's to fill)
            WriteState(appName, "dismissed", version ?? "");
        }

        /// <summary>Whether the window checks for a new version by itself at its start. Until the user chooses: yes for
        /// the analysis programs (the standalone one, an analysis-only installation), no for the recorder - its manual
        /// promises that it connects nowhere on its own, and it runs on instrument PCs. "Check for updates" always works.</summary>
        public static bool AutoCheck(string appName, bool standaloneOrAnalysisOnly) =>
            ReadState(appName).TryGetValue("auto", out object v) && v is bool on ? on : standaloneOrAnalysisOnly;

        public static void SetAutoCheck(string appName, bool on) => WriteState(appName, "auto", on);

        // ---- the start after a failed silent update ----

        /// <summary>The exit code of "--update-failed=&lt;code&gt;" among the program's arguments: null when the argument is
        /// not there, -1 when it carries no number. (It starts with "--": it is never a file to open.)</summary>
        public static int? UpdateFailedCode(string[] argv)
        {
            if (argv == null) return null;
            foreach (string a in argv)
            {
                if (a == null) continue;
                if (a.Equals(UpdateFailedOption, StringComparison.OrdinalIgnoreCase)) return -1;
                if (!a.StartsWith(UpdateFailedOption + "=", StringComparison.OrdinalIgnoreCase)) continue;
                string text = a.Substring(UpdateFailedOption.Length + 1).Trim().Trim('"');
                // (Windows' exit codes are 32 bits: one written without its sign is the same code)
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long code) || code < int.MinValue || code > uint.MaxValue) return -1;
                return unchecked((int)code);
            }
            return null;
        }

        /// <summary>How long (seconds) the start after a failed update waits for the window that started the update.</summary>
        public const int ClosingWindowSeconds = 15;

        /// <summary>The start after a silent update that failed may come while the window that started the update is
        /// still closing (its last autosave, the web control): that window still counts as the running one, so this
        /// start would only call it to the front and end - and nothing would come back to the user. Such a start
        /// therefore waits, limit at most, until that window's process has let go of the mutex that marks the running
        /// window. True: this start has the mutex now, it is the first; false: a window stays (handed over to as always).</summary>
        public static bool TakeOverFrom(Mutex earlier, TimeSpan limit)
        {
            try { return earlier.WaitOne(limit); }
            catch (AbandonedMutexException) { return true; }       // (that process ended without letting go: the mutex is this start's)
        }

        /// <summary>The same for a window that is known by its named event (the recorder's window): waits, limit at
        /// most, until no such event is there any more. True: it is gone.</summary>
        public static bool GoneWithin(string eventName, TimeSpan limit)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle there)) return true;
                    there.Dispose();               // (held, it would keep the event there)
                }
                catch (UnauthorizedAccessException) { return false; }      // (a window with other rights: the caller deals with it)
                if (waited.Elapsed >= limit) return false;
                Thread.Sleep(200);
            }
        }

        // ---- what the window tells its page ----

        /// <summary>This window may install the update its check found: the release can be installed (CanInstall), the
        /// program is an installed one (a portable copy has no installer to run over it), and the window does not record
        /// by itself (the installer would stop the recording, then fail on the running program).</summary>
        public static bool CanInstallHere(UpdateResult update, string edition, bool recordsHere) =>
            update != null && update.Available && update.CanInstall && edition == "installed" && !recordsHere;

        static UpdateProblem NotInstallable() =>
            new UpdateProblem("this version cannot be installed from the program (GitHub has no installer with a published SHA-256 for it): download it from the release page",
                              "\u8fd9\u4e2a\u7248\u672c\u4e0d\u80fd\u5728\u7a0b\u5e8f\u91cc\u76f4\u63a5\u5b89\u88c5\uff08GitHub \u4e0a\u6ca1\u6709\u5e26 SHA-256 \u6821\u9a8c\u503c\u7684\u5b89\u88c5\u7a0b\u5e8f\uff09\uff0c\u8bf7\u5230\u53d1\u5e03\u9875\u4e0b\u8f7d");

        /// <summary>Why the page's "installUpdate" of a version is refused; null: it may run. pending: what the window's
        /// own last check found - the page only names the version, and it must be that one.</summary>
        public static UpdateProblem InstallRefusal(UpdateResult pending, string version, string edition, bool recordsHere)
        {
            if (pending == null || !pending.Available || string.IsNullOrEmpty(version) || !string.Equals(version, pending.Version, StringComparison.Ordinal))
                return new UpdateProblem("please check for updates again", "\u8bf7\u91cd\u65b0\u68c0\u67e5\u66f4\u65b0");
            if (!pending.CanInstall) return NotInstallable();
            if (edition != "installed")
                return new UpdateProblem("a portable copy is not updated from the program: download the new version from the release page",
                                         "\u4fbf\u643a\u7248\u4e0d\u80fd\u5728\u7a0b\u5e8f\u91cc\u66f4\u65b0\uff0c\u8bf7\u5230\u53d1\u5e03\u9875\u4e0b\u8f7d\u65b0\u7248\u672c");
            if (recordsHere)
                return new UpdateProblem("this window records the instruments itself, an update would stop the recording: download the new version from the release page and install it while no instrument is measuring",
                                         "\u8fd9\u4e2a\u7a97\u53e3\u81ea\u5df1\u5728\u8bb0\u5f55\u4eea\u5668\u6570\u636e\uff0c\u66f4\u65b0\u4f1a\u4e2d\u65ad\u8bb0\u5f55\uff1a\u8bf7\u5230\u53d1\u5e03\u9875\u4e0b\u8f7d\u65b0\u7248\u672c\uff0c\u5728\u6ca1\u6709\u4eea\u5668\u6d4b\u91cf\u65f6\u5b89\u88c5");
            return null;
        }

        /// <summary>The "updateInfo" message to the page: {cmd, available, manual, version, notes, url, canInstall, auto,
        /// error, errorZh, assetUrl} and, where given, state ("downloading", "installing", "error"), progress (0-100) and
        /// code. problem: what failed (else the check's own error). assetUrl is there only so that a page of an earlier
        /// version still shows its "Update now": nothing reads it back.</summary>
        public static Dictionary<string, object> Info(UpdateResult update, bool manual, bool canInstall, bool auto, string state = null,
                                                      int progress = -1, UpdateProblem problem = null, int? code = null)
        {
            update = update ?? new UpdateResult();
            bool busy = state == "downloading" || state == "installing";
            var info = new Dictionary<string, object>
            {
                ["cmd"] = "updateInfo",
                ["available"] = update.Available || state != null,
                ["manual"] = manual,
                ["version"] = update.Version ?? "",
                ["notes"] = busy ? "" : update.Notes ?? "",          // (not with every percent of a download)
                ["url"] = update.ReleaseUrl ?? "",
                ["canInstall"] = canInstall,
                ["auto"] = auto,
                ["error"] = problem != null ? problem.Message : update.Error ?? "",
                ["errorZh"] = problem != null ? problem.MessageZh : update.ErrorZh ?? "",
                ["assetUrl"] = canInstall ? update.AssetUrl ?? "" : "",
            };
            if (state != null) info["state"] = state;
            if (progress >= 0) info["progress"] = Math.Min(100, progress);
            if (code != null) info["code"] = code.Value;
            return info;
        }

        /// <summary>The "updateInfo" message after the installer started the program again because a silent update had
        /// failed (code: its exit code): the error state, with the repository's release page.</summary>
        public static Dictionary<string, object> FailedInfo(string appName, bool analysisOnly, int code, bool auto)
        {
            var nothing = new UpdateResult { ReleaseUrl = ReleasesPage(RepoFor(appName, analysisOnly)) };
            var problem = new UpdateProblem("the installer ended with code " + code + "; nothing was changed",
                                            "\u5b89\u88c5\u7a0b\u5e8f\u4ee5\u4ee3\u7801 " + code + " \u7ed3\u675f\uff0c\u6ca1\u6709\u505a\u4efb\u4f55\u66f4\u6539");
            return Info(nothing, false, false, auto, "error", -1, problem, code);
        }

        // ---- the check ----

        /// <summary>Asks GitHub for the latest release. Never throws and always answers: an error is in the result.</summary>
        public static async Task<UpdateResult> CheckAsync(string appName, string currentVersion, bool analysisOnly, bool manual)
        {
            // on the thread pool: the name lookup and the proxy detection of a request run on the thread that starts it
            Task<UpdateResult> work = Task.Run(() => Check(appName, currentVersion, analysisOnly, manual));
            // (those two cannot be aborted: the answer does not wait for them beyond the deadlines)
            Task first = await Task.WhenAny(work, Task.Delay((CheckSeconds + ChecksumSeconds + 5) * 1000)).ConfigureAwait(false);
            if (first == work) return work.Result;
            return new UpdateResult
            {
                Manual = manual, CurrentVersion = currentVersion ?? "", ReleaseUrl = ReleasesPage(RepoFor(appName, analysisOnly)),
                Error = "GitHub did not answer in time", ErrorZh = "GitHub \u6ca1\u6709\u53ca\u65f6\u5e94\u7b54",
            };
        }

        static UpdateProblem NoAnswer(int seconds) =>
            new UpdateProblem("GitHub did not answer within " + seconds + " seconds", "GitHub \u5728 " + seconds + " \u79d2\u5185\u6ca1\u6709\u5e94\u7b54");

        static UpdateResult Check(string appName, string currentVersion, bool analysisOnly, bool manual)
        {
            string repo = RepoFor(appName, analysisOnly);
            var res = new UpdateResult { Manual = manual, CurrentVersion = currentVersion ?? "", ReleaseUrl = ReleasesPage(repo) };
            try
            {
                var api = new Uri("https://api.github.com/repos/" + repo + "/releases/latest");
                byte[] body = Timed(CheckSeconds, 0, NoAnswer(CheckSeconds), CancellationToken.None,
                                    limit => Fetch(api, false, "application/vnd.github+json", MaxReleaseBytes, limit));
                res = ReadRelease(Encoding.UTF8.GetString(body), appName, currentVersion, analysisOnly, manual, ReadDismissedVersion(appName), out string sums);
                if (res.Available && !res.CanInstall && TrustedAsset(sums, repo, out Uri list))
                {
                    // the release's own list of checksums; when it cannot be had, the version is announced all the same
                    try
                    {
                        byte[] text = Timed(ChecksumSeconds, 0, NoAnswer(ChecksumSeconds), CancellationToken.None,
                                            limit => Fetch(list, true, null, MaxChecksumBytes, limit));
                        res.Sha256 = ExtractSha256(ChecksumText(text), res.AssetName);
                        res.CanInstall = IsSha256(res.Sha256);
                        // (said in the log: nothing else tells why such a version never shows "Update now")
                        if (!res.CanInstall)
                            Log.Warning("the checksum file of the release " + Shown(res.Version) + " has no SHA-256 of " + Shown(res.AssetName) +
                                        " (looked for: a line with its 64 hexadecimal digits and that file name): it is not installed from the program");
                    }
                    catch (Exception error)
                    {
                        Log.Warning("the checksums of the release " + Shown(res.Version) + " could not be read (" + error.Message + "): it is not installed from the program");
                    }
                }
                return res;
            }
            catch (WebException web) when (web.Response is HttpWebResponse answer && answer.StatusCode == HttpStatusCode.NotFound)
            {
                // Repository not published yet or no releases yet: treat silently as up to date
                try { web.Response.Dispose(); } catch (Exception) { }
                return res;
            }
            catch (Exception error)
            {
                res.Error = error.Message;
                res.ErrorZh = (error as UpdaterException)?.Zh ?? "";
                return res;
            }
        }

        /// <summary>What a release (GitHub's JSON of releases/latest) means for this program; no network, no files.
        /// dismissed: the version the user said "Later" to (a check the user did not ask for does not announce it again).
        /// shaUrl: the release's checksum file to read when its notes carry no SHA-256 of the installer; "" for none.</summary>
        public static UpdateResult ReadRelease(string json, string appName, string currentVersion, bool analysisOnly, bool manual, string dismissed,
                                               out string shaUrl)
        {
            shaUrl = "";
            string repo = RepoFor(appName, analysisOnly);
            string expected = SetupAssetPrefix(appName, analysisOnly);
            var res = new UpdateResult { Manual = manual, CurrentVersion = currentVersion ?? "", ReleaseUrl = ReleasesPage(repo) };
            Dictionary<string, object> rel = null;
            try { rel = Json.Parse(json ?? "") as Dictionary<string, object>; }
            catch (Exception) { }
            if (rel == null)
            {
                res.Error = "invalid GitHub release response";
                res.ErrorZh = "GitHub \u8fd4\u56de\u7684\u53d1\u5e03\u4fe1\u606f\u65e0\u6cd5\u8bfb\u53d6";
                return res;
            }
            if (rel.TryGetValue("draft", out object d) && d is bool draft && draft) return res;
            if (rel.TryGetValue("prerelease", out object p) && p is bool pre && pre) return res;
            string tag = rel.TryGetValue("tag_name", out object tn) ? tn as string : "";
            string notes = rel.TryGetValue("body", out object bd) ? bd as string : "";
            string cleanVer = (tag ?? "").TrimStart('v', 'V');
            res.Version = cleanVer;
            res.ReleaseUrl = ReleasePage(rel.TryGetValue("html_url", out object hu) ? hu as string : null, repo);
            res.Notes = notes ?? "";
            if (!IsNewer(cleanVer, currentVersion)) return res;
            if (!manual && string.Equals(dismissed, cleanVer, StringComparison.OrdinalIgnoreCase)) return res;

            res.Available = true;
            var assets = rel.TryGetValue("assets", out object a) ? a as List<object> : null;
            PickAssets(assets, expected, out string setupUrl, out string setupName, out string sums);
            // an installer anywhere else than among this repository's release files is not downloaded: the version is
            // announced, its release page has the rest
            if (!IsTrustedAssetUrl(setupUrl, repo)) return res;
            res.AssetUrl = setupUrl;
            res.AssetName = setupName;
            res.Sha256 = ExtractSha256(res.Notes, setupName);
            res.CanInstall = IsSha256(res.Sha256);
            if (!res.CanInstall && IsTrustedAssetUrl(sums, repo)) shaUrl = sums;
            return res;
        }

        // ---- the network: every step with a deadline ----

        /// <summary>The time a network step has. When it is over, when no byte came for the idle time, or when the step
        /// is cancelled, the request in hand is aborted - which ends its waiting and its reading at once.</summary>
        sealed class Deadline : IDisposable
        {
            readonly Stopwatch clock = Stopwatch.StartNew();
            readonly long overall, idle;           // milliseconds; idle 0: no such limit
            readonly UpdateProblem slow;           // what is said when the whole time is over
            readonly CancellationToken cancel;
            readonly object gate = new object();
            readonly System.Threading.Timer timer;
            HttpWebRequest request;
            long lastByte;
            UpdateProblem over;

            public Deadline(int seconds, int idleSeconds, UpdateProblem slow, CancellationToken cancel)
            {
                overall = seconds * 1000L;
                idle = idleSeconds * 1000L;
                this.slow = slow;
                this.cancel = cancel;
                timer = new System.Threading.Timer(state => Look(), null, 250, 250);
            }

            /// <summary>Why the step was ended; null: it was not.</summary>
            public UpdateProblem Over { get { lock (gate) return over; } }

            /// <summary>For the request's own time limits (they cover what they cover): a little longer than the
            /// deadline, so that the deadline's words are the ones said.</summary>
            public int RequestMilliseconds => (int)((idle > 0 ? Math.Min(idle, overall) : overall) + 2000);

            /// <summary>The request that waits or reads now.</summary>
            public void Watch(HttpWebRequest current)
            {
                lock (gate) request = current;
                Look();
            }

            /// <summary>An answer or a byte came.</summary>
            public void Progress() => Interlocked.Exchange(ref lastByte, clock.ElapsedMilliseconds);

            void Look()
            {
                long now = clock.ElapsedMilliseconds;
                UpdateProblem why = null;
                if (cancel.IsCancellationRequested) why = new UpdateProblem("the update was cancelled", "\u66f4\u65b0\u5df2\u53d6\u6d88");
                else if (now >= overall) why = slow;
                else if (idle > 0 && now - Interlocked.Read(ref lastByte) >= idle)
                    why = new UpdateProblem("the download stalled: no data for " + idle / 1000 + " seconds", "\u4e0b\u8f7d\u4e2d\u65ad\uff1a" + idle / 1000 + " \u79d2\u6ca1\u6709\u6536\u5230\u6570\u636e");
                if (why == null) return;
                HttpWebRequest current;
                lock (gate)
                {
                    if (over == null) over = why;
                    current = request;
                }
                try { current?.Abort(); } catch (Exception) { }
            }

            public void Dispose() => timer.Dispose();
        }

        /// <summary>One network step within its time; when the deadline ended it, the error says so.</summary>
        static T Timed<T>(int seconds, int idleSeconds, UpdateProblem slow, CancellationToken cancel, Func<Deadline, T> step)
        {
            using (var limit = new Deadline(seconds, idleSeconds, slow, cancel))
            {
                try { return step(limit); }
                catch (Exception) when (limit.Over != null) { throw new UpdaterException(limit.Over.Message, limit.Over.MessageZh); }
            }
        }

        /// <summary>The answer to a GET. redirects: followed by hand, each address looked at before it is asked - only
        /// GitHub's (IsTrustedRedirectHop), 5 at most; false: a redirect is an error (api.github.com).</summary>
        static HttpWebResponse Open(Uri address, bool redirects, string accept, Deadline limit)
        {
            Uri at = address;
            for (int hop = 0; ; hop++)
            {
                if (redirects && !TrustedHop(at))
                    throw new UpdaterException("the download was sent to an address that is not GitHub's (" + at.Host + ")",
                                               "\u4e0b\u8f7d\u88ab\u8f6c\u5230\u4e86\u4e0d\u5c5e\u4e8e GitHub \u7684\u5730\u5740\uff08" + at.Host + "\uff09");
                var request = (HttpWebRequest)WebRequest.Create(at);
                request.Method = "GET";
                request.UserAgent = UserAgent;
                if (accept != null) request.Accept = accept;
                request.AllowAutoRedirect = false;
                request.Timeout = limit.RequestMilliseconds;
                request.ReadWriteTimeout = limit.RequestMilliseconds;
                limit.Watch(request);
                var response = (HttpWebResponse)request.GetResponse();
                limit.Progress();
                int status = (int)response.StatusCode;
                if (status == 200) return response;
                string location = response.Headers[HttpResponseHeader.Location];
                response.Dispose();
                bool moved = status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
                if (!moved) throw new UpdaterException("GitHub answered " + status, "GitHub \u8fd4\u56de\u4e86 " + status);
                if (!redirects)
                    throw new UpdaterException("GitHub answered with a redirect (" + status + "), which is not followed",
                                               "GitHub \u7684\u5e94\u7b54\u662f\u91cd\u5b9a\u5411\uff08" + status + "\uff09\uff0c\u4e0d\u4e88\u8ddf\u968f");
                if (hop >= MaxRedirects)
                    throw new UpdaterException("the download was redirected more than " + MaxRedirects + " times",
                                               "\u4e0b\u8f7d\u88ab\u91cd\u5b9a\u5411\u8d85\u8fc7 " + MaxRedirects + " \u6b21");
                if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(at, location, out Uri next))
                    throw new UpdaterException("the download was redirected without an address", "\u4e0b\u8f7d\u88ab\u91cd\u5b9a\u5411\uff0c\u4f46\u6ca1\u6709\u7ed9\u51fa\u5730\u5740");
                at = next;
            }
        }

        static UpdaterException TooLarge(long limitBytes) =>
            new UpdaterException("GitHub sent more than " + (limitBytes >> 10) + " KB: not read", "GitHub \u53d1\u6765\u7684\u5185\u5bb9\u8d85\u8fc7 " + (limitBytes >> 10) + " KB\uff0c\u6ca1\u6709\u8bfb\u53d6");

        /// <summary>The body of a GET, maxBytes long at most.</summary>
        static byte[] Fetch(Uri address, bool redirects, string accept, int maxBytes, Deadline limit)
        {
            using (HttpWebResponse response = Open(address, redirects, accept, limit))
            using (Stream input = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                if (response.ContentLength > maxBytes) throw TooLarge(maxBytes);
                var buffer = new byte[16384];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + n > maxBytes) throw TooLarge(maxBytes);
                    memory.Write(buffer, 0, n);
                    limit.Progress();
                }
                return memory.ToArray();
            }
        }

        // ---- the download and the start of the installer ----

        /// <summary>A new folder for one download: %LOCALAPPDATA%\&lt;the program's name&gt;\updates\&lt;a name nobody can
        /// guess&gt;. The folders of earlier downloads are removed first (what is still in use stays).</summary>
        public static string NewUpdateFolder(string appName)
        {
            string root = Path.Combine(StateFolder(appName), "updates");
            try
            {
                if (Directory.Exists(root))
                    foreach (string old in Directory.GetDirectories(root))
                    {
                        try { Directory.Delete(old, true); } catch (Exception) { }
                    }
            }
            catch (Exception) { }
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The downloaded installer as it lies on the disk: long enough, a Windows program, with the SHA-256 the
        /// release published. null when it is right - held is then the open file: until it is closed nobody can write
        /// to the file, replace or delete it, so what was checked is what is started. Else what is wrong.</summary>
        public static UpdateProblem VerifyInstaller(string file, string sha256, out FileStream held)
        {
            held = null;
            if (!IsSha256(sha256)) return NotInstallable();
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            UpdateProblem problem;
            try { problem = InstallerProblem(stream, sha256); }
            catch (Exception)
            {
                stream.Dispose();
                throw;
            }
            if (problem != null) stream.Dispose();
            else held = stream;
            return problem;
        }

        static UpdateProblem InstallerProblem(FileStream stream, string sha256)
        {
            if (stream.Length < MinInstallerBytes) return new UpdateProblem("the downloaded installer is incomplete", "\u4e0b\u8f7d\u7684\u5b89\u88c5\u7a0b\u5e8f\u4e0d\u5b8c\u6574");
            if (stream.ReadByte() != 0x4D || stream.ReadByte() != 0x5A)
                return new UpdateProblem("the downloaded file is not a Windows program", "\u4e0b\u8f7d\u7684\u6587\u4ef6\u4e0d\u662f Windows \u7a0b\u5e8f");
            stream.Position = 0;
            string actual, wanted = sha256.ToLowerInvariant();
            using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (actual != wanted)
                return new UpdateProblem("the downloaded installer does not have the SHA-256 the release publishes (expected " + wanted.Substring(0, 12) +
                                         "..., got " + actual.Substring(0, 12) + "...): it was not started",
                                         "\u4e0b\u8f7d\u7684\u5b89\u88c5\u7a0b\u5e8f\u4e0e\u53d1\u5e03\u9875\u516c\u5e03\u7684 SHA-256 \u4e0d\u4e00\u81f4\uff08\u5e94\u4e3a " + wanted.Substring(0, 12) + "...\uff0c\u5b9e\u9645 " +
                                         actual.Substring(0, 12) + "...\uff09\uff0c\u6ca1\u6709\u8fd0\u884c");
            return null;
        }

        /// <summary>Downloads the installer of an update this program's own check found, checks it and starts it silently
        /// (/S /RELAUNCH, and /ANALYSISONLY for the analysis programs). null when the installer runs; else what went
        /// wrong. Never throws; onProgress (0-100) is called on a thread of the pool.</summary>
        public static Task<UpdateProblem> DownloadAndInstallAsync(string appName, UpdateResult update, bool analysisOnly, Action<int> onProgress,
                                                                  CancellationToken cancel = default(CancellationToken))
        {
            return Task.Run(() =>
            {
                try { return DownloadAndInstall(appName, update, analysisOnly, onProgress, cancel); }
                catch (Exception error) { return ProblemOf(error); }
            });
        }

        static UpdateProblem DownloadAndInstall(string appName, UpdateResult update, bool analysisOnly, Action<int> onProgress, CancellationToken cancel)
        {
            // only what the check found, looked at once more: this program's installer at GitHub, with its SHA-256
            if (update == null || !update.CanInstall || !IsSha256(update.Sha256) || !TrustedAsset(update.AssetUrl, RepoFor(appName, analysisOnly), out Uri address))
                return NotInstallable();
            string folder = null;
            bool started = false;
            try
            {
                folder = NewUpdateFolder(appName);
                string file = Path.Combine(folder, SetupAssetPrefix(appName, analysisOnly));
                var slow = new UpdateProblem("the download took longer than " + DownloadSeconds / 60 + " minutes", "\u4e0b\u8f7d\u8d85\u8fc7\u4e86 " + DownloadSeconds / 60 + " \u5206\u949f");
                Timed(DownloadSeconds, DownloadIdleSeconds, slow, cancel, limit => Download(address, file, onProgress, limit));
                UpdateProblem problem = VerifyInstaller(file, update.Sha256, out FileStream held);
                if (problem != null) return problem;
                string arguments = "/S /RELAUNCH" + (analysisOnly ? " /ANALYSISONLY" : "");
                using (held)
                {
                    if (cancel.IsCancellationRequested) return new UpdateProblem("the update was cancelled", "\u66f4\u65b0\u5df2\u53d6\u6d88");
                    using (Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })) { }
                    started = true;
                }
                Log.Info("the installer of version " + Shown(update.Version) + " was downloaded, checked (SHA-256 " + update.Sha256.Substring(0, 12).ToLowerInvariant() +
                         "...) and started: \"" + file + "\" " + arguments);
                return null;
            }
            catch (Exception error) { return ProblemOf(error); }
            finally
            {
                // a download that did not become the running installer is not left behind
                if (!started && folder != null)
                {
                    try { Directory.Delete(folder, true); } catch (Exception) { }
                }
            }
        }

        static bool Download(Uri address, string file, Action<int> onProgress, Deadline limit)
        {
            UpdaterException Large() => new UpdaterException("the installer is larger than " + (MaxInstallerBytes >> 20) + " MB: not downloaded",
                                                             "\u5b89\u88c5\u7a0b\u5e8f\u8d85\u8fc7 " + (MaxInstallerBytes >> 20) + " MB\uff0c\u6ca1\u6709\u4e0b\u8f7d");
            using (HttpWebResponse response = Open(address, true, null, limit))
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long total = response.ContentLength;       // -1: not told
                if (total > MaxInstallerBytes) throw Large();
                var buffer = new byte[81920];
                long done = 0;
                int lastPercent = -1, n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    limit.Progress();
                    done += n;
                    if (done > MaxInstallerBytes) throw Large();
                    output.Write(buffer, 0, n);
                    limit.Progress();              // (a slow disk is not a download that stalled)
                    if (total > 0 && onProgress != null)
                    {
                        int percent = (int)Math.Min(100, done * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            try { onProgress(percent); } catch (Exception) { }
                        }
                    }
                }
                if (total > 0 && done != total)
                    throw new UpdaterException("the downloaded installer is incomplete", "\u4e0b\u8f7d\u7684\u5b89\u88c5\u7a0b\u5e8f\u4e0d\u5b8c\u6574");
            }
            return true;
        }

        /// <summary>A text from outside (the tag of a release) for the log: its printable ASCII, 40 characters at most.</summary>
        static string Shown(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
                if (c >= ' ' && c <= '~' && sb.Length < 40) sb.Append(c);
            return sb.ToString();
        }
    }
}
