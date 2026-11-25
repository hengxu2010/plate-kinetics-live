// The professional mode of the installed program: a Windows service does the watching and recording (it starts with
// Windows, needs nobody signed in and no window, and Windows restarts it if it fails), and the program's window only
// shows it. The service runs the same host as the program itself (Host.RunCore), on the same address and port, so
// the page, the shell and every check made over the local address work unchanged.
//
//   PlateKineticsLive.exe --service            run as the service (started by Windows)
//   PlateKineticsLive.exe --install-service    register the service and start it (administrator; the installer calls this);
//                                              with --demand-start it is registered to start only when asked (the
//                                              installer's move between folders: it starts with Windows once the move
//                                              is complete, so that a power cut halfway leaves the former one in charge)
//   PlateKineticsLive.exe --uninstall-service  stop and remove the service (administrator; the uninstaller calls this)
//   PlateKineticsLive.exe --start-service      start the service (administrator; the window asks for this when needed)
//   PlateKineticsLive.exe                      with a service that can run: the window only; without one: the whole program
// The service runs under Windows' own account at "below normal" priority (the instrument software always comes first),
// and Windows restarts it when it fails (10 s, 30 s, then every minute).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SAAControl
{
    public static class ServiceMode
    {
        // Windows errors (sc.exe exits with them, ServiceController reports them as the inner Win32Exception)
        const int AccessDenied = 5, NoSuchService = 1060, MarkedForDeletion = 1072, ServiceExists = 1073;

        public static bool IsInstalled(string appName)
        {
            try { return ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, appName, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception) { return false; }
        }

        public static ServiceControllerStatus? Status(string appName)
        {
            try { using (var sc = new ServiceController(appName)) return sc.Status; }
            catch (Exception) { return null; }
        }

        /// <summary>True when the service can do the program's work, so that starting the program only shows its window:
        /// it runs or is starting, or Windows can start it - its program file is there, it is not disabled and it is not
        /// marked for deletion.</summary>
        public static bool IsUsable(string appName)
        {
            var status = Status(appName);
            if (status == null) return false;                                    // not installed
            if (status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending) return true;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + appName))
                {
                    if (key == null) return false;
                    if (key.GetValue("Start") is int start && start == 4) return false;               // disabled
                    if (key.GetValue("DeleteFlag") is int deleted && deleted != 0) return false;       // marked for deletion
                    string exe = ImageExe(key.GetValue("ImagePath") as string);
                    return exe != null && File.Exists(exe);
                }
            }
            catch (Exception) { return true; }           // cannot tell: the service is used, as it always was
        }

        static string ImageExe(string imagePath) => InstallShared.ImageExe(imagePath);

        static int Sc(string arguments, out string output)
        {
            // by its full path: a sc.exe put beside the program (by anyone who may write there) must never run as administrator
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return p.ExitCode;
            }
        }

        /// <summary>The settings file the service reads: beside the program, written when the service is registered.</summary>
        public static string SettingsFile(string appName) => Path.Combine(Files.ProgramDir, appName + ".service.json");

        /// <summary>The service runs under Windows' own account, whose "Desktop" is not the user's. Where the programs
        /// look on the Desktop (the PCR program finds the experiment files there, as before), the service looks at the
        /// Desktop of the user who registered it: their profile folder is kept in the settings file.</summary>
        public static void UseUserProfile(string appName)
        {
            try
            {
                string file = SettingsFile(appName);
                if (!File.Exists(file)) return;
                if (!(Json.Parse(File.ReadAllText(file)) is Dictionary<string, object> s)) return;
                if (s.TryGetValue("userProfile", out var p) && p is string profile && Directory.Exists(profile))
                {
                    Environment.SetEnvironmentVariable("USERPROFILE", profile);
                    Log.Info("the service looks at the Desktop of " + profile);
                }
            }
            catch (Exception error) { Log.Error("reading " + SettingsFile(appName) + " failed", error); }
        }

        /// <summary>The settings file is written at the first registration. A later one keeps the profile it holds while
        /// that folder is there: the installer may run with another administrator's rights, whose Desktop is not the
        /// one the experiments are saved on.</summary>
        static void WriteSettings(string appName)
        {
            string file = SettingsFile(appName);
            try
            {
                if (KeptProfile(file) != null) return;
                // the program under its former name (SAAControl until 2026-11-23) was registered before: its profile is kept
                string former = InstallShared.FormerName(appName);
                string formerFile = former == null ? null : Path.Combine(Files.ProgramDir, former + ".service.json");
                if (formerFile != null && KeptProfile(formerFile) != null) { File.Copy(formerFile, file, true); return; }
                var settings = new Dictionary<string, object>
                {
                    ["userProfile"] = Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
                    ["user"] = Environment.UserDomainName + "\\" + Environment.UserName,
                    ["registered"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                };
                File.WriteAllText(file, Json.Serialize(settings, ensureAscii: false, indent: 1));
            }
            catch (Exception error) { Console.Error.WriteLine("writing the service settings: " + error.Message); }
        }

        /// <summary>The profile folder the settings file names, when it is there; else null.</summary>
        static string KeptProfile(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                if (!(Json.Parse(File.ReadAllText(file)) is Dictionary<string, object> s)) return null;
                return s.TryGetValue("userProfile", out var p) && p is string profile && profile.Length > 0 && Directory.Exists(profile) ? profile : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Register the service for this program file and start it. Returns 0 when it is running. A service that
        /// is registered already is changed to run this program file, not removed and made again: removing it only marks
        /// it for deletion while another program holds it open (Services, say), and security software may stop a new
        /// service from being made.</summary>
        public static int Install(string appName, string description)
        {
            string exe = Application.ExecutablePath;
            bool demand = Environment.GetCommandLineArgs().Any(a => a.Equals("--demand-start", StringComparison.OrdinalIgnoreCase));
            WriteSettings(appName);
            StopAndWait(appName, 60);          // when it runs: it starts again below, from this program file
            string config = "\"" + appName + "\" binPath= \"\\\"" + exe + "\\\" --service\" start= " + (demand ? "demand" : "auto") +
                            " DisplayName= \"" + InstallShared.DisplayName(appName) + "\"";
            var waited = Stopwatch.StartNew();
            bool exists = IsInstalled(appName);
            string verb, text;
            int code;
            while (true)
            {
                verb = exists ? "config" : "create";
                code = Sc(verb + " " + config, out text);
                if (code == 0 || waited.Elapsed.TotalSeconds >= 60) break;
                if (code == ServiceExists) exists = true;              // made meanwhile: change it
                else if (code == NoSuchService) exists = false;        // removed meanwhile (it was marked for deletion): make it
                else if (code != MarkedForDeletion) break;
                // marked for deletion: it can be neither changed nor made again until Windows has removed it, which it
                // does once no program holds it open any more
                Thread.Sleep(code == MarkedForDeletion ? 1000 : 250);
            }
            if (code == MarkedForDeletion)
            {
                Console.Error.WriteLine("the Windows service " + appName + " is marked for deletion: Windows removes it only when no program " +
                                        "holds it open any more. Close Services (services.msc) and any other service tool, then run the installer again.");
                return code;
            }
            if (code != 0) { Console.Error.WriteLine("sc " + verb + " failed: " + text.Trim()); return code; }
            Sc("description \"" + appName + "\" \"" + description.Replace("\"", "'") + "\"", out _);
            // when it fails - it ends without being asked to, or with an error - Windows starts it again (after 10 s,
            // 30 s, then every minute; the count resets after a day). Checked: a service that is not restarted after a
            // failure would leave the PC without a recorder until somebody notices.
            int failure = Sc("failure \"" + appName + "\" reset= 86400 actions= restart/10000/restart/30000/restart/60000", out string failureText);
            int flag = Sc("failureflag \"" + appName + "\" 1", out string flagText);
            if (failure != 0 || flag != 0 || !InstallShared.RestartsAfterFailure(appName))
                Console.Error.WriteLine("WARNING: the restart after a failure could not be set: " + (failureText + " " + flagText).Trim());
            // signed-in users may start it (the window starts it when it is not running) - besides the usual rights
            Sc("sdset \"" + appName + "\" D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)", out _);
            return Start(appName, 60) ? 0 : 1;
        }

        /// <summary>Stops the service, when it is there and not stopped, and waits for that (seconds).</summary>
        static void StopAndWait(string appName, int seconds)
        {
            var status = Status(appName);
            if (status == null || status == ServiceControllerStatus.Stopped) return;
            try
            {
                using (var sc = new ServiceController(appName))
                {
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(seconds));
                }
            }
            catch (Exception error) { Console.Error.WriteLine("stopping the service: " + error.Message); }
        }

        /// <summary>Stop the service and remove it. Returns 0 when it is gone.</summary>
        public static int Uninstall(string appName, bool quiet = false)
        {
            if (!IsInstalled(appName)) return 0;
            try
            {
                using (var sc = new ServiceController(appName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
            }
            catch (Exception error) { if (!quiet) Console.Error.WriteLine("stopping the service: " + error.Message); }
            int code = Sc("delete \"" + appName + "\"", out string text);
            if (code != 0 && !quiet) Console.Error.WriteLine("sc delete failed: " + text.Trim());
            for (int i = 0; i < 40 && IsInstalled(appName); i++) Thread.Sleep(250);   // Windows removes it once nobody holds it
            return IsInstalled(appName) ? 1 : 0;
        }

        /// <summary>Start the service and wait until it runs (seconds). False when it does not.</summary>
        public static bool Start(string appName, int seconds) => Start(appName, seconds, out _);

        /// <summary>Start the service and wait until it runs (seconds). One that is starting is waited for; one that is
        /// stopping (a restart, an update) is started once it has stopped. False when it does not run; error is then the
        /// Windows error of the failed start (5: this user may not start it), or 0.</summary>
        public static bool Start(string appName, int seconds, out int error)
        {
            error = 0;
            var watch = Stopwatch.StartNew();
            TimeSpan Left() => TimeSpan.FromSeconds(Math.Max(1, seconds - watch.Elapsed.TotalSeconds));
            try
            {
                using (var sc = new ServiceController(appName))
                {
                    for (int look = 0; look < 5; look++)
                    {
                        sc.Refresh();
                        if (sc.Status == ServiceControllerStatus.Running) return true;
                        if (sc.Status == ServiceControllerStatus.StopPending)
                        {
                            if (!WaitFor(sc, ServiceControllerStatus.Stopped, Left())) return false;
                            continue;
                        }
                        if (sc.Status == ServiceControllerStatus.Stopped)
                        {
                            try { sc.Start(); }
                            catch (InvalidOperationException e)
                            {
                                error = Win32Error(e);
                                sc.Refresh();
                                if (error == AccessDenied || sc.Status == ServiceControllerStatus.Stopped) return false;
                                continue;      // it began starting or stopping meanwhile (Windows, a restart): look again
                            }
                        }
                        return WaitFor(sc, ServiceControllerStatus.Running, Left());
                    }
                    return false;
                }
            }
            catch (InvalidOperationException e)
            {
                if (error == 0) error = Win32Error(e);   // not installed (1060), not allowed to look (5)
                return false;
            }
            catch (Exception) { return false; }
        }

        static bool WaitFor(ServiceController sc, ServiceControllerStatus status, TimeSpan time)
        {
            try { sc.WaitForStatus(status, time); }
            catch (System.ServiceProcess.TimeoutException) { }
            sc.Refresh();
            return sc.Status == status;
        }

        static int Win32Error(Exception error) => (error.InnerException as Win32Exception)?.NativeErrorCode ?? 0;

        /// <summary>The window of the installed program: make sure the service runs - start it, and only when this user
        /// may not, ask for the administrator's confirmation. Returns what was done, for the window's log ("" when it
        /// ran already).</summary>
        public static string EnsureRunning(string appName)
        {
            if (Status(appName) == ServiceControllerStatus.Running) return "";
            if (Start(appName, 30, out int error)) return "the service was started";
            if (error != AccessDenied) return "starting the service failed" + (error != 0 ? " (Windows error " + error + ")" : "");
            try
            {
                // not allowed to start it from here (an older registration): ask Windows for the administrator's rights
                using (var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--start-service") { UseShellExecute = true, Verb = "runas" }))
                    p?.WaitForExit(60000);
                return Status(appName) == ServiceControllerStatus.Running ? "the service was started as administrator"
                                                                           : "starting the service as administrator failed";
            }
            catch (Win32Exception e)
            {
                return e.NativeErrorCode == 1223 ? "starting the service needs the administrator's confirmation, which was declined"
                                                 : "asking for the administrator's rights failed (Windows error " + e.NativeErrorCode + ")";
            }
        }
    }

    /// <summary>The service itself: the shared host without a window, until Windows stops it.</summary>
    public sealed class HostService : ServiceBase
    {
        readonly Func<ManualResetEvent, int> run;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread worker;

        public HostService(string appName, Func<ManualResetEvent, int> run)
        {
            ServiceName = appName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
            this.run = run;
        }

        protected override void OnStart(string[] args)
        {
            // the instrument software comes first: the recorder only reads, and catches up on what it missed
            try { using (Process me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception) { }
            worker = new Thread(() =>
            {
                try { run(stop); }
                catch (Exception error) { Log.Error("the service failed", error); }
                // the host ended by itself (it could not start): let Windows know, so it restarts the service
                if (!stop.WaitOne(0)) { ExitCode = 1; Stop(); }
            }) { IsBackground = true, Name = "service" };
            worker.Start();
        }

        protected override void OnStop() => Finish();
        protected override void OnShutdown() => Finish();

        void Finish()
        {
            stop.Set();
            if (worker == null || worker == Thread.CurrentThread) return;   // it ended by itself and asked for this stop
            try { RequestAdditionalTime(30000); } catch (Exception) { }    // the watcher writes what it has before it ends
            worker.Join(30000);
        }
    }
}
