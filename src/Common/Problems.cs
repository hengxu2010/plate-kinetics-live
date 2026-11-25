// What the program tells the page besides the instruments (/api/state): the problems that are there now ("problems":
// storage, disk space, a start-up repair, and what the watchers add: recorder-stuck, unverified-version, ...) and the
// last 50 events of the runs ("events": completed, stopped, interrupted, stuck, save-failed), which the window and the
// tray announce once each (by their increasing id). Also the clock (times with the PC's UTC offset), the user who is
// logged on, and what the parts of the program share at run time (Runtime).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SAAControl
{
    /// <summary>Times as the experiment files write them: ISO 8601 with the PC's UTC offset, "2026-01-20T09:30:00+08:00".</summary>
    public static class Clock
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A local time with the offset the PC's time zone had then (to the second).</summary>
        public static string Iso(DateTime local)
        {
            if (local.Kind == DateTimeKind.Utc) local = local.ToLocalTime();
            var at = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Local);
            return at.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", Inv) + Offset(TimeZoneInfo.Local.GetUtcOffset(at));
        }

        public static string Now() => Iso(DateTime.Now);

        /// <summary>"+08:00", "-05:00", "+00:00".</summary>
        public static string Offset(TimeSpan offset)
        {
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            offset = offset.Duration();
            return sign + ((int)offset.TotalHours).ToString("00", Inv) + ":" + offset.Minutes.ToString("00", Inv);
        }

        /// <summary>A time as the records hold it ("2026-01-20T09:30:00", local) with the offset of the PC's time zone at
        /// that time. A time that has an offset (or "Z") already is returned as it is; null and "" stay null.</summary>
        public static string WithOffset(string time)
        {
            if (string.IsNullOrEmpty(time)) return null;
            if (HasOffset(time)) return time;
            DateTime? local = Local(time);
            return local.HasValue ? Iso(local.Value) : time;
        }

        static bool HasOffset(string time)
        {
            if (time.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return true;
            int t = time.IndexOf('T');
            if (t < 0) t = time.IndexOf(' ');
            if (t < 0) return false;
            int sign = time.LastIndexOfAny(new[] { '+', '-' });
            return sign > t;
        }

        /// <summary>The local time of "yyyy-MM-ddTHH:mm:ss" (also with a space, with a fraction, or with an offset);
        /// null when it is none.</summary>
        public static DateTime? Local(string time)
        {
            if (string.IsNullOrEmpty(time) || time.Length < 19) return null;
            if (HasOffset(time) && DateTimeOffset.TryParse(time, Inv, DateTimeStyles.None, out DateTimeOffset withOffset))
                return withOffset.LocalDateTime;
            string head = time.Substring(0, 19);
            if (DateTime.TryParseExact(head, "yyyy'-'MM'-'dd'T'HH':'mm':'ss", Inv, DateTimeStyles.None, out DateTime t)) return t;
            if (DateTime.TryParseExact(head, "yyyy'-'MM'-'dd' 'HH':'mm':'ss", Inv, DateTimeStyles.None, out t)) return t;
            return null;
        }

        /// <summary>The PC's time zone as Windows names it ("China Standard Time").</summary>
        public static string TimeZoneId
        {
            get
            {
                try { return TimeZoneInfo.Local.Id; }
                catch (Exception) { return null; }
            }
        }
    }

    /// <summary>Who uses the PC: the user logged on at the console (a service runs as SYSTEM, the user is someone
    /// else), and the PC's name.</summary>
    public static class Session
    {
        static string cached;
        static long cachedAt = Environment.TickCount - 60000;

        /// <summary>The name of the user logged on at the PC's console ("" when nobody is); the program's own user when it
        /// runs in a user's session. Looked up at most every 10 seconds.</summary>
        public static string ConsoleUser()
        {
            if (cached != null && unchecked((uint)(Environment.TickCount - Interlocked.Read(ref cachedAt))) < 10000) return cached;
            string user = "";
            try
            {
                if (OwnSession() != 0) user = Environment.UserName ?? "";
                else
                {
                    uint session = WTSGetActiveConsoleSessionId();
                    if (session != 0xFFFFFFFF) user = Query(session, 5);                 // WTSUserName
                    if (user.Length == 0) user = ActiveSessionUser();                   // (someone at the PC through Remote Desktop)
                }
            }
            catch (Exception) { }
            cached = user;
            Interlocked.Exchange(ref cachedAt, Environment.TickCount);
            return user;
        }

        /// <summary>The Windows session this process runs in (0: the services' session).</summary>
        public static int OwnSession()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().SessionId; }
            catch (Exception) { return 0; }
        }

        /// <summary>The user who runs this program (the window, a portable copy): the Windows user name.</summary>
        public static string ThisUser()
        {
            try { return Environment.UserName ?? ""; }
            catch (Exception) { return ""; }
        }

        public static string Machine()
        {
            try { return Environment.MachineName ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>"user (PC)": who saved something.</summary>
        public static string UserOnMachine(string user)
        {
            string pc = Machine();
            if (string.IsNullOrEmpty(user)) return pc;
            return pc.Length > 0 ? user + " (" + pc + ")" : user;
        }

        /// <summary>The user of the first active session (Remote Desktop included); "" when there is none.</summary>
        static string ActiveSessionUser()
        {
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr sessions, out int count)) return "";
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var info = (WTS_SESSION_INFO)Marshal.PtrToStructure(sessions + i * size, typeof(WTS_SESSION_INFO));
                    if (info.State != 0 || info.SessionId == 0) continue;           // only active sessions (WTSActive)
                    string user = Query(info.SessionId, 5);
                    if (user.Length > 0) return user;
                }
                return "";
            }
            finally { WTSFreeMemory(sessions); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WTS_SESSION_INFO
        {
            public uint SessionId;
            public string WinStationName;
            public int State;
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

        static string Query(uint session, int what)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, what, out IntPtr buffer, out int bytes)) return "";
            try { return bytes > 1 ? Marshal.PtrToStringUni(buffer) ?? "" : ""; }
            finally { WTSFreeMemory(buffer); }
        }

        [DllImport("kernel32.dll")]
        static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);
    }

    /// <summary>What the parts of the program share while it runs (set by the host at the start).</summary>
    public static class Runtime
    {
        /// <summary>The program's name as a file name ("PlateKineticsLive", "PlateKineticsLive-Archimed").</summary>
        public static string AppName = "";
        /// <summary>The program's name as people see it ("Plate Kinetics Live", "Plate Kinetics Live (Archimed)").</summary>
        public static string ProgramName = "Plate Kinetics Live";
        /// <summary>"installed" (the installer put it there) or "portable" (the zip, no service).</summary>
        public static string Edition = "portable";
        /// <summary>True for the Windows service (no user's session: no windows, no Recycle Bin, no Explorer).</summary>
        public static bool AsService;
        public static DataLayout Layout;
        public static SettingsStore Settings;
    }

    public static class Problems
    {
        public const string Info = "info", Warning = "warning", Error = "error";
        /// <summary>The kinds of events (and of notifications).</summary>
        public static readonly string[] EventKinds = { "completed", "stopped", "interrupted", "stuck", "save-failed" };
        const int KeptEvents = 50;

        sealed class Problem
        {
            public string Code, Severity, Message, MessageZh, Since, Instrument;
            public bool Dismissible;
        }

        static readonly object Gate = new object();
        static readonly List<Problem> Current = new List<Problem>();
        static readonly List<Dictionary<string, object>> Kept = new List<Dictionary<string, object>>();
        // event ids grow across restarts too: they start at the time of the start (milliseconds since 2020), and there are
        // never a thousand events a second
        static long lastEvent = (DateTime.UtcNow.Ticks - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / TimeSpan.TicksPerMillisecond;

        /// <summary>Whether the program keeps the PC awake right now (a run is measuring and the setting is on).</summary>
        public static volatile bool KeepAwakeActive;

        /// <summary>A problem is there (again, or with new words): shown until it is cleared. Its "since" stays the time it
        /// was first set.</summary>
        public static void Set(string code, string severity, string message, string messageZh, string instrument = null, bool dismissible = false)
        {
            if (string.IsNullOrEmpty(code)) return;
            lock (Gate)
            {
                Problem known = Current.FirstOrDefault(p => p.Code == code && p.Instrument == instrument);
                if (known == null)
                {
                    known = new Problem { Code = code, Instrument = instrument, Since = Clock.Now() };
                    Current.Add(known);
                }
                known.Severity = severity == Error || severity == Warning ? severity : Info;
                known.Message = message ?? "";
                known.MessageZh = messageZh ?? "";
                known.Dismissible = dismissible;
            }
        }

        /// <summary>The problem is gone (for that instrument; instrument null: the program's own problem).</summary>
        public static void Clear(string code, string instrument = null)
        {
            lock (Gate) Current.RemoveAll(p => p.Code == code && p.Instrument == instrument);
        }

        public static bool Has(string code, string instrument = null)
        {
            lock (Gate) return Current.Any(p => p.Code == code && p.Instrument == instrument);
        }

        /// <summary>/api/state "problems": errors first, then warnings, then the rest; each kind in the order it came.</summary>
        public static List<object> List()
        {
            lock (Gate)
            {
                int Rank(Problem p) => p.Severity == Error ? 0 : p.Severity == Warning ? 1 : 2;
                return Current.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p)).ThenBy(x => x.i).Select(x =>
                {
                    var item = new Dictionary<string, object>
                    {
                        ["code"] = x.p.Code, ["severity"] = x.p.Severity, ["message"] = x.p.Message, ["messageZh"] = x.p.MessageZh,
                        ["since"] = x.p.Since,
                    };
                    if (x.p.Instrument != null) item["instrument"] = x.p.Instrument;
                    item["dismissible"] = x.p.Dismissible;
                    return (object)item;
                }).ToList();
            }
        }

        /// <summary>Something happened to a run (kind: one of EventKinds). Returns its id.</summary>
        public static long Event(string kind, string instrument, string runId, string name, string message, string messageZh)
        {
            lock (Gate)
            {
                long id = ++lastEvent;
                Kept.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["at"] = Clock.Now(), ["kind"] = kind ?? "", ["instrument"] = instrument, ["runId"] = runId, ["name"] = name,
                    ["message"] = message ?? "", ["messageZh"] = messageZh ?? "",
                });
                if (Kept.Count > KeptEvents) Kept.RemoveRange(0, Kept.Count - KeptEvents);
                return id;
            }
        }

        /// <summary>/api/state "events": the last 50, oldest first.</summary>
        public static List<object> Events()
        {
            lock (Gate) return Kept.Select(e => (object)new Dictionary<string, object>(e)).ToList();
        }

        /// <summary>For tests: nothing there.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Current.Clear();
                Kept.Clear();
            }
        }
    }

    /// <summary>Why a run ended early (looked up in Windows' event logs around the gap: Interruptions.cs), in words for
    /// the card, the experiment file and the workbook: "03:10 the PC restarted for a Windows update".</summary>
    public static partial class Interruptions
    {
        public static readonly string[] Reasons =
        {
            "power-loss-or-crash", "windows-update-restart", "restart", "shutdown", "sleep", "instrument-software-crashed",
            "instrument-software-closed", "recorder-stopped", "unknown",
        };

        /// <summary>The words for an interruption {at, reason, detail} (null: none): "03:10 the PC restarted for a Windows
        /// update" / "03:10 \u7535\u8111\u56e0 Windows \u66f4\u65b0\u91cd\u542f". The time is the day's time; another day than the start's gets its date.</summary>
        public static (string En, string Zh) Text(Dictionary<string, object> interruption, string startedAt = null)
        {
            if (interruption == null) return (null, null);
            string reason = interruption.TryGetValue("reason", out object r) ? r as string : null;
            string at = interruption.TryGetValue("at", out object a) ? a as string : null;
            string detail = interruption.TryGetValue("detail", out object d) ? d as string : null;
            string when = When(at, startedAt);
            string en, zh;
            switch (reason)
            {
                case "power-loss-or-crash":
                    en = "the PC lost power or stopped unexpectedly";
                    zh = "\u7535\u8111\u65ad\u7535\u6216\u610f\u5916\u505c\u6b62";                        // the PC lost power or stopped
                    break;
                case "windows-update-restart":
                    en = "the PC restarted for a Windows update";
                    zh = "\u7535\u8111\u56e0 Windows \u66f4\u65b0\u91cd\u542f";                           // the PC restarted for an update
                    break;
                case "restart":
                    en = "the PC was restarted";
                    zh = "\u7535\u8111\u91cd\u542f";                                                       // the PC restarted
                    break;
                case "shutdown":
                    en = "the PC was shut down";
                    zh = "\u7535\u8111\u5173\u673a";                                                       // the PC was shut down
                    break;
                case "sleep":
                    en = "the PC went to sleep";
                    zh = "\u7535\u8111\u8fdb\u5165\u7761\u7720";                                           // the PC went to sleep
                    break;
                case "instrument-software-crashed":
                    en = "the instrument software stopped with an error";
                    zh = "\u4eea\u5668\u8f6f\u4ef6\u51fa\u9519\u9000\u51fa";                               // the software failed
                    break;
                case "instrument-software-closed":
                    en = "the instrument software was closed";
                    zh = "\u4eea\u5668\u8f6f\u4ef6\u88ab\u5173\u95ed";                                     // the software was closed
                    break;
                case "recorder-stopped":
                    en = "the recording program was stopped";
                    zh = "\u8bb0\u5f55\u7a0b\u5e8f\u88ab\u505c\u6b62";                                     // the recorder was stopped
                    break;
                default:
                    en = "reason unknown";
                    zh = "\u539f\u56e0\u4e0d\u660e";                                                       // reason unknown
                    break;
            }
            if (!string.IsNullOrWhiteSpace(detail)) { en += " (" + detail.Trim() + ")"; zh += "\uff08" + detail.Trim() + "\uff09"; }
            return when.Length > 0 ? (when + " " + en, when + " " + zh) : (en, zh);
        }

        static string When(string at, string startedAt)
        {
            DateTime? t = Clock.Local(at);
            if (!t.HasValue) return "";
            DateTime? start = Clock.Local(startedAt);
            string format = start.HasValue && start.Value.Date != t.Value.Date ? "MM'-'dd' 'HH':'mm" : "HH':'mm";
            return t.Value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
