// Standalone desktop runner for Plate Kinetics Analysis (and for Plate Kinetics Live when installed in Analysis-only mode).
// Hosts web\PlateKineticsAnalysis.html directly via WebView2 virtual host mapping (https://app.platekinetics.local/...) -
// zero local TCP port, zero Windows service, zero tray icon, instant launch, single-instance file handoff for .pk / .pkproj / data files.
// Pure ASCII source (Chinese texts as \u escapes).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SAAControl
{
    public static class AnalysisRunner
    {
        public const string MarkerFileName = "analysis-only.mode";

        /// <summary>True when this folder was installed in Analysis-only mode (no service, no tray, standalone analyzer).</summary>
        public static bool IsAnalysisOnlyInstall(string programDir) =>
            !string.IsNullOrEmpty(programDir) && File.Exists(Path.Combine(programDir, MarkerFileName));

        /// <summary>The program's version as its releases are tagged: three parts, four when the fourth is not 0 (so that
        /// a release tagged x.y.z.w is not offered again once it is installed).</summary>
        public static string ProgramVersion()
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v != null ? Updater.VersionText(v) : "3.18.0";
        }

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        static string UserDataFolder(string appName)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, InstallShared.DisplayName(appName));
        }

        /// <summary>Collect all file/folder paths passed on the command line (bare paths or --open values). An option
        /// ("--update-failed=3" of the installer, any other "--..." or "/...") is never a file to open.</summary>
        public static List<string> IncomingPaths(string[] argv)
        {
            var list = new List<string>();
            if (argv == null) return list;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (a.Equals("--open", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                {
                    AddPath(list, argv[++i]);
                }
                else if (a.StartsWith("--open=", StringComparison.OrdinalIgnoreCase))
                {
                    AddPath(list, a.Substring(7));
                }
                else if (!a.StartsWith("--") && !a.StartsWith("/"))
                {
                    AddPath(list, a);
                }
            }
            return list;
        }

        static void AddPath(List<string> list, string raw)
        {
            try
            {
                string clean = (raw ?? "").Trim().Trim('"');
                if (clean.Length == 0) return;
                string full = Path.GetFullPath(clean);
                if (File.Exists(full) || Directory.Exists(full)) list.Add(full);
            }
            catch (Exception) { }
        }

        /// <summary>Launch the standalone desktop analyzer window with single-instance file handoff.</summary>
        public static int Run(string appName, string[] argv)
        {
            Runtime.AppName = appName;
            Runtime.ProgramName = InstallShared.DisplayName(appName);
            Runtime.Edition = File.Exists(InstallShared.ManifestPath(Files.ProgramDir, appName)) ? "installed" : "portable";

            if (argv != null && argv.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
                return Uninstaller.Run(appName);

            string userDir = UserDataFolder(appName);
            Directory.CreateDirectory(userDir);
            Log.Open(Path.Combine(userDir, "analysis.log"));
            List<string> filesToOpen = IncomingPaths(argv);
            // the installer started the program again after a silent update that failed (--update-failed=<its exit
            // code>): the window tells the page once it is there
            Updater.FailedUpdate = Updater.UpdateFailedCode(argv);

            string mutexName = "Local\\" + appName + "-analysis-window";
            string showEventName = "Local\\" + appName + "-analysis-show";
            using (var mutex = new Mutex(true, mutexName, out bool isFirst))
            {
                // after a failed update the window that started it may still be closing: this start waits until it is
                // gone and shows the window (and the notice) itself, instead of calling a window that is about to go
                if (!isFirst && Updater.FailedUpdate != null)
                    isFirst = Updater.TakeOverFrom(mutex, TimeSpan.FromSeconds(Updater.ClosingWindowSeconds));
                if (!isFirst)
                {
                    foreach (string f in filesToOpen) LeaveRequest(appName, f);
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(showEventName))
                        {
                            try { AllowSetForegroundWindow(-1); } catch (Exception) { }
                            ev.Set();
                        }
                    }
                    catch (Exception) { }
                    return 0;
                }

                string webDir = Path.Combine(Files.ProgramDir, "web");
                string htmlFile = Path.Combine(webDir, "PlateKineticsAnalysis.html");
                if (!File.Exists(htmlFile))
                {
                    MessageBox.Show(
                        "\u627e\u4e0d\u5230\u5206\u6790\u9875\u9762\u6587\u4ef6\uff1a\n" + htmlFile + "\n\nMissing analysis page:\n" + htmlFile,
                        InstallShared.DisplayName(appName), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string virtualHost = "app.platekinetics.local";
                string pageUrl = "https://" + virtualHost + "/PlateKineticsAnalysis.html";
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName))
                using (var window = new MainWindow(appName, pageUrl, userDir))
                {
                    window.Standalone = true;
                    window.AnalysisOnly = true;
                    window.VirtualHostName = virtualHost;
                    window.VirtualHostFolder = webDir;
                    window.CurrentVersion = ProgramVersion();

                    var initial = new List<string>(filesToOpen);
                    initial.AddRange(TakeRequests(appName));
                    if (initial.Count > 0) window.OpenLocalPaths(initial);

                    var stopSignal = new ManualResetEventSlim(false);
                    var listener = new Thread(() =>
                    {
                        var handles = new WaitHandle[] { showEvent, stopSignal.WaitHandle };
                        while (WaitHandle.WaitAny(handles) == 0)
                        {
                            var incoming = TakeRequests(appName);
                            try
                            {
                                if (!window.IsDisposed && window.IsHandleCreated)
                                    window.BeginInvoke(new Action(() =>
                                    {
                                        window.ShowToFront();
                                        if (incoming.Count > 0) window.OpenLocalPaths(incoming);
                                    }));
                            }
                            catch (Exception) { }
                        }
                    }) { IsBackground = true, Name = "AnalysisShowSignal" };
                    listener.Start();

                    try
                    {
                        Application.Run(window);
                    }
                    finally
                    {
                        stopSignal.Set();
                        listener.Join(1000);
                    }
                }
                return 0;
            }
        }

        static string RequestFolder(string appName) => Path.Combine(Path.GetTempPath(), appName + "-analysis-open");

        static void LeaveRequest(string appName, string path)
        {
            try
            {
                string dir = RequestFolder(appName);
                Directory.CreateDirectory(dir);
                string name = DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
                File.WriteAllText(Path.Combine(dir, name), path, new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static List<string> TakeRequests(string appName)
        {
            var result = new List<string>();
            try
            {
                string dir = RequestFolder(appName);
                if (!Directory.Exists(dir)) return result;
                foreach (string f in Directory.GetFiles(dir, "*.txt").OrderBy(x => x))
                {
                    try
                    {
                        bool fresh = (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMinutes <= 2;
                        string content = File.ReadAllText(f, System.Text.Encoding.UTF8).Trim();
                        File.Delete(f);
                        if (fresh && content.Length > 0) result.Add(content);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return result;
        }
    }
}
