// Power-safe saving of the program's own files (run records, .pk, .xlsx), and what the page is told about saving
// (/api/state "storage").
//
// A file is written into a temporary file beside it (write-through, flushed to the disk), read back and checked, and
// only then put in the place of the old one, which is kept as "<file>.bak" (a second name of the old file - a hard
// link, a copy on a disk without them - then one MoveFileEx with REPLACE_EXISTING and WRITE_THROUGH). After a power cut
// the file holds the old content or the new one, never neither. (Not File.Replace: with a backup it renames twice,
// and a cut between the two leaves no file at all.) At the start a record that does not parse is copied aside into the
// recovery folder and restored from its .bak (Recovery.cs does the whole repair before the watcher starts).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SAAControl
{
    public static class SafeFile
    {
        public const string BackupSuffix = ".bak";
        public const string TempSuffix = ".pktmp";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>Write bytes to path power-safely. validate gets the bytes as read back from the disk and throws when
        /// they are not right; then nothing is replaced. keepBackup: the old file stays as path + ".bak" (hidden when
        /// hideBackup). Throws IOException (or UnauthorizedAccessException) when the file cannot be written.</summary>
        public static void Write(string path, byte[] bytes, Action<byte[]> validate, bool keepBackup = true, bool hideBackup = false)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            Directory.CreateDirectory(dir);                    // (re-created when someone removed it)
            string temp = TempName(dir);
            string backup = keepBackup ? full + BackupSuffix : null;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);                        // FlushFileBuffers: on the disk, not in a cache
                }
                byte[] back = ReadAll(temp);
                if (back.Length != bytes.Length) throw new IOException("the file read back has another length than was written: " + full);
                try { validate?.Invoke(back); }
                catch (Exception error) when (!(error is IOException))
                {
                    throw new IOException("the file read back is not right (" + error.Message + "): " + full, error);
                }
                Retried(() => Place(temp, full, backup));
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
            if (backup != null && hideBackup)
            {
                try { if (File.Exists(backup)) File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.Hidden); }
                catch (Exception) { }
            }
        }

        /// <summary>A JSON value as UTF-8 (Python's json.dumps(value, ensure_ascii=False)), checked by parsing it back.</summary>
        public static void WriteJson(string path, object value, bool keepBackup = true)
        {
            byte[] bytes = Utf8.GetBytes(Json.Serialize(value, ensureAscii: false));
            Write(path, bytes, back => Json.Parse(Utf8.GetString(back)), keepBackup);
        }

        /// <summary>The whole file, opened so that nobody is kept from replacing or deleting it meanwhile.</summary>
        public static byte[] ReadAll(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        static void Place(string temp, string path, string backup)
        {
            if (!File.Exists(path))
            {
                Move(temp, path, false);
                return;
            }
            if (backup != null)
            {
                // the old content stays as the backup: a second name of the old file (a copy where the disk has no hard
                // links), made under a temporary name and put in the place of the previous backup in one step
                string link = TempName(Path.GetDirectoryName(path));
                try
                {
                    if (!CreateHardLinkW(link, path, IntPtr.Zero)) File.Copy(path, link, false);
                    if (File.Exists(backup)) File.SetAttributes(backup, FileAttributes.Normal);   // (a read-only one cannot be replaced)
                    Move(link, backup, true);
                }
                finally
                {
                    try { if (File.Exists(link)) File.Delete(link); } catch (Exception) { }
                }
            }
            Move(temp, path, true);           // one step: the file holds the old content or the new one
        }

        // (a short name: the path of the file itself may be near the length Windows allows)
        static string TempName(string dir) => Path.Combine(dir, "." + Guid.NewGuid().ToString("N").Substring(0, 10) + TempSuffix);

        static void Move(string from, string to, bool replace)
        {
            const int ReplaceExisting = 0x1, WriteThrough = 0x8;
            if (!MoveFileExW(from, to, WriteThrough | (replace ? ReplaceExisting : 0)))
            {
                int code = Marshal.GetLastWin32Error();
                throw new IOException(new Win32Exception(code).Message + ": '" + to + "'", unchecked((int)0x80070000) | code);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

        /// <summary>A sharing violation (a reader, a virus scanner, the search indexer has the file open for a moment):
        /// tried again after 0.1, 0.5 and 2 seconds.</summary>
        static void Retried(Action action)
        {
            int[] pauses = { 100, 500, 2000 };
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (Exception error) when (attempt < pauses.Length && (error is IOException || error is UnauthorizedAccessException))
                {
                    Thread.Sleep(pauses[attempt]);
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool MoveFileExW(string existing, string target, int flags);

        /// <summary>At the start: every record (*.json) in the folder that does not parse is copied aside into the recovery
        /// folder beside it (..\recovery\&lt;stamp&gt;) and restored from its .bak when that parses. Returns the names of the
        /// restored records. (Recovery.Records does this and more before the watcher starts; this is what the watchers
        /// do themselves.)</summary>
        public static List<string> RestoreBroken(string folder)
        {
            var restored = new List<string>();
            if (!Directory.Exists(folder)) return restored;
            List<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.json").ToList(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return restored; }
            string aside = null;
            foreach (string file in files)
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;   // ("*.json" also finds *.jsonx)
                if (ParsesAsJson(file)) continue;
                string backup = file + BackupSuffix;
                try
                {
                    if (aside == null)
                    {
                        string recovery = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\')), "recovery");
                        string stamp = DateTime.Now.ToString("yyyyMMdd'-'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                        aside = Path.Combine(recovery, stamp);
                        for (int n = 2; Directory.Exists(aside) && n < 1000; n++) aside = Path.Combine(recovery, stamp + "-" + n);
                        Directory.CreateDirectory(aside);
                    }
                    File.Copy(file, Path.Combine(aside, Path.GetFileName(file)), true);
                }
                catch (Exception error) { Log.Warning("the damaged record " + file + " could not be copied aside (" + error.Message + ")"); }
                if (!File.Exists(backup) || !ParsesAsJson(backup))
                {
                    Log.Warning("the record " + Path.GetFileName(file) + " cannot be read, and there is no readable " + Path.GetFileName(backup));
                    continue;
                }
                try
                {
                    Write(file, ReadAll(backup), back => Json.Parse(Utf8.GetString(back)), keepBackup: false);
                    restored.Add(Path.GetFileName(file));
                    Log.Warning("the record " + Path.GetFileName(file) + " could not be read (a write cut short?): restored from " + Path.GetFileName(backup)
                                + " (the damaged file is kept in " + aside + ")");
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Log.Error("restoring the record " + Path.GetFileName(file) + " from its .bak failed", error);
                }
            }
            return restored;
        }

        /// <summary>The files named like `pattern` in a folder and in the folders in it, however deep - gone through folder
        /// by folder, not by one listing of the whole tree. A junction or symbolic link in it gives the files that are in
        /// it itself (a month folder that is a link to another disk is a month folder like any other: the program writes
        /// into it, so it must find there what it wrote), but the folders in it are not gone into (one to C:\ would have
        /// the whole disk looked through). A folder that cannot be listed (its target is gone, no rights, deleted
        /// meanwhile) is left out without the others being lost. skipped is told each folder left out, with the error, and
        /// each link (error null: the folders in it were not gone into). Returns false - and no file - when the folder
        /// itself cannot be listed.</summary>
        public static bool ListFiles(string folder, string pattern, List<string> files, Action<string, Exception> skipped = null)
        {
            var waiting = new Stack<string>();
            var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            waiting.Push(folder);
            bool top = true;
            while (waiting.Count > 0)
            {
                string dir = waiting.Pop();
                bool link = links.Contains(dir);
                try
                {
                    string[] here = Directory.GetFiles(dir, pattern);
                    var inside = new List<string>();
                    if (!link)
                        foreach (DirectoryInfo sub in new DirectoryInfo(dir).GetDirectories())
                        {
                            string path = Path.Combine(dir, sub.Name);
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(path)) links.Add(path);
                            inside.Add(path);
                        }
                    files.AddRange(here);                                  // (all of a folder, or nothing of it)
                    if (link) skipped?.Invoke(dir, null);
                    for (int i = inside.Count - 1; i >= 0; i--) waiting.Push(inside[i]);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
                {
                    skipped?.Invoke(dir, error);
                    if (top) return false;
                }
                top = false;
            }
            return true;
        }

        /// <summary>Whether a folder is a junction or a symbolic link: a name for another place. Told by its reparse tag, not
        /// by the attribute "reparse point" alone - the folders that OneDrive and other cloud storage keep on the disk have
        /// that attribute too, and are folders like any other.</summary>
        public static bool IsLink(string path)
        {
            IntPtr handle = FindFirstFileW(path, out FindData data);
            if (handle == new IntPtr(-1)) return false;        // (not to be told: listing it says what is wrong with it)
            FindClose(handle);
            return (data.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && IsLinkTag(data.Reserved0);
        }

        /// <summary>Windows' IsReparseTagNameSurrogate: true for the tags of junctions (0xA0000003) and symbolic links
        /// (0xA000000C), false for those of cloud files (0x9000?01A) and the rest.</summary>
        public static bool IsLinkTag(uint tag) => (tag & 0x20000000) != 0;

        // WIN32_FIND_DATAW
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            public uint Attributes;
            public uint Created1, Created2, Accessed1, Accessed2, Written1, Written2;
            public uint SizeHigh, SizeLow;
            public uint Reserved0, Reserved1;                  // Reserved0: the reparse tag of a reparse point
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string name, out FindData data);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr handle);

        /// <summary>Temporary files of writes that were cut short (a power cut, the program killed): the file they were
        /// for still holds its old content, and what they held is made again. Removed when written before
        /// writtenBeforeUtc (the start of this run of the program: none of its own), else when older than ten minutes.
        /// Returns how many were removed.</summary>
        public static int RemoveTemporary(string folder, bool subfolders, DateTime? writtenBeforeUtc = null)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                DateTime before = writtenBeforeUtc ?? DateTime.UtcNow - TimeSpan.FromMinutes(10);
                var files = new List<string>();
                // (with the folders in it - not those of a junction, a name for another place - and a folder that cannot be
                // listed costs only its own files)
                if (subfolders) ListFiles(folder, "*" + TempSuffix, files);
                else files.AddRange(Directory.GetFiles(folder, "*" + TempSuffix));
                foreach (string file in files)
                {
                    try
                    {
                        string name = Path.GetFileName(file);
                        if (!name.StartsWith(".") || !name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) || File.GetLastWriteTimeUtc(file) >= before) continue;
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return removed;
        }

        static bool ParsesAsJson(string file)
        {
            try
            {
                Json.Parse(Utf8.GetString(ReadAll(file)));
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Whether saving works: a failed save (per file, until that file is saved again) and the free space of
    /// the disk of the Experiments folder, looked at every 5 minutes and after a failure. The page shows it as a red bar
    /// (/api/state "storage") and as problems: "storage-failed" (error), "disk-low" (less than 5 GB, warning),
    /// "disk-critical" (less than 1 GB, error: the program then saves only the records and the .pk files). The first
    /// failure of a file is also an event ("save-failed").</summary>
    public sealed class StorageStatus
    {
        public static long LowSpace = 1L << 30;      // 1 GB: "disk-critical"
        public static long WarnSpace = 5L << 30;     // 5 GB: "disk-low"
        public static TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
        readonly string folder;
        readonly object gate = new object();
        readonly Dictionary<string, (string En, string Zh, long At)> failures = new Dictionary<string, (string, string, long)>(StringComparer.OrdinalIgnoreCase);
        long free = -1;
        long freeAt;                                 // Environment.TickCount of the last look at the free space
        bool lowLogged;
        Timer timer;

        public StorageStatus(string folder) { this.folder = folder; }

        public string Folder => folder;

        /// <summary>The free space is looked at every 5 minutes from now on (the problems follow it).</summary>
        public void StartChecking()
        {
            lock (gate)
            {
                if (timer != null) return;
                timer = new Timer(_ => { try { Check(); } catch (Exception) { } }, null, TimeSpan.Zero, CheckEvery);
            }
        }

        public void StopChecking()
        {
            lock (gate)
            {
                timer?.Dispose();
                timer = null;
            }
        }

        /// <summary>Less than 1 GB is free: only the records and the .pk files are saved.</summary>
        public bool Critical
        {
            get
            {
                long bytes = FreeBytes();
                return bytes >= 0 && bytes < LowSpace;
            }
        }

        /// <summary>A file could not be saved: said in the log once (per file and reason), shown until it is saved.</summary>
        public void Failed(string path, Exception error)
        {
            string name = Path.GetFileName(path);
            string reason = error is IOException || error is UnauthorizedAccessException ? error.Message : error.GetType().Name + ": " + error.Message;
            string en = "Saving failed: " + name + " (" + reason + ")";
            // "\u4fdd\u5b58\u5931\u8d25\uff1a<file>\uff08<reason>\uff09"
            string zh = "\u4fdd\u5b58\u5931\u8d25\uff1a" + name + "\uff08" + reason + "\uff09";
            bool fresh, first;
            lock (gate)
            {
                first = !failures.ContainsKey(path);
                fresh = first || failures[path].En != en;
                failures[path] = (en, zh, DateTime.UtcNow.Ticks);
            }
            if (fresh) Log.Error("saving " + path + " failed", error);
            if (first) Problems.Event("save-failed", null, null, name, en, zh);
            Show();
            lock (gate) free = -1;                   // (the disk may be full: looked at again now)
            Check();
        }

        public void Succeeded(string path)
        {
            bool was;
            lock (gate) was = failures.Remove(path);
            if (!was) return;
            Log.Info(path + " is saved again");
            Show();
        }

        /// <summary>The problem "storage-failed" as the failures are now: since the first of them, with the newest's words.</summary>
        void Show()
        {
            (string En, string Zh, long At)? newest = null;
            int count;
            lock (gate)
            {
                count = failures.Count;
                if (count > 0) newest = failures.Values.OrderByDescending(f => f.At).First();
            }
            if (newest == null)
            {
                Problems.Clear("storage-failed");
                return;
            }
            string more = count > 1 ? " (+" + (count - 1) + ")" : "";
            Problems.Set("storage-failed", Problems.Error,
                         newest.Value.En + more + ". The run goes on in the instrument software; what could not be saved is saved as soon as it can be.",
                         // "...\u3002\u5b9e\u9a8c\u5728\u4eea\u5668\u8f6f\u4ef6\u91cc\u7167\u5e38\u8fdb\u884c\uff0c\u80fd\u4fdd\u5b58\u65f6\u4f1a\u81ea\u52a8\u8865\u5b58\u3002"
                         newest.Value.Zh + more + "\u3002\u5b9e\u9a8c\u5728\u4eea\u5668\u8f6f\u4ef6\u91cc\u7167\u5e38\u8fdb\u884c\uff0c\u80fd\u4fdd\u5b58\u65f6\u4f1a\u81ea\u52a8\u8865\u5b58\u3002");
        }

        /// <summary>The free space now, and the problems "disk-low" / "disk-critical" set or cleared.</summary>
        public long Check()
        {
            long bytes;
            lock (gate) free = -1;
            bytes = FreeBytes();
            string drive = Root();
            string gb = bytes >= 0 ? (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "?";
            if (bytes >= 0 && bytes < LowSpace)
            {
                Problems.Clear("disk-low");
                Problems.Set("disk-critical", Problems.Error,
                             "Only " + gb + " GB free on " + drive + ". When the disk is full, neither the instrument software nor this program can save data; workbooks (.xlsx) wait until there is space again.",
                             // "<drive> \u53ea\u5269 <n> GB\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u4eea\u5668\u8f6f\u4ef6\u548c\u672c\u7a0b\u5e8f\u90fd\u65e0\u6cd5\u4fdd\u5b58\u6570\u636e\uff1bExcel \u6587\u4ef6\u6682\u505c\u751f\u6210\uff0c\u7b49\u6709\u7a7a\u95f4\u65f6\u518d\u8865\u3002"
                             drive + " \u53ea\u5269 " + gb + " GB\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u4eea\u5668\u8f6f\u4ef6\u548c\u672c\u7a0b\u5e8f\u90fd\u65e0\u6cd5\u4fdd\u5b58\u6570\u636e\uff1bExcel \u6587\u4ef6\u6682\u505c\u751f\u6210\uff0c\u7b49\u6709\u7a7a\u95f4\u65f6\u518d\u8865\u3002");
            }
            else if (bytes >= 0 && bytes < WarnSpace)
            {
                Problems.Clear("disk-critical");
                Problems.Set("disk-low", Problems.Warning,
                             "Only " + gb + " GB free on " + drive + ". When the disk is full, neither the instrument software nor this program can save data.",
                             // "<drive> \u53ea\u5269 <n> GB\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u4eea\u5668\u8f6f\u4ef6\u548c\u672c\u7a0b\u5e8f\u90fd\u65e0\u6cd5\u4fdd\u5b58\u6570\u636e\u3002"
                             drive + " \u53ea\u5269 " + gb + " GB\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u4eea\u5668\u8f6f\u4ef6\u548c\u672c\u7a0b\u5e8f\u90fd\u65e0\u6cd5\u4fdd\u5b58\u6570\u636e\u3002");
            }
            else
            {
                Problems.Clear("disk-low");
                Problems.Clear("disk-critical");
            }
            return bytes;
        }

        /// <summary>/api/state "storage": {"ok", "message", "messageZh", "freeBytes", "experimentsFolder"}; not ok while a
        /// save fails or less than 1 GB is free.</summary>
        public Dictionary<string, object> State()
        {
            long bytes = FreeBytes();
            string en = "", zh = "";
            bool ok;
            lock (gate)
            {
                ok = failures.Count == 0;
                if (!ok)
                {
                    var newest = failures.Values.OrderByDescending(f => f.At).First();
                    en = newest.En;
                    zh = newest.Zh;
                }
            }
            if (bytes >= 0 && bytes < LowSpace)
            {
                string drive = Root();
                string gb = (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                string lowEn = "Little disk space left: " + gb + " GB free on " + drive + ". Free some space so that the records can be saved.";
                // "\u78c1\u76d8\u7a7a\u95f4\u4e0d\u8db3\uff1a<drive> \u53ea\u5269 <n> GB\uff0c\u8bf7\u6e05\u7406\u78c1\u76d8\uff0c\u4ee5\u514d\u8bb0\u5f55\u65e0\u6cd5\u4fdd\u5b58\u3002"
                string lowZh = "\u78c1\u76d8\u7a7a\u95f4\u4e0d\u8db3\uff1a" + drive + " \u53ea\u5269 " + gb + " GB\uff0c\u8bf7\u6e05\u7406\u78c1\u76d8\uff0c\u4ee5\u514d\u8bb0\u5f55\u65e0\u6cd5\u4fdd\u5b58\u3002";
                if (ok) { en = lowEn; zh = lowZh; }
                else { en += " " + lowEn; zh += lowZh; }
                ok = false;
                if (!lowLogged) Log.Warning("little disk space left: " + bytes + " bytes free on " + drive);
                lowLogged = true;
            }
            else lowLogged = false;
            return new Dictionary<string, object>
            {
                ["ok"] = ok, ["message"] = en, ["messageZh"] = zh, ["freeBytes"] = bytes >= 0 ? (object)bytes : null,
                ["experimentsFolder"] = folder,
            };
        }

        string Root()
        {
            try { return Path.GetPathRoot(Path.GetFullPath(folder)).TrimEnd('\\'); }
            catch (Exception) { return folder; }
        }

        /// <summary>The free space for this user on the disk of the folder (looked at every 15 seconds at most); -1 when
        /// it cannot be found out.</summary>
        public long FreeBytes()
        {
            lock (gate)
            {
                if (free >= 0 && unchecked((uint)(Environment.TickCount - (int)freeAt)) < 15000) return free;
            }
            long bytes = -1;
            try
            {
                string dir = Path.GetFullPath(folder);
                while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (dir != null && GetDiskFreeSpaceExW(dir, out ulong available, out _, out _)) bytes = (long)Math.Min(available, (ulong)long.MaxValue);
            }
            catch (Exception) { }
            lock (gate)
            {
                free = bytes;
                freeAt = Environment.TickCount;
            }
            return bytes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);
    }
}
