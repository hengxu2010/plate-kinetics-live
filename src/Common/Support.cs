// What helps people help themselves, and helps whoever supports them:
//   GET /api/health            the program, its version, the page's version, the edition, the folders, the free space,
//                              the checks of the PC (others are added by the parts that know them: Health.Checks) and
//                              whether the PC is kept awake
//   GET /api/support-bundle    a zip to send to whoever helps: the logs, versions, settings, a snapshot of the state and
//                              the checks; the experiments only when asked for (?data=1)
//   GET /api/open-help?lang=   the manual (Help\index.html beside the program); the program's window opens it itself
// and the files a second start of the program hands to the running one (POST /api/open: "openQueue" in /api/state, then
// GET /api/pk-file?id=), and the Recycle Bin.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAControl
{
    public static class Health
    {
        /// <summary>More checks of the PC, added by the parts that know them: each gives {"code", "ok", "message",
        /// "messageZh"} items.</summary>
        public static readonly List<Func<IEnumerable<Dictionary<string, object>>>> Checks = new List<Func<IEnumerable<Dictionary<string, object>>>>();

        public static Dictionary<string, object> Check(string code, bool ok, string message, string messageZh) => new Dictionary<string, object>
        {
            ["code"] = code, ["ok"] = ok, ["message"] = message, ["messageZh"] = messageZh,
        };

        /// <summary>The version of the page (its "application-version" meta tag, "v3.9.1"); null when not found.</summary>
        public static string PageVersion()
        {
            try
            {
                string page = Files.FindPage();
                using (var reader = new StreamReader(page, Encoding.UTF8))
                {
                    var head = new char[16384];
                    int n = reader.ReadBlock(head, 0, head.Length);
                    var found = Regex.Match(new string(head, 0, n), "<meta name=\"application-version\" content=\"([^\"]*)\"");
                    return found.Success ? found.Groups[1].Value : null;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>GET /api/health.</summary>
        public static Dictionary<string, object> Build(Experiments experiments)
        {
            DataLayout layout = experiments?.Layout ?? Runtime.Layout;
            var checks = new List<object>();
            long free = experiments?.Storage.FreeBytes() ?? -1;
            if (layout != null)
            {
                bool writable = CanWrite(layout.SystemDir);
                checks.Add(Check("data-folder", writable,
                    writable ? "The program can save in its data folder " + layout.Root + "." : "The program cannot save in its data folder " + layout.SystemDir + ".",
                    writable ? "\u7a0b\u5e8f\u53ef\u4ee5\u5728\u6570\u636e\u6587\u4ef6\u5939 " + layout.Root + " \u91cc\u4fdd\u5b58\u3002"      // "\u7a0b\u5e8f\u53ef\u4ee5\u5728\u6570\u636e\u6587\u4ef6\u5939 ... \u91cc\u4fdd\u5b58\u3002"
                             : "\u7a0b\u5e8f\u65e0\u6cd5\u5728\u6570\u636e\u6587\u4ef6\u5939 " + layout.SystemDir + " \u91cc\u4fdd\u5b58\u3002"));  // "\u7a0b\u5e8f\u65e0\u6cd5\u5728\u6570\u636e\u6587\u4ef6\u5939 ... \u91cc\u4fdd\u5b58\u3002"
                bool experimentsThere = Directory.Exists(layout.Experiments);
                checks.Add(Check("experiments-folder", experimentsThere,
                    experimentsThere ? "The Experiments folder is there." : "The Experiments folder " + layout.Experiments + " is missing.",
                    experimentsThere ? "Experiments \u6587\u4ef6\u5939\u6b63\u5e38\u3002"                                                // "Experiments \u6587\u4ef6\u5939\u6b63\u5e38\u3002"
                                     : "Experiments \u6587\u4ef6\u5939 " + layout.Experiments + " \u4e0d\u89c1\u4e86\u3002"));              // "... \u4e0d\u89c1\u4e86\u3002"
            }
            if (free >= 0)
            {
                string gb = (free / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);
                bool enough = free >= StorageStatus.WarnSpace;
                checks.Add(Check("disk-space", enough,
                    gb + " GB free on the data folder's disk" + (enough ? "." : ": less than 5 GB."),
                    "\u6570\u636e\u6587\u4ef6\u5939\u6240\u5728\u7684\u78c1\u76d8\u8fd8\u5269 " + gb + " GB" + (enough ? "\u3002" : "\uff0c\u4e0d\u5230 5 GB\u3002")));  // "\u6570\u636e\u6587\u4ef6\u5939\u6240\u5728\u7684\u78c1\u76d8\u8fd8\u5269 ... GB"
            }
            if (experiments != null)
            {
                var storage = experiments.Storage.State();
                bool ok = storage["ok"] is bool b && b;
                checks.Add(Check("saving", ok, ok ? "Saving works." : (string)storage["message"],
                                 ok ? "\u4fdd\u5b58\u6b63\u5e38\u3002" : (string)storage["messageZh"]));                              // "\u4fdd\u5b58\u6b63\u5e38\u3002"
            }
            string pageVersion = PageVersion();
            checks.Add(Check("page", pageVersion != null, pageVersion != null ? "The analysis page " + pageVersion + " is there." : "The analysis page is missing.",
                             pageVersion != null ? "\u5206\u6790\u9875\u9762 " + pageVersion + " \u6b63\u5e38\u3002" : "\u5206\u6790\u9875\u9762\u4e0d\u89c1\u4e86\u3002"));   // "\u5206\u6790\u9875\u9762 ... \u6b63\u5e38\u3002" / "\u5206\u6790\u9875\u9762\u4e0d\u89c1\u4e86\u3002"
            foreach (var more in Checks.ToList())
            {
                try { checks.AddRange(more().Select(c => (object)c)); }
                catch (Exception error) { Log.Warning("a check of the PC failed (" + error.Message + ")"); }
            }
            var settings = experiments?.Settings ?? Runtime.Settings;
            return new Dictionary<string, object>
            {
                ["program"] = Runtime.ProgramName, ["version"] = Experiments.ProgramVersion(), ["pageVersion"] = pageVersion,
                ["edition"] = Runtime.Edition, ["dataFolder"] = layout?.Root, ["experimentsFolder"] = layout?.Experiments,
                ["freeBytes"] = free >= 0 ? (object)free : null, ["checks"] = checks,
                ["keepAwake"] = new Dictionary<string, object> { ["enabled"] = settings == null || settings.KeepAwake, ["active"] = Problems.KeepAwakeActive },
            };
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- the support file

        /// <summary>GET /api/support-bundle?data=0|1: a zip for whoever helps. Never the API token.</summary>
        public static RawAnswer Bundle(Experiments experiments, Func<object> state, bool withData)
        {
            DataLayout layout = experiments?.Layout ?? Runtime.Layout;
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    void Text(string name, string text)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
                    }
                    void FileEntry(string name, string path)
                    {
                        try
                        {
                            byte[] bytes = SafeFile.ReadAll(path);
                            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            entry.LastWriteTime = File.GetLastWriteTime(path);
                            using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
                        }
                        catch (Exception error) { Text(name + ".unreadable.txt", error.Message); }
                    }
                    Text("README.txt", "Plate Kinetics Live support file, made " + Clock.Now() + " on " + Session.Machine() + ".\r\n" +
                                       "versions.json  the program, the page, Windows, .NET, WebView2\r\n" +
                                       "health.json    the checks of the PC (as GET /api/health)\r\n" +
                                       "state.json     what the program showed (as GET /api/state)\r\n" +
                                       "settings.json  the program's settings\r\n" +
                                       "log\\           the program's logs; users\\<user>\\ the windows' logs\r\n" +
                                       (withData ? "Experiments\\   the experiment files; records\\ the program's records of the runs\r\n"
                                                 : "No experiment data (asked for without).\r\n"));
                    Text("versions.json", Json.Serialize(Versions(), ensureAscii: false, indent: 1));
                    try { Text("health.json", Json.Serialize(Build(experiments), ensureAscii: false, indent: 1)); }
                    catch (Exception error) { Text("health.txt", error.ToString()); }
                    try { Text("state.json", Json.Serialize(state(), ensureAscii: false, indent: 1)); }
                    catch (Exception error) { Text("state.txt", error.ToString()); }
                    if (layout != null)
                    {
                        if (File.Exists(layout.Settings)) FileEntry("settings.json", layout.Settings);
                        if (File.Exists(layout.Index)) FileEntry("experiments.json", layout.Index);
                        foreach (string log in Safe(() => Directory.GetFiles(layout.Logs)))
                            FileEntry("log/" + Path.GetFileName(log), log);
                        foreach (string user in Safe(() => Directory.GetDirectories(layout.Users)))
                            foreach (string log in Safe(() => Directory.GetFiles(user, "window.log*")))
                                FileEntry("users/" + Path.GetFileName(user) + "/" + Path.GetFileName(log), log);
                        if (withData)
                        {
                            string root = layout.Experiments.TrimEnd('\\') + "\\";
                            foreach (string file in Safe(() => Directory.GetFiles(layout.Experiments, "*", SearchOption.AllDirectories)))
                                if (file.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase) || file.EndsWith(XlsxFile.Extension, StringComparison.OrdinalIgnoreCase))
                                    FileEntry("Experiments/" + file.Substring(root.Length).Replace('\\', '/'), file);
                            foreach (string record in Safe(() => Directory.GetFiles(layout.Runs, "*.json")))
                                FileEntry("records/" + Path.GetFileName(record), record);
                        }
                    }
                }
                string name = (string.IsNullOrEmpty(Runtime.AppName) ? "PlateKineticsLive" : Runtime.AppName) + "-support-" +
                              DateTime.Now.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture) + ".zip";
                return new RawAnswer(memory.ToArray(), "application/zip", name);
            }
        }

        static string[] Safe(Func<string[]> list)
        {
            try { return list(); }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>The versions of what the program runs on.</summary>
        public static Dictionary<string, object> Versions()
        {
            object Try(Func<object> get) { try { return get(); } catch (Exception) { return null; } }
            return new Dictionary<string, object>
            {
                ["program"] = Runtime.ProgramName, ["version"] = Experiments.ProgramVersion(), ["pageVersion"] = PageVersion(),
                ["edition"] = Runtime.Edition, ["windows"] = Try(() => Environment.OSVersion.VersionString), ["windowsBuild"] = Try(() => WindowsBuild()),
                ["is64Bit"] = Environment.Is64BitOperatingSystem, [".net"] = Try(() => DotNetRelease()), ["webView2"] = Try(() => WebView2Version()),
                ["pc"] = Session.Machine(), ["user"] = Session.ThisUser(), ["consoleUser"] = Session.ConsoleUser(),
                ["timeZone"] = Clock.TimeZoneId, ["now"] = Clock.Now(), ["codePage"] = Try(() => (long)Encoding.Default.CodePage),
                ["culture"] = Try(() => CultureInfo.CurrentCulture.Name), ["uiCulture"] = Try(() => CultureInfo.CurrentUICulture.Name),
                ["programFolder"] = Files.ProgramDir, ["dataFolder"] = Runtime.Layout?.Root,
            };
        }

        static object WindowsBuild()
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return key == null ? null : (key.GetValue("CurrentBuild") as string) + "." + Convert.ToString(key.GetValue("UBR"), CultureInfo.InvariantCulture);
        }

        static object DotNetRelease()
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                return key?.GetValue("Release") is int release ? (object)(long)release : null;
        }

        static object WebView2Version()
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                foreach (string path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                                                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" })
                    using (var key = hive.OpenSubKey(path))
                        if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return pv;
            return null;
        }
    }

    /// <summary>The manual: Help\index.html beside the program (both languages in one page: "?lang=zh|en" in its address
    /// picks one); else, as earlier versions had it, Guide\ beside the program, or GUIDE.md / README.md.</summary>
    public static class Help
    {
        /// <summary>The manual's file (or folder) for a language ("zh", "en"; null: either); null when there is none.</summary>
        public static string Find(string language = null) => Find(language, Files.ProgramDir);

        public static string Find(string language, string programDir)
        {
            string manual = Path.Combine(programDir, "Help", "index.html");
            if (File.Exists(manual)) return manual;
            string guide = Path.Combine(programDir, "Guide");
            if (Directory.Exists(guide))
            {
                List<string> files;
                try { files = Directory.GetFiles(guide).Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch (Exception) { files = new List<string>(); }
                bool Speaks(string file, string lang) => Regex.IsMatch(Path.GetFileNameWithoutExtension(file), "(^|[^A-Za-z])" + lang + "($|[^A-Za-z])", RegexOptions.IgnoreCase);
                string index = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("index", StringComparison.OrdinalIgnoreCase));
                string chosen = (language != null ? files.FirstOrDefault(f => Speaks(f, language)) : null)
                                ?? index
                                ?? files.FirstOrDefault(f => !Speaks(f, language == "zh" ? "en" : "zh"))
                                ?? files.FirstOrDefault();
                return chosen ?? guide;
            }
            foreach (string name in new[] { "GUIDE.md", "README.md" })
            {
                string beside = Path.Combine(programDir, name);
                if (File.Exists(beside)) return beside;
            }
            return null;
        }

        /// <summary>Opens the manual in its program (the browser, a PDF reader); false when there is none. A language
        /// ("zh", "en") opens the manual's page in it, in the default browser.</summary>
        public static bool Open(string language = null)
        {
            string lang = language == null ? null : language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh"
                        : language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : null;
            string manual = Find(lang);
            if (manual == null) return false;
            // (the shell opens files on a thread of its own kind: a request's thread is not one)
            Exception failure = null;
            var opener = new System.Threading.Thread(() =>
            {
                try
                {
                    // Windows drops "?lang=" from a file it opens by its type: the address goes to the default browser itself
                    if (lang != null && manual.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && InBrowser(new Uri(manual).AbsoluteUri + "?lang=" + lang)) return;
                    Process.Start(new ProcessStartInfo(manual) { UseShellExecute = true });
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "help" };
            opener.SetApartmentState(System.Threading.ApartmentState.STA);
            opener.Start();
            opener.Join(TimeSpan.FromSeconds(20));
            if (failure == null) return true;
            Log.Warning("the manual " + manual + " could not be opened (" + failure.Message + ")");
            return false;
        }

        /// <summary>An address in the user's default browser (the program Windows opens web addresses with); false when it
        /// is not known or does not start.</summary>
        static bool InBrowser(string url)
        {
            try
            {
                var (exe, arguments) = BrowserCommand(RegisteredBrowser(), url);
                if (exe == null || !File.Exists(exe)) return false;
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
                return true;
            }
            catch (Exception error)
            {
                Log.Warning("the default browser could not be started (" + error.Message + "); the manual is opened as a file");
                return false;
            }
        }

        /// <summary>The command line Windows opens https (else http) addresses with, for this user; null: not known.</summary>
        static string RegisteredBrowser()
        {
            foreach (string scheme in new[] { "https", "http" })
            {
                try
                {
                    using (var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice"))
                    {
                        if (!(choice?.GetValue("ProgId") is string progId) || progId.Trim().Length == 0) continue;
                        using (var command = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId.Trim() + @"\shell\open\command"))
                            if (command?.GetValue(null) is string line && line.Trim().Length > 0) return line;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>The program and its arguments for an address, from a registered command line such as
        /// "\"C:\...\msedge.exe\" --single-argument %1" (%1 or %L: the address; none: added at the end). (null, null) when
        /// the command line names no program.</summary>
        public static (string Exe, string Arguments) BrowserCommand(string registered, string url)
        {
            string line = Environment.ExpandEnvironmentVariables(registered ?? "").Trim();
            string exe, rest;
            if (line.StartsWith("\""))
            {
                int end = line.IndexOf('"', 1);
                if (end <= 1) return (null, null);
                exe = line.Substring(1, end - 1);
                rest = line.Substring(end + 1).Trim();
            }
            else
            {
                int at = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (at <= 0) return (null, null);
                exe = line.Substring(0, at + 4);
                rest = line.Substring(at + 4).Trim();
            }
            // (an address has no spaces or quotes: %20 and %22 stand for them)
            bool placed = false;
            foreach (string mark in new[] { "%1", "%L", "%l" })
                if (rest.Contains(mark)) { rest = rest.Replace(mark, url); placed = true; }
            rest = rest.Replace("%*", "").Trim();
            if (!placed) rest = (rest + " " + url).Trim();
            return (exe, rest);
        }
    }

    /// <summary>The experiment files a second start of the program handed over (POST /api/open), until the page takes
    /// them (GET /api/pk-file?id=). Kept for ten minutes.</summary>
    public sealed class OpenQueue
    {
        sealed class Entry
        {
            public string Id, Path, Name;
            public DateTime At;
        }

        readonly object gate = new object();
        readonly List<Entry> entries = new List<Entry>();
        long next;

        public string Add(string path)
        {
            lock (gate)
            {
                Expire();
                var entry = new Entry
                {
                    Id = (++next).ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6), Path = path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path), At = DateTime.UtcNow,
                };
                entries.Add(entry);
                return entry.Id;
            }
        }

        /// <summary>/api/state "openQueue": [{"id", "path", "name"}].</summary>
        public List<object> List()
        {
            lock (gate)
            {
                Expire();
                return entries.Select(e => (object)new Dictionary<string, object> { ["id"] = e.Id, ["path"] = e.Path, ["name"] = e.Name }).ToList();
            }
        }

        /// <summary>The path of a queued file, taken out of the queue; null when there is no such entry.</summary>
        public string Take(string id)
        {
            lock (gate)
            {
                Expire();
                var entry = entries.FirstOrDefault(e => e.Id == id);
                if (entry == null) return null;
                entries.Remove(entry);
                return entry.Path;
            }
        }

        void Expire() => entries.RemoveAll(e => DateTime.UtcNow - e.At > TimeSpan.FromMinutes(10));

        /// <summary>GET /api/pk-file?id=: the queued file's {"run", "analysis", "name", "path"}.</summary>
        public static Dictionary<string, object> Open(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Length > 512L << 20) throw new ApiError(400, "the file is too large to be an experiment file");
                var read = PkFile.ReadFile(path, true, true);
                return new Dictionary<string, object>
                {
                    ["run"] = read.Run, ["analysis"] = read.Analysis, ["name"] = Path.GetFileNameWithoutExtension(path), ["path"] = path,
                    ["info"] = read.Info,
                };
            }
            catch (NewerFormatException) { throw new ApiError(400, PkFile.NewerText, PkFile.NewerTextZh); }
            catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException)
            {
                throw new ApiError(400, "not a Plate Kinetics Live experiment file (" + error.Message + ")");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new ApiError(404, "the file cannot be read (" + error.Message + ")");
            }
        }
    }

    /// <summary>The Recycle Bin of the user who runs the program.</summary>
    public static class RecycleBin
    {
        /// <summary>The files to the Recycle Bin (no questions, no progress window); true when Windows did it.</summary>
        public static bool Send(IList<string> files)
        {
            if (files == null || files.Count == 0) return true;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", files.Select(Path.GetFullPath)) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            int result = SHFileOperationW(ref op);
            return result == 0 && !op.fAnyOperationsAborted && files.All(f => !File.Exists(f));
        }

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
    }
}
