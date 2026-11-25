// The repair at the start, before the watcher begins (after a power cut, a crash, or a disk that failed): every record
// in System\runs is checked. A record that cannot be read is put aside into System\recovery\<stamp>\ (never deleted) and
// replaced by the best there is: a save that was cut short and is complete, else its previous copy (.bak), else the
// run's experiment file (.pk), else the instrument software's own files (BMG: Omega's database). Saves cut short whose
// record is fine are put aside too. What was done is said in the log and, for the user, in the "recovered" problem of
// /api/state. (The experiment files themselves are checked in the background: Experiments.CheckFiles.)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SAAControl
{
    public static class Recovery
    {
        public sealed class Report
        {
            /// <summary>What was done, for the user (English, Chinese).</summary>
            public readonly List<(string En, string Zh)> Lines = new List<(string, string)>();
            /// <summary>Where damaged files were put aside (null: nowhere).</summary>
            public string Folder;
        }

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>Check and repair the records of layout.Runs. experiments: to find a run's .pk (null: none); rebuild: a
        /// record made from the instrument software's own files (null: none, or it returns null).</summary>
        public static Report Records(DataLayout layout, Experiments experiments, Func<string, Dictionary<string, object>> rebuild)
        {
            var report = new Report();
            string folder = layout.Runs;
            if (!Directory.Exists(folder)) return report;
            string AsideFolder()
            {
                if (report.Folder == null)
                {
                    report.Folder = layout.NewRecoveryFolder();
                    Directory.CreateDirectory(report.Folder);
                }
                return report.Folder;
            }

            // saves that were cut short: the run they were for, when they hold a whole record
            var cut = new Dictionary<string, (string Path, byte[] Bytes, long Cycles)>(StringComparer.Ordinal);
            var leftovers = new List<string>();
            foreach (string temp in List(folder, ".*" + SafeFile.TempSuffix))
            {
                leftovers.Add(temp);
                try
                {
                    byte[] bytes = SafeFile.ReadAll(temp);
                    var record = Parse(bytes);
                    string id = RunIdOf(record);
                    if (id != null && (!cut.ContainsKey(id) || Cycles(record) >= cut[id].Cycles)) cut[id] = (temp, bytes, Cycles(record));
                }
                catch (Exception) { }
            }

            // a record that is not there beside its previous copy (an earlier version put a damaged record aside before
            // it had the replacement, and the start was cut short then): the previous copy comes back
            foreach (string backup in List(folder, "*.json" + SafeFile.BackupSuffix))
            {
                string path = backup.Substring(0, backup.Length - SafeFile.BackupSuffix.Length);
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(path)) continue;
                try
                {
                    byte[] bytes = SafeFile.ReadAll(backup);
                    var record = Parse(bytes);
                    if (RunIdOf(record) == null || !TryPlace(path, bytes)) continue;
                    long cycles = Cycles(record);
                    string name = NameOf(record) ?? Path.GetFileNameWithoutExtension(path);
                    Log.Warning("the record " + Path.GetFileName(path) + " was missing: restored from its previous copy (" + cycles + " cycles)");
                    report.Lines.Add(("The record of \"" + name + "\" was missing and was restored from its previous copy (cycles 1-" + cycles + ").",
                                      // "“...”的记录不见了，已从上一份副本恢复（第 1–N 轮）。"
                                      "“" + name + "”的记录不见了，已从上一份副本恢复（第 1–" + cycles + " 轮）。"));
                }
                catch (Exception) { }
            }

            foreach (string path in List(folder, "*.json"))
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string runId = Path.GetFileNameWithoutExtension(path);
                // a record that has not changed for two weeks was whole when it was written: only looked at for what a
                // failing disk leaves (nothing, zeros, a cut), not read through (a PC may hold thousands)
                if (!cut.ContainsKey(runId) && Old(path) && LooksWhole(path)) continue;
                Dictionary<string, object> current = null;
                try { current = Parse(SafeFile.ReadAll(path)); }
                catch (Exception) { }
                if (current != null && RunIdOf(current) != null)
                {
                    // a whole record; a save of it cut short that is whole and further on is completed
                    if (cut.TryGetValue(runId, out var newer) && newer.Cycles > Cycles(current) && TryPlace(path, newer.Bytes))
                    {
                        leftovers.Remove(newer.Path);
                        Remove(newer.Path);
                        Log.Info("the record " + runId + " was saved further when the program stopped: that save was completed (" + newer.Cycles + " cycles)");
                    }
                    continue;
                }

                // damaged: a copy put aside, then the best there is in its place (the damaged file stays until then: a
                // start cut short here finds it again, and never a run without its record)
                string aside;
                try
                {
                    aside = Path.Combine(AsideFolder(), Path.GetFileName(path));
                    File.Copy(path, aside, true);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Log.Error("the damaged record " + path + " could not be put aside", error);
                    continue;
                }
                (string En, string Zh)? how = null;
                Dictionary<string, object> found = null;
                if (cut.TryGetValue(runId, out var save) && TryPlace(path, save.Bytes))
                {
                    found = Parse(save.Bytes);
                    leftovers.Remove(save.Path);
                    Remove(save.Path);
                    // "\u5b83\u88ab\u4e2d\u65ad\u7684\u4fdd\u5b58"
                    how = ("the save that was cut short", "\u88ab\u4e2d\u65ad\u7684\u90a3\u6b21\u4fdd\u5b58");
                }
                if (how == null)
                {
                    try
                    {
                        string backup = path + SafeFile.BackupSuffix;
                        if (File.Exists(backup))
                        {
                            byte[] bytes = SafeFile.ReadAll(backup);
                            var record = Parse(bytes);
                            if (RunIdOf(record) != null && TryPlace(path, bytes))
                            {
                                found = record;
                                how = ("its previous copy", "\u4e0a\u4e00\u4efd\u526f\u672c");           // "\u4e0a\u4e00\u4efd\u526f\u672c"
                            }
                        }
                    }
                    catch (Exception) { }
                }
                Dictionary<string, object> fromPk = null, fromInstrument = null;
                if (how == null)
                {
                    try { fromPk = experiments?.RecordFromPk(runId); } catch (Exception) { }
                    try { fromInstrument = rebuild?.Invoke(runId); } catch (Exception error) { Log.Warning("the instrument's files could not give the run " + runId + " (" + error.Message + ")"); }
                    var best = Cycles(fromInstrument) > Cycles(fromPk) ? fromInstrument : fromPk ?? fromInstrument;
                    if (best != null && TryPlace(path, Utf8.GetBytes(Json.Serialize(best, ensureAscii: false))))
                    {
                        found = best;
                        how = ReferenceEquals(best, fromPk)
                            ? ("its experiment file (.pk)", "\u5b9e\u9a8c\u6587\u4ef6\uff08.pk\uff09")                        // "\u5b9e\u9a8c\u6587\u4ef6\uff08.pk\uff09"
                            : ("the instrument software's own files", "\u4eea\u5668\u8f6f\u4ef6\u81ea\u5df1\u7684\u6587\u4ef6");    // "\u4eea\u5668\u8f6f\u4ef6\u81ea\u5df1\u7684\u6587\u4ef6"
                    }
                }
                string stamp = Path.GetFileName(report.Folder);
                if (how != null)
                {
                    long cycles = Cycles(found);
                    string name = NameOf(found) ?? runId;
                    Log.Warning("the record " + runId + " could not be read (put aside in " + report.Folder + "): restored from " + how.Value.En + " (" + cycles + " cycles)");
                    report.Lines.Add(("The record of \"" + name + "\" was damaged and was restored from " + how.Value.En + " (cycles 1-" + cycles + ").",
                                      // "\u201c...\u201d\u7684\u8bb0\u5f55\u635f\u574f\uff0c\u5df2\u4ece...\u6062\u590d\uff08\u7b2c 1\u2013N \u8f6e\uff09\u3002"
                                      "\u201c" + name + "\u201d\u7684\u8bb0\u5f55\u635f\u574f\uff0c\u5df2\u4ece" + how.Value.Zh +
                                      "\u6062\u590d\uff08\u7b2c 1\u2013" + cycles + " \u8f6e\uff09\u3002"));
                }
                else
                {
                    // (the copy is in recovery: the damaged file leaves the records)
                    try
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        Log.Warning("the damaged record " + path + " stays where it is (" + error.Message + ")");
                    }
                    Log.Error("the record " + runId + " could not be read and there is nothing to restore it from; it was put aside in " + report.Folder);
                    report.Lines.Add(("The record " + runId + " is damaged and could not be restored; it was put aside in System\\recovery\\" + stamp + ".",
                                      // "\u8bb0\u5f55 ... \u5df2\u635f\u574f\uff0c\u65e0\u6cd5\u6062\u590d\uff1b\u539f\u6587\u4ef6\u5df2\u632a\u5230 System\recovery\...\u3002"
                                      "\u8bb0\u5f55 " + runId + " \u5df2\u635f\u574f\uff0c\u65e0\u6cd5\u6062\u590d\uff1b\u539f\u6587\u4ef6\u5df2\u632a\u5230 System\\recovery\\" + stamp + "\u3002"));
                }
            }

            // what is left of saves cut short (their record is whole): put aside, never deleted
            foreach (string temp in leftovers)
            {
                try
                {
                    if (!File.Exists(temp)) continue;
                    string aside = Path.Combine(AsideFolder(), Path.GetFileName(temp).TrimStart('.'));
                    File.SetAttributes(temp, FileAttributes.Normal);
                    File.Move(temp, aside);
                    Log.Info("a save that was cut short (" + Path.GetFileName(temp) + ") was put aside in " + report.Folder);
                }
                catch (Exception) { }
            }
            return report;
        }

        static bool Old(string path)
        {
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(14); }
            catch (Exception) { return false; }
        }

        /// <summary>A JSON object from its first byte to its last: "{" ... "}".</summary>
        static bool LooksWhole(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < 2) return false;
                    if (stream.ReadByte() != '{') return false;
                    long at = stream.Length - 1;
                    while (at > 0)
                    {
                        stream.Position = at;
                        int b = stream.ReadByte();
                        if (b == ' ' || b == '\r' || b == '\n' || b == '\t') { at--; continue; }
                        return b == '}';
                    }
                    return false;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>The files of the folder that match (not in its subfolders).</summary>
        static List<string> List(string folder, string pattern)
        {
            try { return Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        static Dictionary<string, object> Parse(byte[] bytes) => Json.Parse(Utf8.GetString(bytes)) as Dictionary<string, object>;

        static string RunIdOf(Dictionary<string, object> record)
        {
            if (record == null || !(record.TryGetValue("summary", out object s) && s is Dictionary<string, object> summary)
                || !(record.TryGetValue("data", out object d) && d is Dictionary<string, object>)) return null;
            return summary.TryGetValue("id", out object id) ? id as string : null;
        }

        static long Cycles(Dictionary<string, object> record)
        {
            if (record == null) return -1;
            var summary = record.TryGetValue("summary", out object s) ? s as Dictionary<string, object> : null;
            return summary != null && summary.TryGetValue("cyclesComplete", out object c) ? ExperimentInfo.Number(c) : 0;
        }

        /// <summary>The run's name for the user, with its start ("alice, 01-16 09:00": runs of one protocol share a name).</summary>
        static string NameOf(Dictionary<string, object> record)
        {
            if (record == null) return null;
            var summary = record.TryGetValue("summary", out object s) ? s as Dictionary<string, object> : null;
            string name = record.TryGetValue("experiment", out object e) && e is string typed && typed.Trim().Length > 0 ? typed.Trim()
                : summary != null && summary.TryGetValue("protocol", out object p) && p is string protocol && protocol.Length > 0 ? protocol : null;
            if (name == null) return null;
            DateTime? start = Naming.Start(summary != null && summary.TryGetValue("startedAt", out object at) ? at as string : null);
            return start.HasValue ? name + ", " + start.Value.ToString("MM'-'dd' 'HH':'mm", CultureInfo.InvariantCulture) : name;
        }

        static bool TryPlace(string path, byte[] bytes)
        {
            try
            {
                SafeFile.Write(path, bytes, back => { if (RunIdOf(Parse(back)) == null) throw new InvalidDataException("not a record"); }, keepBackup: false);
                return true;
            }
            catch (Exception error)
            {
                Log.Error("the record " + path + " could not be written", error);
                return false;
            }
        }

        static void Remove(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        /// <summary>The problem "recovered" (shown until dismissed): what the start-up repair and the check of the experiment
        /// files did. Nothing when they did nothing.</summary>
        public static void Show(IEnumerable<(string En, string Zh)> lines)
        {
            var all = lines.ToList();
            if (all.Count == 0) return;
            Problems.Set("recovered", Problems.Info,
                         "Repaired after an unexpected stop: " + string.Join(" ", all.Select(l => l.En)),
                         // "\u610f\u5916\u505c\u6b62\u540e\u5df2\u4fee\u590d\uff1a"
                         "\u610f\u5916\u505c\u6b62\u540e\u5df2\u4fee\u590d\uff1a" + string.Join("", all.Select(l => l.Zh)),
                         dismissible: true);
        }
    }
}
