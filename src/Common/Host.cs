// What both programs do around their data side (Python: app.py main()): find the data folder and put it in order
// (DataLayout: README.txt, Experiments, System), make sure only one instance runs, open the log, repair what a power cut
// or a crash left (Recovery), start the watcher, show the window, and end everything cleanly.
//
// Three ways to run (see ServiceMode.cs): as the Windows service of the installed program (watcher and local server,
// no window), as the window of the installed program (shows what the service serves), or - portable, with
// --no-window, or when the service cannot run - as the whole program in one process, as the Python programs did.
//
// "<exe> --open <file.pk>" (a double-click on an experiment file; a bare .pk argument too, "Open with") shows that run
// in the page. The installed program's window that is open gets it (a request file in %TEMP%\<name>-open, then the
// window's show signal; it comes to the front); else a window is opened for it. A program that records itself (the
// portable one) gets it through its server (POST /api/open: the page opens it from "openQueue").
//
// Each Windows user's window keeps its web profile, size and log in System\users\<user> (or, where the user may not
// write there, in %LOCALAPPDATA%\<name>).
//
// The process that records also watches itself (Watchdog.cs: its looks at the instruments; a crash file for an error
// nothing caught; how the previous run ended), checks the PC every 10 minutes (PcHealth.cs) and keeps it awake while an
// instrument measures. A port another program holds is named (PortOwner) in the log and, for a window, in a message.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SAAControl
{
    public static class Host
    {
        public const string DataFolderName = InstallShared.DataFolder;        // beside the program: records, the log, the window's profile
        // its earlier names, newest first (SAAControl-data up to 2026-11-23; "SAA experiment data" in Chinese before)
        public static readonly string[] OldDataFolderNames = InstallShared.OldDataFolders;

        static string Text(params int[] codes) => new string(codes.Select(c => (char)c).ToArray());

        /// <summary>The command line: --name value pairs (repeatable) and bare flags. Which names take a value is given.</summary>
        public sealed class Args
        {
            readonly Dictionary<string, List<string>> values = new Dictionary<string, List<string>>();
            public Args(string[] argv, ICollection<string> withValue)
            {
                for (int i = 0; i < argv.Length; i++)
                {
                    string a = argv[i], name = a, value = null;
                    int eq = a.IndexOf('=');
                    if (a.StartsWith("--") && eq > 0) { name = a.Substring(0, eq); value = a.Substring(eq + 1); }
                    else if (withValue.Contains(a) && i + 1 < argv.Length) value = argv[++i];
                    if (!values.TryGetValue(name, out var list)) values[name] = list = new List<string>();
                    if (value != null) list.Add(value);
                }
            }
            public bool Has(string name) => values.ContainsKey(name);
            public string Value(string name) => values.TryGetValue(name, out var l) && l.Count > 0 ? l[l.Count - 1] : null;
            public List<string> All(string name) => values.TryGetValue(name, out var l) ? new List<string>(l) : new List<string>();

            /// <summary>The experiment or project file to show: --open's, else a bare argument that is a .pk or .pkproj
            /// (Explorer's "Open with"); null for none.</summary>
            public string OpenFile()
            {
                string file = Value("--open");
                if (file == null)
                    foreach (string name in values.Keys)
                        if (!name.StartsWith("--") &&
                            (name.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(InstallShared.PkProjExtension, StringComparison.OrdinalIgnoreCase)))
                            file = name;
                if (string.IsNullOrWhiteSpace(file)) return null;
                try { return Path.GetFullPath(file.Trim().Trim('"')); }
                catch (Exception) { return null; }
            }
        }

        /// <summary>What a program's watcher is made with: the command line, the data folder's layout (the watcher keeps
        /// its records in Layout.Runs: give it Layout.SystemDir as its data folder) and the experiment files.</summary>
        public sealed class Context
        {
            public Args Args;
            public DataLayout Layout;
            public Experiments Experiments;
        }

        /// <summary>The data folder in baseDir, and a note of what was done about a folder of an earlier name ("" =
        /// nothing). A folder of an earlier name is renamed at the start; when that fails (something in it is in use),
        /// it stays in use under its name and the renaming is tried again at the next start. A folder of the current
        /// name always wins, and of the earlier names the newest.</summary>
        public static string DataFolder(string baseDir, out string note, bool archimedNotes = false, bool mayRename = true)
        {
            string fresh = Path.Combine(baseDir, DataFolderName);
            note = "";
            if (Directory.Exists(fresh) || File.Exists(fresh)) return fresh;
            foreach (string name in OldDataFolderNames)
            {
                string old = Path.Combine(baseDir, name);
                // BMG: the old folder must be a folder (old.is_dir()); Archimed: anything of that name (old.exists())
                bool oldThere = archimedNotes ? (Directory.Exists(old) || File.Exists(old)) : Directory.Exists(old);
                if (!oldThere) continue;
                if (!mayRename)
                {
                    // the former program's service is still registered (an installation is replacing it): should the
                    // installation fail, the former program goes on in this folder, so it keeps its name until it is gone
                    note = "the data folder keeps its name " + name + " while the former program's service is registered";
                    return old;
                }
                try { Directory.Move(old, fresh); }
                catch (Exception error)
                {
                    // the two Python programs worded these notes differently: kept as they were
                    note = archimedNotes
                        ? "the data folder " + old + " could not be renamed to " + DataFolderName + " (" + error.Message + "); it stays in use"
                        : "the data folder keeps its old name for now: renaming it to " + DataFolderName + " failed (" + error.Message + ")";
                    return old;
                }
                note = archimedNotes ? "the data folder " + old + " was renamed to " + fresh : "the data folder was renamed to " + DataFolderName;
                return fresh;
            }
            return fresh;
        }

        /// <summary>Run the program. makeWatcher is called once the log is open and must log the start line, as app.py does.
        /// serviceDescription: the text Windows shows for the service. rebuildRecord: a record lost at a power cut made
        /// again from the instrument software's own files (null: none).</summary>
        public static int Run(string appName, int defaultPort, string[] argv, ICollection<string> programOptionsWithValue,
                              Func<Context, IWatcher> makeWatcher, string serviceDescription,
                              Func<Context, string, Dictionary<string, object>> rebuildRecord = null)
        {
            var withValue = new HashSet<string>(programOptionsWithValue) { "--port", "--data-dir", "--open" };
            var args = new Args(argv, withValue);
            Runtime.AppName = appName;
            Runtime.ProgramName = InstallShared.DisplayName(appName);
            if (args.Has("--uninstall")) return Uninstaller.Run(appName);
            // the service's own starts come before everything else: a registered service is always the recorder, also
            // in a folder that (by a stray marker file) looks like an analysis-only installation
            if (args.Has("--install-service")) return ServiceMode.Install(appName, serviceDescription);
            if (args.Has("--uninstall-service")) return ServiceMode.Uninstall(appName);
            if (args.Has("--start-service")) return ServiceMode.Start(appName, 60) ? 0 : 1;
            if (args.Has("--service"))
            {
                ServiceBase.Run(new HostService(appName, stop => RunCore(appName, defaultPort, args, makeWatcher, stop, null, rebuildRecord)));
                return 0;
            }
            // the installer started the program again after a silent update that failed (--update-failed=<its exit
            // code>; never a file to open): the window tells the page once it is there
            Updater.FailedUpdate = Updater.UpdateFailedCode(argv);
            if (args.Has("--analysis-only") || AnalysisRunner.IsAnalysisOnlyInstall(Files.ProgramDir))
            {
                if (args.Has("--tray")) return 0;      // (an analysis-only installation has no tray)
                return AnalysisRunner.Run(appName, argv);
            }
            // the tray of the installed program (one per signed-in user): notifications of the runs' events
            if (args.Has("--tray")) return TrayMode.Run(appName, int.TryParse(args.Value("--port"), out int trayPort) ? trayPort : defaultPort, args.Value("--data-dir"));
            // (an installed program - its list of files is beside it - is never a temporary copy, wherever it is)
            if (!args.Has("--no-window") && !Installed(appName) && InsideZip(Files.ProgramDir))
            {
                // "Extract the zip into a folder first, then run <name>.exe there. Run inside the zip, what it records
                // would be lost."
                MessageBox.Show(Text(0x8bf7, 0x5148, 0x628a, 0x538b, 0x7f29, 0x5305, 0x89e3, 0x538b, 0x5230, 0x4e00, 0x4e2a, 0x6587,
                                     0x4ef6, 0x5939, 0xff0c, 0x518d, 0x8fd0, 0x884c, 0x91cc, 0x9762, 0x7684, 0x20) + appName + ".exe" +
                                Text(0x3002, 0x76f4, 0x63a5, 0x5728, 0x538b, 0x7f29, 0x5305, 0x91cc, 0x8fd0, 0x884c, 0xff0c, 0x8bb0,
                                     0x5f55, 0x7684, 0x6570, 0x636e, 0x4f1a, 0x4e22, 0x5931, 0x3002) +
                                "\n\nExtract the zip into a folder first, then run " + appName + ".exe there. Run inside the zip, " +
                                "what it records would be lost.", InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            // the window only when the service can do the work (a service that is disabled, marked for deletion or whose
            // program file is gone cannot)
            if (!args.Has("--no-window") && ServiceMode.IsUsable(appName)) return RunWindowOnly(appName, defaultPort, args, makeWatcher, rebuildRecord);
            return RunCore(appName, defaultPort, args, makeWatcher, null, null, rebuildRecord);
        }

        /// <summary>The installer put the program there (its list of files is beside it).</summary>
        static bool Installed(string appName) => File.Exists(InstallShared.ManifestPath(Files.ProgramDir, appName));

        /// <summary>Started from inside a zip: the archiver (Explorer, 7-Zip, WinRAR, 360, Bandizip, ...) put the program
        /// into a temporary folder that goes away again - and with it everything the program records. Any folder in the
        /// temporary folder counts: each archiver names its own differently.</summary>
        static bool InsideZip(string dir)
        {
            try
            {
                string temp = LongPath(Path.GetFullPath(Path.GetTempPath())).TrimEnd('\\');
                string full = LongPath(Path.GetFullPath(dir)).TrimEnd('\\');
                return full.Equals(temp, StringComparison.OrdinalIgnoreCase) || full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>A path with its short (8.3) names spelled out: %TEMP% is often given as C:\Users\ADMINI~1\..., the
        /// program's folder in full.</summary>
        static string LongPath(string path)
        {
            var buffer = new StringBuilder(1024);
            int length = GetLongPathName(path, buffer, buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        static string DataDir(string appName, Args args, out string note)
        {
            note = "";
            return args.Value("--data-dir") ?? DataFolder(Files.ProgramDir, out note, appName.EndsWith("-Archimed", StringComparison.OrdinalIgnoreCase),
                                                          mayRename: !FormerServiceHere(appName));
        }

        /// <summary>The data folder's layout: the full one, or - while the program under its former name is registered as
        /// a service of this same folder (an installation is replacing it) - the folder as that program uses it.</summary>
        static DataLayout LayoutOf(string dataDir, Args args, string appName) =>
            new DataLayout(dataDir, legacy: args.Value("--data-dir") == null && FormerServiceHere(appName));

        /// <summary>The data folder of the program in this folder as it is used now (for the tray, the window and other
        /// processes beside the one that records): --data-dir's, else PlateKineticsLive-data (or a folder of an earlier
        /// name that is not renamed yet).</summary>
        public static DataLayout LayoutForProgram(string appName, string dataDirOverride = null)
        {
            string dataDir = dataDirOverride ?? Path.Combine(Files.ProgramDir, DataFolderName);
            if (dataDirOverride == null && !Directory.Exists(dataDir))
                foreach (string name in OldDataFolderNames)
                {
                    string old = Path.Combine(Files.ProgramDir, name);
                    if (Directory.Exists(old)) { dataDir = old; break; }
                }
            return new DataLayout(dataDir, legacy: dataDirOverride == null && FormerServiceHere(appName));
        }

        /// <summary>Whether the program under its former name (SAAControl until 2026-11-23) is still registered as a
        /// Windows service of this program's folder: then the data folder is used as that program uses it (the installer
        /// removes the former, then starts this program's service again). A former program in another folder (the
        /// program moved to a new folder) does not count.</summary>
        static bool FormerServiceHere(string appName)
        {
            string former = InstallShared.FormerName(appName);
            if (former == null) return false;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + former))
                    if (key == null) return false;
                string exe = InstallShared.ServiceExe(former);
                return exe == null || InstallShared.SameFolder(Path.GetDirectoryName(exe), Files.ProgramDir);
            }
            catch (Exception) { return false; }
        }

        /// <summary>Where a second start leaves the experiment files to show for the open window: %TEMP%\&lt;name&gt;-open.</summary>
        static string RequestFolder(string appName) => Path.Combine(Path.GetTempPath(), appName + "-open");

        /// <summary>Leave a file to show for the window that is open (it takes it when its show signal comes).</summary>
        static void Request(string appName, string file)
        {
            if (file == null) return;
            try
            {
                string folder = RequestFolder(appName);
                Directory.CreateDirectory(folder);
                string name = DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
                File.WriteAllText(Path.Combine(folder, name), file, new UTF8Encoding(false));
            }
            catch (Exception error) { Log.Warning("the experiment file " + file + " could not be handed to the open window (" + error.Message + ")"); }
        }

        /// <summary>The files second starts left to show (in the order they came; those older than two minutes are dropped).</summary>
        public static List<string> TakeRequests(string appName)
        {
            var files = new List<string>();
            try
            {
                string folder = RequestFolder(appName);
                if (!Directory.Exists(folder)) return files;
                foreach (string request in Directory.GetFiles(folder, "*.txt").OrderBy(p => p, StringComparer.Ordinal))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(request) < TimeSpan.FromMinutes(2))
                        {
                            string file = File.ReadAllText(request, Encoding.UTF8).Trim();
                            if (file.Length > 0) files.Add(file);
                        }
                        File.Delete(request);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return files;
        }

        /// <summary>The watcher and the local server; with a window (the whole program), without one (--no-window), or as
        /// the service (stop is set when Windows stops it). reason: why this process does the work, for the log.</summary>
        public static int RunCore(string appName, int defaultPort, Args args, Func<Context, IWatcher> makeWatcher, ManualResetEvent stop,
                                  string reason = null, Func<Context, string, Dictionary<string, object>> rebuildRecord = null)
        {
            bool asService = stop != null;
            int port = int.TryParse(args.Value("--port"), out int p) ? p : defaultPort;
            bool noWindow = asService || args.Has("--no-window");
            string url = "http://127.0.0.1:" + port + "/";
            string dataDir = DataDir(appName, args, out string note);
            string open = noWindow ? null : args.OpenFile();          // an experiment file to show in the page

            // One instance only. The port is the lock: whoever binds it is the instance; everybody else brings that
            // instance's window to the front or, while it is closing, waits for it. The service waits for as long as
            // another instance (the previous program, or the program started by hand) holds the port.
            var service = new Service(appName);
            var server = new WebServer(service, port);
            var waited = Stopwatch.StartNew();
            SocketException refused = null;
            bool named = false, earlyLog = false;
            Stopwatch saidWaiting = null;              // since the service last said that it waits for another instance
            string saidIn = null;                      // (the log that says it)
            while (true)
            {
                string other = WebServer.OtherInstance(port, appName);
                if (other == "running" && !asService)
                {
                    if (!noWindow) AskToShow(appName, url, dataDir, open, port, args);
                    return 0;
                }
                // the service waits while another instance of the program has the port (a copy started by hand, a window
                // that records because the service did not run when it was opened): the service records nothing
                // meanwhile, so its log says whom it waits for - after 30 s, then every 10 minutes
                if (other == "running" && asService && waited.Elapsed.TotalSeconds >= 30 && (saidWaiting == null || saidWaiting.Elapsed.TotalMinutes >= 10))
                {
                    saidWaiting = Stopwatch.StartNew();
                    try
                    {
                        earlyLog |= SayServiceWaits(appName, port, LayoutOf(dataDir, args, appName).Logs, waited.Elapsed);
                        saidIn = Log.CurrentFile;
                    }
                    catch (Exception) { }
                }
                if (other == "")
                {
                    try { server.Bind(); break; }
                    catch (SocketException error) { refused = error; }   // bound by an instance that does not answer yet (starting) or any more
                }
                // a program that is not this one holds the port (or Windows keeps it): named in the log - and, for a
                // program started by hand with its window, in a message - instead of a silent wait
                if (refused != null && !named && waited.Elapsed.TotalSeconds >= 5)
                {
                    var owner = PortOwner.Find(port);
                    if (owner == null || !IsThisProgram(owner.Value.Program, appName))
                    {
                        named = true;
                        var (en, zh) = PortOwner.Words(port, InstallShared.DisplayName(appName), owner, refused.SocketErrorCode == SocketError.AccessDenied);
                        if (Log.CurrentFile == null)
                        {
                            Log.Open(Path.Combine(LayoutOf(dataDir, args, appName).Logs, appName + ".log"));
                            earlyLog = true;
                        }
                        Log.Error(en + (asService ? " The service waits until the port is free." : ""));
                        if (!noWindow)
                        {
                            MessageBox.Show(zh + "\n\n" + en, InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            Log.Close();
                            return 1;
                        }
                    }
                }
                if (!asService && waited.Elapsed.TotalSeconds > 150) return 0;     // as the Python: give up quietly
                if (asService && stop.WaitOne(0)) return 0;
                Thread.Sleep(asService && waited.Elapsed.TotalSeconds > 30 ? 5000 : 500);
            }
            if (earlyLog) Log.Close();                 // (the data folder is put in order first; the log is opened again then)

            // the data folder in order (what earlier versions kept in it moves into System), then the log
            DataLayout layout = LayoutOf(dataDir, args, appName);
            List<string> notes = layout.Prepare();
            if (!asService && !CanWrite(layout.SystemDir))
            {
                // a copy of the program in a folder this user may not change (under Program Files, on a share, on a CD):
                // it would record nothing. "The program cannot save its data in its folder: ... Copy the whole folder of
                // the program to a folder you can write to (for example Documents), then start it there."
                string text = "\u7a0b\u5e8f\u65e0\u6cd5\u5728\u5b83\u7684\u6587\u4ef6\u5939\u91cc\u4fdd\u5b58\u6570\u636e\uff1a\n" + layout.Root +
                              "\n\u8bf7\u628a\u6574\u4e2a\u7a0b\u5e8f\u6587\u4ef6\u5939\u590d\u5236\u5230\u4e00\u4e2a\u53ef\u4ee5\u5199\u5165\u7684\u6587\u4ef6\u5939\uff08\u4f8b\u5982\u201c\u6587\u6863\u201d\uff09\uff0c\u518d\u5728\u90a3\u91cc\u542f\u52a8\u3002\n\n" +
                              "The program cannot save its data in its folder:\n" + layout.Root +
                              "\nCopy the whole folder of the program to a folder you can write to (for example Documents), then start it there.";
                if (!noWindow) MessageBox.Show(text, InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                server.Stop();
                return 1;
            }
            Log.Open(Path.Combine(layout.Logs, appName + ".log"));    // before serving: a failing request is logged
            Runtime.AppName = appName;
            Runtime.ProgramName = InstallShared.DisplayName(appName);
            Runtime.Edition = Installed(appName) ? "installed" : "portable";
            Runtime.AsService = asService;
            Runtime.Layout = layout;
            var experiments = new Experiments(layout, InstallShared.DisplayName(appName), Experiments.ProgramVersion());
            Runtime.Settings = experiments.Settings;
            Log.Info(Runtime.ProgramName + " " + Experiments.ProgramVersion() + " (" + Runtime.Edition + (asService ? ", the Windows service" : "") +
                     ") starts; data folder " + layout.Root + (layout.Legacy ? " (as the former program uses it)" : "") + ", " +
                     FreeText(experiments.Storage.FreeBytes()) + "; Windows " + Environment.OSVersion.Version + ", code page " + Encoding.Default.CodePage);
            if (note.Length > 0) Log.Info(note);
            foreach (string line in notes) Log.Info(line);
            if (reason != null) Log.Info(reason);
            if (saidWaiting != null)
                Log.Warning("before this start the service waited " + Lasted(waited.Elapsed) + " for another instance of the program, which held port " +
                            port + " and recorded in its place" +
                            (saidIn != null && !saidIn.Equals(Log.CurrentFile, StringComparison.OrdinalIgnoreCase) ? " (see " + saidIn + ")" : ""));
            if (asService)
            {
                // the instrument software comes first: the recorder only reads, and catches up on what it missed
                try { using (Process me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception) { }
                Log.Info("running as the Windows service " + appName + " (priority below normal)");
                ServiceMode.UseUserProfile(appName);
            }
            // how the previous run ended (a crash shows "crash-restart"); an error nothing caught goes to a crash file
            Watchdog.Started(layout);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Watchdog.Crash(e.ExceptionObject as Exception, e.IsTerminating);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Warning("an error of a background task was not looked at: " + e.Exception?.GetBaseException().Message);
                e.SetObserved();
            };
            // one API token for the data folder, kept across restarts: a window left open while the service restarts
            // (Windows restarting it after a failure, an update) goes on getting answers instead of "refused"
            try { service.Token = Service.KeptToken(layout.Token); }
            catch (Exception error) { Log.Warning("the API token could not be kept in " + layout.Token + " (" + error.Message + "): it changes at every start"); }
            // only a program in the user's session may open Explorer, the manual or the Recycle Bin for the page; the
            // service's window does that itself
            try { service.InUserSession = !asService && Process.GetCurrentProcess().SessionId != 0; } catch (Exception) { }
            service.Experiments = experiments;
            server.Start();

            // what a power cut or a crash left: repaired before the watcher begins
            var context = new Context { Args = args, Layout = layout, Experiments = experiments };
            Recovery.Report report;
            try { report = Recovery.Records(layout, experiments, rebuildRecord == null ? null : (Func<string, Dictionary<string, object>>)(runId => rebuildRecord(context, runId))); }
            catch (Exception error)
            {
                Log.Error("the check of the records at the start failed", error);
                report = new Recovery.Report();
            }
            Recovery.Show(report.Lines);
            experiments.RepairedChanged = () => Recovery.Show(report.Lines.Concat(experiments.Repaired));
            experiments.Storage.StartChecking();

            IWatcher watcher = null;
            while (watcher == null)
            {
                try
                {
                    watcher = makeWatcher(context);
                    watcher.Start();                   // (the BMG watcher's first look runs here and may throw, as in Python)
                }
                catch (Exception error)
                {
                    Log.Error("the program could not start", error);
                    try { watcher?.Stop(); } catch (Exception) { }
                    watcher = null;
                    // the service tries again (unexpected errors never end it); a program started by hand ends
                    if (!asService || stop.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        experiments.Storage.StopChecking();
                        server.Stop();
                        Log.Close();
                        return asService ? 0 : 1;
                    }
                }
            }
            service.Watcher = watcher;                 // from now on the page gets answers
            // the recorder watches its own looks (as the service it ends itself when they stop, so that Windows starts
            // it again), and the PC is checked every 10 minutes (sleep, a pending restart, the instruments' disks)
            Watchdog.Start(asService);
            PcChecks.Start(layout.Root);

            try
            {
                if (asService) stop.WaitOne();
                else if (noWindow) Thread.Sleep(Timeout.Infinite);
                else ShowWindow(appName, url, layout, service, open);
            }
            finally
            {
                service.Closing = true;                // first: a new start waits until this one is gone
                Watchdog.Stop();
                try { watcher.Stop(); } catch (Exception error) { Log.Error("stopping the watcher failed", error); }
                // workbooks still behind their saved analyses (the rest are made at the next start)
                experiments.Close(TimeSpan.FromSeconds(10));
                PcChecks.Stop();
                KeepAwake.Release();
                experiments.Storage.StopChecking();
                server.Stop();
                Watchdog.Stopped();
                Log.Info("stopped");
                Log.Close();
            }
            return 0;
        }

        /// <summary>The log the service writes to while it waits for another instance of the program: a file of its own
        /// beside the program's log. The program's log may be that instance's (when it records into the same data
        /// folder), and two processes appending to one file overwrite each other's lines.</summary>
        static string ServiceWaitsLog(string logs, string appName) => Path.Combine(logs, appName + "-service-waits.log");

        /// <summary>The service cannot record: another instance of this program answers on the port and records in its
        /// place. One Error line names that process (in ServiceWaitsLog, unless a log is open already). True when the
        /// log was opened for it (it is closed again once the service has the port).</summary>
        static bool SayServiceWaits(string appName, int port, string logs, TimeSpan waited)
        {
            var owner = PortOwner.Find(port);
            string who = "a process that was not found";
            if (owner != null)
            {
                string path = null;
                try { using (Process process = Process.GetProcessById(owner.Value.Pid)) path = process.MainModule.FileName; }
                catch (Exception) { }
                who = owner.Value.Program + " (process " + owner.Value.Pid + (path != null ? ", " + path : "") + ")";
            }
            bool opened = false;
            if (Log.CurrentFile == null)
            {
                Log.Open(ServiceWaitsLog(logs, appName));
                opened = true;
            }
            Log.Error("The Windows service " + appName + " records nothing: another instance of the program, " + who + ", holds port " + port +
                      " and records in its place. The service has waited " + Lasted(waited) +
                      " and goes on waiting until that instance ends: close it (its window, or end that process), and the service records again.");
            return opened;
        }

        static string Lasted(TimeSpan time) => time.TotalMinutes < 2 ? (int)time.TotalSeconds + " seconds" : (int)time.TotalMinutes + " minutes";

        /// <summary>Whether a program file name is this program's (or its former name's): an instance that is starting or
        /// ending, not another program.</summary>
        static bool IsThisProgram(string program, string appName)
        {
            string name = Path.GetFileNameWithoutExtension(program ?? "");
            if (name.Equals(appName, StringComparison.OrdinalIgnoreCase)) return true;
            string former = InstallShared.FormerName(appName);
            if (former != null && name.Equals(former, StringComparison.OrdinalIgnoreCase)) return true;
            try { return name.Equals(Path.GetFileNameWithoutExtension(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        static string FreeText(long bytes) => bytes < 0 ? "free space not known"
            : (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB free";

        /// <summary>The installed program's window: the service does the work; this process only shows its page. A second
        /// start brings the window to the front. When the service neither runs nor can be started, this process runs the
        /// whole program instead, as without a service (the port still lets only one of them record).</summary>
        static int RunWindowOnly(string appName, int defaultPort, Args args, Func<Context, IWatcher> makeWatcher,
                                 Func<Context, string, Dictionary<string, object>> rebuildRecord)
        {
            int port = int.TryParse(args.Value("--port"), out int p) ? p : defaultPort;
            string url = "http://127.0.0.1:" + port + "/";
            string open = args.OpenFile();             // an experiment file to show in the page
            // after a failed update the window that started it may still be closing (its event is there until its
            // process ends): this start waits until it is gone and shows the window (and the notice) itself, instead of
            // calling a window that is about to go
            if (Updater.FailedUpdate != null) Updater.GoneWithin(ShowEventName(appName), TimeSpan.FromSeconds(Updater.ClosingWindowSeconds));
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName(appName), out var existing))
                {
                    using (existing)
                    {
                        Request(appName, open);
                        AllowSetForegroundWindow(-1);  // the open window may come to the front: this start was the user's
                        existing.Set();
                    }
                    return 0;
                }
            }
            catch (UnauthorizedAccessException) { }    // the open window runs with other rights (as administrator): open one of our own

            string started = ServiceMode.EnsureRunning(appName);
            // the service needs a moment after Windows starts (or after it was started just now); one that neither runs
            // nor starts is not waited for
            var waited = Stopwatch.StartNew();
            bool answers;
            while (!(answers = WebServer.OtherInstance(port, appName) == "running") && waited.Elapsed.TotalSeconds < 30 && ServiceBusy(appName))
                Thread.Sleep(500);
            if (!answers && !ServiceBusy(appName))
                return RunCore(appName, defaultPort, args, makeWatcher, null,
                               "the Windows service " + appName + " does not run" + (started.Length > 0 ? ": " + started : "") +
                               "; this process records, as without the service", rebuildRecord);
            if (!answers)
            {
                // the service runs but cannot have its port: another program holds it (the service's log says so too)
                var owner = PortOwner.Find(port);
                if (owner != null && !IsThisProgram(owner.Value.Program, appName))
                {
                    var (en, zh) = PortOwner.Words(port, InstallShared.DisplayName(appName), owner, false);
                    MessageBox.Show(zh + "\n\n" + en, InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }

            DataLayout layout = LayoutForProgram(appName, args.Value("--data-dir"));
            Runtime.Layout = layout;
            Runtime.Edition = Installed(appName) ? "installed" : "portable";
            string userDir = UserFolder(layout, appName, out string moved);
            // a log of its own: the service's log is the service's, and two processes appending to one file overwrite
            // each other's lines
            Log.Open(Path.Combine(userDir, "window.log"));
            Log.Info("the window started" + (started.Length > 0 ? "; " + started : "") +
                     (answers ? "" : "; the service does not answer on port " + port + " yet") + moved);
            try
            {
                var window = ShowWindowLoop(appName, url, layout, userDir, "webview", true, open, recordsHere: false);
                if (window.StartFailure != null) OpenInBrowser(url);
            }
            catch (Exception error)
            {
                Log.Error("the window could not be shown: showing the page in the default browser", error);
                OpenInBrowser(url);
            }
            Log.Info("the window closed");
            Log.Close();
            return 0;
        }

        /// <summary>The folder of this Windows user's window: System\users\&lt;user&gt; when the user may write there, else
        /// %LOCALAPPDATA%\&lt;name&gt; (moved: a note for the log).</summary>
        static string UserFolder(DataLayout layout, string appName, out string moved)
        {
            moved = "";
            string userDir = layout.UserFolder();
            if (CanWrite(userDir)) return userDir;
            string own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
            moved = "; " + userDir + " cannot be written: the window's files are kept in " + own;
            try { Directory.CreateDirectory(own); } catch (Exception) { }
            return own;
        }

        static bool ServiceBusy(string appName)
        {
            var status = ServiceMode.Status(appName);
            return status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending;
        }

        /// <summary>True when this user can make the folder and write in it.</summary>
        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        static void OpenInBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }

        static void ShowWindow(string appName, string url, DataLayout layout, Service service, string open)
        {
            string userDir = UserFolder(layout, appName, out string moved);
            if (moved.Length > 0) Log.Info(moved.TrimStart(';', ' '));
            var window = ShowWindowLoop(appName, url, layout, userDir, "webview", true, open, recordsHere: true);
            if (window.StartFailure != null)
            {
                // no web control: the page in the default browser, and the program ends when the page has stopped asking
                OpenInBrowser(url);
                service.Touch();
                while (service.SecondsSinceRequest < 90) Thread.Sleep(5000);
            }
        }

        /// <summary>Where the window's profile may be carried over from (the first that is there): the profile the window
        /// of an earlier version had (System\old-cache\webview), the Edge profile of the Python programs (browser); and the
        /// same not moved yet.</summary>
        static List<string> CarryOverSources(DataLayout layout) => new List<string>
        {
            Path.Combine(layout.OldCache, "webview", "EBWebView"), Path.Combine(layout.Root, "webview", "EBWebView"),
            Path.Combine(layout.OldCache, "browser"), Path.Combine(layout.Root, "browser"),
        };

        /// <summary>Show the window until it is closed; meanwhile a second start of the program brings it to the front and
        /// hands it the experiment file it was started with. open: an experiment file to show once the page is there;
        /// recordsHere: this process records (closing the window stops it: the user is asked first).</summary>
        static MainWindow ShowWindowLoop(string appName, string url, DataLayout layout, string userDir, string profile, bool carryOver,
                                         string open, bool recordsHere)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var window = new MainWindow(appName, url, userDir, profile, carryOver ? CarryOverSources(layout) : null,
                                        Path.Combine(layout.SystemDir, profile + "-window.json"))
            {
                ExperimentsFolder = layout.Experiments, RecordsHere = recordsHere, CurrentVersion = AnalysisRunner.ProgramVersion(),
            };
            EventWaitHandle show = null;
            try { show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(appName)); }
            catch (UnauthorizedAccessException) { }    // made by a window with other rights (as administrator): this one cannot be called to the front
            // files second starts left before this window could be signalled, then the one of this start
            if (show != null) window.Open(TakeRequests(appName));
            if (open != null) window.Open(new[] { open });
            using (show)
            {
                if (show != null)
                {
                    var listener = new Thread(() =>
                    {
                        try
                        {
                            while (show.WaitOne())
                                if (window.IsHandleCreated)
                                    window.BeginInvoke(new Action(() =>
                                    {
                                        window.ShowToFront();
                                        window.Open(TakeRequests(appName));
                                    }));
                        }
                        catch (Exception) { }
                    }) { IsBackground = true, Name = "show" };
                    listener.Start();
                }
                Application.Run(window);
            }
            return window;
        }

        static string ShowEventName(string appName) => "Local\\" + appName + "-show-window";

        /// <summary>A second start of the whole program: an experiment file it was started with goes to the running program
        /// (POST /api/open: its page opens it), then that instance's window comes to the front. An instance without a
        /// window of its own (--no-window, or the old Python program) gets a window on its page from this process, as the
        /// Python programs opened another Edge window on the running one.</summary>
        static void AskToShow(string appName, string url, string dataDir, string open, int port, Args args)
        {
            if (open != null && HandOver(port, LayoutOf(dataDir, args, appName), open)) open = null;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName(appName), out var show))
                {
                    using (show)
                    {
                        Request(appName, open);
                        AllowSetForegroundWindow(-1);
                        show.Set();
                    }
                    return;
                }
            }
            catch (UnauthorizedAccessException) { }    // that window runs with other rights (as administrator): open one of our own
            try
            {
                DataLayout layout = LayoutOf(dataDir, args, appName);
                string userDir = UserFolder(layout, appName, out _);
                var viewer = ShowWindowLoop(appName, url, layout, userDir, "webview-viewer", false, open, recordsHere: false);
                if (viewer.StartFailure == null) return;
            }
            catch (Exception) { }
            // "... is already running (the previous version)."
            MessageBox.Show(InstallShared.DisplayName(appName) + " " + Text(0x5df2, 0x7ecf, 0x5728, 0x8fd0, 0x884c, 0xff08, 0x65e7, 0x7248, 0x672c, 0xff09, 0x3002)
                            + "\n" + InstallShared.DisplayName(appName) + " is already running (the previous version).", InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>POST /api/open {"path"} to the running program (the token from its data folder); true when it took it.</summary>
        static bool HandOver(int port, DataLayout layout, string file)
        {
            string token = null;
            foreach (string path in new[] { layout.Token, Path.Combine(layout.Root, "api-token") })
            {
                try
                {
                    string text = File.ReadAllText(path).Trim();
                    if (Service.IsToken(text)) { token = text; break; }
                }
                catch (Exception) { }
            }
            if (token == null) return false;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/open");
                request.Method = "POST";
                request.Proxy = null;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.ContentType = "application/json";
                request.Headers["X-SAA-Token"] = token;
                byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["path"] = file }, ensureAscii: false));
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (var response = (HttpWebResponse)request.GetResponse()) return response.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception) { return false; }
        }
    }
}
