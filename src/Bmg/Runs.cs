// Port of BMG/runs.py: watches every Omega installation and keeps the run in progress as plain data.
//
// What the host must call (as BMG/app.py did):
//
//     Log.Open(<dataDir>\log\saacontrol.log);                  // first: the watcher logs what happens to each run
//     List<Install> installs = OmegaLive.Discover(bmgRoots);   // bmgRoots: the --bmg-root folders; null or empty:
//                                                              // BMG under Program Files. A root that does not exist throws.
//     Log.Info(Runs.StartedLogLine(installs));                 // app.py: logging.info("started; installations: %s", [...])
//                                                              // -> "started; installations: [('Omega', '0415-001234', '6.20.101.11')]"
//     var watcher = new Watcher(installs, dataDir, assumeRunning: assumeRunning);   // interval 2.0 s, as app.py;
//                                                              // records go to <dataDir>\runs\<id>.json
//     watcher.Start();      // the first full look runs synchronously (an exception propagates, as in Python), then the
//                           // watcher thread and the thread that looks up the incubator targets start
//     ... the web server calls watcher.State(), RunData(key, id), Recorded() from its own threads ...
//     watcher.Stop();       // at the end: stops the watcher thread (waits at most 5 s)
//
// RunData and Recorded can throw where Python raised (a TypeError for a record of an unexpected shape, or a sort of
// incomparable start times): the server then answers 500, as before.
//
// One thread polls all installations every couple of seconds. While a test runs Omega keeps its settings in
// Temp/TestAkt.DB; the moment that file was written identifies the run, so a run keeps its id across restarts of this
// program. Every change is also written to runs/<id>.json, so a finished run can still be opened later.
//
// Omega keeps Readings.abs open and its modification time does not move while it is written to, so a change is
// detected by reading the file and comparing its content.
//
// The last cycle of a run is in Readings.abs for under a second only (recorded on reader 2: written at 00:42:12.09,
// settings file gone at .943, file emptied at .952), so it cannot be relied on to be seen there, and a test of one cycle
// may not be seen at all. Therefore, when a test has ended, the run is taken once more from Omega's own database of
// finished runs (OmegaLive.SavedRun), which has every cycle. Readings.abs is read through a memory mapping, and Windows
// refuses to shrink a file while it is mapped: that is why the file is read no more often than every Fast seconds and
// not at all for Rest seconds after the settings file has gone.
//
// Since 2.2 (Plate Kinetics Live): a record has a status (measuring, then completed, stopped or interrupted) and is
// written only while its run is measuring and once at its end - a finished run is never written again (a run Omega's
// readings left behind is shown, not recorded anew). Records are saved power-safely (Common\Storage.cs). Every run
// also gets its experiment files (Common\Experiments.cs): the .pk after every completed cycle and at the end, the .xlsx
// at the end, named after Omega's ID1 when one was typed (known at the end), else after the protocol.
//
// A record also keeps the run's experiment id, who ran it (the user logged on at its start) and, for a run that ended
// early, why ({at, reason, detail} from Windows' event logs: Common\Interruptions.cs). Omega's program gone for 2 minutes
// while its test measures ends the run there ("instrument-software-closed" or "-crashed"); so it does for a test whose
// record is still open when the program starts without Omega running (Omega or the PC stopped during the run). A look
// that cannot list the running programs, or cannot read TestAkt.DB although it is there, goes on with what was found
// before (TestAkt.DB: for TestMisses looks at most); only TestAkt.DB not there ends a test at once. After every look the
// watchdog hears from the watcher, the PC is kept awake while a test measures, and a test that brings no new data for
// more than two cycles gives the event "stuck". A reader whose live data cannot be read says so on its card ("unreadable", for a
// version that is not verified: OmegaVersions.UnsupportedText), never "idle" while Omega measures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SAAControl.Bmg
{
    public static class Runs
    {
        public static readonly string[] MetaKeys =
            { "protocol", "startedAt", "plate", "cycleSeconds", "plannedCycles", "excitation", "emission", "gain", "targetTemperature" };
        public static double StartSlack = 120;      // seconds a reading's time may lie ahead of the time since the test started
        public static int OpenFailures = 5;         // polls in a row that may fail to open the readings before it is reported
        public static double Fast = 1.0;            // seconds between two looks at an installation with a test in progress
        public static double Rest = 2.0;            // seconds Omega's files are left alone after a test has ended (it is tidying up)
        public static double ProgramsEvery = 2.0;   // seconds the list of running programs is good for
        public static int SavedTries = 8;           // looks into Omega's database for a test that has just ended ...
        public static double SavedEvery = 4.0;      // ... and the seconds between them
        public static double StoreEvery = 60.0;     // seconds after which a record is written again although no cycle was completed
        public static int ParseFailures = 10;       // reads of Readings.abs in a row that may fail to parse before the reader is "unreadable"
        public static double GoneAfter = 120.0;     // seconds Omega's program may be gone while its test measures before the run has ended
        public static double UnreadableAfter = 30.0;   // seconds TestAkt.DB may be there but unreadable before the reader is "unreadable"
        // looks in a row that go on with the test as it was read before while TestAkt.DB is there but cannot be read just
        // now (held for a moment, being written); after them the test has ended, as when the file is gone
        public static int TestMisses = 5;
        public static double NoDataAfter = 600.0;   // a test of a version not verified that brings no readable data for this long (or two cycles): "unreadable"
        // a test that brings no new data for max(QuietFloor, QuietCycles x its cycle time + QuietSlack) seconds: the event "stuck"
        public static double QuietFloor = 300.0, QuietCycles = 2.0, QuietSlack = 60.0;
        // the messages of an instrument (shown to the user; the page has their Chinese: web/live.js, ZH)
        public const string Waiting = "The run has started; waiting for the data of cycle 1.";
        public const string LeftOver = "Omega is not running: this is the data the last measurement left behind.";
        internal static readonly char[] PathCharacters = { '/', '.', ':', '\\' };

        // What the watcher calls of the other modules. Python looked these up on their module at every call and its tests
        // replaced them there; these fields are that seam (only tests change them).
        public static Func<HashSet<string>> RunningPrograms = OmegaLive.RunningPrograms;
        public static Func<byte[], Snapshot> SnapshotFromContent = OmegaLive.SnapshotFromContent;
        public static Func<Install, double, object, Tuple<Snapshot, Dictionary<string, object>>> SavedRun =
            (install, started, protocol) => OmegaLive.SavedRun(install, started, protocol);
        public static Func<Install, object> LastTarget = OmegaLive.LastTarget;
        public static Func<Install, string> LastModel = OmegaLive.LastModel;
        public static Func<string, object> JsonLoads = Json.Parse;
        public static Func<string, Py.StatResult> TestStat = Py.Stat;      // (of Temp\TestAkt.DB)

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The line app.py logs at the start: "started; installations: [('Omega', '0415-001234', '6.20.101.11')]".</summary>
        public static string StartedLogLine(IList<Install> installs) => "started; installations: " + InstallationsText(installs);

        /// <summary>Python's repr of [(i.key, i.serial, i.version) for i in installs].</summary>
        public static string InstallationsText(IList<Install> installs)
        {
            var list = new List<object>();
            foreach (Install i in installs) list.Add(new object[] { i.Key, i.Serial, i.Version });
            return Py.Repr(list);
        }

        static Dictionary<string, object> PlateOf(long rows, long cols) =>
            new Dictionary<string, object> { ["rows"] = rows, ["cols"] = cols };

        /// <summary>96 unless a well lies outside A-H / 1-12 (the plate itself is not stored with the readings).</summary>
        public static Dictionary<string, object> PlateFromWells(IEnumerable<string> wells)
        {
            foreach (string well in wells)
            {
                var found = OmegaLive.WellIdPattern.Match(well);
                if (!found.Success) throw new PyAttributeError("'NoneType' object has no attribute 'group'");
                string letters = found.Groups[1].Value;
                if (Py.CompareStr(letters, "H") > 0 || letters.Length > 1 || Py.ParseInt(found.Groups[2].Value) > 12)
                    return PlateOf(16, 24);
            }
            return PlateOf(8, 12);
        }

        /// <summary>A value of Omega's settings table as a record can hold it (null for what it cannot).</summary>
        public static object Plain(object value)
        {
            switch (value)
            {
                case null: case bool _: case long _: case int _: return value;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)d;
                case string s: return Py.Len(s) <= 2000 ? Py.Strip(s) : null;
                default: return null;
            }
        }

        /// <summary>Settings of the test in progress from Temp/TestAkt.DB, or null when no test is active.</summary>
        public static Dictionary<string, object> TestSettings(string tempDir)
        {
            string path = Path.Combine(tempDir, "TestAkt.DB");
            double started;
            List<Dictionary<string, object>> rows;
            try
            {
                started = Py.Stat(path).Mtime;
                rows = Paradox.Read(path).Rows;
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.IndexError | PyKinds.KeyError)
                                      || !(e is OutOfMemoryException || e is ThreadAbortException))
            {
                return null;                     // (not there, being written, or of a format not known: the watcher tells which)
            }
            if (rows.Count == 0) return null;
            var row = rows[0];
            object cols = Py.Get(row, "XAnz"), plateRows = Py.Get(row, "YAnz");
            bool known = (Py.Eq(plateRows, 8L) && Py.Eq(cols, 12L)) || (Py.Eq(plateRows, 16L) && Py.Eq(cols, 24L));
            var test = new Dictionary<string, object>();
            test["started"] = started;
            test["protocol"] = Py.OrEmptyStrip(Py.Get(row, "tstname"));
            test["plate"] = known ? new Dictionary<string, object> { ["rows"] = plateRows, ["cols"] = cols } : null;
            test["cycleSeconds"] = Py.Get(row, "Kintime");
            test["plannedCycles"] = Py.Get(row, "Kinetics");
            test["excitation"] = Py.OrEmptyStrip(Py.Get(row, "ExFilt1"));
            test["emission"] = Py.OrEmptyStrip(Py.Get(row, "EmFilt1"));
            test["gain"] = Py.Get(row, "Gain1");
            var all = new Dictionary<string, object>();               // every setting, for the record
            foreach (var entry in row) all[entry.Key] = Plain(entry.Value);
            test["all"] = all;
            return test;
        }

        /// <summary>_count: a positive int, else 0.</summary>
        public static long Count(object value) => value is long l && l > 0 ? l : value is int i && i > 0 ? i : 0;

        /// <summary>How many cycles the run was started with.
        ///
        /// While a test runs its readings have one column per planned cycle. A run that was STOPPED is stored by Omega
        /// with the measured cycles only (seen on reader 1: 300 planned, stopped at 98, stored as 98 of 98), so the number
        /// is remembered from while the test ran (`meta`, see Watcher.Look) and never shrinks.</summary>
        public static long PlannedCycles(Snapshot snap, Dictionary<string, object> meta) =>
            Math.Max(snap.PlannedCycles, Count(meta != null ? Py.Get(meta, "plannedCycles") : null));

        static List<object> Head(List<object> values, int count) => values.GetRange(0, Math.Min(Math.Max(count, 0), values.Count));

        /// <summary>A digest of the complete cycles (times and readings). It changes when a cycle is completed and when a
        /// value of a complete cycle changes, not while the wells of the cycle under way are read: the page fetches the
        /// run again when it changes.</summary>
        public static string Settled(Snapshot snap)
        {
            int count = snap.CyclesComplete;
            var digest = new Blake2b(8);
            void Add(object value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Py.Repr(value));
                digest.Update(bytes, 0, bytes.Length);
            }
            Add(new object[] { (long)count, Head(snap.Times, count) });
            foreach (string channel in snap.Channels.Keys.OrderBy(c => c, Py.StrComparer.Instance))
            {
                var wells = snap.Channels[channel];
                foreach (string well in wells.Keys.OrderBy(w => w, Py.StrComparer.Instance))
                    Add(new object[] { channel, well, Head(wells[well], count) });
            }
            return digest.HexDigest();
        }

        /// <summary>Whether two snapshots have the same complete cycles (times and readings): then their Settled digest is
        /// the same, and it is not made again.</summary>
        public static bool SameComplete(Snapshot a, Snapshot b)
        {
            if (a == null || b == null || a.CyclesComplete != b.CyclesComplete) return false;
            int count = b.CyclesComplete;
            if (!SameHead(a.Times, b.Times, count) || a.Channels.Count != b.Channels.Count) return false;
            foreach (var channel in b.Channels)
            {
                if (!a.Channels.TryGetValue(channel.Key, out var wells) || wells.Count != channel.Value.Count) return false;
                foreach (var well in channel.Value)
                    if (!wells.TryGetValue(well.Key, out var values) || !SameHead(values, well.Value, count)) return false;
            }
            return true;
        }

        static bool SameHead(List<object> a, List<object> b, int count)
        {
            int na = Math.Min(Math.Max(count, 0), a.Count), nb = Math.Min(Math.Max(count, 0), b.Count);
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (!Equals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>The /api/run body for a snapshot. `planned`: the cycles the run was started with, when known.
        /// `digest`: Settled(snap) when the caller has it already (it goes through every reading).</summary>
        public static Dictionary<string, object> RunPayload(object runId, string key, Snapshot snap, object dataRev, object plate,
                                                            object planned = null, string digest = null)
        {
            int started = snap.CyclesStarted;
            List<object> Padded(List<object> values)
            {
                var result = new List<object>(started);
                for (int i = 0; i < started; i++) result.Add(i < values.Count ? values[i] : null);
                return result;
            }
            var channels = new List<object>();
            foreach (string channel in snap.Channels.Keys.OrderBy(c => c, Py.StrComparer.Instance))
            {
                var wells = snap.Channels[channel];
                var list = new List<object>();
                foreach (string well in OmegaLive.SortWells(wells.Keys))
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = well,
                        ["content"] = snap.Contents.TryGetValue(well, out string content) ? content : "",
                        ["y"] = Padded(wells[well]),
                    });
                channels.Add(new Dictionary<string, object> { ["channel"] = channel, ["wells"] = list });
            }
            var payload = new Dictionary<string, object>
            {
                ["id"] = runId, ["key"] = key, ["dataRev"] = dataRev, ["plate"] = plate,
                ["cyclesStarted"] = (long)started, ["cyclesComplete"] = (long)snap.CyclesComplete,
                ["plannedCycles"] = Math.Max(snap.PlannedCycles, Count(planned)),
                ["settled"] = digest ?? Settled(snap),
                ["seconds"] = Padded(snap.Times), ["temperature"] = Padded(snap.Temperature), ["channels"] = channels,
            };
            // Omega's own plate layout (content codes, reading order, dilution): default sample names for the page
            Dictionary<string, object> layout = snap.Layout();
            if (layout != null) payload["omegaLayout"] = layout;
            return payload;
        }

        /// <summary>A run is known by its installation and the moment Omega wrote the settings of its test.</summary>
        public static string RunIdOf(Install install, double started) =>
            install.Key + "-" + PyTime.FromTimestamp(started).ToString("yyyyMMdd'-'HHmmss", Inv);

        /// <summary>Whether readings can be those of the test that began at `started`.
        ///
        /// When a test begins, Readings.abs still holds the previous run until the first cycle is measured. Those readings
        /// give themselves away by a cycle time that this test cannot have reached yet.</summary>
        public static bool Belongs(Snapshot snap, double started)
        {
            object reached = null;
            foreach (object seconds in snap.Times)
                if (seconds != null && (reached == null || Py.Gt(seconds, reached))) reached = seconds;
            return reached == null || Py.Le(reached, PyTime.Time() - started + StartSlack);
        }

        /// <summary>Whether a recorded run and fresh readings are the same measurement (one may have more cycles).</summary>
        public static bool SameRun(object stored, object payload)
        {
            object storedComplete = Py.Get(stored, "cyclesComplete");
            object count = Py.Min(Py.Truthy(storedComplete) ? storedComplete : 0L, Py.Item(payload, "cyclesComplete"));
            if (Py.Lt(count, 1L) || !Py.Eq(Py.Get(stored, "key"), Py.Item(payload, "key"))
                || !Py.Eq(Py.Slice(Py.Item(stored, "seconds"), count), Py.Slice(Py.Item(payload, "seconds"), count)))
                return false;

            List<object> Start(object data)
            {
                var result = new List<object>();
                foreach (object channel in Py.Iter(Py.Item(data, "channels")))
                {
                    object name = Py.Item(channel, "channel");
                    var wells = new List<object>();
                    foreach (object well in Py.Iter(Py.Item(channel, "wells")))
                        wells.Add(new object[] { Py.Item(well, "id"), Py.Slice(Py.Item(well, "y"), count) });
                    result.Add(new object[] { name, wells });
                }
                return result;
            }

            return Py.Eq(Start(stored), Start(payload));
        }
    }

    /// <summary>Python: runs.Watcher.</summary>
    public sealed class Watcher : IWatcher
    {
        /// <summary>A test that is followed, or one that has ended and is to be taken from Omega's database
        /// (Python: the dicts in followed / pending).</summary>
        public sealed class Tracked
        {
            public object Run;
            public double Started;
            public object Protocol;
            public Dictionary<string, object> Meta;
            public long Tries;
            public double Next;
            /// <summary>Catching up after a start: what our record of the run holds (its "data"); null otherwise.</summary>
            public object Recorded;
            /// <summary>When the end of the test was seen (null: catching up, it ended while the program was closed).</summary>
            public string EndedAt;
            /// <summary>A failure to take it from Omega's database is in the log already.</summary>
            public bool Logged;
        }

        /// <summary>What the record of the run that is shown holds beside its readings: every setting of its test as Omega
        /// has it, when each cycle was seen complete (null: not seen while it ran), and what happened to it; and since
        /// 2.2 its status, its end, its experiment name and the reader's model.</summary>
        public sealed class RecordExtra
        {
            public object Settings;
            public List<object> CycleTimes = new List<object>();
            public List<object> Events = new List<object>();
            public bool Exists;                 // the record was there
            public string Status;               // null: a record of before 2.2
            public string EndedAt;
            public string Experiment;
            public string Model;
            public bool Closed;
            // since 2.2: who ran it (the user logged on at its start; an analysis may name another), its experiment id,
            // and why it ended early ({at, reason, detail}; null: it did not, or it is not known)
            public string Operator;
            public string ExperimentId;
            public Dictionary<string, object> Interruption;
        }

        /// <summary>State of one installation. Written by the watcher thread, read under the lock of the watcher.</summary>
        public sealed class Instrument
        {
            public readonly Install Install;
            public bool ProgramRunning;
            public string State = "idle";
            public string Message = "";
            public string Signature;            // digest of Readings.abs at the last successful read
            public Snapshot Latest;             // what Readings.abs held at that read (whichever run it belongs to)
            public int Failures;                // polls in a row that could not open Readings.abs
            public Dictionary<string, object> Test;    // settings of the test in progress (null: no test), read when the file changes
            public (long, long)? TestStamp;     // (modification time, size) of TestAkt.DB at that read
            public double NextLook;             // when an installation without a test is looked at again
            public Tracked Followed;            // the test in progress: its run id, start, protocol and settings
            public Tracked Pending;             // the same for a test that has ended and is not yet taken from Omega's database
            public object Target;               // incubator target of the newest run in Omega's database (degrees C)
            public double RestUntil;            // until then Omega's files are not opened (a test has just ended)
            public object RunId;                // the run that is shown: id, settings, readings
            public Dictionary<string, object> Meta = new Dictionary<string, object>();
            public Snapshot Snap;
            public object DataRev = 0L;
            public string UpdatedAt;
            public string Settled = "";         // Runs.Settled() of Snap
            public bool Unsaved;                // the record could not be written at the last try: written at the next look
            public double StoredAt;             // when the record was written last (monotonic)
            public bool Closed;                 // the run that is shown was taken from Omega's database after its end
            public bool CaughtUp;               // the look for a run that ended while the program was closed is done
            public RecordExtra Extra = new RecordExtra();
            // since 2.2
            public string Status;               // of the run that is shown: "measuring", or its end ("completed", "stopped", "interrupted")
            public bool Sealed;                 // its record is not written (any more): it has ended, or it was not recorded live
            public bool Final;                  // it has ended: the next write is its last (record, .pk and .xlsx)
            public string EndedAt;
            public string Experiment;           // its name: Omega's ID1 when typed (known at the end), else the protocol
            public string Model;                // the reader's model as Omega's database names it ("" not known)
            public long PkCycles = -1;          // complete cycles in its .pk as last written
            public int ParseFailures;           // reads of Readings.abs in a row that did not parse
            public string MessageZh;            // the message in Chinese, where the program has it (else the page translates)
            public double LastNews;             // (monotonic) when the test that measures last brought new data
            public bool StuckSent;              // the event "stuck" was sent for that quiet spell
            public double? GoneSince;           // (monotonic) Omega's program gone while its test measures
            public double? TestUnreadableSince; // (monotonic) TestAkt.DB there but not readable
            public int TestMissed;              // looks in a row that went on with Test as it was (TestAkt.DB not readable just now)
            public double? WaitingSince;        // (monotonic) a test runs and no reading of it could be read yet

            public Instrument(Install install) { Install = install; }
        }

        readonly bool assumeRunning;            // demonstrations with copied files: no Omega process exists
        readonly string runsDir;
        readonly double interval;
        readonly Experiments experiments;       // the experiment files (.pk, .xlsx); null: none are written
        readonly History history;
        readonly object gate = new object();
        long rev;
        HashSet<string> programs;               // the running programs as last listed (null: no list could be had yet)
        double? programsAt;
        int programsFailed;                     // looks in a row that could not list the running programs
        Dictionary<string, ((long, long) Stamp, Dictionary<string, object> Item)> recordedCache =
            new Dictionary<string, ((long, long), Dictionary<string, object>)>();    // file name -> (stamp, its line of Recorded())
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        Thread thread;

        public List<Instrument> Instruments { get; }
        public string RunsDir => runsDir;
        /// <summary>The lock that guards what the web server reads.</summary>
        public object Lock => gate;

        public Watcher(IList<Install> installs, string dataDir, double interval = 2.0, bool assumeRunning = false, Experiments experiments = null)
        {
            this.assumeRunning = assumeRunning;
            runsDir = Path.Combine(dataDir, "runs");
            Directory.CreateDirectory(runsDir);
            SafeFile.RestoreBroken(runsDir);            // a record a power cut left half written: its previous copy
            this.interval = interval;
            this.experiments = experiments;
            Instruments = installs.Select(install => new Instrument(install)).ToList();
            history = new History(installs);
        }

        public Experiments Experiments => experiments;

        // ---- lifecycle

        public void Start()
        {
            // the readers' models first: the names of the experiment files that the first look may write
            foreach (Instrument instrument in Instruments)
                if (string.IsNullOrEmpty(instrument.Model)) instrument.Model = Runs.LastModel(instrument.Install) ?? "";
            Poll();
            FinishLeftOpen();
            thread = new Thread(Loop) { Name = "omega-watcher", IsBackground = true };
            thread.Start();
            new Thread(Targets) { Name = "omega-targets", IsBackground = true }.Start();
            experiments?.StartPreparing(runsDir, Describe, Live, stopEvent);
        }

        /// <summary>What the incubators were last set to, from Omega's databases: the page offers it as the temperature;
        /// and the readers' models, for the names of the experiment files.</summary>
        void Targets()
        {
            try
            {
                foreach (Instrument instrument in Instruments)
                {
                    object target = Runs.LastTarget(instrument.Install);
                    string model = Runs.LastModel(instrument.Install);
                    lock (gate)
                    {
                        if (instrument.Target == null && target != null)
                        {
                            instrument.Target = target;
                            rev += 1;
                        }
                        if (string.IsNullOrEmpty(instrument.Model) && !string.IsNullOrEmpty(model)) instrument.Model = model;
                    }
                }
            }
            catch (Exception error)          // (in Python an unexpected failure ended this thread just the same)
            {
                Log.Error("looking up the incubator targets failed", error);
            }
        }

        public void Stop()
        {
            stopEvent.Set();
            thread?.Join(TimeSpan.FromSeconds(5));
        }

        // ---- the end of a run, and the experiment files

        /// <summary>Whether the program itself still writes a run's files: it is followed, ending, or shown while measuring.</summary>
        bool Live(string runId)
        {
            lock (gate)
                foreach (Instrument i in Instruments)
                    if ((i.Followed != null && Py.Eq(i.Followed.Run, runId)) || (i.Pending != null && Py.Eq(i.Pending.Run, runId))
                        || (Py.Eq(i.RunId, runId) && !i.Sealed))
                        return true;
            return false;
        }

        /// <summary>The description of a run for its experiment files, from its record.</summary>
        public ExperimentInfo Describe(Dictionary<string, object> record)
        {
            ExperimentInfo info = ExperimentInfo.FromRecord(record, "Omega");
            if (info == null) return null;
            string key = Py.Get(info.Run, "key") as string;
            Instrument instrument = Instruments.FirstOrDefault(i => i.Install.Key == key);
            if (instrument != null)
            {
                Install install = instrument.Install;
                if (info.Serial.Length == 0) info.Serial = install.Serial;
                if (info.Software == "Omega" && install.Version.Length > 0) info.Software = "Omega " + install.Version;
                if (info.Model.Length == 0) info.Model = instrument.Model ?? "";
            }
            if (info.Model.Length == 0) info.Model = "BMG";
            return info;
        }

        /// <summary>The run that is shown has ended: its last write (record, .pk and .xlsx) with its final status. status:
        /// null for completed or stopped by its cycles.</summary>
        void Finish(Instrument instrument, string status)
        {
            // (Sealed with Final: its end is written already, and a finished run is never written again - after Omega was
            // gone its settings file can go later, or the next test begin, and the run stays as it ended)
            if (instrument.Snap == null || !Py.Truthy(instrument.RunId) || instrument.Sealed) return;
            instrument.Status = status ?? ExperimentInfo.EndStatus(instrument.Snap.CyclesComplete, Runs.PlannedCycles(instrument.Snap, instrument.Meta));
            if (instrument.EndedAt == null)
                instrument.EndedAt = status == "interrupted" ? ExperimentInfo.LastReading(Py.Get(instrument.Meta, "startedAt") as string, instrument.Snap.Times) ?? PyTime.Now()
                                                             : PyTime.Now();
            if (instrument.Status == "interrupted" && instrument.Extra.Interruption == null)
                instrument.Extra.Interruption = WhyEnded(instrument.Install, instrument.EndedAt, false, instrument.LastNews > 0);
            instrument.Final = true;
            instrument.Sealed = false;
            Note(instrument, "end=" + instrument.Status);
            Store(instrument);
        }

        /// <summary>Why a run of this installation ended early (Windows' event logs around its last reading). gone: Omega's
        /// program was seen gone; seenLive: this program saw the run measuring.</summary>
        static Dictionary<string, object> WhyEnded(Install install, string lastReading, bool gone, bool seenLive)
        {
            DateTime last = Clock.Local(lastReading) ?? DateTime.Now;
            Dictionary<string, object> why = Interruptions.ForRun(last, install != null ? new[] { Path.GetFileName(install.Exe) } : new string[0], gone, seenLive);
            Log.Info((install != null ? install.Key + ": " : "") + "the run ended early: " + Interruptions.Text(why).En);
            return why;
        }

        /// <summary>Omega's program is gone (closed, or it failed) while its test was measuring: the run has ended at its
        /// last reading - Omega does not go on with a test after it starts again.</summary>
        void EndGone(Instrument instrument)
        {
            string last = ExperimentInfo.LastReading(Py.Get(instrument.Meta, "startedAt") as string, instrument.Snap?.Times) ?? PyTime.Now();
            instrument.EndedAt = last;
            instrument.Extra.Interruption = WhyEnded(instrument.Install, last, true, true);
            instrument.GoneSince = null;
            Finish(instrument, "interrupted");
        }

        /// <summary>A run has ended that is not the one shown (its record is on disk): its record gets its final status,
        /// and the run its experiment files. A record that has ended already is left as it is.</summary>
        void FinishStored(object runId, string status)
        {
            foreach (Instrument instrument in Instruments)
                if (Py.Eq(instrument.RunId, runId) && instrument.Snap != null && (!instrument.Sealed || instrument.Final))
                {
                    Finish(instrument, status);          // the run that is shown, as the program has it
                    return;
                }
            string path = RecordPath(runId);
            Dictionary<string, object> record;
            try { record = Runs.JsonLoads(Py.ReadText(path)) as Dictionary<string, object>; }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError)) { return; }
            if (record == null || ExperimentInfo.IsFinal(Py.Get(record, "status"))) return;
            if (!(Py.Get(record, "summary") is Dictionary<string, object> summary) || !(Py.Get(record, "data") is Dictionary<string, object> data)) return;
            long done = Runs.Count(Py.Get(summary, "cyclesComplete")), planned = Runs.Count(Py.Get(summary, "plannedCycles"));
            string final = status ?? ExperimentInfo.EndStatus(done, planned);
            record["status"] = final;
            if (!(Py.Get(record, "endedAt") is string))
                record["endedAt"] = ExperimentInfo.LastReading(Py.Get(summary, "startedAt") as string, Py.Get(data, "seconds")) ?? PyTime.Now();
            if (final == "interrupted" && !(Py.Get(record, "interruption") is Dictionary<string, object>))
            {
                string key = Py.Get(data, "key") as string;
                record["interruption"] = WhyEnded(Instruments.FirstOrDefault(i => i.Install.Key == key)?.Install, (string)record["endedAt"], false, false);
            }
            var events = Py.Get(record, "events") as List<object> ?? new List<object>();
            events.Add(new List<object> { PyTime.Now(), "end=" + final });
            record["events"] = events;
            Log.Info(Py.Str(runId) + ": end=" + final + " (its end was not seen while the program ran)");
            try
            {
                SafeFile.WriteJson(path, record);
                experiments?.Storage.Succeeded(path);
            }
            catch (Exception error) when (Py.Is(error, PyKinds.OSError))
            {
                experiments?.Storage.Failed(path, error);
                return;
            }
            experiments?.Save(Describe(record), xlsx: true);
        }

        /// <summary>After the first look: runs whose record says "measuring" but that are not measuring any more (the
        /// program or the PC stopped while they ran, and the end was not caught up) are interrupted.</summary>
        void FinishLeftOpen()
        {
            foreach (string path in Py.Glob(runsDir, "", ".json"))
            {
                string runId = Path.GetFileNameWithoutExtension(path);
                try
                {
                    if (Live(runId)) continue;
                    string text = Py.ReadText(path);
                    if (text.IndexOf("\"status\": \"measuring\"", StringComparison.Ordinal) < 0) continue;   // (most records: not parsed)
                    var record = Runs.JsonLoads(text) as Dictionary<string, object>;
                    if (record != null && Py.Get(record, "status") as string == "measuring") FinishStored(runId, "interrupted");
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError)) { }
            }
        }

        /// <summary>GET /api/bmg/history, GET /api/bmg/run?key=, POST /api/bmg/save?key= (History.cs).</summary>
        public object Api(string method, string path, Dictionary<string, List<string>> query)
        {
            string key = query.TryGetValue("key", out var keys) && keys.Count > 0 ? keys[0] : "";
            switch (path)
            {
                case "/api/bmg/history": return history.List();
                case "/api/bmg/run": return history.Run(key);
                case "/api/bmg/save":
                    if (experiments == null) throw new ApiError(409, "this program writes no experiment files");
                    ExperimentInfo info = history.Describe(key);
                    if (info == null) return null;
                    string file = experiments.Save(info, xlsx: true, onlyIfNew: true);
                    if (file == null)
                    {
                        var storage = experiments.Storage.State();
                        throw new ApiError(500, "the experiment files could not be written: " + storage["message"]);
                    }
                    return new Dictionary<string, object> { ["file"] = file };
                default: return null;
            }
        }

        /// <summary>The watcher's thread: a look every couple of seconds, for as long as the program runs. A look that fails
        /// (one installation's failure is caught in Poll; this is anything else) never ends it: it is logged (the first,
        /// then every 100th in a row) and the next look comes after a pause that grows to 30 s. A look that never ends is
        /// the watchdog's (Common\Watchdog.cs).</summary>
        void Loop()
        {
            int failures = 0;
            while (true)
            {
                double pause = failures > 0 ? Math.Min(30.0, interval * (1 << Math.Min(failures, 4))) : Busy() ? Runs.Fast : interval;
                if (stopEvent.WaitOne(TimeSpan.FromSeconds(pause))) return;
                try
                {
                    Poll(false);
                    if (failures > 0) Log.Info("the instruments are looked at again (after " + failures + " failed looks)");
                    failures = 0;
                }
                catch (Exception error)
                {
                    failures++;
                    if (failures == 1 || failures % 100 == 0)
                        Log.Error("looking at the instruments failed" + (failures > 1 ? " (" + failures + " times in a row)" : ""), error);
                }
            }
        }

        bool Busy()
        {
            foreach (Instrument i in Instruments) if (i.Test != null || i.Pending != null) return true;
            return false;
        }

        // ---- polling

        /// <summary>One look at the installations. `everything = false` skips those without a test that are not due yet.</summary>
        public void Poll(bool everything = true)
        {
            double now = PyTime.Monotonic();
            if (programsAt == null || everything || now - programsAt.Value >= Runs.ProgramsEvery)
            {
                try
                {
                    programs = Runs.RunningPrograms();
                    programsAt = now;
                    if (programsFailed > 0) Log.Info("the running programs can be listed again (after " + programsFailed + " looks that could not)");
                    programsFailed = 0;
                }
                catch (System.ComponentModel.Win32Exception error)
                {
                    // Windows could not list the running programs just now: that is not "Omega is not running". The last
                    // list stands for this look; the next look asks again.
                    if (programsFailed++ == 0) Log.Warning("the running programs could not be listed (" + error.Message + "): the last list stands until they can");
                }
            }
            HashSet<string> running = programs;     // null: no list could be had since the start
            foreach (Instrument instrument in Instruments)
            {
                if (!everything && instrument.Test == null && instrument.Pending == null && now < instrument.NextLook) continue;
                instrument.NextLook = now + interval;
                bool changed;
                try { changed = PollOne(instrument, running); }
                catch (Exception error)          // one unreadable installation must not stop the others
                {
                    changed = Unreadable(instrument, Py.ExceptionText(error));
                }
                if (changed)
                    lock (gate) rev += 1;
            }
            AfterLook();
        }

        // the instrument's messages in Chinese (the page has the same words in its tables)
        const string ReadingFailedZh = "\u8bfb\u53d6\u51fa\u9519\uff1a";      // "Reading failed: "
        const string WaitingZh = "\u5b9e\u9a8c\u5df2\u5f00\u59cb\uff0c\u7b49\u5f85\u7b2c 1 \u8f6e\u6570\u636e\u3002";
        const string LeftOverZh = "Omega \u6ca1\u6709\u5728\u8fd0\u884c\uff0c\u8fd9\u662f\u4e0a\u6b21\u6d4b\u91cf\u7559\u4e0b\u7684\u6570\u636e\u3002";

        static string LeftOverMessageZh(bool program) => program ? "" : LeftOverZh;

        /// <summary>After every look: the watchdog hears that the looks go on, the PC is kept awake while an instrument
        /// measures, and a measuring run that brings no new data for long gets the event "stuck".</summary>
        void AfterLook()
        {
            var measuring = new List<string>();
            double now = PyTime.Monotonic();
            foreach (Instrument instrument in Instruments)
            {
                if (instrument.State != "measuring")
                {
                    instrument.StuckSent = false;
                    continue;
                }
                measuring.Add(instrument.Install.Key);
                try { CheckQuiet(instrument, now); }
                catch (Exception error) { Log.Warning(instrument.Install.Key + ": the check for new data failed (" + error.Message + ")"); }
            }
            Watchdog.Beat(measuring);
            KeepAwake.Update(measuring);
        }

        /// <summary>No new data for more than two cycles (at least 5 minutes) while the test measures: the event "stuck",
        /// once for that quiet spell (the page's card says the same).</summary>
        void CheckQuiet(Instrument instrument, double now)
        {
            if (instrument.LastNews <= 0)
            {
                instrument.LastNews = now;
                return;
            }
            long cycle = Runs.Count(TestValue(instrument, "cycleSeconds"));
            double limit = Math.Max(Runs.QuietFloor, Runs.QuietCycles * cycle + Runs.QuietSlack), quiet = now - instrument.LastNews;
            if (quiet <= limit || instrument.StuckSent) return;
            instrument.StuckSent = true;
            string key = instrument.Install.Key;
            string minutes = ((long)(quiet / 60)).ToString(CultureInfo.InvariantCulture);
            object protocol = Py.Get(instrument.Meta, "protocol");
            if (!Py.Truthy(protocol)) protocol = TestValue(instrument, "protocol");
            string name = instrument.Experiment ?? (Py.Truthy(protocol) ? Py.Str(protocol) : key);
            string who = experiments?.Settings.DisplayName(key) ?? instrument.Install.Label;
            Problems.Event("stuck", key, Py.Truthy(instrument.RunId) ? Py.Str(instrument.RunId) : null, name,
                           name + " (" + who + "): no new data for " + minutes + " min while measuring: please check the instrument and the Omega software.",
                           // "<name>\uff08<who>\uff09\uff1a\u5df2\u7ecf <n> \u5206\u949f\u6ca1\u6709\u65b0\u6570\u636e\u4e86\uff0c\u8bf7\u770b\u4e00\u4e0b\u4eea\u5668\u548c Omega \u8f6f\u4ef6\u3002"
                           name + "\uff08" + who + "\uff09\uff1a\u5df2\u7ecf " + minutes + " \u5206\u949f\u6ca1\u6709\u65b0\u6570\u636e\u4e86\uff0c\u8bf7\u770b\u4e00\u4e0b\u4eea\u5668\u548c Omega \u8f6f\u4ef6\u3002");
            Note(instrument, "no new data for " + minutes + " min");
        }

        bool Set(Instrument instrument, string state, string message = "", string messageZh = null)
        {
            bool changed = instrument.State != state || instrument.Message != message;
            if (instrument.State != state) Note(instrument, "state=" + state);
            lock (gate)
            {
                instrument.State = state;
                instrument.Message = message;
                instrument.MessageZh = string.IsNullOrEmpty(message) ? "" : messageZh;
            }
            // a reader that cannot be read is also a problem of the program (shown until it reads again)
            if (state == "unreadable") Problems.Set("unreadable", Problems.Error, message, messageZh ?? message, instrument.Install.Key);
            else Problems.Clear("unreadable", instrument.Install.Key);
            return changed;
        }

        static object TestValue(Instrument instrument, string key) => instrument.Test != null ? Py.Get(instrument.Test, key) : null;

        /// <summary>A reader whose live data cannot be read at all: said plainly on its card (for a version that is not
        /// verified, that this version cannot be read) instead of an idle card.</summary>
        bool Unreadable(Instrument instrument, string why)
        {
            Install install = instrument.Install;
            bool verified = OmegaVersions.IsVerified(install.Version);
            if (instrument.State != "unreadable") Log.Warning(install.Key + ": the live data cannot be read (" + why + "; Omega " + (install.Version.Length > 0 ? install.Version : "?") + ")");
            if (verified) return Set(instrument, "unreadable", "Reading failed: " + why, ReadingFailedZh + why);
            var (en, zh) = OmegaVersions.UnsupportedText(install.Version, false);
            return Set(instrument, "unreadable", en, zh);
        }

        /// <summary>What happened to the run that is shown: a line of the log, and of the run's record (the last 200).</summary>
        void Note(Instrument instrument, string text)
        {
            Log.Info(instrument.Install.Key + "[" + (Py.Truthy(instrument.RunId) ? Py.Str(instrument.RunId) : "-") + "]:" + text);
            if (Py.Truthy(instrument.RunId))
            {
                var events = new List<object>(instrument.Extra.Events) { new List<object> { PyTime.Now(), text } };
                if (events.Count > 200) events = events.GetRange(events.Count - 200, 200);
                instrument.Extra.Events = events;
            }
        }

        /// <summary>Keep the time each complete cycle was seen (`live`: seen now, while it runs; else taken from the database).</summary>
        void Seen(Instrument instrument, bool live)
        {
            List<object> times = instrument.Extra.CycleTimes;
            int count = instrument.Snap.CyclesComplete;
            if (count > times.Count)
            {
                if (live) Log.Info(instrument.Install.Key + "[" + Py.Str(instrument.RunId) + "]:cycle=" + count);
                object value = live ? PyTime.Now() : null;
                while (times.Count < count) times.Add(value);
            }
            if (times.Count > count) times.RemoveRange(count, times.Count - count);
        }

        string RecordPath(object runId) => Path.Combine(runsDir, Py.Str(runId) + ".json");

        /// <summary>What the record of a run holds beside its readings: the run goes on with it after a restart.</summary>
        public RecordExtra StoredExtra(object runId)
        {
            var extra = new RecordExtra();
            object record;
            try { record = Runs.JsonLoads(Py.ReadText(RecordPath(runId))); }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError)) { return extra; }
            if (record is Dictionary<string, object> dict)
            {
                extra.Exists = true;
                if (Py.Get(dict, "settings") is Dictionary<string, object> settings) extra.Settings = settings;
                if (Py.Get(dict, "cycleTimes") is List<object> cycleTimes) extra.CycleTimes = new List<object>(cycleTimes);
                if (Py.Get(dict, "events") is List<object> events) extra.Events = new List<object>(events);
                extra.Status = Py.Get(dict, "status") as string;
                extra.EndedAt = Py.Get(dict, "endedAt") as string;
                extra.Experiment = Py.Get(dict, "experiment") as string;
                extra.Closed = Py.Truthy(Py.Get(dict, "closed"));
                if (Py.Get(dict, "instrument") is Dictionary<string, object> instrument) extra.Model = Py.Get(instrument, "model") as string;
                extra.Operator = Py.Get(dict, "operator") as string;
                extra.ExperimentId = Py.Get(dict, "experimentId") as string;
                extra.Interruption = Py.Get(dict, "interruption") as Dictionary<string, object>;
            }
            return extra;
        }

        /// <summary>Who runs an experiment that begins now: the settings' operator, else the user logged on at the PC
        /// (an analysis saved later may name another); null when nobody is logged on.</summary>
        string OperatorNow()
        {
            string chosen = experiments?.Settings.OperatorDefault ?? "";
            if (chosen.Length == 0) chosen = Session.ConsoleUser();
            return string.IsNullOrEmpty(chosen) ? null : chosen;
        }

        /// <summary>The run that is shown is now runId (read from its record): written while it is measuring, never when
        /// it is a finished run or one that was not recorded while it ran.</summary>
        void Shown(Instrument instrument, bool measuring)
        {
            RecordExtra extra = instrument.Extra;
            instrument.Status = measuring ? "measuring" : extra.Status;
            instrument.Sealed = !measuring;
            instrument.Final = false;
            instrument.EndedAt = extra.EndedAt;
            instrument.Experiment = extra.Experiment;
            if (!string.IsNullOrEmpty(extra.Model) && string.IsNullOrEmpty(instrument.Model)) instrument.Model = extra.Model;
            instrument.PkCycles = -1;
        }

        bool PollOne(Instrument instrument, HashSet<string> running)
        {
            bool changed = Look(instrument, running);
            if (!instrument.CaughtUp)
            {
                instrument.CaughtUp = true;
                if (!(instrument.Test != null || instrument.Followed != null || instrument.Pending != null)) CatchUp(instrument);
            }
            if (instrument.Pending != null) changed = Complete(instrument) || changed;
            if (instrument.Unsaved && instrument.Snap != null && Py.Truthy(instrument.RunId)) Store(instrument);
            return changed;
        }

        static Dictionary<string, object> MetaOf(object source)
        {
            var meta = new Dictionary<string, object>();
            foreach (string key in Runs.MetaKeys) meta[key] = Py.Get(source, key);
            return meta;
        }

        bool Look(Instrument instrument, HashSet<string> running)
        {
            Install install = instrument.Install;
            bool program = assumeRunning || (running != null && running.Contains(Py.Casefold(Path.GetFileName(install.Exe))));
            bool changed = program != instrument.ProgramRunning;
            instrument.ProgramRunning = program;
            Dictionary<string, object> test = TestOf(instrument);      // null: no test is in progress
            double testStarted = test != null ? (double)test["started"] : 0;
            if (test != null)
            {
                var meta0 = MetaOf(test);
                meta0["startedAt"] = PyTime.Iso(PyTime.FromTimestamp(testStarted));
                object followed = Runs.RunIdOf(install, testStarted);
                // another test in place of the one followed, without its end seen in between (Omega restarted after a
                // crash, or a new test began within a look): the earlier run ends at its last reading
                if (instrument.Followed != null && !Py.Eq(instrument.Followed.Run, followed)) FinishStored(instrument.Followed.Run, null);
                if (instrument.Followed == null || !Py.Eq(instrument.Followed.Run, followed))
                {
                    instrument.LastNews = PyTime.Monotonic();               // a test that begins: "no new data" counts from now
                    instrument.StuckSent = false;
                }
                instrument.Followed = new Tracked
                {
                    Run = followed, Started = testStarted, Protocol = test["protocol"], Meta = meta0,
                    Tries = Runs.SavedTries, Next = 0.0,
                };
            }
            else if (instrument.Followed != null)                       // that test has ended: Omega stores it in its database
            {
                instrument.Pending = instrument.Followed;
                instrument.Followed = null;
                instrument.Pending.Next = instrument.RestUntil = PyTime.Monotonic() + Runs.Rest;
                instrument.Pending.EndedAt = PyTime.Now();
            }
            if (test == null) instrument.WaitingSince = null;
            // Omega's settings of a test are there but cannot be read (another Omega version): said, not shown as idle
            if (test == null && program && instrument.TestUnreadableSince is double unreadable && PyTime.Monotonic() - unreadable >= Runs.UnreadableAfter)
                return Unreadable(instrument, "the settings of the test (TestAkt.DB) cannot be read") || changed;
            // Omega's program is gone while its test was measuring (closed, or it failed): the run has ended
            bool shownMeasuring = test != null && instrument.Snap != null && instrument.Status == "measuring" && !instrument.Sealed
                                  && Py.Eq(instrument.RunId, Runs.RunIdOf(install, testStarted));
            if (shownMeasuring && !program)
            {
                instrument.GoneSince = instrument.GoneSince ?? PyTime.Monotonic();
                if (PyTime.Monotonic() - instrument.GoneSince.Value >= Runs.GoneAfter)
                {
                    EndGone(instrument);
                    return Set(instrument, "finished", Runs.LeftOver, LeftOverZh) || true;
                }
            }
            else instrument.GoneSince = null;
            if (PyTime.Monotonic() < instrument.RestUntil) return changed;

            byte[] content;
            try { content = OmegaLive.ReadShared(install.Readings); }
            catch (Exception error) when (Py.IsNotFound(error)) { content = new byte[0]; }
            catch (Exception error) when (Py.Is(error, PyKinds.OSError))
            {
                instrument.Failures += 1;
                if (instrument.Failures < Runs.OpenFailures) return changed;      // Omega may hold the file for a moment
                string name = Path.GetFileName(install.Readings), why = Py.StrErrorOr(error);
                // "\u65e0\u6cd5\u6253\u5f00 <file>\uff1a<why>"
                return Set(instrument, "unreadable", "Cannot open " + name + ": " + why, "\u65e0\u6cd5\u6253\u5f00 " + name + "\uff1a" + why) || changed;
            }
            instrument.Failures = 0;
            string signature = Py.Blake2bHex(content, 16);
            bool fresh = signature != instrument.Signature;
            if (fresh)
            {
                try { instrument.Latest = OmegaLive.HoldUnread(Runs.SnapshotFromContent(content)); }
                catch (AbsDb.AbsError error)
                {
                    // Omega is in the middle of writing: tried again on the next tick. A file that never parses (another
                    // Omega version, a measurement kind this program does not know) is said, not shown as a stale state.
                    instrument.ParseFailures += 1;
                    if (instrument.ParseFailures < Runs.ParseFailures) return changed;
                    if (instrument.ParseFailures == Runs.ParseFailures)
                        Log.Warning(install.Key + ": " + Path.GetFileName(install.Readings) + " cannot be read (" + Py.ExceptionText(error) + ")");
                    // (the Python program's words, which the page has in Chinese: "Reading failed: {0}")
                    return Unreadable(instrument, Path.GetFileName(install.Readings) + ": " + Py.ExceptionText(error)) || changed;
                }
                instrument.ParseFailures = 0;
                instrument.Signature = signature;
                if (test != null)
                {
                    instrument.LastNews = PyTime.Monotonic();               // the test brought something new
                    instrument.StuckSent = false;
                }
            }
            Snapshot snap = instrument.Latest;

            if (!snap.HasData || (test != null && !Runs.Belongs(snap, testStarted)))
            {
                if (test != null && program)
                {
                    if (instrument.Snap != null && Py.Eq(instrument.RunId, Runs.RunIdOf(install, testStarted)))
                        // The test we follow: at its end Omega empties the readings a moment before it removes the settings
                        // file. Nothing new; the run stays as it is until that file is gone.
                        return changed;
                    // a test has begun, its first cycle is not in yet
                    if (instrument.RunId != null)
                    {
                        lock (gate)
                        {
                            instrument.RunId = null;
                            instrument.Snap = null;
                            instrument.Meta = new Dictionary<string, object>();
                        }
                        changed = true;
                    }
                    // a version not verified whose test brings nothing readable for long (two cycles, at least 10 minutes):
                    // the card says that this version cannot be read, instead of waiting for ever
                    instrument.WaitingSince = instrument.WaitingSince ?? PyTime.Monotonic();
                    long cycle = Runs.Count(Py.Get(test, "cycleSeconds"));
                    if (!OmegaVersions.IsVerified(install.Version) && PyTime.Monotonic() - instrument.WaitingSince.Value > Math.Max(Runs.NoDataAfter, 2.0 * cycle + 120.0))
                        return Unreadable(instrument, "no readings of the test could be read") || changed;
                    return Set(instrument, "measuring", Runs.Waiting, WaitingZh) || changed;
                }
                if (instrument.Snap == null) return Set(instrument, "idle") || changed;
                return Set(instrument, "finished", program ? "" : Runs.LeftOver, LeftOverMessageZh(program)) || changed;
            }
            instrument.WaitingSince = null;

            object plate = test != null ? Py.Get(test, "plate") : null;
            if (!Py.Truthy(plate)) plate = Py.Get(instrument.Meta, "plate");
            if (!Py.Truthy(plate)) plate = Runs.PlateFromWells(snap.Wells());
            object runId;
            Dictionary<string, object> meta;
            if (test != null)
            {
                runId = Runs.RunIdOf(install, testStarted);
                meta = MetaOf(test);
                meta["startedAt"] = PyTime.Iso(PyTime.FromTimestamp(testStarted));
                meta["plate"] = plate;
            }
            else if (instrument.RunId != null)                    // the run we followed has ended
            {
                runId = instrument.RunId;
                meta = instrument.Meta;
            }
            else (runId, meta) = LeftOver(install, snap, plate);  // found on start: what an earlier run left behind
            // the planned cycles are the most that was ever known for this run (see Runs.PlannedCycles)
            Dictionary<string, object> known = Py.Eq(runId, instrument.RunId) ? instrument.Meta : null;
            long planned = Math.Max(Runs.PlannedCycles(snap, meta), Runs.Count(known != null ? Py.Get(known, "plannedCycles") : null));
            meta = new Dictionary<string, object>(meta) { ["plannedCycles"] = planned != 0 ? (object)planned : null };
            if (fresh || !Py.Eq(runId, instrument.RunId))
            {
                // the digest of the complete cycles (before the lock: it goes through every reading): only made again when
                // they changed - while a 6.40 reader fills the newest cycle well by well, they do not
                string digest = Py.Eq(runId, instrument.RunId) && instrument.Settled.Length > 0 && Runs.SameComplete(instrument.Snap, snap)
                    ? instrument.Settled : Runs.Settled(snap);
                bool news;
                string startedBy = null;
                if (!Py.Eq(runId, instrument.RunId) && test != null && program) startedBy = OperatorNow();   // (not under the lock)
                // A test whose record is still open, first seen while Omega's program is not running (Omega or the PC
                // stopped during the run while this program was not running, and the settings file stayed): it is
                // followed as measuring, so that the end for "Omega is gone" (GoneAfter) ends it as interrupted - only
                // when the readings hold all that its record holds (after a power loss they may hold less: then it is
                // shown as before and its record is not touched).
                bool resumed = !Py.Eq(runId, instrument.RunId) && test != null && !program && running != null && OpenRecordHeld(install, runId, snap);
                lock (gate)
                {
                    bool same = Py.Eq(runId, instrument.RunId);
                    news = !same || digest != instrument.Settled;
                    if (!same)
                    {
                        object storedRev = StoredRev(runId);
                        instrument.RunId = runId;
                        instrument.DataRev = storedRev;
                        instrument.Closed = false;
                        instrument.Extra = StoredExtra(runId);
                        instrument.Closed = instrument.Extra.Closed;
                        Shown(instrument, test != null && (program || resumed));
                        if (resumed) Note(instrument, "followed while Omega is not running");
                        // a run that begins now: who is logged on at the PC runs it (a run recorded before keeps its own)
                        if (!instrument.Extra.Exists && string.IsNullOrEmpty(instrument.Extra.Operator)) instrument.Extra.Operator = startedBy;
                    }
                    else if (test != null && program && instrument.Sealed && !instrument.Final)
                    {
                        // (Omega started again on the settings of a test the program had seen without Omega running)
                        instrument.Status = "measuring";
                        instrument.Sealed = false;
                    }
                    instrument.Meta = meta;
                    instrument.Snap = snap;
                    if (test != null && Py.Truthy(Py.Get(test, "all"))) instrument.Extra.Settings = test["all"];
                    Seen(instrument, test != null);
                    if (!same) Note(instrument, "followed;cycles=" + snap.CyclesComplete);
                    instrument.Settled = digest;
                    instrument.DataRev = Py.AddOne(instrument.DataRev);
                    if (test != null && program) instrument.UpdatedAt = PyTime.Now();   // news of a test that is running
                    else if (resumed)
                    {
                        // it ended at its last reading, also when Omega comes back before GoneAfter and the run ends later
                        // in another way (its settings file goes, the next test begins): Finish keeps an end that is known
                        instrument.UpdatedAt = instrument.EndedAt = ExperimentInfo.LastReading(Py.Get(meta, "startedAt") as string, snap.Times);
                    }
                    else if (!same) instrument.UpdatedAt = null;        // left behind by an earlier run: when it ended is not known
                }
                // The record is written when a cycle is complete (or the run is another one). A 6.40 reader fills the table
                // well by well for a minute or two, a change every second: those go to the record once a minute.
                if (news || PyTime.Monotonic() - instrument.StoredAt >= Runs.StoreEvery) Store(instrument);
                changed = true;
            }
            // (a run this program ended - Omega was gone - is not measuring when Omega comes back with its old settings file)
            bool measuring = program && test != null && !(instrument.Final && Py.Eq(instrument.RunId, Runs.RunIdOf(install, testStarted)));
            return Set(instrument, measuring ? "measuring" : "finished", program ? "" : Runs.LeftOver, LeftOverMessageZh(program)) || changed;
        }

        /// <summary>Whether the record of runId is still open ("measuring") and the readings hold all of it: the same run,
        /// and at least its complete cycles (as Complete asks of Omega's database before it takes a run from there).</summary>
        bool OpenRecordHeld(Install install, object runId, Snapshot snap)
        {
            try
            {
                var record = Runs.JsonLoads(Py.ReadText(RecordPath(runId))) as Dictionary<string, object>;
                if (record == null || Py.Get(record, "status") as string != "measuring") return false;
                object recorded = Py.Item(record, "data");
                object complete = Py.Get(recorded, "cyclesComplete");
                if (Py.Ge(Py.Truthy(complete) ? complete : 0L, 1L)
                    && (Py.Lt((long)snap.CyclesComplete, complete) || !Runs.SameRun(recorded, Runs.RunPayload("", install.Key, snap, 0L, null))))
                    return false;
                return true;
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError | PyKinds.TypeError | PyKinds.IndexError))
            {
                return false;
            }
        }

        /// <summary>A test has ended: take the run as Omega stored it in its own database, which has every cycle. Whatever
        /// comes of it, the run then gets its last write (Finish): with the database's readings, or as it was read.</summary>
        bool Complete(Instrument instrument)
        {
            Tracked pending = instrument.Pending;
            double now = PyTime.Monotonic();
            // catching up after a start (the run ended while the program was closed): its end was not seen
            string unseen = pending.Recorded != null ? "interrupted" : null;
            if (instrument.Test != null)                         // the next test is running already: too late for this one
            {
                instrument.Pending = null;
                FinishStored(pending.Run, unseen);
                return false;
            }
            if (now < pending.Next) return false;
            pending.Tries -= 1;
            pending.Next = now + Runs.SavedEvery;
            Tuple<Snapshot, Dictionary<string, object>> found;
            try { found = Runs.SavedRun(instrument.Install, pending.Started, pending.Protocol); }
            catch (Exception error)                              // Omega is still writing it, or it is not as expected:
            {                                                    // asked again a few times, then the run stays as it was read
                found = null;
                if (!pending.Logged)
                    Log.Warning(instrument.Install.Key + "[" + Py.Str(pending.Run) + "]: taking the run from Omega's database failed (" + Py.ExceptionText(error) + ")");
                pending.Logged = true;
            }
            if (found == null)
            {
                if (pending.Tries <= 0)
                {
                    instrument.Pending = null;
                    FinishStored(pending.Run, unseen);
                }
                return false;
            }
            instrument.Pending = null;
            Snapshot saved = found.Item1;
            Dictionary<string, object> extra = found.Item2;
            Snapshot ours = Py.Eq(instrument.RunId, pending.Run) ? instrument.Snap : null;
            // a run stopped while a cycle was read (6.40: the wells not read yet are 0): those zeros are no readings. A run
            // Omega stored with all its planned cycles has ended normally: every 0 of it is a reading.
            long plannedKnown = Math.Max(Runs.Count(Py.Get(pending.Meta, "plannedCycles")), ours != null ? Runs.PlannedCycles(ours, instrument.Meta) : 0);
            if (plannedKnown == 0 || saved.CyclesStarted < plannedKnown) OmegaLive.HoldUnread(saved);
            if (!saved.HasData)
            {
                FinishStored(pending.Run, unseen);
                return false;
            }
            string key = instrument.Install.Key;
            object recorded = pending.Recorded;                  // catching up after a start: what our record of the run holds
            if (ours == null && recorded != null)
            {
                object complete = Py.Get(recorded, "cyclesComplete");
                if (Py.Ge(Py.Truthy(complete) ? complete : 0L, 1L))
                {
                    if (Py.Lt((long)saved.CyclesComplete, Py.Item(recorded, "cyclesComplete"))
                        || !Runs.SameRun(recorded, Runs.RunPayload("", key, saved, 0L, null)))
                    {
                        FinishStored(pending.Run, unseen);
                        return false;                            // not the run of the record, or less of it: the record stays
                    }
                }
            }
            if (ours != null && ours.CyclesComplete != 0)
            {
                object plate = Plate(instrument);
                if (!Runs.SameRun(Runs.RunPayload("", key, ours, 0L, plate), Runs.RunPayload("", key, saved, 0L, plate)))
                {
                    FinishStored(pending.Run, unseen);
                    return false;                                // same start and protocol, other readings: leave ours alone
                }
            }
            lock (gate)
            {
                if (ours == null)                                // nothing of this run was seen while it ran
                {
                    object storedRev = StoredRev(pending.Run);
                    instrument.RunId = pending.Run;
                    instrument.DataRev = storedRev;
                    instrument.Extra = StoredExtra(pending.Run);
                    Shown(instrument, false);
                    instrument.Sealed = false;                   // (its last write follows)
                    instrument.Meta = new Dictionary<string, object>(pending.Meta);
                    object plate = Py.Get(instrument.Meta, "plate");
                    instrument.Meta["plate"] = Py.Truthy(plate) ? plate : Runs.PlateFromWells(saved.Wells());
                }
                // the experiment's name: Omega's ID1 when the user typed one; the reader's model as the database names it
                if (Py.Get(extra, "id1") is string id1 && Naming.IsTypedId(id1)) instrument.Experiment = id1.Trim();
                if (Py.Get(extra, "model") is string model && model.Length > 0) instrument.Model = model;
                long planned = ours != null ? Runs.PlannedCycles(ours, instrument.Meta) : Runs.Count(Py.Get(instrument.Meta, "plannedCycles"));
                if (ours == null || saved.CyclesComplete >= ours.CyclesComplete) instrument.Snap = saved;
                instrument.Settled = Runs.Settled(instrument.Snap);
                var meta = new Dictionary<string, object>(instrument.Meta);
                foreach (var entry in extra) meta[entry.Key] = entry.Value;
                long most = Math.Max(planned, saved.PlannedCycles);
                meta["plannedCycles"] = most != 0 ? (object)most : null;
                instrument.Meta = meta;
                if (Py.Get(extra, "targetTemperature") != null) instrument.Target = extra["targetTemperature"];
                instrument.DataRev = Py.AddOne(instrument.DataRev);
                instrument.UpdatedAt = PyTime.Now();
                if (recorded != null)                            // it ended while the program was closed: at its last reading
                {
                    object last = null;
                    foreach (object seconds in instrument.Snap.Times)
                        if (seconds != null && (last == null || Py.Gt(seconds, last))) last = seconds;
                    instrument.UpdatedAt = last == null ? null : PyTime.Iso(PyTime.FromTimestamp(pending.Started + Convert.ToDouble(last, CultureInfo.InvariantCulture)));
                }
                instrument.Closed = true;
                Seen(instrument, false);
                Note(instrument, "database;cycles=" + instrument.Snap.CyclesComplete);
                instrument.EndedAt = pending.EndedAt ?? ExperimentInfo.LastReading(Py.Get(instrument.Meta, "startedAt") as string, instrument.Snap.Times);
            }
            Finish(instrument, null);                            // Omega stored the run at its end: completed, or stopped early
            Set(instrument, "finished", instrument.ProgramRunning ? "" : Runs.LeftOver, LeftOverMessageZh(instrument.ProgramRunning));
            return true;
        }

        /// <summary>Once after the start, when no test is in progress: the newest recorded run of this reader may have gone
        /// on and ended while the program was closed, and its record stopped where the program was closed. Omega has
        /// stored the whole run in its database since: it is asked for like a test that has just ended (Complete), which
        /// takes only the run of the same start, protocol and readings, and never less than the record holds.</summary>
        void CatchUp(Instrument instrument)
        {
            try
            {
                string path = null;
                double newest = 0;
                foreach (string candidate in Py.Glob(runsDir, instrument.Install.Key + "-", ".json"))
                {
                    double modified = Py.Stat(candidate).Mtime;
                    if (path == null || modified > newest) { path = candidate; newest = modified; }
                }
                if (path == null) throw new PyValueError("max() iterable argument is empty");
                object record = Runs.JsonLoads(Py.ReadText(path));
                object summary = Py.Item(record, "summary");
                long planned = Runs.Count(Py.Get(summary, "plannedCycles"));
                object done = Py.Get(summary, "cyclesComplete");
                if (!Py.Truthy(done)) done = 0L;
                if (Py.Truthy(Py.Get(record, "closed")) || ExperimentInfo.IsFinal(Py.Get(record, "status"))
                    || !Py.Truthy(Py.Get(summary, "protocol")) || (planned != 0 && Py.Ge(done, planned)))
                    return;                                      // taken from the database before, ended, or nothing is missing
                object id = Py.Item(summary, "id");
                if (!(instrument.RunId == null || Py.Eq(instrument.RunId, id)))
                    return;                                      // the readings left behind are those of another run
                double started = PyTime.FromIsoformatTimestamp(Py.Item(summary, "startedAt"));
                instrument.Pending = new Tracked
                {
                    Run = Py.Item(summary, "id"), Started = started, Protocol = Py.Item(summary, "protocol"), Meta = MetaOf(summary),
                    Tries = 1, Next = 0.0, Recorded = Py.Item(record, "data"),
                };
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError | PyKinds.TypeError))
            {
                // no record yet (max of nothing), or one that cannot be read
            }
        }

        /// <summary>Settings of the test in progress; TestAkt.DB is only parsed again when it has changed. TestAkt.DB not
        /// there: the test has ended (at once). There but not readable just now while a test is followed: see KeepTest.</summary>
        Dictionary<string, object> TestOf(Instrument instrument)
        {
            string path = Path.Combine(instrument.Install.Root, "Temp", "TestAkt.DB");
            Py.StatResult stat;
            try { stat = Runs.TestStat(path); }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError))
            {
                if (!Py.IsNotFound(e) && KeepTest(instrument, Py.ExceptionText(e))) return instrument.Test;
                instrument.Test = null;
                instrument.TestStamp = null;
                instrument.TestUnreadableSince = null;
                instrument.TestMissed = 0;
                return null;
            }
            var stamp = (stat.MtimeNs, stat.Size);
            if (!(instrument.TestStamp.HasValue && instrument.TestStamp.Value == stamp))
            {
                Dictionary<string, object> test = Runs.TestSettings(Path.GetDirectoryName(path));
                if (test == null && instrument.Test != null && !Gone(path) && KeepTest(instrument, "it changed and could not be read"))
                    return instrument.Test;
                instrument.Test = test;
                instrument.TestStamp = instrument.Test != null ? stamp : ((long, long)?)null;   // unreadable (being written): again next time
                // (unreadable for long: another Omega version - Look says so)
                if (instrument.Test != null) instrument.TestUnreadableSince = null;
                else if (instrument.TestUnreadableSince == null) instrument.TestUnreadableSince = PyTime.Monotonic();
            }
            instrument.TestMissed = 0;
            return instrument.Test;
        }

        /// <summary>TestAkt.DB is there but could not be read at this look (held for a moment by another program, being
        /// written: a sharing violation, access denied, a torn read). While a test is followed the look goes on with the
        /// test as it was read before, as if the file had been read: at most Runs.TestMisses looks in a row, after them the
        /// test has ended as when the file is gone. False: no test is followed, or those looks are used up.</summary>
        bool KeepTest(Instrument instrument, string why)
        {
            if (instrument.Test == null || instrument.TestMissed >= Runs.TestMisses) return false;
            if (instrument.TestMissed++ == 0)
                Log.Info(instrument.Install.Key + ": TestAkt.DB could not be read just now (" + why + "): the test goes on as it was read before");
            return true;
        }

        /// <summary>The file is not there (any other answer, an error included, is not "gone").</summary>
        static bool Gone(string path)
        {
            try
            {
                Runs.TestStat(path);
                return false;
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { return Py.IsNotFound(e); }
        }

        /// <summary>Id and settings for readings no test in progress accounts for: those they were recorded under, or an id
        /// from the date of the file.</summary>
        (object RunId, Dictionary<string, object> Meta) LeftOver(Install install, Snapshot snap, object plate)
        {
            var payload = Runs.RunPayload("", install.Key, snap, 0L, plate);
            var recent = Py.Glob(runsDir, install.Key + "-", ".json")
                .Select(path => (Path: path, Modified: Py.Stat(path).Mtime)).ToList()
                .OrderByDescending(x => x.Modified).Select(x => x.Path).ToList();
            foreach (string path in recent.Take(8))
            {
                try
                {
                    object record = Runs.JsonLoads(Py.ReadText(path));
                    if (Runs.SameRun(Py.Item(record, "data"), payload))
                    {
                        object summary = Py.Item(record, "summary");
                        object id = Py.Item(summary, "id");
                        return (id, MetaOf(summary));
                    }
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError | PyKinds.TypeError | PyKinds.IndexError))
                {
                }
            }
            double written;
            try { written = Py.Stat(install.Readings).Mtime; }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { written = PyTime.Time(); }
            DateTime stamp = PyTime.FromTimestamp(written);
            return (install.Key + "-" + stamp.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture),
                    new Dictionary<string, object> { ["startedAt"] = PyTime.Iso(stamp), ["plate"] = plate });
        }

        // ---- persistence

        object Plate(Instrument instrument)
        {
            object plate = Py.Get(instrument.Meta, "plate");
            return Py.Truthy(plate) ? plate : Runs.PlateFromWells(instrument.Snap.Wells());
        }

        /// <summary>Continue numbering after a restart so the page notices that the data moved on.</summary>
        object StoredRev(object runId)
        {
            try { return Py.Item(Py.Item(Runs.JsonLoads(Py.ReadText(RecordPath(runId))), "data"), "dataRev"); }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError)) { return 0L; }
        }

        /// <summary>Write the record of the run that is shown, while it is measuring and once at its end (Finish) - never
        /// for a run that has ended or was not recorded live (Sealed). Saved power-safely, with the previous version as
        /// .bak. A record that cannot be written now (its file is held for a moment, the disk is full) is written at the
        /// next look: that is no failure of reading the instrument. After a completed cycle the run's .pk is written too,
        /// at the end its .pk and .xlsx.</summary>
        void Store(Instrument instrument)
        {
            if (instrument.Sealed) return;
            var record = new Dictionary<string, object>
            {
                ["summary"] = Summary(instrument),
                ["data"] = Runs.RunPayload(instrument.RunId, instrument.Install.Key, instrument.Snap, instrument.DataRev,
                                           Plate(instrument), Py.Get(instrument.Meta, "plannedCycles"), instrument.Settled),
                ["label"] = instrument.Install.Label,
            };
            if (instrument.Closed) record["closed"] = true;
            Install install = instrument.Install;
            record["instrument"] = new Dictionary<string, object>
            {
                ["key"] = install.Key, ["serial"] = install.Serial, ["software"] = install.Version, ["model"] = instrument.Model ?? "",
            };
            record["settings"] = instrument.Extra.Settings;
            record["cycleTimes"] = instrument.Extra.CycleTimes;
            record["events"] = instrument.Extra.Events;
            record["status"] = instrument.Status ?? "measuring";
            record["endedAt"] = instrument.Final ? instrument.EndedAt : null;
            object protocol = Py.Get(instrument.Meta, "protocol");
            record["experiment"] = instrument.Experiment ?? (Py.Truthy(protocol) ? protocol : "");
            // the experiment's id (made once per run, kept in its files), who ran it, why it ended early
            if (instrument.Extra.ExperimentId == null && experiments != null && Py.Truthy(instrument.RunId))
                instrument.Extra.ExperimentId = experiments.IdFor(Py.Str(instrument.RunId));
            record["experimentId"] = instrument.Extra.ExperimentId;
            record["operator"] = instrument.Extra.Operator;
            record["interruption"] = instrument.Extra.Interruption;
            string target = RecordPath(instrument.RunId);
            try
            {
                SafeFile.WriteJson(target, record);
                experiments?.Storage.Succeeded(target);
            }
            catch (Exception error) when (Py.Is(error, PyKinds.OSError))
            {
                if (!instrument.Unsaved)
                    Log.Warning("the record " + Path.GetFileName(target) + " could not be written (" + Py.ExceptionText(error) + "): trying again");
                experiments?.Storage.Failed(target, error);
                instrument.Unsaved = true;
                return;
            }
            instrument.Unsaved = false;
            instrument.StoredAt = PyTime.Monotonic();
            if (instrument.Final) instrument.Sealed = true;      // the last write of this run
            int complete = instrument.Snap.CyclesComplete;
            if (experiments != null && complete > 0 && (instrument.Final || (instrument.Status == "measuring" && complete != instrument.PkCycles)))
            {
                if (experiments.Save(Describe(record), xlsx: instrument.Final) != null) instrument.PkCycles = complete;
            }
        }

        // ---- what the HTTP API returns

        Dictionary<string, object> Summary(Instrument instrument)
        {
            Snapshot snap = instrument.Snap;
            Dictionary<string, object> meta = instrument.Meta;
            long total = 0;
            foreach (var wells in snap.Channels.Values) total += wells.Count;
            long current;
            if (snap.CyclesStarted > snap.CyclesComplete)
            {
                current = 0;
                foreach (var wells in snap.Channels.Values)
                    foreach (var values in wells.Values)
                        if (values.Count >= snap.CyclesStarted && values[snap.CyclesStarted - 1] != null) current++;
            }
            else current = total;
            object temperature = null;
            for (int i = snap.Temperature.Count - 1; i >= 0; i--)
                if (snap.Temperature[i] != null) { temperature = snap.Temperature[i]; break; }
            object protocol = Py.Get(meta, "protocol"), excitation = Py.Get(meta, "excitation"), emission = Py.Get(meta, "emission");
            return new Dictionary<string, object>
            {
                ["id"] = instrument.RunId, ["protocol"] = Py.Truthy(protocol) ? protocol : "", ["startedAt"] = Py.Get(meta, "startedAt"),
                ["plate"] = Plate(instrument),
                ["plannedCycles"] = Runs.PlannedCycles(snap, meta), ["cycleSeconds"] = Py.Get(meta, "cycleSeconds"),
                ["cyclesStarted"] = (long)snap.CyclesStarted, ["cyclesComplete"] = (long)snap.CyclesComplete,
                ["wells"] = total, ["wellsInCurrentCycle"] = current, ["temperature"] = temperature,
                ["excitation"] = Py.Truthy(excitation) ? excitation : "", ["emission"] = Py.Truthy(emission) ? emission : "",
                ["gain"] = Py.Get(meta, "gain"), ["targetTemperature"] = Py.Get(meta, "targetTemperature"),
                ["dataRev"] = instrument.DataRev, ["updatedAt"] = instrument.UpdatedAt, ["settled"] = instrument.Settled,
            };
        }

        /// <summary>GET /api/state.</summary>
        public object State()
        {
            lock (gate)
            {
                var result = new Dictionary<string, object> { ["rev"] = rev };
                var instruments = new List<object>();
                foreach (Instrument i in Instruments)
                {
                    Dictionary<string, object> run = i.Snap != null && Py.Truthy(i.RunId) ? Summary(i) : null;
                    if (run != null)
                    {
                        // since 2.2: the run's status, why it ended early, who runs it, its experiment id
                        if (i.Status != null) run["status"] = i.Status;
                        run["interruption"] = i.Extra.Interruption;
                        run["operator"] = i.Extra.Operator;
                        if (i.Extra.ExperimentId != null) run["experimentId"] = i.Extra.ExperimentId;
                    }
                    var entry = new Dictionary<string, object>
                    {
                        ["key"] = i.Install.Key, ["label"] = i.Install.Label, ["serial"] = i.Install.Serial,
                        ["version"] = i.Install.Version, ["programRunning"] = i.ProgramRunning,
                        ["state"] = i.State, ["message"] = i.Message, ["targetTemperature"] = i.Target,
                        ["run"] = run,
                    };
                    if (!string.IsNullOrEmpty(i.MessageZh)) entry["messageZh"] = i.MessageZh;
                    entry["versionVerified"] = OmegaVersions.IsVerified(i.Install.Version);
                    instruments.Add(entry);
                }
                result["instruments"] = instruments;
                return result;
            }
        }

        /// <summary>Readings of a run: the live one from memory, any other from disk. null when unknown.</summary>
        public object RunData(string key, string runId)
        {
            lock (gate)
            {
                foreach (Instrument i in Instruments)
                    if (i.Install.Key == key && Py.Eq(i.RunId, runId) && i.Snap != null)
                        return Runs.RunPayload(runId, key, i.Snap, i.DataRev, Plate(i), Py.Get(i.Meta, "plannedCycles"), i.Settled);
            }
            if (string.IsNullOrEmpty(runId) || runId.IndexOfAny(Runs.PathCharacters) >= 0) return null;
            try { return Py.Item(Runs.JsonLoads(Py.ReadText(Path.Combine(runsDir, runId + ".json"))), "data"); }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError)) { return null; }
        }

        /// <summary>Runs kept on disk, newest first. The page asks every minute, and the records only grow in number: a
        /// record is read again only when its file has changed.</summary>
        public object Recorded()
        {
            var found = new List<Dictionary<string, object>>();
            var seen = new Dictionary<string, ((long, long) Stamp, Dictionary<string, object> Item)>();
            var cache = recordedCache;
            foreach (string path in Py.Glob(runsDir, "", ".json"))
            {
                try
                {
                    Py.StatResult stat = Py.Stat(path);
                    var stamp = (stat.MtimeNs, stat.Size);
                    string name = Path.GetFileName(path);
                    Dictionary<string, object> item;
                    if (cache.TryGetValue(name, out var known) && known.Stamp == stamp) item = known.Item;
                    else
                    {
                        object record = Runs.JsonLoads(Py.ReadText(path));
                        object summary = Py.Item(record, "summary");
                        item = new Dictionary<string, object>();
                        item["id"] = Py.Item(summary, "id");
                        item["key"] = Py.Item(Py.Item(record, "data"), "key");
                        item["label"] = Py.Get(record, "label", "");
                        item["protocol"] = Py.Get(summary, "protocol", "");
                        item["startedAt"] = Py.Get(summary, "startedAt");
                        item["cycles"] = Py.Get(summary, "cyclesComplete", 0L);
                        item["plannedCycles"] = Py.Get(summary, "plannedCycles");
                    }
                    seen[name] = (stamp, item);
                    found.Add(item);
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError | PyKinds.ValueError | PyKinds.KeyError | PyKinds.TypeError))
                {
                }
            }
            recordedCache = seen;
            return NewestFirst(found);
        }

        /// <summary>sorted(found, key=lambda item: item["startedAt"] or "", reverse=True): stable, compared with Python's
        /// &lt; (a TypeError for start times that do not compare, as in Python).</summary>
        static List<object> NewestFirst(List<Dictionary<string, object>> found)
        {
            var keys = found.Select(item => { object k = item["startedAt"]; return Py.Truthy(k) ? k : ""; }).ToList();
            var order = Enumerable.Range(0, found.Count).ToArray();
            var buffer = new int[order.Length];
            // stable merge sort, greater keys first
            for (int width = 1; width < order.Length; width *= 2)
            {
                for (int low = 0; low < order.Length; low += 2 * width)
                {
                    int mid = Math.Min(low + width, order.Length), high = Math.Min(low + 2 * width, order.Length);
                    int a = low, b = mid, k = low;
                    while (a < mid && b < high)
                        buffer[k++] = Py.Lt(keys[order[a]], keys[order[b]]) ? order[b++] : order[a++];
                    while (a < mid) buffer[k++] = order[a++];
                    while (b < high) buffer[k++] = order[b++];
                }
                Array.Copy(buffer, order, order.Length);
            }
            return order.Select(index => (object)found[index]).ToList();
        }
    }
}
