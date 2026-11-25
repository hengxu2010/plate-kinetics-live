// What the program looks after on the PC itself, only reading Windows' settings, never changing them:
//   KeepAwake  while an instrument measures (and the setting "keepAwake" is on) the PC does not go to sleep by itself:
//              the Windows service holds a power request (PowerCreateRequest + PowerSetRequest(SystemRequired), seen in
//              "powercfg /requests" with the reason "Plate Kinetics Live: Omega2 is measuring"); a program in a user's
//              session (the portable one) sets its thread's execution state (SetThreadExecutionState). Released as soon
//              as nothing measures. When Windows refuses: the problem "keep-awake-failed".
//   PcChecks   every 10 minutes: does the active power plan put the PC to sleep (or hibernate) after N minutes on mains
//              power ("pc-sleep"), is a restart pending to finish an update ("reboot-pending"), and is there space on the
//              disk the instrument software saves to (when it is not the data folder's disk, which Storage watches). The
//              same go into GET /api/health "checks".
//   PortOwner  the program that holds the program's port (GetExtendedTcpTable), named in the log and the window when
//              the program cannot start.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace SAAControl
{
    public static class KeepAwake
    {
        /// <summary>One way of keeping the PC awake: Hold (true when Windows took it; error: its error), Release.</summary>
        public interface IHold
        {
            bool Hold(string reason, out int error);
            void Release();
        }

        /// <summary>How the PC is kept awake: the service holds a power request, a program in a user's session sets its
        /// thread's execution state. Tests replace it.</summary>
        public static Func<IHold> Make = () => Runtime.AsService ? (IHold)new PowerRequest() : new ExecutionState();

        static readonly object Gate = new object();
        static IHold held;
        static string heldReason, failedReason;

        /// <summary>The reason Windows shows for the request now (null: none held).</summary>
        public static string Reason
        {
            get { lock (Gate) return heldReason; }
        }

        /// <summary>After every look of a watcher: the instruments measuring now (their keys). The PC is kept awake while
        /// one measures and the setting is on.</summary>
        public static void Update(IEnumerable<string> measuring)
        {
            List<string> keys = (measuring ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal)
                                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            bool enabled = Runtime.Settings == null || Runtime.Settings.KeepAwake;
            lock (Gate)
            {
                if (enabled && keys.Count > 0)
                {
                    string reason = Runtime.ProgramName + ": " + string.Join(", ", keys) + (keys.Count == 1 ? " is measuring" : " are measuring");
                    if (held != null && reason == heldReason) return;
                    IHold fresh;
                    int error = 0;
                    bool ok;
                    try
                    {
                        fresh = Make();
                        ok = fresh.Hold(reason, out error);
                    }
                    catch (Exception e)
                    {
                        fresh = null;
                        ok = false;
                        error = Marshal.GetHRForException(e);
                    }
                    if (ok)
                    {
                        try { held?.Release(); } catch (Exception) { }
                        bool was = held != null;
                        held = fresh;
                        heldReason = reason;
                        failedReason = null;
                        Problems.KeepAwakeActive = true;
                        Problems.Clear("keep-awake-failed");
                        if (!was) Log.Info("the PC is kept awake while measuring (" + reason + ")");
                        return;
                    }
                    try { fresh?.Release(); } catch (Exception) { }
                    if (failedReason != reason) Log.Warning("the PC could not be kept awake (Windows error " + error + "): " + reason);
                    failedReason = reason;
                    Problems.KeepAwakeActive = held != null;
                    Problems.Set("keep-awake-failed", Problems.Warning,
                        "The program could not keep the PC awake while measuring (Windows error " + error + "): the PC may go to sleep during the run. " +
                        "Ask the administrator to set sleep to \"Never\" in the power options.",
                        // "\u7a0b\u5e8f\u65e0\u6cd5\u5728\u6d4b\u91cf\u671f\u95f4\u8ba9\u7535\u8111\u4fdd\u6301\u5524\u9192\uff08Windows \u9519\u8bef <n>\uff09\uff1a\u7535\u8111\u53ef\u80fd\u5728\u5b9e\u9a8c\u4e2d\u7761\u7720\u3002\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\u3002"
                        "\u7a0b\u5e8f\u65e0\u6cd5\u5728\u6d4b\u91cf\u671f\u95f4\u8ba9\u7535\u8111\u4fdd\u6301\u5524\u9192\uff08Windows \u9519\u8bef " + error +
                        "\uff09\uff1a\u7535\u8111\u53ef\u80fd\u5728\u5b9e\u9a8c\u4e2d\u7761\u7720\u3002\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\u3002");
                    return;
                }
                failedReason = null;
                Problems.Clear("keep-awake-failed");
                if (held == null) return;
                try { held.Release(); } catch (Exception) { }
                held = null;
                heldReason = null;
                Problems.KeepAwakeActive = false;
                Log.Info(enabled ? "nothing is measuring: the PC may sleep again" : "keeping the PC awake is turned off in the settings");
            }
        }

        /// <summary>At the end of the program: the PC may sleep again.</summary>
        public static void Release()
        {
            lock (Gate)
            {
                try { held?.Release(); } catch (Exception) { }
                held = null;
                heldReason = null;
                Problems.KeepAwakeActive = false;
            }
        }

        /// <summary>A power request (the service; also works in a user's session).</summary>
        sealed class PowerRequest : IHold
        {
            IntPtr handle = IntPtr.Zero;

            public bool Hold(string reason, out int error)
            {
                var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };   // POWER_REQUEST_CONTEXT_SIMPLE_STRING
                IntPtr h = PowerCreateRequest(ref context);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                if (!PowerSetRequest(h, SystemRequired))
                {
                    error = Marshal.GetLastWin32Error();
                    CloseHandle(h);
                    return false;
                }
                handle = h;
                error = 0;
                return true;
            }

            public void Release()
            {
                if (handle == IntPtr.Zero) return;
                PowerClearRequest(handle, SystemRequired);
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        /// <summary>The execution state of a thread of its own (it holds while that thread lives).</summary>
        sealed class ExecutionState : IHold
        {
            readonly ManualResetEvent stop = new ManualResetEvent(false), set = new ManualResetEvent(false);
            Thread thread;
            volatile bool ok;
            volatile int failure;

            public bool Hold(string reason, out int error)
            {
                thread = new Thread(() =>
                {
                    ok = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
                    failure = ok ? 0 : Marshal.GetLastWin32Error();
                    set.Set();
                    if (!ok) return;
                    stop.WaitOne();
                    SetThreadExecutionState(EsContinuous);
                }) { IsBackground = true, Name = "keep-awake" };
                thread.Start();
                set.WaitOne(5000);
                error = failure;
                return ok;
            }

            public void Release()
            {
                stop.Set();
                thread?.Join(2000);
            }
        }

        const int SystemRequired = 1;                  // PowerRequestSystemRequired
        const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PowerSetRequest(IntPtr request, int type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PowerClearRequest(IntPtr request, int type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SetThreadExecutionState(uint flags);
    }

    public static class PcChecks
    {
        public static TimeSpan Every = TimeSpan.FromMinutes(10);
        public static TimeSpan FirstAfter = TimeSpan.FromSeconds(20);

        /// <summary>The active power plan as Windows has it (only read).</summary>
        public sealed class PowerPlan
        {
            public string Name = "";
            /// <summary>Seconds without use before the PC sleeps / hibernates on mains power; 0 = never.</summary>
            public int SleepSeconds, HibernateSeconds;
        }

        // what is read (tests replace these)
        public static Func<PowerPlan> ReadPlan = ActivePlan;
        /// <summary>Why a restart is pending ("Windows Update", "a component update"); null when none is.</summary>
        public static Func<string> ReadRestartPending = PendingRestart;
        public static Func<string, long> FreeBytes = FreeOf;

        static readonly object Gate = new object();
        static readonly List<(string Folder, string Instrument, string Software)> watched = new List<(string, string, string)>();
        static string dataFolder;
        static Timer timer;
        static PowerPlan plan;
        static string pending;
        static bool registered;
        static readonly HashSet<string> diskProblems = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The folder an instrument's software saves its data to (Omega's installation, Archimed's data folder):
        /// the free space of its disk is looked at too when it is not the data folder's disk.</summary>
        public static void WatchDrive(string folder, string instrument, string software)
        {
            if (string.IsNullOrEmpty(folder)) return;
            lock (Gate)
                if (!watched.Any(w => string.Equals(w.Folder, folder, StringComparison.OrdinalIgnoreCase) && w.Instrument == instrument))
                    watched.Add((folder, instrument, software));
        }

        /// <summary>Begin the checks (every 10 minutes, the first soon after the start); their items also go into GET
        /// /api/health.</summary>
        public static void Start(string dataRoot)
        {
            lock (Gate)
            {
                dataFolder = dataRoot;
                if (!registered)
                {
                    registered = true;
                    Health.Checks.Add(Checks);
                }
                if (timer != null) return;
                timer = new Timer(_ => { try { RunOnce(); } catch (Exception error) { Log.Warning("the check of the PC failed (" + error.Message + ")"); } },
                                  null, FirstAfter, Every);
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                timer?.Dispose();
                timer = null;
            }
        }

        /// <summary>One check of the PC: the problems "pc-sleep", "reboot-pending" and the instrument software's disk.</summary>
        public static void RunOnce()
        {
            PowerPlan now = null;
            try { now = ReadPlan(); } catch (Exception) { }
            string restart = null;
            try { restart = ReadRestartPending(); } catch (Exception) { }
            lock (Gate)
            {
                plan = now;
                pending = restart;
            }
            bool keepAwake = Runtime.Settings == null || Runtime.Settings.KeepAwake;
            int sleeps = SleepsAfter(now);
            if (sleeps > 0)
            {
                string minutes = (sleeps / 60).ToString(CultureInfo.InvariantCulture);
                string name = now.Name.Length > 0 ? now.Name : "?";
                string en = "This PC goes to sleep after " + minutes + " minutes without use (power plan \"" + name + "\", on mains power). " +
                            (keepAwake ? "The program keeps it awake while a run is measuring; for unattended runs ask the administrator to set sleep to \"Never\" in the power options."
                                       : "Sleep stops a run: ask the administrator to set sleep to \"Never\" in the power options, or turn on \"Keep the PC awake while measuring\" in the settings.");
                // "\u8fd9\u53f0\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6 <n> \u5206\u949f\u65e0\u4eba\u64cd\u4f5c\u5c31\u4f1a\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c<name>\u201d\uff09\u3002"
                string zh = "\u8fd9\u53f0\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6 " + minutes + " \u5206\u949f\u65e0\u4eba\u64cd\u4f5c\u5c31\u4f1a\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c" + name + "\u201d\uff09\u3002" +
                            (keepAwake
                                // "\u6d4b\u91cf\u671f\u95f4\u672c\u7a0b\u5e8f\u4f1a\u8ba9\u5b83\u4fdd\u6301\u5524\u9192\uff1b\u65e0\u4eba\u503c\u5b88\u7684\u957f\u5b9e\u9a8c\uff0c\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\u3002"
                                ? "\u6d4b\u91cf\u671f\u95f4\u672c\u7a0b\u5e8f\u4f1a\u8ba9\u5b83\u4fdd\u6301\u5524\u9192\uff1b\u65e0\u4eba\u503c\u5b88\u7684\u957f\u5b9e\u9a8c\uff0c\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\u3002"
                                // "\u7761\u7720\u4f1a\u4e2d\u65ad\u5b9e\u9a8c\uff1a\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\uff0c\u6216\u5728\u8bbe\u7f6e\u91cc\u6253\u5f00\u201c\u6d4b\u91cf\u65f6\u4fdd\u6301\u7535\u8111\u5524\u9192\u201d\u3002"
                                : "\u7761\u7720\u4f1a\u4e2d\u65ad\u5b9e\u9a8c\uff1a\u8bf7\u8ba9\u7ba1\u7406\u5458\u5728\u201c\u7535\u6e90\u9009\u9879\u201d\u91cc\u628a\u7761\u7720\u8bbe\u4e3a\u201c\u4ece\u4e0d\u201d\uff0c\u6216\u5728\u8bbe\u7f6e\u91cc\u6253\u5f00\u201c\u6d4b\u91cf\u65f6\u4fdd\u6301\u7535\u8111\u5524\u9192\u201d\u3002");
                Problems.Set("pc-sleep", keepAwake ? Problems.Info : Problems.Warning, en, zh, null, dismissible: true);
            }
            else Problems.Clear("pc-sleep");
            if (restart != null)
                Problems.Set("reboot-pending", Problems.Warning,
                    "Windows is waiting to restart to finish an update (" + restart + "): a long run started now may be interrupted by that restart. Restart the PC before starting a long run.",
                    // "Windows \u6b63\u5728\u7b49\u5f85\u91cd\u542f\u4ee5\u5b8c\u6210\u66f4\u65b0\uff1a\u73b0\u5728\u5f00\u59cb\u7684\u957f\u5b9e\u9a8c\u53ef\u80fd\u88ab\u91cd\u542f\u6253\u65ad\uff0c\u5efa\u8bae\u5148\u91cd\u542f\u7535\u8111\u3002"
                    "Windows \u6b63\u5728\u7b49\u5f85\u91cd\u542f\u4ee5\u5b8c\u6210\u66f4\u65b0\uff08" + restart + "\uff09\uff1a\u73b0\u5728\u5f00\u59cb\u7684\u957f\u5b9e\u9a8c\u53ef\u80fd\u88ab\u91cd\u542f\u6253\u65ad\uff0c\u5efa\u8bae\u5148\u91cd\u542f\u7535\u8111\u3002");
            else Problems.Clear("reboot-pending");
            CheckDisks();
        }

        /// <summary>Seconds after which the PC sleeps or hibernates by itself on mains power (the sooner); 0: never.</summary>
        static int SleepsAfter(PowerPlan p)
        {
            if (p == null) return 0;
            int a = p.SleepSeconds > 0 ? p.SleepSeconds : int.MaxValue, b = p.HibernateSeconds > 0 ? p.HibernateSeconds : int.MaxValue;
            int sooner = Math.Min(a, b);
            return sooner == int.MaxValue ? 0 : sooner;
        }

        /// <summary>The instrument software's disk, when it is not the data folder's: "disk-low" / "disk-critical" for the
        /// first instrument on that disk (the data folder's own disk is Storage's).</summary>
        static void CheckDisks()
        {
            List<(string Folder, string Instrument, string Software)> list;
            string data;
            lock (Gate)
            {
                list = watched.ToList();
                data = dataFolder;
            }
            string dataDrive = DriveOf(data);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in list)
            {
                string drive = DriveOf(w.Folder);
                if (drive == null || string.Equals(drive, dataDrive, StringComparison.OrdinalIgnoreCase) || !seen.Add(drive)) continue;
                long free = -1;
                try { free = FreeBytes(drive); } catch (Exception) { }
                if (free < 0 || free >= StorageStatus.WarnSpace) continue;
                bool critical = free < StorageStatus.LowSpace;
                string gb = (free / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);
                string code = critical ? "disk-critical" : "disk-low";
                Problems.Clear(critical ? "disk-low" : "disk-critical", w.Instrument);
                Problems.Set(code, critical ? Problems.Error : Problems.Warning,
                    "Only " + gb + " GB free on " + drive.TrimEnd('\\') + ", where " + w.Software + " saves its data. When the disk is full, the run's data cannot be saved.",
                    // "<drive> \u53ea\u5269 <n> GB\uff0c<software> \u7684\u6570\u636e\u5b58\u5728\u8fd9\u4e2a\u76d8\u4e0a\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u5b9e\u9a8c\u6570\u636e\u65e0\u6cd5\u4fdd\u5b58\u3002"
                    drive.TrimEnd('\\') + " \u53ea\u5269 " + gb + " GB\uff0c" + w.Software + " \u7684\u6570\u636e\u5b58\u5728\u8fd9\u4e2a\u76d8\u4e0a\u3002\u78c1\u76d8\u6ee1\u4e86\u4ee5\u540e\uff0c\u5b9e\u9a8c\u6570\u636e\u65e0\u6cd5\u4fdd\u5b58\u3002",
                    w.Instrument);
                shown.Add(w.Instrument ?? "");
            }
            lock (Gate)
            {
                foreach (string gone in diskProblems.Where(k => !shown.Contains(k)).ToList())
                {
                    Problems.Clear("disk-low", gone.Length == 0 ? null : gone);
                    Problems.Clear("disk-critical", gone.Length == 0 ? null : gone);
                }
                diskProblems.Clear();
                diskProblems.UnionWith(shown);
            }
        }

        static string DriveOf(string folder)
        {
            try { return string.IsNullOrEmpty(folder) ? null : Path.GetPathRoot(Path.GetFullPath(folder)); }
            catch (Exception) { return null; }
        }

        static long FreeOf(string root)
        {
            try { return new DriveInfo(root).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }

        /// <summary>The PC's items of GET /api/health: sleep, a pending restart, keeping awake, the recorder's looks.</summary>
        public static IEnumerable<Dictionary<string, object>> Checks()
        {
            PowerPlan p;
            string restart;
            lock (Gate)
            {
                p = plan;
                restart = pending;
            }
            if (p == null)
            {
                try { p = ReadPlan(); } catch (Exception) { }
            }
            var items = new List<Dictionary<string, object>>();
            if (p != null)
            {
                int sleeps = SleepsAfter(p);
                string name = p.Name.Length > 0 ? p.Name : "?";
                items.Add(Health.Check("sleep", sleeps == 0,
                    sleeps == 0 ? "The PC does not go to sleep by itself on mains power (power plan \"" + name + "\")."
                                : "The PC goes to sleep after " + sleeps / 60 + " minutes without use (power plan \"" + name + "\", on mains power).",
                    // "\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6\u4e0d\u4f1a\u81ea\u52a8\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c<name>\u201d\uff09\u3002" / "\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6 <n> \u5206\u949f\u65e0\u4eba\u64cd\u4f5c\u5c31\u4f1a\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c<name>\u201d\uff09\u3002"
                    sleeps == 0 ? "\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6\u4e0d\u4f1a\u81ea\u52a8\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c" + name + "\u201d\uff09\u3002"
                                : "\u7535\u8111\u63d2\u7740\u7535\u6e90\u65f6 " + sleeps / 60 + " \u5206\u949f\u65e0\u4eba\u64cd\u4f5c\u5c31\u4f1a\u7761\u7720\uff08\u7535\u6e90\u8ba1\u5212\u201c" + name + "\u201d\uff09\u3002"));
            }
            if (restart == null)
            {
                try { restart = ReadRestartPending(); } catch (Exception) { }
            }
            items.Add(Health.Check("restart-pending", restart == null,
                restart == null ? "No restart is pending." : "Windows is waiting to restart to finish an update (" + restart + ").",
                // "\u6ca1\u6709\u7b49\u5f85\u4e2d\u7684\u91cd\u542f\u3002" / "Windows \u6b63\u5728\u7b49\u5f85\u91cd\u542f\u4ee5\u5b8c\u6210\u66f4\u65b0\uff08...\uff09\u3002"
                restart == null ? "\u6ca1\u6709\u7b49\u5f85\u4e2d\u7684\u91cd\u542f\u3002" : "Windows \u6b63\u5728\u7b49\u5f85\u91cd\u542f\u4ee5\u5b8c\u6210\u66f4\u65b0\uff08" + restart + "\uff09\u3002"));
            bool keepAwake = Runtime.Settings == null || Runtime.Settings.KeepAwake;
            bool active = Problems.KeepAwakeActive;
            items.Add(Health.Check("keep-awake", keepAwake && !Problems.Has("keep-awake-failed"),
                !keepAwake ? "Keeping the PC awake while measuring is turned off in the settings."
                : Problems.Has("keep-awake-failed") ? "The PC could not be kept awake while measuring."
                : active ? "The PC is kept awake now: a run is measuring." : "The PC is kept awake while a run is measuring (none is now).",
                // "\u8bbe\u7f6e\u91cc\u5173\u95ed\u4e86\u201c\u6d4b\u91cf\u65f6\u4fdd\u6301\u7535\u8111\u5524\u9192\u201d\u3002" / "\u6d4b\u91cf\u671f\u95f4\u672a\u80fd\u8ba9\u7535\u8111\u4fdd\u6301\u5524\u9192\u3002" /
                // "\u73b0\u5728\u6709\u5b9e\u9a8c\u5728\u6d4b\u91cf\uff0c\u7535\u8111\u4fdd\u6301\u5524\u9192\u3002" / "\u6709\u5b9e\u9a8c\u6d4b\u91cf\u65f6\u7535\u8111\u4fdd\u6301\u5524\u9192\uff08\u73b0\u5728\u6ca1\u6709\uff09\u3002"
                !keepAwake ? "\u8bbe\u7f6e\u91cc\u5173\u95ed\u4e86\u201c\u6d4b\u91cf\u65f6\u4fdd\u6301\u7535\u8111\u5524\u9192\u201d\u3002"
                : Problems.Has("keep-awake-failed") ? "\u6d4b\u91cf\u671f\u95f4\u672a\u80fd\u8ba9\u7535\u8111\u4fdd\u6301\u5524\u9192\u3002"
                : active ? "\u73b0\u5728\u6709\u5b9e\u9a8c\u5728\u6d4b\u91cf\uff0c\u7535\u8111\u4fdd\u6301\u5524\u9192\u3002" : "\u6709\u5b9e\u9a8c\u6d4b\u91cf\u65f6\u7535\u8111\u4fdd\u6301\u5524\u9192\uff08\u73b0\u5728\u6ca1\u6709\uff09\u3002"));
            int ago = (int)Watchdog.SinceLook.TotalSeconds;
            bool looking = Watchdog.SinceLook < Watchdog.StuckAfter;
            items.Add(Health.Check("recorder", looking,
                "The recorder last looked at the instruments " + ago + " s ago.",
                // "\u8bb0\u5f55\u7a0b\u5e8f <n> \u79d2\u524d\u770b\u8fc7\u4eea\u5668\u3002"
                "\u8bb0\u5f55\u7a0b\u5e8f " + ago + " \u79d2\u524d\u770b\u8fc7\u4eea\u5668\u3002"));
            return items;
        }

        // ---- Windows' settings, only read

        static readonly Guid SleepGroup = new Guid("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
        static readonly Guid StandbyTimeout = new Guid("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
        static readonly Guid HibernateTimeout = new Guid("9D7815A6-7EE4-497E-8888-515A05F02364");

        /// <summary>The active power plan's sleep and hibernate times on mains power; null when they cannot be read.</summary>
        static PowerPlan ActivePlan()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr active) != 0 || active == IntPtr.Zero) return null;
            try
            {
                Guid scheme = (Guid)Marshal.PtrToStructure(active, typeof(Guid));
                var result = new PowerPlan();
                Guid group = SleepGroup, standby = StandbyTimeout, hibernate = HibernateTimeout;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref standby, out uint sleep) == 0) result.SleepSeconds = (int)Math.Min(sleep, int.MaxValue);
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref hibernate, out uint hib) == 0) result.HibernateSeconds = (int)Math.Min(hib, int.MaxValue);
                uint size = 0;
                uint asked = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if ((asked == 0 || asked == 234) && size > 0 && size < 4096)          // (234: ERROR_MORE_DATA, the size is given)
                {
                    IntPtr buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0)
                            result.Name = (Marshal.PtrToStringUni(buffer) ?? "").Trim();
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (result.Name.Length == 0) result.Name = RegistryName(scheme);
                return result;
            }
            finally { LocalFree(active); }
        }

        /// <summary>The plan's name as the registry keeps it ("@%SystemRoot%\system32\powrprof.dll,-15,Balanced"): the
        /// text in Windows' language, else the name after the last comma.</summary>
        static string RegistryName(Guid scheme)
        {
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme.ToString("D")))
                {
                    if (!(key?.GetValue("FriendlyName") is string name) || name.Trim().Length == 0) return "";
                    name = name.Trim();
                    if (!name.StartsWith("@", StringComparison.Ordinal)) return name;
                    var parts = name.Split(',');
                    if (parts.Length >= 2)
                    {
                        var text = new System.Text.StringBuilder(512);
                        if (SHLoadIndirectString(parts[0] + "," + parts[1], text, text.Capacity, IntPtr.Zero) == 0 && text.Length > 0) return text.ToString();
                    }
                    return parts.Length >= 3 ? parts[parts.Length - 1].Trim() : "";
                }
            }
            catch (Exception) { return ""; }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int size, IntPtr reserved);

        /// <summary>A restart Windows waits for to finish an update (the registry's marks of it); null: none.</summary>
        static string PendingRestart()
        {
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                    if (key != null) return "Windows Update";
                using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                    if (key != null) return "Windows component servicing";
            }
            return null;
        }

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);
    }

    /// <summary>Who holds a TCP port (GetExtendedTcpTable): named when the program cannot start on its port.</summary>
    public static class PortOwner
    {
        /// <summary>The process listening on the port (IPv4 or IPv6, any address): its id and program file name
        /// ("xyz.exe"; "System" for Windows itself); null when none is found.</summary>
        public static (int Pid, string Program)? Find(int port)
        {
            foreach (int family in new[] { 2, 23 })                     // AF_INET, AF_INET6
            {
                int pid = Listener(port, family);
                if (pid >= 0) return (pid, ProgramOf(pid));
            }
            return null;
        }

        /// <summary>The words for a port the program cannot use: (en, zh). denied: Windows refused the port itself (it is in
        /// a range Windows keeps for other uses).</summary>
        public static (string En, string Zh) Words(int port, string programName, (int Pid, string Program)? owner, bool denied)
        {
            if (owner != null)
            {
                string who = owner.Value.Program + " (" + (owner.Value.Pid == 4 ? "Windows" : "process " + owner.Value.Pid) + ")";
                return ("Port " + port + " is used by " + who + ": " + programName + " cannot start. Close that program, or ask the administrator to move it to another port.",
                        // "\u7aef\u53e3 <port> \u88ab <who> \u5360\u7528\uff0c<program> \u65e0\u6cd5\u542f\u52a8\u3002\u8bf7\u5173\u95ed\u90a3\u4e2a\u7a0b\u5e8f\uff0c\u6216\u8bf7\u7ba1\u7406\u5458\u628a\u5b83\u6362\u5230\u522b\u7684\u7aef\u53e3\u3002"
                        "\u7aef\u53e3 " + port + " \u88ab " + who + " \u5360\u7528\uff0c" + programName + " \u65e0\u6cd5\u542f\u52a8\u3002\u8bf7\u5173\u95ed\u90a3\u4e2a\u7a0b\u5e8f\uff0c\u6216\u8bf7\u7ba1\u7406\u5458\u628a\u5b83\u6362\u5230\u522b\u7684\u7aef\u53e3\u3002");
            }
            if (denied)
                return ("Windows does not let programs use port " + port + " (it is in a range Windows keeps for other uses, e.g. for Hyper-V): " + programName + " cannot start. " +
                        "The administrator can see the ranges with \"netsh int ipv4 show excludedportrange protocol=tcp\".",
                        // "Windows \u4e0d\u5141\u8bb8\u7a0b\u5e8f\u4f7f\u7528\u7aef\u53e3 <port>\uff08\u5b83\u5728 Windows \u4fdd\u7559\u7684\u8303\u56f4\u91cc\uff0c\u4f8b\u5982\u7ed9 Hyper-V\uff09\uff0c<program> \u65e0\u6cd5\u542f\u52a8\u3002"
                        "Windows \u4e0d\u5141\u8bb8\u7a0b\u5e8f\u4f7f\u7528\u7aef\u53e3 " + port + "\uff08\u5b83\u5728 Windows \u4fdd\u7559\u7684\u8303\u56f4\u91cc\uff0c\u4f8b\u5982\u7ed9 Hyper-V\uff09\uff0c" + programName +
                        " \u65e0\u6cd5\u542f\u52a8\u3002\u7ba1\u7406\u5458\u53ef\u4ee5\u7528 \"netsh int ipv4 show excludedportrange protocol=tcp\" \u67e5\u770b\u3002");
            return ("Port " + port + " cannot be used: " + programName + " cannot start.",
                    // "\u7aef\u53e3 <port> \u65e0\u6cd5\u4f7f\u7528\uff0c<program> \u65e0\u6cd5\u542f\u52a8\u3002"
                    "\u7aef\u53e3 " + port + " \u65e0\u6cd5\u4f7f\u7528\uff0c" + programName + " \u65e0\u6cd5\u542f\u52a8\u3002");
        }

        static string ProgramOf(int pid)
        {
            if (pid == 4) return "System";
            if (pid == 0) return "?";
            try
            {
                using (Process p = Process.GetProcessById(pid)) return p.ProcessName + ".exe";
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>The process id listening on the port in that address family; -1 when none.</summary>
        static int Listener(int port, int family)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);    // TCP_TABLE_OWNER_PID_LISTENER
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    uint result = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
                    if (result == 122) continue;                                   // ERROR_INSUFFICIENT_BUFFER: it grew meanwhile
                    if (result != 0) return -1;
                    int count = Marshal.ReadInt32(table);
                    int rowSize = family == 2 ? 24 : 56;
                    int portOffset = family == 2 ? 8 : 20, pidOffset = family == 2 ? 20 : 52;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = table + 4 + i * rowSize;
                        int raw = Marshal.ReadInt32(row + portOffset);
                        int local = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        if (local == port) return Marshal.ReadInt32(row + pidOffset);
                    }
                    return -1;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            return -1;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);
    }
}
