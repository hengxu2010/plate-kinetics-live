// The recorder watches itself. Every look of a watcher at its instruments ends with Beat(); a thread of its own checks
// every 15 seconds that the looks go on:
//   - no look for 90 s: the problem "recorder-stuck" for each instrument that was measuring ("the recorder has not looked
//     at the instrument since 14:02; the run itself goes on in the instrument software") - not the instrument's fault;
//   - no look for 10 minutes: logged as an error; the Windows service then ends with exit code 3, and Windows' recovery
//     starts it again after 10 s (what it missed is caught up from the instrument software's files, as after an update).
//     A program started by hand cannot be restarted by anyone: it says so instead ("close the program and start it again").
//   - a check that comes long after the one before (the PC slept or hibernated): the time asleep is no time without a
//     look; the quiet time counts from that check on.
// An unexpected error that ends the program is written to System\log\crash-<yyyyMMdd-HHmmss>.txt (and the log).
// System\log\run-state.json tells the next start how this one ended: {"pid", "startedAt", "lookedAt" (the last look, at
// most a minute old), "stoppedAt" (null until a clean stop)}. A start after a crash or a watchdog exit shows the problem
// "crash-restart" once; when the recorder was not running is used for the reasons of interrupted runs (Interruptions).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SAAControl
{
    public static class Watchdog
    {
        public static TimeSpan StuckAfter = TimeSpan.FromSeconds(90);
        public static TimeSpan ExitAfter = TimeSpan.FromMinutes(10);
        public static TimeSpan CheckEvery = TimeSpan.FromSeconds(15);
        public static TimeSpan StateEvery = TimeSpan.FromMinutes(1);
        public const int ExitCode = 3;
        public const int KeptCrashFiles = 30;
        /// <summary>How the service ends itself when it hangs (tests replace it).</summary>
        public static Action<int> Exit = code => Environment.Exit(code);

        static readonly object Gate = new object();
        static long lastBeat = Stopwatch.GetTimestamp();
        static long lastCheck;                  // (Stopwatch) when the previous check ran; 0: none since the start
        static DateTime lastBeatAt = DateTime.Now;
        static List<string> measuring = new List<string>();
        static readonly HashSet<string> stuckShown = new HashSet<string>(StringComparer.Ordinal);   // instrument keys ("": none measuring)
        static bool exited, toldPortable, asService;
        static Thread thread;
        static ManualResetEvent stopping;
        static string logFolder;
        static DateTime startedAt = DateTime.Now, stateWritten = DateTime.MinValue;
        static Previous previous;

        /// <summary>How the program ran before this start (from System\log\run-state.json); null when not known.</summary>
        public sealed class Previous
        {
            public DateTime? StartedAt, LookedAt, StoppedAt;
            /// <summary>It did not stop cleanly (a crash, a power cut, the watchdog, a kill).</summary>
            public bool Unclean => StoppedAt == null;
            /// <summary>From when on it did not look at the instruments any more.</summary>
            public DateTime? DownSince => StoppedAt ?? LookedAt ?? StartedAt;
        }

        public static Previous PreviousRun => previous;
        public static DateTime StartedAt => startedAt;
        public static string LogFolder => logFolder;

        /// <summary>A watcher's look at its instruments is done; measuringNow: the keys of the instruments measuring.</summary>
        public static void Beat(IEnumerable<string> measuringNow)
        {
            List<string> keys = (measuringNow ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
            List<string> cleared;
            lock (Gate)
            {
                lastBeat = Stopwatch.GetTimestamp();
                lastBeatAt = DateTime.Now;
                measuring = keys;
                cleared = stuckShown.ToList();
                stuckShown.Clear();
                toldPortable = false;
            }
            foreach (string key in cleared) Problems.Clear("recorder-stuck", key.Length == 0 ? null : key);
            if (cleared.Count > 0) Log.Info("the recorder looks at the instruments again");
        }

        /// <summary>How long ago the last look ended.</summary>
        public static TimeSpan SinceLook
        {
            get { lock (Gate) return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - lastBeat) / (double)Stopwatch.Frequency); }
        }

        public static DateTime LastLookAt
        {
            get { lock (Gate) return lastBeatAt; }
        }

        /// <summary>Start watching the looks (once the watcher runs). service: the Windows service, which ends itself when
        /// the looks stop for ExitAfter.</summary>
        public static void Start(bool service)
        {
            lock (Gate)
            {
                asService = service;
                if (thread != null) return;
                lastBeat = lastCheck = Stopwatch.GetTimestamp();
                lastBeatAt = DateTime.Now;
                stopping = new ManualResetEvent(false);
                var stop = stopping;
                thread = new Thread(() =>
                {
                    while (!stop.WaitOne(CheckEvery))
                    {
                        try { Check(); }
                        catch (Exception error) { Log.Warning("the watchdog's check failed (" + error.Message + ")"); }
                    }
                }) { IsBackground = true, Name = "watchdog", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
        }

        public static void Stop()
        {
            Thread t;
            lock (Gate)
            {
                t = thread;
                thread = null;
                stopping?.Set();
            }
            t?.Join(2000);
        }

        /// <summary>The check the watchdog's thread makes (tests call it directly). A check that comes more than three
        /// CheckEvery after the one before did not see the looks stop: the process was not running (the PC slept or
        /// hibernated) or not given time, and so was the watcher. The quiet time counts from then on, as if the watcher
        /// had just looked; a watcher that really hangs is still found, one check later.</summary>
        public static void Check()
        {
            TimeSpan quiet;
            DateTime since;
            List<string> keys;
            bool service;
            double held = 0;
            lock (Gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (lastCheck != 0 && (now - lastCheck) / (double)Stopwatch.Frequency > 3 * CheckEvery.TotalSeconds)
                {
                    held = (now - lastCheck) / (double)Stopwatch.Frequency;
                    lastBeat = now;
                }
                lastCheck = now;
                quiet = TimeSpan.FromSeconds((now - lastBeat) / (double)Stopwatch.Frequency);
                since = lastBeatAt;
                keys = measuring.ToList();
                service = asService;
            }
            if (held > 0)
                Log.Info("the watchdog did not run for " + (int)held + " s (the PC slept, or the program was held): the time without a look counts from now");
            WriteState(false);
            if (quiet < StuckAfter) return;
            string at = since.ToString("HH':'mm", CultureInfo.InvariantCulture);
            var (en, zh) = StuckText(at, service);
            foreach (string key in keys.Count > 0 ? keys : new List<string> { "" })
            {
                bool fresh;
                lock (Gate) fresh = stuckShown.Add(key);
                Problems.Set("recorder-stuck", Problems.Error, en, zh, key.Length == 0 ? null : key);
                if (fresh) Log.Error("the recorder has not looked at the instruments since " + since.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture) +
                                     (key.Length > 0 ? " (" + key + " was measuring)" : ""));
            }
            if (quiet < ExitAfter) return;
            if (service)
            {
                lock (Gate)
                {
                    if (exited) return;
                    exited = true;
                }
                string text = "no look at the instruments for " + (int)quiet.TotalMinutes + " minutes (the last at " +
                              since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "): the service ends with exit code " + ExitCode +
                              " so that Windows starts it again\r\n\r\n" + Threads();
                Log.Error("the recorder hangs: " + text.Split('\r')[0]);
                WriteCrash("watchdog", text);
                Exit(ExitCode);
            }
            else
            {
                lock (Gate)
                {
                    if (toldPortable) return;
                    toldPortable = true;
                }
                Log.Error("the recorder hangs: no look at the instruments for " + (int)quiet.TotalMinutes + " minutes; the program must be started again");
            }
        }

        /// <summary>The words of "recorder-stuck": (en, zh).</summary>
        public static (string En, string Zh) StuckText(string at, bool service)
        {
            string en = "The recorder has not looked at the instrument since " + at + "; the run itself goes on in the instrument software. ";
            // "\u8bb0\u5f55\u7a0b\u5e8f\u4ece <at> \u8d77\u6ca1\u6709\u5728\u770b\u8fd9\u53f0\u4eea\u5668\uff1b\u5b9e\u9a8c\u672c\u8eab\u5728\u4eea\u5668\u8f6f\u4ef6\u91cc\u7167\u5e38\u8fdb\u884c\u3002"
            string zh = "\u8bb0\u5f55\u7a0b\u5e8f\u4ece " + at + " \u8d77\u6ca1\u6709\u5728\u770b\u8fd9\u53f0\u4eea\u5668\uff1b\u5b9e\u9a8c\u672c\u8eab\u5728\u4eea\u5668\u8f6f\u4ef6\u91cc\u7167\u5e38\u8fdb\u884c\u3002";
            if (service)
            {
                en += "The recorder starts itself again if this lasts 10 minutes.";
                // "\u6301\u7eed 10 \u5206\u949f\u4f1a\u81ea\u52a8\u91cd\u542f\u8bb0\u5f55\u7a0b\u5e8f\u3002"
                zh += "\u6301\u7eed 10 \u5206\u949f\u4f1a\u81ea\u52a8\u91cd\u542f\u8bb0\u5f55\u7a0b\u5e8f\u3002";
            }
            else
            {
                en += "If this goes on, close the program and start it again.";
                // "\u5982\u679c\u4e00\u76f4\u8fd9\u6837\uff0c\u8bf7\u5173\u95ed\u672c\u7a0b\u5e8f\u518d\u91cd\u65b0\u6253\u5f00\u3002"
                zh += "\u5982\u679c\u4e00\u76f4\u8fd9\u6837\uff0c\u8bf7\u5173\u95ed\u672c\u7a0b\u5e8f\u518d\u91cd\u65b0\u6253\u5f00\u3002";
            }
            return (en, zh);
        }

        /// <summary>The managed threads cannot be listed with their stacks here; what is known of the process is.</summary>
        static string Threads()
        {
            try
            {
                using (Process me = Process.GetCurrentProcess())
                    return "process " + me.Id + ", " + me.Threads.Count + " threads, " + (me.WorkingSet64 >> 20) + " MB in use, " +
                           (int)me.TotalProcessorTime.TotalSeconds + " s of processor time since " + me.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }

        // ---- how the program ended: crash files and run-state.json

        /// <summary>At the start, once the log is open: what the previous run left (a crash shows "crash-restart"), and
        /// this run's run-state.json.</summary>
        public static void Started(DataLayout layout)
        {
            logFolder = layout.Logs;
            startedAt = DateTime.Now;
            string path = StatePath;
            previous = null;
            try
            {
                if (File.Exists(path) && Json.Parse(File.ReadAllText(path, Encoding.UTF8)) is Dictionary<string, object> state)
                {
                    DateTime? Time(string key) => state.TryGetValue(key, out object v) && v is string s ? Clock.Local(s) : null;
                    previous = new Previous { StartedAt = Time("startedAt"), LookedAt = Time("lookedAt"), StoppedAt = Time("stoppedAt") };
                }
            }
            catch (Exception) { }
            if (previous != null && previous.Unclean)
            {
                Log.Warning("the previous run of the program did not stop cleanly (it last looked at the instruments at " +
                            (previous.LookedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?") + ")");
                ShowCrash(previous);
            }
            WriteState(true);
        }

        /// <summary>The problem "crash-restart" when the previous run ended with a crash file (an error, or the watchdog).</summary>
        static void ShowCrash(Previous before)
        {
            try
            {
                if (logFolder == null || !Directory.Exists(logFolder)) return;
                DateTime after = before.StartedAt ?? DateTime.MinValue;
                var crash = Directory.GetFiles(logFolder, "crash-*.txt").Select(p => new FileInfo(p))
                                     .Where(f => f.LastWriteTime >= after).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (crash == null) return;
                string first = File.ReadLines(crash.FullName).FirstOrDefault() ?? "";
                bool hung = first.IndexOf("watchdog", StringComparison.OrdinalIgnoreCase) >= 0;
                string at = crash.LastWriteTime.ToString("MM'-'dd' 'HH':'mm", CultureInfo.InvariantCulture);
                string file = "System\\log\\" + crash.Name;
                string en = (hung ? "The recorder hung at " + at + " and was started again" : "The program stopped because of an unexpected error at " + at + " and was started again") +
                            "; readings it missed are filled in from the instrument software's files. Details: " + file + ".";
                // hung: "\u8bb0\u5f55\u7a0b\u5e8f\u5728 <at> \u5361\u4f4f\uff0c\u5df2\u81ea\u52a8\u91cd\u542f" / error: "\u7a0b\u5e8f\u5728 <at> \u56e0\u610f\u5916\u9519\u8bef\u505c\u6b62\uff0c\u5df2\u91cd\u65b0\u542f\u52a8"
                // then "\uff1b\u6f0f\u6389\u7684\u8bfb\u6570\u4f1a\u4ece\u4eea\u5668\u8f6f\u4ef6\u7684\u6587\u4ef6\u91cc\u8865\u56de\u3002\u8be6\u60c5\u89c1 <file>\u3002"
                string zh = (hung ? "\u8bb0\u5f55\u7a0b\u5e8f\u5728 " + at + " \u5361\u4f4f\uff0c\u5df2\u81ea\u52a8\u91cd\u542f"
                                  : "\u7a0b\u5e8f\u5728 " + at + " \u56e0\u610f\u5916\u9519\u8bef\u505c\u6b62\uff0c\u5df2\u91cd\u65b0\u542f\u52a8") +
                            "\uff1b\u6f0f\u6389\u7684\u8bfb\u6570\u4f1a\u4ece\u4eea\u5668\u8f6f\u4ef6\u7684\u6587\u4ef6\u91cc\u8865\u56de\u3002\u8be6\u60c5\u89c1 " + file + "\u3002";
                Problems.Set("crash-restart", Problems.Warning, en, zh, null, dismissible: true);
            }
            catch (Exception error) { Log.Warning("the crash files could not be looked at (" + error.Message + ")"); }
        }

        static string StatePath => logFolder == null ? null : Path.Combine(logFolder, "run-state.json");

        /// <summary>run-state.json now (at most once a minute unless now).</summary>
        static void WriteState(bool now, string stoppedAt = null)
        {
            string path = StatePath;
            if (path == null) return;
            DateTime looked;
            lock (Gate)
            {
                if (!now && DateTime.Now - stateWritten < StateEvery) return;
                stateWritten = DateTime.Now;
                looked = lastBeatAt;
            }
            var state = new Dictionary<string, object>
            {
                ["pid"] = (long)SafePid(), ["startedAt"] = Clock.Iso(startedAt), ["lookedAt"] = Clock.Iso(looked), ["stoppedAt"] = stoppedAt,
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, Json.Serialize(state), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception) { }
        }

        static int SafePid()
        {
            try { using (Process me = Process.GetCurrentProcess()) return me.Id; }
            catch (Exception) { return 0; }
        }

        /// <summary>A clean stop: run-state.json says when.</summary>
        public static void Stopped() => WriteState(true, Clock.Now());

        /// <summary>An error no part of the program caught: the log and a crash file.</summary>
        public static void Crash(Exception error, bool terminating)
        {
            Log.Error("an unexpected error " + (terminating ? "ends the program" : "was not caught"), error);
            WriteCrash(terminating ? "unexpected error" : "unexpected error (the program went on)", error?.ToString() ?? "(no details)");
        }

        /// <summary>System\log\crash-yyyyMMdd-HHmmss.txt (the newest 30 are kept). Returns its path, or null.</summary>
        public static string WriteCrash(string kind, string details)
        {
            string folder = logFolder;
            if (folder == null) return null;
            try
            {
                Directory.CreateDirectory(folder);
                DateTime now = DateTime.Now;
                string path = Path.Combine(folder, "crash-" + now.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture) + ".txt");
                for (int n = 2; File.Exists(path) && n < 100; n++)
                    path = Path.Combine(folder, "crash-" + now.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture) + "-" + n + ".txt");
                var text = new StringBuilder();
                text.Append(kind).Append("\r\n");
                text.Append("time: ").Append(Clock.Iso(now)).Append("\r\n");
                text.Append("program: ").Append(Runtime.ProgramName).Append(' ').Append(Experiments.ProgramVersion()).Append(" (").Append(Runtime.Edition)
                    .Append(Runtime.AsService ? ", the Windows service" : "").Append(")\r\n");
                text.Append("Windows: ").Append(Environment.OSVersion.VersionString).Append(", ").Append(Environment.Is64BitProcess ? "64-bit" : "32-bit").Append("\r\n");
                text.Append("started: ").Append(Clock.Iso(startedAt)).Append(", last look at the instruments: ").Append(Clock.Iso(LastLookAt)).Append("\r\n\r\n");
                text.Append((details ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                foreach (var old in Directory.GetFiles(folder, "crash-*.txt").Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptCrashFiles))
                {
                    try { old.Delete(); } catch (Exception) { }
                }
                return path;
            }
            catch (Exception) { return null; }
        }

        /// <summary>For tests: as at a fresh start.</summary>
        public static void Reset()
        {
            Stop();
            lock (Gate)
            {
                lastBeat = Stopwatch.GetTimestamp();
                lastCheck = 0;
                lastBeatAt = DateTime.Now;
                measuring = new List<string>();
                stuckShown.Clear();
                exited = toldPortable = asService = false;
                logFolder = null;
                previous = null;
                stateWritten = DateTime.MinValue;
            }
        }

        /// <summary>For tests: the last look was that long ago.</summary>
        public static void Pretend(TimeSpan ago)
        {
            lock (Gate)
            {
                lastBeat = Stopwatch.GetTimestamp() - (long)(ago.TotalSeconds * Stopwatch.Frequency);
                lastBeatAt = DateTime.Now - ago;
            }
        }

        /// <summary>For tests: the PC slept that long right after the last look and the last check.</summary>
        public static void PretendSlept(TimeSpan span)
        {
            lock (Gate)
            {
                long ticks = (long)(span.TotalSeconds * Stopwatch.Frequency);
                lastBeat -= ticks;
                lastCheck = (lastCheck != 0 ? lastCheck : Stopwatch.GetTimestamp()) - ticks;
                lastBeatAt -= span;
            }
        }
    }

    /// <summary>The tray's looks at the service in a row that got no answer: after Limit of them the user is told once
    /// that the service does not answer. Counted in looks, not with the clock, so that the first look after a standby (or
    /// after the clock was set forward) is one look that failed, not minutes of silence.</summary>
    public sealed class SilentLooks
    {
        public readonly int Limit;
        int failed;
        bool told;

        public SilentLooks(int limit)
        {
            Limit = Math.Max(1, limit);
        }

        /// <summary>The looks in a row without an answer.</summary>
        public int Count => failed;

        /// <summary>A look got no answer. True once per silent spell: at the look that makes it Limit looks long.</summary>
        public bool Failed()
        {
            failed++;
            if (told || failed < Limit) return false;
            told = true;
            return true;
        }

        /// <summary>The service answered: the next silence is a new spell.</summary>
        public void Answered()
        {
            failed = 0;
            told = false;
        }
    }
}
