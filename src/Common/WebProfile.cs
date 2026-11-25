// The window's web profile: what Windows' own Edge (WebView2) keeps on disk for each Windows user who opens the window.
// - Where: installed edition, the user's own %LOCALAPPDATA%\<program's name>\<profile>, so that the program's data folder
//   holds only the experiments and the program's records; portable edition, beside the program as before
//   (System\users\<user>\<profile>), so that nothing of it is put elsewhere on the PC.
// - Edge's add-on components are not downloaded (--disable-component-update): video DRM, ad-filter lists, speech,
//   hyphenation and the like, about 70 MB for each user; the page needs none of them. Edge itself is updated by Windows
//   as before.
// - Before each start of the window only what the page keeps is left: its settings (Local Storage), its autosave
//   (IndexedDB) and Edge's own small state files. Caches and everything else Edge makes again when it needs them. Nothing
//   is removed while a window has the profile open (Edge holds its lockfile then).
// An earlier version's profile (System\users\<user>\webview) is moved once: its kept parts are copied to the new place,
// then the old folder is removed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAAControl
{
    public static class WebProfile
    {
        /// <summary>The arguments the window's Edge is started with.</summary>
        public const string BrowserArguments = "--disable-component-update";

        /// <summary>The parts of a profile folder (relative to it) that are kept from one start to the next.</summary>
        public static readonly string[] Kept =
        {
            @"EBWebView\Local State",
            @"EBWebView\Default\Local Storage",
            @"EBWebView\Default\IndexedDB",
            @"EBWebView\Default\Preferences",
            @"EBWebView\Default\Secure Preferences",
        };

        /// <summary>The profile folder of a window. installed: in the user's local application data (localAppData, null:
        /// this user's), under the program's name; else (portable) userDir\profile.</summary>
        public static string Folder(string userDir, string profile, bool installed, string displayName, string localAppData = null)
        {
            if (!installed || string.IsNullOrWhiteSpace(displayName)) return Path.Combine(userDir, profile);
            string root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(root) ? Path.Combine(userDir, profile) : Path.Combine(root, displayName, profile);
        }

        /// <summary>A window has the profile open: Edge holds its lockfile.</summary>
        public static bool InUse(string folder)
        {
            string lockfile = Path.Combine(folder, "EBWebView", "lockfile");
            if (!File.Exists(lockfile)) return false;
            try
            {
                using (new FileStream(lockfile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>An earlier version's profile (old) to the new place (folder): its kept parts are copied there (into a
        /// folder beside it first, which then takes the new name), then the old folder is removed. Nothing is done while
        /// the old one is in use or when the new place has a profile already. What was done, for the log; null: nothing.</summary>
        public static string MoveOld(string old, string folder)
        {
            if (string.Equals(Full(old), Full(folder), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(old) || InUse(old)) return null;
            int copied = 0;
            if (!Directory.Exists(Path.Combine(folder, "EBWebView")))
            {
                string staging = Full(folder) + ".moving";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                foreach (string part in Kept) copied += Copy(Path.Combine(old, part), Path.Combine(staging, part));
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(Path.GetDirectoryName(Full(folder)));
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(staging, true);       // something else is there: left as it is, the old profile too
                    return null;
                }
                Directory.Move(staging, folder);
            }
            long size = SizeOf(old);
            string left = "";
            try { Directory.Delete(old, true); }
            catch (Exception error) { left = "; the old folder could not be removed completely (" + error.Message + ")"; }
            return "the window's web profile moved from " + old + " to " + folder + " (" + copied + " files kept, " +
                   (size / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB in the old folder)" + left;
        }

        /// <summary>Everything of the profile but the kept parts is removed (nothing while a window has it open). The bytes
        /// that went.</summary>
        public static long Trim(string folder)
        {
            string edge = Path.Combine(folder, "EBWebView");
            if (!Directory.Exists(edge) || InUse(folder)) return 0;
            var keep = new HashSet<string>(Kept.Select(p => Full(Path.Combine(folder, p))), StringComparer.OrdinalIgnoreCase);
            return TrimIn(edge, keep);
        }

        static long TrimIn(string dir, HashSet<string> keep)
        {
            long freed = 0;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception) { return 0; }
            foreach (string entry in entries)
            {
                string full = Full(entry);
                if (keep.Contains(full)) continue;
                bool holdsKept = keep.Any(k => k.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (Directory.Exists(full))
                    {
                        if (holdsKept) { freed += TrimIn(full, keep); continue; }
                        long size = SizeOf(full);
                        Directory.Delete(full, true);
                        freed += size;
                    }
                    else
                    {
                        long size = new FileInfo(full).Length;
                        File.Delete(full);
                        freed += size;
                    }
                }
                catch (Exception) { /* a file Edge still holds (or one we may not touch): left, tried again next time */ }
            }
            return freed;
        }

        static int Copy(string from, string to)
        {
            if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to, true);
                return 1;
            }
            if (!Directory.Exists(from)) return 0;
            int n = 0;
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) n += Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (string sub in Directory.GetDirectories(from)) n += Copy(sub, Path.Combine(to, Path.GetFileName(sub)));
            return n;
        }

        static long SizeOf(string dir)
        {
            try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } }); }
            catch (Exception) { return 0; }
        }

        static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }
}
