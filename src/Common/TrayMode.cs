// The tray of the installed program: "<exe> --tray", started at every sign-in for every user of the PC (the installer's
// entry in HKLM\...\Run), one per user session. It shows a Windows notification (a toast on Windows 10 and 11), with a
// sound when the settings say so, for each new event the service reports in /api/state "events" - a run completed,
// stopped early, was interrupted, is stuck, could not be saved - of the kinds the settings list (GET /api/settings:
// notify.sound, notify.kinds); and one when the service has not answered for 3 minutes (nothing records then). Its menu
// opens the window and the data folder; a click on the icon or a notification opens the window. It only reads the local
// API, with the token the service keeps in System\api-token (the PC's users may read it), and is never needed for
// recording.
//
// It follows the program: when an update replaces the program file, or the service runs the program from another
// folder (a move), it starts that program's tray and ends; an uninstall tells it to end (the Global event
// InstallShared.TrayQuitEvent, looked for every 2 s). Which events it has shown is kept per user (System\users\<user>\
// tray.json, else %LOCALAPPDATA%\<name>\tray.json), so that a sign-in shows what happened since (up to 12 hours back)
// and nothing twice.
//
// Entry: TrayMode.Run(appName, port, dataDir); Host dispatches "--tray" to it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace SAAControl
{
    public static class TrayMode
    {
        /// <summary>Runs the tray until it is told to end (Quit, an uninstall, a newer program file taking over). One per
        /// user session: a second start ends at once. dataDir: the program's data folder (null: the one beside it).</summary>
        public static int Run(string appName, int port, string dataDir)
        {
            string exe = Application.ExecutablePath;
            if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) dataDir = InstallShared.DataFolderIn(Path.GetDirectoryName(exe));
            Mutex single = null;
            bool mine = false;
            try
            {
                single = new Mutex(false, @"Local\" + appName + "-tray");
                // a tray handing over to a newer program file ends within moments: wait for it
                try { mine = single.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine) return 0;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                }
                catch (InvalidOperationException) { }
                using (var tray = new Tray(appName, port, dataDir, exe))
                    Application.Run(tray);
                return 0;
            }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("the tray failed: " + error.Message); } catch (Exception) { }
                return 1;
            }
            finally
            {
                if (mine) try { single.ReleaseMutex(); } catch (Exception) { }
                single?.Dispose();
            }
        }
    }

    /// <summary>The tray icon, its menu, its notifications and the looks at the service.</summary>
    sealed class Tray : ApplicationContext
    {
        const int LookEvery = 5;                  // ticks of 2 s: the service is asked every 10 s
        static readonly TimeSpan Recent = TimeSpan.FromHours(12), SilentFor = TimeSpan.FromMinutes(3), SettingsEvery = TimeSpan.FromSeconds(60);

        readonly string appName, shown, exe, dataDir;
        readonly int port;
        readonly bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        readonly NotifyIcon icon;
        readonly Control invoker = new Control();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly TrayMemory memory;
        readonly string startIdentity;
        int ticks, missingLooks;
        string replacedBy;                         // the program file's identity after a replacement, until it is settled
        volatile bool looking;
        bool firstAnswer = true, silenceShown, ending;
        DateTime lastAnswer = DateTime.UtcNow, settingsAt = DateTime.MinValue;
        Dictionary<string, object> settings;

        public Tray(string appName, int port, string dataDir, string exe)
        {
            this.appName = appName;
            this.port = port;
            this.dataDir = dataDir;
            this.exe = exe;
            shown = InstallShared.DisplayName(appName);
            memory = TrayMemory.Load(dataDir, appName);
            startIdentity = FileIdentity(exe);
            invoker.CreateControl();

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem(Say("\u6253\u5f00\u7a97\u53e3", "Open window"), null, (s, e) => OpenWindow());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripMenuItem(Say("\u6253\u5f00\u6570\u636e\u6587\u4ef6\u5939", "Open data folder"), null, (s, e) => OpenDataFolder()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Say("\u9000\u51fa", "Quit"), null, (s, e) => End()));
            icon = new NotifyIcon { Icon = LoadIcon(exe), Text = Clip(shown, 63), ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };
            icon.BalloonTipClicked += (s, e) => OpenWindow();

            timer.Tick += (s, e) => Tick();
            timer.Start();
            Look();
        }

        string Say(string zh, string en) => chinese ? zh : en;

        void Tick()
        {
            if (ending) return;
            try
            {
                if (EventWaitHandle.TryOpenExisting(InstallShared.TrayQuitEvent(appName), EventWaitHandleRights.Synchronize, out EventWaitHandle quit))
                    using (quit)
                        if (quit.WaitOne(0))
                        {
                            End();
                            return;
                        }
            }
            catch (Exception) { }
            if (++ticks % LookEvery != 0) return;
            Follow();
            if (!ending) Look();
        }

        // ---- following the program: an update replaces its file, a move puts it into another folder ----

        void Follow()
        {
            try
            {
                string target = InstallShared.ServiceExe(appName);
                if (target != null && File.Exists(target) && !InstallShared.SameFile(target, exe) && ServiceSettled())
                {
                    HandOver(target);                   // the service runs the program from another folder now
                    return;
                }
                if (!File.Exists(exe))
                {
                    if (++missingLooks >= 3 && target == null) End();       // uninstalled
                    return;
                }
                missingLooks = 0;
                string now = FileIdentity(exe);
                if (now == null || now == startIdentity)
                {
                    replacedBy = null;                  // the same file (an update that failed put it back)
                    return;
                }
                if (now != replacedBy)
                {
                    replacedBy = now;                   // replaced just now: once more, in case the update is still going on
                    return;
                }
                if (ServiceSettled() && InstallShared.IsManagedExe(exe)) HandOver(exe);
            }
            catch (Exception) { }
        }

        /// <summary>The service is not being changed: it runs, or there is none.</summary>
        bool ServiceSettled()
        {
            try
            {
                using (var sc = new ServiceController(appName)) return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception) { return true; }
        }

        void HandOver(string program)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(program, InstallShared.TrayOption) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(program) })) { }
            }
            catch (Exception) { return; }              // this one stays
            End();
        }

        // ---- the looks at the service (on a worker thread; the results come back to this one) ----

        void Look()
        {
            if (looking || ending) return;
            looking = true;
            bool wantSettings = DateTime.UtcNow - settingsAt > SettingsEvery;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> state = null, newSettings = null;
                try
                {
                    string token = ReadToken(dataDir);
                    state = Get("/api/state", token) as Dictionary<string, object>;
                    if (state != null && wantSettings) newSettings = Get("/api/settings", token) as Dictionary<string, object>;
                }
                catch (Exception) { }
                try { invoker.BeginInvoke(new Action(() => Answered(state, newSettings))); }
                catch (Exception) { looking = false; }
            });
        }

        object Get(string path, string token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
                request.Proxy = null;
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                if (token != null) request.Headers["X-SAA-Token"] = token;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return Json.Parse(reader.ReadToEnd());
            }
            catch (Exception) { return null; }
        }

        static string ReadToken(string dataDir)
        {
            try
            {
                string file = InstallShared.TokenFile(dataDir);
                return file == null ? null : File.ReadAllText(file).Trim();
            }
            catch (Exception) { return null; }
        }

        void Answered(Dictionary<string, object> state, Dictionary<string, object> newSettings)
        {
            looking = false;
            if (ending) return;
            if (newSettings != null)
            {
                settings = newSettings;
                settingsAt = DateTime.UtcNow;
            }
            if (state == null)
            {
                if (!silenceShown && DateTime.UtcNow - lastAnswer >= SilentFor)
                {
                    silenceShown = true;
                    if (Wants("stuck"))
                        Notify(shown + ": " + Say("\u540e\u53f0\u670d\u52a1\u6ca1\u6709\u56de\u5e94", "the service does not answer"),
                               Say("\u5df2\u7ecf 3 \u5206\u949f\u6ca1\u6709\u56de\u5e94\uff0c\u73b0\u5728\u53ef\u80fd\u6ca1\u6709\u5728\u8bb0\u5f55\u3002\u8bf7\u6253\u5f00\u7a97\u53e3\u67e5\u770b\uff0c\u6216\u8005\u91cd\u65b0\u542f\u52a8\u7535\u8111\u3002",
                                   "It has not answered for 3 minutes and may not be recording. Open the window to check, or restart the PC."), true);
                }
                SetTip(Say("\u540e\u53f0\u670d\u52a1\u6ca1\u6709\u56de\u5e94", "The service does not answer"));
                return;
            }
            lastAnswer = DateTime.UtcNow;
            silenceShown = false;

            var fresh = new List<Dictionary<string, object>>();
            if (state.TryGetValue("events", out object list) && list is List<object> events)
                foreach (Dictionary<string, object> item in events.OfType<Dictionary<string, object>>())
                {
                    string key = EventKey(item);
                    if (memory.Contains(key)) continue;
                    memory.Add(key);
                    if (!IsRecent(Text(item, "at"))) continue;      // shown long ago, or before this user's last sign-in window
                    if (Wants(Text(item, "kind"))) fresh.Add(item);
                }
            if (fresh.Count > 0) Show(fresh);
            if (firstAnswer || fresh.Count > 0) memory.Save();
            firstAnswer = false;
            SetTip(Summary(state));
        }

        /// <summary>What makes an event the same event across restarts of the service (whose ids may start again).</summary>
        static string EventKey(Dictionary<string, object> item) =>
            Text(item, "at") + "|" + Text(item, "kind") + "|" + Text(item, "instrument") + "|" + Text(item, "runId") + "|" + Text(item, "id");

        static bool IsRecent(string at)
        {
            if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset when)) return true;
            return DateTimeOffset.Now - when <= Recent;
        }

        static string Text(Dictionary<string, object> item, string name) =>
            item != null && item.TryGetValue(name, out object value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";

        Dictionary<string, object> Notifying() =>
            settings != null && settings.TryGetValue("notify", out object n) ? n as Dictionary<string, object> : null;

        /// <summary>Whether the settings want notifications of this kind (all kinds while they are not known).</summary>
        bool Wants(string kind)
        {
            var notify = Notifying();
            if (notify == null || !notify.TryGetValue("kinds", out object kinds) || !(kinds is List<object> wanted)) return true;
            return wanted.Any(k => string.Equals(Convert.ToString(k, CultureInfo.InvariantCulture), kind, StringComparison.OrdinalIgnoreCase));
        }

        bool WantsSound()
        {
            var notify = Notifying();
            return notify == null || !notify.TryGetValue("sound", out object sound) || !(sound is bool on) || on;
        }

        string KindText(string kind)
        {
            switch (kind)
            {
                case "completed": return Say("\u5df2\u5b8c\u6210", "completed");
                case "stopped": return Say("\u63d0\u524d\u505c\u6b62", "stopped early");
                case "interrupted": return Say("\u4e2d\u65ad", "interrupted");
                case "stuck": return Say("\u957f\u65f6\u95f4\u6ca1\u6709\u65b0\u6570\u636e", "no new data");
                case "save-failed": return Say("\u6ca1\u80fd\u4fdd\u5b58", "could not be saved");
                default: return kind;
            }
        }

        string InstrumentName(string key)
        {
            try
            {
                if (settings != null && settings.TryGetValue("instruments", out object all) && all is Dictionary<string, object> instruments &&
                    instruments.TryGetValue(key, out object one) && one is Dictionary<string, object> instrument)
                {
                    string name = Text(instrument, "displayName");
                    if (name.Length > 0) return name;
                }
            }
            catch (Exception) { }
            return key;
        }

        string Title(Dictionary<string, object> item)
        {
            string instrument = Text(item, "instrument");
            return (instrument.Length > 0 ? InstrumentName(instrument) : shown) + ": " + KindText(Text(item, "kind"));
        }

        string Message(Dictionary<string, object> item)
        {
            string message = chinese ? Text(item, "messageZh") : "";
            if (message.Length == 0) message = Text(item, "message");
            string name = Text(item, "name");
            if (name.Length > 0 && message.IndexOf(name, StringComparison.Ordinal) < 0) message = name + (message.Length > 0 ? "\n" + message : "");
            return message;
        }

        void Show(List<Dictionary<string, object>> fresh)
        {
            bool problem = fresh.Any(e => Text(e, "kind") != "completed");
            if (fresh.Count == 1) Notify(Title(fresh[0]), Message(fresh[0]), problem);
            else
                Notify(shown + ": " + Say(fresh.Count + " \u6761\u65b0\u6d88\u606f", fresh.Count + " new notifications"),
                       string.Join("\n", fresh.Select(e => Title(e) + (Text(e, "name").Length > 0 ? " - " + Text(e, "name") : ""))), problem);
        }

        void Notify(string title, string text, bool problem)
        {
            if (!SilentBalloon(title, text, problem))
                try { icon.ShowBalloonTip(15000, Clip(title, 63), Clip(text.Length > 0 ? text : " ", 255), problem ? ToolTipIcon.Warning : ToolTipIcon.Info); }
                catch (Exception) { }
            if (WantsSound())
                try { (problem ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play(); } catch (Exception) { }
        }

        string Summary(Dictionary<string, object> state)
        {
            var measuring = new List<string>();
            if (state.TryGetValue("instruments", out object list) && list is List<object> instruments)
                foreach (Dictionary<string, object> instrument in instruments.OfType<Dictionary<string, object>>())
                    if (Text(instrument, "state") == "measuring") measuring.Add(InstrumentName(Text(instrument, "key")));
            string problem = "";
            if (state.TryGetValue("problems", out object p) && p is List<object> problems)
            {
                var worst = problems.OfType<Dictionary<string, object>>().FirstOrDefault(x => Text(x, "severity") == "error");
                if (worst != null) problem = chinese && Text(worst, "messageZh").Length > 0 ? Text(worst, "messageZh") : Text(worst, "message");
            }
            if (problem.Length > 0) return "! " + problem;
            return measuring.Count > 0 ? Say("\u6b63\u5728\u6d4b\u91cf\uff1a", "Measuring: ") + string.Join(", ", measuring)
                                       : Say("\u6ca1\u6709\u6b63\u5728\u6d4b\u91cf\u7684\u5b9e\u9a8c", "Nothing is measuring");
        }

        void SetTip(string status)
        {
            try { icon.Text = Clip(shown + "\n" + status, 63); } catch (Exception) { }
        }

        static string Clip(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "\u2026";

        // ---- the menu ----

        void OpenWindow()
        {
            try { using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) })) { } }
            catch (Exception) { }
        }

        void OpenDataFolder()
        {
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                using (Process.Start(new ProcessStartInfo(explorer, "\"" + dataDir + "\"") { UseShellExecute = false })) { }
            }
            catch (Exception) { }
        }

        void End()
        {
            if (ending) return;
            ending = true;
            timer.Stop();
            try
            {
                icon.Visible = false;
                icon.Dispose();
            }
            catch (Exception) { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                try { icon.Dispose(); } catch (Exception) { }
                invoker.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- a notification without Windows' own sound (the tray plays its own, as the settings say) ----

        const int NIM_MODIFY = 1, NIF_INFO = 0x10, NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_NOSOUND = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NotifyIconData
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

        /// <summary>The notification through the icon's own window (the one WinForms made), with NIIF_NOSOUND; false
        /// when that window cannot be reached (then NotifyIcon shows it, with Windows' sound).</summary>
        bool SilentBalloon(string title, string text, bool warning)
        {
            try
            {
                var window = typeof(NotifyIcon).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(icon) as NativeWindow;
                object id = typeof(NotifyIcon).GetField("id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(icon);
                if (window == null || window.Handle == IntPtr.Zero || !(id is int number)) return false;
                var data = new NotifyIconData
                {
                    cbSize = Marshal.SizeOf(typeof(NotifyIconData)),
                    hWnd = window.Handle,
                    uID = number,
                    uFlags = NIF_INFO,
                    szTip = "",
                    szInfo = Clip(text.Length > 0 ? text : " ", 255),
                    uTimeoutOrVersion = 15000,
                    szInfoTitle = Clip(title, 63),
                    dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_NOSOUND,
                };
                return Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            catch (Exception) { return false; }
        }

        // ---- the program file's identity (a replaced file is another file) and its icon ----

        [StructLayout(LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

        /// <summary>Which file is at the path now (volume and file index), or null.</summary>
        public static string FileIdentity(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    if (GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation info))
                        return info.VolumeSerialNumber.ToString("x8") + ":" + info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
            }
            catch (Exception) { }
            return null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        /// <summary>The program's icon in the size the notification area uses (sharp at any scale), else the program's.</summary>
        static Icon LoadIcon(string exe)
        {
            try
            {
                int size = SystemInformation.SmallIconSize.Width;
                var handles = new IntPtr[1];
                var ids = new int[1];
                if (PrivateExtractIcons(exe, 0, size, size, handles, ids, 1, 0) == 1 && handles[0] != IntPtr.Zero)
                    try
                    {
                        using (Icon shared = Icon.FromHandle(handles[0])) return (Icon)shared.Clone();
                    }
                    finally { DestroyIcon(handles[0]); }
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application; }
            catch (Exception) { return SystemIcons.Application; }
        }
    }

    /// <summary>Which events this user's tray has shown: their keys, newest last (at most 300), in System\users\&lt;user&gt;\
    /// tray.json where the program keeps its users' folders, else in %LOCALAPPDATA%\&lt;name&gt;\tray.json.</summary>
    sealed class TrayMemory
    {
        const int Keep = 300;
        readonly string file;
        readonly List<string> keys = new List<string>();
        readonly HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);

        TrayMemory(string file) { this.file = file; }

        public static TrayMemory Load(string dataDir, string appName)
        {
            var memory = new TrayMemory(Place(dataDir, appName));
            try
            {
                if (memory.file != null && File.Exists(memory.file) &&
                    Json.Parse(File.ReadAllText(memory.file, Encoding.UTF8)) is Dictionary<string, object> saved &&
                    saved.TryGetValue("shown", out object list) && list is List<object> shown)
                    foreach (string key in shown.OfType<string>()) memory.Add(key);
            }
            catch (Exception) { }
            return memory;
        }

        /// <summary>The user's own folder in System\users when the program keeps one for its users (made here if need
        /// be: each user may make his own), else %LOCALAPPDATA%\name.</summary>
        static string Place(string dataDir, string appName)
        {
            try
            {
                string users = Path.Combine(dataDir, InstallShared.SystemFolder, InstallShared.UsersFolder);
                // the folder the program keeps for this user (DataLayout.UserFolderName: the name as a file name, else "user")
                string user = Naming.Clean(Environment.UserName ?? "");
                if (user.Length == 0) user = "user";
                if (Directory.Exists(users) && user.Length > 0)
                {
                    string own = Path.Combine(users, user);
                    Directory.CreateDirectory(own);
                    string probe = Path.Combine(own, "tray-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                    return Path.Combine(own, "tray.json");
                }
            }
            catch (Exception) { }
            try
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
                Directory.CreateDirectory(local);
                return Path.Combine(local, "tray.json");
            }
            catch (Exception) { return null; }
        }

        public bool Contains(string key) => known.Contains(key);

        public void Add(string key)
        {
            if (!known.Add(key)) return;
            keys.Add(key);
            while (keys.Count > Keep)
            {
                known.Remove(keys[0]);
                keys.RemoveAt(0);
            }
        }

        public void Save()
        {
            if (file == null) return;
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var value = new Dictionary<string, object> { ["shown"] = keys.Cast<object>().ToList(), ["savedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) };
                File.WriteAllText(temp, Json.Serialize(value, ensureAscii: false), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            catch (Exception) { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
        }
    }
}
