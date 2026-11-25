// "All BMG experiments": every run in Omega's own databases - MeasurementData.abs of every Omega installation and of
// each of its users' data folders (OmegaLive.Databases) - for the page's list, searchable there by name, date and
// instrument. A run can be shown in the page (GET /api/bmg/run?key=) or saved as experiment files (POST
// /api/bmg/save?key=: its .pk and .xlsx in Experiments). Only read; Omega's files are never written.
//
// Verified with Omega 6.20 and 6.40 (plate-mode kinetics, 96/384 wells); runs of other versions and modes are listed and
// can be opened, but are not guaranteed to be read right.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SAAControl.Bmg
{
    public sealed class History
    {
        readonly IList<Install> installs;
        readonly object gate = new object();
        readonly Dictionary<string, ((long, long) Stamp, List<Entry> Entries)> cache =
            new Dictionary<string, ((long, long), List<Entry>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One run of a database: its row of table "Measure" and the line the list shows.</summary>
        sealed class Entry
        {
            public string Key;
            public Install Install;
            public string Database;
            public Dictionary<string, object> Row;
            public string StartedAt;
            public Dictionary<string, object> Item;
        }

        public History(IList<Install> installs) { this.installs = installs; }

        /// <summary>A short name of a database file for the keys: the first 10 hex digits of the SHA-1 of its path.</summary>
        static string DatabaseId(string path)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
                return Py.Hex(hash).Substring(0, 10);
            }
        }

        List<Entry> Entries()
        {
            var all = new List<Entry>();
            foreach (Install install in installs)
                foreach (string path in OmegaLive.Databases(install))
                {
                    try { all.AddRange(EntriesOf(install, path)); }
                    catch (Exception e) when (Py.Is(e, PyKinds.OSError) || e is AbsDb.AbsError)
                    {
                        Log.Warning("Omega's database " + path + " cannot be read for the list of experiments (" + Py.ExceptionText(e) + ")");
                    }
                }
            return all;
        }

        /// <summary>The runs of one database, read again only when the file has changed.</summary>
        List<Entry> EntriesOf(Install install, string path)
        {
            Py.StatResult stat = Py.Stat(path);
            var stamp = (stat.MtimeNs, stat.Size);
            if (cache.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Entries;
            var entries = new List<Entry>();
            string id = DatabaseId(path);
            // <user folder>\Data\MeasurementData.abs: the folder of the Omega user
            string folderUser = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "") ?? "";
            using (AbsDb.Content view = OmegaLive.SharedView(path))
            {
                AbsDb.Table runs = new AbsDb.Database(view).Table(OmegaLive.Saved);
                foreach (var row in runs != null ? runs.Rows : new List<Dictionary<string, object>>())
                {
                    object table = Py.Get(row, "MessDatei");
                    if (table == null) continue;
                    double? at = OmegaLive.RowTime(row);
                    string startedAt = at.HasValue ? PyTime.Iso(PyTime.FromTimestamp(at.Value)) : null;
                    string user = Py.OrEmptyStrip(Py.Get(row, "User"));
                    var entry = new Entry
                    {
                        Key = id + "-" + Py.Str(table), Install = install, Database = path, Row = row, StartedAt = startedAt,
                    };
                    entry.Item = new Dictionary<string, object>
                    {
                        ["key"] = entry.Key, ["install"] = install.Key, ["user"] = user.Length > 0 ? user : folderUser,
                        ["testName"] = Py.OrEmptyStrip(Py.Get(row, "TstName")), ["id1"] = Py.OrEmptyStrip(Py.Get(row, "IDNumber")),
                        ["startedAt"] = startedAt, ["reader"] = Py.OrEmptyStrip(Py.Get(row, "Reader")),
                        ["serial"] = Py.OrEmptyStrip(Py.Get(row, "SerialNum")),
                        ["wells"] = Py.Get(row, "PlateSize") is long size ? (object)size : null,
                        ["cycles"] = Py.Get(row, "Kinetics") is long cycles ? (object)cycles : null,
                    };
                    entries.Add(entry);
                }
            }
            cache[path] = (stamp, entries);
            return entries;
        }

        /// <summary>GET /api/bmg/history: [{"key", "install", "user", "testName", "id1", "startedAt", "reader", "serial",
        /// "wells" (the plate's wells), "cycles" (the planned cycles)}], newest first.</summary>
        public List<object> List()
        {
            lock (gate)
                return Entries().OrderByDescending(e => e.StartedAt ?? "", StringComparer.Ordinal)
                                .Select(e => (object)new Dictionary<string, object>(e.Item)).ToList();
        }

        Entry Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return Entries().FirstOrDefault(e => e.Key == key);
        }

        /// <summary>The run's readings from its table, as a snapshot (null: the table is not there).</summary>
        static Snapshot Read(Entry entry)
        {
            using (AbsDb.Content view = OmegaLive.SharedView(entry.Database))
            {
                AbsDb.Table table = new AbsDb.Database(view).Table(Py.Str(Py.Get(entry.Row, "MessDatei")));
                if (table == null) return null;
                Snapshot snap = OmegaLive.HoldUnread(OmegaLive.SnapshotFromTable(table));
                if (Py.Get(entry.Row, "Kinetics") is long planned && planned > snap.PlannedCycles) snap.PlannedCycles = planned;
                return snap;
            }
        }

        static string RunId(Entry entry)
        {
            DateTime? start = Naming.Start(entry.StartedAt);
            return start.HasValue
                ? entry.Install.Key + "-" + start.Value.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture)
                : entry.Install.Key + "-db" + Py.Str(Py.Get(entry.Row, "MessDatei"));
        }

        static object Plate(Entry entry, Snapshot snap)
        {
            object size = Py.Get(entry.Row, "PlateSize");
            if (Py.Eq(size, 96L)) return new Dictionary<string, object> { ["rows"] = 8L, ["cols"] = 12L };
            if (Py.Eq(size, 384L)) return new Dictionary<string, object> { ["rows"] = 16L, ["cols"] = 24L };
            return Runs.PlateFromWells(snap.Wells());
        }

        static Dictionary<string, object> Payload(Entry entry, Snapshot snap) =>
            Runs.RunPayload(RunId(entry), entry.Install.Key, snap, 1L, Plate(entry, snap), snap.PlannedCycles);

        /// <summary>GET /api/bmg/run?key=: the run as GET /api/run gives one; null when there is no such run.</summary>
        public Dictionary<string, object> Run(string key)
        {
            lock (gate)
            {
                Entry entry = Find(key);
                if (entry == null) return null;
                Snapshot snap = Read(entry);
                return snap == null ? null : Payload(entry, snap);
            }
        }

        /// <summary>The run's description for its experiment files (POST /api/bmg/save); null when there is no such run.</summary>
        public ExperimentInfo Describe(string key)
        {
            lock (gate)
            {
                Entry entry = Find(key);
                if (entry == null) return null;
                Snapshot snap = Read(entry);
                if (snap == null) return null;
                Dictionary<string, object> run = Payload(entry, snap);
                Dictionary<string, object> row = entry.Row;
                string id1 = Py.OrEmptyStrip(Py.Get(row, "IDNumber"));
                string protocol = Py.OrEmptyStrip(Py.Get(row, "TstName"));
                string serial = Py.OrEmptyStrip(Py.Get(row, "SerialNum"));
                Install install = entry.Install;
                var info = new ExperimentInfo
                {
                    RunId = (string)run["id"],
                    Name = Naming.IsTypedId(id1) ? id1 : protocol.Length > 0 ? protocol : "Experiment",
                    Model = Naming.ReaderModel(Py.Get(row, "Reader") as string),
                    // the serial as this installation spells it (HardwareConfig.log), when it is the same reader
                    Serial = install.Serial.Length > 0 && (serial.Length == 0 || SameSerial(serial, install.Serial)) ? install.Serial : serial,
                    Software = "Omega" + (install.Version.Length > 0 ? " " + install.Version : ""),
                    Protocol = protocol,
                    StartedAt = entry.StartedAt,
                    Done = snap.CyclesComplete,
                    Planned = snap.PlannedCycles,
                    TargetTemperature = OmegaLive.Target(row),
                    Run = run,
                };
                if (info.Model.Length == 0) info.Model = "BMG";
                info.Status = ExperimentInfo.EndStatus(info.Done, info.Planned);
                info.EndedAt = ExperimentInfo.LastReading(info.StartedAt, run["seconds"]);
                string[] excitation = Filters(row, "ExFilt"), emission = Filters(row, "EmFilt"), gain = Filters(row, "Gain");
                int index = 0;
                foreach (object item in (List<object>)run["channels"])
                {
                    string name = (string)((Dictionary<string, object>)item)["channel"];
                    int n = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 ? number - 1 : index;
                    info.Channels.Add(new ChannelInfo
                    {
                        Name = name, Excitation = At(excitation, n), Emission = At(emission, n),
                        Gain = long.TryParse(At(gain, n), NumberStyles.Integer, CultureInfo.InvariantCulture, out long g) ? (object)g : At(gain, n),
                    });
                    index++;
                }
                info.Events.Add(new List<object> { PyTime.Now(), "saved from Omega's database " + entry.Database + " (run " + Py.Str(Py.Get(row, "MessDatei")) + ")" });
                return info;
            }
        }

        /// <summary>Omega keeps the filters and gains of the chromatics in one field, separated by tabs: "448-10\t".</summary>
        static string[] Filters(Dictionary<string, object> row, string field) =>
            (Py.Get(row, field) as string ?? "").Split('\t').Select(p => p.Trim()).ToArray();

        static string At(string[] parts, int index) => index >= 0 && index < parts.Length && parts[index].Length > 0 ? parts[index] : null;

        /// <summary>"415-1234" and "0415-001234" are one reader: the same numbers.</summary>
        static bool SameSerial(string a, string b)
        {
            long[] Numbers(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\d+").Cast<System.Text.RegularExpressions.Match>()
                .Select(m => long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : -1).ToArray();
            long[] x = Numbers(a), y = Numbers(b);
            return x.Length > 0 && x.SequenceEqual(y);
        }
    }
}
