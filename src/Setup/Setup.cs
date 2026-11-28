// The installer of a Plate Kinetics Live program: one source for PlateKineticsLive-Setup.exe and PlateKineticsLive-Archimed-Setup.exe.
// build.ps1 embeds two resources: "setup.settings" (lines "key=value": name, exe, version, port) and "payload.zip" (the
// program's files as they are installed: the exe, the WebView2 DLLs, web\..., Help\..., the licence texts).
//
// The installed program runs as a Windows service (it records; the program's window only shows it) and is installed
// for all users of the PC, so the installer runs as administrator. Before anything is changed it checks that the PC
// has what the program needs: 64-bit Windows 10 or 11, the .NET Framework 4.8 and the WebView2 Runtime. An offline
// package carries Microsoft's installers of the last two beside the installer (redist\): what is missing is installed
// from there (signed by Microsoft, checked), silently; without them a clear message names Microsoft's download page
// (exit code 7). It installs by default into C:\Program Files\Plate Kinetics Live (BMG) - or (Archimed) -, but an
// installation stays where it is: where the program is installed already (under its name or its former one) it installs
// there; another folder moves it (below). It registers the program for the whole machine: its entry in the list of
// apps, shortcuts for all users (and one to its Experiments folder), the .pk experiment files (double-click: "<exe>
// --open <file>"), and its tray ("<exe> --tray" at every sign-in, for every user: notifications of finished and
// stopped runs), which it also starts in the installing user's session.
// The data folder PlateKineticsLive-data (SAAControl-data up to 2026-11-23) lives beside the program. The installer
// never changes what is in it; it sets its rights, also on an update (Permissions): the program folder as Program Files
// has it (users read and run it), Experiments where users may add, rename, move and delete experiments, System (hidden)
// that users only read, and System\users where each user may make his own folder for his window's data.
// To keep using an old program's data, install into its folder, whose one-file (Python) exe of the same name is then
// renamed to <name>-python-old.exe (never deleted).
// An update stops the service for the moment of copying (it catches up on what happened meanwhile when it starts
// again; should the update fail, the service is started - or registered - again before the error is reported) and
// asks the program's windows in its own session to close (they only show what the service records). While an
// instrument measures, the window first says that the recording pauses about 10 seconds and loses nothing (Continue or
// Cancel); the silent installer notes it. It does not wait for windows in other sessions (an installer started over SSH
// cannot reach the user's session): a file such a window holds - the exe, the WebView2 DLLs - is renamed aside to
// <file>.replaced-N, which the next installation or the uninstaller removes. A program that records by itself - the
// old program, a portable copy - stops the installation: it is not closed, the user closes it. The service counts as
// running once its watcher is going (GET /api/state answers).
//
// The program under its former name (SAAControl, SAAControl-Archimed: until 2026-11-23) is replaced in its folder: its
// service is stopped, the new program's service registered and started, and only once that one runs with its watcher
// going is the former program removed (service, files, shortcuts, entry); should anything fail before, the former
// program is put back and started again. Meanwhile the data folder keeps its former name (the program does not rename it
// while the former program's service is registered): once the former program is gone, the service is restarted once and
// the program renames the folder at that start.
//
// A move (--move-to, or another folder chosen in the window) puts the installed program - under its name or its former
// one - into a new, empty folder: the program's files are written there and its data folder is copied (its records,
// logs and settings; not the web caches), a first time while it still records, then - its service stopped - what
// changed meanwhile, and every copied file is checked against the original (size and SHA-256). The new folder's service
// is registered (to start only when asked, until the move holds) and must answer with every instrument the old one
// showed; then it starts with Windows, the old service is removed and the registrations follow the new folder. Any
// failure puts everything back: the new service and folder go, the old service runs again from the old folder. The old
// folder is never changed: it stays as a backup that its owner deletes. Recording pauses about 10-20 seconds; the
// program reads what it missed from the instrument software's own files.
//
// Command line:
//   (nothing)      a small window; its texts are Chinese with the English beneath
//   /S             silent: no window at all; exit code 0 = installed, else see Exit; one line goes into
//                  %TEMP%\<name>-setup.log and to the standard output (when the caller captures it)
//   /NODESKTOP     silent: no desktop shortcut where there is none (an existing one is kept)
//   /ANALYSISONLY  a Live program as the analysis window only: no service, no tray (the file analysis-only.mode in its
//                  folder says so from then on). Where the recorder's service is registered this removes it: refused
//                  while an instrument measures; the window asks first, /S takes it as an administrator's command.
//                  The marker file alone never removes a registered recorder (nor does /ANALYSISONLY with /RELAUNCH,
//                  which the program's updater gives because of that file): such a folder is installed in full
//   /FULL          the full installation whatever the folder's analysis-only.mode says (the instrument PCs' deploy
//                  script gives it: a stray marker file never turns a recorder into an analysis-only installation)
//   /RELAUNCH      silent: afterwards the program is started again as the signed-in user (the program's own updater
//                  gives it); after a failure the program that is in the folder, with --update-failed=<exit code>
//   --move-to [<folder>]   move the installed program into this folder (default: the standard one under Program Files)
//   /D=<folder>    install into this folder; the rest of the command line, quotes optional (the rule of NSIS installers)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SAAControl.Setup
{
    /// <summary>The installer's exit codes.</summary>
    static class Exit
    {
        public const int Ok = 0, Failed = 1, Running = 2, BadFolder = 3, BadInstaller = 4, NotInstalled = 5, ServiceFailed = 6, Missing = 7,
                         NoSpace = 8, MovedBack = 9;
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            bool noDesktop = args.Any(a => a.Equals("/NODESKTOP", StringComparison.OrdinalIgnoreCase));
            bool analysisOnlyArg = args.Any(a => a.Equals("/ANALYSISONLY", StringComparison.OrdinalIgnoreCase));
            bool fullArg = args.Any(a => a.Equals("/FULL", StringComparison.OrdinalIgnoreCase));
            bool relaunch = args.Any(a => a.Equals("/RELAUNCH", StringComparison.OrdinalIgnoreCase));
            string folderArgument = Options.FolderArgument(Environment.CommandLine);
            string moveArgument = Options.MoveTarget(args, out bool move);
            string name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            if (name.EndsWith("-Setup", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 6);
            try
            {
                if (!silent)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                }
                SetupSettings settings = SetupSettings.Load();
                name = settings.Name;
                if (!silent && args.Any(a => a == "/?" || a == "-h" || a == "--help"))
                {
                    MessageBox.Show(string.Format(T.Usage, settings.Name), settings.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return Exit.Ok;
                }
                Requirement missing = Requirement.Missing();
                if (missing != null) missing = Prerequisites.Provide(settings, missing, silent);
                if (missing != null)
                {
                    Report(settings.Name, "install FAILED (exit " + Exit.Missing + "): " + missing.English, silent);
                    if (!silent)
                    {
                        string text = missing.Chinese + "\n" + missing.English + (missing.Link != null ? "\n\n" + T.OpenPage : "");
                        if (MessageBox.Show(text, settings.DisplayName, missing.Link != null ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
                                MessageBoxIcon.Error) == DialogResult.Yes)
                            OpenLink(missing.Link);
                    }
                    return Exit.Missing;
                }
                Payload payload = Payload.Load(settings);
                if (silent)
                    return move ? SilentMove(settings, payload, moveArgument, noDesktop, analysisOnlyArg, fullArg, relaunch)
                                : Silent(settings, payload, folderArgument, noDesktop, analysisOnlyArg, fullArg, relaunch);

                string start = move ? moveArgument ?? Installer.StandardFolder(settings) : folderArgument ?? Installer.DefaultFolder(settings);
                using (var form = new SetupForm(settings, payload, start, analysisOnlyArg, fullArg))
                {
                    Application.Run(form);
                    return form.Installed ? Exit.Ok : Exit.NotInstalled;
                }
            }
            catch (Exception error)
            {
                int code = error is SetupException known ? known.Code : Exit.Failed;
                Report(name, "install FAILED (exit " + code + "): " + error.Message, silent);
                if (!silent)
                    MessageBox.Show(error is SetupException e ? e.Text : string.Format(T.Failed, error.Message), name,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return code;
            }
        }

        static int Silent(SetupSettings settings, Payload payload, string folderArgument, bool noDesktop, bool analysisOnlyArg, bool fullArg, bool relaunch)
        {
            string folder = folderArgument ?? Installer.DefaultFolder(settings);
            bool analysisOnly = settings.IsStandaloneAnalysis || (!fullArg && (analysisOnlyArg || File.Exists(Path.Combine(folder, InstallShared.AnalysisOnlyMarker))));
            // The marker file alone says "analysis only" while the recorder's service is registered: such a folder is a
            // recorder's with a stray marker, and it is installed in full (the full installation removes the marker).
            // Only /ANALYSISONLY typed by an administrator removes a recorder silently - with /RELAUNCH it comes from the
            // program's own updater, whose window read the same marker file.
            if (Installer.RecorderWins(settings, analysisOnly, analysisOnlyArg && !relaunch))
            {
                analysisOnly = false;
                InstallShared.Log(settings.Name, "the folder " + folder + " is marked analysis-only (" + InstallShared.AnalysisOnlyMarker + ") but the service " +
                                                 settings.Name + " is registered: installed in full, the recorder stays");
            }
            try
            {
                InstallResult result = Installer.Install(settings, payload, folder, noDesktop ? DesktopChoice.KeepOnly : DesktopChoice.Default, analysisOnly);
                if (!result.AnalysisOnly) StartTray(result);
                if (relaunch) RelaunchProgram(result);
                Detail(settings.Name, result);
                Report(settings.Name, result.Summary(settings), true);
                return (result.AnalysisOnly || result.Service) ? Exit.Ok : Exit.ServiceFailed;
            }
            catch (Exception error)
            {
                int code = error is SetupException known ? known.Code : Exit.Failed;
                string again = relaunch ? RelaunchAfterFailure(settings, folder, code) : "";
                Report(settings.Name, "install FAILED (exit " + code + "): " + error.Message + Installer.ServiceNote(error, false) + " [folder " + folder + "]" + again, true);
                return code;
            }
        }

        /// <summary>--move-to: the installed program into another folder (the standard one when none is given); into the
        /// folder it is in already: an ordinary update.</summary>
        static int SilentMove(SetupSettings settings, Payload payload, string target, bool noDesktop, bool analysisOnlyArg, bool fullArg, bool relaunch)
        {
            string folder = target ?? Installer.StandardFolder(settings);
            string installed = Installer.InstalledFolder(settings);
            if (installed == null || InstallShared.SameFolder(installed, folder)) return Silent(settings, payload, folder, noDesktop, analysisOnlyArg, fullArg, relaunch);
            try
            {
                InstallResult result = Mover.Move(settings, payload, folder, noDesktop ? DesktopChoice.KeepOnly : DesktopChoice.Default,
                                                  new SystemMoveHost(settings), new MoveOptions());
                StartTray(result);
                if (relaunch) RelaunchProgram(result);
                Detail(settings.Name, result);
                Report(settings.Name, result.Summary(settings), true);
                return Exit.Ok;
            }
            catch (Exception error)
            {
                int code = error is SetupException known ? known.Code : Exit.Failed;
                // (after a move that failed the program is where it was)
                string again = relaunch ? RelaunchAfterFailure(settings, installed, code) : "";
                Report(settings.Name, "move FAILED (exit " + code + "): " + error.Message + " [from " + installed + " to " + folder + "]" + again, true);
                return code;
            }
        }

        /// <summary>Launch the newly updated app as the signed-in shell user (used by /RELAUNCH after auto-update).</summary>
        public static void RelaunchProgram(InstallResult result)
        {
            try
            {
                if (result == null || string.IsNullOrEmpty(result.ExePath) || !File.Exists(result.ExePath)) return;
                InstallShared.StartAsShellUser(result.ExePath, "", result.Folder);
            }
            catch (Exception) { }
        }

        // what the program is told when it is started again after an update that failed (the programs read it:
        // Updater.UpdateFailedOption), followed by "=" and the installer's exit code
        const string UpdateFailedOption = "--update-failed";

        /// <summary>/RELAUNCH after an update that failed: the window that started the update has closed, so the
        /// program that is in the folder (the version from before; none in a new folder) is started again as the
        /// signed-in user and told the installer's exit code - its window says that the update failed. An argument
        /// without blanks: it needs no quotes on the command line the shell makes. Best effort, never throws; returns
        /// what was done, for the end of the log line.</summary>
        static string RelaunchAfterFailure(SetupSettings settings, string folderText, int code)
        {
            try
            {
                string folder = Installer.FullFolder(folderText);
                string exe = Path.Combine(folder, settings.Exe);
                // (the old one-file program of the same name does not know the argument)
                if (!File.Exists(exe) || !InstallShared.IsManagedExe(exe)) return "; no program in the folder to start again";
                string argument = UpdateFailedOption + "=" + code;
                return InstallShared.StartAsShellUser(exe, argument, folder)
                    ? "; the program was started again (" + argument + ")"
                    : "; the program was not started again (no desktop to start it on in this session)";
            }
            catch (Exception error) { return "; the program was not started again (" + error.Message + ")"; }
        }

        /// <summary>The tray, in the session of the user who installs (as that user, never as administrator); over SSH
        /// or as a system task there is none: the tray starts at the next sign-in.</summary>
        public static void StartTray(InstallResult result)
        {
            if (!result.Service) return;
            result.Tray = InstallShared.StartAsShellUser(result.ExePath, InstallShared.TrayOption, result.Folder)
                ? "started" : "starts at the next sign-in";
        }

        /// <summary>The longer lines of a run into the log, before its one-line summary (which callers read as the last line):
        /// the rights now on the folders, and for a move every file copied and checked.</summary>
        public static void Detail(string name, InstallResult result)
        {
            foreach (string line in result.Details) InstallShared.Log(name, "  " + line);
        }

        /// <summary>The line of this run into %TEMP%\name-setup.log, and to the standard output in silent mode.</summary>
        public static void Report(string name, string line, bool toOutput)
        {
            InstallShared.Log(name, line);
            if (!toOutput) return;
            try
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
            catch (Exception) { }
        }

        /// <summary>Opens a web page through Explorer: in the user's own browser, not with the installer's administrator
        /// rights (Explorer by its full path: a bare name would be looked for first in the installer's own folder).</summary>
        public static void OpenLink(string url)
        {
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                using (Process.Start(new ProcessStartInfo(explorer, "\"" + url + "\"") { UseShellExecute = false })) { }
            }
            catch (Exception) { }
        }
    }

    static class Options
    {
        /// <summary>The folder of "/D=", or null: the rest of the command line, quotes optional (as NSIS has it, /D=
        /// comes last); our own switches written after it by mistake are left out.</summary>
        public static string FolderArgument(string commandLine)
        {
            string rest = SkipProgram(commandLine ?? "");
            int at = -1;
            for (int i = 0; i + 3 <= rest.Length; i++)
                if (string.Compare(rest, i, "/D=", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i == 0 || rest[i - 1] == ' ' || rest[i - 1] == '\t' || rest[i - 1] == '"'))
                {
                    at = i;
                    break;
                }
            if (at < 0) return null;
            string value = rest.Substring(at + 3).Trim();
            int move = value.IndexOf(" --move-to", StringComparison.OrdinalIgnoreCase);
            if (move >= 0) value = value.Substring(0, move);
            for (bool again = true; again;)
            {
                again = false;
                foreach (string option in new[] { "/S", "/NODESKTOP", "/ANALYSISONLY", "/FULL", "/RELAUNCH" })
                    if (value.EndsWith(" " + option, StringComparison.OrdinalIgnoreCase) || value.EndsWith("\t" + option, StringComparison.OrdinalIgnoreCase))
                    {
                        value = value.Substring(0, value.Length - option.Length).TrimEnd();
                        again = true;
                    }
            }
            value = value.Trim().Trim('"').Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>"--move-to [folder]" or "--move-to=folder": move is true when it is there; the folder, or null for
        /// the standard one.</summary>
        public static string MoveTarget(string[] args, out bool move)
        {
            move = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--move-to=", StringComparison.OrdinalIgnoreCase))
                {
                    move = true;
                    string value = a.Substring(10).Trim().Trim('"').Trim();
                    return value.Length > 0 ? value : null;
                }
                if (!a.Equals("--move-to", StringComparison.OrdinalIgnoreCase)) continue;
                move = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("/") && !args[i + 1].StartsWith("--"))
                {
                    string value = args[i + 1].Trim().Trim('"').Trim();
                    return value.Length > 0 ? value : null;
                }
                return null;
            }
            return null;
        }

        static string SkipProgram(string commandLine)
        {
            string line = commandLine.TrimStart();
            if (line.StartsWith("\""))
            {
                int end = line.IndexOf('"', 1);
                return end < 0 ? "" : line.Substring(end + 1);
            }
            int space = line.IndexOfAny(new[] { ' ', '\t' });
            return space < 0 ? "" : line.Substring(space);
        }
    }

    sealed class SetupException : Exception
    {
        public readonly int Code;
        public readonly string Chinese;

        public SetupException(int code, string chinese, string english) : base(english)
        {
            Code = code;
            Chinese = chinese;
        }

        /// <summary>The message as the window shows it: Chinese, the English beneath.</summary>
        public string Text => Chinese + "\n" + Message;
    }

    sealed class SetupSettings
    {
        public string Name, Exe, Version, Mode;
        public int Port;
        public bool IsStandaloneAnalysis => string.Equals(Mode, "analysis", StringComparison.OrdinalIgnoreCase);
        public string DisplayName => InstallShared.DisplayName(Name);     // shortcuts, the list of apps, the texts
        public string Former => InstallShared.FormerName(Name);           // the name up to 2026-11-23 (SAAControl), or null

        public static SetupSettings Load()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (Stream stream = typeof(SetupSettings).Assembly.GetManifestResourceStream("setup.settings"))
            {
                if (stream == null) throw new SetupException(Exit.BadInstaller, string.Format(T.IncompleteZh, "setup.settings"), string.Format(T.IncompleteEn, "setup.settings"));
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    for (string line; (line = reader.ReadLine()) != null;)
                    {
                        int equals = line.IndexOf('=');
                        if (equals > 0) values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
                    }
            }
            var settings = new SetupSettings
            {
                Name = values.TryGetValue("name", out string name) ? name : "",
                Exe = values.TryGetValue("exe", out string exe) ? exe : "",
                Version = values.TryGetValue("version", out string version) ? version : "",
                Mode = values.TryGetValue("mode", out string mode) ? mode : "",
                Port = values.TryGetValue("port", out string port) && int.TryParse(port, out int number) ? number : 0,
            };
            if (settings.Name.Length == 0 || settings.Exe.Length == 0 || !InstallShared.IsSafeRelative(settings.Exe) || settings.Exe.Contains("\\"))
                throw new SetupException(Exit.BadInstaller, string.Format(T.IncompleteZh, "name, exe"), string.Format(T.IncompleteEn, "name, exe"));
            return settings;
        }
    }

    /// <summary>What the program needs on the PC, looked at before anything is changed.</summary>
    sealed class Requirement
    {
        public const string NetLink = "https://dotnet.microsoft.com/download/dotnet-framework/net48";
        public const string WebView2Link = "https://developer.microsoft.com/microsoft-edge/webview2/";
        public string Chinese, English, Link;
        public string Kind;             // "windows", "net48", "webview2"
        public string Shown;            // its name in texts

        /// <summary>What this PC lacks, or null: 64-bit Windows 10 or 11 (build 14393 or later; the programs are 64-bit
        /// programs), the .NET Framework 4.8 and the WebView2 Runtime (the program's window shows its page with it).</summary>
        public static Requirement Missing()
        {
            Version os = WindowsVersion();
            if (!Environment.Is64BitOperatingSystem || os.Major < 10 || (os.Major == 10 && os.Build < 14393))
                return new Requirement { Chinese = T.NeedWindowsZh, English = T.NeedWindowsEn, Kind = "windows", Shown = "Windows 10/11 (64-bit)" };
            if (NetRelease() < 528040)
                return new Requirement { Chinese = string.Format(T.NeedNetZh, NetLink), English = string.Format(T.NeedNetEn, NetLink), Link = NetLink, Kind = "net48", Shown = ".NET Framework 4.8" };
            if (!WebView2Present())
                return new Requirement { Chinese = string.Format(T.NeedWebView2Zh, WebView2Link), English = string.Format(T.NeedWebView2En, WebView2Link), Link = WebView2Link, Kind = "webview2", Shown = "WebView2 Runtime" };
            return null;
        }

        /// <summary>The version of Windows as it is (RtlGetVersion: Environment.OSVersion tells a program whose manifest
        /// does not name Windows 10 that it runs on Windows 8).</summary>
        static Version WindowsVersion()
        {
            try
            {
                var info = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
                if (RtlGetVersion(ref info) == 0) return new Version(info.Major, info.Minor, info.Build);
            }
            catch (Exception) { }
            return Environment.OSVersion.Version;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OsVersionInfo
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }

        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref OsVersionInfo info);

        /// <summary>The .NET Framework 4 installed: its Release number (528040 and up: 4.8), 0 for none.</summary>
        static int NetRelease()
        {
            try
            {
                using (RegistryKey machine = InstallShared.Machine())
                using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return key?.GetValue("Release") is int release ? release : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The WebView2 Runtime (Evergreen): per machine or per user.</summary>
        public static bool WebView2Present()
        {
            const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            var places = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\" + client),
                Tuple.Create(Registry.CurrentUser, @"Software\" + client),
            };
            foreach (var place in places)
            {
                try
                {
                    using (RegistryKey key = place.Item1.OpenSubKey(place.Item2))
                        if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
                }
                catch (Exception) { }
            }
            return false;
        }
    }

    /// <summary>Microsoft's installers of the .NET Framework 4.8 and the WebView2 Runtime, which an offline package carries
    /// in redist\ beside the installer (build.ps1 -Offline): what the PC lacks is installed from them - only when they
    /// carry Microsoft's valid signature -, silently (the window asks first: it takes minutes, and not while an
    /// instrument measures). Nothing is ever downloaded.</summary>
    static class Prerequisites
    {
        public static string Folder(string setupFolder) => Path.Combine(setupFolder, "redist");

        /// <summary>The redistributable for what is missing, in redist\ or beside the installer; null for none.</summary>
        public static string Find(string kind, string setupFolder)
        {
            string[] patterns;
            if (kind == "net48") patterns = new[] { "ndp48-x86-x64-allos-*.exe", "ndp481-x86-x64-allos-*.exe" };
            else if (kind == "webview2") patterns = new[] { "MicrosoftEdgeWebView2RuntimeInstallerX64.exe", "MicrosoftEdgeWebView2RuntimeInstaller*X64*.exe" };
            else return null;
            foreach (string dir in new[] { Folder(setupFolder), setupFolder })
                foreach (string pattern in patterns)
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        string found = Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
                        if (found != null) return found;
                    }
                    catch (Exception) { }
            return null;
        }

        /// <summary>Installs what is missing from the package (the window asks first); returns what is still missing
        /// afterwards, or null.</summary>
        public static Requirement Provide(SetupSettings settings, Requirement missing, bool silent)
        {
            string setupFolder = Path.GetDirectoryName(Application.ExecutablePath);
            for (int round = 0; missing != null && round < 2; round++)
            {
                string file = Find(missing.Kind, setupFolder);
                if (file == null) return missing;
                if (!silent && MessageBox.Show(string.Format(T.PrerequisiteAskZh, missing.Shown) + "\n\n" + string.Format(T.PrerequisiteAskEn, missing.Shown),
                        settings.DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                    return missing;
                string problem = Run(missing.Kind, file, silent, out bool restart);
                Program.Report(settings.Name, "prerequisite " + missing.Shown + " from " + file + ": " + (problem ?? "installed") +
                                              (restart ? " (Windows asks for a restart to finish it)" : ""), false);
                if (problem != null)
                {
                    if (!silent)
                        MessageBox.Show(string.Format(T.PrerequisiteFailedZh, missing.Shown, problem) + "\n" + string.Format(T.PrerequisiteFailedEn, missing.Shown, problem),
                            settings.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return missing;
                }
                Requirement next = Requirement.Missing();
                if (next != null && next.Kind == missing.Kind) return next;     // installed, but not there yet (a restart first)
                missing = next;
            }
            return missing;
        }

        /// <summary>Runs one redistributable silently (the .NET Framework with its progress bar when the window is used);
        /// null when it installed, else why not. restart: Windows wants a restart to finish (the program usually runs
        /// before that all the same).</summary>
        static string Run(string kind, string file, bool silent, out bool restart)
        {
            restart = false;
            if (!SignedByMicrosoft(file)) return Path.GetFileName(file) + " does not carry a valid signature of Microsoft";
            string arguments = kind == "net48" ? (silent ? "/q /norestart" : "/passive /norestart") : "/silent /install";
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = silent }))
                {
                    if (!p.WaitForExit(40 * 60 * 1000)) return "it did not finish within 40 minutes";
                    if (p.ExitCode == 3010 || p.ExitCode == 1641)
                    {
                        restart = true;
                        return null;
                    }
                    return p.ExitCode == 0 ? null : "exit code " + p.ExitCode;
                }
            }
            catch (Exception error) { return error.Message; }
        }

        /// <summary>True when the file carries a valid Authenticode signature (checked by Windows, offline) of Microsoft
        /// Corporation.</summary>
        public static bool SignedByMicrosoft(string file)
        {
            IntPtr fileInfo = IntPtr.Zero;
            try
            {
                var info = new WinTrustFileInfo { cbStruct = Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = file };
                fileInfo = Marshal.AllocHGlobal(info.cbStruct);
                Marshal.StructureToPtr(info, fileInfo, false);
                var data = new WinTrustData
                {
                    cbStruct = Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,                     // WTD_UI_NONE
                    fdwRevocationChecks = 0,            // WTD_REVOKE_NONE: the PC may be offline
                    dwUnionChoice = 1,                  // WTD_CHOICE_FILE
                    pFile = fileInfo,
                    dwProvFlags = 0x1000,               // WTD_CACHE_ONLY_URL_RETRIEVAL: nothing is fetched
                };
                if (WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) != 0) return false;
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(file)))
                    return certificate.Subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception) { return false; }
            finally
            {
                if (fileInfo != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(fileInfo, typeof(WinTrustFileInfo));
                    Marshal.FreeHGlobal(fileInfo);
                }
            }
        }

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public int cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public int dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);
    }

    sealed class PayloadFile
    {
        public readonly string Path;      // relative, '\' between folders
        public readonly byte[] Data;

        public PayloadFile(string path, byte[] data)
        {
            Path = path;
            Data = data;
        }
    }

    /// <summary>The program's files, read from the embedded zip in full before anything is written.</summary>
    sealed class Payload
    {
        public readonly List<PayloadFile> Files = new List<PayloadFile>();

        public long Size => Files.Sum(f => (long)f.Data.Length);

        public PayloadFile Find(string rel) => Files.FirstOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));

        public static Payload Load(SetupSettings settings)
        {
            var payload = new Payload();
            using (Stream stream = typeof(Payload).Assembly.GetManifestResourceStream("payload.zip"))
            {
                if (stream == null) throw new SetupException(Exit.BadInstaller, string.Format(T.IncompleteZh, "payload.zip"), string.Format(T.IncompleteEn, "payload.zip"));
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string rel = entry.FullName.Replace('/', '\\');
                        if (rel.EndsWith("\\")) continue;                       // a folder
                        if (!InstallShared.IsSafeRelative(rel) || rel.EndsWith(InstallShared.ManifestSuffix, StringComparison.OrdinalIgnoreCase))
                            throw new SetupException(Exit.BadInstaller, string.Format(T.IncompleteZh, rel), string.Format(T.IncompleteEn, rel));
                        using (Stream data = entry.Open())
                        using (var copy = new MemoryStream())
                        {
                            data.CopyTo(copy);
                            payload.Files.Add(new PayloadFile(rel, copy.ToArray()));
                        }
                    }
            }
            if (payload.Find(settings.Exe) == null)
                throw new SetupException(Exit.BadInstaller, string.Format(T.IncompleteZh, settings.Exe), string.Format(T.IncompleteEn, settings.Exe));
            return payload;
        }
    }

    /// <summary>What is in a folder before installing into it.</summary>
    sealed class FolderInfo
    {
        public bool Exists;
        public bool OurInstall;         // installed by this installer before: its list of files is there
        public bool NewProgram;         // the program's exe there is a .NET program: the new program
        public bool OldProgram;         // the program's exe there is not: the old one-file (Python) build
        public bool FormerProgram;      // the program under its former name (SAAControl) is there: its exe or its list of files
        public bool DataFolder;         // PlateKineticsLive-data (or an earlier name of it) is there
        public string OtherProgram;     // another of our programs is installed there (its list of files)
    }

    enum DesktopChoice
    {
        Default,        // silent: on a first installation create it, on an update keep it if it is there
        KeepOnly,       // silent with /NODESKTOP: keep it if it is there, never create it
        Create,         // window, box ticked
        Remove,         // window, box not ticked: remove ours if it is there
    }

    sealed class InstallResult
    {
        public string Folder, ExePath, RenamedOld, ExeHash, DataFolder;
        public bool Updated, Desktop, Service, AnalysisOnly;
        public bool FormerReplaced;     // the program under its former name was there and is replaced
        public string PreviousExe;      // the program file of the version installed before, kept aside (".replaced-N")
        public string FormerRemoved;    // what became of the program under its former name (SAAControl)
        public bool Moved;              // a move: the program came from another folder
        public string MovedFrom;        // ... that folder, left as it was
        public int MovedFiles;          // the data files copied and checked
        public long MovedBytes;
        public double PausedSeconds = -1;   // how long nothing recorded (the service stopped until the new one answered)
        public bool Recovery;           // Windows restarts the service after a failure
        public string Tray;             // what became of the tray in this session
        public readonly List<string> Measuring = new List<string>();     // instruments measuring when it began
        public readonly List<string> Details = new List<string>();       // longer lines for the log (rights, copied files)
        public readonly List<string> KeptAside = new List<string>();
        public readonly List<string> Removed = new List<string>();
        public readonly List<string> Replaced = new List<string>();      // renamed aside: an open window held them
        public readonly List<string> OpenWindows = new List<string>();   // the program's windows still open, "id (session n)"
        public readonly List<string> Problems = new List<string>();      // what did not work out, while the program runs

        public string Summary(SetupSettings settings)
        {
            var line = new StringBuilder(Moved
                ? "move ok: " + settings.Name + " " + settings.Version + " from " + MovedFrom + " to " + Folder + " (" + MovedFiles + " data files, " +
                  (MovedBytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB, copied and checked: size and SHA-256; exe sha256 " + ExeHash +
                  "); the former folder is left as it was"
                : "install ok: " + settings.Name + " " + settings.Version + " into " + Folder + " (" + (Updated ? "update" : "new") + "; exe sha256 " + ExeHash + ")");
            if (Measuring.Count > 0) line.Append("; measuring when it began: " + string.Join(", ", Measuring));
            if (PausedSeconds >= 0) line.Append("; recording paused " + Math.Round(PausedSeconds) + " s (the program reads what it missed from the instrument software's files)");
            if (RenamedOld != null) line.Append("; old program renamed to " + RenamedOld);
            if (KeptAside.Count > 0) line.Append("; kept as .old: " + string.Join(", ", KeptAside));
            if (Removed.Count > 0) line.Append("; removed (no longer part of the program): " + string.Join(", ", Removed));
            if (Replaced.Count > 0) line.Append("; in use, renamed aside (removed by the next installation): " + string.Join(", ", Replaced));
            if (OpenWindows.Count > 0) line.Append("; program windows still open: " + string.Join(", ", OpenWindows));
            line.Append("; desktop shortcut: " + (Desktop ? "yes" : "no"));
            if (DataFolder != null) line.Append("; data folder: " + DataFolder);
            if (AnalysisOnly) line.Append("; mode: analysis-only");
            else
            {
                line.Append("; service: " + (Service ? "running" : "NOT running"));
                if (Service) line.Append(", restarted by Windows after a failure: " + (Recovery ? "yes" : "NO"));
            }
            if (Tray != null) line.Append("; tray: " + Tray);
            if (FormerRemoved != null) line.Append("; " + FormerRemoved);
            if (Problems.Count > 0) line.Append("; WARNING: " + string.Join("; ", Problems));
            return line.ToString();
        }
    }

    static class Installer
    {
        /// <summary>Where the program is installed already (under its name, else its former one), else C:\Program
        /// Files\Plate Kinetics Live (BMG) - or (Archimed).</summary>
        public static string DefaultFolder(SetupSettings settings) => InstalledFolder(settings) ?? StandardFolder(settings);

        /// <summary>C:\Program Files\Plate Kinetics Live (BMG) - or (Archimed): the 64-bit Program Files in any case.</summary>
        public static string StandardFolder(SetupSettings settings)
        {
            string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");      // the 64-bit one in any case
            if (string.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles)) programFiles = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Program Files");
            return Path.Combine(programFiles, InstallShared.DefaultFolderName(settings.Name));
        }

        /// <summary>The folder the program is installed in now - under its name, else under its former one -: the folder
        /// its service runs it from, else the one its entry in the list of apps names when the program, its list of
        /// files or its data are there; null for none.</summary>
        public static string InstalledFolder(SetupSettings settings)
        {
            foreach (string name in new[] { settings.Name, settings.Former })
            {
                if (name == null) continue;
                // (the folder of a move that did not hold is not it: the program under its former name runs elsewhere)
                string exe = InstallShared.ServiceExe(name);
                if (exe != null && File.Exists(exe) && !Mover.UnfinishedMoveInto(settings, Path.GetDirectoryName(exe), InstallShared.ServiceExe))
                    return Path.GetDirectoryName(exe);
                string registered = InstallShared.RegisteredFolder(name);
                if (registered != null && Directory.Exists(registered) && !Mover.UnfinishedMoveInto(settings, registered, InstallShared.ServiceExe) &&
                    (File.Exists(Path.Combine(registered, name + ".exe")) || File.Exists(InstallShared.ManifestPath(registered, name)) ||
                     Directory.Exists(InstallShared.DataFolderIn(registered))))
                    return registered;
            }
            return null;
        }

        /// <summary>The folder the program is installed in now, when that is not the given one; else null. An
        /// installation stays where it is - its run records are in its data folder there -: another folder is a move.</summary>
        public static string InstalledElsewhere(SetupSettings settings, string folder)
        {
            string installed = InstalledFolder(settings);
            return installed != null && !InstallShared.SameFolder(installed, folder) ? installed : null;
        }

        /// <summary>The full path of a folder typed or given; a SetupException for one we cannot install into.</summary>
        public static string FullFolder(string text)
        {
            text = (text ?? "").Trim().Trim('"').Trim();
            string full;
            try
            {
                if (text.Length == 0 || !Path.IsPathRooted(text)) throw new ArgumentException();
                full = Path.GetFullPath(text).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                throw new SetupException(Exit.BadFolder, T.BadFolderZh, T.BadFolderEn);
            }
            string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\', '/');
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) throw new SetupException(Exit.BadFolder, T.RootZh, T.RootEn);
            if (File.Exists(full)) throw new SetupException(Exit.BadFolder, T.IsFileZh, T.IsFileEn);
            return full;
        }

        /// <summary>The folder to show for one picked with "Browse": the picked one when it holds the program (old, new
        /// or under its former name) or its data, or is empty, or has the program's name; else a folder of the program's
        /// name in it (Program Files: Program Files\Plate Kinetics Live (BMG)).</summary>
        public static string ChosenFolder(SetupSettings settings, string picked)
        {
            try
            {
                FolderInfo info = Inspect(settings, picked);
                bool empty = !Directory.EnumerateFileSystemEntries(picked).Any();
                string leaf = Path.GetFileName(picked.TrimEnd('\\', '/'));
                if (info.OurInstall || info.NewProgram || info.OldProgram || info.FormerProgram || info.DataFolder || empty ||
                    leaf.Equals(settings.Name, StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals(InstallShared.DefaultFolderName(settings.Name), StringComparison.OrdinalIgnoreCase))
                    return picked;
                return Path.Combine(picked, InstallShared.DefaultFolderName(settings.Name));
            }
            catch (Exception)
            {
                return picked;
            }
        }

        public static FolderInfo Inspect(SetupSettings settings, string folder)
        {
            var info = new FolderInfo { Exists = Directory.Exists(folder) };
            if (!info.Exists) return info;
            info.OurInstall = File.Exists(InstallShared.ManifestPath(folder, settings.Name));
            string exe = Path.Combine(folder, settings.Exe);
            if (File.Exists(exe))
            {
                if (InstallShared.IsManagedExe(exe)) info.NewProgram = true;
                else info.OldProgram = true;
            }
            string former = settings.Former;
            info.FormerProgram = former != null &&
                                 (File.Exists(InstallShared.ManifestPath(folder, former)) || File.Exists(Path.Combine(folder, former + ".exe")));
            info.DataFolder = Directory.Exists(Path.Combine(folder, InstallShared.DataFolder)) ||
                              InstallShared.OldDataFolders.Any(n => Directory.Exists(Path.Combine(folder, n)));
            foreach (string list in Directory.GetFiles(folder, "*" + InstallShared.ManifestSuffix))
            {
                string other = Path.GetFileName(list);
                if (!other.EndsWith(InstallShared.ManifestSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                other = other.Substring(0, other.Length - InstallShared.ManifestSuffix.Length);
                if (other.Length > 0 && !other.Equals(settings.Name, StringComparison.OrdinalIgnoreCase) && !other.Equals(former ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    info.OtherProgram = other;
                    break;
                }
            }
            return info;
        }

        /// <summary>Why the installation cannot go on (Chinese, English), or null: a copy of the program that records by
        /// itself is running - the old program, a portable copy, the program started without its service - which the
        /// user closes first. (The service itself and the windows that only show it are dealt with by the installation.)</summary>
        public static Tuple<string, string> Running(SetupSettings settings)
        {
            if (ServiceRunning(settings.Name)) return null;
            string answer = Ping(settings);
            if (answer == "closing") return Tuple.Create("\u6b63\u5728\u5173\u95ed", "it is closing");
            if (answer == "running") return Tuple.Create("\u7aef\u53e3 " + settings.Port + " \u4e0a\u6709\u5b83\u7684\u56de\u5e94", "it answers on port " + settings.Port);
            return null;
        }

        public static bool ServiceRunning(string name)
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(name))
                    return sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped;
            }
            catch (Exception) { return false; }      // not installed
        }

        static bool ServiceStopped(string name)
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(name))
                    return sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped;
            }
            catch (Exception) { return false; }      // not installed: nothing to tell
        }

        /// <summary>Stops the program's service (if it runs) for the copying; true when it was running. One that does not
        /// stop in time is started again, and the installation stops with nothing changed (Exit.Running).</summary>
        public static bool StopService(string name, int seconds = 90)
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(name))
                {
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) return false;
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(seconds));
                    return true;
                }
            }
            catch (InvalidOperationException) { return false; }   // not installed
            catch (System.ServiceProcess.TimeoutException)
            {
                Tuple<string, string> note = StartAgain(name);
                throw WithNote(new SetupException(Exit.Running, string.Format(T.NotStoppedZh, name, seconds), string.Format(T.NotStoppedEn, name, seconds)), note);
            }
        }

        /// <summary>Starts the service (once a stop under way has ended) and waits until it runs, for up to the seconds
        /// given. False when it does not run then; state says what it is instead.</summary>
        public static bool StartService(string name, int seconds, out string state)
        {
            var waited = Stopwatch.StartNew();
            TimeSpan Left() => TimeSpan.FromSeconds(Math.Max(1, seconds - waited.Elapsed.TotalSeconds));
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(name))
                {
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.StopPending)
                        try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, Left()); }
                        catch (System.ServiceProcess.TimeoutException) { }
                    sc.Refresh();
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) sc.Start();
                    try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, Left()); }
                    catch (System.ServiceProcess.TimeoutException) { }
                    sc.Refresh();
                    state = sc.Status.ToString();
                    return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                }
            }
            catch (Exception error)
            {
                state = error.Message;
                return false;
            }
        }

        /// <summary>The service stopped for the copying started again (waiting up to 30 s until it runs): what came of
        /// it, Chinese and English.</summary>
        static Tuple<string, string> StartAgain(string name)
        {
            return StartService(name, 30, out string state)
                ? Tuple.Create(T.ServiceRunsZh, T.ServiceRunsEn)
                : Tuple.Create(string.Format(T.ServiceDownZh, state), string.Format(T.ServiceDownEn, state));
        }

        /// <summary>After a failed update, the recorder as it was: the service started again when it is still
        /// registered, else registered again for the program file in the folder (when that is the new .NET program).
        /// What was done, Chinese and English.</summary>
        static Tuple<string, string> RestoreService(SetupSettings settings, string exe, string previousExe)
        {
            Tuple<string, string> note = TryRestore(settings, exe, out bool runs);
            if (runs || previousExe == null || !File.Exists(previousExe)) return note;
            // the new program does not run (quarantined, it fails at once, its watcher does not start): the program file
            // of the version installed before goes back, and the service is started again with it
            try
            {
                try { StopService(settings.Name); } catch (Exception) { }
                if (File.Exists(exe)) { File.SetAttributes(exe, FileAttributes.Normal); string failed = InstallShared.ReplacedName(exe); Retry(() => File.Move(exe, failed)); }
                Retry(() => File.Move(previousExe, exe));
                Tuple<string, string> again = TryRestore(settings, exe, out runs);
                return Tuple.Create(T.PreviousBackZh + " " + again.Item1, T.PreviousBackEn + " " + again.Item2);
            }
            catch (Exception error)
            {
                return Tuple.Create(note.Item1 + " (" + error.Message + ")", note.Item2 + " (" + error.Message + ")");
            }
        }

        static Tuple<string, string> TryRestore(SetupSettings settings, string exe, out bool runs)
        {
            runs = false;
            try
            {
                if (InstallShared.ServiceExe(settings.Name) != null)
                {
                    runs = StartService(settings.Name, 30, out string now);
                    if (runs && (now = WaitReady(settings, Path.GetDirectoryName(exe), 120)) != null) runs = false;
                    return runs ? Tuple.Create(T.ServiceRunsZh, T.ServiceRunsEn)
                                : Tuple.Create(string.Format(T.ServiceDownZh, now), string.Format(T.ServiceDownEn, now));
                }
                if (!InstallShared.IsManagedExe(exe))
                    return Tuple.Create(string.Format(T.ServiceDownZh, "not registered"), string.Format(T.ServiceDownEn, "not registered"));
                string problem = RegisterService(settings, exe);
                runs = problem == null;
                if (runs) return Tuple.Create(T.ServiceAgainZh, T.ServiceAgainEn);
                string why = "registering it again: " + problem;
                return Tuple.Create(string.Format(T.ServiceDownZh, why), string.Format(T.ServiceDownEn, why));
            }
            catch (Exception error)
            {
                return Tuple.Create(string.Format(T.ServiceDownZh, error.Message), string.Format(T.ServiceDownEn, error.Message));
            }
        }

        // what was done about the service after a failure travels with the error to the report (Exception.Data)
        const string NoteZh = "SAAControl.Setup.service-zh", NoteEn = "SAAControl.Setup.service-en";

        static Exception WithNote(Exception error, Tuple<string, string> note)
        {
            try
            {
                error.Data[NoteZh] = note.Item1;
                error.Data[NoteEn] = note.Item2;
            }
            catch (Exception) { }
            return error;
        }

        /// <summary>What was done about the service after this error, as a sentence that follows the error's message
        /// (" The service runs."), or "".</summary>
        public static string ServiceNote(Exception error, bool chinese)
        {
            try { return error.Data[chinese ? NoteZh : NoteEn] is string note && note.Length > 0 ? " " + note : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Registers the installed program as the service and starts it ("exe --install-service"; with
        /// demandStart it starts only when asked, until a move holds), then waits until it answers on its port and its
        /// watcher is going. Null when it runs, else what went wrong.</summary>
        public static string RegisterService(SetupSettings settings, string exe, bool demandStart = false)
        {
            var psi = new ProcessStartInfo(exe, "--install-service" + (demandStart ? " --demand-start" : ""))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            Process started;
            try { started = Process.Start(psi); }
            catch (Exception error) { return "the program could not be started (" + error.Message + ")"; }      // quarantined, say
            using (Process p = started)
            {
                Task<string> output = p.StandardOutput.ReadToEndAsync(), errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(180000)) return "--install-service did not finish";
                if (p.ExitCode != 0) return "--install-service exit " + p.ExitCode + ": " + (output.Result + " " + errors.Result).Trim();
            }
            var waited = Stopwatch.StartNew();
            while (Ping(settings) != "running")
            {
                if (waited.Elapsed.TotalSeconds >= 60) return "the service runs but does not answer on port " + settings.Port + " yet";
                Thread.Sleep(500);
            }
            // it answers: its watcher must be going too (a program that cannot look at the instruments stops again)
            return WaitReady(settings, Path.GetDirectoryName(exe), 300);
        }

        /// <summary>Waits until the program's watcher is going: GET /api/state answers (with the token the program keeps
        /// in its data folder) and does not say "starting". Null when it does within the seconds given, else why not. A
        /// service that has stopped and is not started again (by Windows, after a failure) within 25 s has failed.
        /// serviceName: the service that runs it (the program's own when null).</summary>
        public static string WaitReady(SetupSettings settings, string folder, int seconds, string serviceName = null)
        {
            var waited = Stopwatch.StartNew();
            Stopwatch stopped = null;
            while (true)
            {
                string state = StateNow(settings.Port, folder);
                if (state == "ready") return null;
                if (!ServiceStopped(serviceName ?? settings.Name)) stopped = null;
                else if (stopped == null) stopped = Stopwatch.StartNew();
                else if (stopped.Elapsed.TotalSeconds > 25) return "the service stopped (" + state + ")";
                if (waited.Elapsed.TotalSeconds >= seconds) return "the service did not get its watcher going within " + seconds + " s (" + state + ")";
                Thread.Sleep(1000);
            }
        }

        /// <summary>One look at GET /api/state on the port: "ready", "starting", or what stands in the way.</summary>
        public static string StateNow(int port, string folder)
        {
            ReadState(port, folder, out string status);
            return status;
        }

        /// <summary>GET /api/state on the port, when it answers with a state (status "ready" or "starting"); else null,
        /// and status says what stands in the way. The token the request needs is the one the program keeps in the
        /// folder's data folder (its name now, or an earlier one; System\api-token or, before the program moved it,
        /// api-token), else the one its page carries.</summary>
        public static Dictionary<string, object> ReadState(int port, string folder, out string status)
        {
            status = "no answer";
            var tried = new List<string>();
            var tokens = new List<Func<string>>();
            foreach (string data in new[] { InstallShared.DataFolder }.Concat(InstallShared.OldDataFolders))
            {
                string dir = folder == null ? null : Path.Combine(folder, data);
                tokens.Add(() => dir == null ? null : File.ReadAllText(Path.Combine(dir, InstallShared.SystemFolder, "api-token")).Trim());
                tokens.Add(() => dir == null ? null : File.ReadAllText(Path.Combine(dir, "api-token")).Trim());
            }
            tokens.Add(() => PageToken(port));
            foreach (Func<string> source in tokens)
            {
                string token;
                try { token = source(); }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(token) || tried.Contains(token)) continue;
                tried.Add(token);
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/state");
                    request.Proxy = null;
                    request.Timeout = 5000;
                    request.ReadWriteTimeout = 5000;
                    request.Headers["X-SAA-Token"] = token;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        if (!(Json.Parse(reader.ReadToEnd()) is Dictionary<string, object> state))
                        {
                            status = "an answer that is not a state";
                            return null;
                        }
                        status = state.TryGetValue("starting", out object starting) && Truthy(starting) ? "starting" : "ready";
                        return state;
                    }
                }
                catch (WebException error) when (error.Response is HttpWebResponse http)
                {
                    using (http)
                    {
                        if ((int)http.StatusCode == 404)
                        {
                            status = "starting";      // it answers before its watcher is going
                            return null;
                        }
                        status = "answer " + (int)http.StatusCode;              // 403: another token
                    }
                }
                catch (Exception error)
                {
                    status = "no answer (" + error.Message + ")";
                    return null;
                }
            }
            return null;
        }

        /// <summary>The keys of the instruments a state lists (null for no state), and those that measure now.</summary>
        public static List<string> Instruments(Dictionary<string, object> state, out List<string> measuring)
        {
            measuring = new List<string>();
            if (state == null) return null;
            var keys = new List<string>();
            if (state.TryGetValue("instruments", out object list) && list is List<object> instruments)
                foreach (Dictionary<string, object> instrument in instruments.OfType<Dictionary<string, object>>())
                {
                    string key = instrument.TryGetValue("key", out object k) && k != null ? Convert.ToString(k) : "";
                    if (key.Length == 0) continue;
                    keys.Add(key);
                    if (instrument.TryGetValue("state", out object s) && s as string == "measuring") measuring.Add(key);
                }
            return keys;
        }

        /// <summary>The instruments that measure now, as the program that answers on the port says; none when it does not
        /// answer.</summary>
        public static List<string> MeasuringNow(SetupSettings settings, string folder)
        {
            try
            {
                Instruments(ReadState(settings.Port, folder, out _), out List<string> measuring);
                return measuring;
            }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>The API token the program's page carries (its meta tag "saa-token"), or null.</summary>
        static string PageToken(int port)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                request.Proxy = null;
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string page = reader.ReadToEnd();
                    const string mark = "saa-token\" content=\"";
                    int at = page.IndexOf(mark, StringComparison.Ordinal);
                    int end = at < 0 ? -1 : page.IndexOf('"', at + mark.Length);
                    return end < 0 ? null : page.Substring(at + mark.Length, end - at - mark.Length);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The program's own test for another instance: GET /api/ping on its port, the answer's "app" must be
        /// the program's name. "running", "closing", or null for none.</summary>
        static string Ping(SetupSettings settings)
        {
            if (settings.Port <= 0 || settings.Port > 65535) return null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + settings.Port + "/api/ping");
                request.Proxy = null;
                request.Timeout = 2000;
                request.ReadWriteTimeout = 2000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    if (!(Json.Parse(reader.ReadToEnd()) is Dictionary<string, object> answer) ||
                        !(answer.TryGetValue("app", out object app) && app is string text && text == settings.Name))
                        return null;
                    return answer.TryGetValue("closing", out object closing) && Truthy(closing) ? "closing" : "running";
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool Truthy(object value)
        {
            switch (value)
            {
                case null: return false;
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0;
                case string s: return s.Length > 0;
                case System.Collections.ICollection c: return c.Count > 0;
                default: return true;
            }
        }

        /// <summary>True when "analysis only" comes from the folder's marker file alone (asked: /ANALYSISONLY was given
        /// by an administrator) although the program's service is registered: a recorder with a stray marker file, which
        /// stays a recorder.</summary>
        public static bool RecorderWins(SetupSettings settings, bool analysisOnly, bool asked) =>
            analysisOnly && !settings.IsStandaloneAnalysis && !asked && InstallShared.ServiceRegistered(settings.Name);

        /// <summary>"Analysis only" where the recorder's service is registered removes that service - which would end a
        /// run in progress for good (nothing reads the instrument any more). The refusal while these instruments measure.</summary>
        public static SetupException MeasuringKeepsRecorder(List<string> measuring)
        {
            string names = string.Join(", ", measuring);
            return new SetupException(Exit.Running, string.Format(T.MeasuringNoSwitchZh, names), string.Format(T.MeasuringNoSwitchEn, names));
        }

        public static InstallResult Install(SetupSettings settings, Payload payload, string folderText, DesktopChoice desktop, bool analysisOnly = false)
        {
            if (settings.IsStandaloneAnalysis) analysisOnly = true;
            string folder = FullFolder(folderText);
            FolderInfo info = Inspect(settings, folder);
            if (info.OtherProgram != null)
                throw new SetupException(Exit.BadFolder, string.Format(T.OtherProgramZh, info.OtherProgram), string.Format(T.OtherProgramEn, info.OtherProgram));
            string elsewhere = InstalledElsewhere(settings, folder);
            if (elsewhere != null)
            {
                // (an analysis-only installation is never moved: it is not sent to --move-to)
                Tuple<string, string> never = Mover.NeverMoved(settings, new SystemMoveHost(settings));
                if (never != null) throw new SetupException(Exit.BadFolder, never.Item1, never.Item2);
                throw new SetupException(Exit.BadFolder, string.Format(T.ElsewhereZh, settings.DisplayName, elsewhere), string.Format(T.ElsewhereEn, settings.DisplayName, elsewhere));
            }
            Tuple<string, string> running = Running(settings);
            if (running != null)
                throw new SetupException(Exit.Running, string.Format(T.RunningZh, settings.DisplayName, running.Item1), string.Format(T.RunningEn, settings.DisplayName, running.Item2));

            // The program under its former name (SAAControl, until 2026-11-23) is replaced: its service is stopped here
            // and removed - with its files, shortcuts and entry in the list of apps - only once the program under the
            // new name runs. Its desktop shortcut decides whether the new program gets one: read before anything changes.
            string former = analysisOnly ? null : settings.Former;
            bool formerDesktop = former != null && info.FormerProgram && FormerDesktopLink(former, folder) != null;

            // what measures now: the recording pauses for the moment of the update (the window asked first; noted here)
            List<string> measuring = settings.IsStandaloneAnalysis ? new List<string>() : MeasuringNow(settings, folder);

            // "Analysis only" for a Live program whose service is registered removes the recorder: not while an
            // instrument measures (that run would never be recorded to its end). Refused here, before anything is
            // stopped, so that it holds for the window and for /S alike.
            if (analysisOnly && !settings.IsStandaloneAnalysis && measuring.Count > 0 && InstallShared.ServiceRegistered(settings.Name))
                throw MeasuringKeepsRecorder(measuring);

            // The services are stopped for the copying (each catches up on what happened meanwhile when it starts
            // again): the former program's first, then the program's own. One that does not stop in time is started
            // again and nothing is changed (Exit.Running). Should the installation fail from here on, the program's
            // service is started again - or registered again, when it is gone -, or the former program is put back,
            // before the error is reported, so that the PC goes on recording.
            var paused = Stopwatch.StartNew();
            bool formerRegistered = former != null && InstallShared.ServiceRegistered(former);
            bool formerWasRunning = formerRegistered && StopService(former);
            bool serviceWasRunning = false;
            if (!settings.IsStandaloneAnalysis)
            {
                try { serviceWasRunning = StopService(settings.Name); }
                catch (Exception)
                {
                    if (formerWasRunning) StartService(former, 30, out _);       // back as it was
                    throw;
                }
            }
            bool dataUnderFormerName = !Directory.Exists(Path.Combine(folder, InstallShared.DataFolder)) &&
                                       Directory.Exists(Path.Combine(folder, InstallShared.OldDataFolders[0]));
            InstallResult result = null;
            try
            {
                // something else answers on the port: a copy of the program that records by itself, which the user
                // closes first
                string answering = settings.Port > 0 ? PingApp(settings.Port) : null;
                if (answering != null && (answering == settings.Name || answering == former))
                {
                    string blocking = "port " + settings.Port + " (" + answering + ")";
                    throw new SetupException(Exit.Running, string.Format(T.RunningZh, settings.DisplayName, blocking), string.Format(T.RunningEn, settings.DisplayName, blocking));
                }

                // the program's windows only show what the service records: those in this session are asked to close.
                // The installation does not wait for the others (over SSH it runs in session 0, the user's windows in
                // session 1): a file such a window holds is renamed aside (WriteFile).
                string processName = Path.GetFileNameWithoutExtension(settings.Exe);
                InstallShared.CloseWindows(processName, -1, 15, folder);
                if (former != null) InstallShared.CloseWindows(former, -1, 15, null);

                result = new InstallResult
                {
                    Folder = folder,
                    ExePath = Path.Combine(folder, settings.Exe),
                    Updated = info.OurInstall || info.NewProgram,
                    AnalysisOnly = analysisOnly,
                };
                result.ExeHash = Sha256(payload.Find(settings.Exe).Data).Substring(0, 16);
                result.Measuring.AddRange(measuring);
                result.OpenWindows.AddRange(InstallShared.UserSessionProcesses(processName, -1, folder));
                if (former != null) result.OpenWindows.AddRange(InstallShared.UserSessionProcesses(former, -1, null));

                CopyFiles(settings, payload, folder, info, result);

                string modeMarker = Path.Combine(folder, InstallShared.AnalysisOnlyMarker);
                if (analysisOnly)
                {
                    if (!settings.IsStandaloneAnalysis)
                    {
                        File.WriteAllText(modeMarker, "analysis-only\r\n", new UTF8Encoding(false));
                        // If switching an existing Full install to Analysis-only, remove the background service and tray
                        if (InstallShared.ServiceRegistered(settings.Name))
                        {
                            try { StopService(settings.Name); } catch (Exception) { }
                            ScDelete(settings.Name);
                            // a service that could not be removed stays the recorder: no marker beside it (the catch
                            // below starts it again when it ran)
                            for (int i = 0; i < 20 && InstallShared.ServiceRegistered(settings.Name); i++) Thread.Sleep(500);
                            if (InstallShared.ServiceRegistered(settings.Name))
                            {
                                InstallShared.TryDelete(modeMarker);
                                throw new SetupException(Exit.Failed, string.Format(T.ServiceStaysZh, settings.DisplayName), string.Format(T.ServiceStaysEn, settings.DisplayName));
                            }
                            result.Details.Add("the service " + settings.Name + " was registered here and is removed: analysis-only was asked for (this PC no longer records)");
                        }
                        InstallShared.RemoveRunEntry(settings.Name, result.ExePath);
                        InstallShared.StopTrays(settings.Name, processName, 10);
                    }
                    else if (File.Exists(modeMarker)) InstallShared.TryDelete(modeMarker);
                }
                else
                {
                    // (a marker that stays would make the recorder's window open as the analysis program: an error)
                    if (File.Exists(modeMarker) && !InstallShared.TryDelete(modeMarker))
                        throw new SetupException(Exit.Failed, string.Format(T.MarkerStaysZh, InstallShared.AnalysisOnlyMarker), string.Format(T.MarkerStaysEn, InstallShared.AnalysisOnlyMarker));
                    // the program's service: registered for the installed program file, started, its watcher going
                    string serviceError = RegisterService(settings, result.ExePath);
                    if (serviceError != null)
                        throw new SetupException(Exit.ServiceFailed, string.Format(T.ServiceFailedZh, settings.DisplayName, serviceError),
                                                 string.Format(T.ServiceFailedEn, settings.DisplayName, serviceError));
                    result.Service = true;
                    if (serviceWasRunning || formerWasRunning) result.PausedSeconds = paused.Elapsed.TotalSeconds;
                }
            }
            catch (Exception error) when (serviceWasRunning || (!analysisOnly && formerRegistered))
            {
                if (serviceWasRunning)
                {
                    // "analysis only" that did not get through leaves the recorder as it was: its marker file goes first
                    // (the service is registered again when it was removed already), then as after any failed update
                    if (analysisOnly) InstallShared.TryDelete(Path.Combine(folder, InstallShared.AnalysisOnlyMarker));
                    WithNote(error, RestoreService(settings, Path.Combine(folder, settings.Exe), result?.PreviousExe));
                }
                else WithNote(error, BackToFormer(settings, former, folder, Path.Combine(folder, settings.Exe), dataUnderFormerName));
                throw;
            }
            catch (SetupException error) when (error.Code == Exit.ServiceFailed && result != null)
            {
                // a first installation whose service does not run: the program is there all the same (without its
                // service it records while its window is open), with its shortcuts and its entry, so that it can be
                // started and uninstalled
                Finish(settings, folder, result, payload, desktop, info.FormerProgram, formerDesktop);
                throw;
            }
            // the program runs: its rights, shortcuts, entry, file type and tray; then the former program goes, and the
            // data folder gets its name
            Finish(settings, folder, result, payload, desktop, former != null && info.FormerProgram, formerDesktop);
            if (!analysisOnly && former != null)
            {
                result.FormerReplaced = formerRegistered || info.FormerProgram;
                result.FormerRemoved = RemoveFormer(settings, former, folder, payload, result.ExePath);
                if (formerRegistered) RestartForDataFolder(settings, folder, result);
            }
            if (!analysisOnly) ExperimentsShortcut(settings, folder, result);
            // the marker of a move into this folder that ended halfway once its service ran from here: installed here now
            if (File.Exists(Mover.Marker(folder, settings))) InstallShared.TryDelete(Mover.Marker(folder, settings));
            return result;
        }

        /// <summary>Once the program runs (on a first installation: once it is there): the rights on its folders, its
        /// shortcuts, its entry in Windows' list of apps, the .pk/.pkproj files' type and its tray's start at sign-in, for every
        /// user of the PC. A step that fails is noted, not fatal: the program records all the same. (The service runs
        /// meanwhile: setting the rights on a large data folder takes a moment.)</summary>
        public static void Finish(SetupSettings settings, string folder, InstallResult result, Payload payload, DesktopChoice desktop,
                                  bool formerHere, bool formerDesktop)
        {
            string exe = result.ExePath;
            if (!result.AnalysisOnly) result.DataFolder = PrepareDataFolder(folder, result);
            else ProgramFolderRights(folder, result);
            try
            {
                InstallShared.CreateShortcut(InstallShared.AllUsersStartMenuShortcut(settings.DisplayName), exe, folder, settings.DisplayName);
                string desktopLink = InstallShared.AllUsersDesktopShortcut(settings.DisplayName);
                string userDesktopLink = InstallShared.DesktopShortcut(settings.DisplayName);
                bool hasDesktopLink = File.Exists(desktopLink) || OurLink(userDesktopLink, exe);
                if (WantDesktop(desktop, hasDesktopLink, result.Updated, formerHere, formerDesktop))
                    InstallShared.CreateShortcut(desktopLink, exe, folder, settings.DisplayName);
                else if (desktop == DesktopChoice.Remove && OurLink(desktopLink, exe))
                    InstallShared.TryDelete(desktopLink);
                // those an earlier installation made for the installing user only (up to 2.1) are replaced by these
                foreach (string link in new[] { userDesktopLink, InstallShared.StartMenuShortcut(settings.DisplayName) })
                    if (OurLink(link, exe)) InstallShared.TryDelete(link);
                result.Desktop = File.Exists(desktopLink);
            }
            catch (Exception error) { result.Problems.Add("shortcuts: " + error.Message); }
            try { WriteEntry(settings, folder, exe, payload.Size); }
            catch (Exception error) { result.Problems.Add("the entry in the list of apps: " + error.Message); }
            try { InstallShared.RegisterPk(settings.Name, exe); }
            catch (Exception error) { result.Problems.Add("the .pk file type: " + error.Message); }
            try { InstallShared.RegisterPkProj(settings.Name, exe); }
            catch (Exception error) { result.Problems.Add("the .pkproj file type: " + error.Message); }
            if (!result.AnalysisOnly)
            {
                // the tray: at every sign-in, for every user (notifications of finished and stopped runs)
                try { InstallShared.SetRunEntry(settings.Name, exe); }
                catch (Exception error) { result.Problems.Add("the tray's start at sign-in: " + error.Message); }
                result.Recovery = InstallShared.RestartsAfterFailure(settings.Name);
                if (result.Service && !result.Recovery) result.Problems.Add("Windows would not restart the service after a failure (its recovery actions could not be set)");
            }
        }

        /// <summary>The Start menu shortcut to the Experiments folder (made once the data folder has its final name).</summary>
        public static void ExperimentsShortcut(SetupSettings settings, string folder, InstallResult result)
        {
            try
            {
                string experiments = Path.Combine(InstallShared.DataFolderIn(folder), InstallShared.ExperimentsFolder);
                if (!Directory.Exists(experiments)) return;
                InstallShared.CreateShortcut(InstallShared.ExperimentsShortcut(settings.DisplayName), experiments, experiments,
                                             settings.DisplayName + " - Experiments", result.ExePath);
            }
            catch (Exception error) { result.Problems.Add("the shortcut to the Experiments folder: " + error.Message); }
        }

        /// <summary>Whether the program gets a desktop shortcut. Where the program under its former name is replaced, the
        /// new shortcut follows the former one's; else one is made on a first installation and kept on an update.</summary>
        public static bool WantDesktop(DesktopChoice desktop, bool hasDesktopLink, bool updated, bool formerHere, bool formerDesktop)
        {
            switch (desktop)
            {
                case DesktopChoice.Create: return true;
                case DesktopChoice.Remove: return false;
                case DesktopChoice.KeepOnly: return hasDesktopLink || (formerHere && formerDesktop);
                default: return hasDesktopLink || (formerHere ? formerDesktop : !updated);
            }
        }

        /// <summary>The window's desktop box, ticked as an installation into this folder would decide by itself.</summary>
        public static bool DesktopByDefault(SetupSettings settings, string folderText)
        {
            try
            {
                string folder = FullFolder(folderText);
                FolderInfo info = Inspect(settings, folder);
                string exe = Path.Combine(folder, settings.Exe);
                bool has = File.Exists(InstallShared.AllUsersDesktopShortcut(settings.DisplayName)) || OurLink(InstallShared.DesktopShortcut(settings.DisplayName), exe);
                bool formerDesktop = info.FormerProgram && FormerDesktopLink(settings.Former, folder) != null;
                string installed = InstalledElsewhere(settings, folder);
                if (installed != null)
                {
                    // a move: the shortcut follows the one the program has now
                    string name = InstallShared.ServiceExe(settings.Name) != null ? settings.Name : settings.Former ?? settings.Name;
                    return has || DesktopLinkInto(name, installed) != null;
                }
                return WantDesktop(DesktopChoice.Default, has, info.OurInstall || info.NewProgram, info.FormerProgram, formerDesktop);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>The former program's desktop shortcut (the user's, or all users') that starts it from this folder, or null.</summary>
        static string FormerDesktopLink(string former, string folder) => former == null ? null : DesktopLinkInto(former, folder);

        /// <summary>A desktop shortcut of the program of this name (the user's, or all users') that starts it from this
        /// folder, or null.</summary>
        public static string DesktopLinkInto(string name, string folder)
        {
            string exe = Path.Combine(folder, name + ".exe");
            string shown = InstallShared.DisplayName(name);
            return new[] { InstallShared.DesktopShortcut(shown), InstallShared.AllUsersDesktopShortcut(shown), InstallShared.DesktopShortcut(name),
                           InstallShared.AllUsersDesktopShortcut(name) }.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(link => OurLink(link, exe));
        }

        static bool OurLink(string link, string exe) => File.Exists(link) && InstallShared.SameFile(InstallShared.ShortcutTarget(link), exe);

        /// <summary>The data folder beside the program, made when there is none (one of an earlier name is the program's
        /// data folder: the program renames it), with the rights the program's folders need. Returns it, or null.</summary>
        static string PrepareDataFolder(string folder, InstallResult result)
        {
            try
            {
                string data = InstallShared.DataFolderIn(folder);
                Directory.CreateDirectory(data);
                result.Details.AddRange(Permissions.Apply(folder, data));
                return data;
            }
            catch (Exception error)
            {
                result.Problems.Add("the rights on the program's folders could not be set (" + error.Message + ")");
                return null;
            }
        }

        /// <summary>An analysis-only or standalone installation has no data folder beside the program, but its program
        /// folder gets the rights the full installation gives it: outside Program Files a new folder lets every signed-in
        /// user change it - and replace the program that the other users (and an elevated uninstall) run.</summary>
        static void ProgramFolderRights(string folder, InstallResult result)
        {
            try { result.Details.Add(Permissions.ApplyProgramFolder(folder)); }
            catch (Exception error) { result.Problems.Add("the rights on the program's folder could not be set (" + error.Message + ")"); }
        }

        /// <summary>The program does not rename a data folder of the former name while the former program's service is
        /// registered (Host.DataFolder: should the installation fail, the former program goes on with it). Now that the
        /// former program is gone, the service is restarted once, and the program gives the folder its name at that start.</summary>
        static void RestartForDataFolder(SetupSettings settings, string folder, InstallResult result)
        {
            string data = InstallShared.DataFolderIn(folder);
            if (!Directory.Exists(data) || Path.GetFileName(data).Equals(InstallShared.DataFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (settings.Former != null && InstallShared.ServiceExe(settings.Former) != null)
            {
                result.Problems.Add("the former service is not gone yet: the data folder keeps the name " + Path.GetFileName(data) + " until the next start");
                return;
            }
            try { StopService(settings.Name); } catch (Exception) { }
            string problem = StartService(settings.Name, 60, out string state) ? WaitReady(settings, folder, 300) : "it does not run (" + state + ")";
            result.DataFolder = InstallShared.DataFolderIn(folder);
            if (problem == null) return;
            result.Service = StartService(settings.Name, 30, out _) && WaitReady(settings, folder, 120) == null;
            result.Problems.Add("restarting the service for the data folder's name: " + problem);
        }

        /// <summary>The name ("app") of the copy of the program that answers /api/ping on the port (under either of its
        /// names), or null.</summary>
        static string PingApp(int port)
        {
            if (port <= 0 || port > 65535) return null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/ping");
                request.Proxy = null;
                request.Timeout = 2000;
                request.ReadWriteTimeout = 2000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return Json.Parse(reader.ReadToEnd()) is Dictionary<string, object> answer && answer.TryGetValue("app", out object app) ? app as string : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>sc.exe (by its full path) with these arguments: its exit code.</summary>
        public static int Sc(string arguments)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                Task<string> output = p.StandardOutput.ReadToEndAsync(), errors = p.StandardError.ReadToEndAsync();
                p.WaitForExit(30000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        /// <summary>sc.exe delete: the service goes once nothing holds it open any more. Its exit code.</summary>
        public static int ScDelete(string name) => Sc("delete \"" + name + "\"");

        /// <summary>After a failed installation that was to replace the program under its former name: the new program's
        /// service goes and its windows close; what the installation added goes (shortcuts, entry, file type, the new
        /// program's own files); the data folder gets its former name back should the new program have renamed it; and
        /// the former program's service is started again - always, pointed at its program file where it is now should
        /// that not be where its service looks. What was done, Chinese and English.</summary>
        static Tuple<string, string> BackToFormer(SetupSettings settings, string former, string folder, string newExe, bool dataUnderFormerName)
        {
            var notes = new List<string>();
            try
            {
                if (InstallShared.ServiceExe(settings.Name) != null)
                {
                    try { StopService(settings.Name); } catch (Exception) { }
                    ScDelete(settings.Name);
                    for (int i = 0; i < 20 && InstallShared.ServiceExe(settings.Name) != null; i++) Thread.Sleep(500);
                }
                // its windows hold files in the folder and in the data folder (a window started meanwhile)
                InstallShared.CloseWindows(Path.GetFileNameWithoutExtension(settings.Exe), -1, 15, folder);
            }
            catch (Exception error) { notes.Add(error.Message); }
            try { RemoveNew(settings, folder, newExe); }
            catch (Exception error) { notes.Add(error.Message); }

            // the data folder's former name, should the new program have renamed it (an earlier version did: the
            // service is told to keep the name while the former program is registered)
            string newData = Path.Combine(folder, InstallShared.DataFolder), formerData = Path.Combine(folder, InstallShared.OldDataFolders[0]);
            string dataArgument = null;
            if (dataUnderFormerName && Directory.Exists(newData) && !Directory.Exists(formerData))
                try { Retry(() => Directory.Move(newData, formerData), 20, 500); }
                catch (Exception error)
                {
                    dataArgument = newData;
                    notes.Add("the data folder keeps the name " + InstallShared.DataFolder + " (" + error.Message + ")");
                }

            // the former program's service: pointed at its program file where it is now (and at the data folder when that
            // keeps the new name), then started
            try
            {
                string registered = InstallShared.ServiceExe(former);
                string here = Path.Combine(folder, former + ".exe");
                if (File.Exists(here) && (registered == null || !File.Exists(registered) || dataArgument != null))
                    notes.Add(PointService(former, here, dataArgument));
            }
            catch (Exception error) { notes.Add(error.Message); }
            bool runs = StartService(former, 30, out string state);
            string extra = notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "";
            return runs ? Tuple.Create(string.Format(T.FormerBackZh, former) + extra, string.Format(T.FormerBackEn, former) + extra)
                        : Tuple.Create(string.Format(T.ServiceDownZh, former + ": " + state + extra), string.Format(T.ServiceDownEn, former + ": " + state + extra));
        }

        /// <summary>What a failed replacement of the former program had added goes again: the new program's shortcuts,
        /// its entry, its file type and its tray's start, and those of its own files the former program does not have
        /// (the exe, its .config, its settings file, its list of files; one in use is renamed aside). The files both have
        /// (the page, the WebView2 DLLs) stay, in their new version.</summary>
        static void RemoveNew(SetupSettings settings, string folder, string newExe)
        {
            foreach (string link in InstallShared.Shortcuts(settings.DisplayName))
                if (OurLink(link, newExe)) InstallShared.TryDelete(link);
            try { InstallShared.RemoveEntry(settings.Name, folder, newExe); } catch (Exception) { }
            try { InstallShared.UnregisterPk(settings.Name, newExe); } catch (Exception) { }
            InstallShared.RemoveRunEntry(settings.Name, newExe);
            InstallShared.Manifest formerList = settings.Former == null ? null : InstallShared.ReadManifest(folder, settings.Former);
            var formerFiles = new HashSet<string>(formerList != null ? formerList.Files : new List<string>(), StringComparer.OrdinalIgnoreCase);
            InstallShared.Manifest mine = InstallShared.ReadManifest(folder, settings.Name);
            var files = new List<string> { settings.Exe, settings.Exe + ".config", settings.Name + ".service.json" };
            if (mine != null) files.AddRange(mine.Files);
            foreach (string rel in files.Where(InstallShared.IsSafeRelative).Distinct(StringComparer.OrdinalIgnoreCase).Where(r => !formerFiles.Contains(r)))
            {
                string path = Path.Combine(folder, rel);
                if (File.Exists(path) && !InstallShared.TryDelete(path))
                    try { File.Move(path, InstallShared.ReplacedName(path)); } catch (Exception) { }
            }
            InstallShared.TryDelete(InstallShared.ManifestPath(folder, settings.Name));
        }

        /// <summary>Points a service at a program file (and a data folder): sc.exe config binPath=; a service that is not
        /// registered any more is registered by the program itself (its --install-service). What was done.</summary>
        public static string PointService(string name, string exe, string dataDir)
        {
            if (InstallShared.ServiceExe(name) == null)
            {
                var psi = new ProcessStartInfo(exe, "--install-service") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
                using (Process p = Process.Start(psi)) p.WaitForExit(180000);
                if (dataDir == null) return name + " registered again for " + exe;
            }
            string line = "\"" + exe + "\" --service" + (dataDir != null ? " --data-dir \"" + dataDir + "\"" : "");
            int code = Sc("config \"" + name + "\" binPath= \"" + line.Replace("\"", "\\\"") + "\"");
            return code == 0 ? name + " now runs " + line : "pointing " + name + " at " + exe + " failed (sc exit " + code + ")";
        }

        /// <summary>Once the program under its new name runs, the one under its former name goes: its service, the files
        /// its installer put in its folder (not those the new program has now, never the data folder) - one in use (a
        /// window of it still open) is renamed aside and stays listed, for the next installation removes it -, the copies
        /// of them renamed aside, its settings file, its shortcuts and its entry in the list of apps. Other shortcuts to
        /// its program file (a pin on the taskbar) start the new program from now on. What was done.</summary>
        static string RemoveFormer(SetupSettings settings, string former, string folder, Payload payload, string newExe)
        {
            var done = new List<string>();
            try
            {
                string formerExe = InstallShared.ServiceExe(former);
                if (formerExe != null)
                {
                    try { StopService(former); } catch (Exception) { }
                    if (ScDelete(former) == 0) done.Add("service");
                    for (int i = 0; i < 20 && InstallShared.ServiceExe(former) != null; i++) Thread.Sleep(500);    // gone once nothing holds it
                    if (InstallShared.ServiceRegistered(former))
                    {
                        Sc("config \"" + former + "\" start= disabled");     // never again at the start of Windows
                        done.Add("its service could not be removed and is disabled");
                    }
                }
                // its folders: this one, where its service ran it from and where its entry in the list of apps says
                var dirs = new List<string> { folder };
                foreach (string d in new[] { formerExe == null ? null : Path.GetDirectoryName(formerExe), InstallShared.RegisteredFolder(former) })
                    if (d != null && Directory.Exists(d) && !dirs.Any(x => InstallShared.SameFolder(x, d))) dirs.Add(d);
                var ours = new HashSet<string>(payload.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                int files = 0, held = 0;
                foreach (string dir in dirs)
                {
                    // its files go only from this folder: a folder the program was moved from stays exactly as it is (a
                    // backup its owner deletes), also when its service is found there later (one that could not be removed)
                    InstallShared.Manifest list = InstallShared.ReadManifest(dir, former);
                    if (list == null || !InstallShared.SameFolder(dir, folder)) continue;
                    bool here = true;
                    List<string> theirs = list.Files.Where(InstallShared.IsSafeRelative).Where(r => !(here && ours.Contains(r)))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (string rel in theirs)
                    {
                        string path = Path.Combine(dir, rel);
                        if (!File.Exists(path)) continue;
                        if (InstallShared.TryDelete(path)) files++;
                        else try { File.Move(path, InstallShared.ReplacedName(path)); } catch (Exception) { }
                    }
                    foreach (string path in InstallShared.ReplacedFiles(dir, theirs))
                        if (InstallShared.TryDelete(path)) files++;
                    string settingsFile = Path.Combine(dir, former + ".service.json");
                    if (File.Exists(settingsFile) && InstallShared.TryDelete(settingsFile)) files++;
                    int left = theirs.Count(r => File.Exists(Path.Combine(dir, r))) + InstallShared.ReplacedFiles(dir, theirs).Count;
                    if (left > 0)
                    {
                        held += left;
                        InstallShared.WriteManifest(dir, former, theirs, list.CreatedFolder);
                    }
                    else if (InstallShared.TryDelete(InstallShared.ManifestPath(dir, former))) files++;
                }
                if (files > 0) done.Add(files + " files");
                if (held > 0) done.Add(held + " files in use, removed at the next installation");
                // its shortcuts: those that lead into its folders
                int links = 0;
                foreach (string link in InstallShared.Shortcuts(former))
                {
                    string target = File.Exists(link) ? InstallShared.ShortcutTarget(link) : null;
                    if (target != null && dirs.Any(d => InstallShared.IsInFolder(target, d)) && InstallShared.TryDelete(link)) links++;
                }
                if (links > 0) done.Add(links + " shortcuts");
                int moved = RepointLinks(former, dirs, newExe, folder, settings.DisplayName);
                if (moved > 0) done.Add(moved + " other shortcuts now start " + settings.Exe);
                // its entry in the list of apps, when it is this installation's
                if (dirs.Any(d => InstallShared.RemoveEntry(former, d, Path.Combine(d, former + ".exe")))) done.Add("apps entry");
            }
            catch (Exception error)
            {
                done.Add("stopped early: " + error.Message);
            }
            return "the former program " + former + " removed (" + (done.Count > 0 ? string.Join(", ", done) : "nothing was left of it") + ")";
        }

        /// <summary>Shortcuts to the former program's file made by hand - on the desktop (the user's, all users'), pinned
        /// to the taskbar - start the new program from now on. How many.</summary>
        public static int RepointLinks(string former, List<string> dirs, string newExe, string folder, string description)
        {
            int count = 0;
            string pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            foreach (string place in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), pins })
                try
                {
                    if (string.IsNullOrEmpty(place) || !Directory.Exists(place)) continue;
                    foreach (string link in Directory.GetFiles(place, "*.lnk"))
                    {
                        string target = InstallShared.ShortcutTarget(link);
                        if (target == null || !Path.GetFileName(target).Equals(former + ".exe", StringComparison.OrdinalIgnoreCase) ||
                            !dirs.Any(d => InstallShared.IsInFolder(target, d)))
                            continue;
                        InstallShared.CreateShortcut(link, newExe, folder, description);
                        count++;
                    }
                }
                catch (Exception) { }
            return count;
        }

        /// <summary>Puts the program's files into the folder (made when missing) and writes their list there.</summary>
        static void CopyFiles(SetupSettings settings, Payload payload, string folder, FolderInfo info, InstallResult result)
        {
            Directory.CreateDirectory(folder);
            InstallShared.Manifest previous = InstallShared.ReadManifest(folder, settings.Name);
            var previousFiles = new HashSet<string>(previous != null ? previous.Files : new List<string>(), StringComparer.OrdinalIgnoreCase);
            // the files of the program under its former name are this installer's own too: the new versions of those of
            // the same names replace them without a copy kept as .old (its other files go once the new program runs)
            InstallShared.Manifest formerList = settings.Former == null ? null : InstallShared.ReadManifest(folder, settings.Former);
            var formerFiles = new HashSet<string>(formerList != null ? formerList.Files : new List<string>(), StringComparer.OrdinalIgnoreCase);

            // copies an earlier update renamed aside because a window held them: removed now that nothing holds them
            // (one still held stays until the next time); the user's files kept as .old are never among them
            foreach (string old in InstallShared.ReplacedFiles(folder, payload.Files.Select(f => f.Path).Concat(previousFiles)))
                try
                {
                    File.SetAttributes(old, FileAttributes.Normal);
                    File.Delete(old);
                }
                catch (Exception) { }
            CheckSpace(folder, payload.Size);

            // the old one-file program keeps its place in the folder, under another name
            if (info.OldProgram) result.RenamedOld = Path.GetFileName(MoveAside(result.ExePath, settings.Name + "-python-old", ".exe"));

            foreach (PayloadFile file in payload.Files)
            {
                string target = Path.Combine(folder, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target))
                {
                    // a file of the same name that this installer did not put there (a page dropped in by hand, say)
                    // is kept under another name; ours are replaced
                    bool isExe = file.Path.Equals(settings.Exe, StringComparison.OrdinalIgnoreCase);
                    if (isExe)
                    {
                        // the program file of the version installed before is kept aside until the next installation:
                        // should the new one not start, it goes back (Install's catch, RestoreService)
                        File.SetAttributes(target, FileAttributes.Normal);
                        string previousExe = InstallShared.ReplacedName(target);
                        Retry(() => File.Move(target, previousExe));
                        result.PreviousExe = previousExe;
                    }
                    else if (!previousFiles.Contains(file.Path) && !formerFiles.Contains(file.Path) && !SameContent(target, file.Data))
                        result.KeptAside.Add(MakeRelative(folder, MoveAside(target, Path.GetFileName(target) + ".old", "")));
                    else File.SetAttributes(target, FileAttributes.Normal);
                }
                string aside = WriteFile(target, file.Data);
                if (aside != null) result.Replaced.Add(MakeRelative(folder, aside));
            }

            // files of the version installed before that this one no longer has
            foreach (string rel in previousFiles)
            {
                if (payload.Find(rel) != null || !InstallShared.IsSafeRelative(rel)) continue;
                string path = Path.Combine(folder, rel);
                if (File.Exists(path) && InstallShared.TryDelete(path)) result.Removed.Add(rel);
            }

            InstallShared.WriteManifest(folder, settings.Name, payload.Files.Select(f => f.Path),
                !info.Exists || (previous != null && previous.CreatedFolder) || (formerList != null && formerList.CreatedFolder));
        }

        /// <summary>Writes one of the program's files. One that cannot be overwritten because a running process holds it
        /// (the exe and the WebView2 DLLs of a window still open: Windows lets them be renamed, not overwritten) is
        /// renamed aside to "file.replaced-N" and the new one is written in its place. Returns the path it was renamed
        /// to, or null.</summary>
        public static string WriteFile(string target, byte[] data)
        {
            try
            {
                Retry(() => File.WriteAllBytes(target, data));
                return null;
            }
            catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && File.Exists(target))
            {
                if (SameContent(target, data)) return null;           // the file in use is the new one already
                string aside = InstallShared.ReplacedName(target);
                Retry(() => File.Move(target, aside));
                try { Retry(() => File.WriteAllBytes(target, data)); }
                catch (Exception)
                {
                    // the new file cannot be written either (an antivirus, a full disk): the old one goes back, so
                    // that the service started again finds its program
                    try { if (File.Exists(target)) File.Delete(target); File.Move(aside, target); } catch (Exception) { }
                    throw;
                }
                return aside;
            }
        }

        /// <summary>The program's entry in Windows' list of apps (Settings > Apps), for all users; the entry an earlier
        /// installation made for the installing user only (up to 2.1) goes.</summary>
        static void WriteEntry(SetupSettings settings, string folder, string exe, long size)
        {
            using (RegistryKey machine = InstallShared.Machine())
            using (RegistryKey key = machine.CreateSubKey(InstallShared.UninstallKey(settings.Name)))
            {
                key.SetValue("DisplayName", settings.DisplayName);
                key.SetValue("DisplayVersion", settings.Version);
                key.SetValue("Publisher", InstallShared.Publisher);
                key.SetValue("InstallLocation", folder);
                key.SetValue("DisplayIcon", exe);
                key.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                key.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall /S");
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, (size + 1023) / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                // the manual that comes with the program (Settings > Apps shows it as the support link)
                string help = Path.Combine(folder, "Help", "index.html");
                if (File.Exists(help)) key.SetValue("HelpLink", help);
                else key.DeleteValue("HelpLink", false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(InstallShared.UninstallKey(settings.Name), false);
        }

        /// <summary>Renames a file to the first free name of base + ext, base-2 + ext, ...; returns the new path.</summary>
        static string MoveAside(string path, string baseName, string extension)
        {
            string candidate = FreeName(Path.GetDirectoryName(path), baseName, extension);
            Retry(() => File.Move(path, candidate));
            return candidate;
        }

        /// <summary>The first of base + ext, base-2 + ext, ... that nothing in the folder is called.</summary>
        public static string FreeName(string dir, string baseName, string extension)
        {
            for (int n = 1; ; n++)
            {
                string candidate = Path.Combine(dir, baseName + (n == 1 ? "" : "-" + n) + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        static string MakeRelative(string folder, string path) => path.Substring(folder.Length).TrimStart('\\');

        static bool SameContent(string path, byte[] data)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length != data.Length) return false;
                    var buffer = new byte[data.Length];
                    for (int read = 0; read < buffer.Length;)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                    return buffer.SequenceEqual(data);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void CheckSpace(string folder, long size)
        {
            try
            {
                string root = Path.GetPathRoot(folder);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return;
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < size + (4L << 20))
                {
                    long megabytes = (size >> 20) + 4;
                    throw new SetupException(Exit.NoSpace, string.Format(T.NoSpaceZh, megabytes), string.Format(T.NoSpaceEn, megabytes));
                }
            }
            catch (SetupException)
            {
                throw;
            }
            catch (Exception) { }
        }

        /// <summary>Runs a file operation, again a few times when the file is held for a moment (a virus scanner).</summary>
        public static void Retry(Action action, int attempts = 6, int milliseconds = 300)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (attempt < attempts)
                {
                    Thread.Sleep(milliseconds);
                }
                catch (UnauthorizedAccessException) when (attempt < attempts)
                {
                    Thread.Sleep(milliseconds);
                }
            }
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>The rights on the program's folders, set at every installation (an update brings an older installation's
    /// folders to them). Well-known SIDs, not names: Windows names them in its own language.
    ///   program folder        as Program Files has it: administrators and Windows change it, users read and run it.
    ///                         Under Program Files it inherits exactly that; elsewhere it gets it explicitly, without
    ///                         what the drive gives its users (C:\ and D:\ let every signed-in user change new folders) -
    ///                         the service runs this folder's program as Windows' own account.
    ///   PlateKineticsLive-data   as the program folder: users read it (the "Users: Modify" of earlier versions goes)
    ///   Experiments           users may add, rename, move and delete experiments and folders in it (not the folder)
    ///   System (hidden)       users read; only the service (and administrators) write
    ///   System\users          each user may make his own folder there, which is his alone (CREATOR OWNER), for his
    ///                         window's data
    /// Rights the PC's administrators gave other groups are kept; a folder whose rights are as wanted is not written
    /// again (setting rights walks everything below a folder).</summary>
    static class Permissions
    {
        static SecurityIdentifier Sid(WellKnownSidType type) => new SecurityIdentifier(type, null);
        static readonly SecurityIdentifier SystemAccount = Sid(WellKnownSidType.LocalSystemSid), Administrators = Sid(WellKnownSidType.BuiltinAdministratorsSid),
                                           Users = Sid(WellKnownSidType.BuiltinUsersSid), CreatorOwner = Sid(WellKnownSidType.CreatorOwnerSid),
                                           AppPackages = new SecurityIdentifier("S-1-15-2-1"), RestrictedAppPackages = new SecurityIdentifier("S-1-15-2-2");
        // the groups whose rights the installer decides: those that hold every user of the PC
        static readonly SecurityIdentifier[] Everybody = { Users, Sid(WellKnownSidType.AuthenticatedUserSid), Sid(WellKnownSidType.WorldSid), Sid(WellKnownSidType.InteractiveSid) };
        const InheritanceFlags Below = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const FileSystemRights Read = FileSystemRights.ReadAndExecute;

        static FileSystemAccessRule Rule(SecurityIdentifier who, FileSystemRights rights, InheritanceFlags inheritance, PropagationFlags propagation = PropagationFlags.None) =>
            new FileSystemAccessRule(who, rights, inheritance, propagation, AccessControlType.Allow);

        /// <summary>Sets the rights; returns one line per folder for the log: its path and its rights now (SDDL).</summary>
        public static List<string> Apply(string programFolder, string dataFolder)
        {
            var lines = new List<string>();
            // the program folder
            lines.Add(ApplyProgramFolder(programFolder));

            // the data folder: what it inherits
            Directory.CreateDirectory(dataFolder);
            Set(dataFolder, false, new FileSystemAccessRule[0], null);
            lines.Add("rights " + Describe(dataFolder));

            // Experiments: users work in it, the folder itself stays
            string experiments = Path.Combine(dataFolder, InstallShared.ExperimentsFolder);
            Directory.CreateDirectory(experiments);
            Set(experiments, false,
                new[]
                {
                    Rule(Users, Read | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, InheritanceFlags.None),
                    Rule(Users, FileSystemRights.Modify, Below, PropagationFlags.InheritOnly),
                }, null);
            lines.Add("rights " + Describe(experiments));

            // System: the program's own, hidden; users read it
            string system = Path.Combine(dataFolder, InstallShared.SystemFolder);
            Directory.CreateDirectory(system);
            File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.Hidden);
            Set(system, false, new FileSystemAccessRule[0], null);
            lines.Add("rights " + Describe(system));

            // System\users: every user makes his own folder, which is his alone
            string users = Path.Combine(system, InstallShared.UsersFolder);
            Directory.CreateDirectory(users);
            Set(users, true,
                new[] { Rule(Users, Read | FileSystemRights.CreateDirectories, InheritanceFlags.None) },
                new[]
                {
                    Rule(SystemAccount, FileSystemRights.FullControl, Below), Rule(Administrators, FileSystemRights.FullControl, Below),
                    Rule(CreatorOwner, FileSystemRights.FullControl, Below, PropagationFlags.InheritOnly),
                });
            lines.Add("rights " + Describe(users));
            return lines;
        }

        /// <summary>The program folder's rights alone (all an analysis-only or standalone installation has); returns its
        /// line for the log.</summary>
        public static string ApplyProgramFolder(string programFolder)
        {
            if (IsUnderProgramFiles(programFolder)) Set(programFolder, false, new FileSystemAccessRule[0], null);
            else
                Set(programFolder, true,
                    new[] { Rule(Users, Read, Below), Rule(AppPackages, Read, Below), Rule(RestrictedAppPackages, Read, Below) },
                    new[] { Rule(SystemAccount, FileSystemRights.FullControl, Below), Rule(Administrators, FileSystemRights.FullControl, Below) });
            return "rights " + Describe(programFolder);
        }

        /// <summary>True for a folder under Program Files (64-bit or 32-bit): it inherits their rights.</summary>
        public static bool IsUnderProgramFiles(string folder)
        {
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string programFiles = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(programFiles) && InstallShared.IsInFolder(folder, programFiles)) return true;
            }
            return false;
        }

        /// <summary>Gives the folder these rules: everybody's (the groups that hold every user) exactly as given - others
        /// of theirs go -, the others' (SYSTEM, Administrators, CREATOR OWNER) added when missing; protected: it no
        /// longer inherits; else it does. Written only when something changes. True when written.</summary>
        static bool Set(string folder, bool protect, FileSystemAccessRule[] everybody, FileSystemAccessRule[] others)
        {
            DirectorySecurity security = Directory.GetAccessControl(folder, AccessControlSections.Access);
            bool changed = false;
            List<FileSystemAccessRule> explicitRules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            foreach (FileSystemAccessRule rule in explicitRules)
                if (rule.AccessControlType == AccessControlType.Allow && Everybody.Any(s => s.Equals(rule.IdentityReference)) && !everybody.Any(w => Same(w, rule)))
                {
                    security.RemoveAccessRuleSpecific(rule);
                    changed = true;
                }
            foreach (FileSystemAccessRule wanted in everybody.Concat(others ?? new FileSystemAccessRule[0]))
                if (!explicitRules.Any(r => Same(wanted, r)))
                {
                    security.AddAccessRule(wanted);
                    changed = true;
                }
            if (security.AreAccessRulesProtected != protect)
            {
                security.SetAccessRuleProtection(protect, false);
                changed = true;
            }
            if (changed) Directory.SetAccessControl(folder, security);
            return changed;
        }

        static bool Same(FileSystemAccessRule a, FileSystemAccessRule b) =>
            a.IdentityReference.Equals(b.IdentityReference) && a.AccessControlType == b.AccessControlType && a.FileSystemRights == b.FileSystemRights &&
            a.InheritanceFlags == b.InheritanceFlags && a.PropagationFlags == b.PropagationFlags;

        /// <summary>"path: SDDL" of a folder's rights.</summary>
        public static string Describe(string folder)
        {
            try { return folder + ": " + Directory.GetAccessControl(folder, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access); }
            catch (Exception error) { return folder + ": (" + error.Message + ")"; }
        }
    }

    /// <summary>Timeouts of a move, in seconds (tests make them short).</summary>
    sealed class MoveOptions
    {
        public int StopSeconds = 90;            // the old service stops
        public int ReadySeconds = 300;          // the new one answers with its watcher going
        public int InstrumentSeconds = 300;     // ... and shows every instrument the old one showed
        public int BackSeconds = 120;           // after a failure: the old one answers again
        public int LookMilliseconds = 1000;
    }

    /// <summary>What a move does to the PC beyond its two folders - the services, the program's answers, the processes,
    /// the registrations -, so that tests can stand in for it. SystemMoveHost does it for real.</summary>
    interface IMoveHost
    {
        string ServiceExe(string name);
        string ServiceImagePath(string name);
        bool ServiceRegistered(string name);
        string RegisteredFolder(string name);
        Tuple<string, string> Running(SetupSettings settings);     // a copy of the program that records by itself (Installer.Running)
        bool StopService(string name, int seconds);                 // false: it did not stop in time (it is started again)
        bool StartService(string name, int seconds);
        string RegisterService(SetupSettings settings, string exe, bool demandStart);    // null: it runs, its watcher going
        string PointService(string name, string exe);               // runs this program file from now on
        bool SetAutoStart(string name);
        bool DeleteService(string name);                            // false: it is still there
        void DisableService(string name);
        string WaitReady(SetupSettings settings, string folder, int seconds, string serviceName);
        List<string> Instruments(SetupSettings settings, string folder, out List<string> measuring);   // null: no answer
        long FreeBytes(string folder);                              // -1: unknown
        List<string> ProcessesIn(string folder);                    // processes whose program file is in the folder
        void EndProcessesIn(string folder);
        void CloseWindows(SetupSettings settings, string sourceName);
        bool HadDesktopShortcut(string sourceName, string sourceFolder);
        string RemoveRegistrations(SetupSettings settings, string sourceName, string sourceFolder, string newExe, string newFolder);
        void Finish(SetupSettings settings, string folder, InstallResult result, Payload payload, DesktopChoice desktop, bool formerDesktop);
        void Log(string line);
    }

    /// <summary>The move of an installed program into another folder (see the top of this file). The old folder is only
    /// read; the new one must be new, empty, or what a move that did not finish left there.</summary>
    static class Mover
    {
        const string MarkerSuffix = "-move-in-progress.txt";

        sealed class Source
        {
            public string Name, Folder, Exe, Data, Service;
        }

        /// <summary>The move's marker in the new folder: there from the start of a move until it holds.</summary>
        public static string Marker(string folder, SetupSettings settings) => Path.Combine(folder, settings.Name + MarkerSuffix);

        /// <summary>True for the folder of a move under the former name that did not hold (the PC restarted, the installer
        /// was ended): its marker is there while the program under its former name still runs from another folder. That
        /// one is where the program is; the folder holds only copies (and a service registered there to start only when
        /// asked), which the next move clears. serviceExe: the program file a service runs, by name.</summary>
        public static bool UnfinishedMoveInto(SetupSettings settings, string folder, Func<string, string> serviceExe)
        {
            if (folder == null || settings.Former == null || !File.Exists(Marker(folder, settings))) return false;
            string former = serviceExe(settings.Former);
            return former != null && File.Exists(former) && !InstallShared.SameFolder(Path.GetDirectoryName(former), folder);
        }

        /// <summary>Why the installed program is never moved (Chinese, English), or null. A move carries a recorder
        /// over: it registers the program's service for the new folder. The standalone Analysis program and a Live
        /// program installed "analysis only" have none and must not get one. (The marker file beside a program that a
        /// service runs is a stray one: that is a recorder, and it is moved as one - without the marker.)</summary>
        public static Tuple<string, string> NeverMoved(SetupSettings settings, IMoveHost host)
        {
            Source source = FindSource(settings, host);
            return source == null ? null : NeverMoved(settings, source);
        }

        static Tuple<string, string> NeverMoved(SetupSettings settings, Source source)
        {
            if (!settings.IsStandaloneAnalysis && (source.Service != null || !File.Exists(Path.Combine(source.Folder, InstallShared.AnalysisOnlyMarker)))) return null;
            return Tuple.Create(string.Format(T.AnalysisNotMovedZh, settings.DisplayName, source.Folder), string.Format(T.AnalysisNotMovedEn, settings.DisplayName, source.Folder));
        }

        /// <summary>Why the program cannot be moved into this folder (Chinese, English), or null. Nothing is changed.</summary>
        public static Tuple<string, string> TargetProblem(SetupSettings settings, string target, IMoveHost host)
        {
            Source source = FindSource(settings, host);
            if (source == null) return Tuple.Create(string.Format(T.NothingToMoveZh, settings.DisplayName), string.Format(T.NothingToMoveEn, settings.DisplayName));
            Tuple<string, string> never = NeverMoved(settings, source);
            if (never != null) return never;
            if (InstallShared.IsInFolder(target, source.Folder) || InstallShared.IsInFolder(source.Folder, target) ||
                InstallShared.IsInFolder(target, source.Data) || InstallShared.SameFolder(target, source.Data))
                return Tuple.Create(T.NestedZh, T.NestedEn);
            if (Directory.Exists(target) && !File.Exists(Marker(target, settings)) && Directory.EnumerateFileSystemEntries(target).Any())
                return Tuple.Create(string.Format(T.NotEmptyZh, target), string.Format(T.NotEmptyEn, target));
            List<string> running = Directory.Exists(target) ? host.ProcessesIn(target) : new List<string>();
            if (running.Count > 0)
                return Tuple.Create(string.Format(T.InUseZh, target, string.Join(", ", running)), string.Format(T.InUseEn, target, string.Join(", ", running)));
            return null;
        }

        /// <summary>Where the program is now: the folder its service runs it from (under its name, else its former one),
        /// else the folder its entry in the list of apps names. Null when it is not installed.</summary>
        static Source FindSource(SetupSettings settings, IMoveHost host)
        {
            foreach (string name in new[] { settings.Name, settings.Former })
            {
                if (name == null) continue;
                string exe = host.ServiceExe(name);
                if (exe == null || !File.Exists(exe)) continue;
                string folder = Path.GetDirectoryName(exe);
                if (UnfinishedMoveInto(settings, folder, host.ServiceExe)) continue;      // a move that did not hold: the former is the source
                return new Source
                {
                    Name = name, Folder = folder, Exe = exe, Service = name,
                    Data = DataArgument(host.ServiceImagePath(name)) ?? InstallShared.DataFolderIn(folder),
                };
            }
            foreach (string name in new[] { settings.Name, settings.Former })
            {
                if (name == null) continue;
                string folder = host.RegisteredFolder(name);
                if (folder == null || !Directory.Exists(folder) || UnfinishedMoveInto(settings, folder, host.ServiceExe)) continue;
                string exe = Path.Combine(folder, name + ".exe");
                if (File.Exists(exe) || Directory.Exists(InstallShared.DataFolderIn(folder)))
                    return new Source { Name = name, Folder = folder, Exe = exe, Service = null, Data = InstallShared.DataFolderIn(folder) };
            }
            return null;
        }

        /// <summary>The data folder a service's command line names (--data-dir), or null.</summary>
        public static string DataArgument(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return null;
            int at = imagePath.IndexOf("--data-dir", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string rest = imagePath.Substring(at + 10).TrimStart('=', ' ', '\t');
            string value;
            if (rest.StartsWith("\""))
            {
                int end = rest.IndexOf('"', 1);
                value = end > 0 ? rest.Substring(1, end - 1) : rest.Substring(1);
            }
            else
            {
                int end = rest.IndexOfAny(new[] { ' ', '\t' });
                value = end > 0 ? rest.Substring(0, end) : rest;
            }
            return value.Length > 0 ? value : null;
        }

        /// <summary>Moves the installed program into the folder. Throws a SetupException when it does not: before the old
        /// service was stopped nothing of the old installation changed (the code says why: 2, 3, 8, ...); after it, Exit.
        /// MovedBack when everything is back and the old service records again, else the failure's code with a warning.</summary>
        public static InstallResult Move(SetupSettings settings, Payload payload, string targetText, DesktopChoice desktop, IMoveHost host, MoveOptions options)
        {
            string target = Installer.FullFolder(targetText);
            Source source = FindSource(settings, host);
            if (source == null)
                throw new SetupException(Exit.BadFolder, string.Format(T.NothingToMoveZh, settings.DisplayName), string.Format(T.NothingToMoveEn, settings.DisplayName));
            Tuple<string, string> never = NeverMoved(settings, source);
            if (never != null) throw new SetupException(Exit.BadFolder, never.Item1, never.Item2);
            if (InstallShared.SameFolder(source.Folder, target))
                throw new SetupException(Exit.BadFolder, string.Format(T.AlreadyThereZh, settings.DisplayName, target), string.Format(T.AlreadyThereEn, settings.DisplayName, target));
            // a move into this folder that did not hold (the PC restarted, the installer was ended): what it left running
            // goes first - the folder itself is cleared below
            if (Directory.Exists(target) && File.Exists(Marker(target, settings))) UndoUnfinished(settings, host, options, source, target);
            Tuple<string, string> problem = TargetProblem(settings, target, host);
            if (problem != null) throw new SetupException(Exit.BadFolder, problem.Item1, problem.Item2);
            Tuple<string, string> running = host.Running(settings);
            if (running != null)
                throw new SetupException(Exit.Running, string.Format(T.RunningZh, settings.DisplayName, running.Item1), string.Format(T.RunningEn, settings.DisplayName, running.Item2));

            bool existed = Directory.Exists(target);
            bool leftovers = existed && File.Exists(Marker(target, settings));
            string targetData = Path.Combine(target, InstallShared.DataFolder);
            string newExe = Path.Combine(target, settings.Exe);

            // every copy must stay within the 260 characters Windows programs (and Excel) can open - also once the new
            // program has put the old flat items into System\ at its first start
            string longest = Directory.Exists(source.Data) ? DataCopy.LongestFile(source.Data) : null;
            int intoSystem = Directory.Exists(source.Data) && !Directory.Exists(Path.Combine(source.Data, InstallShared.SystemFolder)) ? InstallShared.SystemFolder.Length + 1 : 0;
            if (longest != null && targetData.Length + 1 + intoSystem + longest.Length > 259)
                throw new SetupException(Exit.BadFolder, string.Format(T.PathTooLongZh, Path.Combine(targetData, longest)), string.Format(T.PathTooLongEn, Path.Combine(targetData, longest)));

            // room: the program, the data copied, and what the new program makes of the data at its first start (the
            // experiment files, a copy of the old records), with a margin
            long dataBytes = Directory.Exists(source.Data) ? DataCopy.PlannedBytes(source.Data) : 0;
            long need = payload.Size + 3 * dataBytes + (512L << 20);
            long free = host.FreeBytes(existed ? target : Path.GetPathRoot(target));
            if (free >= 0 && free < need)
                throw new SetupException(Exit.NoSpace, string.Format(T.NoSpaceMoveZh, target, need >> 20, free >> 20), string.Format(T.NoSpaceMoveEn, target, need >> 20, free >> 20));

            var result = new InstallResult
            {
                Folder = target, ExePath = newExe, Moved = true, MovedFrom = source.Folder, DataFolder = targetData,
                ExeHash = Installer.Sha256(payload.Find(settings.Exe).Data).Substring(0, 16),
            };
            List<string> before = host.Instruments(settings, source.Folder, out List<string> measuring);
            result.Measuring.AddRange(measuring);
            bool formerDesktop = host.HadDesktopShortcut(source.Name, source.Folder);
            host.Log("move: " + source.Name + " from " + source.Folder + " (data " + source.Data + ", service " + (source.Service ?? "none") + ") to " + target +
                     "; instruments before: " + (before == null ? "no answer" : string.Join(", ", before)) + (leftovers ? "; an unfinished move is cleared first" : ""));

            bool stopped = false, registered = false, switched = false;
            Stopwatch paused = null;
            var copy = new DataCopy(source.Data, targetData, host);
            try
            {
                // 1. the new folder: the program's files, their list, the service's settings (whose Desktop the program
                //    looks at), the marker
                if (leftovers) ClearFolder(target);
                Directory.CreateDirectory(target);
                File.WriteAllText(Marker(target, settings), "Moving " + settings.DisplayName + " from " + source.Folder + " to here, started " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ". Should this file still be here, the move did not finish: running the installer again finishes or undoes it.\r\n",
                    new UTF8Encoding(false));
                foreach (PayloadFile file in payload.Files)
                {
                    string path = Path.Combine(target, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    Installer.Retry(() => File.WriteAllBytes(path, file.Data));
                }
                InstallShared.WriteManifest(target, settings.Name, payload.Files.Select(f => f.Path), !existed || leftovers);
                string sourceSettings = Path.Combine(source.Folder, source.Name + ".service.json");
                if (File.Exists(sourceSettings)) File.Copy(sourceSettings, Path.Combine(target, settings.Name + ".service.json"), true);

                // 2. the data, a first time while the old program still records (it changes a few files meanwhile)
                if (Directory.Exists(source.Data)) copy.Pass();

                // 3. the program's windows in this session close (they show the old program); then the old service stops:
                //    from here on the recording pauses
                host.CloseWindows(settings, source.Name);
                paused = Stopwatch.StartNew();
                if (source.Service != null)
                {
                    if (!host.StopService(source.Service, options.StopSeconds))
                        throw new SetupException(Exit.Running, string.Format(T.NotStoppedZh, source.Service, options.StopSeconds), string.Format(T.NotStoppedEn, source.Service, options.StopSeconds));
                    stopped = true;
                }

                // 4. what changed meanwhile, then every file checked against the original: size and SHA-256
                if (Directory.Exists(source.Data))
                {
                    copy.Pass();
                    copy.Verify();
                }
                result.MovedFiles = copy.Files;
                result.MovedBytes = copy.Bytes;
                host.Log("move: " + copy.Files + " data files (" + copy.Bytes + " bytes) copied and checked" +
                         (copy.LeftOut.Count > 0 ? "; not copied (web caches): " + string.Join(", ", copy.LeftOut) : "") +
                         (copy.Skipped.Count > 0 ? "; log files held by another program, not copied: " + string.Join(", ", copy.Skipped) : ""));

                // 5. the new folder's service: the same service run from the new folder, or the new name's service,
                //    which starts only when asked until the move holds (a power cut now leaves the old one in charge)
                string serviceError;
                if (source.Service == settings.Name)
                {
                    switched = true;
                    serviceError = host.RegisterService(settings, newExe, false);
                }
                else
                {
                    registered = true;
                    serviceError = host.RegisterService(settings, newExe, true);
                }
                if (serviceError != null)
                    throw new SetupException(Exit.ServiceFailed, string.Format(T.ServiceFailedZh, settings.DisplayName, serviceError), string.Format(T.ServiceFailedEn, settings.DisplayName, serviceError));

                // 6. ... answering with every instrument the old one showed
                string missing = WaitInstruments(host, settings, target, before, options);
                if (missing != null)
                    throw new SetupException(Exit.ServiceFailed, string.Format(T.InstrumentsMissingZh, missing), string.Format(T.InstrumentsMissingEn, missing));
                result.PausedSeconds = paused.Elapsed.TotalSeconds;

                // 7. the move holds: the new service starts with Windows from now on, and then the marker goes - from here
                //    on a restart of the PC leaves the new folder in charge (before, the old service, which starts with
                //    Windows, and the next run of the installer undoes what is left). Either failing still goes back.
                if (registered && !host.SetAutoStart(settings.Name)) throw new SetupException(Exit.ServiceFailed, T.AutoStartFailedZh, T.AutoStartFailedEn);
                Installer.Retry(() => File.Delete(Marker(target, settings)));
                result.Service = true;
            }
            catch (Exception error)
            {
                // the old installation was touched once its service stopped or a service was registered for the new folder
                bool touched = stopped || registered || switched;
                Tuple<string, string> back = Back(settings, host, options, source, target, newExe, existed, leftovers, touched, registered, switched, out bool oldRuns);
                var failure = error as SetupException;
                string zh = failure != null ? failure.Chinese : error.Message, en = error.Message;
                host.Log("move FAILED: " + en + "; " + back.Item2);
                int code = !touched ? failure?.Code ?? Exit.Failed : oldRuns ? Exit.MovedBack : failure?.Code ?? Exit.Failed;
                throw new SetupException(code, string.Format(T.MoveFailedZh, zh) + " " + back.Item1, string.Format(T.MoveFailedEn, en) + " " + back.Item2);
            }

            // the move holds: the old service goes; the registrations follow the new folder (the old folder itself is left
            // exactly as it is). A step that fails is noted: the program records.
            void Step(string what, Action action)
            {
                try { action(); }
                catch (Exception error) { result.Problems.Add(what + ": " + error.Message); }
            }
            var done = new List<string>();
            if (source.Service != null && source.Service != settings.Name)
                Step("removing the former service " + source.Service, () =>
                {
                    if (host.DeleteService(source.Service)) done.Add("its service removed");
                    else
                    {
                        host.DisableService(source.Service);
                        result.Problems.Add("the former service " + source.Service + " could not be removed and is disabled");
                    }
                });
            Step("the former registrations", () =>
            {
                string registrations = host.RemoveRegistrations(settings, source.Name, source.Folder, newExe, target);
                if (!string.IsNullOrEmpty(registrations)) done.Add(registrations);
            });
            result.FormerRemoved = "the program in " + source.Folder + " (" + source.Name + "): " + (done.Count > 0 ? string.Join(", ", done) : "nothing registered") +
                                   "; its folder is left as it was";
            result.Updated = true;
            Step("the registrations", () => host.Finish(settings, target, result, payload, desktop, formerDesktop));
            result.Details.AddRange(copy.Lines);
            return result;
        }

        /// <summary>What a move into the folder that did not hold left running: the service of the program's name that
        /// runs the folder's program (that move registered it, to start only when asked) goes, whatever runs from the
        /// folder ends, and the old service runs again (an installer ended after it had stopped it, with no restart of
        /// the PC since). The folder's files are cleared by the move that follows.</summary>
        static void UndoUnfinished(SetupSettings settings, IMoveHost host, MoveOptions options, Source source, string target)
        {
            var notes = new List<string>();
            if (source.Service != settings.Name && host.ServiceRegistered(settings.Name) && InstallShared.IsInFolder(host.ServiceExe(settings.Name), target))
            {
                host.StopService(settings.Name, 60);
                if (host.DeleteService(settings.Name)) notes.Add("the service " + settings.Name + " registered there removed");
                else
                {
                    host.DisableService(settings.Name);
                    notes.Add("the service " + settings.Name + " registered there could not be removed and is disabled");
                }
            }
            host.EndProcessesIn(target);
            if (source.Service != null)
            {
                bool started = host.StartService(source.Service, 60);
                string ready = started ? host.WaitReady(settings, source.Folder, options.BackSeconds, source.Service) : "it does not start";
                notes.Add(source.Service + (ready == null ? " runs" : ": " + ready));
            }
            host.Log("move: an unfinished move into " + target + " is undone first" + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""));
        }

        /// <summary>Waits until the new program shows every instrument the old one showed (when the old one answered;
        /// else until it answers at all). Null when it does, else what is missing.</summary>
        static string WaitInstruments(IMoveHost host, SetupSettings settings, string folder, List<string> before, MoveOptions options)
        {
            var waited = Stopwatch.StartNew();
            string missing;
            while (true)
            {
                List<string> now = host.Instruments(settings, folder, out _);
                if (now != null)
                {
                    List<string> lacking = before == null ? new List<string>() : before.Where(k => !now.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (lacking.Count == 0)
                    {
                        host.Log("move: the new service answers with " + (now.Count > 0 ? string.Join(", ", now) : "no instruments") + " after " +
                                 Math.Round(waited.Elapsed.TotalSeconds) + " s");
                        return null;
                    }
                    missing = string.Join(", ", lacking);
                }
                else missing = "(no answer)";
                if (waited.Elapsed.TotalSeconds >= options.InstrumentSeconds) return missing;
                Thread.Sleep(options.LookMilliseconds);
            }
        }

        /// <summary>After a failure: everything as before the move. The new service goes (or the program's service runs
        /// the old folder's program file again), whatever runs from the new folder ends, the new folder goes (what is
        /// in it this move put there), and the old service runs again from the old folder. What was done, Chinese and
        /// English; oldRuns: the old program answers again (or had no service).</summary>
        static Tuple<string, string> Back(SetupSettings settings, IMoveHost host, MoveOptions options, Source source, string target, string newExe,
                                          bool existed, bool leftovers, bool touched, bool registered, bool switched, out bool oldRuns)
        {
            var notes = new List<string>();
            try
            {
                if (registered && host.ServiceRegistered(settings.Name) && InstallShared.SameFile(host.ServiceExe(settings.Name), newExe))
                {
                    host.StopService(settings.Name, 60);
                    if (!host.DeleteService(settings.Name))
                    {
                        host.DisableService(settings.Name);
                        notes.Add("the new service could not be removed and is disabled");
                    }
                }
                if (switched && source.Service != null)
                {
                    host.StopService(source.Service, 60);
                    notes.Add(host.PointService(source.Service, source.Exe));
                }
            }
            catch (Exception error) { notes.Add("the new service: " + error.Message); }
            try { host.EndProcessesIn(target); } catch (Exception) { }

            // the new folder: only what this move put there
            try
            {
                if (Directory.Exists(target))
                {
                    if (!existed || leftovers)
                    {
                        if (!DeleteFolder(target)) notes.Add("some files stay in " + target + " (in use): running the installer again clears them");
                    }
                    else
                    {
                        ClearFolder(target);         // it was there, empty: it stays, empty
                    }
                }
            }
            catch (Exception error) { notes.Add("clearing " + target + ": " + error.Message); }

            // the old service, from the old folder
            oldRuns = true;
            if (source.Service != null)
            {
                bool started = host.StartService(source.Service, 60);
                string ready = started ? host.WaitReady(settings, source.Folder, options.BackSeconds, source.Service) : "it does not start";
                oldRuns = started && ready == null;
                if (!oldRuns) notes.Add(source.Service + ": " + ready);
            }
            string extra = notes.Count > 0 ? " (" + string.Join("; ", notes.Where(n => !string.IsNullOrEmpty(n))) + ")" : "";
            if (!touched && oldRuns) return Tuple.Create(T.NothingChangedZh + extra, T.NothingChangedEn + extra);
            return oldRuns ? Tuple.Create(string.Format(T.MovedBackZh, source.Folder) + extra, string.Format(T.MovedBackEn, source.Folder) + extra)
                           : Tuple.Create(string.Format(T.ServiceDownZh, source.Service + extra), string.Format(T.ServiceDownEn, source.Service + extra));
        }

        /// <summary>Removes what is in the folder (not the folder).</summary>
        static void ClearFolder(string folder)
        {
            foreach (string dir in Directory.GetDirectories(folder))
            {
                ResetAttributes(dir);
                Installer.Retry(() => Directory.Delete(dir, true), 10, 500);
            }
            foreach (string file in Directory.GetFiles(folder))
                if (!InstallShared.TryDelete(file)) throw new IOException("in use: " + file);
        }

        /// <summary>Removes the folder with everything in it; false when something in it could not be removed.</summary>
        static bool DeleteFolder(string folder)
        {
            try
            {
                ResetAttributes(folder);
                Installer.Retry(() => Directory.Delete(folder, true), 10, 500);
                return true;
            }
            catch (Exception) { return !Directory.Exists(folder); }
        }

        static void ResetAttributes(string folder)
        {
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                            File.SetAttributes(path, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                    }
                    catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    /// <summary>The data folder copied for a move: everything but the web caches (the windows' WebView2 and browser
    /// profiles: rebuilt by themselves), twice - while the old program still records, then, stopped, what changed
    /// meanwhile and what is gone - and then every file checked against the original: size and SHA-256 (one that differs
    /// is copied once more). The original is only read. A log file another program holds for itself alone (a window of
    /// the old program) is not copied and is named; anything else that cannot be read stops the move.</summary>
    sealed class DataCopy
    {
        readonly string from, to;
        readonly IMoveHost host;
        readonly Dictionary<string, Stamp> copied = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> LeftOut = new List<string>();      // caches not copied
        public readonly List<string> Skipped = new List<string>();      // log files held, not copied
        public readonly List<string> Lines = new List<string>();        // per file: what was checked
        public int Files;
        public long Bytes;

        sealed class Stamp
        {
            public long Size;
            public DateTime Written;
        }

        public DataCopy(string from, string to, IMoveHost host)
        {
            this.from = from;
            this.to = to;
            this.host = host;
        }

        /// <summary>True for a web cache: the windows' profiles in the data folder (old layout: browser\, webview\,
        /// webview-viewer\, webview*.unused-*, webview*.copying; new layout: System\old-cache\, a user's webview*\ or
        /// EBWebView\).</summary>
        public static bool IsCache(string relativeFolder)
        {
            string[] parts = relativeFolder.Split('\\');
            string first = parts[0];
            if (parts.Length == 1 && (first.Equals("browser", StringComparison.OrdinalIgnoreCase) || first.StartsWith("webview", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (first.Equals(InstallShared.SystemFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 && parts[1].Equals("old-cache", StringComparison.OrdinalIgnoreCase)) return true;
                if (parts.Length == 4 && parts[1].Equals(InstallShared.UsersFolder, StringComparison.OrdinalIgnoreCase) &&
                    (parts[3].StartsWith("webview", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("EBWebView", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>The bytes a copy of this data folder takes (caches left out).</summary>
        public static long PlannedBytes(string from)
        {
            long total = 0;
            foreach (Tuple<string, FileInfo> file in Walk(from, null)) total += file.Item2.Length;
            return total;
        }

        /// <summary>The longest relative path among the files to copy, or null for none.</summary>
        public static string LongestFile(string from)
        {
            string longest = null;
            foreach (Tuple<string, FileInfo> file in Walk(from, null))
                if (longest == null || file.Item1.Length > longest.Length) longest = file.Item1;
            return longest;
        }

        /// <summary>The files to copy: (relative path, file), folders' reparse points not followed; caches named in leftOut.</summary>
        static IEnumerable<Tuple<string, FileInfo>> Walk(string root, List<string> leftOut)
        {
            var pending = new Stack<string>();
            pending.Push("");
            while (pending.Count > 0)
            {
                string rel = pending.Pop();
                var dir = new DirectoryInfo(rel.Length == 0 ? root : Path.Combine(root, rel));
                foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos())
                {
                    string path = rel.Length == 0 ? entry.Name : rel + "\\" + entry.Name;
                    if (entry is DirectoryInfo)
                    {
                        if (IsCache(path) || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            leftOut?.Add(path + "\\");
                            continue;
                        }
                        pending.Push(path);
                    }
                    else if (rel.Length == 0 && entry.Name.StartsWith("ini-", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase))
                        leftOut?.Add(path);               // the old browser profile's marker files
                    else yield return Tuple.Create(path, (FileInfo)entry);
                }
            }
        }

        /// <summary>The folders to make (empty ones included).</summary>
        IEnumerable<string> Folders()
        {
            var pending = new Stack<string>();
            pending.Push("");
            while (pending.Count > 0)
            {
                string rel = pending.Pop();
                if (rel.Length > 0) yield return rel;
                var dir = new DirectoryInfo(rel.Length == 0 ? from : Path.Combine(from, rel));
                foreach (DirectoryInfo sub in dir.EnumerateDirectories())
                {
                    string path = rel.Length == 0 ? sub.Name : rel + "\\" + sub.Name;
                    if (!IsCache(path) && (sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(path);
                }
            }
        }

        /// <summary>Copies what is new or changed since the last pass and removes what is gone from the original.</summary>
        public void Pass()
        {
            Directory.CreateDirectory(to);
            foreach (string rel in Folders())
            {
                string made = Path.Combine(to, rel);
                Directory.CreateDirectory(made);
                try
                {
                    if ((File.GetAttributes(Path.Combine(from, rel)) & FileAttributes.Hidden) != 0) File.SetAttributes(made, File.GetAttributes(made) | FileAttributes.Hidden);
                }
                catch (Exception) { }
            }
            LeftOut.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Tuple<string, FileInfo> file in Walk(from, LeftOut).ToList())
            {
                string rel = file.Item1;
                seen.Add(rel);
                FileInfo info = file.Item2;
                if (copied.TryGetValue(rel, out Stamp stamp) && stamp.Size == info.Length && stamp.Written == info.LastWriteTimeUtc) continue;
                var now = new Stamp { Size = info.Length, Written = info.LastWriteTimeUtc };
                if (!CopyOne(rel)) continue;
                copied[rel] = now;
            }
            foreach (string rel in copied.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                InstallShared.TryDelete(Path.Combine(to, rel));         // gone from the original (a temporary file)
                copied.Remove(rel);
            }
        }

        /// <summary>One file, read with every kind of sharing (the old program may still write it) and written through
        /// to the disk, with its times. False for a log file held for itself alone by another program.</summary>
        bool CopyOne(string rel)
        {
            string source = Path.Combine(from, rel), target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try
            {
                Installer.Retry(() =>
                {
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                }, 8, 300);
                var info = new FileInfo(source);
                File.SetCreationTimeUtc(target, info.CreationTimeUtc);
                File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
                if ((info.Attributes & FileAttributes.Hidden) != 0) File.SetAttributes(target, FileAttributes.Hidden);
                Skipped.Remove(rel);
                return true;
            }
            catch (IOException) when (IsLog(rel) && File.Exists(source))
            {
                if (!Skipped.Contains(rel)) Skipped.Add(rel);
                return false;
            }
        }

        static bool IsLog(string rel)
        {
            string[] parts = rel.Split('\\');
            return parts.Length >= 2 && (parts[parts.Length - 2].Equals("log", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Every file of the original against its copy: size and SHA-256; one that differs is copied again and
        /// checked again; still different, or missing: an IOException. Lines and the totals are filled.</summary>
        public void Verify()
        {
            Lines.Clear();
            Files = 0;
            Bytes = 0;
            foreach (Tuple<string, FileInfo> file in Walk(from, null).ToList())
            {
                string rel = file.Item1;
                if (Skipped.Contains(rel)) continue;
                string source = Path.Combine(from, rel), target = Path.Combine(to, rel);
                string original = Hash(source, out long size), copy = File.Exists(target) ? Hash(target, out long copySize) : null;
                if (copy == null || copy != original || new FileInfo(target).Length != size)
                {
                    host.Log("move: " + rel + " differs from its copy: copied again");
                    if (!CopyOne(rel)) continue;
                    original = Hash(source, out size);
                    copy = Hash(target, out _);
                    if (copy != original) throw new IOException("the copy of " + rel + " does not match the original (SHA-256)");
                }
                Files++;
                Bytes += size;
                Lines.Add("moved and checked: " + rel + " " + size + " bytes sha256 " + original);
            }
            foreach (string rel in Skipped) Lines.Add("not copied (held by another program): " + rel);
        }

        static string Hash(string path, out long size)
        {
            string result = null;
            long length = 0;
            Installer.Retry(() =>
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                using (SHA256 sha = SHA256.Create())
                {
                    length = stream.Length;
                    result = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
                }
            }, 8, 300);
            size = length;
            return result;
        }
    }

    /// <summary>The move's work on this PC, done for real.</summary>
    sealed class SystemMoveHost : IMoveHost
    {
        readonly SetupSettings settings;

        public SystemMoveHost(SetupSettings settings) { this.settings = settings; }

        public string ServiceExe(string name) => InstallShared.ServiceExe(name);
        public string ServiceImagePath(string name) => InstallShared.ServiceImagePath(name);
        public bool ServiceRegistered(string name) => InstallShared.ServiceRegistered(name);
        public string RegisteredFolder(string name) => InstallShared.RegisteredFolder(name);
        public Tuple<string, string> Running(SetupSettings s) => Installer.Running(s);

        public bool StopService(string name, int seconds)
        {
            try
            {
                Installer.StopService(name, seconds);
                return true;
            }
            catch (SetupException error) when (error.Code == Exit.Running) { return false; }     // started again
        }

        public bool StartService(string name, int seconds) => Installer.StartService(name, seconds, out _);
        public string RegisterService(SetupSettings s, string exe, bool demandStart) => Installer.RegisterService(s, exe, demandStart);
        public string PointService(string name, string exe) => Installer.PointService(name, exe, null);
        public bool SetAutoStart(string name) => Installer.Sc("config \"" + name + "\" start= auto") == 0;

        public bool DeleteService(string name)
        {
            Installer.ScDelete(name);
            for (int i = 0; i < 20 && InstallShared.ServiceRegistered(name); i++) Thread.Sleep(500);      // gone once nothing holds it
            return !InstallShared.ServiceRegistered(name);
        }

        public void DisableService(string name) => Installer.Sc("config \"" + name + "\" start= disabled");

        public string WaitReady(SetupSettings s, string folder, int seconds, string serviceName) => Installer.WaitReady(s, folder, seconds, serviceName);

        public List<string> Instruments(SetupSettings s, string folder, out List<string> measuring) =>
            Installer.Instruments(Installer.ReadState(s.Port, folder, out _), out measuring);

        public long FreeBytes(string folder)
        {
            try
            {
                string root = Path.GetPathRoot(folder);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return -1;
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : -1;
            }
            catch (Exception) { return -1; }
        }

        public List<string> ProcessesIn(string folder)
        {
            var found = new List<string>();
            foreach (Process process in Process.GetProcesses())
                using (process)
                    try
                    {
                        string file = process.MainModule.FileName;
                        if (InstallShared.IsInFolder(file, folder)) found.Add(process.ProcessName + " " + process.Id);
                    }
                    catch (Exception) { }
            return found;
        }

        public void EndProcessesIn(string folder)
        {
            foreach (Process process in Process.GetProcesses())
                using (process)
                    try
                    {
                        if (!InstallShared.IsInFolder(process.MainModule.FileName, folder)) continue;
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception) { }
        }

        public void CloseWindows(SetupSettings s, string sourceName)
        {
            InstallShared.CloseWindows(Path.GetFileNameWithoutExtension(s.Exe), -1, 15, null);
            if (!sourceName.Equals(Path.GetFileNameWithoutExtension(s.Exe), StringComparison.OrdinalIgnoreCase))
                InstallShared.CloseWindows(sourceName, -1, 15, null);
        }

        public bool HadDesktopShortcut(string sourceName, string sourceFolder) => Installer.DesktopLinkInto(sourceName, sourceFolder) != null;

        /// <summary>What registers the program in the old folder goes - its shortcuts there, its entry in the list of
        /// apps, its .pk file type, its tray's start at sign-in - and other shortcuts to it (on a desktop, pinned to the
        /// taskbar) start the program in the new folder. The old folder's files are not touched.</summary>
        public string RemoveRegistrations(SetupSettings s, string sourceName, string sourceFolder, string newExe, string newFolder)
        {
            var done = new List<string>();
            string oldExe = Path.Combine(sourceFolder, sourceName + ".exe");
            int links = 0;
            foreach (string link in InstallShared.Shortcuts(InstallShared.DisplayName(sourceName)).Concat(InstallShared.Shortcuts(sourceName))
                         .Concat(new[] { InstallShared.ExperimentsShortcut(InstallShared.DisplayName(sourceName)) }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string target = File.Exists(link) ? InstallShared.ShortcutTarget(link) : null;
                if (target != null && InstallShared.IsInFolder(target, sourceFolder) && InstallShared.TryDelete(link)) links++;
            }
            if (links > 0) done.Add(links + " shortcuts removed");
            int moved = Installer.RepointLinks(sourceName, new List<string> { sourceFolder }, newExe, newFolder, s.DisplayName);
            if (moved > 0) done.Add(moved + " other shortcuts now start the new folder's program");
            try { if (InstallShared.RemoveEntry(sourceName, sourceFolder, oldExe)) done.Add("its entry in the list of apps removed"); } catch (Exception) { }
            try { if (InstallShared.UnregisterPk(sourceName, oldExe)) done.Add("its .pk file type removed"); } catch (Exception) { }
            if (InstallShared.RemoveRunEntry(sourceName, oldExe)) done.Add("its tray's start at sign-in removed");
            return string.Join(", ", done);
        }

        public void Finish(SetupSettings s, string folder, InstallResult result, Payload payload, DesktopChoice desktop, bool formerDesktop)
        {
            Installer.Finish(s, folder, result, payload, desktop, true, formerDesktop);
            Installer.ExperimentsShortcut(s, folder, result);
        }

        public void Log(string line) => InstallShared.Log(settings.Name, line);
    }

    /// <summary>The window: target folder, desktop shortcut, Install; afterwards Open now.</summary>
    sealed class SetupForm : Form
    {
        readonly SetupSettings settings;
        readonly Payload payload;
        readonly TextBox folderBox = new TextBox();
        readonly Button browseButton = new Button(), installButton = new Button(), openButton = new Button(), closeButton = new Button();
        readonly CheckBox desktopBox = new CheckBox();
        readonly RadioButton modeFullRadio = new RadioButton(), modeAnalysisRadio = new RadioButton();
        readonly Label hintLabel = new Label(), statusLabel = new Label();
        bool busy;
        string installedExe;
        string moveFrom;           // the folder the program is in now, when installing into the chosen one moves it

        public bool Installed => installedExe != null;
        bool IsAnalysisMode => settings.IsStandaloneAnalysis || modeAnalysisRadio.Checked;

        public SetupForm(SetupSettings settings, Payload payload, string folder, bool analysisOnlyArg = false, bool fullArg = false)
        {
            this.settings = settings;
            this.payload = payload;
            Font = SystemFonts.MessageBoxFont;
            Text = string.Format(T.WindowTitle, settings.DisplayName);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;         // every size below follows the font, which follows the screen's DPI
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            int u = Font.Height;
            int textWidth = 30 * u;
            int wrapWidth = textWidth + 7 * u;

            var table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(u, u, u, u / 2),
                Location = Point.Empty,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = new Label
            {
                AutoSize = true,
                Text = string.Format(T.Title, settings.DisplayName),
                Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.25f, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 0, 0, u / 4),
            };
            var version = new Label { AutoSize = true, Text = string.Format(T.Version, settings.Version), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, u) };
            var folderLabel = new Label { AutoSize = true, Text = T.FolderLabel, Margin = new Padding(0, 0, 0, u / 4) };
            folderBox.Width = textWidth;
            folderBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            folderBox.Margin = new Padding(0, 0, u / 2, 0);
            folderBox.Text = folder;
            browseButton.AutoSize = true;
            browseButton.Text = T.Browse;
            browseButton.Margin = new Padding(0);
            hintLabel.AutoSize = true;
            hintLabel.MaximumSize = new Size(wrapWidth, 0);
            hintLabel.ForeColor = SystemColors.GrayText;
            hintLabel.Margin = new Padding(0, u / 3, 0, u / 2);

            var modePanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, u / 2),
            };
            if (!settings.IsStandaloneAnalysis)
            {
                var modeLabel = new Label { AutoSize = true, Text = T.ModeLabel, Margin = new Padding(0, 0, 0, u / 4) };
                modeFullRadio.AutoSize = true;
                modeFullRadio.MaximumSize = new Size(wrapWidth, 0);
                modeFullRadio.Text = T.ModeFull;
                modeFullRadio.Margin = new Padding(u / 2, 0, 0, u / 4);
                modeAnalysisRadio.AutoSize = true;
                modeAnalysisRadio.MaximumSize = new Size(wrapWidth, 0);
                modeAnalysisRadio.Text = T.ModeAnalysis;
                modeAnalysisRadio.Margin = new Padding(u / 2, 0, 0, 0);
                bool startAnalysis = !fullArg && (analysisOnlyArg || File.Exists(Path.Combine(folder, InstallShared.AnalysisOnlyMarker)));
                // (a marker file beside a registered recorder is a stray one: the full installation is offered)
                if (Installer.RecorderWins(settings, startAnalysis, analysisOnlyArg)) startAnalysis = false;
                modeAnalysisRadio.Checked = startAnalysis;
                modeFullRadio.Checked = !startAnalysis;
                modePanel.Controls.Add(modeLabel);
                modePanel.Controls.Add(modeFullRadio);
                modePanel.Controls.Add(modeAnalysisRadio);
            }

            desktopBox.AutoSize = true;
            desktopBox.Text = T.Desktop;
            desktopBox.Margin = new Padding(0, 0, 0, u / 2);
            statusLabel.AutoSize = true;
            statusLabel.MaximumSize = new Size(wrapWidth, 0);
            statusLabel.Margin = new Padding(0, 0, 0, u / 2);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, u / 2, 0, 0),
                WrapContents = false,
            };
            foreach (Button button in new[] { closeButton, installButton, openButton })
            {
                button.AutoSize = true;
                button.MinimumSize = new Size(6 * u, 0);
                button.Margin = new Padding(u / 2, 0, 0, 0);
                buttons.Controls.Add(button);
            }
            closeButton.Text = T.Close;
            installButton.Text = T.Install;
            openButton.Text = T.Open;
            openButton.Visible = false;

            int row = 0;
            void Wide(Control control)
            {
                table.Controls.Add(control, 0, row);
                table.SetColumnSpan(control, 2);
                row++;
            }
            Wide(title);
            Wide(version);
            Wide(folderLabel);
            table.Controls.Add(folderBox, 0, row);
            table.Controls.Add(browseButton, 1, row);
            row++;
            Wide(hintLabel);
            if (!settings.IsStandaloneAnalysis) Wide(modePanel);
            Wide(desktopBox);
            Wide(statusLabel);
            Wide(buttons);
            Controls.Add(table);
            AcceptButton = installButton;
            CancelButton = closeButton;
            ActiveControl = installButton;

            // the desktop shortcut: kept as it is on an update, following the former program's where that is replaced,
            // offered on a first installation
            desktopBox.Checked = Installer.DesktopByDefault(settings, folder);

            folderBox.TextChanged += (sender, e) => UpdateHint();
            modeFullRadio.CheckedChanged += (sender, e) => UpdateHint();
            modeAnalysisRadio.CheckedChanged += (sender, e) => UpdateHint();
            browseButton.Click += (sender, e) => Browse();
            installButton.Click += async (sender, e) => await InstallClicked();
            openButton.Click += (sender, e) => OpenProgram();
            closeButton.Click += (sender, e) => Close();
            FormClosing += (sender, e) => { if (busy) e.Cancel = true; };
            Shown += async (sender, e) => await WarnIfRunning();
            UpdateHint();
        }

        void UpdateHint()
        {
            if (Installed) return;
            var chinese = new List<string>();
            var english = new List<string>();
            bool update = false;
            moveFrom = null;
            try
            {
                string folder = Installer.FullFolder(folderBox.Text);
                FolderInfo info = Installer.Inspect(settings, folder);
                string elsewhere = Installer.InstalledElsewhere(settings, folder);
                if (info.OtherProgram != null)
                {
                    chinese.Add(string.Format(T.OtherProgramZh, info.OtherProgram));
                    english.Add(string.Format(T.OtherProgramEn, info.OtherProgram));
                }
                else if (elsewhere != null)
                {
                    // another folder than the one the program is in: a move
                    Tuple<string, string> problem = Mover.TargetProblem(settings, folder, new SystemMoveHost(settings));
                    if (problem != null)
                    {
                        chinese.Add(problem.Item1);
                        english.Add(problem.Item2);
                    }
                    else
                    {
                        moveFrom = elsewhere;
                        chinese.Add(string.Format(T.MoveHereZh, settings.DisplayName, elsewhere));
                        english.Add(string.Format(T.MoveHereEn, settings.DisplayName, elsewhere));
                    }
                }
                else
                {
                    update = info.OurInstall || info.NewProgram;
                    if (!info.Exists)
                    {
                        chinese.Add(T.NewFolderZh);
                        english.Add(T.NewFolderEn);
                    }
                    if (update)
                    {
                        chinese.Add(string.Format(T.UpdateHereZh, settings.DisplayName));
                        english.Add(string.Format(T.UpdateHereEn, settings.DisplayName));
                    }
                    else if (info.FormerProgram)
                    {
                        chinese.Add(string.Format(T.FormerHereZh, settings.Former));
                        english.Add(string.Format(T.FormerHereEn, settings.Former));
                    }
                    if (info.OldProgram)
                    {
                        string old = Path.GetFileName(Installer.FreeName(folder, settings.Name + "-python-old", ".exe"));
                        chinese.Add(string.Format(T.OldProgramZh, settings.Exe, old));
                        english.Add(string.Format(T.OldProgramEn, settings.Exe, old));
                    }
                    string data = Path.GetFileName(InstallShared.DataFolderIn(folder));
                    if (IsAnalysisMode)
                    {
                        chinese.Add(T.AnalysisOnlyHintZh);
                        english.Add(T.AnalysisOnlyHintEn);
                    }
                    else if (info.DataFolder)
                    {
                        chinese.Add(string.Format(T.DataStaysZh, data));
                        english.Add(string.Format(T.DataStaysEn, data));
                    }
                    else
                    {
                        chinese.Add(string.Format(T.DataHereZh, data));
                        english.Add(string.Format(T.DataHereEn, data));
                    }
                }
            }
            catch (SetupException problem)
            {
                chinese.Add(problem.Chinese);
                english.Add(problem.Message);
            }
            catch (Exception) { }
            hintLabel.Text = string.Join("\n", chinese.Concat(english));
            installButton.Text = moveFrom != null ? T.MoveButton : update ? T.Update : T.Install;
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = T.BrowseDescription, ShowNewFolderButton = true })
            {
                try
                {
                    string current = folderBox.Text.Trim().Trim('"');
                    while (!string.IsNullOrEmpty(current) && !Directory.Exists(current)) current = Path.GetDirectoryName(current);
                    if (!string.IsNullOrEmpty(current)) dialog.SelectedPath = current;
                }
                catch (Exception) { }
                if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath)) return;
                folderBox.Text = Installer.ChosenFolder(settings, dialog.SelectedPath);
            }
        }

        async Task InstallClicked()
        {
            if (busy || Installed) return;
            string folderText = folderBox.Text;
            string from = moveFrom;
            bool analysisOnly = IsAnalysisMode;
            DesktopChoice desktop = desktopBox.Checked ? DesktopChoice.Create : DesktopChoice.Remove;

            // an instrument measures: the recording pauses for the moment of the update or the move (it loses nothing);
            // a move is a bigger step: both are asked first
            List<string> measuring = new List<string>();
            if (!settings.IsStandaloneAnalysis && (!analysisOnly || Installer.ServiceRunning(settings.Name)))
            {
                SetBusy(true);
                ShowStatus(T.Checking, SystemColors.ControlText);
                measuring = await Task.Run(() => Installer.MeasuringNow(settings, Installer.InstalledFolder(settings) ?? folderText));
                SetBusy(false);
                ShowStatus("", SystemColors.ControlText);
            }
            // "analysis only" where the recorder's service is registered removes the recorder: never while an instrument
            // measures, and else only after a question of its own (not the one about a pause of 10 seconds)
            bool removesRecorder = from == null && analysisOnly && !settings.IsStandaloneAnalysis && InstallShared.ServiceRegistered(settings.Name);
            string question = null;
            string names = string.Join(", ", measuring);
            if (from != null)
                question = string.Format(T.MoveAskZh, settings.DisplayName, from, folderText) + (measuring.Count > 0 ? "\n" + string.Format(T.MeasuringNowZh, names) : "") +
                           "\n\n" + string.Format(T.MoveAskEn, settings.DisplayName, from, folderText) + (measuring.Count > 0 ? "\n" + string.Format(T.MeasuringNowEn, names) : "");
            else if (removesRecorder)
            {
                if (measuring.Count > 0)
                {
                    ShowStatus(Installer.MeasuringKeepsRecorder(measuring).Text, Color.Firebrick);
                    return;
                }
                question = T.RemoveRecorderAskZh + "\n\n" + T.RemoveRecorderAskEn;
            }
            else if (measuring.Count > 0)
                question = string.Format(T.PauseAskZh, names) + "\n\n" + string.Format(T.PauseAskEn, names);
            if (question != null && !Ask.Continue(this, settings.DisplayName, question))
            {
                ShowStatus(T.NotChangedZh + "\n" + T.NotChangedEn, SystemColors.GrayText);
                return;
            }

            SetBusy(true);
            ShowStatus(from != null ? T.Moving : T.Installing, SystemColors.ControlText);
            try
            {
                InstallResult result = await Task.Run(() => from != null
                    ? Mover.Move(settings, payload, folderText, desktop, new SystemMoveHost(settings), new MoveOptions())
                    : Installer.Install(settings, payload, folderText, desktop, analysisOnly));
                if (!result.AnalysisOnly) Program.StartTray(result);
                Program.Detail(settings.Name, result);
                Program.Report(settings.Name, result.Summary(settings), false);
                installedExe = result.ExePath;
                folderBox.Text = result.Folder;
                hintLabel.Text = "";
                installButton.Visible = false;
                openButton.Visible = true;
                AcceptButton = openButton;
                ShowStatus(Done(result), (result.AnalysisOnly || result.Service) && result.Problems.Count == 0 ? Color.FromArgb(0, 110, 40) : Color.DarkOrange);
            }
            catch (Exception error)
            {
                int code = error is SetupException known ? known.Code : Exit.Failed;
                Program.Report(settings.Name, (from != null ? "move" : "install") + " FAILED (exit " + code + "): " + error.Message + Installer.ServiceNote(error, false) +
                                              " [folder " + folderText + "]", false);
                string text = error is SetupException e ? e.Text : string.Format(T.Failed, error.Message);
                string service = Installer.ServiceNote(error, true).Trim();
                if (service.Length > 0) text += "\n" + service + "\n" + Installer.ServiceNote(error, false).Trim();
                ShowStatus(text, Color.Firebrick);
            }
            finally
            {
                SetBusy(false);
            }
            if (Installed) openButton.Focus();
        }

        string Done(InstallResult result)
        {
            var chinese = new StringBuilder(result.Moved ? string.Format(T.MovedZh, result.Folder) : string.Format(result.Updated ? T.UpdatedZh : T.InstalledZh, result.Folder));
            var english = new StringBuilder(result.Moved ? string.Format(T.MovedEn, result.Folder) : string.Format(result.Updated ? T.UpdatedEn : T.InstalledEn, result.Folder));
            if (result.Moved)
            {
                chinese.Append("\n" + string.Format(T.MovedKeptZh, result.MovedFrom));
                english.Append(" " + string.Format(T.MovedKeptEn, result.MovedFrom));
            }
            if (result.FormerReplaced)
            {
                chinese.Append("\n" + string.Format(T.FormerReplacedZh, settings.Former));
                english.Append(" " + string.Format(T.FormerReplacedEn, settings.Former));
            }
            if (result.RenamedOld != null)
            {
                chinese.Append(string.Format(T.RenamedZh, result.RenamedOld));
                english.Append(string.Format(T.RenamedEn, result.RenamedOld));
            }
            if (result.KeptAside.Count > 0)
            {
                chinese.Append(string.Format(T.KeptZh, string.Join(", ", result.KeptAside)));
                english.Append(string.Format(T.KeptEn, string.Join(", ", result.KeptAside)));
            }
            if (result.OpenWindows.Count > 0)
            {
                chinese.Append(T.WindowOpenZh);
                english.Append(T.WindowOpenEn);
            }
            if (result.Problems.Count > 0)
            {
                chinese.Append("\n" + string.Format(T.NoteZh, string.Join("; ", result.Problems)));
                english.Append(" " + string.Format(T.NoteEn, string.Join("; ", result.Problems)));
            }
            return chinese + "\n" + english;
        }

        async Task WarnIfRunning()
        {
            if (settings.IsStandaloneAnalysis) return;
            Tuple<string, string> running = await Task.Run(() =>
            {
                try { return Installer.Running(settings); }
                catch (Exception) { return null; }
            });
            if (running != null && !busy && !Installed)
                ShowStatus(string.Format(T.RunningZh, settings.DisplayName, running.Item1) + "\n" + string.Format(T.RunningEn, settings.DisplayName, running.Item2), Color.DarkOrange);
        }

        void OpenProgram()
        {
            try
            {
                // as the signed-in user, not with the installer's administrator rights: through the desktop's shell, else
                // through Explorer (by its full path: a bare name would be looked for first in the installer's own folder)
                if (!InstallShared.StartAsShellUser(installedExe, "", Path.GetDirectoryName(installedExe)))
                {
                    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    using (Process.Start(new ProcessStartInfo(explorer, "\"" + installedExe + "\"") { UseShellExecute = false })) { }
                }
                Close();
            }
            catch (Exception error)
            {
                ShowStatus(string.Format(T.Failed, error.Message), Color.Firebrick);
            }
        }

        void SetBusy(bool on)
        {
            busy = on;
            UseWaitCursor = on;
            bool editable = !on && !Installed;
            folderBox.ReadOnly = !editable;
            browseButton.Enabled = editable;
            modeFullRadio.Enabled = editable;
            modeAnalysisRadio.Enabled = editable;
            desktopBox.Enabled = editable;
            installButton.Enabled = editable;
            closeButton.Enabled = !on;
        }

        void ShowStatus(string text, Color color)
        {
            statusLabel.ForeColor = color;
            statusLabel.Text = text;
        }
    }

    /// <summary>A question with "Continue" and "Cancel" (Chinese with the English beneath, as the window's buttons).</summary>
    static class Ask
    {
        public static bool Continue(IWin32Window owner, string caption, string text)
        {
            using (var form = new Form())
            {
                form.Font = SystemFonts.MessageBoxFont;
                form.Text = caption;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.AutoScaleMode = AutoScaleMode.None;
                form.AutoSize = true;
                form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                int u = form.Font.Height;
                var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(u) };
                var label = new Label { AutoSize = true, MaximumSize = new Size(34 * u, 0), Text = text, Margin = new Padding(0, 0, 0, u) };
                var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, WrapContents = false };
                var cancel = new Button { AutoSize = true, MinimumSize = new Size(6 * u, 0), Text = T.Cancel, DialogResult = DialogResult.Cancel, Margin = new Padding(u / 2, 0, 0, 0) };
                var go = new Button { AutoSize = true, MinimumSize = new Size(6 * u, 0), Text = T.Continue, DialogResult = DialogResult.OK, Margin = new Padding(u / 2, 0, 0, 0) };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(go);
                table.Controls.Add(label, 0, 0);
                table.Controls.Add(buttons, 0, 1);
                form.Controls.Add(table);
                form.AcceptButton = go;
                form.CancelButton = cancel;
                return form.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    /// <summary>The installer's texts. Window texts: Chinese, the English beneath (the users are Chinese).</summary>
    static class T
    {
        public const string WindowTitle = "{0} \u5b89\u88c5 / Setup";
        public const string Title = "\u5b89\u88c5 {0}\nInstall {0}";
        public const string Version = "\u7248\u672c / Version {0}";
        public const string FolderLabel = "\u5b89\u88c5\u5230\u8fd9\u4e2a\u6587\u4ef6\u5939\uff1a\nInstall into this folder:";
        public const string Browse = "\u6d4f\u89c8\u2026\nBrowse\u2026";
        public const string BrowseDescription = "\u9009\u62e9\u5b89\u88c5\u6587\u4ef6\u5939\uff08\u88c5\u8fc7\u7684\u8bdd\uff0c\u9009\u5b83\u73b0\u5728\u6240\u5728\u7684\u6587\u4ef6\u5939\uff1b\u9009\u522b\u7684\u6587\u4ef6\u5939\u5c31\u4f1a\u628a\u5b83\u642c\u8fc7\u53bb\uff09\nChoose the folder (where it is installed now; another folder moves it there)";
        public const string Desktop = "\u5728\u684c\u9762\u4e0a\u653e\u4e00\u4e2a\u5feb\u6377\u65b9\u5f0f\nPut a shortcut on the desktop";
        public const string ModeLabel = "\u5b89\u88c5\u6a21\u5f0f / Install mode:";
        public const string ModeFull = "\u5b8c\u6574\u5b89\u88c5\uff08\u4eea\u5668\u5b9e\u65f6\u76d1\u6d4b + \u5206\u6790\uff0c\u9ed8\u8ba4\uff09\nFull install (live instrument monitoring + analysis)";
        public const string ModeAnalysis = "\u4ec5\u5b89\u88c5\u5206\u6790\u8f6f\u4ef6\uff08\u65e0\u540e\u53f0\u670d\u52a1\uff0c\u9002\u5408\u975e\u4eea\u5668\u7535\u8111\uff09\nAnalysis software only (no background service)";
        public const string AnalysisOnlyHintZh = "\u4ec5\u5b89\u88c5\u72ec\u7acb\u5206\u6790\u8f6f\u4ef6\uff08\u4e0d\u5b89\u88c5\u540e\u53f0\u670d\u52a1\u548c\u6258\u76d8\uff0c\u652f\u6301\u53cc\u51fb\u6253\u5f00 .pk / .pkproj \u6587\u4ef6\u4e0e GitHub \u81ea\u52a8\u66f4\u65b0\uff09\u3002";
        public const string AnalysisOnlyHintEn = "Installs the standalone analysis window only (no background service or tray; registers .pk / .pkproj and GitHub auto-updates).";
        public const string Install = "\u5b89\u88c5\nInstall";
        public const string Update = "\u66f4\u65b0\nUpdate";
        public const string MoveButton = "\u642c\u5230\u8fd9\u91cc\nMove here";
        public const string Open = "\u7acb\u5373\u6253\u5f00\nOpen now";
        public const string Close = "\u5173\u95ed\nClose";
        public const string Continue = "\u7ee7\u7eed\nContinue";
        public const string Cancel = "\u53d6\u6d88\nCancel";
        public const string Checking = "\u6b63\u5728\u67e5\u770b\u2026\nChecking\u2026";
        public const string Installing = "\u6b63\u5728\u5b89\u88c5\u2026\nInstalling\u2026";
        public const string Moving = "\u6b63\u5728\u642c\uff0c\u8bf7\u7a0d\u5019\uff08\u7ea6 1 \u5206\u949f\uff09\u2026\nMoving, please wait (about a minute)\u2026";
        public const string Failed = "\u5b89\u88c5\u5931\u8d25\uff1a{0}\nInstallation failed: {0}";
        public const string Usage = "{0}-Setup.exe [/S] [/NODESKTOP] [/ANALYSISONLY] [/FULL] [/RELAUNCH] [--move-to [<folder>]] [/D=<folder>]\n\n" +
                                    "/S  \u4e0d\u663e\u793a\u7a97\u53e3\uff0c\u7ed3\u679c\u89c1\u9000\u51fa\u7801\uff080 = \u6210\u529f\uff09\u548c %TEMP%\\{0}-setup.log\n    no window; the exit code (0 = installed) and %TEMP%\\{0}-setup.log tell the result\n" +
                                    "/NODESKTOP  \u4e0d\u5728\u684c\u9762\u4e0a\u65b0\u5efa\u5feb\u6377\u65b9\u5f0f\n    no new desktop shortcut\n" +
                                    "/ANALYSISONLY  \u4ec5\u5b89\u88c5\u5206\u6790\u8f6f\u4ef6\uff08\u65e0\u540e\u53f0\u670d\u52a1\u548c\u6258\u76d8\uff09\n    analysis software only (no background service or tray)\n" +
                                    "/FULL  \u5b8c\u6574\u5b89\u88c5\uff08\u5e26\u540e\u53f0\u8bb0\u5f55\u670d\u52a1\uff09\uff0c\u5373\u4f7f\u6587\u4ef6\u5939\u88ab\u6807\u6210\u4e86\u4ec5\u5206\u6790\n    full installation (with the recording service), also where the folder is marked analysis-only\n" +
                                    "/RELAUNCH  \u9759\u9ed8\u5b89\u88c5\u5b8c\u6210\u540e\u81ea\u52a8\u91cd\u65b0\u6253\u5f00\u7a0b\u5e8f\n    relaunch the program after silent install\n" +
                                    "--move-to [<folder>]  \u628a\u88c5\u597d\u7684\u7a0b\u5e8f\u8fde\u540c\u6570\u636e\u642c\u5230\u8fd9\u4e2a\u6587\u4ef6\u5939\uff08\u4e0d\u5199\u5c31\u662f Program Files \u91cc\u7684\u6807\u51c6\u4f4d\u7f6e\uff09\uff1b\u539f\u6765\u7684\u6587\u4ef6\u5939\u539f\u6837\u4fdd\u7559\n    move the installed program with its data into this folder (none: the standard one under Program Files); the former folder is left as it is\n" +
                                    "/D=<folder>  \u5b89\u88c5\u5230\u8fd9\u4e2a\u6587\u4ef6\u5939\uff08\u653e\u5728\u6700\u540e\uff09\n    install into this folder (last on the line)\n\n" +
                                    "\u9000\u51fa\u7801 / exit codes: 0 ok, 1 failed, 2 running, 3 folder, 4 installer incomplete, 5 not installed, 6 service, 7 prerequisite, 8 disk space, 9 move undone";

        // what the PC lacks (checked before anything is changed)
        public const string NeedWindowsZh = "\u8fd9\u4e2a\u7a0b\u5e8f\u9700\u8981 64 \u4f4d\u7684 Windows 10 \u6216 Windows 11\u3002";
        public const string NeedWindowsEn = "This program needs 64-bit Windows 10 or 11.";
        public const string NeedNetZh = "\u8fd9\u53f0\u7535\u8111\u4e0a\u6ca1\u6709 .NET Framework 4.8\u3002\u8bf7\u5148\u4ece\u5fae\u8f6f\u7684\u7f51\u7ad9\u4e0b\u8f7d\u5b89\u88c5\uff1a{0}";
        public const string NeedNetEn = "This PC does not have the .NET Framework 4.8. Install it from Microsoft first: {0}";
        public const string NeedWebView2Zh = "\u8fd9\u53f0\u7535\u8111\u4e0a\u6ca1\u6709 WebView2 \u8fd0\u884c\u65f6\uff08\u7a0b\u5e8f\u7a97\u53e3\u8981\u7528\u5b83\u663e\u793a\u9875\u9762\uff09\u3002\u8bf7\u5148\u4ece\u5fae\u8f6f\u7684\u7f51\u7ad9\u4e0b\u8f7d\u5b89\u88c5\uff1a{0}";
        public const string NeedWebView2En = "This PC does not have the WebView2 Runtime, which the program's window shows its page with. Install it from Microsoft first: {0}";
        public const string OpenPage = "\u73b0\u5728\u6253\u5f00\u4e0b\u8f7d\u9875\u9762\u5417\uff1f\nOpen the download page now?";
        public const string PrerequisiteAskZh = "\u8fd9\u53f0\u7535\u8111\u7f3a\u5c11 {0}\u3002\u5b89\u88c5\u5305\u91cc\u5e26\u7740\u5b83\uff0c\u73b0\u5728\u5b89\u88c5\u5417\uff1f\u9700\u8981\u51e0\u5206\u949f\uff1b\u8bf7\u4e0d\u8981\u5728\u4eea\u5668\u6d4b\u91cf\u65f6\u5b89\u88c5\u3002";
        public const string PrerequisiteAskEn = "This PC lacks {0}. The package carries it: install it now? It takes a few minutes; do not do it while an instrument measures.";
        public const string PrerequisiteFailedZh = "{0} \u6ca1\u80fd\u88c5\u4e0a\uff08{1}\uff09\u3002";
        public const string PrerequisiteFailedEn = "{0} could not be installed ({1}).";

        public const string NewFolderZh = "\u8fd9\u4e2a\u6587\u4ef6\u5939\u8fd8\u6ca1\u6709\uff0c\u5b89\u88c5\u65f6\u4f1a\u65b0\u5efa\u3002";
        public const string NewFolderEn = "This folder does not exist yet: it is made when installing.";
        public const string UpdateHereZh = "\u8fd9\u91cc\u5df2\u7ecf\u88c5\u6709 {0}\uff0c\u4f1a\u66f4\u65b0\u5b83\u7684\u7a0b\u5e8f\u6587\u4ef6\u3002";
        public const string UpdateHereEn = "{0} is installed here: its program files are updated.";
        public const string FormerHereZh = "\u8fd9\u91cc\u539f\u6765\u7684\u7a0b\u5e8f {0} \u4f1a\u88ab\u66ff\u6362\uff0c\u5b9e\u9a8c\u8bb0\u5f55\u63a5\u7740\u7528\u3002";
        public const string FormerHereEn = "The former program {0} here is replaced; its run records carry on.";
        public const string OldProgramZh = "\u8fd9\u91cc\u7684\u65e7\u7248 {0} \u4f1a\u6539\u540d\u4e3a {1}\uff08\u4e0d\u5220\u9664\uff09\u3002";
        public const string OldProgramEn = "The old {0} here is renamed to {1} (not deleted).";
        public const string DataStaysZh = "\u6570\u636e\u6587\u4ef6\u5939 {0} \u7559\u5728\u539f\u5904\uff0c\u4e0d\u4f1a\u6539\u52a8\u3002";
        public const string DataStaysEn = "The data folder {0} stays as it is.";
        public const string DataHereZh = "\u7a0b\u5e8f\u7684\u6570\u636e\uff08\u5b9e\u9a8c\u6587\u4ef6\u3001\u8bb0\u5f55\u3001\u65e5\u5fd7\uff09\u4f1a\u653e\u5728\u8fd9\u4e2a\u6587\u4ef6\u5939\u91cc\u7684 {0}\u3002";
        public const string DataHereEn = "The program keeps its data (experiment files, records, log) in {0} in this folder.";
        public const string OtherProgramZh = "\u8fd9\u4e2a\u6587\u4ef6\u5939\u91cc\u88c5\u7740 {0}\uff0c\u8bf7\u6362\u4e00\u4e2a\u6587\u4ef6\u5939\u3002";
        public const string OtherProgramEn = "{0} is installed in this folder: choose another folder.";
        public const string ElsewhereZh = "{0} \u5df2\u7ecf\u88c5\u5728 {1}\uff0c\u5b9e\u9a8c\u8bb0\u5f55\u4e5f\u5728\u90a3\u91cc\u3002\u8bf7\u88c5\u5230\u90a3\u4e2a\u6587\u4ef6\u5939\uff1b\u8981\u6362\u5730\u65b9\uff0c\u8bf7\u7528 --move-to\uff08\u6216\u8005\u5728\u5b89\u88c5\u7a97\u53e3\u91cc\u9009\u65b0\u7684\u6587\u4ef6\u5939\uff09\u628a\u5b83\u642c\u8fc7\u53bb\u3002";
        public const string ElsewhereEn = "{0} is installed in {1}, with its run records. Install into that folder; to move it, use --move-to (or choose the new folder in the installer's window).";
        public const string MoveHereZh = "{0} \u73b0\u5728\u88c5\u5728 {1}\u3002\u88c5\u5230\u8fd9\u91cc\u5c31\u662f\u628a\u5b83\u642c\u8fc7\u6765\uff1a\u5b9e\u9a8c\u8bb0\u5f55\u3001\u65e5\u5fd7\u548c\u8bbe\u7f6e\u590d\u5236\u5230\u8fd9\u91cc\u5e76\u9010\u4e2a\u6838\u5bf9\uff0c\u539f\u6765\u7684\u6587\u4ef6\u5939\u539f\u6837\u4fdd\u7559\uff0c\u4f5c\u4e3a\u5907\u4efd\uff08\u4ee5\u540e\u53ef\u4ee5\u81ea\u5df1\u5220\u6389\uff09\u3002";
        public const string MoveHereEn = "{0} is installed in {1}. Installing here moves it: its records, log and settings are copied here and every file is checked; the current folder is left exactly as it is, as a backup (delete it yourself later).";
        public const string MoveAskZh = "\u628a {0} \u4ece {1} \u642c\u5230 {2}\uff1f\n\u8bb0\u5f55\u4f1a\u505c\u7ea6 10\u201320 \u79d2\uff0c\u4e0d\u4e22\u6570\u636e\uff1a\u6f0f\u6389\u7684\u8bfb\u6570\u4f1a\u4ece\u4eea\u5668\u8f6f\u4ef6\u81ea\u5df1\u7684\u6587\u4ef6\u91cc\u8865\u56de\u6765\u3002\u65b0\u4f4d\u7f6e\u51fa\u4efb\u4f55\u95ee\u9898\uff0c\u90fd\u4f1a\u81ea\u52a8\u6362\u56de\u539f\u6765\u7684\u6587\u4ef6\u5939\u548c\u670d\u52a1\u3002";
        public const string MoveAskEn = "Move {0} from {1} to {2}?\nRecording pauses about 10-20 seconds; no data is lost: what is missed is read back from the instrument software's own files. Should anything go wrong at the new place, the current folder and service are put back automatically.";
        public const string MeasuringNowZh = "\u6b63\u5728\u6d4b\u91cf\uff1a{0}\u3002";
        public const string MeasuringNowEn = "Measuring now: {0}.";
        public const string PauseAskZh = "{0} \u6b63\u5728\u6d4b\u91cf\u3002\n\u8bb0\u5f55\u4f1a\u505c\u7ea6 10 \u79d2\uff0c\u4e0d\u4e22\u6570\u636e\uff1a\u6f0f\u6389\u7684\u8bfb\u6570\u4f1a\u4ece\u4eea\u5668\u8f6f\u4ef6\u81ea\u5df1\u7684\u6587\u4ef6\u91cc\u8865\u56de\u6765\u3002";
        public const string PauseAskEn = "{0} is measuring.\nRecording pauses about 10 seconds; no data is lost: what is missed is read back from the instrument software's own files.";
        public const string NotChangedZh = "\u6ca1\u6709\u6539\u52a8\u4efb\u4f55\u4e1c\u897f\u3002";
        public const string NotChangedEn = "Nothing was changed.";
        // the modes: "analysis only" and the recorder
        public const string MeasuringNoSwitchZh = "{0} \u6b63\u5728\u6d4b\u91cf\uff1a\u73b0\u5728\u4e0d\u80fd\u628a\u8fd9\u4e2a\u5b89\u88c5\u6539\u6210\u4ec5\u5206\u6790\uff08\u90a3\u6837\u8bb0\u5f55\u5c31\u4e2d\u65ad\u4e86\uff09\u3002\u8bf7\u7b49\u8fd9\u6b21\u8fd0\u884c\u7ed3\u675f\u540e\u518d\u6539\u3002";
        public const string MeasuringNoSwitchEn = "{0} is measuring: the installation is not changed to analysis-only now (that would end the recording). Finish the run first.";
        public const string RemoveRecorderAskZh = "\u8fd9\u6837\u4f1a\u5220\u9664\u8fd9\u53f0\u7535\u8111\u4e0a\u7684\u540e\u53f0\u8bb0\u5f55\u670d\u52a1\uff1a\u4ee5\u540e\u8fd9\u53f0\u7535\u8111\u4e0d\u518d\u8bb0\u5f55\u5b9e\u9a8c\uff08\u6570\u636e\u6587\u4ef6\u5939\u4fdd\u7559\uff09\u3002\u7ee7\u7eed\u5417\uff1f";
        public const string RemoveRecorderAskEn = "This removes the background recording service from this PC: experiments are no longer recorded on it (the data folder stays). Continue?";
        public const string ServiceStaysZh = "{0} \u7684\u540e\u53f0\u670d\u52a1\u5220\u4e0d\u6389\uff0c\u6240\u4ee5\u6ca1\u6709\u6539\u6210\u4ec5\u5206\u6790\u5b89\u88c5\u3002";
        public const string ServiceStaysEn = "The background service of {0} could not be removed, so the installation was not changed to analysis-only.";
        public const string MarkerStaysZh = "\u7a0b\u5e8f\u6587\u4ef6\u5939\u91cc\u7684 {0} \u5220\u4e0d\u6389\uff1a\u6709\u5b83\u5728\uff0c\u7a0b\u5e8f\u7a97\u53e3\u4f1a\u5f53\u6210\u4ec5\u5206\u6790\u5b89\u88c5\u6253\u5f00\u3002\u8bf7\u624b\u52a8\u5220\u9664\u5b83\uff0c\u518d\u8fd0\u884c\u4e00\u6b21\u5b89\u88c5\u7a0b\u5e8f\u3002";
        public const string MarkerStaysEn = "The file {0} in the program's folder could not be removed: with it the program's window opens as an analysis-only installation. Delete it by hand, then run the installer again.";
        public const string AnalysisNotMovedZh = "{1} \u91cc\u7684 {0} \u662f\u4ec5\u5206\u6790\u5b89\u88c5\uff0c\u4e0d\u80fd\u642c\uff1a\u8bf7\u5148\u5378\u8f7d\u5b83\uff0c\u518d\u5b89\u88c5\u5230\u65b0\u7684\u6587\u4ef6\u5939\uff08\u81ea\u5df1\u4fdd\u5b58\u7684\u9879\u76ee\u6587\u4ef6\u4e0d\u53d7\u5f71\u54cd\uff0c\u7559\u5728\u539f\u5904\uff09\u3002";
        public const string AnalysisNotMovedEn = "{0} in {1} is an analysis-only installation, which is not moved: uninstall it and install it in the new folder (your project files are yours and stay where they are).";

        // the move
        public const string NothingToMoveZh = "\u8fd9\u53f0\u7535\u8111\u4e0a\u6ca1\u6709\u88c5\u597d\u7684 {0}\uff0c\u6ca1\u6709\u53ef\u4ee5\u642c\u7684\u3002";
        public const string NothingToMoveEn = "{0} is not installed on this PC: there is nothing to move.";
        public const string AlreadyThereZh = "{0} \u5df2\u7ecf\u88c5\u5728 {1}\u3002";
        public const string AlreadyThereEn = "{0} is installed in {1} already.";
        public const string NestedZh = "\u65b0\u6587\u4ef6\u5939\u4e0d\u80fd\u5728\u7a0b\u5e8f\u73b0\u5728\u7684\u6587\u4ef6\u5939\uff08\u6216\u5b83\u7684\u6570\u636e\u6587\u4ef6\u5939\uff09\u91cc\u9762\uff0c\u53cd\u8fc7\u6765\u4e5f\u4e0d\u884c\u3002";
        public const string NestedEn = "The new folder cannot be inside the program's current folder (or its data folder), nor the other way round.";
        public const string NotEmptyZh = "\u6587\u4ef6\u5939 {0} \u4e0d\u662f\u7a7a\u7684\u3002\u8981\u642c\u8fc7\u53bb\uff0c\u8bf7\u9009\u4e00\u4e2a\u65b0\u7684\u6216\u7a7a\u7684\u6587\u4ef6\u5939\u3002";
        public const string NotEmptyEn = "The folder {0} is not empty. Choose a new or empty folder to move to.";
        public const string InUseZh = "\u6709\u7a0b\u5e8f\u6b63\u5728\u4ece {0} \u8fd0\u884c\uff08{1}\uff09\uff0c\u8bf7\u5148\u5173\u6389\u5b83\u3002";
        public const string InUseEn = "A program runs from {0} ({1}): close it first.";
        public const string PathTooLongZh = "\u642c\u8fc7\u53bb\u4ee5\u540e\u8fd9\u4e2a\u6587\u4ef6\u7684\u8def\u5f84\u4f1a\u8d85\u8fc7 Windows \u7684 260 \u4e2a\u5b57\u7b26\uff08\u5f88\u591a\u7a0b\u5e8f\u548c Excel \u90fd\u6253\u4e0d\u5f00\uff09\uff1a{0}\u3002\u8bf7\u9009\u4e00\u4e2a\u8def\u5f84\u77ed\u4e00\u4e9b\u7684\u6587\u4ef6\u5939\uff0c\u6216\u8005\u5148\u628a\u8fd9\u4e2a\u6587\u4ef6\u6539\u4e2a\u77ed\u4e00\u4e9b\u7684\u540d\u5b57\u3002\u6ca1\u6709\u6539\u52a8\u4efb\u4f55\u4e1c\u897f\u3002";
        public const string PathTooLongEn = "After the move this file's path would be longer than Windows' 260 characters (many programs and Excel cannot open it): {0}. Choose a folder with a shorter path, or give the file a shorter name first. Nothing was changed.";
        public const string NoSpaceMoveZh = "{0} \u6240\u5728\u7684\u78c1\u76d8\u7a7a\u95f4\u4e0d\u591f\uff1a\u9700\u8981 {1} MB\uff0c\u53ea\u5269 {2} MB\u3002\u6ca1\u6709\u6539\u52a8\u4efb\u4f55\u4e1c\u897f\u3002";
        public const string NoSpaceMoveEn = "Not enough space on the drive of {0}: {1} MB needed, {2} MB free. Nothing was changed.";
        public const string InstrumentsMissingZh = "\u65b0\u4f4d\u7f6e\u7684\u540e\u53f0\u670d\u52a1\u6ca1\u6709\u663e\u793a\u8fd9\u4e9b\u4eea\u5668\uff1a{0}";
        public const string InstrumentsMissingEn = "the service at the new place does not show these instruments: {0}";
        public const string MoveFailedZh = "\u6ca1\u80fd\u642c\u8fc7\u53bb\uff1a{0}\u3002";
        public const string MoveFailedEn = "The move did not work: {0}.";
        public const string MovedBackZh = "\u5df2\u7ecf\u5168\u90e8\u6362\u56de\u539f\u6837\uff1a\u7a0b\u5e8f\u7167\u5e38\u4ece {0} \u8fd0\u884c\u548c\u8bb0\u5f55\u3002";
        public const string MovedBackEn = "Everything is back as it was: the program runs and records from {0}.";
        public const string NothingChangedZh = "\u539f\u6765\u7684\u7a0b\u5e8f\u6ca1\u6709\u6539\u52a8\uff0c\u7167\u5e38\u8fd0\u884c\u3002";
        public const string NothingChangedEn = "The program as it was is unchanged and runs.";
        public const string AutoStartFailedZh = "\u65b0\u7684\u540e\u53f0\u670d\u52a1\u6ca1\u80fd\u8bbe\u4e3a\u968f Windows \u542f\u52a8";
        public const string AutoStartFailedEn = "the new service could not be set to start with Windows";
        public const string MovedZh = "\u5df2\u642c\u5230 {0}\u3002";
        public const string MovedEn = "Moved to {0}.";
        public const string MovedKeptZh = "\u539f\u6765\u7684\u6587\u4ef6\u5939 {0} \u539f\u6837\u4fdd\u7559\uff0c\u4f5c\u4e3a\u5907\u4efd\uff1b\u786e\u8ba4\u4e00\u5207\u6b63\u5e38\u540e\u53ef\u4ee5\u81ea\u5df1\u5220\u6389\u5b83\u3002";
        public const string MovedKeptEn = "The former folder {0} is left exactly as it was, as a backup; delete it yourself once everything works.";

        public const string BadFolderZh = "\u8bf7\u586b\u5199\u5b8c\u6574\u7684\u6587\u4ef6\u5939\u8def\u5f84\uff0c\u4f8b\u5982 D:\\PlateKineticsLive\u3002";
        public const string BadFolderEn = "Enter a full folder path, such as D:\\PlateKineticsLive.";
        public const string RootZh = "\u4e0d\u80fd\u76f4\u63a5\u88c5\u5230\u78c1\u76d8\u7684\u6839\u76ee\u5f55\u3002";
        public const string RootEn = "Not into the root of a drive.";
        public const string IsFileZh = "\u8fd9\u662f\u4e00\u4e2a\u6587\u4ef6\uff0c\u4e0d\u662f\u6587\u4ef6\u5939\u3002";
        public const string IsFileEn = "This is a file, not a folder.";
        public const string RunningZh = "{0} \u6b63\u5728\u8fd0\u884c\uff08{1}\uff09\u3002\u8bf7\u5148\u5173\u95ed\u5b83\uff0c\u518d\u5b89\u88c5\u3002";
        public const string RunningEn = "{0} is running ({1}). Close it first, then install again.";
        public const string ServiceFailedZh = "{0} \u5df2\u7ecf\u88c5\u597d\uff0c\u4f46\u540e\u53f0\u670d\u52a1\u6ca1\u80fd\u542f\u52a8\uff1a{1}";
        public const string ServiceFailedEn = "{0} is installed, but its service did not start: {1}";
        public const string PreviousBackZh = "\u65b0\u7248\u672c\u542f\u52a8\u4e0d\u4e86\uff0c\u5df2\u6362\u56de\u539f\u6765\u7684\u7248\u672c\u3002";
        public const string PreviousBackEn = "The new version did not start; the previous one is back.";
        public const string FormerBackZh = "\u5df2\u6362\u56de\u539f\u6765\u7684\u7a0b\u5e8f {0}\uff0c\u5b83\u7167\u5e38\u8bb0\u5f55\u3002";
        public const string FormerBackEn = "The former program {0} is back and records as before.";
        public const string NotStoppedZh = "{0} \u7684\u540e\u53f0\u670d\u52a1\u5728 {1} \u79d2\u5185\u6ca1\u6709\u505c\u4e0b\u6765\uff0c\u6ca1\u6709\u6539\u52a8\u4efb\u4f55\u6587\u4ef6\u3002";
        public const string NotStoppedEn = "The service of {0} did not stop within {1} s: no file was changed.";
        // after a failure: what became of the service (follows the error's message)
        public const string ServiceRunsZh = "\u540e\u53f0\u670d\u52a1\u5728\u8fd0\u884c\u3002";
        public const string ServiceRunsEn = "The service runs.";
        public const string ServiceAgainZh = "\u540e\u53f0\u670d\u52a1\u5df2\u91cd\u65b0\u6ce8\u518c\uff0c\u5728\u8fd0\u884c\u3002";
        public const string ServiceAgainEn = "The service was registered again and runs.";
        public const string ServiceDownZh = "\u6ce8\u610f\uff1a\u540e\u53f0\u670d\u52a1\u6ca1\u6709\u8fd0\u884c\uff08{0}\uff09\uff0c\u5b83\u73b0\u5728\u4e0d\u8bb0\u5f55\u3002\u8bf7\u518d\u8fd0\u884c\u4e00\u6b21\u5b89\u88c5\u7a0b\u5e8f\u3002";
        public const string ServiceDownEn = "WARNING: the service does not run ({0}), so it does not record now. Run the installer again.";
        public const string NoSpaceZh = "\u78c1\u76d8\u7a7a\u95f4\u4e0d\u591f\uff08\u9700\u8981 {0} MB\uff09\u3002";
        public const string NoSpaceEn = "Not enough disk space ({0} MB needed).";
        public const string IncompleteZh = "\u8fd9\u4e2a\u5b89\u88c5\u7a0b\u5e8f\u4e0d\u5b8c\u6574\uff08{0}\uff09\uff0c\u8bf7\u91cd\u65b0\u751f\u6210\u3002";
        public const string IncompleteEn = "This installer is incomplete ({0}): build it again.";

        public const string InstalledZh = "\u5b89\u88c5\u5b8c\u6210\uff1a{0}";
        public const string InstalledEn = "Installed: {0}.";
        public const string UpdatedZh = "\u66f4\u65b0\u5b8c\u6210\uff1a{0}";
        public const string UpdatedEn = "Updated: {0}.";
        public const string FormerReplacedZh = "\u539f\u6765\u7684\u7a0b\u5e8f {0} \u5df2\u88ab\u66ff\u6362\uff0c\u5b9e\u9a8c\u8bb0\u5f55\u63a5\u7740\u7528\u3002";
        public const string FormerReplacedEn = "The former program {0} was replaced; its run records carry on.";
        public const string RenamedZh = "\n\u65e7\u7248\u7a0b\u5e8f\u6539\u540d\u4e3a {0}\u3002";
        public const string RenamedEn = " The old program was renamed to {0}.";
        public const string KeptZh = "\n\u539f\u6709\u7684\u540c\u540d\u6587\u4ef6\u6539\u540d\u4fdd\u7559\uff1a{0}\u3002";
        public const string KeptEn = " Files of the same names that were there are kept as {0}.";
        public const string WindowOpenZh = "\n\u8fd8\u6709\u7a0b\u5e8f\u7a97\u53e3\u5f00\u7740\uff1a\u8bf7\u5173\u6389\u5b83\uff0c\u518d\u91cd\u65b0\u6253\u5f00\u7a0b\u5e8f\u3002";
        public const string WindowOpenEn = " A window of the program is still open: close it and open the program again.";
        public const string NoteZh = "\u6ce8\u610f\uff1a{0}";
        public const string NoteEn = "Note: {0}";
    }
}
