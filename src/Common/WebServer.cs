// Local HTTP server: the analysis page with the shell around it, and the JSON API. A port of server.py (the same
// in both Python programs but for the name and the port): the same address (http://127.0.0.1:<port>/), routes,
// checks, page patching and answers, so the page, the shell and everything that used the Python server work as
// before. GET is served, and POST for the routes that change something; every /api/ request needs the Host check and
// the X-SAA-Token. Nothing is ever sent to an instrument.
// No client can use the server up: at most MaxConnections are served at once (one more is closed at once), a request's
// first line and headers have HeadTime and MaxHead, and a body is read only after the method, the Host and the token
// were found right (one over MaxBody: 413, and the connection ends).
//
// API: /api/state (with "storage", "problems", "events", "operator", "keepAwake", "openQueue"; each run's "analysisRev"),
// /api/run, /api/runs (each run's "analysisRev") as before (/api/run, /api/pk and /api/pk-file's "run" with the run's
// "protocolDetails", ProtocolDetails.cs); /api/experiments, /api/pk?file=, /api/open-folder?file=,
// /api/analysis?runId= (GET, POST; a POST with "baseRev" is refused with 409 "conflict" when the saved analysis is
// another revision now: see Experiments.SaveAnalysis), POST /api/analysis/revert|xlsx?runId=, /api/export-xlsx, /api/download?runId=&kind=
// (the complete .pk or .xlsx: the online view's downloads), POST /api/experiments/rename|regenerate-xlsx|recycle|restore
// (Experiments.cs); /api/settings (GET, POST),
// /api/layouts (GET, POST), POST /api/layouts/delete|import-xlsx (Settings.cs); /api/health, /api/support-bundle,
// POST /api/open, /api/pk-file?id=, /api/open-help (Support.cs); the program's own routes (BMG: /api/bmg/history,
// /api/bmg/run?key=, POST /api/bmg/save?key=) through IWatcher.Api.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SAAControl
{
    /// <summary>Everything a request needs (Python: server.Service). Watcher is null while the program is starting.</summary>
    public sealed class Service
    {
        public readonly string AppName;
        public volatile IWatcher Watcher;
        /// <summary>What every API request must carry (the page gets it in a meta tag). Set before the server starts: the
        /// host sets the one kept in the data folder (KeptToken), so a window open across a restart keeps working.</summary>
        public string Token { get; set; } = NewToken();
        public volatile bool Closing;          // the window is closed and the program is ending: a new start waits for that
        long lastRequest = Environment.TickCount;
        string pageFile;                       // the page's file, as found at the last request for the page

        public Service(string appName) { AppName = appName; }

        public void Touch() => Interlocked.Exchange(ref lastRequest, Environment.TickCount);
        public double SecondsSinceRequest => unchecked((uint)(Environment.TickCount - (int)Interlocked.Read(ref lastRequest))) / 1000.0;

        public static string NewToken()
        {
            // secrets.token_urlsafe(24): 24 random bytes, base64url without padding
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>True for a token as NewToken makes them: 32 characters of base64url.</summary>
        public static bool IsToken(string text) => text != null && text.Length == 32 &&
            text.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_');

        /// <summary>The token kept in a file (the data folder's api-token): read when the file holds one, else a new one
        /// is written there - into a temporary file first, then moved into place, so that no start reads half a token.
        /// Throws when the file can be neither read nor written.</summary>
        public static string KeptToken(string file)
        {
            string kept = ReadToken(file);
            if (kept != null) return kept;
            string token = NewToken();
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = Encoding.ASCII.GetBytes(token);
                    stream.Write(bytes, 0, bytes.Length);
                }
                if (File.Exists(file)) File.Replace(temp, file, null);     // one that holds no token (cut short, edited)
                else
                {
                    try { File.Move(temp, file); }
                    catch (IOException)
                    {
                        kept = ReadToken(file);                           // another start wrote one meanwhile
                        if (kept == null) throw;
                        return kept;
                    }
                }
                return token;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
        }

        static string ReadToken(string file)
        {
            if (!File.Exists(file)) return null;
            string text = File.ReadAllText(file).Trim();
            return IsToken(text) ? text : null;
        }

        /// <summary>The page as the window gets it; plain: the page's file as it is.</summary>
        public byte[] Page(bool plain)
        {
            try { pageFile = Files.FindPage(); }
            catch (IOException) { if (pageFile == null) throw; }   // the files are gone: the page that was read last serves
            byte[] data = Files.ReadKept(pageFile);
            return plain ? data : Encoding.UTF8.GetBytes(Files.PatchPage(Encoding.UTF8.GetString(data), Token));
        }

        /// <summary>The program runs in a user's session (portable, or the whole program with its window): it may open
        /// Explorer for the page (/api/open-folder). The Windows service may not: its window does that itself.</summary>
        public volatile bool InUserSession;

        public object Get(string path, Dictionary<string, List<string>> query) => Answer("GET", path, query);

        /// <summary>The program's experiment files (set by the host before the watcher starts; else the watcher's).</summary>
        public Experiments Experiments;

        /// <summary>The experiment files second starts handed over (POST /api/open), until the page takes them.</summary>
        public readonly OpenQueue Opened = new OpenQueue();

        // The routes of the service and their methods; any other /api/ route is the watcher's (GET, and POST for
        // /api/bmg/save). These answer while the watcher is still starting too.
        static readonly Dictionary<string, string[]> Routes = new Dictionary<string, string[]>
        {
            ["/api/state"] = new[] { "GET" }, ["/api/run"] = new[] { "GET" }, ["/api/runs"] = new[] { "GET" },
            ["/api/experiments"] = new[] { "GET" }, ["/api/pk"] = new[] { "GET" }, ["/api/open-folder"] = new[] { "GET" },
            ["/api/analysis"] = new[] { "GET", "POST" }, ["/api/analysis/revert"] = new[] { "POST" }, ["/api/analysis/xlsx"] = new[] { "POST" },
            ["/api/export-xlsx"] = new[] { "GET" }, ["/api/download"] = new[] { "GET" },
            ["/api/experiments/rename"] = new[] { "POST" }, ["/api/experiments/regenerate-xlsx"] = new[] { "POST" },
            ["/api/experiments/recycle"] = new[] { "POST" }, ["/api/experiments/restore"] = new[] { "POST" },
            ["/api/settings"] = new[] { "GET", "POST" }, ["/api/layouts"] = new[] { "GET", "POST" },
            ["/api/layouts/delete"] = new[] { "POST" }, ["/api/layouts/import-xlsx"] = new[] { "POST" },
            ["/api/support-bundle"] = new[] { "GET" }, ["/api/health"] = new[] { "GET" }, ["/api/open"] = new[] { "POST" },
            ["/api/pk-file"] = new[] { "GET" }, ["/api/open-help"] = new[] { "GET" },
        };
        static readonly HashSet<string> NeedWatcher = new HashSet<string> { "/api/state", "/api/run", "/api/runs" };

        /// <summary>The answer to an API request: a JSON value, or a RawAnswer (a file); null: not found (404); ApiError:
        /// another status. body: what a POST sent (null: nothing).</summary>
        public object Answer(string method, string path, Dictionary<string, List<string>> query, byte[] body = null)
        {
            var watcher = Watcher;
            Experiments experiments = Experiments ?? watcher?.Experiments;
            if (!Routes.TryGetValue(path, out string[] methods))
            {
                if (watcher == null) return null;      // still starting
                if ((method == "POST") != (path == "/api/bmg/save")) throw new ApiError(405, "method not allowed");
                return watcher.Api(method, path, query);
            }
            if (Array.IndexOf(methods, method) < 0) throw new ApiError(405, "method not allowed");
            if (watcher == null && NeedWatcher.Contains(path)) return null;          // still starting
            if (experiments == null && !NeedWatcher.Contains(path) && path != "/api/health" && path != "/api/support-bundle")
                return path == "/api/experiments" ? new Dictionary<string, object> { ["items"] = new List<object>(), ["removed"] = new List<object>() } : null;
            switch (path)
            {
                case "/api/state": return State(watcher, experiments);
                case "/api/run": return WithDetails(watcher.RunData(First(query, "key"), First(query, "id")), experiments, First(query, "id"));
                case "/api/runs": return WithAnalysisRevisions(watcher.Recorded(), experiments);
                case "/api/experiments": return experiments.List();
                case "/api/pk": return WithDetails(experiments.ReadRun(First(query, "file")), experiments, null);
                case "/api/open-folder": return experiments.OpenFolder(First(query, "file"), InUserSession);
                case "/api/analysis":
                    if (method == "GET") return experiments.GetAnalysis(First(query, "runId"));
                    {
                        // {"analysis": {...}, "savedBy": who saved it (the online view: "<name> (online)"; else the
                        // analysis' own savedBy, else the Windows user), "xlsx": when the workbook follows (or ?xlsx=),
                        // "baseRev": the revision the change was made on (optional; see Experiments.SaveAnalysis: 409
                        // "conflict" when the saved analysis is another revision now)}
                        var posted = Body(body);
                        if (!(posted.TryGetValue("analysis", out object a) && a is Dictionary<string, object> analysis))
                            throw new ApiError(400, "the body must be {\"analysis\": {...}}");
                        long? baseRev = BaseRev(posted);
                        string by = SavedBy(posted);
                        if (by != null) analysis = new Dictionary<string, object>(analysis) { ["savedBy"] = by };
                        return experiments.SaveAnalysis(First(query, "runId"), analysis, Session.UserOnMachine(User()),
                                                        WorkbookWhen(query, posted, Experiments.WorkbookAuto), baseRev);
                    }
                case "/api/analysis/revert":
                {
                    var posted = body != null && body.Length > 0 ? Body(body) : null;
                    return experiments.RevertAnalysis(First(query, "runId"), SavedBy(posted) ?? Session.UserOnMachine(User()),
                                                      WorkbookWhen(query, posted, Experiments.WorkbookLeave));
                }
                case "/api/analysis/xlsx":
                {
                    var posted = body != null && body.Length > 0 ? Body(body) : null;
                    return experiments.Workbook(First(query, "runId"), WorkbookWhen(query, posted, Experiments.WorkbookLeave) == Experiments.WorkbookNow);
                }
                case "/api/export-xlsx":
                {
                    string runId = First(query, "runId");
                    return experiments.ExportXlsx(runId, LiveRun(watcher, runId));
                }
                case "/api/download":
                {
                    // the experiment's complete .pk or .xlsx (the online view's downloads); read-only
                    string runId = First(query, "runId");
                    return experiments.Download(runId, First(query, "kind"), () => LiveRun(watcher, runId));
                }
                case "/api/experiments/rename": return experiments.Rename(First(query, "file"), First(query, "name"));
                case "/api/experiments/regenerate-xlsx": return experiments.RegenerateXlsx(First(query, "file"));
                case "/api/experiments/recycle": return experiments.Recycle(First(query, "file"), InUserSession);
                case "/api/experiments/restore": return experiments.Restore(First(query, "runId"));
                case "/api/settings": return method == "GET" ? experiments.Settings.Get() : experiments.Settings.Update(Body(body));
                case "/api/layouts":
                    if (method == "GET") return experiments.Layouts.List();
                    {
                        var posted = Body(body);
                        var layout = posted.TryGetValue("layout", out object l) && l is Dictionary<string, object> inner ? inner : posted;
                        return new Dictionary<string, object> { ["id"] = experiments.Layouts.Save(layout) };
                    }
                case "/api/layouts/delete":
                    return experiments.Layouts.Delete(First(query, "id")) ? new Dictionary<string, object> { ["ok"] = true } : null;
                case "/api/layouts/import-xlsx":
                    if (body == null || body.Length == 0) throw new ApiError(400, "send the workbook (.xlsx) as the body");
                    return new Dictionary<string, object> { ["layout"] = LayoutStore.ImportXlsx(body) };
                case "/api/support-bundle":
                    return Health.Bundle(experiments, () => watcher != null ? State(watcher, experiments) : new Dictionary<string, object> { ["starting"] = true },
                                         First(query, "data") == "1");
                case "/api/health": return Health.Build(experiments);
                case "/api/open":
                {
                    if (!InUserSession) throw new ApiError(409, "the background service does not open files; the program's window does");
                    var posted = Body(body);
                    string file = posted.TryGetValue("path", out object p) ? p as string : null;
                    if (string.IsNullOrWhiteSpace(file) || !file.Trim().EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase))
                        throw new ApiError(400, "not a .pk file");
                    string full;
                    try { full = Path.GetFullPath(file.Trim().Trim('"')); }
                    catch (Exception) { throw new ApiError(400, "not a path"); }
                    if (!File.Exists(full)) return null;
                    Log.Info("a second start handed over the experiment file " + full);
                    return new Dictionary<string, object> { ["id"] = Opened.Add(full) };
                }
                case "/api/pk-file":
                {
                    string file = Opened.Take(First(query, "id"));
                    if (file == null) return null;
                    Dictionary<string, object> opened = OpenQueue.Open(file);
                    string runId = opened.TryGetValue("info", out object info) && info is Dictionary<string, object> fields
                                   && fields.TryGetValue("runId", out object id) ? id as string : null;
                    opened["run"] = WithDetails(opened["run"], experiments, runId);
                    return opened;
                }
                case "/api/open-help":
                    if (!InUserSession) throw new ApiError(409, "the background service cannot open the manual; the program's window does");
                    return Help.Open(First(query, "lang") is string lang && lang.Length > 0 ? lang : null) ? new Dictionary<string, object> { ["ok"] = true } : null;
                default: return null;
            }
        }

        /// <summary>Who uses the program: the user logged on at the PC (the service), or the program's own user.</summary>
        string User() => InUserSession ? Session.ThisUser() : Session.ConsoleUser();

        /// <summary>Who saved, as a POST's body says ("savedBy": the online view sends "&lt;name&gt; (online)"); null: not said.</summary>
        static string SavedBy(Dictionary<string, object> posted) =>
            posted != null && posted.TryGetValue("savedBy", out object by) && by is string text && text.Trim().Length > 0 ? SettingsStore.Plain(text, 200) : null;

        /// <summary>The revision a save of an analysis was made on, as a POST's body says ("baseRev": a whole number, 0 = there
        /// was no saved analysis); null: not said, or null (the last save wins, as before). Anything else is refused (400):
        /// a guard that is not understood must not let the save through as an unguarded one.</summary>
        static long? BaseRev(Dictionary<string, object> posted)
        {
            if (posted == null || !posted.TryGetValue("baseRev", out object said) || said == null) return null;
            if (said is long whole && whole >= 0) return whole;
            if (said is double number && number >= 0 && number < 9e15 && Math.Floor(number) == number) return (long)number;
            throw new ApiError(400, "baseRev must be a whole number of 0 or more (the \"rev\" of the analysis the change was made on)");
        }

        /// <summary>When the workbook follows a save (?xlsx= or the body's "xlsx"): "now" (Ctrl+S; also 1, true, yes),
        /// "leave" (the experiment was left or closed; also close, flush), "auto" (the page saved by itself; also 0, false,
        /// no, later); fallback when not said.</summary>
        static string WorkbookWhen(Dictionary<string, List<string>> query, Dictionary<string, object> posted, string fallback)
        {
            object said = First(query, "xlsx");
            if ((string)said == "" && posted != null && posted.TryGetValue("xlsx", out object inBody)) said = inBody;
            if (said is bool flag) return flag ? Experiments.WorkbookNow : Experiments.WorkbookAuto;
            switch ((said as string ?? "").Trim().ToLowerInvariant())
            {
                case "now": case "1": case "true": case "yes": return Experiments.WorkbookNow;
                case "leave": case "close": case "flush": return Experiments.WorkbookLeave;
                case "auto": case "0": case "false": case "no": case "later": return Experiments.WorkbookAuto;
                default: return fallback;
            }
        }

        /// <summary>A run's data as an answer (/api/run, /api/pk, /api/pk-file's "run"): a copy with "protocolDetails" (the
        /// settings that matter to repeat the run, ProtocolDetails.cs; null when not known). null stays null (404).</summary>
        static object WithDetails(object run, Experiments experiments, string runId)
        {
            if (!(run is Dictionary<string, object> data)) return run;
            return experiments != null ? experiments.WithProtocolDetails(data, runId) : new Dictionary<string, object>(data) { ["protocolDetails"] = null };
        }

        /// <summary>/api/runs with each run's "analysisRev" (0: no saved analysis), on copies of the watcher's items.</summary>
        static object WithAnalysisRevisions(object recorded, Experiments experiments)
        {
            if (experiments == null || !(recorded is List<object> items)) return recorded;
            Dictionary<string, long> revisions = experiments.AnalysisRevisions();
            return items.Select(item =>
            {
                if (!(item is Dictionary<string, object> run) || !(run.TryGetValue("id", out object id) && id is string runId)) return item;
                return (object)new Dictionary<string, object>(run) { ["analysisRev"] = revisions.TryGetValue(runId, out long rev) ? rev : 0L };
            }).ToList();
        }

        static Dictionary<string, object> Body(byte[] body)
        {
            if (body == null || body.Length == 0) throw new ApiError(400, "the request has no body");
            Dictionary<string, object> value = null;
            try { value = Json.Parse(new UTF8Encoding(false, true).GetString(body)) as Dictionary<string, object>; }
            catch (Exception) { }
            if (value == null) throw new ApiError(400, "the body is not a JSON object");
            // NaN, Infinity and a number too large (1e999) are read as Python reads them, but they are no JSON: kept (in
            // an analysis), they would come back in answers that no browser can read
            if (!Json.Finite(value))
                throw new ApiError(400, "the body holds a number that is not a number of JSON (NaN, Infinity, or one too large such as 1e999); nothing was saved",
                                   // "The request holds a value that is not a valid number (NaN, Infinity, or one too large); nothing was saved."
                                   "\u8bf7\u6c42\u91cc\u6709\u4e0d\u662f\u6709\u6548\u6570\u5b57\u7684\u6570\u503c\uff08NaN\u3001Infinity\uff0c\u6216\u8fc7\u5927\u7684\u6570\uff09\uff0c\u6ca1\u6709\u4fdd\u5b58\u3002");
            return value;
        }

        /// <summary>The run's data now when it is the run an instrument shows (measuring or just ended); null else.</summary>
        static Dictionary<string, object> LiveRun(IWatcher watcher, string runId)
        {
            if (watcher == null || string.IsNullOrEmpty(runId)) return null;
            try
            {
                if (!(watcher.State() is Dictionary<string, object> state) || !(state.TryGetValue("instruments", out object list) && list is List<object> instruments)) return null;
                foreach (object item in instruments)
                {
                    if (!(item is Dictionary<string, object> instrument) || !(instrument.TryGetValue("run", out object r) && r is Dictionary<string, object> run)) continue;
                    if (!(run.TryGetValue("id", out object id) && id as string == runId)) continue;
                    string key = instrument.TryGetValue("key", out object k) ? k as string : null;
                    return watcher.RunData(key, runId) as Dictionary<string, object>;
                }
            }
            catch (Exception error) { Log.Warning("the live data of " + runId + " could not be had (" + error.Message + ")"); }
            return null;
        }

        /// <summary>GET /api/state: the watcher's state, and what the program adds: "storage", "problems", "events",
        /// "operator" (the user logged on), "keepAwake", "openQueue", and per instrument's run "experimentId", "status",
        /// "interruption", "operator" (when the watcher does not give them) and "analysisRev" (the revision of its saved
        /// analysis, 0: none).</summary>
        object State(IWatcher watcher, Experiments experiments)
        {
            object state = watcher.State();
            if (!(state is Dictionary<string, object> fields)) return state;
            if (experiments != null)
            {
                fields["storage"] = experiments.Storage.State();
                if (fields.TryGetValue("instruments", out object list) && list is List<object> instruments)
                {
                    var copies = new List<object>();
                    foreach (object item in instruments)
                    {
                        if (!(item is Dictionary<string, object> instrument) || !(instrument.TryGetValue("run", out object r) && r is Dictionary<string, object> run))
                        {
                            copies.Add(item);
                            continue;
                        }
                        // (copies: the watcher's own dictionaries are left as they are)
                        var copy = new Dictionary<string, object>(instrument);
                        var runCopy = new Dictionary<string, object>(run);
                        string runId = run.TryGetValue("id", out object id) ? id as string : null;
                        if (!string.IsNullOrEmpty(runId))
                        {
                            var known = experiments.Known(runId);
                            if (!runCopy.ContainsKey("experimentId")) runCopy["experimentId"] = known.ExperimentId ?? experiments.IdFor(runId);
                            if (!runCopy.ContainsKey("status"))
                                runCopy["status"] = known.Status ?? (instrument.TryGetValue("state", out object s) && s as string == "measuring" ? "measuring" : null);
                            if (!runCopy.ContainsKey("interruption")) runCopy["interruption"] = null;
                            if (!runCopy.ContainsKey("operator")) runCopy["operator"] = known.Operator;
                            runCopy["analysisRev"] = experiments.AnalysisRevision(runId);
                        }
                        copy["run"] = runCopy;
                        copies.Add(copy);
                    }
                    fields["instruments"] = copies;
                }
            }
            fields["problems"] = Problems.List();
            fields["events"] = Problems.Events();
            fields["operator"] = User();
            var settings = experiments?.Settings;
            fields["keepAwake"] = new Dictionary<string, object> { ["enabled"] = settings == null || settings.KeepAwake, ["active"] = Problems.KeepAwakeActive };
            fields["openQueue"] = Opened.List();
            return fields;
        }

        static string First(Dictionary<string, List<string>> query, string name) =>
            query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : "";
    }

    /// <summary>The program's files: the page, the shell, and what the page gets patched with (Python: server.py).</summary>
    public static class Files
    {
        // Names the shell needs from inside the page's closure. Exported by one line added before `boot();` when the
        // page is served; the file on disk is never changed.
        static readonly string[] HookNames = { "S", "C", "L", "inv", "invAll", "curDS", "newDataset", "ensureWells", "toast" };
        static readonly string[] HookProof = { "const S = {", "const C = window.PKCore;", "function inv(", "function invAll(", "const curDS",
                                               "function newDataset(", "function ensureWells(", "function toast(", "const { T: L" };
        // Names the shell uses when the page has them; `typeof` keeps the added line from failing on a page without one.
        static readonly string[] HookExtras = { "ensureAnalysis", "dataWells", "isDark", "resolveColor", "wellColorHex", "setOf",
                                                "getDatasetAnalysis", "applyDatasetAnalysis", "getDatasetResults" };
        // What the shell consists of, by URL: (file, content type).
        public static readonly Dictionary<string, KeyValuePair<string, string>> Assets = new Dictionary<string, KeyValuePair<string, string>>
        {
            ["/shell.css"] = new KeyValuePair<string, string>("web/shell.css", "text/css; charset=utf-8"),
            ["/live.js"] = new KeyValuePair<string, string>("web/live.js", "text/javascript; charset=utf-8"),
            ["/shell.js"] = new KeyValuePair<string, string>("web/shell.js", "text/javascript; charset=utf-8"),
        };
        // Added at the end of the head: after the page's own style sheet, before its script.
        static readonly string[] HeadLines = { "<link rel=\"stylesheet\" href=\"/shell.css\">", "<script src=\"/live.js\"></script>",
                                               "<script src=\"/shell.js\"></script>" };

        static readonly object Gate = new object();
        static readonly Dictionary<string, byte[]> Kept = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public static string ProgramDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        public static string HookLine()
        {
            var parts = HookNames.Concat(HookExtras.Select(n => n + ": typeof " + n + " === \"undefined\" ? null : " + n));
            return "window.PKApp = { " + string.Join(", ", parts) + " }; "
                 + "try { if (window.SAAShell) window.SAAShell.mount(window.PKApp); } catch (e) { console.error(e); }";
        }

        /// <summary>The content of a file of the program; when it cannot be read, what it held at the last read.</summary>
        public static byte[] ReadKept(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                lock (Gate) Kept[path] = data;
                return data;
            }
            catch (IOException) { lock (Gate) if (Kept.TryGetValue(path, out var old)) return old; throw; }
            catch (UnauthorizedAccessException) { lock (Gate) if (Kept.TryGetValue(path, out var old)) return old; throw; }
        }

        /// <summary>A file of the program (Python: resource): web/... beside the program.</summary>
        public static string Resource(string name) => Path.Combine(ProgramDir, name.Replace('/', '\\'));

        /// <summary>The analysis page: a copy the user put beside the program wins over the installed one.</summary>
        public static string FindPage()
        {
            var beside = new DirectoryInfo(ProgramDir).GetFiles("PlateKineticsAnalysis*.html")
                .Where(f => f.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            if (beside.Count > 0) return beside[beside.Count - 1].FullName;
            var web = new DirectoryInfo(Resource("web"));
            var bundled = web.Exists ? web.GetFiles("PlateKineticsAnalysis*.html")
                .Where(f => f.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList() : new List<string>();
            if (bundled.Count == 0) throw new FileNotFoundException("PlateKineticsAnalysis*.html");
            return bundled[bundled.Count - 1];
        }

        /// <summary>Add the token, the shell (style sheet and scripts) and the closure hook (only when this is a page we
        /// recognise; without it the shell shows the instruments above the page and leaves the page as it is).</summary>
        public static string PatchPage(string html, string token)
        {
            var lines = html.Split('\n').ToList();
            var stripped = lines.Select(PyStrip).ToList();
            if (stripped.Count(s => s == "boot();") == 1 && HookProof.All(p => html.Contains(p)))
            {
                int at = stripped.IndexOf("boot();");
                lines.Insert(at, HookLine());
                stripped.Insert(at, "");
            }
            int head = stripped.IndexOf("</head>");
            if (head >= 0)
            {
                lines.InsertRange(head, HeadLines);
                stripped.InsertRange(head, HeadLines.Select(_ => ""));
            }
            int open = stripped.IndexOf("<head>");
            if (open >= 0) lines.Insert(open + 1, "<meta name=\"saa-token\" content=\"" + token + "\">");
            return string.Join("\n", lines);
        }

        // Python's str.strip(): the whitespace characters of str.isspace()
            static readonly char[] PySpace = { (char)0x0020, (char)0x0009, (char)0x000a, (char)0x000d, (char)0x000b, (char)0x000c, (char)0x001c, (char)0x001d, (char)0x001e, (char)0x001f, (char)0x0085, (char)0x00a0, (char)0x1680, (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004, (char)0x2005, (char)0x2006, (char)0x2007, (char)0x2008, (char)0x2009, (char)0x200a, (char)0x2028, (char)0x2029, (char)0x202f, (char)0x205f, (char)0x3000 };
        static string PyStrip(string s) => s.Trim(PySpace);
    }

    /// <summary>The HTTP server on 127.0.0.1:port (Python: make_server / _Server / Handler). The port is bound for this
    /// process alone (exclusive address use), so the port also says that an instance is running.</summary>
    public sealed class WebServer
    {
        readonly Service service;
        readonly int port;
        readonly HashSet<string> hosts;
        TcpListener listener;
        Thread acceptThread;
        volatile bool stopping;
        int active;                              // connections being served now (each has a thread of its own)
        static readonly Dictionary<string, long> Logged = new Dictionary<string, long>();
        const long MaxBody = 64L << 20;          // a POST's body: an analysis, a workbook to import
        const int IdleTime = 120000;             // ms a connection waits for its next request, and for more of a body
        const int LeaveTime = 2000, LeaveBytes = 1 << 20;   // what is still read of a body that is not wanted (see Leave)

        /// <summary>What keeps one client from using the server up (tests change them): the connections served at once
        /// (the window, the tray and the online view together use a handful), and the size and the time a request's first
        /// line and headers may take.</summary>
        public static int MaxConnections = 64, MaxHead = 32 << 10;
        public static TimeSpan HeadTime = TimeSpan.FromSeconds(15);

        public WebServer(Service service, int port)
        {
            this.service = service;
            this.port = port;
            hosts = new HashSet<string> { "127.0.0.1:" + port, "localhost:" + port };
        }

        /// <summary>Bind the port; throws SocketException when it is taken (another instance, or one that is ending).</summary>
        public void Bind()
        {
            var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            l.Start();
            // .NET Framework sockets are inheritable: a child process (the browser fallback, the web control's processes)
            // would keep the port bound but silent after this program ends, and every later start would wait in vain
            try { SetHandleInformation(l.Server.Handle, HandleFlagInherit, 0); } catch (Exception) { }
            listener = l;
        }

        const uint HandleFlagInherit = 1;
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        public void Start()
        {
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "http" };
            acceptThread.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { listener?.Stop(); } catch (Exception) { }
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { if (stopping) return; Thread.Sleep(50); continue; }
                // Nothing here may end this thread - it is the server - nor the program: a connection that cannot be
                // served (too many at once, no memory for one more thread) is closed, and the next one is waited for.
                try
                {
                    if (Interlocked.Increment(ref active) > MaxConnections)
                    {
                        Interlocked.Decrement(ref active);
                        client.Close();
                        if (Rarely("connections")) Log.Warning("the local server serves " + MaxConnections + " connections at once: one more was closed");
                        continue;
                    }
                    bool started = false;
                    try
                    {
                        var t = new Thread(() => Serve(client)) { IsBackground = true, Name = "http-client" };
                        t.Start();
                        started = true;                // (from here on Serve gives the place back)
                    }
                    finally
                    {
                        if (!started)
                        {
                            Interlocked.Decrement(ref active);
                            try { client.Close(); } catch (Exception) { }
                        }
                    }
                }
                catch (Exception error)
                {
                    try { LogFailure("connection", "127.0.0.1", error); } catch (Exception) { }
                }
            }
        }

        /// <summary>True once in ten minutes for a key: what is logged with it would else fill the log.</summary>
        static bool Rarely(string key)
        {
            long now = Environment.TickCount;
            lock (Logged)
            {
                if (Logged.TryGetValue(key, out long last) && unchecked((uint)(now - last)) < 600000) return false;
                Logged[key] = now;
            }
            return true;
        }

        /// <summary>A request that failed, to the log: the same failure once in ten minutes (the page asks every two seconds).</summary>
        static void LogFailure(string method, string path, Exception error)
        {
            if (Rarely(method + " " + path + " " + error.GetType().Name)) Log.Error(method + " " + path + " failed", error);
        }

        sealed class Request
        {
            public string Method, Target, Path, Query;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool KeepAlive;
            public long Length;                  // the Content-Length (0: none)
            public byte[] Body;                  // what a POST sent (null: nothing, or not read)
            public bool TooLarge;                // it would send more than MaxBody
            public bool Unread;                  // it has a body that was not read: the connection ends with the answer
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = IdleTime;
                        client.SendTimeout = 60000;
                        client.NoDelay = true;
                        var stream = client.GetStream();
                        var reader = new BufferedReader(stream);
                        bool used = false;
                        while (!stopping)
                        {
                            // a connection that was used waits for its next request as long as before; the request itself -
                            // its first line and headers - has HeadTime and MaxHead, whoever sends it
                            if (used && !reader.Wait(IdleTime)) return;
                            reader.HeadBegins((int)HeadTime.TotalMilliseconds, MaxHead);
                            Request req = ReadHead(reader);
                            reader.HeadEnds(IdleTime);
                            if (req == null) return;
                            used = true;
                            if (req.Length > 0)
                            {
                                // the method, the Host and the token first: only a body that will be used is read
                                if (!WantsBody(req)) req.Unread = true;
                                else if (req.Length > MaxBody) req.TooLarge = req.Unread = true;
                                else req.Body = reader.Read((int)req.Length);
                            }
                            bool keep = Answer(stream, req);
                            if (req.Unread)
                            {
                                Leave(client, stream);
                                return;
                            }
                            if (!keep || !req.KeepAlive) return;
                        }
                    }
                    catch (IOException) { }            // the window was closed while the answer was on its way
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    catch (Exception e) { LogFailure("request", "127.0.0.1", e); }
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }

        /// <summary>Whether a request's body will be used: a POST to the API with the right Host and token (Answer makes
        /// the same checks again, in its order, for what it answers).</summary>
        bool WantsBody(Request req) =>
            req.Method == "POST" && req.Path.StartsWith("/api/") && req.Path != "/api/ping" && Allowed(req, true);

        /// <summary>The end of a connection whose request had a body that was not read. Closed at once, with the body still
        /// on its way, Windows resets the connection and the client may lose the answer that was just sent. So the sending
        /// side is closed first, and what still comes in is read and dropped for a moment: a little, never the whole of
        /// a large body.</summary>
        static void Leave(TcpClient client, Stream stream)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
                var dropped = new byte[8192];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long total = 0;
                while (total < LeaveBytes)
                {
                    long left = LeaveTime - clock.ElapsedMilliseconds;
                    if (left <= 0) break;
                    stream.ReadTimeout = (int)left;
                    int count = stream.Read(dropped, 0, dropped.Length);
                    if (count <= 0) break;
                    total += count;
                }
            }
            catch (Exception) { }
        }

        /// <summary>A request's first line and headers (its body is read by Serve, when it is wanted); null: none came, or
        /// not a request.</summary>
        static Request ReadHead(BufferedReader reader)
        {
            string line = reader.ReadLine();
            while (line != null && line.Length == 0) line = reader.ReadLine();   // tolerate stray blank lines
            if (line == null) return null;
            var parts = line.Split(' ');
            if (parts.Length < 2) return null;
            var req = new Request { Method = parts[0], Target = parts[1] };
            string version = parts.Length > 2 ? parts[2] : "HTTP/0.9";
            while (true)
            {
                string h = reader.ReadLine();
                if (h == null) return null;
                if (h.Length == 0) break;
                int colon = h.IndexOf(':');
                if (colon > 0) req.Headers[h.Substring(0, colon).Trim()] = h.Substring(colon + 1).Trim();
            }
            if (req.Headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out long n) && n > 0) req.Length = n;
            req.Headers.TryGetValue("Connection", out var conn);
            conn = (conn ?? "").ToLowerInvariant();
            req.KeepAlive = version == "HTTP/1.1" ? conn != "close" : conn == "keep-alive";
            string target = req.Target;
            int hash = target.IndexOf('#');
            if (hash >= 0) target = target.Substring(0, hash);
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            // urlsplit(...).path keeps a full URL's path only
            if (req.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = req.Path.IndexOf('/', 7);
                req.Path = slash >= 0 ? req.Path.Substring(slash) : "/";
            }
            return req;
        }

        /// <summary>Answer one request; false when the connection cannot go on.</summary>
        bool Answer(Stream stream, Request req)
        {
            bool begun = false;
            try
            {
                // GET, and POST for the one API route that changes something (/api/bmg/save: it writes experiment files)
                if (req.Method != "GET" && !(req.Method == "POST" && req.Path.StartsWith("/api/") && req.Path != "/api/ping"))
                {
                    begun = true;
                    Send(stream, 501, Encoding.UTF8.GetBytes("Unsupported method ('" + req.Method + "')"), "text/html;charset=utf-8");
                    return true;
                }
                service.Touch();
                if (req.Path == "/api/ping")        // lets a second start find the running program
                {
                    begun = true;
                    SendJson(stream, new Dictionary<string, object> { ["app"] = service.AppName, ["closing"] = service.Closing }, 200);
                    return true;
                }
                if (!Allowed(req, req.Path.StartsWith("/api/")))
                {
                    begun = true;
                    SendJson(stream, new Dictionary<string, object> { ["error"] = "forbidden" }, 403);
                    return true;
                }
                if (req.Path == "/" || req.Path == "/index.html")
                {
                    // The page's "copy for offline use" fetches its own address again and saves what comes. That
                    // request is made by script (Sec-Fetch-Dest: empty; the window itself asks for a document) and
                    // gets the page's file as it is: no shell, no token.
                    bool plain = req.Headers.TryGetValue("Sec-Fetch-Dest", out var dest) && dest == "empty";
                    byte[] page = service.Page(plain);
                    begun = true;
                    Send(stream, 200, page, "text/html; charset=utf-8");
                    return true;
                }
                if (Files.Assets.TryGetValue(req.Path, out var asset))
                {
                    byte[] body = Files.ReadKept(Files.Resource(asset.Key));
                    begun = true;
                    Send(stream, 200, body, asset.Value);
                    return true;
                }
                if (req.Path.StartsWith("/api/"))
                {
                    object value;
                    if (req.TooLarge)
                    {
                        begun = true;
                        SendJson(stream, new Dictionary<string, object> { ["error"] = "the request is too large" }, 413);
                        return false;
                    }
                    try { value = service.Answer(req.Method, req.Path, ParseQuery(req.Query), req.Body); }
                    catch (ApiError refused)
                    {
                        begun = true;
                        var said = new Dictionary<string, object> { ["error"] = refused.Message };
                        if (refused.MessageZh != null) said["errorZh"] = refused.MessageZh;
                        if (refused.Fields != null)
                            foreach (var field in refused.Fields)
                                if (!said.ContainsKey(field.Key)) said[field.Key] = field.Value;
                        SendJson(stream, said, refused.Status);
                        return true;
                    }
                    begun = true;
                    if (value == null) SendJson(stream, new Dictionary<string, object> { ["error"] = "not found" }, 404);
                    else if (value is RawAnswer file) Send(stream, 200, file.Body, file.ContentType, Disposition(file.FileName));
                    else SendJson(stream, value, 200);
                    return true;
                }
                begun = true;
                SendJson(stream, new Dictionary<string, object> { ["error"] = "not found" }, 404);
                return true;
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
            catch (Exception error)
            {
                LogFailure(req.Method, req.Path, error);
                if (begun) return false;          // half an answer is out: nothing sensible can follow on this connection
                try { SendJson(stream, new Dictionary<string, object> { ["error"] = "internal error" }, 500); return true; }
                catch (Exception) { return false; }
            }
        }

        bool Allowed(Request req, bool api)
        {
            req.Headers.TryGetValue("Host", out var host);
            if (host == null || !hosts.Contains(host)) return false;
            if (!api) return true;
            req.Headers.TryGetValue("X-SAA-Token", out var token);
            return FixedTimeEquals(token ?? "", service.Token);
        }

        static bool FixedTimeEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a);
            var y = Encoding.UTF8.GetBytes(b);
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        // (allowNan: false - a NaN or an infinite number, which a record of an earlier version may hold, is sent as null:
        // a missing value for the page, instead of an answer that no browser can read)
        static void SendJson(Stream stream, object value, int status) =>
            Send(stream, status, Encoding.UTF8.GetBytes(Json.Serialize(value, ensureAscii: false, allowNan: false)), "application/json; charset=utf-8");

        static readonly Dictionary<int, string> Reasons = new Dictionary<int, string>
        {
            [200] = "OK", [400] = "Bad Request", [403] = "Forbidden", [404] = "Not Found", [405] = "Method Not Allowed",
            [409] = "Conflict", [413] = "Payload Too Large", [500] = "Internal Server Error", [501] = "Not Implemented",
        };

        /// <summary>Content-Disposition for a file to save: an ASCII name for old clients, the real one (RFC 5987) for the rest.</summary>
        static string Disposition(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var ascii = new StringBuilder();
            foreach (char c in fileName) ascii.Append(c >= 0x20 && c < 0x7f && c != '"' && c != '\\' ? c : '_');
            var encoded = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(fileName))
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || "-._~".IndexOf(c) >= 0) encoded.Append(c);
                else encoded.Append('%').Append(b.ToString("X2"));
            }
            return "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + encoded;
        }

        static void Send(Stream stream, int status, byte[] body, string kind, string disposition = null)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reasons.TryGetValue(status, out var r) ? r : "").Append("\r\n");
            if (disposition != null) head.Append("Content-Disposition: ").Append(disposition).Append("\r\n");
            head.Append("Server: PlateKineticsLive\r\n");
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Content-Type: ").Append(kind).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("X-Frame-Options: DENY\r\n");          // no other page may show this one in a frame
            head.Append("\r\n");
            byte[] h = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(h, 0, h.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>urllib.parse.parse_qs: name -> values; '+' is a space; empty values are left out.</summary>
        public static Dictionary<string, List<string>> ParseQuery(string query)
        {
            var result = new Dictionary<string, List<string>>();
            if (string.IsNullOrEmpty(query)) return result;
            foreach (string part in query.Split('&', ';'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq < 0) continue;                                   // parse_qs drops a name without '='
                string name = Unquote(part.Substring(0, eq)), value = Unquote(part.Substring(eq + 1));
                if (value.Length == 0) continue;                        // keep_blank_values=False
                if (!result.TryGetValue(name, out var list)) result[name] = list = new List<string>();
                list.Add(value);
            }
            return result;
        }

        static string Unquote(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        /// <summary>Reads lines (ASCII, CRLF or LF) and bodies on a network stream, without reading ahead past a request.
        /// Between HeadBegins and HeadEnds - a request's first line and headers - all that is read must come within a
        /// time and a size, or the reading fails (IOException) and the connection ends.</summary>
        sealed class BufferedReader
        {
            readonly Stream s;
            readonly byte[] buf = new byte[8192];
            int pos, len;
            readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            long headUntil = -1;                 // on the clock: when the head being read must be complete (-1: none is read)
            int headLeft;                        // the bytes it may still have
            public BufferedReader(Stream s) { this.s = s; }

            public void HeadBegins(int milliseconds, int most)
            {
                headUntil = clock.ElapsedMilliseconds + milliseconds;
                headLeft = most;
            }

            /// <summary>The head is read: from now on each read waits up to `timeout` ms again.</summary>
            public void HeadEnds(int timeout)
            {
                headUntil = -1;
                s.ReadTimeout = timeout;
            }

            /// <summary>Wait up to `timeout` ms for what comes next; false when the connection ended instead.</summary>
            public bool Wait(int timeout)
            {
                s.ReadTimeout = timeout;
                return Fill();
            }

            bool Fill()
            {
                if (pos < len) return true;
                if (headUntil >= 0)
                {
                    // (the time is for the whole head, not for each read: a client that sends a byte now and then
                    // does not keep its connection)
                    long left = headUntil - clock.ElapsedMilliseconds;
                    if (left <= 0) throw new IOException("the request's first line and headers took too long");
                    s.ReadTimeout = (int)left;
                }
                len = s.Read(buf, 0, buf.Length);
                pos = 0;
                return len > 0;
            }

            public string ReadLine()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (!Fill()) return sb.Length > 0 ? sb.ToString() : null;
                    byte b = buf[pos++];
                    if (headUntil >= 0 && --headLeft < 0) throw new IOException("the request's first line and headers are too long");
                    if (b == (byte)'\n') { if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--; return sb.ToString(); }
                    sb.Append((char)b);
                    if (sb.Length > 65536) throw new IOException("header line too long");
                }
            }

            /// <summary>The next n bytes (fewer when the connection ends first).</summary>
            public byte[] Read(int n)
            {
                var data = new byte[n];
                int at = 0;
                while (at < n)
                {
                    if (!Fill()) break;
                    int take = Math.Min(n - at, len - pos);
                    Buffer.BlockCopy(buf, pos, data, at, take);
                    pos += take;
                    at += take;
                }
                if (at < n) Array.Resize(ref data, at);
                return data;
            }
        }

        /// <summary>Python: other_instance(port). "running", "closing" (its window is closed and it is ending) or "" for none.</summary>
        public static string OtherInstance(int port, string appName)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/ping");
                req.Timeout = 2000;
                req.ReadWriteTimeout = 2000;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if (!(Json.Parse(rd.ReadToEnd()) is Dictionary<string, object> answer)) return "";
                    if (!answer.TryGetValue("app", out var app) || !(app is string a) || a != appName) return "";
                    return answer.TryGetValue("closing", out var c) && c is bool closing && closing ? "closing" : "running";
                }
            }
            catch (Exception) { return ""; }
        }
    }
}
