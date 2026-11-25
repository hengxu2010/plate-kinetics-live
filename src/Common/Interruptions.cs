// Why a run ended early, looked up in Windows' own event logs (System and Application; only read) around the time its
// readings stopped:
//   power-loss-or-crash          Kernel-Power 41 ("rebooted without cleanly shutting down first"), EventLog 6008 ("the
//                                previous system shutdown ... was unexpected", with its time)
//   windows-update-restart       User32 1074 asked by Windows Update (TrustedInstaller, MoUsoCoreWorker, UsoClient, wuauclt,
//                                or an operating-system update reason)
//   restart / shutdown           User32 1074 otherwise (detail: the program that asked, and for which user)
//   sleep                        Kernel-Power 42 (entering sleep), Power-Troubleshooter 1 (woke up; detail: when)
//   instrument-software-crashed  Application Error 1000 / Application Hang 1002 of the instrument software's program
//   recorder-stopped             this program was not looking then (System\log\run-state.json), or its service ended
//                                unexpectedly (Service Control Manager 7031 / 7034)
//   instrument-software-closed   the instrument software's program was seen gone, without an error
//   unknown
// What came first after the last reading ended the run; a stop of the recorder that the PC's own restart, power loss or
// sleep explains (within 5 minutes) gives the PC's reason.
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SAAControl
{
    public static partial class Interruptions
    {
        /// <summary>An event of Windows' logs as far as it matters here.</summary>
        public sealed class WinEvent
        {
            public string Log = "", Provider = "";
            public int Id;
            /// <summary>When it was logged (local time).</summary>
            public DateTime Time;
            /// <summary>Its data, in order (times as ISO text of local time).</summary>
            public List<string> Data = new List<string>();
        }

        /// <summary>The events of the System and Application logs between two local times (tests replace it).</summary>
        public static Func<DateTime, DateTime, List<WinEvent>> Source = ReadLogs;

        public static TimeSpan Before = TimeSpan.FromMinutes(2), After = TimeSpan.FromMinutes(2), Explains = TimeSpan.FromMinutes(5);

        static readonly string[] UpdatePrograms =
        {
            "trustedinstaller.exe", "mousocoreworker.exe", "usoclient.exe", "usocoreworker.exe", "wuauclt.exe", "musnotification.exe",
            "musnotificationux.exe", "waasmedicagent.exe", "sihclient.exe", "tiworker.exe",
        };

        /// <summary>The reason a run ended early, as {"at", "reason", "detail"}: its readings stopped at lastReading
        /// (local), its end was noticed at noticed. software: the instrument software's program files ("Omega2.exe",
        /// "qpcr.exe"); softwareGone: its program was seen gone; recorderDownSince: since when this program did not look
        /// at the instruments (null: it was looking).</summary>
        public static Dictionary<string, object> Find(DateTime lastReading, DateTime noticed, IEnumerable<string> software,
                                                     bool softwareGone = false, DateTime? recorderDownSince = null)
        {
            var programs = new HashSet<string>((software ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s))
                                                   .Select(s => Path.GetFileName(s).ToLowerInvariant()), StringComparer.Ordinal);
            foreach (string p in programs.ToList()) if (!p.EndsWith(".exe", StringComparison.Ordinal)) programs.Add(p + ".exe");
            DateTime from = lastReading - Before, to = (noticed < lastReading ? lastReading : noticed) + After;
            List<WinEvent> events;
            try { events = Source(from, to) ?? new List<WinEvent>(); }
            catch (Exception error)
            {
                Log.Warning("Windows' event logs could not be read (" + error.Message + ")");
                events = new List<WinEvent>();
            }
            // (time, rank: the PC before the software before the recorder, reason, detail)
            var found = new List<(DateTime At, int Rank, string Reason, string Detail)>();
            // a power loss or crash is logged at the next start of Windows: it happened when this program last looked (or
            // the readings stopped), unless the log of that start says when
            DateTime died = recorderDownSince ?? lastReading;
            foreach (WinEvent e in events)
            {
                string provider = e.Provider ?? "";
                if (Is(e, "System", "Microsoft-Windows-Kernel-Power", 41))
                {
                    if (!events.Any(x => Is(x, "System", "EventLog", 6008) && Math.Abs((x.Time - e.Time).TotalMinutes) <= 10))
                        found.Add((died < e.Time ? died : e.Time, 0, "power-loss-or-crash", null));
                }
                else if (Is(e, "System", "EventLog", 6008))
                {
                    DateTime? at = Unexpected(e);
                    found.Add((at.HasValue && at.Value >= from && at.Value <= e.Time ? at.Value : died < e.Time ? died : e.Time, 0, "power-loss-or-crash", null));
                }
                else if (Is(e, "System", "User32", 1074))
                {
                    string process = Item(e, 0), reasonText = Item(e, 2), code = Item(e, 3), kind = Item(e, 4), user = Item(e, 6);
                    string program = ProgramName(process);
                    bool update = UpdatePrograms.Contains(program.ToLowerInvariant()) || UpdateReason(code, reasonText, program, user);
                    string what = update ? "windows-update-restart" : PowersOff(kind) ? "shutdown" : "restart";
                    string by = program.Length > 0 ? program : null;
                    if (by != null && !update && user.Length > 0 && !IsSystemAccount(user)) by += ", " + user;
                    found.Add((e.Time, update ? 1 : 2, what, by));
                }
                else if (Is(e, "System", "Microsoft-Windows-Kernel-Power", 42))
                    found.Add((e.Time, 3, "sleep", null));
                else if (Is(e, "System", "Microsoft-Windows-Power-Troubleshooter", 1))
                {
                    DateTime? slept = Clock.Local(Item(e, 0)), woke = Clock.Local(Item(e, 1));
                    found.Add((slept ?? e.Time, 3, "sleep", woke.HasValue ? "woke " + woke.Value.ToString("HH':'mm", CultureInfo.InvariantCulture) : null));
                }
                else if (e.Log == "Application" && ((provider == "Application Error" && e.Id == 1000) || (provider == "Application Hang" && e.Id == 1002)))
                {
                    string program = Item(e, 0);
                    if (programs.Contains(Path.GetFileName(program).ToLowerInvariant())) found.Add((e.Time, 4, "instrument-software-crashed", program));
                }
                else if (Is(e, "System", "Service Control Manager", 7031) || Is(e, "System", "Service Control Manager", 7034))
                {
                    string service = Item(e, 0);
                    if (service.Length > 0 && string.Equals(service, Runtime.ProgramName, StringComparison.OrdinalIgnoreCase))
                        found.Add((e.Time, 5, "recorder-stopped", "the service ended unexpectedly"));
                }
            }
            if (recorderDownSince.HasValue) found.Add((recorderDownSince.Value, 5, "recorder-stopped", null));
            var candidates = found.Where(c => c.At >= from).OrderBy(c => c.At).ThenBy(c => c.Rank).ToList();
            if (candidates.Count > 0)
            {
                var first = candidates[0];
                // what came at the same time (a minute), the PC's own reason first; a stop of the recorder that the PC's
                // restart, power loss or sleep explains gives the PC's reason
                var same = candidates.Where(c => c.At <= first.At + TimeSpan.FromMinutes(1)).OrderBy(c => c.Rank).First();
                if (same.Rank >= 4)
                {
                    var pc = candidates.Where(c => c.Rank <= 3 && c.At <= first.At + Explains).OrderBy(c => c.At).ThenBy(c => c.Rank).FirstOrDefault();
                    if (pc.Reason != null && (same.Rank == 5 || pc.At <= first.At + TimeSpan.FromMinutes(1))) same = pc;
                }
                return Make(same.At, same.Reason, same.Detail);
            }
            if (softwareGone) return Make(lastReading, "instrument-software-closed", null);
            return Make(lastReading, "unknown", null);
        }

        /// <summary>The reason for a run whose readings stopped at lastReading and whose end is noticed now. seenLive: this
        /// program saw the run measuring (it was looking when it ended); else, when the readings stopped before this
        /// program started, it was not looking then (since the previous run of the program stopped).</summary>
        public static Dictionary<string, object> ForRun(DateTime lastReading, IEnumerable<string> software, bool softwareGone = false, bool seenLive = false)
        {
            DateTime? down = null;
            if (!seenLive && Watchdog.StartedAt > lastReading)
            {
                DateTime? since = Watchdog.PreviousRun?.DownSince;
                down = since.HasValue && since.Value < Watchdog.StartedAt ? since.Value : (DateTime?)null;
                if (down.HasValue && down.Value < lastReading) down = lastReading;
                down = down ?? lastReading;
            }
            return Find(lastReading, DateTime.Now, software, softwareGone, down);
        }

        static Dictionary<string, object> Make(DateTime at, string reason, string detail) => new Dictionary<string, object>
        {
            ["at"] = Clock.Iso(at), ["reason"] = reason, ["detail"] = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
        };

        static bool Is(WinEvent e, string log, string provider, int id) =>
            e.Id == id && string.Equals(e.Log, log, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase);

        static string Item(WinEvent e, int index) => e.Data != null && index < e.Data.Count && e.Data[index] != null ? e.Data[index].Trim() : "";

        /// <summary>"C:\Windows\servicing\TrustedInstaller.exe (PC)" -> "TrustedInstaller.exe".</summary>
        static string ProgramName(string process)
        {
            string text = process ?? "";
            int paren = text.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) text = text.Substring(0, paren);
            try { return Path.GetFileName(text.Trim()); }
            catch (ArgumentException) { return text.Trim(); }
        }

        static bool IsSystemAccount(string user) =>
            user.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase) || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            user.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase);

        /// <summary>An operating-system update as the reason (planned upgrade, service pack, hot fix, security fix), asked
        /// for by the system itself.</summary>
        static bool UpdateReason(string code, string reasonText, string program, string user)
        {
            uint value = 0;
            string hex = (code ?? "").Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) value = 0;
            uint major = value & 0x00FF0000, minor = value & 0x0000FFFF;
            bool osUpdate = major == 0x00020000 && (minor == 0x3 || minor == 0x10 || minor == 0x11 || minor == 0x12);
            return osUpdate && (IsSystemAccount(user ?? "") || program.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The kind of a 1074 ("restart", "power off"; translated on other Windows languages).</summary>
        static bool PowersOff(string kind)
        {
            string k = (kind ?? "").ToLowerInvariant();
            return k.Contains("power off") || k.Contains("shutdown") || k.Contains("\u5173\u673a") || k.Contains("ausschalten") || k.Contains("herunterfahren");
        }

        /// <summary>The time of the unexpected shutdown in a 6008 (its time and date as Windows wrote them).</summary>
        static DateTime? Unexpected(WinEvent e)
        {
            string time = Clean(Item(e, 0)), date = Clean(Item(e, 1));
            if (time.Length == 0 || date.Length == 0) return null;
            foreach (CultureInfo culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("zh-CN") })
                if (DateTime.TryParse(date + " " + time, culture, DateTimeStyles.AllowWhiteSpaces, out DateTime at)) return at;
            return null;
        }

        static string Clean(string text) => new string((text ?? "").Where(c => c != '\u200e' && c != '\u200f' && c != '\u202a' && c != '\u202c').ToArray()).Trim();

        /// <summary>The events that tell (System: 41, 42, 1, 1074, 6008, 7031, 7034; Application: 1000, 1002) between two
        /// local times.</summary>
        static List<WinEvent> ReadLogs(DateTime from, DateTime to)
        {
            var list = new List<WinEvent>();
            string range = "TimeCreated[@SystemTime>='" + Utc(from) + "' and @SystemTime<='" + Utc(to) + "']";
            Read("System", "*[System[(EventID=41 or EventID=42 or EventID=1 or EventID=1074 or EventID=6008 or EventID=7031 or EventID=7034) and " + range + "]]", list);
            Read("Application", "*[System[(EventID=1000 or EventID=1002) and " + range + "]]", list);
            return list;
        }

        static string Utc(DateTime local) =>
            DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);

        static void Read(string log, string query, List<WinEvent> list)
        {
            try
            {
                using (var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query)))
                {
                    for (EventRecord record = reader.ReadEvent(); record != null && list.Count < 5000; record = reader.ReadEvent())
                        using (record)
                        {
                            var e = new WinEvent { Log = log, Provider = record.ProviderName ?? "", Id = record.Id, Time = record.TimeCreated ?? DateTime.MinValue };
                            if (e.Time.Kind == DateTimeKind.Utc) e.Time = e.Time.ToLocalTime();
                            try
                            {
                                foreach (EventProperty p in record.Properties)
                                    e.Data.Add(p.Value is DateTime d ? Clock.Iso(d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d)
                                                                     : Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "");
                            }
                            catch (Exception) { }
                            list.Add(e);
                        }
                }
            }
            catch (Exception error) { Log.Warning("Windows' " + log + " log could not be read (" + error.Message + ")"); }
        }
    }
}
