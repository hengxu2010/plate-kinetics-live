// The data folder beside the program (PlateKineticsLive-data) and what is where in it:
//
//   README.txt            what is where, what to back up (Chinese, then English)
//   Experiments\          the experiment files, <yyyy-MM>\<name>.pk and .xlsx: the users' own (rename, move, delete)
//   System\               the program's own files (hidden; in the installed program only the service writes there):
//     runs\               the records of the runs (<run id>.json, .bak) and pk-made.txt
//     log\                the program's log (size-limited, older parts kept as .1 ... .4)
//     recovery\<stamp>\   files found damaged, put aside (never deleted by the program)
//     api-token, settings.json, experiments.json (the program's list of the experiment files), layouts\<id>.json,
//     analysis\<run id>.json (a copy of each saved analysis; <run id>.prev.json the version its last save replaced),
//     omega-*.json,
//     users\<windows user>\   that user's window: its web profile, window size, window.log (each user makes his own)
//     old-cache\          the web profiles of earlier versions (browser\, webview\), never deleted by the program
//
// Earlier versions kept everything in the data folder itself. At the first start of this version those items move into
// System (a file in use stays where it is and moves at a later start); the earlier web profiles go to System\old-cache.
// While the program under its former name is still registered as a service of this same folder (an installation is
// replacing it), the folder is used as it is ("legacy": System is the data folder itself), so that the former program
// finds everything should the installation go back to it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SAAControl
{
    public sealed class DataLayout
    {
        public const string SystemName = "System", ExperimentsName = "Experiments", ReadmeName = "README.txt";
        /// <summary>The data folder (PlateKineticsLive-data).</summary>
        public readonly string Root;
        /// <summary>The program's own folder: Root\System (Root itself in a legacy layout).</summary>
        public readonly string SystemDir;
        public readonly bool Legacy;

        public DataLayout(string root, bool legacy = false)
        {
            Root = Path.GetFullPath(root);
            Legacy = legacy;
            SystemDir = legacy ? Root : Path.Combine(Root, SystemName);
        }

        public string Experiments => Path.Combine(Root, ExperimentsName);
        public string Runs => Path.Combine(SystemDir, "runs");
        public string Logs => Path.Combine(SystemDir, "log");
        public string Recovery => Path.Combine(SystemDir, "recovery");
        public string Analysis => Path.Combine(SystemDir, "analysis");
        public string Layouts => Path.Combine(SystemDir, "layouts");
        public string Users => Path.Combine(SystemDir, "users");
        public string OldCache => Path.Combine(SystemDir, "old-cache");
        public string Settings => Path.Combine(SystemDir, "settings.json");
        public string Index => Path.Combine(SystemDir, "experiments.json");
        public string Token => Path.Combine(SystemDir, "api-token");
        public string Readme => Path.Combine(Root, ReadmeName);

        /// <summary>The folder of one Windows user's window data: System\users\&lt;user&gt;.</summary>
        public string UserFolder(string user = null) => Path.Combine(Users, UserFolderName(user ?? Session.ThisUser()));

        /// <summary>A user's name as a folder name.</summary>
        public static string UserFolderName(string user)
        {
            string name = Naming.Clean(user ?? "");
            return name.Length > 0 ? name : "user";
        }

        /// <summary>A new folder for files put aside: System\recovery\yyyyMMdd-HHmmss (made when used).</summary>
        public string NewRecoveryFolder()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd'-'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string folder = Path.Combine(Recovery, stamp);
            for (int n = 2; Directory.Exists(folder) && n < 1000; n++) folder = Path.Combine(Recovery, stamp + "-" + n);
            return folder;
        }

        // ---- the first start of this version

        /// <summary>Make the folders, move what earlier versions kept in the data folder itself into System, and write
        /// README.txt. Returns what was done, for the log. Never throws: what fails is tried again at the next start.</summary>
        public List<string> Prepare()
        {
            var notes = new List<string>();
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(SystemDir);
                Directory.CreateDirectory(Experiments);
                if (!Legacy) Directory.CreateDirectory(Users);     // (where each user's window makes its own folder)
            }
            catch (Exception error)
            {
                notes.Add("the data folder " + Root + " cannot be made (" + error.Message + ")");
                return notes;
            }
            if (Legacy) return notes;
            Hide(SystemDir);
            MoveEarlierItems(notes);
            WriteReadme(notes);
            return notes;
        }

        static void Hide(string folder)
        {
            try
            {
                var attributes = File.GetAttributes(folder);
                if ((attributes & FileAttributes.Hidden) == 0) File.SetAttributes(folder, attributes | FileAttributes.Hidden);
            }
            catch (Exception) { }
        }

        /// <summary>The items earlier versions kept in the data folder: into System (records, logs, settings) or
        /// System\old-cache (the web profiles).</summary>
        void MoveEarlierItems(List<string> notes)
        {
            var held = new List<string>();
            foreach (string folder in new[] { "runs", "log" })
                MergeFolder(Path.Combine(Root, folder), Path.Combine(SystemDir, folder), notes, held);
            var files = new List<string> { "api-token", "webview-window.json", "webview-viewer-window.json" };
            try { files.AddRange(Directory.GetFiles(Root, "omega-*.json*").Select(Path.GetFileName)); } catch (Exception) { }
            foreach (string name in files) MoveFile(Path.Combine(Root, name), Path.Combine(SystemDir, name), notes, held);
            var caches = new List<string> { "browser", "webview", "webview-viewer" };
            try
            {
                caches.AddRange(Directory.GetDirectories(Root, "webview*.unused-*").Select(Path.GetFileName));
                caches.AddRange(Directory.GetDirectories(Root, "webview*.copying").Select(Path.GetFileName));
                caches.AddRange(Directory.GetFiles(Root, "ini-*.dirty").Select(Path.GetFileName));
            }
            catch (Exception) { }
            foreach (string name in caches.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string from = Path.Combine(Root, name);
                if (!Directory.Exists(from) && !File.Exists(from)) continue;
                try
                {
                    Directory.CreateDirectory(OldCache);
                    string to = Free(Path.Combine(OldCache, name));
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);
                    notes.Add("the earlier web data " + name + " was moved to " + to);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    held.Add(name);
                }
            }
            if (held.Count > 0)
                notes.Add("in use, moved at a later start: " + string.Join(", ", held.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        /// <summary>A path that is not taken yet: the path, else "path-2", "path-3", ...</summary>
        static string Free(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int n = 2; n < 10000; n++)
            {
                string candidate = path + "-" + n;
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
            return path + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>A folder into its place in System: in one step when the place is free, else file by file. A file that
        /// is in the place already stays there; the earlier one goes to recovery when it differs (a copy of the same bytes
        /// is dropped). A file in use stays where it is (for a later start).</summary>
        void MergeFolder(string from, string to, List<string> notes, List<string> held)
        {
            if (!Directory.Exists(from)) return;
            if (!Directory.Exists(to))
            {
                try
                {
                    Directory.Move(from, to);
                    notes.Add(from + " was moved to " + to);
                    return;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }   // a file in it is in use
            }
            int moved = 0;
            foreach (string file in SafeList(() => Directory.GetFiles(from, "*", SearchOption.AllDirectories)))
            {
                string relative = file.Substring(from.Length).TrimStart('\\');
                if (MoveOne(file, Path.Combine(to, relative), notes)) moved++;
                else held.Add(Path.GetFileName(from) + "\\" + relative);
            }
            RemoveEmpty(from);
            if (moved > 0) notes.Add(moved + " files of " + from + " were moved to " + to);
        }

        void MoveFile(string from, string to, List<string> notes, List<string> held)
        {
            if (!File.Exists(from)) return;
            if (MoveOne(from, to, notes)) notes.Add(Path.GetFileName(from) + " was moved to " + to);
            else held.Add(Path.GetFileName(from));
        }

        /// <summary>One file into its new place; false when it is in use.</summary>
        bool MoveOne(string from, string to, List<string> notes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (File.Exists(to))
                {
                    if (SameBytes(from, to))
                    {
                        File.SetAttributes(from, FileAttributes.Normal);
                        File.Delete(from);                 // the same bytes are in the new place already
                        return true;
                    }
                    string aside = Free(Path.Combine(NewRecoveryFolderOnce(), Path.GetFileName(from)));
                    Directory.CreateDirectory(Path.GetDirectoryName(aside));
                    File.Move(from, aside);
                    notes.Add(from + " differs from " + to + " (which is kept): put aside as " + aside);
                    return true;
                }
                File.Move(from, to);
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return false;
            }
        }

        string recoveryOnce;
        string NewRecoveryFolderOnce() => recoveryOnce ?? (recoveryOnce = NewRecoveryFolder());

        static bool SameBytes(string a, string b)
        {
            try
            {
                var x = new FileInfo(a);
                var y = new FileInfo(b);
                if (x.Length != y.Length) return false;
                return SafeFile.ReadAll(a).SequenceEqual(SafeFile.ReadAll(b));
            }
            catch (Exception) { return false; }
        }

        static string[] SafeList(Func<string[]> list)
        {
            try { return list(); }
            catch (Exception) { return new string[0]; }
        }

        static void RemoveEmpty(string folder)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(folder)) RemoveEmpty(sub);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (Exception) { }
        }

        // ---- README.txt

        /// <summary>README.txt (UTF-8 with a byte order mark, Windows line ends): written when it is missing or holds
        /// other text.</summary>
        void WriteReadme(List<string> notes)
        {
            try
            {
                byte[] bytes = ReadmeBytes();
                if (File.Exists(Readme) && SafeFile.ReadAll(Readme).SequenceEqual(bytes)) return;
                SafeFile.Write(Readme, bytes, null, keepBackup: false);
            }
            catch (Exception error) { notes.Add("README.txt could not be written (" + error.Message + ")"); }
        }

        public static byte[] ReadmeBytes() => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(ReadmeText.Replace("\n", "\r\n"))).ToArray();

        public const string ReadmeText =
            "Plate Kinetics Live \u6570\u636e\u6587\u4ef6\u5939\n" +
            "\n" +
            "\u8fd9\u4e2a\u6587\u4ef6\u5939\u91cc\u6709\u4ec0\u4e48\n" +
            "  Experiments\\   \u5b9e\u9a8c\u6587\u4ef6\u3002\u6bcf\u4e2a\u5b9e\u9a8c\u6709\u4e00\u4e2a .pk \u6587\u4ef6\uff08\u5b8c\u6574\u7684\u5b9e\u9a8c\u8bb0\u5f55\uff0c\u53cc\u51fb\u7528 Plate Kinetics Live \u6253\u5f00\uff09\u548c\u4e00\u4e2a\u540c\u540d\u7684\n" +
            "                 .xlsx \u6587\u4ef6\uff08Excel \u8868\u683c\uff09\uff0c\u6309\u5b9e\u9a8c\u5f00\u59cb\u7684\u5e74\u6708\u653e\u5728\u5b50\u6587\u4ef6\u5939\u91cc\uff08\u4f8b\u5982 2026-11\uff09\u3002\n" +
            "                 \u8fd9\u4e9b\u662f\u4f60\u4eec\u81ea\u5df1\u7684\u6587\u4ef6\uff1a\u53ef\u4ee5\u6539\u540d\u3001\u5728 Experiments \u91cc\u79fb\u52a8\u3001\u5220\u9664\u3002\n" +
            "  System\\        \u7a0b\u5e8f\u81ea\u5df1\u7684\u6587\u4ef6\uff08\u9690\u85cf\uff09\uff1a\u539f\u59cb\u8bb0\u5f55\u3001\u65e5\u5fd7\u3001\u8bbe\u7f6e\u3001\u4fdd\u5b58\u8fc7\u7684\u5206\u6790\uff0c\u4ee5\u53ca\u6062\u590d\u7528\u7684\u526f\u672c\u3002\n" +
            "                 \u8bf7\u4e0d\u8981\u6539\u52a8\u91cc\u9762\u7684\u6587\u4ef6\u3002\u4ece Experiments \u5220\u6389\u7684\u5b9e\u9a8c\uff0c\u53ef\u4ee5\u5728\u7a0b\u5e8f\u7684\u201c\u5b9e\u9a8c\u201d\u5217\u8868\u91cc\u6062\u590d\u3002\n" +
            "  README.txt     \u672c\u8bf4\u660e\u3002\n" +
            "\n" +
            "\u5907\u4efd\n" +
            "  \u8bf7\u5907\u4efd\u6574\u4e2a PlateKineticsLive-data \u6587\u4ef6\u5939\uff08\u5305\u62ec\u9690\u85cf\u7684 System \u6587\u4ef6\u5939\uff09\uff0c\u4e0d\u8981\u53ea\u5907\u4efd Experiments\u3002\n" +
            "  \u6062\u590d\u65f6\uff0c\u628a\u6574\u4e2a\u6587\u4ef6\u5939\u653e\u56de\u7a0b\u5e8f\u65c1\u8fb9\u3002\n" +
            "\n" +
            "\u51fa\u4e86\u95ee\u9898\n" +
            "  \u635f\u574f\u7684\u6587\u4ef6\u4e0d\u4f1a\u88ab\u5220\u9664\uff0c\u800c\u662f\u632a\u5230 System\\recovery \u6587\u4ef6\u5939\u91cc\u3002\u5b8c\u6574\u7684\u8bf4\u660e\u89c1\u7a0b\u5e8f\u7a97\u53e3\u91cc\u7684\u201c\u5e2e\u52a9\u201d\u3002\n" +
            "\n" +
            "\n" +
            "Plate Kinetics Live data folder\n" +
            "\n" +
            "What is in this folder\n" +
            "  Experiments\\   The experiment files. Each experiment has a .pk file (the complete record of the experiment; a\n" +
            "                 double-click opens it in Plate Kinetics Live) and an .xlsx file of the same name (an Excel\n" +
            "                 workbook), in subfolders by the month the experiment started (for example 2026-11).\n" +
            "                 These files are yours: you may rename them, move them within Experiments, or delete them.\n" +
            "  System\\        The program's own files (hidden): the original records, logs, settings, saved analyses and the\n" +
            "                 copies used for restoring. Please do not change anything in it. An experiment deleted from\n" +
            "                 Experiments can be restored in the program's Experiments list.\n" +
            "  README.txt     This text.\n" +
            "\n" +
            "Backup\n" +
            "  Back up the whole PlateKineticsLive-data folder, including the hidden System folder - not only Experiments.\n" +
            "  To restore, put the whole folder back beside the program.\n" +
            "\n" +
            "Problems\n" +
            "  Damaged files are never deleted: they are moved to the folder System\\recovery. The program window's Help\n" +
            "  has the full manual.\n";
    }
}
