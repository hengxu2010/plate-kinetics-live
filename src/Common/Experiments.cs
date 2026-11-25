// The experiment files of a program: <data folder>\Experiments\<yyyy-MM>\<name>.pk and <name>.xlsx for every run
// (see Naming.cs, PkFile.cs, XlsxFile.cs), saved power-safely (Storage.cs). The .pk is written after every completed
// cycle while a run is measuring and once at its end (then its run data are sealed: later writes may only add cycles
// that were missing), the .xlsx at the end and after an analysis is saved.
//
// What the program keeps about the files of each run is in System\experiments.json ({"format":
// "plate-kinetics-live-index/1", "runs": {"<run id>": {"experimentId", "name" (a name the user gave), "operator",
// "sealedAt", "status", "file", "xlsx", "xlsxSha256", "title", "startedAt", "instrument", "key", "analysisRev",
// "xlsxRev"}}}): the experiment's id (made once per run, kept across renames), the name a user gave it, and the checksum
// of the .xlsx as written (a workbook that differs, or that is open in Excel, is never overwritten: "<name> (analysis
// yyyy-MM-dd HH-mm).xlsx" is written instead). A copy of every saved analysis is kept in System\analysis\<run id>.json.
//
// The analysis of a run is shared: the window on the PC and signed-in editors of the online view save it (the last save
// wins). Every save that changes it gets the next revision ("rev": 1, 2, ...; never used twice; /api/runs and /api/state
// tell it as "analysisRev"), and keeps the version it replaces in System\analysis\<run id>.prev.json ("Undo last save"
// puts that back - as a new revision). The page saves by itself a few seconds after each change; the .xlsx follows on
// Ctrl+S (made now), when the experiment is left, and otherwise at most every 10 minutes ("xlsxRev": the revision the
// workbook was made with; one behind is made again later, also after a restart).
//
// A run's files are found by the run id in their info.json, not by their names: the folder is scanned at the start, and
// again when a file was moved or renamed. Runs whose files were made whole once (the .pk with the run's end, and the
// .xlsx) are listed in System\runs\pk-made.txt: files a user deleted are not made again for an ended run (they are
// listed as "removed" and can be restored). The background preparation of the records makes what such a run lacks.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SAAControl
{
    /// <summary>The filters and gain of one channel of a run.</summary>
    public sealed class ChannelInfo
    {
        public string Name;
        public object Excitation, Emission, Gain;
    }

    /// <summary>What the .pk and the .xlsx of a run hold, built from the program's record of the run.</summary>
    public sealed class ExperimentInfo
    {
        public string RunId;
        public string ExperimentId;            // the record's "experimentId"; else the program's list of files gives it
        public string Key;                     // the instrument's key (BMG: Omega's folder, "Omega2"; Archimed: "Archimed")
        public string Name;                    // the experiment's name (BMG: typed ID1, else the protocol; Archimed: the file)
        public string Model = "", Serial = "";
        public string Software = "";           // "Omega 6.40.101.9": the instrument software's name and version
        public string SoftwareName = "", SoftwareVersion = "";
        public string Protocol = "";
        public string StartedAt, EndedAt;      // "yyyy-MM-ddTHH:mm:ss" (local time, as the records have it); EndedAt null while measuring
        public string Status;                  // measuring | completed | stopped | interrupted | unknown
        public long Done, Planned;
        public object TargetTemperature;
        public List<ChannelInfo> Channels = new List<ChannelInfo>();
        public Dictionary<string, object> Run; // run.json: what GET /api/run returns for the run
        public List<object> Events = new List<object>();
        public Dictionary<string, object> Interruption;   // why it ended early: {"at", "reason", "detail"}; null: not known
        public string Operator;                // who ran it
        // set when the files are written (for the workbook)
        public string RunSha256, FileName;
        // from the record (ProtocolDetails.cs): the settings that matter to repeat the run, and Omega's settings as stored
        // (without the user accounts); null when the record has none
        public Dictionary<string, object> ProtocolDetails;
        public List<KeyValuePair<string, object>> OmegaValues;

        public static readonly string[] States = { "measuring", "completed", "stopped", "interrupted", "unknown" };

        public ExperimentInfo Clone() => (ExperimentInfo)MemberwiseClone();
        public static readonly string[] FinalStates = { "completed", "stopped", "interrupted", "unknown" };
        public static bool IsFinal(object status) => status is string s && Array.IndexOf(FinalStates, s) >= 0;

        /// <summary>A run that ended: completed when it has all its planned cycles (or the plan is not known), else stopped.</summary>
        public static string EndStatus(long done, long planned) => planned > 0 && done < planned ? "stopped" : "completed";

        static object Get(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;

        static string Text(object value) => value is string s ? s : value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

        public static long Number(object value)
        {
            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d): return (long)d;
                default: return 0;
            }
        }

        /// <summary>The start plus the time of the run's last reading, as "yyyy-MM-ddTHH:mm:ss"; null when not known.</summary>
        public static string LastReading(string startedAt, object seconds)
        {
            DateTime? start = Naming.Start(startedAt);
            if (start == null || !(seconds is List<object> list)) return null;
            double last = -1;
            foreach (object value in list)
            {
                double s = value is long l ? l : value is int i ? i : value is double d ? d : -1;
                if (!double.IsNaN(s) && s > last) last = s;
            }
            if (last < 0) return null;
            return start.Value.AddSeconds(Math.Min(last, 1e9)).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
        }

        /// <summary>The status in words: "Stopped early at cycle 98 of 300" / "\u63d0\u524d\u505c\u6b62\uff08\u7b2c 98/300 \u8f6e\uff09".</summary>
        public static (string En, string Zh) StatusText(string status, long done, long planned, Dictionary<string, object> interruption = null,
                                                         string startedAt = null)
        {
            string of = planned > 0 ? done + " of " + planned : done.ToString(CultureInfo.InvariantCulture);
            string ofZh = planned > 0 ? done + "/" + planned : done.ToString(CultureInfo.InvariantCulture);
            switch (status)
            {
                case "measuring":
                    // "\u6d4b\u91cf\u4e2d\uff08\u7b2c n/m \u8f6e\uff09"
                    return ("Measuring: cycle " + of, "\u6d4b\u91cf\u4e2d\uff08\u7b2c " + ofZh + " \u8f6e\uff09");
                case "completed":
                    // "\u5df2\u5b8c\u6210\uff08n \u8f6e\uff09"
                    return ("Completed: " + done + " cycles", "\u5df2\u5b8c\u6210\uff08" + done + " \u8f6e\uff09");
                case "stopped":
                    // "\u63d0\u524d\u505c\u6b62\uff08\u7b2c n/m \u8f6e\uff09"
                    return ("Stopped early at cycle " + of, "\u63d0\u524d\u505c\u6b62\uff08\u7b2c " + ofZh + " \u8f6e\uff09");
                case "interrupted":
                {
                    var (why, whyZh) = Interruptions.Text(interruption, startedAt);
                    // "\u4e2d\u65ad\uff08\u7b2c n/m \u8f6e\uff09"
                    string en = "Interrupted at cycle " + of, zh = "\u4e2d\u65ad\uff08\u7b2c " + ofZh + " \u8f6e\uff09";
                    return why != null ? (en + ": " + why, zh + "\uff1a" + whyZh) : (en, zh);
                }
                default:
                    // "\u6ca1\u770b\u5230\u7ed3\u675f"
                    return ("End not seen", "\u6ca1\u770b\u5230\u7ed3\u675f");
            }
        }

        /// <summary>The description of a run from the program's record of it ({"summary", "data", "label", and from 2.2:
        /// "status", "endedAt", "experiment", "instrument": {"key", "model", "serial", "software"}, "settings", "events",
        /// "experimentId", "operator", "interruption"}). softwareName: the instrument's control software ("Omega",
        /// "Archimed"). null when the record is not one.</summary>
        public static ExperimentInfo FromRecord(Dictionary<string, object> record, string softwareName)
        {
            if (record == null || !(Get(record, "summary") is Dictionary<string, object> summary)
                || !(Get(record, "data") is Dictionary<string, object> data)) return null;
            var info = new ExperimentInfo { Run = data };
            info.RunId = Text(Get(summary, "id")) ?? Text(Get(data, "id"));
            if (string.IsNullOrEmpty(info.RunId)) return null;
            info.Protocol = Text(Get(summary, "protocol")) ?? "";
            string experiment = Text(Get(record, "experiment"));
            info.Name = !string.IsNullOrWhiteSpace(experiment) ? experiment : info.Protocol.Length > 0 ? info.Protocol : "Experiment";
            var instrument = Get(record, "instrument") as Dictionary<string, object>;
            info.Key = Text(Get(instrument, "key")) ?? Text(Get(data, "key"));
            info.Model = Text(Get(instrument, "model")) ?? "";
            info.Serial = Text(Get(instrument, "serial")) ?? "";
            string label = Text(Get(record, "label")) ?? "";
            int dot = label.IndexOf(" \u00b7 ", StringComparison.Ordinal);
            if (info.Serial.Length == 0 && dot >= 0) info.Serial = label.Substring(dot + 3).Trim();
            string software = Text(Get(instrument, "software")) ?? "";
            info.Software = software.Length == 0 ? softwareName ?? ""
                : softwareName != null && !software.StartsWith(softwareName, StringComparison.OrdinalIgnoreCase) ? softwareName + " " + software : software;
            info.SoftwareName = softwareName ?? "";
            info.SoftwareVersion = softwareName != null && software.StartsWith(softwareName, StringComparison.OrdinalIgnoreCase)
                ? software.Substring(softwareName.Length).Trim() : software;
            info.StartedAt = Text(Get(summary, "startedAt"));
            info.Done = Number(Get(summary, "cyclesComplete") ?? Get(data, "cyclesComplete"));
            info.Planned = Math.Max(Number(Get(summary, "plannedCycles")), Number(Get(data, "plannedCycles")));
            object status = Get(record, "status");
            info.Status = status is string s && s.Length > 0 ? s : EndStatus(info.Done, info.Planned);    // a record of before 2.2: it has ended
            info.EndedAt = Text(Get(record, "endedAt"));
            if (info.EndedAt == null && info.Status != "measuring") info.EndedAt = LastReading(info.StartedAt, Get(data, "seconds"));
            info.TargetTemperature = Get(summary, "targetTemperature");
            info.ExperimentId = Text(Get(record, "experimentId"));
            info.Operator = Text(Get(record, "operator"));
            info.Interruption = Get(record, "interruption") as Dictionary<string, object>;
            var settings = Get(record, "settings") as Dictionary<string, object>;
            bool first = true;
            if (Get(data, "channels") is List<object> channels)
                foreach (object item in channels)
                {
                    var channel = item as Dictionary<string, object>;
                    var c = new ChannelInfo { Name = Text(Get(channel, "channel")) ?? "" };
                    if (settings != null && int.TryParse(c.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                        && settings.ContainsKey("ExFilt" + n))
                    {
                        c.Excitation = Get(settings, "ExFilt" + n);
                        c.Emission = Get(settings, "EmFilt" + n);
                        c.Gain = Get(settings, "Gain" + n);
                    }
                    else if (first)
                    {
                        c.Excitation = Get(summary, "excitation");
                        c.Emission = Get(summary, "emission");
                        c.Gain = Get(summary, "gain");
                    }
                    if (c.Excitation is string ex && ex.Length == 0) c.Excitation = null;
                    if (c.Emission is string em && em.Length == 0) c.Emission = null;
                    info.Channels.Add(c);
                    first = false;
                }
            if (Get(record, "events") is List<object> events) info.Events = events;
            info.ProtocolDetails = global::SAAControl.ProtocolDetails.Build(record);
            info.OmegaValues = global::SAAControl.ProtocolDetails.OmegaValues(record);
            return info;
        }

        /// <summary>The description of a run from its .pk alone (a file of another PC, or one whose record is gone).</summary>
        public static ExperimentInfo FromPk(PkFile.Contents read)
        {
            var i = read.Info;
            var instrument = Get(i, "instrument") as Dictionary<string, object>;
            var cycles = Get(i, "cycles") as Dictionary<string, object>;
            var info = new ExperimentInfo
            {
                RunId = Text(Get(i, "runId")) ?? Text(Get(read.Run, "id")), ExperimentId = Text(Get(i, "experimentId")),
                Key = Text(Get(read.Run, "key")), Name = Text(Get(i, "name")) ?? "Experiment",
                Model = Text(Get(instrument, "model")) ?? "", Serial = Text(Get(instrument, "serial")) ?? "",
                SoftwareName = Text(Get(instrument, "software")) ?? "", SoftwareVersion = Text(Get(instrument, "softwareVersion")) ?? "",
                StartedAt = Text(Get(i, "startedAt")), EndedAt = Text(Get(i, "endedAt")), Status = Text(Get(i, "status")) ?? "unknown",
                Done = Number(Get(cycles, "done")), Planned = Number(Get(cycles, "planned")),
                Run = read.Run, Interruption = Get(i, "interruption") as Dictionary<string, object>, Operator = Text(Get(i, "operator")),
            };
            info.Software = (info.SoftwareName + " " + info.SoftwareVersion).Trim();
            if (Get(read.Run, "channels") is List<object> channels)
                foreach (object item in channels)
                    info.Channels.Add(new ChannelInfo { Name = Text(Get(item as Dictionary<string, object>, "channel")) ?? "" });
            return info;
        }
    }

    public sealed class Experiments
    {
        public const string FolderName = DataLayout.ExperimentsName;
        public const string IndexFormat = "plate-kinetics-live-index/1";
        public readonly DataLayout Layout;
        public readonly string Folder;
        public readonly StorageStatus Storage;
        public readonly SettingsStore Settings;
        public readonly LayoutStore Layouts;
        readonly string programName, version, madeList;
        readonly object gate = new object();
        Dictionary<string, string> index;              // run id -> full path of its .pk
        HashSet<string> made;                          // run ids whose files were made whole once
        Dictionary<string, RunState> states;           // System\experiments.json
        DateTime rescanned = DateTime.MinValue;
        Dictionary<string, Listed> listed = new Dictionary<string, Listed>(StringComparer.OrdinalIgnoreCase);
        Func<Dictionary<string, object>, ExperimentInfo> describe;   // the watcher's description of a record
        Func<string, bool> live = _ => false;                         // the watcher writes that run's files itself
        readonly HashSet<string> keptSealed = new HashSet<string>(StringComparer.Ordinal);   // (logged once)
        readonly DateTime startedUtc = DateTime.UtcNow;               // files of saves cut short are older than this

        /// <summary>How files go to the Recycle Bin (tests replace it): true when they went.</summary>
        public static Func<IList<string>, bool> Recycler = RecycleBin.Send;

        /// <param name="dataDir">the data folder (PlateKineticsLive-data)</param>
        /// <param name="programName">what info.json says made the file, e.g. "Plate Kinetics Live"</param>
        public Experiments(string dataDir, string programName, string version) : this(new DataLayout(dataDir), programName, version) { }

        public Experiments(DataLayout layout, string programName, string version)
        {
            Layout = layout;
            Folder = layout.Experiments;
            Storage = new StorageStatus(Folder);
            Settings = new SettingsStore(layout.Settings);
            Layouts = new LayoutStore(layout.Layouts);
            Settings.LayoutExists = Layouts.Exists;
            this.programName = programName;
            this.version = version;
            madeList = Path.Combine(layout.Runs, "pk-made.txt");
        }

        /// <summary>The program's version for info.json: the exe's version ("2.2.0.0").</summary>
        public static string ProgramVersion()
        {
            try { return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? ""; }
            catch (Exception) { return ""; }
        }

        public string Program => (programName + " " + version).Trim();

        // ---- what the program keeps about each run's files (System\experiments.json)

        sealed class RunState
        {
            public string ExperimentId, Name, Operator, SealedAt, Status, File, Xlsx, XlsxSha256, Title, StartedAt, Instrument, Key;
            public long AnalysisRev;               // the revision of the saved analysis (0: none)
            public long XlsxRev;                   // the revision the .xlsx was made with (behind AnalysisRev: made again later)
            public string XlsxVersion;             // the program version that made the .xlsx
            // the last workbook written beside the run's own (that one was open in Excel, or changed by someone): its file,
            // the workbook it stands for, the revision of the analysis it holds and its checksum - asked for again with
            // nothing new, it is the answer again instead of one more copy
            public string AsideFile, AsideOf, AsideSha256;
            public long AsideRev;

            public Dictionary<string, object> ToJson()
            {
                var d = new Dictionary<string, object>
                {
                    ["experimentId"] = ExperimentId, ["name"] = Name, ["operator"] = Operator, ["sealedAt"] = SealedAt, ["status"] = Status,
                    ["file"] = File, ["xlsx"] = Xlsx, ["xlsxSha256"] = XlsxSha256, ["title"] = Title, ["startedAt"] = StartedAt,
                    ["instrument"] = Instrument, ["key"] = Key,
                };
                if (AnalysisRev > 0) d["analysisRev"] = AnalysisRev;
                if (XlsxRev > 0) d["xlsxRev"] = XlsxRev;
                if (XlsxVersion != null) d["xlsxVersion"] = XlsxVersion;
                if (AsideFile != null)
                {
                    d["asideFile"] = AsideFile;
                    d["asideOf"] = AsideOf;
                    d["asideRev"] = AsideRev;
                    d["asideSha256"] = AsideSha256;
                }
                return d;
            }

            public static RunState FromJson(Dictionary<string, object> d)
            {
                string T(string key) => d.TryGetValue(key, out object v) ? v as string : null;
                long N(string key) => d.TryGetValue(key, out object v) ? Math.Max(0, ExperimentInfo.Number(v)) : 0;
                return new RunState
                {
                    ExperimentId = T("experimentId"), Name = T("name"), Operator = T("operator"), SealedAt = T("sealedAt"), Status = T("status"),
                    File = T("file"), Xlsx = T("xlsx"), XlsxSha256 = T("xlsxSha256"), Title = T("title"), StartedAt = T("startedAt"),
                    Instrument = T("instrument"), Key = T("key"), AnalysisRev = N("analysisRev"), XlsxRev = N("xlsxRev"),
                    XlsxVersion = T("xlsxVersion"), AsideFile = T("asideFile"), AsideOf = T("asideOf"), AsideRev = N("asideRev"),
                    AsideSha256 = T("asideSha256"),
                };
            }
        }

        static bool IsGuid(string id) => id != null && Guid.TryParse(id, out _);

        /// <summary>The state of a run (made, with a new experiment id, when there is none).</summary>
        RunState StateOf(string runId, string recordId = null)
        {
            if (states.TryGetValue(runId, out RunState state))
            {
                if (!IsGuid(state.ExperimentId)) { state.ExperimentId = IsGuid(recordId) ? recordId : NewId(); SaveStates(); }
                return state;
            }
            state = new RunState { ExperimentId = IsGuid(recordId) ? recordId : NewId() };
            states[runId] = state;
            SaveStates();
            return state;
        }

        static string NewId() => Guid.NewGuid().ToString("D");

        void LoadStates()
        {
            states = new Dictionary<string, RunState>(StringComparer.Ordinal);
            foreach (string path in new[] { Layout.Index, Layout.Index + SafeFile.BackupSuffix })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    if (!(Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) is Dictionary<string, object> found)
                        || !(found.TryGetValue("runs", out object r) && r is Dictionary<string, object> runs)) continue;
                    foreach (var run in runs)
                        if (run.Value is Dictionary<string, object> d) states[run.Key] = RunState.FromJson(d);
                    return;
                }
                catch (Exception error) { Log.Warning("the list of experiment files " + path + " cannot be read (" + error.Message + ")"); }
            }
        }

        void SaveStates()
        {
            var runs = new Dictionary<string, object>();
            foreach (var state in states.OrderBy(s => s.Key, StringComparer.Ordinal)) runs[state.Key] = state.Value.ToJson();
            try
            {
                SafeFile.WriteJson(Layout.Index, new Dictionary<string, object> { ["format"] = IndexFormat, ["runs"] = runs });
                Storage.Succeeded(Layout.Index);
            }
            catch (Exception error) { Storage.Failed(Layout.Index, error); }
        }

        /// <summary>The experiment id of a run (made and kept when the run has none yet). The watchers keep it in their
        /// record ("experimentId").</summary>
        public string IdFor(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return null;
            lock (gate)
            {
                EnsureIndex();
                return StateOf(runId).ExperimentId;
            }
        }

        /// <summary>A run's record got its protocol later (an earlier run whose experiment file was found afterwards): its
        /// workbook no longer counts as current, so "Regenerate Excel" writes it again with the "Protocol" sheet (the
        /// workbook is not written by itself), and the page's details are read again.</summary>
        public void ProtocolAdded(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return;
            lock (detailsGate) detailsCache.Remove(runId);
            lock (gate)
            {
                EnsureIndex();
                if (states.TryGetValue(runId, out RunState state) && state.XlsxVersion != null)
                {
                    state.XlsxVersion = null;
                    SaveStates();
                }
            }
        }

        /// <summary>What the program knows of a run's files: (experiment id, status of its .pk, operator); nulls when
        /// nothing.</summary>
        public (string ExperimentId, string Status, string Operator) Known(string runId)
        {
            lock (gate)
            {
                EnsureIndex();
                return states.TryGetValue(runId ?? "", out RunState s) ? (s.ExperimentId, s.Status, s.Operator) : (null, null, null);
            }
        }

        // ---- writing

        /// <summary>Write the .pk of a run (and, with xlsx, its .xlsx). onlyIfNew: a run whose files were made whole once
        /// keeps them as they are; one that has a .pk but is not whole yet gets what it lacks (the .pk again only when
        /// it has not the run's status, the .xlsx when it is missing). Returns the .pk's path relative to Experiments
        /// ('/' between folders), or null when it was not written (a failure is shown in /api/state "storage").</summary>
        public string Save(ExperimentInfo info, bool xlsx, bool onlyIfNew = false) => Write(info, xlsx, onlyIfNew, false);

        string Write(ExperimentInfo info, bool xlsx, bool onlyIfNew, bool force)
        {
            if (info == null || string.IsNullOrEmpty(info.RunId) || info.Run == null) return null;
            lock (gate)
            {
                EnsureIndex();
                bool measuring = info.Status == "measuring";
                string known = PkOf(info.RunId);
                // an ended run whose files the user deleted: not made again by the program's own writes (restore does it)
                if (known == null && !force && !onlyIfNew && !measuring && made.Contains(info.RunId)) return null;
                RunState state = StateOf(info.RunId, info.ExperimentId);
                string stateBefore = Json.Serialize(state.ToJson());
                info.ExperimentId = state.ExperimentId;
                // the first .pk of a run that is measuring: the plan for the instrument's next experiment (name, layout)
                if (known == null && state.File == null && measuring) ApplyNextExperiment(info, state);
                if (string.IsNullOrEmpty(info.Operator)) info.Operator = state.Operator;
                if (string.IsNullOrEmpty(info.Operator) && measuring)
                {
                    info.Operator = Settings.OperatorDefault;
                    if (info.Operator.Length == 0) info.Operator = Session.ConsoleUser();
                }
                if (string.IsNullOrEmpty(info.Operator)) info.Operator = null;
                string name = !string.IsNullOrEmpty(state.Name) ? state.Name : info.Name;
                string month = Path.Combine(Folder, Naming.MonthFolder(info.StartedAt));
                // (as long as the folder it is in lets Excel open the workbook)
                string desired = Naming.BaseNameIn(known != null ? Path.GetDirectoryName(known) : month, info.StartedAt, name, info.Model, info.Serial);
                string pk;
                bool writePk = true;
                if (known != null)
                {
                    if (onlyIfNew && !force)
                    {
                        if (made.Contains(info.RunId)) return Relative(known);
                        writePk = PkStatus(known) != info.Status;
                    }
                    pk = writePk ? Renamed(known, desired) ?? known : known;
                }
                else
                {
                    // (a workbook of this very run - its .pk was deleted or put aside - does not take the name)
                    string unique = Naming.Unique(desired, n => Taken(month, n, state.Xlsx));
                    pk = Path.Combine(month, unique + PkFile.Extension);
                }
                Dictionary<string, object> analysis = AnalysisOf(info.RunId, known);
                var used = info.Run;
                // (any failure - of the disk, or of an unexpected value in the run - is shown and logged, never thrown:
                // the program goes on recording)
                if (writePk)
                {
                    try
                    {
                        byte[] runBytes = PkFile.RunBytes(info.Run);
                        string sealedAt = state.SealedAt;
                        if (known != null && sealedAt == null) sealedAt = SealedAtOf(known);
                        if (sealedAt != null && known != null)
                        {
                            PkFile.Contents old = TryRead(known, true);
                            if (old != null && old.Run != null)
                            {
                                if (!RunGate.Extends(old.Run, info.Run))
                                {
                                    // sealed data never change: the values recorded at the end stay
                                    if (keptSealed.Add(info.RunId))
                                        Log.Warning("the sealed data of " + info.RunId + " are kept: new data that change recorded values were not written");
                                    runBytes = old.RunBytes;
                                    used = old.Run;
                                }
                                else if (!RunGate.Adds(old.Run, info.Run))
                                {
                                    runBytes = old.RunBytes;           // nothing new: the same bytes, the same checksum
                                    used = old.Run;
                                }
                                else Log.Info("the sealed data of " + info.RunId + " gained cycles that were missing (now " +
                                              ExperimentInfo.Number(info.Run.TryGetValue("cyclesComplete", out object c) ? c : null) + ")");
                            }
                        }
                        if (sealedAt == null && ExperimentInfo.IsFinal(info.Status)) sealedAt = Clock.Now();
                        info.RunSha256 = PkFile.Sha256(runBytes);
                        info.FileName = Path.GetFileName(pk);
                        long done = ExperimentInfo.Number(used.TryGetValue("cyclesComplete", out object dc) ? dc : null);
                        if (!ReferenceEquals(used, info.Run)) info.Done = done;
                        byte[] analysisBytes = analysis != null ? PkFile.AnalysisBytes(analysis) : null;
                        var infoJson = InfoJson(info, name, sealedAt, analysis);
                        SafeFile.Write(pk, PkFile.Build(infoJson, runBytes, analysisBytes), PkFile.Validate, keepBackup: true, hideBackup: true);
                        Storage.Succeeded(pk);
                        state.SealedAt = sealedAt;
                    }
                    catch (Exception error)
                    {
                        Storage.Failed(pk, error);
                        return null;
                    }
                    index[info.RunId] = pk;
                    string before = state.Status;
                    state.Status = Status(info.Status);
                    if (before == "measuring" && ExperimentInfo.IsFinal(state.Status)) Ended(info, name, state.Status);
                    state.File = Relative(pk);
                    state.Title = name;
                    state.StartedAt = info.StartedAt;
                    state.Instrument = ((info.Model ?? "") + " " + (info.Serial ?? "")).Trim();
                    state.Key = info.Key ?? state.Key;
                    state.Operator = info.Operator;
                }
                if (Json.Serialize(state.ToJson()) != stateBefore) SaveStates();
                if (xlsx)
                {
                    string book = Path.ChangeExtension(pk, XlsxFile.Extension);
                    if (writePk || !File.Exists(book))
                    {
                        if (Storage.Critical)
                        {
                            Log.Warning("the disk is nearly full: the workbook " + Path.GetFileName(book) + " waits");
                            return Relative(pk);           // (not whole: made at a later start)
                        }
                        if (info.RunSha256 == null)
                        {
                            info.RunSha256 = PkFile.Sha256(PkFile.RunBytes(used));
                            info.FileName = Path.GetFileName(pk);
                        }
                        ExperimentInfo copy = info.Clone();
                        copy.Run = used;
                        string result = WriteWorkbook(copy, analysis, pk, state, explicitly: false);
                        if (result.StartsWith("failed:")) return Relative(pk);    // (not whole: the next start makes the .xlsx)
                    }
                    Made(info.RunId);                      // whole: never made again
                }
                return Relative(pk);
            }
        }

        static string Status(string status) => Array.IndexOf(ExperimentInfo.States, status) >= 0 ? status : "unknown";

        Dictionary<string, object> InfoJson(ExperimentInfo info, string name, string sealedAt, Dictionary<string, object> analysis)
        {
            string status = Status(info.Status);
            var (en, zh) = ExperimentInfo.StatusText(status, info.Done, info.Planned, info.Interruption, info.StartedAt);
            // the software's name and version; a description that has them only together ("Omega 6.40.101.9") is split
            string software = info.SoftwareName ?? "", softwareVersion = info.SoftwareVersion ?? "";
            string both = (info.Software ?? "").Trim();
            if (software.Length == 0 && both.Length > 0)
            {
                int space = both.IndexOf(' ');
                software = space > 0 ? both.Substring(0, space) : both;
            }
            if (softwareVersion.Length == 0 && software.Length > 0 && both.StartsWith(software + " ", StringComparison.OrdinalIgnoreCase))
                softwareVersion = both.Substring(software.Length + 1).Trim();
            return new Dictionary<string, object>
            {
                ["format"] = PkFile.Format, ["program"] = programName, ["version"] = version,
                ["experimentId"] = info.ExperimentId, ["runId"] = info.RunId, ["name"] = name,
                ["instrument"] = new Dictionary<string, object>
                {
                    ["model"] = info.Model ?? "", ["serial"] = info.Serial ?? "", ["software"] = software,
                    ["softwareVersion"] = softwareVersion.Length == 0 ? null : softwareVersion,
                },
                ["startedAt"] = Clock.WithOffset(info.StartedAt), ["endedAt"] = Clock.WithOffset(info.EndedAt), ["timeZone"] = Clock.TimeZoneId,
                ["status"] = status, ["statusText"] = new Dictionary<string, object> { ["en"] = en, ["zh"] = zh },
                ["cycles"] = new Dictionary<string, object> { ["done"] = info.Done, ["planned"] = info.Planned > 0 ? (object)info.Planned : null },
                ["interruption"] = info.Interruption, ["operator"] = info.Operator,
                ["runSha256"] = info.RunSha256, ["sealedAt"] = sealedAt,
                ["analysisSavedAt"] = analysis != null && analysis.TryGetValue("savedAt", out object at) ? at as string : null,
            };
        }

        /// <summary>A run that was measuring has ended (its .pk got its end): the event "completed", "stopped" or
        /// "interrupted" (an end not seen counts as interrupted), which the window and the tray announce.</summary>
        void Ended(ExperimentInfo info, string name, string status)
        {
            string kind = status == "completed" || status == "stopped" ? status : "interrupted";
            var (en, zh) = ExperimentInfo.StatusText(status, info.Done, info.Planned, info.Interruption, info.StartedAt);
            string instrument = (info.Key != null ? Settings.DisplayName(info.Key) : null) ?? ((info.Model ?? "") + " " + (info.Serial ?? "")).Trim();
            string where = instrument.Length > 0 ? " (" + instrument + ")" : "";
            Problems.Event(kind, info.Key, info.RunId, name, name + where + ": " + en,
                           name + (instrument.Length > 0 ? "\uff08" + instrument + "\uff09" : "") + "\uff1a" + zh);
        }

        /// <summary>The plan the user made for the next run of this instrument (Settings: nextExperiment): its name and
        /// the layout's sample names and groups (the run's first analysis). Used once.</summary>
        void ApplyNextExperiment(ExperimentInfo info, RunState state)
        {
            var plan = Settings.TakeNextExperiment(info.Key);
            if (plan == null) return;
            if (plan.Value.Name.Length > 0) state.Name = plan.Value.Name;
            var layout = plan.Value.LayoutId.Length > 0 ? Layouts.Get(plan.Value.LayoutId) : null;
            if (layout != null)
            {
                try
                {
                    long rev = state.AnalysisRev + 1;
                    SafeFile.WriteJson(AnalysisPath(info.RunId), WithRevision(FromLayout(layout, info.Operator), rev));
                    state.AnalysisRev = rev;
                }
                catch (Exception error) { Storage.Failed(AnalysisPath(info.RunId), error); }
            }
            SaveStates();
            Log.Info("the next experiment planned for " + info.Key + " was applied to " + info.RunId +
                     (plan.Value.Name.Length > 0 ? ": name " + plan.Value.Name : "") + (layout != null ? ", layout " + layout["name"] : ""));
        }

        /// <summary>An analysis that holds a layout's sample names and groups.</summary>
        public static Dictionary<string, object> FromLayout(Dictionary<string, object> layout, string operatorName)
        {
            var samples = new Dictionary<string, object>();
            var groups = new List<(string Name, List<object> Wells)>();
            if (layout.TryGetValue("wells", out object w) && w is Dictionary<string, object> wells)
                foreach (var well in wells)
                {
                    if (!(well.Value is Dictionary<string, object> cell)) continue;
                    if (cell.TryGetValue("name", out object n) && n is string name && name.Length > 0) samples[well.Key] = name;
                    if (cell.TryGetValue("group", out object g) && g is string group && group.Length > 0)
                    {
                        int at = groups.FindIndex(x => x.Name == group);
                        if (at < 0) { groups.Add((group, new List<object>())); at = groups.Count - 1; }
                        groups[at].Wells.Add(well.Key);
                    }
                }
            return new Dictionary<string, object>
            {
                ["format"] = PkFile.AnalysisFormat, ["savedAt"] = Clock.Now(),
                ["savedBy"] = "layout " + (layout.TryGetValue("name", out object ln) ? ln as string : ""),
                ["operator"] = operatorName, ["samples"] = samples,
                ["groups"] = groups.Select(g => (object)new Dictionary<string, object> { ["name"] = g.Name, ["wells"] = g.Wells }).ToList(),
                ["notes"] = "", ["settings"] = new Dictionary<string, object>(), ["results"] = new List<object>(),
            };
        }

        /// <summary>The status in a .pk's info.json; null when it cannot be read.</summary>
        static string PkStatus(string pk)
        {
            try { return PkFile.ReadFile(pk, false).Info.TryGetValue("status", out object status) ? status as string : null; }
            catch (Exception) { return null; }
        }

        static string SealedAtOf(string pk)
        {
            try
            {
                var read = PkFile.ReadFile(pk, false);
                return read.Sealed ? read.Info["sealedAt"] as string : null;
            }
            catch (Exception) { return null; }
        }

        static PkFile.Contents TryRead(string pk, bool withRun)
        {
            try { return PkFile.ReadFile(pk, withRun, true, true); }
            catch (Exception) { return null; }
        }

        /// <summary>Whether a run has its .pk.</summary>
        public bool Has(string runId)
        {
            lock (gate)
            {
                EnsureIndex();
                return PkOf(runId) != null;
            }
        }

        /// <summary>A name is taken in a folder when a .pk or .xlsx of that name is there.</summary>
        static bool Taken(string folder, string name) =>
            File.Exists(Path.Combine(folder, name + PkFile.Extension)) || File.Exists(Path.Combine(folder, name + XlsxFile.Extension));

        /// <summary>The same, but the run's own workbook (ownXlsx: relative to Experiments) does not take the name.</summary>
        bool Taken(string folder, string name, string ownXlsx)
        {
            if (File.Exists(Path.Combine(folder, name + PkFile.Extension))) return true;
            string book = Path.Combine(folder, name + XlsxFile.Extension);
            return File.Exists(book) && !string.Equals(Relative(book), ownXlsx, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The run's name changed since its .pk was made (BMG: Omega's ID1 is known at the end; Archimed: the
        /// experiment file was found later): its files get the new name, unless the user renamed them. Returns the new
        /// path of the .pk, or null when it keeps its name.</summary>
        string Renamed(string pk, string desired)
        {
            string current = Path.GetFileNameWithoutExtension(pk);
            if (Naming.IsGenerated(current, desired)) return null;
            Dictionary<string, object> before;
            try { before = PkFile.ReadFile(pk, false).Info; }
            catch (Exception) { return null; }
            var instrument = before.TryGetValue("instrument", out object i) ? i as Dictionary<string, object> : null;
            string Field(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v as string : null;
            string generated = Naming.BaseNameIn(Path.GetDirectoryName(pk), LocalTime(Field(before, "startedAt")), Field(before, "name"), Field(instrument, "model"), Field(instrument, "serial"));
            if (!Naming.IsGenerated(current, generated)) return null;      // a name the user gave it: kept
            return MoveFiles(pk, desired);
        }

        /// <summary>A time of info.json ("...+08:00") as the records have it: the local time.</summary>
        static string LocalTime(string time)
        {
            DateTime? local = Clock.Local(time);
            return local.HasValue ? local.Value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture) : time;
        }

        /// <summary>The .pk and its companions (.pk.bak, .xlsx, .xlsx.bak) under a new name in their folder (" (2)" when it
        /// is taken). Returns the .pk's new path, or null when it could not be renamed.</summary>
        string MoveFiles(string pk, string desired)
        {
            string current = Path.GetFileNameWithoutExtension(pk);
            string folder = Path.GetDirectoryName(pk);
            string fresh = Naming.Unique(desired, name => !string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && Taken(folder, name));
            if (string.Equals(fresh, current, StringComparison.Ordinal)) return null;
            string target = Path.Combine(folder, fresh + PkFile.Extension);
            bool caseOnly = string.Equals(fresh, current, StringComparison.OrdinalIgnoreCase);
            try
            {
                // one step, also for a change of case only (Windows renames a file to the same name in other letters): a
                // power cut or a crash leaves the .pk under its old name or its new one, never under a temporary one
                File.Move(pk, target);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Log.Warning("the experiment file " + pk + " could not be renamed to " + Path.GetFileName(target) + " (" + error.Message + "); it keeps its name");
                return null;
            }
            // (cut short here, the next start puts the workbook beside the .pk again: Reunite)
            foreach (string suffix in new[] { PkFile.Extension + SafeFile.BackupSuffix, XlsxFile.Extension, XlsxFile.Extension + SafeFile.BackupSuffix })
            {
                string from = Path.Combine(folder, current + suffix), to = Path.Combine(folder, fresh + suffix);
                try { if (File.Exists(from) && (caseOnly || !File.Exists(to))) File.Move(from, to); }
                catch (Exception error) { Log.Warning(Path.GetFileName(from) + " keeps its name (" + error.Message + ")"); }
            }
            Log.Info("the experiment file " + current + PkFile.Extension + " is now " + fresh + PkFile.Extension);
            return target;
        }

        string Relative(string full)
        {
            string root = Folder.TrimEnd('\\') + "\\";
            return (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : Path.GetFileName(full)).Replace('\\', '/');
        }

        /// <summary>The .pk of a run that is there; null when there is none. A file that is not where it was (the user
        /// moved or renamed it) is looked for again at once - null would read as "deleted by the user", and Ctrl+S or the
        /// live writer would make the files again in their first place beside the moved ones; a run without a file is
        /// looked for at most every 2 seconds.</summary>
        string PkOf(string runId)
        {
            if (index.TryGetValue(runId, out string path))
            {
                if (File.Exists(path)) return path;
                Scan();                                // (after it the run is either found or no longer indexed)
            }
            else if ((DateTime.UtcNow - rescanned).TotalSeconds >= 2) Scan();
            return index.TryGetValue(runId, out path) && File.Exists(path) ? path : null;
        }

        /// <summary>The .pk files of the folder by run id (once; afterwards kept up to date by Save and looked through
        /// again when a file has moved), the list of the runs whose files were made whole once, and the program's states.</summary>
        void EnsureIndex()
        {
            if (index != null) return;
            made = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(madeList))
                    foreach (string line in Encoding.UTF8.GetString(SafeFile.ReadAll(madeList)).Split('\n'))
                        if (line.Trim().Length > 0) made.Add(line.Trim());
            }
            catch (Exception error) { Log.Warning("the list " + madeList + " cannot be read (" + error.Message + ")"); }
            bool fresh = !File.Exists(Layout.Index) && !File.Exists(Layout.Index + SafeFile.BackupSuffix);
            LoadStates();
            Scan();
            if (fresh) Adopt();
            LearnRevisions();
        }

        /// <summary>The revisions of analyses saved before the program counted them (System\analysis\&lt;run id&gt;.json
        /// without a revision in the program's list): their own "rev", else 1; their workbooks were made with them.</summary>
        void LearnRevisions()
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(Layout.Analysis)) return;
                files = Directory.GetFiles(Layout.Analysis, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                                               && !f.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return; }
            bool changed = false;
            foreach (string file in files)
            {
                string runId = Path.GetFileNameWithoutExtension(file);
                if (!SafeRunId(runId) || (states.TryGetValue(runId, out RunState known) && known.AnalysisRev > 0)) continue;
                Dictionary<string, object> analysis = ReadAnalysisFile(file);
                if (analysis == null) continue;
                RunState state = StateOf(runId);
                state.AnalysisRev = RevisionOf(analysis);
                if (state.XlsxRev == 0) state.XlsxRev = state.AnalysisRev;
                changed = true;
            }
            if (changed) SaveStates();
        }

        void Scan()
        {
            rescanned = DateTime.UtcNow;
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var written = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (string path in PkPaths())
            {
                try
                {
                    var read = PkFile.ReadFile(path, false);
                    string runId = read.RunId;
                    if (string.IsNullOrEmpty(runId))
                    {
                        // a .pk of another writer without the run id in info.json: the run's own id
                        runId = PkFile.ReadFile(path, true).Run?.TryGetValue("id", out object id) == true ? id as string : null;
                    }
                    if (string.IsNullOrEmpty(runId)) continue;
                    // two files of one run id (a copy, or a file of another PC): the one with this PC's experiment id, else the newest
                    string experimentId = read.Info.TryGetValue("experimentId", out object e) ? e as string : null;
                    bool ours = states != null && states.TryGetValue(runId, out RunState s) && s.ExperimentId != null && s.ExperimentId == experimentId;
                    DateTime at = File.GetLastWriteTimeUtc(path);
                    if (!found.ContainsKey(runId) || ours || (at > written[runId] && !OursAt(found[runId], runId)))
                    {
                        found[runId] = path;
                        written[runId] = ours ? DateTime.MaxValue : at;
                    }
                }
                catch (Exception error)
                {
                    // not one of ours, or held by another program just now (a virus scanner, a backup, a zip program): a file
                    // that was the run's stays the run's - else the run would seem to have lost its file, and Ctrl+S would
                    // make it again beside it
                    if ((error is IOException || error is UnauthorizedAccessException) && index != null)
                        foreach (var entry in index)
                            if (string.Equals(entry.Value, path, StringComparison.OrdinalIgnoreCase) && !found.ContainsKey(entry.Key))
                            {
                                found[entry.Key] = path;
                                written[entry.Key] = DateTime.MinValue;
                                break;
                            }
                }
            }
            index = found;
        }

        bool OursAt(string path, string runId)
        {
            try
            {
                string id = PkFile.ReadFile(path, false).Info.TryGetValue("experimentId", out object e) ? e as string : null;
                return states.TryGetValue(runId, out RunState s) && s.ExperimentId != null && s.ExperimentId == id;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The first start of this version: what the .pk files there say becomes the program's states (their
        /// experiment ids, operators, ends), and their .xlsx are taken as the program wrote them.</summary>
        void Adopt()
        {
            foreach (var entry in index)
            {
                try
                {
                    var info = PkFile.ReadFile(entry.Value, false).Info;
                    string T(string key) => info.TryGetValue(key, out object v) ? v as string : null;
                    var state = states.TryGetValue(entry.Key, out RunState known) ? known : new RunState();
                    state.ExperimentId = IsGuid(state.ExperimentId) ? state.ExperimentId : IsGuid(T("experimentId")) ? T("experimentId") : NewId();
                    state.Operator = state.Operator ?? T("operator");
                    state.SealedAt = state.SealedAt ?? T("sealedAt");
                    state.Status = T("status");
                    state.File = Relative(entry.Value);
                    state.Title = T("name");
                    state.StartedAt = LocalTime(T("startedAt"));
                    var instrument = info.TryGetValue("instrument", out object i) ? i as Dictionary<string, object> : null;
                    string Field(string key) => instrument != null && instrument.TryGetValue(key, out object v) ? v as string : null;
                    state.Instrument = ((Field("model") ?? "") + " " + (Field("serial") ?? "")).Trim();
                    string book = Path.ChangeExtension(entry.Value, XlsxFile.Extension);
                    if (state.XlsxSha256 == null && File.Exists(book))
                    {
                        state.Xlsx = Relative(book);
                        state.XlsxSha256 = PkFile.Sha256(SafeFile.ReadAll(book));
                    }
                    states[entry.Key] = state;
                }
                catch (Exception) { }
            }
            SaveStates();
        }

        /// <summary>Every .pk under Experiments (temporary and backup files left out).</summary>
        List<string> PkPaths()
        {
            try
            {
                if (!Directory.Exists(Folder)) return new List<string>();
                return Directory.EnumerateFiles(Folder, "*" + PkFile.Extension, SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("."))
                    .ToList();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        void Made(string runId)
        {
            if (!made.Add(runId)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(madeList));
                byte[] line = Encoding.UTF8.GetBytes(runId + "\r\n");
                using (var stream = new FileStream(madeList, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(line, 0, line.Length);
                    stream.Flush(true);
                }
            }
            catch (Exception error) { Log.Warning("the list " + madeList + " cannot be written (" + error.Message + ")"); }
        }

        // ---- the workbook

        /// <summary>The run's .xlsx beside its .pk. A workbook that is there already is replaced only when it is the one
        /// the program wrote (the same checksum) and not open (in Excel, WPS, LibreOffice). Else, when asked for
        /// (explicitly: an analysis was saved, or "make the workbook again"), "&lt;name&gt; (analysis yyyy-MM-dd HH-mm).xlsx"
        /// is written beside it; the program's own writes leave it. Returns "written", "saved-as:&lt;file relative to
        /// Experiments&gt;", "kept" (left as it is) or "failed:&lt;reason&gt;".</summary>
        string WriteWorkbook(ExperimentInfo info, Dictionary<string, object> analysis, string pk, RunState state, bool explicitly) =>
            WriteWorkbook(info, analysis, pk, state, explicitly, out _);

        /// <summary>The same; why: "open" or "changed" when the workbook there was left as it is or written beside.</summary>
        string WriteWorkbook(ExperimentInfo info, Dictionary<string, object> analysis, string pk, RunState state, bool explicitly, out string why)
        {
            string book = Path.ChangeExtension(pk, XlsxFile.Extension);
            why = Obstacle(book, state);
            if (why != null && !explicitly)
            {
                // (looked at before the workbook is made: making it is the costly part)
                Log.Info("the workbook " + Path.GetFileName(book) + " is " + (why == "open" ? "open in a program" : "not the one the program wrote") + ": it is left as it is");
                return "kept";
            }
            // the copy written beside it for this same version of the analysis is there still, unchanged: that is the answer
            // again (Ctrl+S pressed twice while Excel has the workbook open makes one copy, not two)
            long rev = analysis != null ? RevisionOf(analysis) : 0;
            if (why != null && state != null && state.AsideFile != null && state.AsideRev == rev && info.Status != "measuring"
                && string.Equals(state.AsideOf, Relative(book), StringComparison.OrdinalIgnoreCase))
            {
                string earlier = Resolve(state.AsideFile);
                if (earlier != null && File.Exists(earlier) && state.AsideSha256 != null && SameHash(earlier, state.AsideSha256))
                    return "saved-as:" + Relative(earlier);
            }
            byte[] bytes;
            try { bytes = XlsxFile.Build(info, Program, analysis); }
            catch (Exception error)
            {
                Log.Error("the workbook of " + info.RunId + " could not be made", error);
                return "failed:" + error.Message;
            }
            string target = book;
            if (File.Exists(book))
            {
                if (why != null)
                {
                    string stamp = DateTime.Now.ToString("yyyy'-'MM'-'dd' 'HH'-'mm", CultureInfo.InvariantCulture);
                    string folder = Path.GetDirectoryName(book), baseName = Path.GetFileNameWithoutExtension(book);
                    // (Excel opens no file whose path is longer than 218 characters: the name is shortened to fit)
                    string suffix = " (analysis " + stamp + ")";
                    int room = MaxWorkbookPath - (folder.Length + 1 + suffix.Length + XlsxFile.Extension.Length + 5);
                    if (baseName.Length > room) baseName = baseName.Substring(0, Math.Max(20, room)).TrimEnd(' ', '.');
                    string alternative = Naming.Unique(baseName + suffix, n => File.Exists(Path.Combine(folder, n + XlsxFile.Extension)));
                    target = Path.Combine(folder, alternative + XlsxFile.Extension);
                    Log.Info("the workbook " + Path.GetFileName(book) + " is " + (why == "open" ? "open in a program" : "not the one the program wrote") +
                             ": written as " + Path.GetFileName(target) + " instead");
                }
            }
            try
            {
                SafeFile.Write(target, bytes, XlsxFile.Validate, keepBackup: true, hideBackup: true);
                Storage.Succeeded(target);
            }
            catch (Exception error)
            {
                Storage.Failed(target, error);
                return "failed:" + error.Message;
            }
            if (target == book && state != null)
            {
                state.Xlsx = Relative(book);
                state.XlsxSha256 = PkFile.Sha256(bytes);
                state.XlsxRev = analysis != null ? RevisionOf(analysis) : 0;
                state.XlsxVersion = version;
                SaveStates();
                return "written";
            }
            if (target != book && state != null)
            {
                state.AsideFile = Relative(target);
                state.AsideOf = Relative(book);
                state.AsideRev = rev;
                state.AsideSha256 = PkFile.Sha256(bytes);
                SaveStates();
            }
            return target == book ? "written" : "saved-as:" + Relative(target);
        }

        /// <summary>Excel opens no file whose full path is longer than this.</summary>
        const int MaxWorkbookPath = 218;

        static bool SameHash(string file, string hash)
        {
            try { return string.Equals(PkFile.Sha256(SafeFile.ReadAll(file)), hash, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>Whether a workbook is open in a program: Excel and WPS hold the file (it cannot be opened alone) and
        /// leave "~$&lt;name&gt;" beside it; LibreOffice leaves ".~lock.&lt;name&gt;#".</summary>
        public static bool Busy(string book)
        {
            string folder = Path.GetDirectoryName(book), name = Path.GetFileName(book);
            var marks = new List<string> { "~$" + name, ".~lock." + name + "#" };
            if (name.Length > 2) marks.Add("~$" + name.Substring(2));
            if (marks.Any(m => File.Exists(Path.Combine(folder, m)))) return true;
            try
            {
                using (new FileStream(book, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }    // (read-only: it cannot be replaced either)
        }

        // ---- the analysis

        /// <summary>The program's copies in System\analysis: &lt;run id&gt;.json the analysis, &lt;run id&gt;.prev.json the
        /// version the last save replaced.</summary>
        const string PreviousSuffix = ".prev.json";

        string AnalysisPath(string runId) => Path.Combine(Layout.Analysis, runId + ".json");
        string PreviousPath(string runId) => Path.Combine(Layout.Analysis, runId + PreviousSuffix);

        /// <summary>When the workbook is made after a save: WorkbookNow (Ctrl+S: now; one that is open in a program or was
        /// changed by someone gets "&lt;name&gt; (analysis ...).xlsx" beside it), WorkbookLeave (the experiment was left or
        /// closed: now when it can be replaced, else later), WorkbookAuto (the page saved by itself: later, at most every
        /// WorkbookDelay).</summary>
        public const string WorkbookNow = "now", WorkbookLeave = "leave", WorkbookAuto = "auto";

        /// <summary>How long an analysis the page saved by itself waits for its workbook, and how often the program looks
        /// for workbooks that are due (tests make them short).</summary>
        public static TimeSpan WorkbookDelay = TimeSpan.FromMinutes(10), WorkbookCheck = TimeSpan.FromSeconds(30);

        readonly Dictionary<string, DateTime> waiting = new Dictionary<string, DateTime>(StringComparer.Ordinal);   // run id -> since (UTC)
        readonly Dictionary<string, (string By, DateTime At)> logged = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        readonly object flushGate = new object();
        System.Threading.Timer workbookTimer;
        volatile bool closed;

        /// <summary>The revision of an analysis: its "rev"; 1 for one saved before revisions were counted; 0 for none.</summary>
        static long RevisionOf(Dictionary<string, object> analysis)
        {
            if (analysis == null) return 0;
            long rev = analysis.TryGetValue("rev", out object r) ? ExperimentInfo.Number(r) : 0;
            return rev > 0 ? rev : 1;
        }

        /// <summary>The analysis with its revision ("rev", after "results").</summary>
        static Dictionary<string, object> WithRevision(Dictionary<string, object> analysis, long rev)
        {
            var copy = new Dictionary<string, object>();
            foreach (var item in analysis)
            {
                if (item.Key == "rev") continue;
                copy[item.Key] = item.Value;
                if (item.Key == "results") copy["rev"] = rev;
            }
            if (!copy.ContainsKey("rev")) copy["rev"] = rev;
            return copy;
        }

        /// <summary>Whether two analyses say the same (when, by whom and as which revision they were saved aside).</summary>
        static bool SameContent(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (a == null || b == null) return a == null && b == null;
            string Content(Dictionary<string, object> d) =>
                Json.Serialize(d.Where(item => item.Key != "savedAt" && item.Key != "savedBy" && item.Key != "rev").ToDictionary(item => item.Key, item => item.Value));
            return Content(a) == Content(b);
        }

        static object ValueOf(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;

        static Dictionary<string, object> ReadAnalysisFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) as Dictionary<string, object>;
            }
            catch (Exception error)
            {
                Log.Warning("the saved analysis " + path + " cannot be read (" + error.Message + ")");
                return null;
            }
        }

        /// <summary>The saved analysis of a run: the program's copy (System\analysis), else the one in its .pk; null: none.</summary>
        Dictionary<string, object> AnalysisOf(string runId, string pk)
        {
            Dictionary<string, object> kept = ReadAnalysisFile(AnalysisPath(runId));
            if (kept != null || pk == null) return kept;
            try { return PkFile.ReadFile(pk, false, true).Analysis; }
            catch (Exception) { return null; }
        }

        /// <summary>{"rev", "savedAt", "savedBy"} of the version the last save replaced (what "Undo last save" puts back);
        /// null when there is none.</summary>
        Dictionary<string, object> PreviousOf(string runId)
        {
            Dictionary<string, object> previous = ReadAnalysisFile(PreviousPath(runId));
            return previous == null ? null : new Dictionary<string, object>
            {
                ["rev"] = RevisionOf(previous), ["savedAt"] = ValueOf(previous, "savedAt"), ["savedBy"] = ValueOf(previous, "savedBy"),
            };
        }

        /// <summary>The revision of each run's saved analysis (runs without one left out): /api/runs and /api/state say it
        /// as "analysisRev".</summary>
        public Dictionary<string, long> AnalysisRevisions()
        {
            lock (gate)
            {
                EnsureIndex();
                return states.Where(s => s.Value.AnalysisRev > 0).ToDictionary(s => s.Key, s => s.Value.AnalysisRev, StringComparer.Ordinal);
            }
        }

        /// <summary>The revision of a run's saved analysis; 0: none.</summary>
        public long AnalysisRevision(string runId)
        {
            lock (gate)
            {
                EnsureIndex();
                return states.TryGetValue(runId ?? "", out RunState state) ? state.AnalysisRev : 0;
            }
        }

        /// <summary>GET /api/analysis?runId=: {"analysis": the saved analysis or null, "rev": its revision (0: none),
        /// "savedAt", "savedBy", "file": the .pk (relative to Experiments) or null, "previous": {"rev", "savedAt",
        /// "savedBy"} of the version "Undo last save" puts back, or null}.</summary>
        public object GetAnalysis(string runId)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                Dictionary<string, object> analysis = AnalysisOf(runId, pk);
                long rev = RevisionOf(analysis);
                // (an analysis the program had not counted - one only in a .pk - is counted from now on; its workbook is
                // taken as made with it)
                if (rev > 0 && states.TryGetValue(runId, out RunState state) && state.AnalysisRev < rev)
                {
                    if (state.XlsxRev == 0 || state.XlsxRev == state.AnalysisRev) state.XlsxRev = rev;
                    state.AnalysisRev = rev;
                    SaveStates();
                }
                return new Dictionary<string, object>
                {
                    ["analysis"] = analysis, ["rev"] = rev, ["savedAt"] = ValueOf(analysis, "savedAt"), ["savedBy"] = ValueOf(analysis, "savedBy"),
                    ["file"] = pk != null ? Relative(pk) : null, ["previous"] = PreviousOf(runId),
                };
            }
        }

        static readonly Regex RunIdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,127}$", RegexOptions.CultureInvariant);
        public static bool SafeRunId(string runId) => runId != null && RunIdPattern.IsMatch(runId) && !runId.Contains("..");

        /// <summary>An analysis as it is kept: format, savedAt (now), savedBy (the analysis' own, else the one given),
        /// operator, samples, groups, notes, settings, results, then what else the page sent (a revision it sent is the
        /// program's to give: left out).</summary>
        public static Dictionary<string, object> Normalized(Dictionary<string, object> posted, string savedBy)
        {
            PkFile.CheckAnalysisFormat(posted);
            object Item(string key, object fallback) => posted.TryGetValue(key, out object v) && v != null ? v : fallback;
            string by = posted.TryGetValue("savedBy", out object b) && b is string s && s.Trim().Length > 0 ? SettingsStore.Plain(s, 200) : savedBy;
            var analysis = new Dictionary<string, object>
            {
                ["format"] = PkFile.AnalysisFormat, ["savedAt"] = Clock.Now(), ["savedBy"] = by,
                ["operator"] = Item("operator", null) is string op ? SettingsStore.Plain(op, 100) : null,
                ["samples"] = Item("samples", new Dictionary<string, object>()), ["groups"] = Item("groups", new List<object>()),
                ["notes"] = Item("notes", ""), ["settings"] = Item("settings", new Dictionary<string, object>()),
                ["results"] = Item("results", new List<object>()),
            };
            if (!(analysis["samples"] is Dictionary<string, object>)) throw new ApiError(400, "samples must be an object (well -> name)");
            if (!(analysis["groups"] is List<object>)) throw new ApiError(400, "groups must be a list");
            if (!(analysis["results"] is List<object>)) throw new ApiError(400, "results must be a list");
            if (!(analysis["settings"] is Dictionary<string, object>)) throw new ApiError(400, "settings must be an object");
            foreach (var item in posted)
                if (!analysis.ContainsKey(item.Key) && item.Key != "rev") analysis[item.Key] = item.Value;
            return analysis;
        }

        /// <summary>POST /api/analysis?runId=: the analysis becomes the run's - the last save wins - in System\analysis and in
        /// the run's .pk (analysis.json; the raw data are copied byte for byte), as the next revision; the version it
        /// replaces is kept (System\analysis\&lt;run id&gt;.prev.json). An analysis that says what the saved one says is not
        /// saved again (no new revision). The workbook follows as `workbook` says (WorkbookNow, WorkbookLeave,
        /// WorkbookAuto); an Excel problem never stops the .pk. Experiment files the user deleted are made again only on
        /// WorkbookNow (Ctrl+S), never by a save the page made by itself. Returns {"ok", "rev", "savedAt", "savedBy",
        /// "file", "xlsx": "written|saved-as:&lt;file&gt;|current|pending|kept|none|failed:&lt;reason&gt;" (see
        /// MakeWorkbook), "previous"}; null: no such run.</summary>
        public object SaveAnalysis(string runId, Dictionary<string, object> posted, string savedBy, string workbook = WorkbookNow)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            if (posted == null) throw new ApiError(400, "the analysis must be a JSON object");
            Dictionary<string, object> analysis;
            try { analysis = Normalized(posted, savedBy); }
            catch (NewerFormatException) { throw new ApiError(400, PkFile.NewerText, PkFile.NewerTextZh); }
            catch (InvalidDataException error) { throw new ApiError(400, error.Message); }
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                Dictionary<string, object> record = ReadRecord(runId);
                if (pk == null && record == null) return null;
                return Keep(runId, analysis, pk, record, workbook);
            }
        }

        /// <summary>POST /api/analysis/revert?runId=: "Undo last save" (the program's window on the PC; the online view
        /// never asks for it): the version the last save replaced becomes the run's analysis again, saved now by savedBy as
        /// the next revision; the version it replaces is kept in turn (a second undo goes back to it). Returns what a save
        /// returns, with "analysis": the analysis now; ApiError 409 when there is no earlier version; null: no such run.</summary>
        public object RevertAnalysis(string runId, string savedBy, string workbook = WorkbookLeave)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                Dictionary<string, object> record = ReadRecord(runId);
                if (pk == null && record == null) return null;
                Dictionary<string, object> previous = ReadAnalysisFile(PreviousPath(runId));
                // "There is no earlier version of the analysis to go back to."
                if (previous == null)
                    throw new ApiError(409, "There is no earlier version of the analysis to go back to.",
                                       "\u6ca1\u6709\u53ef\u4ee5\u9000\u56de\u7684\u4e0a\u4e00\u7248\u5206\u6790\u3002");
                Dictionary<string, object> analysis;
                try { analysis = Normalized(new Dictionary<string, object>(previous) { ["savedBy"] = savedBy ?? "" }, savedBy); }
                catch (Exception error) when (error is NewerFormatException || error is InvalidDataException || error is ApiError)
                {
                    // "The earlier version of the analysis cannot be used."
                    throw new ApiError(409, "The earlier version of the analysis cannot be used (" + error.Message + ").",
                                       "\u4e0a\u4e00\u7248\u5206\u6790\u65e0\u6cd5\u4f7f\u7528\u3002");
                }
                Dictionary<string, object> answer = Keep(runId, analysis, pk, record, workbook);
                answer["analysis"] = AnalysisOf(runId, PkOf(runId));
                Log.Info("the analysis of " + runId + " went back to the version of " + ValueOf(previous, "savedAt") + " (now revision " + answer["rev"] + ")");
                return answer;
            }
        }

        /// <summary>An analysis kept as the run's (under the gate; see SaveAnalysis).</summary>
        Dictionary<string, object> Keep(string runId, Dictionary<string, object> analysis, string pk, Dictionary<string, object> record, string workbook)
        {
            Dictionary<string, object> current = AnalysisOf(runId, pk);
            RunState state = StateOf(runId, ValueOf(record, "experimentId") as string);
            bool changed = current == null || !SameContent(current, analysis);
            long rev;
            if (changed)
            {
                // the next revision: never one used before
                rev = Math.Max(state.AnalysisRev, RevisionOf(current)) + 1;
                analysis = WithRevision(analysis, rev);
                if (current != null)
                {
                    // the version it replaces: what "Undo last save" puts back
                    try { SafeFile.WriteJson(PreviousPath(runId), WithRevision(current, RevisionOf(current))); }
                    catch (Exception error) { Log.Warning("the previous analysis of " + runId + " could not be kept (" + error.Message + ")"); }
                }
                try
                {
                    SafeFile.WriteJson(AnalysisPath(runId), analysis);
                    Storage.Succeeded(AnalysisPath(runId));
                }
                catch (Exception error)
                {
                    Storage.Failed(AnalysisPath(runId), error);
                    throw new ApiError(500, "the analysis could not be saved (" + error.Message + ")");
                }
                state.AnalysisRev = rev;
                SaveStates();
            }
            else
            {
                // nothing new: the saved version stays (its revision, time and author)
                analysis = current;
                rev = RevisionOf(current);
                if (state.AnalysisRev < rev) { state.AnalysisRev = rev; SaveStates(); }
                if (!File.Exists(AnalysisPath(runId)))
                {
                    try { SafeFile.WriteJson(AnalysisPath(runId), WithRevision(current, rev)); }
                    catch (Exception error) { Storage.Failed(AnalysisPath(runId), error); }
                }
            }
            if (pk == null && record != null && workbook == WorkbookNow && !live(runId))
            {
                // the run's files were deleted: Ctrl+S makes them again, with the analysis
                Restore(runId, record);
                pk = PkOf(runId);
            }
            string xlsx;
            if (pk == null) xlsx = workbook == WorkbookNow ? "failed:the run has no experiment file yet" : "none";
            else
            {
                // the .pk holds this version (unless it does already)
                string savedAt = ValueOf(analysis, "savedAt") as string;
                if (changed || PkAnalysisSavedAt(pk) != savedAt)
                {
                    try
                    {
                        PkFile.Contents read = PkFile.ReadFile(pk, false, false, true);
                        // raw data changed after they were recorded ("changed" in the list): no analysis is added to that file
                        // (it is no failure of the disk: not shown as one)
                        try { PkFile.CheckHash(read); }
                        catch (InvalidDataException)
                        {
                            throw new ApiError(409, "The raw data in this experiment file were changed after they were recorded, so the analysis was not written into it. The analysis is kept by the program.",
                                               "\u6b64\u5b9e\u9a8c\u6587\u4ef6\u91cc\u7684\u539f\u59cb\u6570\u636e\u5728\u8bb0\u5f55\u4e4b\u540e\u88ab\u6539\u52a8\u8fc7\uff0c\u6240\u4ee5\u5206\u6790\u6ca1\u6709\u5199\u8fdb\u8fd9\u4e2a\u6587\u4ef6\uff1b\u5206\u6790\u5df2\u7531\u7a0b\u5e8f\u4fdd\u5b58\u3002");
                        }
                        SafeFile.Write(pk, PkFile.WithAnalysis(read, analysis), PkFile.Validate, keepBackup: true, hideBackup: true);
                        Storage.Succeeded(pk);
                    }
                    catch (Exception error) when (!(error is ApiError))
                    {
                        Storage.Failed(pk, error);
                        throw new ApiError(500, "the experiment file could not be saved (" + error.Message + "); the analysis is kept in the program's folder");
                    }
                }
                if (workbook == WorkbookNow) xlsx = MakeWorkbook(runId, explicitly: true);
                else if (workbook == WorkbookLeave) xlsx = MakeWorkbook(runId, explicitly: false);
                else if (state.XlsxRev >= state.AnalysisRev) xlsx = "current";
                else
                {
                    Waits(runId, again: false);
                    xlsx = "pending";
                }
            }
            if (changed)
            {
                // (the page saves by itself every few seconds while someone works: one line per author and 10 minutes)
                string by = ValueOf(analysis, "savedBy") as string ?? "?";
                if (!logged.TryGetValue(runId, out var last) || last.By != by || DateTime.UtcNow - last.At > TimeSpan.FromMinutes(10))
                {
                    Log.Info("the analysis of " + runId + " was saved by " + by + " (revision " + rev + ")");
                    logged[runId] = (by, DateTime.UtcNow);
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["rev"] = rev, ["savedAt"] = ValueOf(analysis, "savedAt"), ["savedBy"] = ValueOf(analysis, "savedBy"),
                ["file"] = pk != null ? Relative(pk) : null, ["xlsx"] = xlsx, ["previous"] = PreviousOf(runId),
            };
        }

        /// <summary>When the analysis in a .pk was saved (its info.json); null when it has none or cannot be read.</summary>
        static string PkAnalysisSavedAt(string pk)
        {
            try { return ValueOf(PkFile.ReadFile(pk, false).Info, "analysisSavedAt") as string; }
            catch (Exception) { return null; }
        }

        /// <summary>A run whose workbook is behind its analysis waits to be made (since now, or since it began to wait).</summary>
        void Waits(string runId, bool again)
        {
            if (again || !waiting.ContainsKey(runId)) waiting[runId] = DateTime.UtcNow;
            if (workbookTimer == null && !closed)
                workbookTimer = new System.Threading.Timer(_ => Tick(), null, WorkbookCheck, WorkbookCheck);
        }

        void Tick()
        {
            if (closed || !Monitor.TryEnter(flushGate)) return;          // (the last look is still going)
            try { FlushWorkbooks(); }
            catch (Exception error) { Log.Warning("making the workbooks that wait failed (" + error.Message + ")"); }
            finally { Monitor.Exit(flushGate); }
        }

        /// <summary>The workbook of a run made with its saved analysis when it is behind (under the gate). explicitly:
        /// Ctrl+S - also when it is up to date but not there or not the program's, and one that is open or was changed by
        /// someone gets "&lt;name&gt; (analysis ...).xlsx" beside it. Returns "written", "saved-as:&lt;file&gt;", "current" (it
        /// holds the analysis already), "pending" (made later: it is open in a program, or the run is still measuring and
        /// its end makes it), "kept" (someone changed it, or deleted it: left as it is), "none" (the run has no experiment
        /// file) or "failed:&lt;reason&gt;" (tried again later).</summary>
        string MakeWorkbook(string runId, bool explicitly)
        {
            RunState state = StateOf(runId);
            string pk = PkOf(runId);
            if (pk == null)
            {
                // (made with the files, should they be restored)
                waiting.Remove(runId);
                if (state.XlsxRev < state.AnalysisRev) { state.XlsxRev = state.AnalysisRev; SaveStates(); }
                return "none";
            }
            string book = Path.ChangeExtension(pk, XlsxFile.Extension);
            if (state.XlsxRev >= state.AnalysisRev)
            {
                waiting.Remove(runId);
                if (!explicitly || Current(state, book)) return "current";
            }
            string why = null;
            if (!explicitly)
            {
                if (live(runId) || state.Status == "measuring")
                {
                    Waits(runId, again: false);
                    return "pending";
                }
                if (!File.Exists(book) && made.Contains(runId))
                {
                    // the user deleted the workbook of an ended run: not made again by itself (Ctrl+S does)
                    waiting.Remove(runId);
                    state.XlsxRev = state.AnalysisRev;
                    SaveStates();
                    return "kept";
                }
                why = Obstacle(book, state);                // (before the costly part)
            }
            string result;
            if (why == null)
            {
                try
                {
                    Dictionary<string, object> analysis = AnalysisOf(runId, pk);
                    PkFile.Contents read = PkFile.ReadFile(pk, true, false, true);
                    result = WriteWorkbook(Describe(ReadRecord(runId), read, pk), analysis, pk, state, explicitly, out why);
                }
                catch (Exception error)
                {
                    Log.Warning("the workbook of " + runId + " could not be made (" + error.Message + ")");
                    result = "failed:" + error.Message;
                    why = null;
                }
            }
            else result = "kept";
            if (result == "written") waiting.Remove(runId);
            else if (why == "open")
            {
                Waits(runId, again: false);                 // made once the program that has it open lets it go
                if (result == "kept") result = "pending";
            }
            else if (why == "changed")
            {
                // someone changed the workbook: it is theirs, never written over by the program itself
                waiting.Remove(runId);
                if (state.XlsxRev < state.AnalysisRev) { state.XlsxRev = state.AnalysisRev; SaveStates(); }
                if (!explicitly) Log.Info("the workbook " + Path.GetFileName(book) + " was changed by someone: the saved analysis of " + runId +
                                          " is in its .pk, the workbook is left as it is");
            }
            else if (result.StartsWith("failed:")) Waits(runId, again: true);
            return result;
        }

        /// <summary>Whether a run's workbook holds its saved analysis as this version makes it: it is there, unchanged since
        /// the program wrote it with the analysis' revision, by this version.</summary>
        bool Current(RunState state, string book) =>
            state != null && state.XlsxRev >= state.AnalysisRev && state.XlsxVersion == version && state.XlsxSha256 != null
            && File.Exists(book) && SameHash(book, state.XlsxSha256);

        /// <summary>Why a workbook may not simply be replaced: "open" (in a program), "changed" (not the one the program
        /// wrote); null: it may, or it is not there.</summary>
        string Obstacle(string book, RunState state) =>
            !File.Exists(book) ? null : Busy(book) ? "open" : state == null || state.XlsxSha256 == null || !SameHash(book, state.XlsxSha256) ? "changed" : null;

        /// <summary>POST /api/analysis/xlsx?runId=: the workbook of the run now when a save of its analysis waits for it (the
        /// experiment was left or closed; explicitly: Ctrl+S). Returns {"xlsx"} (see MakeWorkbook); null: no such run.</summary>
        public object Workbook(string runId, bool explicitly)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            lock (gate)
            {
                EnsureIndex();
                if (!states.ContainsKey(runId))
                {
                    if (PkOf(runId) == null && ReadRecord(runId) == null) return null;
                    return new Dictionary<string, object> { ["xlsx"] = "current" };   // (no analysis of it was saved here)
                }
                return new Dictionary<string, object> { ["xlsx"] = MakeWorkbook(runId, explicitly) };
            }
        }

        /// <summary>The workbooks that are behind their analyses and due (waiting for WorkbookDelay, or since before this
        /// start; all: every one) are made - every WorkbookCheck, at the start and when the program stops. budget: no new
        /// one is begun after this long. Returns how many were written.</summary>
        public int FlushWorkbooks(bool all = false, TimeSpan? budget = null)
        {
            lock (flushGate)
            {
                var clock = Stopwatch.StartNew();
                List<string> due;
                lock (gate)
                {
                    EnsureIndex();
                    DateTime now = DateTime.UtcNow;
                    foreach (string done in waiting.Keys.Where(id => !states.TryGetValue(id, out RunState s) || s.XlsxRev >= s.AnalysisRev).ToList())
                        waiting.Remove(done);
                    due = states.Where(s => s.Value.AnalysisRev > s.Value.XlsxRev)
                                .Where(s => all || !waiting.TryGetValue(s.Key, out DateTime since) || now - since >= WorkbookDelay)
                                .Select(s => s.Key).ToList();
                }
                int written = 0;
                foreach (string runId in due)
                {
                    if (budget.HasValue && clock.Elapsed > budget.Value) break;
                    lock (gate)
                        if (MakeWorkbook(runId, explicitly: false) == "written") written++;
                }
                return written;
            }
        }

        /// <summary>The program stops: the workbooks that wait are made (for at most budget; the rest at the next start).</summary>
        public void Close(TimeSpan budget)
        {
            closed = true;
            lock (gate)
            {
                workbookTimer?.Dispose();
                workbookTimer = null;
            }
            try
            {
                int written = FlushWorkbooks(all: true, budget: budget);
                if (written > 0) Log.Info(written + " workbook(s) made with their saved analyses before the program stopped");
            }
            catch (Exception error) { Log.Error("making the workbooks that wait failed", error); }
        }

        /// <summary>The run as the workbook shows it: from its record when there is one (filters, gains, events), with the
        /// data and the words of its .pk.</summary>
        ExperimentInfo Describe(Dictionary<string, object> record, PkFile.Contents read, string pk)
        {
            ExperimentInfo fromPk = ExperimentInfo.FromPk(read);
            ExperimentInfo info = record != null ? (describe?.Invoke(record) ?? ExperimentInfo.FromRecord(record, null)) : null;
            if (info == null) info = fromPk;
            else
            {
                info.Run = read.Run;
                info.Name = fromPk.Name;
                info.Status = fromPk.Status;
                info.Done = fromPk.Done;
                info.Planned = Math.Max(info.Planned, fromPk.Planned);
                info.StartedAt = fromPk.StartedAt ?? info.StartedAt;
                info.EndedAt = fromPk.EndedAt ?? info.EndedAt;
                info.ExperimentId = fromPk.ExperimentId ?? info.ExperimentId;
                info.Operator = fromPk.Operator ?? info.Operator;
                info.Interruption = fromPk.Interruption ?? info.Interruption;
            }
            info.RunSha256 = read.RunBytes != null ? PkFile.Sha256(read.RunBytes) : null;
            info.FileName = Path.GetFileName(pk);
            return info;
        }

        /// <summary>The record of a run (System\runs\&lt;run id&gt;.json); null when there is none or it cannot be read.</summary>
        Dictionary<string, object> ReadRecord(string runId)
        {
            if (!SafeRunId(runId)) return null;
            string path = Path.Combine(Layout.Runs, runId + ".json");
            try
            {
                return File.Exists(path) ? Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) as Dictionary<string, object> : null;
            }
            catch (Exception) { return null; }
        }

        sealed class CachedDetails
        {
            public long Written, Size;
            public DateTime Checked;
            public Dictionary<string, object> Details;
        }

        readonly object detailsGate = new object();
        readonly Dictionary<string, CachedDetails> detailsCache = new Dictionary<string, CachedDetails>(StringComparer.Ordinal);
        /// <summary>How long protocol details found are used without looking at the record again (tests change it).</summary>
        public static TimeSpan DetailsRecheck = TimeSpan.FromSeconds(30);

        /// <summary>The protocol details of a run ("protocolDetails" of /api/run, /api/pk, /api/pk-file) from its record
        /// (System\runs\&lt;run id&gt;.json, see ProtocolDetails.cs); null when the run has no record or the record has
        /// none. A record is read again only when it has changed, and looked at no more than every DetailsRecheck (a
        /// record of a run that measures is written at every cycle).</summary>
        public Dictionary<string, object> ProtocolDetailsOf(string runId)
        {
            if (!SafeRunId(runId)) return null;
            DateTime now = DateTime.UtcNow;
            CachedDetails cached;
            lock (detailsGate)
            {
                if (detailsCache.TryGetValue(runId, out cached) && now - cached.Checked < DetailsRecheck) return cached.Details;
            }
            string path = Path.Combine(Layout.Runs, runId + ".json");
            long written = 0, size = -1;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                {
                    written = file.LastWriteTimeUtc.Ticks;
                    size = file.Length;
                }
            }
            catch (Exception) { }
            Dictionary<string, object> details;
            bool failed = false;
            if (cached != null && cached.Written == written && cached.Size == size) details = cached.Details;
            else
            {
                Dictionary<string, object> record = size >= 0 ? ReadRecord(runId) : null;
                // a record that is there but could not be read (held by a virus scanner or a backup, half written) is
                // read again at the next look (after DetailsRecheck), not taken as one without details until it changes
                failed = size >= 0 && record == null;
                details = global::SAAControl.ProtocolDetails.Build(record);
            }
            lock (detailsGate)
            {
                if (detailsCache.Count > 1000) detailsCache.Clear();
                detailsCache[runId] = new CachedDetails { Written = failed ? long.MinValue : written, Size = size, Checked = now, Details = details };
            }
            return details;
        }

        /// <summary>A copy of a run's data with its "protocolDetails" (an answer to the page; the data themselves, as
        /// run.json has them, are left as they are). The run is known by its "id" (else runId). Anything that is not a run
        /// is returned as it is.</summary>
        public object WithProtocolDetails(object run, string runId)
        {
            if (!(run is Dictionary<string, object> data)) return run;
            if (data.TryGetValue("id", out object id) && id is string own && own.Length > 0) runId = own;
            Dictionary<string, object> details = null;
            try { details = ProtocolDetailsOf(runId); }
            catch (Exception error) { Log.Warning("the protocol details of " + runId + " could not be had (" + error.Message + ")"); }
            return new Dictionary<string, object>(data) { ["protocolDetails"] = details };
        }

        /// <summary>A record of a run made from its .pk (when its record was lost); null when the run has no readable .pk.</summary>
        public Dictionary<string, object> RecordFromPk(string runId)
        {
            PkFile.Contents read;
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                if (pk == null) return null;
                read = PkFile.ReadFile(pk, true);
            }
            var info = read.Info;
            object Field(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;
            var run = read.Run;
            var instrument = Field(info, "instrument") as Dictionary<string, object>;
            var cycles = Field(info, "cycles") as Dictionary<string, object>;
            long done = ExperimentInfo.Number(Field(run, "cyclesComplete") ?? Field(cycles, "done"));
            long wells = 0;
            if (Field(run, "channels") is List<object> channels)
                foreach (object c in channels)
                    if (Field(c as Dictionary<string, object>, "wells") is List<object> w) wells += w.Count;
            object temperature = null;
            if (Field(run, "temperature") is List<object> temperatures)
                for (int i = temperatures.Count - 1; i >= 0 && temperature == null; i--) temperature = temperatures[i];
            string key = Field(run, "key") as string ?? "";
            string serial = Field(instrument, "serial") as string ?? "";
            string software = Field(instrument, "softwareVersion") as string;
            return new Dictionary<string, object>
            {
                ["summary"] = new Dictionary<string, object>
                {
                    ["id"] = runId, ["protocol"] = "", ["startedAt"] = LocalTime(Field(info, "startedAt") as string), ["plate"] = Field(run, "plate"),
                    ["plannedCycles"] = Field(run, "plannedCycles") ?? Field(cycles, "planned"), ["cycleSeconds"] = null,
                    ["cyclesStarted"] = Field(run, "cyclesStarted") ?? done, ["cyclesComplete"] = done, ["wells"] = wells,
                    ["wellsInCurrentCycle"] = wells, ["temperature"] = temperature, ["excitation"] = "", ["emission"] = "", ["gain"] = null,
                    ["targetTemperature"] = null, ["dataRev"] = Field(run, "dataRev") ?? 1L, ["updatedAt"] = null, ["settled"] = Field(run, "settled"),
                },
                ["data"] = run,
                ["label"] = key + (serial.Length > 0 ? " \u00b7 " + serial : ""),
                ["instrument"] = new Dictionary<string, object>
                {
                    ["key"] = key, ["serial"] = serial, ["software"] = string.IsNullOrEmpty(software) ? Field(instrument, "software") : software,
                    ["model"] = Field(instrument, "model") ?? "",
                },
                ["status"] = Field(info, "status") ?? "unknown",
                ["endedAt"] = LocalTime(Field(info, "endedAt") as string),
                ["experiment"] = Field(info, "name") ?? "",
                ["experimentId"] = Field(info, "experimentId"),
                ["operator"] = Field(info, "operator"),
                ["interruption"] = Field(info, "interruption"),
                ["events"] = new List<object> { new List<object> { PyNow(), "the record was made again from the experiment file " + Path.GetFileName(PkOfLocked(runId) ?? runId) } },
            };
        }

        string PkOfLocked(string runId)
        {
            lock (gate) return PkOf(runId);
        }

        static string PyNow() => DateTime.Now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);

        ExperimentInfo DescribeRecord(Dictionary<string, object> record)
        {
            if (record == null) return null;
            try { return describe?.Invoke(record) ?? ExperimentInfo.FromRecord(record, null); }
            catch (Exception) { return ExperimentInfo.FromRecord(record, null); }
        }

        // ---- what the page asks for

        sealed class Listed
        {
            public long Size, Written;
            public Dictionary<string, object> Item;
        }

        /// <summary>GET /api/experiments: {"items": [{"name", "file", "runId", "experimentId", "startedAt", "endedAt",
        /// "status", "statusText", "instrument", "cycles": [done, planned], "operator", "samples" (the sample names, for
        /// searching), "hasAnalysis", "problem" (null, "damaged", "changed": the raw data were changed, "newer": made by a
        /// newer version), "xlsx"}] newest first, "removed": [{"runId", "name", "startedAt", "instrument"}] (runs whose
        /// files the user deleted; they can be restored)}. A file is read again only when it has changed.</summary>
        public object List()
        {
            var found = new List<Dictionary<string, object>>();
            var seen = new Dictionary<string, Listed>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Listed> known;
            lock (gate) known = listed;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in PkPaths())
            {
                Dictionary<string, object> item;
                try
                {
                    var file = new FileInfo(path);
                    long size = file.Length, written = file.LastWriteTimeUtc.Ticks;
                    if (known.TryGetValue(path, out Listed cached) && cached.Size == size && cached.Written == written) item = cached.Item;
                    else item = ListItem(path);
                    seen[path] = new Listed { Size = size, Written = written, Item = item };
                }
                catch (Exception) { continue; }        // gone meanwhile
                var copy = new Dictionary<string, object>(item) { ["xlsx"] = File.Exists(Path.ChangeExtension(path, XlsxFile.Extension)) };
                if (copy["runId"] is string runId) present.Add(runId);
                found.Add(copy);
            }
            List<object> removed;
            lock (gate)
            {
                listed = seen;
                EnsureIndex();
                removed = Removed(present);
            }
            var items = found.OrderByDescending(item => item["startedAt"] as string ?? "", StringComparer.Ordinal)
                             .ThenBy(item => item["file"] as string ?? "", StringComparer.Ordinal)
                             .Select(item => (object)item).ToList();
            return new Dictionary<string, object> { ["items"] = items, ["removed"] = removed };
        }

        /// <summary>The line of the list for one .pk; a file that cannot be read is listed as "damaged" (or "newer").</summary>
        Dictionary<string, object> ListItem(string path)
        {
            var item = new Dictionary<string, object>
            {
                ["name"] = Path.GetFileNameWithoutExtension(path), ["file"] = Relative(path), ["runId"] = null, ["experimentId"] = null,
                ["startedAt"] = null, ["endedAt"] = null, ["status"] = null, ["statusText"] = null, ["instrument"] = "",
                ["cycles"] = new List<object> { null, null }, ["operator"] = null, ["samples"] = "", ["hasAnalysis"] = false, ["problem"] = null,
            };
            PkFile.Contents read;
            try { read = PkFile.ReadFile(path, false, true, true); }
            catch (NewerFormatException) { item["problem"] = "newer"; return item; }
            catch (Exception) { item["problem"] = "damaged"; return item; }
            var info = read.Info;
            object Field(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;
            var instrument = Field(info, "instrument") as Dictionary<string, object>;
            var cycles = Field(info, "cycles") as Dictionary<string, object>;
            string runId = read.RunId;
            string runText = null;
            try { runText = new UTF8Encoding(false, true).GetString(read.RunBytes); }
            catch (Exception) { item["problem"] = "damaged"; }
            if (string.IsNullOrEmpty(runId) && runText != null)
            {
                try { runId = (Json.Parse(runText) as Dictionary<string, object>)?.TryGetValue("id", out object id) == true ? id as string : null; }
                catch (Exception) { item["problem"] = "damaged"; }
            }
            if (item["problem"] == null)
            {
                try { PkFile.CheckHash(read); }
                catch (InvalidDataException) { item["problem"] = "changed"; }
            }
            if (read.AnalysisBytes != null)
            {
                try
                {
                    read.Analysis = Json.Parse(new UTF8Encoding(false, true).GetString(read.AnalysisBytes)) as Dictionary<string, object>;
                    item["hasAnalysis"] = read.Analysis != null;
                }
                catch (Exception) { }
            }
            string model = Field(instrument, "model") as string ?? "", serial = Field(instrument, "serial") as string ?? "";
            item["name"] = Field(info, "name") ?? item["name"];
            item["runId"] = runId;
            item["experimentId"] = Field(info, "experimentId");
            item["startedAt"] = Field(info, "startedAt");
            item["endedAt"] = Field(info, "endedAt");
            item["status"] = Field(info, "status");
            item["statusText"] = Field(info, "statusText");
            item["instrument"] = (model + " " + serial).Trim();
            item["cycles"] = new List<object> { Field(cycles, "done"), Field(cycles, "planned") };
            item["operator"] = Field(info, "operator");
            item["samples"] = SampleText(read.Analysis, runText);
            return item;
        }

        static readonly Regex ContentPattern = new Regex("\"content\": \"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

        /// <summary>The sample names of an experiment, joined (for searching): the saved names, and the wells' contents.</summary>
        static string SampleText(Dictionary<string, object> analysis, string runText)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in XlsxFile.Samples(analysis).Values)
                if (seen.Add(name)) names.Add(name);
            if (runText != null)
                foreach (Match m in ContentPattern.Matches(runText))
                {
                    string content;
                    try { content = Json.Parse("\"" + m.Groups[1].Value + "\"") as string; }
                    catch (Exception) { continue; }
                    if (!string.IsNullOrWhiteSpace(content) && seen.Add(content)) names.Add(content);
                    if (names.Count >= 400) break;
                }
            return string.Join(", ", names);
        }

        /// <summary>Runs whose files were made whole once and are gone now (deleted by the user), whose record is there.</summary>
        List<object> Removed(HashSet<string> present)
        {
            var removed = new List<Dictionary<string, object>>();
            bool changed = false;
            foreach (string runId in made)
            {
                if (present.Contains(runId) || live(runId)) continue;
                string record = Path.Combine(Layout.Runs, runId + ".json");
                if (!SafeRunId(runId) || !File.Exists(record)) continue;
                if (!states.TryGetValue(runId, out RunState state) || state.Title == null)
                {
                    // (a run of before this version: described once from its record)
                    ExperimentInfo info = DescribeRecord(ReadRecord(runId));
                    if (info == null) continue;
                    state = states.TryGetValue(runId, out RunState s) ? s : new RunState { ExperimentId = IsGuid(info.ExperimentId) ? info.ExperimentId : NewId() };
                    state.Title = info.Name;
                    state.StartedAt = info.StartedAt;
                    state.Instrument = ((info.Model ?? "") + " " + (info.Serial ?? "")).Trim();
                    state.Key = info.Key;
                    states[runId] = state;
                    changed = true;
                }
                removed.Add(new Dictionary<string, object>
                {
                    ["runId"] = runId, ["name"] = !string.IsNullOrEmpty(state.Name) ? state.Name : state.Title,
                    ["startedAt"] = Clock.WithOffset(state.StartedAt), ["instrument"] = state.Instrument ?? "",
                });
            }
            if (changed) SaveStates();
            return removed.OrderByDescending(r => r["startedAt"] as string ?? "", StringComparer.Ordinal).Select(r => (object)r).ToList();
        }

        /// <summary>The full path of a file given relative to Experiments ('/' or '\' between folders); null when it would
        /// lie outside Experiments.</summary>
        public string Resolve(string file) => ResolveIn(Folder, file);

        /// <summary>The full path of a file given relative to a folder; null when it would lie outside the folder.</summary>
        public static string ResolveIn(string folder, string file)
        {
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(folder) || file.IndexOf('\0') >= 0) return null;
            try
            {
                string rel = file.Replace('/', '\\');
                if (Path.IsPathRooted(rel) || rel.IndexOf(':') >= 0) return null;
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(Path.Combine(root, rel));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            {
                return null;
            }
        }

        /// <summary>A .pk in Experiments that is there; ApiError 400 when the path is not one, null (404) when it is gone.</summary>
        string ExistingPk(string file)
        {
            string path = Resolve(file);
            if (path == null || !path.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase))
                throw new ApiError(400, "not a .pk file in the Experiments folder");
            return File.Exists(path) ? path : null;
        }

        static ApiError Unreadable(Exception error) => error is NewerFormatException
            ? new ApiError(400, PkFile.NewerText, PkFile.NewerTextZh)
            : new ApiError(400, "not a Plate Kinetics Live experiment file (" + error.Message + ")");

        /// <summary>GET /api/pk?file=: the run (run.json) of a .pk in Experiments. null: no such file (404).</summary>
        public object ReadRun(string file)
        {
            string path = ExistingPk(file);
            if (path == null) return null;
            try { return PkFile.ReadFile(path, true).Run; }
            catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException)
            {
                throw Unreadable(error);
            }
        }

        /// <summary>GET /api/open-folder?file=: Explorer on the Experiments folder, or on the folder of one of its files
        /// with the file selected. Only a program that runs in the user's session can show a window: else 409 (the
        /// program's own window opens folders itself).</summary>
        public object OpenFolder(string file, bool inUserSession)
        {
            if (!inUserSession) throw new ApiError(409, "the background service cannot open folders on the desktop; the program's window does");
            string arguments;
            if (string.IsNullOrWhiteSpace(file))
            {
                try { Directory.CreateDirectory(Folder); } catch (Exception) { }
                arguments = Quote(Folder);
            }
            else
            {
                string path = Resolve(file) ?? throw new ApiError(400, "not a file in the Experiments folder");
                if (File.Exists(path)) arguments = "/select," + Quote(path);
                else if (Directory.Exists(path)) arguments = Quote(path);
                else return null;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return new Dictionary<string, object> { ["ok"] = true };
        }

        static string Quote(string path) => "\"" + path + "\"";

        /// <summary>POST /api/experiments/rename?file=&amp;name=: the experiment gets a new name (in its info.json; its run data
        /// stay as they are) and its files the matching name ("&lt;start&gt; &lt;name&gt; - &lt;instrument&gt;"). Returns {"file":
        /// the .pk's new path relative to Experiments}.</summary>
        public object Rename(string file, string name)
        {
            string fresh = SettingsStore.Plain(name, 200);
            if (Naming.Clean(fresh).Length == 0) throw new ApiError(400, "the new name is empty");
            lock (gate)
            {
                EnsureIndex();
                string pk = ExistingPk(file);
                if (pk == null) return null;
                PkFile.Contents read;
                try { read = PkFile.ReadFile(pk, false, false, true); }
                catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException) { throw Unreadable(error); }
                try { PkFile.CheckHash(read); }
                catch (InvalidDataException)
                {
                    // (raw data changed after they were recorded: the file is not written again; no failure of the disk)
                    throw new ApiError(409, "The raw data in this experiment file were changed after they were recorded, so it cannot be renamed.",
                                       "\u6b64\u5b9e\u9a8c\u6587\u4ef6\u91cc\u7684\u539f\u59cb\u6570\u636e\u5728\u8bb0\u5f55\u4e4b\u540e\u88ab\u6539\u52a8\u8fc7\uff0c\u6240\u4ee5\u4e0d\u80fd\u6539\u540d\u3002");
                }
                try
                {
                    SafeFile.Write(pk, PkFile.WithInfo(read, info => info["name"] = fresh), PkFile.Validate, keepBackup: true, hideBackup: true);
                    Storage.Succeeded(pk);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Storage.Failed(pk, error);
                    throw new ApiError(500, "the experiment file could not be changed (" + error.Message + ")");
                }
                var instrument = read.Info.TryGetValue("instrument", out object i) ? i as Dictionary<string, object> : null;
                string Field(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v as string : null;
                string desired = Naming.BaseNameIn(Path.GetDirectoryName(pk), LocalTime(Field(read.Info, "startedAt")), fresh, Field(instrument, "model"), Field(instrument, "serial"));
                string moved = Naming.IsGenerated(Path.GetFileNameWithoutExtension(pk), desired) ? null : MoveFiles(pk, desired);
                if (moved != null) pk = moved;
                string runId = read.RunId;
                if (!string.IsNullOrEmpty(runId))
                {
                    RunState state = StateOf(runId, Field(read.Info, "experimentId"));
                    state.Name = fresh;
                    state.Title = fresh;
                    state.File = Relative(pk);
                    string book = Path.ChangeExtension(pk, XlsxFile.Extension);
                    if (File.Exists(book) && state.XlsxSha256 != null) state.Xlsx = Relative(book);
                    index[runId] = pk;
                    SaveStates();
                    // the workbook shows the name too: made again when it is the program's own and not open
                    if (File.Exists(book) && state.XlsxSha256 != null && SameHash(book, state.XlsxSha256) && !Busy(book))
                    {
                        try
                        {
                            var whole = PkFile.ReadFile(pk, true, true, true);
                            WriteWorkbook(Describe(ReadRecord(runId), whole, pk), AnalysisOf(runId, pk) ?? whole.Analysis, pk, state, explicitly: false);
                        }
                        catch (Exception error) { Log.Warning("the workbook of " + runId + " keeps the old name (" + error.Message + ")"); }
                    }
                }
                Log.Info("the experiment " + Relative(pk) + " was renamed to \"" + fresh + "\"");
                return new Dictionary<string, object> { ["file"] = Relative(pk) };
            }
        }

        /// <summary>POST /api/experiments/regenerate-xlsx?file=: the .xlsx made again from the .pk (with its saved analysis).
        /// Returns {"xlsx": "written|saved-as:&lt;file&gt;|current|failed:&lt;reason&gt;"} ("current": the run's own workbook
        /// holds its saved analysis already, as this version makes it - not written again).</summary>
        public object RegenerateXlsx(string file)
        {
            lock (gate)
            {
                EnsureIndex();
                string pk = ExistingPk(file);
                if (pk == null) return null;
                PkFile.Contents read;
                try { read = PkFile.ReadFile(pk, false); }
                catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException) { throw Unreadable(error); }
                string runId = read.RunId;
                RunState state = !string.IsNullOrEmpty(runId) && states.TryGetValue(runId, out RunState s) ? s : null;
                // the run's own file: with the program's copy of the saved analysis (another file of the run, a copy: as it is)
                bool own = state != null && string.Equals(PkOf(runId), pk, StringComparison.OrdinalIgnoreCase);
                // (one that holds that analysis already, as this version makes it, is not written again: "current")
                if (own && Current(state, Path.ChangeExtension(pk, XlsxFile.Extension)))
                {
                    waiting.Remove(runId);
                    return new Dictionary<string, object> { ["xlsx"] = "current" };
                }
                try { read = PkFile.ReadFile(pk, true, true, true); }
                catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException) { throw Unreadable(error); }
                ExperimentInfo info = Describe(!string.IsNullOrEmpty(runId) ? ReadRecord(runId) : null, read, pk);
                Dictionary<string, object> analysis = own ? AnalysisOf(runId, pk) ?? read.Analysis : read.Analysis;
                string result = WriteWorkbook(info, analysis, pk, own ? state : null, explicitly: true, out string why);
                if (own && result == "written") waiting.Remove(runId);
                else if (own && why == "open") Waits(runId, again: false);
                return new Dictionary<string, object> { ["xlsx"] = result };
            }
        }

        /// <summary>POST /api/experiments/recycle?file=: the .pk and its .xlsx (with their .bak) to the Recycle Bin. Only a
        /// program in the user's session can (the Windows service cannot: 409, the program's window does it).</summary>
        public object Recycle(string file, bool inUserSession)
        {
            if (!inUserSession) throw new ApiError(409, "the background service cannot use the Recycle Bin; the program's window does");
            string pk = ExistingPk(file);
            if (pk == null) return null;
            var files = Companions(pk);
            if (!Recycler(files)) throw new ApiError(500, "the files could not be moved to the Recycle Bin");
            Log.Info("moved to the Recycle Bin: " + string.Join(", ", files.Select(Path.GetFileName)));
            return new Dictionary<string, object> { ["ok"] = true };
        }

        /// <summary>A .pk and the files that belong to it (.pk.bak, .xlsx, .xlsx.bak), those that are there.</summary>
        public static List<string> Companions(string pk)
        {
            string stem = Path.Combine(Path.GetDirectoryName(pk), Path.GetFileNameWithoutExtension(pk));
            return new[] { PkFile.Extension, PkFile.Extension + SafeFile.BackupSuffix, XlsxFile.Extension, XlsxFile.Extension + SafeFile.BackupSuffix }
                .Select(suffix => stem + suffix).Where(File.Exists).ToList();
        }

        /// <summary>POST /api/experiments/restore?runId=: the run's .pk and .xlsx made again from the program's record (with
        /// the saved analysis). Returns {"file"}.</summary>
        public object Restore(string runId)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                if (pk != null) return new Dictionary<string, object> { ["file"] = Relative(pk) };
                Dictionary<string, object> record = ReadRecord(runId);
                if (record == null) return null;
                string file = Restore(runId, record) ?? throw new ApiError(500, "the experiment files could not be written: " + Storage.State()["message"]);
                return new Dictionary<string, object> { ["file"] = file };
            }
        }

        string Restore(string runId, Dictionary<string, object> record)
        {
            ExperimentInfo info = DescribeRecord(record);
            if (info == null || info.RunId != runId) return null;
            if (info.Status == "measuring" && !live(runId))
            {
                info.Status = "interrupted";             // not measuring any more, and its end was not seen
                info.EndedAt = info.EndedAt ?? ExperimentInfo.LastReading(info.StartedAt, info.Run.TryGetValue("seconds", out object s) ? s : null);
            }
            string file = Write(info, xlsx: ExperimentInfo.IsFinal(info.Status), onlyIfNew: false, force: true);
            if (file != null) Log.Info("the experiment files of " + runId + " were made again from the program's record: " + file);
            return file;
        }

        /// <summary>GET /api/export-xlsx?runId=: the run's data now (also of a run still measuring: liveRun) as a workbook.</summary>
        public RawAnswer ExportXlsx(string runId, Dictionary<string, object> liveRun) => Workbook(runId, liveRun, false);

        public const string PkContentType = "application/zip";
        public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        /// <summary>GET /api/download?runId=&amp;kind=pk|xlsx: the experiment's complete file, to save elsewhere (the online
        /// view's downloads). pk: its .pk in Experiments as it is (while it is measuring: as written after the last
        /// complete cycle); a run whose .pk the user deleted, or whose .pk is damaged or was changed, gets one made from the
        /// program's record (with the saved analysis), in memory only. xlsx: a workbook made now from the run's data and its
        /// saved analysis (while it is measuring: the data so far, named "(cycle n of m)"). Named as the files in
        /// Experiments. Nothing is written. liveRun: the run's data now when an instrument shows it (asked only when
        /// needed). null: the run is not known (404).</summary>
        public RawAnswer Download(string runId, string kind, Func<Dictionary<string, object>> liveRun)
        {
            if (kind != "pk" && kind != "xlsx") throw new ApiError(400, "kind: pk or xlsx");
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            if (kind == "xlsx") return Workbook(runId, liveRun?.Invoke(), true);
            string pk;
            lock (gate)
            {
                EnsureIndex();
                pk = PkOf(runId);
            }
            byte[] asItIs = null;
            if (pk != null)
            {
                try
                {
                    using (var stream = new FileStream(pk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        asItIs = memory.ToArray();
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Log.Warning("the experiment file " + Path.GetFileName(pk) + " cannot be read for a download (" + error.Message + ")");
                }
                if (asItIs != null)
                {
                    try
                    {
                        PkFile.Validate(asItIs);
                        return new RawAnswer(asItIs, PkContentType, Path.GetFileName(pk));
                    }
                    catch (NewerFormatException) { return new RawAnswer(asItIs, PkContentType, Path.GetFileName(pk)); }   // a newer program's: as it is
                    catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException)
                    {
                        Log.Warning("the experiment file " + Path.GetFileName(pk) + " is damaged or was changed (" + error.Message + "): a download gets one made from the record");
                    }
                }
            }
            // no file, or a damaged one: made from the record; without a record, a damaged file as it is (the list says what is wrong)
            return PkFromRecord(runId, liveRun) ?? (asItIs != null ? new RawAnswer(asItIs, PkContentType, Path.GetFileName(pk)) : null);
        }

        /// <summary>A .pk of a run made in memory from the program's record (or, while it is measuring, from its data now),
        /// as the program would write it now; null when there is neither. Nothing is written, no state is made.</summary>
        RawAnswer PkFromRecord(string runId, Func<Dictionary<string, object>> liveRun)
        {
            Dictionary<string, object> now = liveRun?.Invoke();       // (asked before the lock: the watcher may be saving)
            ExperimentInfo info;
            string name, sealedAt, fileName;
            Dictionary<string, object> analysis;
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                info = DescribeRecord(ReadRecord(runId));
                if (info == null && now == null) return null;
                if (info == null) info = new ExperimentInfo { RunId = runId, Name = "Experiment", Status = "measuring" };
                if (now != null)
                {
                    info.Run = now;
                    info.Done = ExperimentInfo.Number(now.TryGetValue("cyclesComplete", out object c) ? c : null);
                    info.Planned = Math.Max(info.Planned, ExperimentInfo.Number(now.TryGetValue("plannedCycles", out object p) ? p : null));
                }
                if (info.Run == null) return null;
                if (info.Status == "measuring" && now == null && !live(runId))
                {
                    info.Status = "interrupted";             // not measuring any more, and its end was not seen (as Restore says)
                    info.EndedAt = info.EndedAt ?? ExperimentInfo.LastReading(info.StartedAt, info.Run.TryGetValue("seconds", out object s) ? s : null);
                }
                states.TryGetValue(runId, out RunState state);
                name = state != null && !string.IsNullOrEmpty(state.Name) ? state.Name : info.Name;
                if (state != null && IsGuid(state.ExperimentId)) info.ExperimentId = state.ExperimentId;
                if (string.IsNullOrEmpty(info.Operator)) info.Operator = state?.Operator;
                if (string.IsNullOrEmpty(info.Operator)) info.Operator = null;
                sealedAt = !ExperimentInfo.IsFinal(info.Status) ? null : state?.SealedAt ?? Clock.WithOffset(info.EndedAt) ?? Clock.Now();
                fileName = (pk != null ? Path.GetFileNameWithoutExtension(pk) : Naming.BaseName(info.StartedAt, name, info.Model, info.Serial)) + PkFile.Extension;
                analysis = AnalysisOf(runId, null);
            }
            // (the file is made outside the lock: the watchers go on saving meanwhile)
            byte[] runBytes = PkFile.RunBytes(info.Run);
            info.RunSha256 = PkFile.Sha256(runBytes);
            info.FileName = fileName;
            byte[] analysisBytes = analysis != null ? PkFile.AnalysisBytes(analysis) : null;
            return new RawAnswer(PkFile.Build(InfoJson(info, name, sealedAt, analysis), runBytes, analysisBytes), PkContentType, fileName);
        }

        /// <summary>A workbook of the run's data now and its saved analysis. asFile: named as the run's file in Experiments
        /// (an export is named after the experiment; both get "(cycle n of m)" while it is measuring).</summary>
        RawAnswer Workbook(string runId, Dictionary<string, object> liveRun, bool asFile)
        {
            if (string.IsNullOrEmpty(runId) || !SafeRunId(runId)) throw new ApiError(400, "not a run id");
            ExperimentInfo info;
            Dictionary<string, object> analysis;
            string fileStem = null;
            lock (gate)
            {
                EnsureIndex();
                string pk = PkOf(runId);
                Dictionary<string, object> record = ReadRecord(runId);
                info = DescribeRecord(record);
                if (info == null && pk != null)
                {
                    try { info = ExperimentInfo.FromPk(PkFile.ReadFile(pk, true)); }
                    catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException) { throw Unreadable(error); }
                }
                if (info == null && liveRun == null) return null;
                if (info == null) info = new ExperimentInfo { RunId = runId, Name = "Experiment", Status = "measuring" };
                if (liveRun != null)
                {
                    info.Run = liveRun;
                    info.Done = ExperimentInfo.Number(liveRun.TryGetValue("cyclesComplete", out object c) ? c : null);
                    info.Planned = Math.Max(info.Planned, ExperimentInfo.Number(liveRun.TryGetValue("plannedCycles", out object p) ? p : null));
                }
                if (info.Run == null) return null;
                if (asFile && info.Status == "measuring" && liveRun == null && !live(runId))
                {
                    info.Status = "interrupted";                 // not measuring any more, and its end was not seen (as Restore says)
                    info.EndedAt = info.EndedAt ?? ExperimentInfo.LastReading(info.StartedAt, info.Run.TryGetValue("seconds", out object s) ? s : null);
                }
                if (states.TryGetValue(runId, out RunState state))
                {
                    if (!string.IsNullOrEmpty(state.Name)) info.Name = state.Name;
                    info.ExperimentId = state.ExperimentId;
                    info.Operator = info.Operator ?? state.Operator;
                }
                info.RunSha256 = PkFile.Sha256(PkFile.RunBytes(info.Run));
                info.FileName = pk != null ? Path.GetFileName(pk) : null;
                analysis = AnalysisOf(runId, pk);
                if (asFile && pk != null) fileStem = Path.GetFileNameWithoutExtension(pk);
            }
            string name = fileStem ?? Naming.BaseName(info.StartedAt, info.Name, info.Model, info.Serial);
            if (info.Status == "measuring")
                name += info.Planned > 0 ? " (cycle " + info.Done + " of " + info.Planned + ")" : " (cycle " + info.Done + ")";
            return new RawAnswer(XlsxFile.Build(info, Program, analysis), XlsxContentType, name + XlsxFile.Extension);
        }

        // ---- the records that existed before this version, and files found damaged

        /// <summary>In the background, at the lowest priority: first the experiment files of the last two weeks are checked
        /// (a damaged one goes to System\recovery and is made again), then every record in runsDir whose files were never
        /// made whole gets what it lacks of its .pk and .xlsx, one every few seconds. describe: the run of a record (null:
        /// none); live(runId): the program itself writes that run's files (it is measuring, or ending).</summary>
        public void StartPreparing(string runsDir, Func<Dictionary<string, object>, ExperimentInfo> describe, Func<string, bool> live, WaitHandle stop)
        {
            this.describe = describe;
            this.live = live ?? (_ => false);
            var thread = new Thread(() =>
            {
                try
                {
                    if (stop.WaitOne(PrepareDelay)) return;              // the start first
                    // what saves cut short by a power cut or a crash left: their temporary files, an experiment's
                    // files split by a rename, a .pk behind the program's copy of its analysis
                    RemoveLeftovers();
                    Reunite(stop);
                    CheckFiles(stop);
                    CatchUpAnalyses(stop);
                    // workbooks still behind their saved analyses when the program last stopped
                    try
                    {
                        int written = FlushWorkbooks();
                        if (written > 0) Log.Info(written + " workbook(s) made with analyses saved before the start");
                    }
                    catch (Exception error) { Log.Warning("making the workbooks that waited failed (" + error.Message + ")"); }
                    Prepare(runsDir, describe, this.live, stop);
                }
                catch (Exception error) { Log.Error("making the experiment files of the earlier records stopped", error); }
                finally { Prepared.Set(); }
            }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "experiment-files" };
            thread.Start();
        }

        /// <summary>Set when the background preparation has gone through the records (or stopped).</summary>
        public readonly ManualResetEvent Prepared = new ManualResetEvent(false);

        /// <summary>The background preparation waits this long after the start, and this long between two records.</summary>
        public static TimeSpan PrepareDelay = TimeSpan.FromSeconds(10), PreparePause = TimeSpan.FromSeconds(3);

        /// <summary>What the check of the files at the start did (for the "recovered" problem): lines in English and Chinese.</summary>
        public readonly List<(string En, string Zh)> Repaired = new List<(string, string)>();

        /// <summary>The .pk files that changed in the last 14 days or are still "measuring": one that cannot be read is put
        /// aside into System\recovery and replaced by its previous copy (.pk.bak), else made again from the record.</summary>
        void CheckFiles(WaitHandle stop)
        {
            List<string> paths = PkPaths();
            string recovery = null;
            foreach (string path in paths)
            {
                if (stop.WaitOne(0)) return;
                try
                {
                    var file = new FileInfo(path);
                    bool recent = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(14);
                    string rel = Relative(path);
                    string runId;
                    lock (gate)
                    {
                        EnsureIndex();
                        runId = states.FirstOrDefault(s => string.Equals(s.Value.File, rel, StringComparison.OrdinalIgnoreCase)).Key;
                        if (!recent && !(runId != null && states[runId].Status == "measuring")) continue;
                    }
                    try
                    {
                        PkFile.Validate(SafeFile.ReadAll(path));
                        continue;
                    }
                    catch (NewerFormatException) { continue; }
                    catch (Exception error) when (error is InvalidDataException || error is FormatException || error is DecoderFallbackException)
                    {
                        if (error.Message.Contains("checksum")) continue;    // readable, the raw data edited: listed as "changed"
                        lock (gate)
                        {
                            recovery = recovery ?? Layout.NewRecoveryFolder();
                            Directory.CreateDirectory(recovery);
                            string aside = Path.Combine(recovery, Path.GetFileName(path));
                            File.Move(path, aside);
                            // the program's record first (the name a user gave it and the saved analysis are the program's
                            // too), else the previous copy beside it
                            string how = null;
                            if (runId != null)
                            {
                                Dictionary<string, object> record = ReadRecord(runId);
                                if (record != null && Restore(runId, record) != null) how = "the program's record";
                            }
                            string backup = path + SafeFile.BackupSuffix;
                            try
                            {
                                if (how == null && File.Exists(backup))
                                {
                                    byte[] bytes = SafeFile.ReadAll(backup);
                                    PkFile.Validate(bytes);
                                    SafeFile.Write(path, bytes, PkFile.Validate, keepBackup: false);
                                    how = "its previous copy";
                                }
                            }
                            catch (Exception) { how = null; }
                            Log.Warning("the experiment file " + rel + " was damaged: put aside in " + recovery +
                                        (how != null ? ", made again from " + how : ", and there is nothing to make it again from"));
                            string name = Path.GetFileNameWithoutExtension(path);
                            Repaired.Add(how != null
                                ? ("The damaged experiment file \"" + name + "\" was made again from " + how + ".",
                                   // "\u635f\u574f\u7684\u5b9e\u9a8c\u6587\u4ef6\u201c...\u201d\u5df2\u4ece...\u91cd\u65b0\u751f\u6210\u3002"
                                   "\u635f\u574f\u7684\u5b9e\u9a8c\u6587\u4ef6\u201c" + name + "\u201d\u5df2\u4ece" +
                                   (how == "its previous copy" ? "\u4e0a\u4e00\u4efd\u526f\u672c" : "\u7a0b\u5e8f\u7684\u8bb0\u5f55") + "\u91cd\u65b0\u751f\u6210\u3002")
                                : ("The experiment file \"" + name + "\" is damaged and could not be made again; it was put aside in System\\recovery.",
                                   // "\u5b9e\u9a8c\u6587\u4ef6\u201c...\u201d\u5df2\u635f\u574f\uff0c\u65e0\u6cd5\u91cd\u65b0\u751f\u6210\uff1b\u539f\u6587\u4ef6\u5df2\u632a\u5230 System\recovery\u3002"
                                   "\u5b9e\u9a8c\u6587\u4ef6\u201c" + name + "\u201d\u5df2\u635f\u574f\uff0c\u65e0\u6cd5\u91cd\u65b0\u751f\u6210\uff1b\u539f\u6587\u4ef6\u5df2\u632a\u5230 System\\recovery\u3002"));
                            RepairedChanged?.Invoke();
                        }
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Log.Warning("the experiment file " + path + " could not be checked (" + error.Message + ")");
                }
            }
        }

        /// <summary>Called when the check of the files repaired something (the host updates the "recovered" problem).</summary>
        public Action RepairedChanged;

        // ---- what saves cut short left (a power cut, a crash, the program killed): looked after at the start

        /// <summary>The temporary files of saves an earlier run of the program did not finish (in Experiments and in
        /// System; System\runs is the start-up repair's): the files they were for hold their earlier content.</summary>
        void RemoveLeftovers()
        {
            int removed = SafeFile.RemoveTemporary(Folder, true, startedUtc) + SafeFile.RemoveTemporary(Layout.SystemDir, false, startedUtc)
                          + SafeFile.RemoveTemporary(Layout.Analysis, false, startedUtc) + SafeFile.RemoveTemporary(Layout.Layouts, false, startedUtc);
            if (removed > 0) Log.Info(removed + " temporary file(s) of saves cut short were removed");
        }

        /// <summary>A rename cut short (the .pk has its new name, its workbook and backups the earlier one): the program's
        /// own workbook goes beside its .pk again, and the program's list follows the .pk (its file, the name in it).</summary>
        void Reunite(WaitHandle stop)
        {
            List<string> runs;
            lock (gate)
            {
                EnsureIndex();
                runs = states.Keys.ToList();
            }
            foreach (string runId in runs)
            {
                if (stop.WaitOne(0)) return;
                lock (gate)
                {
                    try
                    {
                        if (!states.TryGetValue(runId, out RunState state)) continue;
                        string pk = PkOf(runId);
                        if (pk == null) continue;
                        bool changed = false;
                        string rel = Relative(pk);
                        if (!string.Equals(state.File, rel, StringComparison.Ordinal))
                        {
                            state.File = rel;
                            changed = true;
                        }
                        // a name the user gave it: the one written into the .pk first (Rename) is the newest
                        if (state.Name != null)
                        {
                            string inPk = null;
                            try { inPk = PkFile.ReadFile(pk, false).Info.TryGetValue("name", out object n) ? n as string : null; } catch (Exception) { }
                            if (!string.IsNullOrEmpty(inPk) && inPk != state.Name)
                            {
                                state.Name = inPk;
                                state.Title = inPk;
                                changed = true;
                            }
                        }
                        string book = Path.ChangeExtension(pk, XlsxFile.Extension);
                        string old = state.Xlsx != null ? Resolve(state.Xlsx) : null;
                        if (!File.Exists(book) && old != null && File.Exists(old) && !string.Equals(old, book, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Path.GetDirectoryName(old), Path.GetDirectoryName(pk), StringComparison.OrdinalIgnoreCase)
                            && !File.Exists(Path.ChangeExtension(old, PkFile.Extension)) && state.XlsxSha256 != null && SameHash(old, state.XlsxSha256))
                        {
                            File.Move(old, book);
                            foreach (var (from, to) in new[] { (old + SafeFile.BackupSuffix, book + SafeFile.BackupSuffix),
                                                               (Path.ChangeExtension(old, PkFile.Extension) + SafeFile.BackupSuffix, pk + SafeFile.BackupSuffix) })
                                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
                            Log.Info("the workbook " + Path.GetFileName(old) + " was put beside its experiment file again as " + Path.GetFileName(book) + " (a rename was cut short)");
                            state.Xlsx = Relative(book);
                            changed = true;
                        }
                        if (changed) SaveStates();
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        Log.Warning("the experiment files of " + runId + " could not be put together again (" + error.Message + ")");
                    }
                }
            }
        }

        /// <summary>A save of an analysis cut short between the program's copy (System\analysis) and the .pk: the .pk gets
        /// the program's copy (the workbook follows: FlushWorkbooks), and the program's list its revision.</summary>
        void CatchUpAnalyses(WaitHandle stop)
        {
            List<string> runs;
            lock (gate)
            {
                EnsureIndex();
                runs = states.Keys.ToList();
            }
            foreach (string runId in runs)
            {
                if (stop.WaitOne(0)) return;
                lock (gate)
                {
                    if (!states.TryGetValue(runId, out RunState state) || !SafeRunId(runId)) continue;
                    string kept = AnalysisPath(runId);
                    if (!File.Exists(kept)) continue;
                    Dictionary<string, object> analysis = ReadAnalysisFile(kept);
                    if (analysis == null) continue;
                    long rev = RevisionOf(analysis);
                    if (state.AnalysisRev < rev)
                    {
                        state.AnalysisRev = rev;
                        SaveStates();
                    }
                    string pk = PkOf(runId);
                    string savedAt = ValueOf(analysis, "savedAt") as string;
                    if (pk == null || PkAnalysisSavedAt(pk) == savedAt) continue;
                    try
                    {
                        PkFile.Contents read = PkFile.ReadFile(pk, false, false, true);
                        SafeFile.Write(pk, PkFile.WithAnalysis(read, analysis), PkFile.Validate, keepBackup: true, hideBackup: true);
                        Storage.Succeeded(pk);
                        Log.Info("the experiment file " + Path.GetFileName(pk) + " got the analysis saved at " + savedAt + " (that save was cut short)");
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is FormatException)
                    {
                        Log.Warning("the saved analysis of " + runId + " could not be put into " + Path.GetFileName(pk) + " (" + error.Message + ")");
                    }
                }
            }
        }

        void Prepare(string runsDir, Func<Dictionary<string, object>, ExperimentInfo> describe, Func<string, bool> live, WaitHandle stop)
        {
            List<string> records;
            try
            {
                records = Directory.EnumerateFiles(runsDir, "*.json")
                    .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => File.GetLastWriteTimeUtc(p)).ToList();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return; }
            int count = 0;
            foreach (string path in records)
            {
                if (stop.WaitOne(0)) return;
                string runId = Path.GetFileNameWithoutExtension(path);
                lock (gate)
                {
                    EnsureIndex();
                    if (made.Contains(runId)) continue;
                }
                try
                {
                    if (live(runId)) continue;
                    var record = Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) as Dictionary<string, object>;
                    ExperimentInfo info = describe(record);
                    if (info == null || info.RunId != runId) continue;
                    if (info.Status == "measuring")
                    {
                        info.Status = "interrupted";     // not measuring any more (live said so), and its end was not seen
                        info.EndedAt = info.EndedAt ?? ExperimentInfo.LastReading(info.StartedAt, info.Run.TryGetValue("seconds", out object s) ? s : null);
                    }
                    if (Save(info, xlsx: true, onlyIfNew: true) != null) count++;
                }
                catch (Exception error)
                {
                    Log.Warning("the experiment files of the record " + Path.GetFileName(path) + " could not be made (" + error.Message + ")");
                    continue;
                }
                if (stop.WaitOne(PreparePause)) return;              // one every few seconds
            }
            if (count > 0) Log.Info("experiment files made for " + count + " earlier records in " + Folder);
        }
    }

    /// <summary>The rule for sealed run data (run.json after the run ended): new data may only add - cycles that were
    /// missing, readings that were not there - never change a value that was recorded.</summary>
    public static class RunGate
    {
        static object Get(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;

        /// <summary>Whether `fresh` holds every value of `old` unchanged (and at least as many complete cycles).</summary>
        public static bool Extends(Dictionary<string, object> old, Dictionary<string, object> fresh)
        {
            if (old == null || fresh == null) return false;
            if (ExperimentInfo.Number(Get(fresh, "cyclesComplete")) < ExperimentInfo.Number(Get(old, "cyclesComplete"))) return false;
            if (!Keeps(Get(old, "seconds") as List<object>, Get(fresh, "seconds") as List<object>)) return false;
            if (!Keeps(Get(old, "temperature") as List<object>, Get(fresh, "temperature") as List<object>)) return false;
            var freshChannels = Channels(fresh);
            foreach (var channel in Channels(old))
            {
                if (!freshChannels.TryGetValue(channel.Key, out var wells)) return false;
                foreach (var well in channel.Value)
                    if (!wells.TryGetValue(well.Key, out var values) || !Keeps(well.Value, values)) return false;
            }
            return true;
        }

        /// <summary>Whether `fresh` has something `old` has not: more complete cycles, or a reading where there was none.</summary>
        public static bool Adds(Dictionary<string, object> old, Dictionary<string, object> fresh)
        {
            if (ExperimentInfo.Number(Get(fresh, "cyclesComplete")) > ExperimentInfo.Number(Get(old, "cyclesComplete"))) return true;
            if (Fills(Get(old, "seconds") as List<object>, Get(fresh, "seconds") as List<object>)) return true;
            var freshChannels = Channels(fresh);
            foreach (var channel in Channels(old))
                if (freshChannels.TryGetValue(channel.Key, out var wells))
                    foreach (var well in channel.Value)
                        if (wells.TryGetValue(well.Key, out var values) && Fills(well.Value, values)) return true;
            foreach (var channel in freshChannels)
            {
                var oldChannels = Channels(old);
                if (!oldChannels.TryGetValue(channel.Key, out var wells)) return true;
                if (channel.Value.Keys.Any(k => !wells.ContainsKey(k))) return true;
            }
            return false;
        }

        static Dictionary<string, Dictionary<string, List<object>>> Channels(Dictionary<string, object> run)
        {
            var result = new Dictionary<string, Dictionary<string, List<object>>>(StringComparer.Ordinal);
            if (!(Get(run, "channels") is List<object> channels)) return result;
            foreach (object item in channels)
            {
                if (!(item is Dictionary<string, object> channel)) continue;
                string name = Convert.ToString(Get(channel, "channel") ?? "", CultureInfo.InvariantCulture);
                var wells = new Dictionary<string, List<object>>(StringComparer.Ordinal);
                if (Get(channel, "wells") is List<object> list)
                    foreach (object w in list)
                        if (w is Dictionary<string, object> well && Get(well, "id") is string id)
                            wells[id] = Get(well, "y") as List<object> ?? new List<object>();
                result[name] = wells;
            }
            return result;
        }

        /// <summary>Every value of `old` (not null) is in `fresh` at the same place, the same number.</summary>
        static bool Keeps(List<object> old, List<object> fresh)
        {
            if (old == null) return true;
            if (fresh == null) return old.All(v => v == null);
            for (int i = 0; i < old.Count; i++)
            {
                if (old[i] == null) continue;
                if (i >= fresh.Count || fresh[i] == null || !Same(old[i], fresh[i])) return false;
            }
            return true;
        }

        static bool Fills(List<object> old, List<object> fresh)
        {
            if (fresh == null) return false;
            if (old == null) return fresh.Any(v => v != null);
            if (fresh.Count > old.Count && fresh.Skip(old.Count).Any(v => v != null)) return true;
            for (int i = 0; i < old.Count && i < fresh.Count; i++)
                if (old[i] == null && fresh[i] != null) return true;
            return false;
        }

        static bool Same(object a, object b)
        {
            double? x = AsDouble(a), y = AsDouble(b);
            if (x.HasValue && y.HasValue) return x.Value.Equals(y.Value);
            return Equals(a, b);
        }

        static double? AsDouble(object value)
        {
            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d: return d;
                default: return null;
            }
        }
    }

    /// <summary>An answer of the API that is a file, not JSON (an exported workbook, the support file).</summary>
    public sealed class RawAnswer
    {
        public readonly byte[] Body;
        public readonly string ContentType, FileName;
        public RawAnswer(byte[] body, string contentType, string fileName)
        {
            Body = body;
            ContentType = contentType;
            FileName = fileName;
        }
    }
}
