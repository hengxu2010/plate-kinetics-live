// Port of BMG/omega_live.py: live readings from BMG Omega control software, without touching Omega or the reader.
//
// While a measurement runs, every Omega installation keeps the readings taken so far in its own Temp\Readings.abs
// (table "Daten"). This module finds the installations on this PC, reads that file read-only while Omega has it open,
// and turns it into a plain snapshot: wells, cycles, times, temperature. It works the same for every Omega version
// because it only relies on the file Omega itself writes.
//
// Installations are found under Program Files\BMG and, since 2.2, also where the registry says Omega is installed
// (HKLM\SOFTWARE\[WOW6432Node\]BMG Labtechnologies\<family>\<n>\ControlApp\MainDir). Finished runs are looked for in
// every Omega user's own database (the registry's User_<NAME>_Dir, <install>\User and the other <install>\<user>
// folders), not only in <install>\User\Data. Only read, never written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SAAControl.Bmg
{
    /// <summary>One Omega installation (Python: omega_live.Install).</summary>
    public sealed class Install
    {
        /// <summary>Folder name under BMG, e.g. "Omega", "Omega2".</summary>
        public string Key { get; }
        public string Root { get; }
        /// <summary>The control program, e.g. ...\ExeDLL\Omega2.exe.</summary>
        public string Exe { get; }
        /// <summary>Reader serial number, "" when unknown.</summary>
        public string Serial { get; }
        /// <summary>Control software version, "" when unknown.</summary>
        public string Version { get; }

        /// <summary>The data folders of Omega's users as the registry names them (User_NAME_Dir), the user who logged on
        /// last first; empty when the registry does not know the installation.</summary>
        public IReadOnlyList<string> UserDirs { get; }

        public Install(string key, string root, string exe, string serial, string version, IReadOnlyList<string> userDirs = null)
        {
            Key = key;
            Root = root;
            Exe = exe;
            Serial = serial ?? "";
            Version = version ?? "";
            UserDirs = userDirs ?? new List<string>();
        }

        public string Readings => Path.Combine(Root, "Temp", "Readings.abs");

        public string Label => Key + (Serial.Length > 0 ? " " + (char)0x00B7 + " " + Serial : "");
    }

    /// <summary>What Readings.abs holds at one moment. Channels maps channel -> {well id -> values}.</summary>
    public sealed class Snapshot
    {
        public long PlannedCycles;
        /// <summary>Highest cycle that has at least one reading.</summary>
        public int CyclesStarted;
        /// <summary>Highest cycle for which every well has a reading.</summary>
        public int CyclesComplete;
        /// <summary>Seconds since start, one per started cycle.</summary>
        public List<object> Times = new List<object>();
        /// <summary>Degrees C, one per started cycle (null when absent).</summary>
        public List<object> Temperature = new List<object>();
        public Dictionary<string, Dictionary<string, List<object>>> Channels = new Dictionary<string, Dictionary<string, List<object>>>();
        /// <summary>Well id -> Omega content code, e.g. "XA1".</summary>
        public Dictionary<string, string> Contents = new Dictionary<string, string>();
        /// <summary>Well id -> its place in Omega's reading order (1 = read first; the "ReadingOrder" column, where the
        /// file has it).</summary>
        public Dictionary<string, long> ReadingOrder = new Dictionary<string, long>();
        /// <summary>Well id -> Omega's dilution factor of the well (where the file has it).</summary>
        public Dictionary<string, double> Dilutions = new Dictionary<string, double>();
        /// <summary>Other per-cycle rows Omega keeps, e.g. "Humi".</summary>
        public Dictionary<string, List<object>> Extras = new Dictionary<string, List<object>>();

        public bool HasData => CyclesStarted > 0;

        /// <summary>Omega's own plate layout of the run, for default sample names ("omegaLayout" of the run's data): per
        /// well its content code as Omega shows it ("XA1": X sample / B blank / S standard / C control / N negative
        /// control / P positive control, a group letter, a number), the content's kind, Omega's group letter and number,
        /// its place in the reading order and its dilution factor. "name" is the content code; wells of the same code
        /// (replicates) share it as "group". null when Omega gave no contents.</summary>
        public Dictionary<string, object> Layout()
        {
            if (Contents.Count == 0) return null;
            var shared = Contents.Values.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)
                                 .ToDictionary(c => c, c => true, StringComparer.Ordinal);
            var wells = new Dictionary<string, object>();
            foreach (string well in OmegaLive.SortWells(Contents.Keys))
            {
                string content = Contents[well];
                var item = new Dictionary<string, object> { ["name"] = content, ["content"] = content };
                var parts = OmegaLive.ContentParts(content);
                item["kind"] = parts.Kind;
                item["omegaGroup"] = parts.Group;
                item["number"] = parts.Number;
                if (shared.ContainsKey(content)) item["group"] = content;
                if (ReadingOrder.TryGetValue(well, out long order)) item["readingOrder"] = order;
                if (Dilutions.TryGetValue(well, out double dilution)) item["dilution"] = dilution;
                wells[well] = item;
            }
            return new Dictionary<string, object> { ["source"] = "Omega", ["wells"] = wells };
        }

        public List<string> Wells()
        {
            var set = new HashSet<string>();
            foreach (var wells in Channels.Values)
                foreach (string well in wells.Keys) set.Add(well);
            return OmegaLive.SortWells(set);
        }
    }

    public static class OmegaLive
    {
        public const string TableName = "Daten";
        /// <summary>In Omega's own database: one row per finished test run.</summary>
        public const string Saved = "Measure";
        static readonly Regex CycleField = new Regex(@"^M(\d+)$", RegexOptions.CultureInvariant);
        public static readonly Regex WellIdPattern = new Regex(@"^([A-Za-z]{1,2})0*(\d{1,2})$", RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------- installations

        static IEnumerable<string> BmgRoots()
        {
            foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string folder = Environment.GetEnvironmentVariable(variable);
                // (as a 64-bit process sees it, which the Python program was; a 32-bit process would get "(x86)" twice)
                if (variable == "ProgramFiles" && !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
                    folder = Environment.GetEnvironmentVariable("ProgramW6432") ?? folder;
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(Path.Combine(folder, "BMG")))
                    yield return Path.Combine(folder, "BMG");
            }
        }

        static readonly Regex SerialPattern = new Regex(
            "Reader serial number:[" + Py.SpaceClass + "]*([^" + Py.SpaceClass + "]+)", RegexOptions.CultureInvariant);

        static string Serial(string root)
        {
            string text;
            try
            {
                byte[] bytes = Py.ReadBytes(Path.Combine(root, "HardwareConfig.log"));
                text = Py.DecodeLatin1(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { return ""; }
            Match found = SerialPattern.Match(text);
            return found.Success ? found.Groups[1].Value : "";
        }

        static string FileVersion(string path)
        {
            try
            {
                int size = Native.GetFileVersionInfoSizeW(path, IntPtr.Zero);
                if (size == 0) return "";
                var data = new byte[size];
                if (!Native.GetFileVersionInfoW(path, 0, size, data)) return "";
                if (!Native.VerQueryValueW(data, "\\", out IntPtr info, out uint length) || length < 16) return "";
                uint ms = (uint)Marshal.ReadInt32(info, 8), ls = (uint)Marshal.ReadInt32(info, 12);
                return (ms >> 16) + "." + (ms & 0xFFFF) + "." + (ls >> 16) + "." + (ls & 0xFFFF);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { return ""; }
        }

        static readonly Regex OmegaExe = new Regex(@"^Omega[^/\\]*\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Every Omega control program installed on this PC, in folder-name order. `roots`: the folders that
        /// hold the installations (app.py --bmg-root); null or empty: BMG under Program Files, and the installations
        /// the registry knows of (after those, merged by folder). A root that does not exist is an error, as in Python.
        /// With roots given, the registry is not read.</summary>
        public static List<Install> Discover(IList<string> roots = null)
        {
            bool given = roots != null && roots.Count > 0;
            var registered = given ? new List<RegisteredInstall>() : RegisteredInstalls();
            var installs = new List<Install>();
            IEnumerable<string> bmgs = given ? (IEnumerable<string>)roots : BmgRoots();
            foreach (string bmg in bmgs)
            {
                var folders = Directory.EnumerateFileSystemEntries(bmg)
                    .OrderBy(p => Py.Casefold(Path.GetFileName(p)), Py.StrComparer.Instance).ToList();
                foreach (string folder in folders)
                {
                    string name = Path.GetFileName(folder);
                    if (!Directory.Exists(Path.Combine(folder, "Temp")) || !Directory.Exists(Path.Combine(folder, "ExeDLL"))) continue;
                    RegisteredInstall known = registered.FirstOrDefault(r => SamePath(r.MainDir, folder));
                    string exe = FindExe(folder, name, known?.Family);
                    if (exe == null) continue;
                    installs.Add(new Install(name, folder, exe, Serial(folder), FileVersion(exe), known?.UserDirs));
                }
            }
            // installed elsewhere (another folder, another drive): known from the registry only
            foreach (RegisteredInstall r in registered)
            {
                if (installs.Any(i => SamePath(i.Root, r.MainDir))) continue;
                try
                {
                    if (!Directory.Exists(Path.Combine(r.MainDir, "Temp")) || !Directory.Exists(Path.Combine(r.MainDir, "ExeDLL"))) continue;
                    string name = Path.GetFileName(r.MainDir.TrimEnd('\\', '/'));
                    string exe = FindExe(r.MainDir, name, r.Family);
                    if (exe == null) continue;
                    // the key names the run ids and records: no characters that cannot stand in a file name or an id
                    string key = new string(name.Where(c => Array.IndexOf(Runs.PathCharacters, c) < 0 && c > ' ').ToArray());
                    if (key.Length == 0) key = r.Family;
                    string unique = key;
                    for (int n = 2; installs.Any(i => string.Equals(i.Key, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = key + n;
                    installs.Add(new Install(unique, r.MainDir, exe, Serial(r.MainDir), FileVersion(exe), r.UserDirs));
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { }
            }
            return installs;
        }

        /// <summary>The control program of an installation: ExeDLL\&lt;folder&gt;.exe, else &lt;family&gt;*.exe, else
        /// Omega*.exe; null when there is none.</summary>
        static string FindExe(string folder, string name, string family)
        {
            string exe = Path.Combine(folder, "ExeDLL", name + ".exe");
            if (File.Exists(exe)) return exe;
            List<string> candidates;
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(Path.Combine(folder, "ExeDLL")).ToList();
                candidates = new List<string>();
                if (!string.IsNullOrEmpty(family))
                    candidates.AddRange(entries.Where(p =>
                    {
                        string file = Path.GetFileName(p);
                        return file.StartsWith(family, StringComparison.OrdinalIgnoreCase) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    }).OrderBy(p => Path.GetFileName(p).ToLowerInvariant(), Py.StrComparer.Instance));
                candidates.AddRange(entries.Where(p => OmegaExe.IsMatch(Path.GetFileName(p)))
                    .OrderBy(p => Path.GetFileName(p).ToLowerInvariant(), Py.StrComparer.Instance));
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { candidates = new List<string>(); }
            return candidates.Count > 0 ? candidates[0] : null;
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>An installation as the registry has it.</summary>
        public sealed class RegisteredInstall
        {
            public string Family;               // "Omega", or another BMG family
            public string MainDir;
            public List<string> UserDirs = new List<string>();
        }

        /// <summary>HKLM\SOFTWARE\BMG Labtechnologies\&lt;family&gt;\&lt;n&gt;\ControlApp of the 32-bit and the 64-bit
        /// registry: MainDir, and the users' data folders (User_NAME_Dir; LastLoggedUsers first). Only read.</summary>
        public static List<RegisteredInstall> RegisteredInstalls()
        {
            var found = new List<RegisteredInstall>();
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey bmg = machine.OpenSubKey(@"SOFTWARE\BMG Labtechnologies"))
                    {
                        if (bmg == null) continue;
                        foreach (string family in bmg.GetSubKeyNames())
                            using (RegistryKey familyKey = bmg.OpenSubKey(family))
                            {
                                if (familyKey == null) continue;
                                foreach (string number in familyKey.GetSubKeyNames())
                                    using (RegistryKey app = familyKey.OpenSubKey(number + @"\ControlApp"))
                                    {
                                        string main = app?.GetValue("MainDir") as string;
                                        if (string.IsNullOrWhiteSpace(main)) continue;
                                        main = Environment.ExpandEnvironmentVariables(main.Trim()).TrimEnd('\\', '/');
                                        if (found.Any(f => SamePath(f.MainDir, main))) continue;
                                        found.Add(new RegisteredInstall { Family = family, MainDir = main, UserDirs = UserDirsOf(app) });
                                    }
                            }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException) { }
            }
            return found;
        }

        static readonly Regex UserDirValue = new Regex("^User_(.+)_Dir$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static List<string> UserDirsOf(RegistryKey app)
        {
            var dirs = new List<(string User, string Dir)>();
            foreach (string name in app.GetValueNames())
            {
                Match found = UserDirValue.Match(name);
                if (!found.Success || !(app.GetValue(name) is string dir) || string.IsNullOrWhiteSpace(dir)) continue;
                dirs.Add((found.Groups[1].Value, Environment.ExpandEnvironmentVariables(dir.Trim()).TrimEnd('\\', '/')));
            }
            var last = ((app.GetValue("LastLoggedUsers") as string) ?? "").Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return dirs.Select((d, i) => (d.Dir, Rank: last.FindIndex(u => u.Equals(d.User, StringComparison.OrdinalIgnoreCase)), i))
                       .OrderBy(x => x.Rank < 0 ? int.MaxValue : x.Rank).ThenBy(x => x.i).Select(x => x.Dir).ToList();
        }

        /// <summary>Lower-case names of all running executables (to tell whether an Omega program is open).</summary>
        public static HashSet<string> RunningPrograms() => WinUtil.ProcessNames();

        // ---------------------------------------------------------------- reading a file Omega has open

        /// <summary>The whole file as a read-only view, while another program keeps it open for writing.
        ///
        /// Omega holds byte-range locks on parts of its files, which make an ordinary read fail (Windows error 33). A
        /// read-only memory mapping is not subject to those locks and changes nothing. Windows refuses to shrink a file
        /// while it is mapped, so the view is to be held as briefly as possible (dispose it). An empty file gives an
        /// empty content. Errors as Python raised them: PyOSError (NotFound: FileNotFoundError), PyValueError.</summary>
        public static AbsDb.Content SharedView(string path)
        {
            const uint GenericRead = 0x80000000, ShareAll = 0x1 | 0x2 | 0x4, OpenExisting = 3;
            SafeFileHandle handle = Native.CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw Py.CtypesWinError(code);
            }
            try
            {
                if (!Native.GetFileSizeEx(handle, out long size)) throw Py.WindowsErr(Marshal.GetLastWin32Error());
                if (size == 0)
                {
                    handle.Dispose();
                    return new AbsDb.ArrayContent(new byte[0]);
                }
                // mmap.mmap(descriptor, 0, access=ACCESS_READ): the size is asked again
                if (!Native.GetFileSizeEx(handle, out size)) throw Py.WindowsErr(Marshal.GetLastWin32Error());
                if (size == 0) throw new PyValueError("cannot mmap an empty file");
                const uint PageReadonly = 0x02, FileMapRead = 0x0004;
                IntPtr mapping = Native.CreateFileMappingW(handle, IntPtr.Zero, PageReadonly, (uint)(size >> 32), (uint)size, null);
                if (mapping == IntPtr.Zero) throw Py.WindowsErr(Marshal.GetLastWin32Error());
                IntPtr view = Native.MapViewOfFile(mapping, FileMapRead, 0, 0, (UIntPtr)(ulong)size);
                if (view == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    Native.CloseHandle(mapping);
                    throw Py.WindowsErr(code);
                }
                return new MappedView(handle, mapping, view, size);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>A file mapped read-only (see SharedView).</summary>
        sealed class MappedView : AbsDb.Content
        {
            SafeFileHandle file;
            IntPtr mapping, view;
            readonly long length;

            public MappedView(SafeFileHandle file, IntPtr mapping, IntPtr view, long length)
            {
                this.file = file; this.mapping = mapping; this.view = view; this.length = length;
            }

            public override long Length => length;

            public override byte this[long index]
            {
                get
                {
                    if (index < 0 || index >= length) throw new PyIndexError("mmap index out of range");
                    return Marshal.ReadByte(new IntPtr(view.ToInt64() + index));
                }
            }

            public override byte[] Slice(long start, long end)
            {
                Clamp(ref start, ref end);
                var part = new byte[end - start];
                if (part.Length > 0) Marshal.Copy(new IntPtr(view.ToInt64() + start), part, 0, part.Length);
                return part;
            }

            public override void Dispose()
            {
                if (view != IntPtr.Zero) { Native.UnmapViewOfFile(view); view = IntPtr.Zero; }
                if (mapping != IntPtr.Zero) { Native.CloseHandle(mapping); mapping = IntPtr.Zero; }
                if (file != null) { file.Dispose(); file = null; }
            }
        }

        /// <summary>The whole file as bytes (see SharedView).</summary>
        public static byte[] ReadShared(string path)
        {
            using (AbsDb.Content view = SharedView(path))
                return view.Slice(0, view.Length);
        }

        // ---------------------------------------------------------------- snapshot of a run

        /// <summary>'B02' -> 'B2'. Returns null for anything that is not a well position.</summary>
        public static string WellId(object raw)
        {
            Match found = WellIdPattern.Match(Py.OrEmptyStrip(raw));
            if (!found.Success) return null;
            return found.Groups[1].Value.ToUpperInvariant() + Py.ParseInt(found.Groups[2].Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Omega's well order: (row, column).</summary>
        public static (long Row, long Column) WellOrder(string well)
        {
            Match found = WellIdPattern.Match(well);
            if (!found.Success) throw new PyAttributeError("'NoneType' object has no attribute 'group'");
            string letters = found.Groups[1].Value;
            long row = 0, power = 1;
            for (int i = letters.Length - 1; i >= 0; i--)
            {
                row += (letters[i] - 64) * power;
                power *= 26;
            }
            return (row, Py.ParseInt(found.Groups[2].Value));
        }

        /// <summary>sorted(wells, key=_well_order) (stable).</summary>
        public static List<string> SortWells(IEnumerable<string> wells) =>
            wells.Select(w => (Well: w, Key: WellOrder(w))).OrderBy(x => x.Key.Row).ThenBy(x => x.Key.Column).Select(x => x.Well).ToList();

        /// <summary>Turn the rows of table "Daten" into a Snapshot. Raises AbsError when the layout is unknown.</summary>
        public static Snapshot SnapshotFromTable(AbsDb.Table table)
        {
            var names = new Dictionary<string, string>();
            foreach (var f in table.Fields) names[Py.Casefold(f.Name)] = f.Name;
            if (!names.ContainsKey("wellnum") || !names.ContainsKey("ch")) throw new AbsDb.AbsError("table has no well or channel column");
            var matched = new List<(string Key, string Name)>();
            foreach (var f in table.Fields)
            {
                Match m = CycleField.Match(f.Name);
                if (m.Success) matched.Add((Py.NumericKey(m.Groups[1].Value), f.Name));
            }
            // sorted((int(m.group(1)), f.name) ...): by the number, then by the name
            var cycleFields = matched.OrderBy(c => c.Key, StringComparer.Ordinal).ThenBy(c => c.Name, Py.StrComparer.Instance)
                                     .Select(c => c.Name).ToList();
            var snap = new Snapshot { PlannedCycles = cycleFields.Count };
            if (cycleFields.Count == 0) return snap;

            List<object> Series(Dictionary<string, object> row)
            {
                var values = new List<object>(cycleFields.Count);
                foreach (string name in cycleFields) values.Add(row[name]);
                return values;
            }

            var perCycle = new Dictionary<string, List<object>>();     // special rows: 't', 'C', 'Humi', ...
            string contentField = names.TryGetValue("content", out string c0) ? c0 : "";
            string orderField = names.TryGetValue("readingorder", out string o0) ? o0 : "";
            string dilutionField = names.TryGetValue("dilution", out string d0) ? d0 : "";
            foreach (var row in table.Rows)
            {
                string channel = Py.OrEmptyStrip(row[names["ch"]]);
                string well = WellId(row[names["wellnum"]]);
                if (well == null)
                {
                    if (channel.Length > 0) perCycle[channel] = Series(row);
                    continue;
                }
                string key = channel.Length > 0 ? channel : "1";
                if (!snap.Channels.TryGetValue(key, out var wells)) snap.Channels[key] = wells = new Dictionary<string, List<object>>();
                wells[well] = Series(row);
                object content = row.TryGetValue(contentField, out object v) ? v : null;
                if (Py.Truthy(content)) snap.Contents[well] = Py.StripValue(content);
                // where Omega reads the well in a cycle (1 = first), and its dilution factor
                if (orderField.Length > 0 && row.TryGetValue(orderField, out object order) && order is long n && n > 0 && !snap.ReadingOrder.ContainsKey(well))
                    snap.ReadingOrder[well] = n;
                if (dilutionField.Length > 0 && row.TryGetValue(dilutionField, out object dilution) && !snap.Dilutions.ContainsKey(well))
                {
                    if (dilution is double d && !double.IsNaN(d) && !double.IsInfinity(d)) snap.Dilutions[well] = d;
                    else if (dilution is long l) snap.Dilutions[well] = l;
                }
            }

            snap.Times = perCycle.TryGetValue("t", out var times) && times.Count > 0 ? times : new List<object>();
            snap.Temperature = perCycle.TryGetValue("C", out var temperature) && temperature.Count > 0 ? temperature : new List<object>();
            var extras = new Dictionary<string, List<object>>();
            foreach (var entry in perCycle)
                if (entry.Key != "t" && entry.Key != "C") extras[entry.Key] = entry.Value;
            snap.Extras = extras;
            return Tally(snap);
        }

        /// <summary>Count the started and complete cycles and cut every series to the started ones.</summary>
        public static Snapshot Tally(Snapshot snap)
        {
            int width = 0, total = 0;
            foreach (var wells in snap.Channels.Values)
            {
                total += wells.Count;
                foreach (var values in wells.Values) width = Math.Max(width, values.Count);
            }
            var counts = new int[width];
            foreach (var wells in snap.Channels.Values)
                foreach (var values in wells.Values)
                    for (int index = 0; index < values.Count; index++)
                        if (values[index] != null) counts[index]++;
            int started = 0;
            for (int i = 0; i < counts.Length; i++) if (counts[i] != 0) started = i + 1;
            snap.CyclesStarted = started;
            int complete = 0;
            foreach (int count in counts)
            {
                if (total == 0 || count < total) break;
                complete++;
            }
            snap.CyclesComplete = complete;

            foreach (var wells in snap.Channels.Values)
                foreach (string well in wells.Keys.ToList()) wells[well] = Head(wells[well], started);
            snap.Times = Head(snap.Times, started);
            snap.Temperature = Head(snap.Temperature, started);
            var extras = new Dictionary<string, List<object>>();
            foreach (var entry in snap.Extras) extras[entry.Key] = Head(entry.Value, started);
            snap.Extras = extras;
            return snap;
        }

        static List<object> Head(List<object> values, int count) => values.GetRange(0, Math.Min(count, values.Count));

        /// <summary>Zeros in the newest started cycle that are wells not read yet.
        ///
        /// Omega 6.40 (readers 2 and 3) writes the whole new column with 0 when a cycle begins to be read and replaces the
        /// zeros with the readings while the plate is read (a minute or two for 200 wells; seen 2026-11-18: 186 of 202
        /// wells still 0 when the column appeared, none 71 s later). Omega 6.20 writes a cycle in one go. The wells are
        /// read in Omega's reading order (the file's ReadingOrder: 1 = first): in the newest cycle a 0 is "not read yet"
        /// only where it comes after the last well that has a reading in that order. A 0 before it was read and is a
        /// real reading (luminescence, blank-reduced data), so it stays. Without a reading order every 0 of the newest
        /// cycle counts as not read, as before. Older cycles are left as they are. Changes `snap` in place and returns it.</summary>
        public static Snapshot HoldUnread(Snapshot snap)
        {
            int newest = snap.CyclesStarted;
            if (newest < 1) return snap;
            bool held = false;
            foreach (var wells in snap.Channels.Values)
            {
                // how far the reading of this channel has come in the newest cycle: the latest place in the reading order
                // that has a reading (not 0)
                long reached = 0;
                bool ordered = snap.ReadingOrder.Count > 0;
                foreach (var entry in wells)
                {
                    List<object> values = entry.Value;
                    if (values.Count < newest || values[newest - 1] == null || Py.Eq(values[newest - 1], 0L)) continue;
                    if (snap.ReadingOrder.TryGetValue(entry.Key, out long order)) reached = Math.Max(reached, order);
                    else ordered = false;                // a well read that has no place in the order: the order cannot tell
                }
                foreach (var entry in wells)
                {
                    List<object> values = entry.Value;
                    if (values.Count < newest || !Py.Eq(values[newest - 1], 0L)) continue;
                    bool read = ordered && snap.ReadingOrder.TryGetValue(entry.Key, out long order) && order < reached;
                    if (read) continue;
                    values[newest - 1] = null;
                    held = true;
                }
            }
            return held ? Tally(snap) : snap;
        }

        /// <summary>The parts of an Omega content code: "XA1" -> ("sample", "A", 1); "B" -> ("blank", "", null).</summary>
        public static (string Kind, string Group, object Number) ContentParts(string content)
        {
            string text = (content ?? "").Trim();
            if (text.Length == 0) return ("", "", null);
            string kind;
            switch (char.ToUpperInvariant(text[0]))
            {
                case 'X': kind = "sample"; break;
                case 'B': kind = "blank"; break;
                case 'S': kind = "standard"; break;
                case 'C': kind = "control"; break;
                case 'N': kind = "negative control"; break;
                case 'P': kind = "positive control"; break;
                default: kind = ""; break;
            }
            int at = 1;
            string group = "";
            while (at < text.Length && char.IsLetter(text[at])) group += text[at++];
            string digits = "";
            while (at < text.Length && char.IsDigit(text[at])) digits += text[at++];
            object number = digits.Length > 0 && digits.Length < 10 ? (object)long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) : null;
            return (kind, group, number);
        }

        /// <summary>Snapshot of the bytes of a Readings.abs file; an empty Snapshot when it has no measurement table.
        /// Raises AbsError when the bytes do not parse (Omega was in the middle of writing a page).</summary>
        public static Snapshot SnapshotFromContent(byte[] content)
        {
            if (content.Length == 0) return new Snapshot();
            AbsDb.Table table = AbsDb.ReadTable(content, TableName);
            return table != null ? SnapshotFromTable(table) : new Snapshot();
        }

        /// <summary>Snapshot of a Readings.abs file. Returns an empty Snapshot when no measurement table exists.
        ///
        /// Omega may be in the middle of writing a page; such a read fails to parse or is cut short, so it is simply
        /// repeated. Raises AbsError when the file stays unreadable, PyOSError when it cannot be opened.</summary>
        public static Snapshot ReadSnapshot(string path, int attempts = 3, double pause = 0.15)
        {
            Exception error = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(TimeSpan.FromSeconds(pause));
                try { return SnapshotFromContent(ReadShared(path)); }
                catch (AbsDb.AbsError problem) { error = problem; }
            }
            if (error == null) throw new PyTypeError("exceptions must derive from BaseException");
            throw error;
        }

        // ---------------------------------------------------------------- Omega's own database of finished runs

        /// <summary>Where this installation's default user (USER) stores finished test runs (what MARS opens).</summary>
        public static string SavedDatabase(Install install) => Path.Combine(install.Root, "User", "Data", "MeasurementData.abs");

        /// <summary>Every database of finished runs of an installation that exists: the users' data folders the registry
        /// names (the last user first), &lt;install&gt;\User\Data, then &lt;install&gt;\&lt;other user&gt;\Data.</summary>
        public static List<string> Databases(Install install)
        {
            var found = new List<string>();
            void Add(string folder)
            {
                try
                {
                    string database = Path.Combine(folder, "Data", "MeasurementData.abs");
                    if (File.Exists(database) && !found.Any(f => SamePath(f, database))) found.Add(database);
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { }
            }
            foreach (string dir in install.UserDirs) Add(dir);
            Add(Path.Combine(install.Root, "User"));
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(install.Root).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                    Add(sub);
            }
            catch (Exception e) when (Py.Is(e, PyKinds.OSError)) { }
            return found;
        }

        /// <summary>The test run that began at `started` (seconds since 1970), as Omega stored it when the test ended.
        ///
        /// The database is in the format of Readings.abs: table "Measure" has one row per run (protocol, date, time,
        /// settings, and in MessDatei the name of the table with that run's readings). Every database of the
        /// installation is looked in (Databases). Returns (Snapshot, {"targetTemperature": degrees C or null, "id1":
        /// Omega's ID1, "model": the reader's model, "serialNum": the reader's serial as the database has it}), or null
        /// when no such run is stored (yet). Raises PyOSError / AbsError when a database cannot be read and none has the
        /// run. A database can be 200 MB: it is looked at through a view, only the two tables are read.</summary>
        public static Tuple<Snapshot, Dictionary<string, object>> SavedRun(Install install, double started, object protocol, double tolerance = 5.0)
        {
            List<string> databases = Databases(install);
            if (databases.Count == 0) databases.Add(SavedDatabase(install));     // (raises: it is not there)
            Exception failure = null;
            foreach (string path in databases)
            {
                try
                {
                    var found = SavedRunIn(path, started, protocol, tolerance);
                    if (found != null) return found;
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError) || e is AbsDb.AbsError) { failure = failure ?? e; }
            }
            if (failure != null) throw failure;
            return null;
        }

        static Tuple<Snapshot, Dictionary<string, object>> SavedRunIn(string path, double started, object protocol, double tolerance)
        {
            using (AbsDb.Content view = SharedView(path))
            {
                var database = new AbsDb.Database(view);
                AbsDb.Table runs = database.Table(Saved);
                var rows = runs != null ? runs.Rows : new List<Dictionary<string, object>>();
                for (int i = rows.Count - 1; i >= 0; i--)                 // the newest run is the last row
                {
                    var row = rows[i];
                    if (!Py.Eq(Py.OrEmptyStrip(Py.Get(row, "TstName")), protocol)) continue;
                    double when;
                    try { when = PyTime.OmegaTimestamp(Py.Get(row, "Date"), Py.Get(row, "Time")); }
                    catch (Exception e) when (Py.Is(e, PyKinds.KeyError | PyKinds.TypeError | PyKinds.ValueError)) { continue; }
                    if (Math.Abs(when - started) <= tolerance)
                    {
                        AbsDb.Table table = database.Table(Py.Str(Py.Get(row, "MessDatei")));
                        if (table == null) return null;
                        Snapshot stored = SnapshotFromTable(table);
                        object planned = Py.Get(row, "Kinetics");    // a stopped run is stored with the measured cycles only
                        if (planned is long p && p > stored.PlannedCycles) stored.PlannedCycles = p;
                        return Tuple.Create(stored, new Dictionary<string, object>
                        {
                            ["targetTemperature"] = Target(row), ["id1"] = Py.Get(row, "IDNumber") as string,
                            ["model"] = Naming.ReaderModel(Py.Get(row, "Reader") as string), ["serialNum"] = Py.Get(row, "SerialNum") as string,
                        });
                    }
                }
            }
            return null;
        }

        /// <summary>The moment of a row of Omega's run table (its Date and Time), or null.</summary>
        public static double? RowTime(Dictionary<string, object> row)
        {
            try { return PyTime.OmegaTimestamp(Py.Get(row, "Date"), Py.Get(row, "Time")); }
            catch (Exception e) when (Py.Is(e, PyKinds.TypeError | PyKinds.ValueError)) { return null; }
        }

        static readonly object NewestGate = new object();
        static readonly Dictionary<string, ((long, long) Stamp, Dictionary<string, object> Row)> NewestRows =
            new Dictionary<string, ((long, long), Dictionary<string, object>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The row of the newest run in the installation's databases (rows are not kept in the order of the
        /// runs: the one with the latest date and time), or null (no run, unreadable). Kept per database until it
        /// changes.</summary>
        public static Dictionary<string, object> NewestRow(Install install)
        {
            Dictionary<string, object> newest = null;
            double newestAt = double.MinValue;
            foreach (string path in Databases(install))
            {
                Dictionary<string, object> row;
                try
                {
                    Py.StatResult stat = Py.Stat(path);
                    var stamp = (stat.MtimeNs, stat.Size);
                    bool cached;
                    lock (NewestGate) cached = NewestRows.TryGetValue(path, out var known) && known.Stamp == stamp;
                    if (cached) lock (NewestGate) row = NewestRows[path].Row;
                    else
                    {
                        row = null;
                        using (AbsDb.Content view = SharedView(path))
                        {
                            AbsDb.Table runs = new AbsDb.Database(view).Table(Saved);
                            double best = double.MinValue;
                            foreach (var candidate in runs != null ? runs.Rows : new List<Dictionary<string, object>>())
                            {
                                double at = RowTime(candidate) ?? double.MinValue;
                                // a date that cannot be read counts as the oldest; among such rows the text decides
                                bool later = row == null || at > best || (at == best && at == double.MinValue && TextLater(candidate, row));
                                if (later)
                                {
                                    row = candidate;
                                    best = at;
                                }
                            }
                        }
                        lock (NewestGate) NewestRows[path] = (stamp, row);
                    }
                }
                catch (Exception e) when (Py.Is(e, PyKinds.OSError) || e is AbsDb.AbsError) { continue; }
                if (row == null || row.Count == 0) continue;
                double rowAt = RowTime(row) ?? double.MinValue;
                if (newest == null || rowAt > newestAt) { newest = row; newestAt = rowAt; }
            }
            return newest;
        }

        static bool TextLater(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string dateA = Py.Str(Py.Or(Py.Get(a, "Date"), "")), dateB = Py.Str(Py.Or(Py.Get(b, "Date"), ""));
            if (dateA != dateB) return Py.CompareStr(dateA, dateB) > 0;
            return Py.CompareStr(Py.Str(Py.Or(Py.Get(a, "Time"), "")), Py.Str(Py.Or(Py.Get(b, "Time"), ""))) > 0;
        }

        /// <summary>The incubator target (degrees C, a double) of the newest stored run, or null (no run, incubator off,
        /// unreadable).</summary>
        public static object LastTarget(Install install)
        {
            Dictionary<string, object> newest = NewestRow(install);
            return newest != null ? Target(newest) : null;
        }

        /// <summary>The reader's model as the newest stored run names it ("FLUOstar Omega"), "" when not known.</summary>
        public static string LastModel(Install install)
        {
            Dictionary<string, object> newest = NewestRow(install);
            return newest != null ? Naming.ReaderModel(Py.Get(newest, "Reader") as string) : "";
        }

        public static object Target(Dictionary<string, object> row)
        {
            object value = Py.Get(row, "TargetTemp");
            if ((value is long || value is double || value is bool) && Py.Le(20L, value) && Py.Le(value, 70L))
                return value is double d ? d : value is bool b ? (b ? 1.0 : 0.0) : (double)(long)value;
            return null;
        }

        static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetFileSizeEx(SafeFileHandle file, out long size);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateFileMappingW(SafeFileHandle file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnmapViewOfFile(IntPtr view);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int GetFileVersionInfoSizeW(string file, IntPtr handle);

            [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetFileVersionInfoW(string file, int handle, int length, byte[] data);

            [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool VerQueryValueW(byte[] block, string subBlock, out IntPtr buffer, out uint length);
        }
    }
}
