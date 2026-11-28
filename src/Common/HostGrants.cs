// What the window does for its page, within limits. The page shows data from files of other people: a script that got
// to run in it must not be able to have the window write or read files of its choosing, or start programs.
//   HostGrants   the project files this window itself handed to the page, or the user chose in one of its "Save as"
//                dialogs: only those are written without asking, only those (and the recent files) are shown in
//                Explorer; and which links of the page are handed to Windows at all
//   LatestFile   the page's autosave copy: always the latest text, written by one background worker
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SAAControl
{
    /// <summary>The project files a window handed out in this session. The page names a file by its path; a path is
    /// taken only when the window itself gave it out before (full paths compared, in any case of the letters).</summary>
    public sealed class HostGrants
    {
        // what "Save" writes: a project file
        static readonly string[] ProjectExtensions = { ".pkproj", ".pka", ".json" };

        readonly object gate = new object();
        readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The full path as it is compared; "" for what is no path (empty, characters a path cannot have).</summary>
        public static string Full(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path); }
            catch (Exception) { return ""; }
        }

        /// <summary>A project file by its name: .pkproj, .pka or .json.</summary>
        public static bool IsProjectFile(string path)
        {
            string full = Full(path);
            if (full.Length == 0) return false;
            string extension = Path.GetExtension(full);
            foreach (string known in ProjectExtensions)
                if (extension.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The window handed this file to the page (it opened it: a dialog, the command line, a file dropped on
        /// it, another start of the program), or the user chose it in a "Save as" dialog of the window.</summary>
        public void Grant(string path)
        {
            string full = Full(path);
            if (full.Length == 0) return;
            lock (gate) paths.Add(full);
        }

        public bool Granted(string path)
        {
            string full = Full(path);
            if (full.Length == 0) return false;
            lock (gate) return paths.Contains(full);
        }

        /// <summary>"Save" may write there without a dialog: a project file this window handed out.</summary>
        public bool MayWrite(string path) => Granted(path) && IsProjectFile(path);

        /// <summary>One of the paths of a list the window keeps itself (its recent files).</summary>
        public static bool InList(string path, IEnumerable<string> list)
        {
            string full = Full(path);
            if (full.Length == 0 || list == null) return false;
            foreach (string item in list)
                if (string.Equals(Full(item), full, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The file may be shown in Explorer: one the window handed out, or one of its recent files.</summary>
        public bool MayReveal(string path, IEnumerable<string> recent) => Granted(path) || InList(path, recent);

        /// <summary>A link of the page that leaves the program (a web address, "write an e-mail") as it is handed to
        /// Windows: an absolute http, https or mailto address. null for everything else - a file, a program, a folder
        /// on a share, any other scheme - which Windows would open or run just the same.</summary>
        public static string ExternalLink(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri address)) return null;
            bool web = address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps;
            if (!web && address.Scheme != Uri.UriSchemeMailto) return null;
            if (web && address.Host.Length == 0) return null;
            return address.AbsoluteUri;
        }
    }

    /// <summary>A file that gets the latest text given to it, written in the background by one worker: never two writes
    /// at once, and a text that a newer one overtook while the worker was busy is not written at all. Each write is whole
    /// (a temporary file takes the place of the file).</summary>
    public sealed class LatestFile
    {
        readonly string file;
        readonly Action<string> write;
        readonly object gate = new object();
        string waiting;                        // the latest text, not written yet
        bool working;                          // the worker runs
        bool failed;                           // the last write failed (said in the log once, not at every try)

        /// <param name="write">tests: how a text is written (null: into the file)</param>
        public LatestFile(string file, Action<string> write = null)
        {
            this.file = file;
            this.write = write ?? Replace;
        }

        /// <summary>The text to be in the file; returns at once.</summary>
        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (gate)
            {
                waiting = text;
                if (working) return;
                working = true;
            }
            ThreadPool.QueueUserWorkItem(state => Work());
        }

        void Work()
        {
            while (true)
            {
                string text;
                lock (gate)
                {
                    text = waiting;
                    waiting = null;
                    if (text == null)
                    {
                        working = false;
                        Monitor.PulseAll(gate);
                        return;
                    }
                }
                try
                {
                    write(text);
                    failed = false;
                }
                catch (Exception error)
                {
                    if (!failed) Log.Warning("the autosave copy " + file + " could not be written (" + error.Message + ")");
                    failed = true;
                }
            }
        }

        /// <summary>Waits until what was given is written, atMost that long; false when it is not written by then.</summary>
        public bool Complete(TimeSpan atMost)
        {
            var waited = Stopwatch.StartNew();
            lock (gate)
            {
                while (working)
                {
                    TimeSpan left = atMost - waited.Elapsed;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(gate, left)) return !working;
                }
                return true;
            }
        }

        void Replace(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        }
    }
}
