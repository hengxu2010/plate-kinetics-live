// "<program>.exe --uninstall [/S]": removes an installed Plate Kinetics Live program, that is its service, its tray (the
// "--tray" helper every signed-in user runs, and its start at sign-in), its shortcuts, its entry in Windows' list of apps,
// its .pk file type and the files its installer put in place (with the copies of them an update renamed aside,
// <file>.replaced-N), and nothing else. The data folder PlateKineticsLive-data beside the program (experiments, run
// records, log, the windows' profiles) stays where it is, as does every file the installer did not put there (the old
// program renamed to <name>-python-old.exe, the user's files kept as <file>.old, older builds, pages dropped beside the
// exe). The program is installed for all users (its folder under Program Files, its service, its entry), so the
// uninstaller works as administrator: started without those rights, it starts an elevated copy of itself (one Windows
// prompt) and waits for it.
// Compiled into both programs and into the installer (src\Setup\Setup.cs), which shares InstallShared with it.
//
// The program calls it first thing in Main, before any WebView2 type is used (then the WebView2 DLLs are not loaded
// and can be removed at once):
//     if (Array.IndexOf(args, "--uninstall") >= 0) return Uninstaller.Run("PlateKineticsLive");
// "/S" anywhere on the command line: silent, no question and no message (then it must run as administrator already);
// the exit code says what happened, and one line goes to %TEMP%\<name>-setup.log either way.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SAAControl
{
    public static class Uninstaller
    {
        public const int Ok = 0, Failed = 1, Running = 2, Cancelled = 3, NotInstalled = 4, ServiceInstalled = 5;

        // the elevated copy is told which process started it: that one waits for it and is not "another instance"
        const string ParentOption = "--uninstall-parent=";

        /// <summary>Uninstalls the program this process runs from. Returns the exit code (Ok, Failed, Running,
        /// Cancelled, NotInstalled, ServiceInstalled).</summary>
        public static int Run(string appName)
        {
            string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            bool silent = arguments.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            string shown = InstallShared.DisplayName(appName);
            string caption = "\u5378\u8f7d " + shown + " / Uninstall " + shown;
            try
            {
                string exe;
                int self;
                using (Process me = Process.GetCurrentProcess())
                {
                    exe = Path.GetFullPath(me.MainModule.FileName);
                    self = me.Id;
                }
                string dir = Path.GetDirectoryName(exe);
                string processName = Path.GetFileNameWithoutExtension(exe);
                if (!silent) Application.EnableVisualStyles();

                InstallShared.Manifest manifest = InstallShared.ReadManifest(dir, appName);
                string registered = InstallShared.RegisteredFolder(appName);
                if (manifest == null && !(registered != null && InstallShared.SameFolder(registered, dir)))
                    return End(appName, silent, caption, NotInstalled, MessageBoxIcon.Information,
                        "uninstall stopped: " + dir + " holds no installation of " + shown,
                        "\u8fd9\u4e2a\u6587\u4ef6\u5939\u91cc\u7684 " + shown + " \u4e0d\u662f\u7528\u5b89\u88c5\u7a0b\u5e8f\u88c5\u7684\uff0c\u6ca1\u6709\u8981\u5378\u8f7d\u7684\u5185\u5bb9\u3002\n" + dir + "\n\n" +
                        shown + " in this folder was not put there by its installer: there is nothing to uninstall.");

                // installed for all users: removing it needs administrator rights
                if (!InstallShared.IsElevated())
                {
                    if (silent)
                        return End(appName, true, caption, Failed, MessageBoxIcon.Error,
                            "uninstall stopped: it needs administrator rights (run \"" + exe + "\" --uninstall /S as administrator)", "");
                    return RunElevated(appName, exe, self, caption);
                }
                int parent = 0;
                foreach (string a in arguments)
                    if (a.StartsWith(ParentOption, StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring(ParentOption.Length), out int id)) parent = id;

                // A Windows service that runs this program (the program's --install-service, administrator) is removed
                // first, with the program's own --uninstall-service; its process counts as running until then. The
                // trays ("--tray", one per signed-in user) do not count: they are told to end below.
                bool service = InstallShared.SameFile(InstallShared.ServiceExe(appName), exe);
                string runningLine = "uninstall stopped: " + shown + " is running (process {0})";
                string runningText = shown + " \u6b63\u5728\u8fd0\u884c\u3002\u8bf7\u5148\u5173\u95ed\u5b83\uff0c\u518d\u5378\u8f7d\u3002\n\n" + shown + " is running. Close it, then uninstall again.";
                HashSet<int> trays = InstallShared.TrayProcessIds(processName);
                List<int> others = Others(processName, self, parent).Where(id => !trays.Contains(id)).ToList();
                if (!service && others.Count > 0)
                    return End(appName, silent, caption, Running, MessageBoxIcon.Warning, string.Format(runningLine, string.Join(", ", others)), runningText);

                // the data folder as the program finds it (its name now, or an earlier one it has not renamed yet)
                string data = InstallShared.DataFolderIn(dir);
                string dataName = Path.GetFileName(data);
                if (!silent)
                {
                    string question = "\u8981\u5378\u8f7d " + shown + " \u5417\uff1f\n\u7a0b\u5e8f\u6587\u4ef6\u548c\u5feb\u6377\u65b9\u5f0f\u4f1a\u88ab\u5220\u9664\uff1b\u6570\u636e\u6587\u4ef6\u5939 " + dataName +
                                      "\uff08\u5b9e\u9a8c\u8bb0\u5f55\u3001\u65e5\u5fd7\uff09\u4e0d\u4f1a\u5220\u9664\u3002\n" + dir + "\n\nUninstall " + shown + "?\nThe program files and shortcuts are removed; the data folder " +
                                      dataName + " (run records, log) is not.";
                    if (service)
                        question += "\n\n\u540c\u65f6\u5220\u9664 Windows \u670d\u52a1 " + shown + "\u3002\nThe Windows service " + shown + " is removed too.";
                    if (MessageBox.Show(question, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        InstallShared.Log(appName, "uninstall cancelled: " + dir);
                        return Cancelled;
                    }
                }
                // the tray: no longer started at sign-in, and the running ones end (each holds the program file)
                bool runEntry = InstallShared.RemoveRunEntry(appName, exe);
                int traysEnded = InstallShared.StopTrays(appName, processName, 15);
                if (service)
                {
                    // the program's windows only show what the service records: those in this session are asked to
                    // close. While another copy is still there - a window in another session (an uninstall over SSH
                    // runs in session 0), one that does not close - nothing is changed: the service stays. The
                    // service's own process (session 0) ends with the service.
                    InstallShared.CloseWindows(processName, self, 15, dir, parent);
                    List<string> windows = InstallShared.UserSessionProcesses(processName, self, dir).Where(w => !w.StartsWith(parent + " ")).ToList();
                    if (windows.Count > 0)
                        return End(appName, silent, caption, Running, MessageBoxIcon.Warning, string.Format(runningLine, string.Join(", ", windows)), runningText);
                    if (!RemoveService(appName, exe))
                        return End(appName, silent, caption, ServiceInstalled, MessageBoxIcon.Warning,
                            "uninstall stopped: the Windows service " + shown + " runs this program; remove it first (as administrator: \"" + exe + "\" --uninstall-service)",
                            "Windows \u670d\u52a1 " + shown + " \u8fd8\u5728\u4f7f\u7528\u8fd9\u4e2a\u7a0b\u5e8f\u3002\u8bf7\u5148\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u5220\u9664\u670d\u52a1\uff1a\n\"" + exe + "\" --uninstall-service\n\n" +
                            "The Windows service " + shown + " still runs this program. Remove the service first, as administrator:\n\"" + exe + "\" --uninstall-service");
                    trays = InstallShared.TrayProcessIds(processName);      // a tray that did not end holds the exe only
                    others = Others(processName, self, parent).Where(id => !trays.Contains(id)).ToList();
                    for (int wait = 0; others.Count > 0 && wait < 30; wait++)
                    {
                        System.Threading.Thread.Sleep(500);     // the service's process ends a moment after the service stops
                        others = Others(processName, self, parent).Where(id => !trays.Contains(id)).ToList();
                    }
                    if (others.Count > 0)
                        return End(appName, silent, caption, Running, MessageBoxIcon.Warning, string.Format(runningLine, string.Join(", ", others)), runningText);
                }

                // 1. shortcuts that lead to this program: for all users, and those of an earlier installation for this
                //    user only; and the one that opens its Experiments folder
                int shortcuts = 0;
                foreach (string link in InstallShared.Shortcuts(shown))
                    if (File.Exists(link) && InstallShared.SameFile(InstallShared.ShortcutTarget(link), exe) && InstallShared.TryDelete(link))
                        shortcuts++;
                string experimentsLink = InstallShared.ExperimentsShortcut(shown);
                if (File.Exists(experimentsLink) && InstallShared.IsInFolder(InstallShared.ShortcutTarget(experimentsLink), dir) && InstallShared.TryDelete(experimentsLink))
                    shortcuts++;

                // 2. the program's files, apart from the exe this process runs from (removed once the process has ended)
                List<string> files = (manifest != null ? manifest.Files : InstallShared.DefaultFiles(Path.GetFileName(exe)))
                    .Where(InstallShared.IsSafeRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var later = new List<string> { exe };
                int removed = 0;
                foreach (string rel in files)
                {
                    string path = Path.Combine(dir, rel);
                    if (InstallShared.SameFile(path, exe) || !File.Exists(path)) continue;
                    if (InstallShared.TryDelete(path)) removed++;
                    else later.Add(path);
                }
                // the copies of them an update renamed aside because a window held them (never the user's .old files)
                foreach (string path in InstallShared.ReplacedFiles(dir, files))
                {
                    if (InstallShared.TryDelete(path)) removed++;
                    else later.Add(path);
                }
                string list = InstallShared.ManifestPath(dir, appName);
                if (File.Exists(list) && !InstallShared.TryDelete(list)) later.Add(list);
                // the folders that held them (web\), deepest first, when they are empty
                var folders = files.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).SelectMany(Ancestors)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Count(c => c == '\\'))
                    .Select(d => Path.Combine(dir, d)).ToList();
                foreach (string folder in folders) InstallShared.TryRemoveEmptyFolder(folder);

                // 3. the entry in Windows' list of apps and the .pk file type, when they are this installation's
                bool entry = InstallShared.RemoveEntry(appName, dir, exe);
                bool fileType = false;
                try { fileType = InstallShared.UnregisterPk(appName, exe); }
                catch (Exception error) { InstallShared.Log(appName, "uninstall: the .pk file type stays (" + error.Message + ")"); }

                // 4. once this process has ended: its exe, what could not be removed now, then the folders left empty;
                //    the program's folder itself only when the installer made it (never the desktop or the like)
                var emptyFolders = new List<string>(folders);
                if (manifest != null && manifest.CreatedFolder && !InstallShared.IsSpecialFolder(dir)) emptyFolders.Add(dir);
                InstallShared.DeleteAfterExit(later, emptyFolders);

                bool hasData = Directory.Exists(data);
                InstallShared.Log(appName, "uninstall ok: " + dir + " (" + (removed + 1) + " files, " + shortcuts + " shortcuts, entry " +
                                           (entry ? "removed" : "not there") + ", .pk file type " + (fileType ? "removed" : "not there") +
                                           ", tray start at sign-in " + (runEntry ? "removed" : "not there") + ", " + traysEnded + " trays ended" +
                                           (later.Count > 1 ? ", " + (later.Count - 1) + " files removed after exit" : "") +
                                           (hasData ? "; the data folder stays: " + data : "") + ")");
                if (!silent)
                    MessageBox.Show(shown + " \u5df2\u5378\u8f7d\u3002" + (hasData ? "\n\u6570\u636e\u6587\u4ef6\u5939\u7559\u5728\u539f\u5904\uff1a" + data : "") + "\n\n" +
                                    shown + " was uninstalled." + (hasData ? "\nThe data folder stays where it is: " + data : ""),
                        caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return Ok;
            }
            catch (Exception error)
            {
                InstallShared.Log(appName, "uninstall FAILED: " + error.Message);
                if (!silent)
                    MessageBox.Show("\u5378\u8f7d\u5931\u8d25\uff1a" + error.Message + "\n\nUninstalling failed: " + error.Message, caption,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return Failed;
            }
        }

        /// <summary>The other processes of the program, apart from the one that started this elevated copy.</summary>
        static List<int> Others(string processName, int self, int parent) =>
            InstallShared.ProcessIds(processName, self).Where(id => id != parent).ToList();

        /// <summary>Starts this program as administrator for the uninstalling (Windows asks the user once) and waits for
        /// it: its exit code, or Cancelled when the prompt was declined.</summary>
        static int RunElevated(string appName, string exe, int self, string caption)
        {
            try
            {
                var start = new ProcessStartInfo(exe, "--uninstall " + ParentOption + self) { UseShellExecute = true, Verb = "runas" };
                using (Process child = Process.Start(start))
                {
                    if (child == null) return Failed;
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                InstallShared.Log(appName, "uninstall cancelled: administrator rights were not given");
                return Cancelled;
            }
            catch (Exception error)
            {
                return End(appName, false, caption, Failed, MessageBoxIcon.Error, "uninstall FAILED: " + error.Message,
                    "\u5378\u8f7d\u5931\u8d25\uff1a" + error.Message + "\n\nUninstalling failed: " + error.Message);
            }
        }

        static int End(string appName, bool silent, string caption, int code, MessageBoxIcon icon, string logLine, string message)
        {
            InstallShared.Log(appName, logLine);
            if (!silent) MessageBox.Show(message, caption, MessageBoxButtons.OK, icon);
            return code;
        }

        /// <summary>Runs "exe --uninstall-service" (this process is elevated) and waits until the service is gone. False
        /// when it is still there.</summary>
        static bool RemoveService(string appName, string exe)
        {
            try
            {
                var start = new ProcessStartInfo(exe, "--uninstall-service") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (Process process = Process.Start(start)) process?.WaitForExit(120000);
            }
            catch (Exception)
            {
                return false;
            }
            for (int wait = 0; InstallShared.ServiceExe(appName) != null && wait < 20; wait++) System.Threading.Thread.Sleep(500);
            InstallShared.Log(appName, "uninstall: the Windows service " + appName + (InstallShared.ServiceExe(appName) == null ? " was removed" : " is still there"));
            return InstallShared.ServiceExe(appName) == null;
        }

        static IEnumerable<string> Ancestors(string relativeFolder)
        {
            for (string d = relativeFolder; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) yield return d;
        }
    }

    /// <summary>What the installer and the uninstaller agree on: names, the list of installed files, the entry in
    /// Windows' list of apps, shortcuts, the .pk file type, the log.</summary>
    static class InstallShared
    {
        public const string Publisher = "hx_mao";

        /// <summary>The name people see for one of the programs (shortcuts, the list of apps, the service, the window),
        /// from its name as a file name.</summary>
        public static string DisplayName(string name)
        {
            if (string.Equals(name, "PlateKineticsLive", StringComparison.OrdinalIgnoreCase)) return "Plate Kinetics Live";
            if (string.Equals(name, "PlateKineticsLive-Archimed", StringComparison.OrdinalIgnoreCase)) return "Plate Kinetics Live (Archimed)";
            return name;
        }

        /// <summary>The name of the folder a first installation goes into, under Program Files.</summary>
        public static string DefaultFolderName(string name)
        {
            if (string.Equals(name, "PlateKineticsLive", StringComparison.OrdinalIgnoreCase)) return "Plate Kinetics Live (BMG)";
            if (string.Equals(name, "PlateKineticsLive-Archimed", StringComparison.OrdinalIgnoreCase)) return "Plate Kinetics Live (Archimed)";
            return name;
        }

        /// <summary>The program's name up to 2026-11-23 (SAAControl: the programs no longer control instruments), whose
        /// installation an installation under the new name replaces; null for none.</summary>
        public static string FormerName(string name)
        {
            if (string.Equals(name, "PlateKineticsLive", StringComparison.OrdinalIgnoreCase)) return "SAAControl";
            if (string.Equals(name, "PlateKineticsLive-Archimed", StringComparison.OrdinalIgnoreCase)) return "SAAControl-Archimed";
            return null;
        }
        public const string DataFolder = "PlateKineticsLive-data";
        // its earlier names, newest first: up to 2026-11-23, and up to 2026-11-19 ("SAA experiment data")
        public static readonly string[] OldDataFolders = { "SAAControl-data", "SAA\u5b9e\u9a8c\u6570\u636e" };
        public const string ManifestSuffix = "-installed-files.txt";
        const string CreatedLine = "# folder created by the installer: yes";

        /// <summary>The data folder in a program's folder as the program finds it: the one of its name, else the first
        /// one of an earlier name that is there (the program renames it at a start), else the one of its name.</summary>
        public static string DataFolderIn(string dir)
        {
            string fresh = Path.Combine(dir, DataFolder);
            if (Directory.Exists(fresh)) return fresh;
            foreach (string old in OldDataFolders)
                if (Directory.Exists(Path.Combine(dir, old))) return Path.Combine(dir, old);
            return fresh;
        }

        public static string UninstallKey(string name) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;

        /// <summary>The list of the files the installer put in the program's folder (a hidden text file there).</summary>
        public static string ManifestPath(string dir, string name) => Path.Combine(dir, name + ManifestSuffix);

        // shortcuts: the installer makes them for all users; installations up to 2.1 made them for the installing user
        public static string StartMenuShortcut(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk");

        public static string DesktopShortcut(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");

        public static string AllUsersStartMenuShortcut(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name + ".lnk");

        public static string AllUsersDesktopShortcut(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name + ".lnk");

        /// <summary>Every place a shortcut of this name may be: the Start menu and the desktop, for all users and for
        /// the current user.</summary>
        public static string[] Shortcuts(string name) =>
            new[] { AllUsersStartMenuShortcut(name), AllUsersDesktopShortcut(name), StartMenuShortcut(name), DesktopShortcut(name) };

        /// <summary>The Start menu shortcut (all users) that opens the program's Experiments folder.</summary>
        public static string ExperimentsShortcut(string displayName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), displayName + " Experiments.lnk");

        /// <summary>The folder in the data folder that holds the experiment files (.pk, .xlsx): the only one people browse.</summary>
        public const string ExperimentsFolder = "Experiments";
        /// <summary>The folder in the data folder that holds what the program keeps for itself (hidden).</summary>
        public const string SystemFolder = "System";
        /// <summary>The folder in System that holds each Windows user's window data, one folder per user.</summary>
        public const string UsersFolder = "users";

        /// <summary>The API token the program keeps in its data folder: System\api-token, or where an earlier version kept
        /// it (the data folder itself) while the program has not moved it yet. Null when there is none.</summary>
        public static string TokenFile(string dataDir)
        {
            foreach (string path in new[] { Path.Combine(dataDir, SystemFolder, "api-token"), Path.Combine(dataDir, "api-token") })
                if (File.Exists(path)) return path;
            return null;
        }

        public static string LogPath(string name) => Path.Combine(Path.GetTempPath(), name + "-setup.log");

        /// <summary>One line into %TEMP%\name-setup.log; a failure to write is ignored.</summary>
        public static void Log(string name, string line)
        {
            try
            {
                File.AppendAllText(LogPath(name), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public static bool IsElevated()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        public sealed class Manifest
        {
            public readonly List<string> Files = new List<string>();     // relative paths, '\' between folders
            public bool CreatedFolder;                                    // the installer made the program's folder
        }

        public static Manifest ReadManifest(string dir, string name)
        {
            string path = ManifestPath(dir, name);
            if (!File.Exists(path)) return null;
            var manifest = new Manifest();
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#"))
                {
                    if (line == CreatedLine) manifest.CreatedFolder = true;
                    continue;
                }
                manifest.Files.Add(line.Replace('/', '\\'));
            }
            return manifest;
        }

        public static void WriteManifest(string dir, string name, IEnumerable<string> files, bool createdFolder)
        {
            string path = ManifestPath(dir, name);
            var lines = new List<string>
            {
                "# Files that the installer of " + name + " put in this folder. Uninstalling removes them and nothing else;" +
                " the data folder " + DataFolder + " is never touched."
            };
            if (createdFolder) lines.Add(CreatedLine);
            lines.AddRange(files);
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);     // a hidden file cannot be overwritten
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            File.SetAttributes(path, FileAttributes.Hidden);
        }

        /// <summary>The files of an installation whose list is missing: what the installer puts in place.</summary>
        public static List<string> DefaultFiles(string exeName) => new List<string>
        {
            exeName, exeName + ".config", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
            "WebView2Loader.dll", @"web\PlateKineticsAnalysis.html", @"web\shell.js", @"web\live.js", @"web\shell.css",
        };

        /// <summary>A path the installer may write and the uninstaller may remove: relative, inside the program's
        /// folder, not in the data folder.</summary>
        public static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            string[] parts = rel.Split('\\', '/');
            if (parts.Any(p => p.Trim().Length == 0 || p == "." || p == ".." || p.IndexOf(':') >= 0)) return false;
            return !parts[0].Equals(DataFolder, StringComparison.OrdinalIgnoreCase) &&
                   !OldDataFolders.Any(old => parts[0].Equals(old, StringComparison.OrdinalIgnoreCase));
        }

        static string Normal(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        public static bool SameFolder(string a, string b)
        {
            try { return a != null && b != null && string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public static bool SameFile(string a, string b) => SameFolder(a, b);

        /// <summary>True when the path lies in the folder (or one below it).</summary>
        public static bool IsInFolder(string path, string folder)
        {
            try { return path != null && folder != null && Normal(path).StartsWith(Normal(folder) + "\\", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>HKEY_LOCAL_MACHINE as 64-bit programs see it (the programs are 64-bit; the installer runs as one).</summary>
        public static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        /// <summary>The folder of the installation in Windows' list of apps (for all users, else - up to 2.1 - for the
        /// current user), or null.</summary>
        public static string RegisteredFolder(string name)
        {
            foreach (bool machine in new[] { true, false })
                try
                {
                    using (RegistryKey root = machine ? Machine() : null)
                    using (RegistryKey key = (machine ? root : Registry.CurrentUser).OpenSubKey(UninstallKey(name)))
                    {
                        string location = key?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(location)) return location;
                    }
                }
                catch (Exception) { }
            return null;
        }

        /// <summary>Removes the program's entry from Windows' list of apps (for all users and for the current user) where
        /// it is the one of this installation. True when one was removed.</summary>
        public static bool RemoveEntry(string name, string dir, string exe)
        {
            bool removed = false;
            using (RegistryKey machine = Machine())
                removed |= RemoveEntry(machine, name, dir, exe);
            removed |= RemoveEntry(Registry.CurrentUser, name, dir, exe);
            return removed;
        }

        static bool RemoveEntry(RegistryKey root, string name, string dir, string exe)
        {
            string path = UninstallKey(name);
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null) return false;
                string location = key.GetValue("InstallLocation") as string;
                string uninstall = key.GetValue("UninstallString") as string ?? "";
                bool ours = string.IsNullOrEmpty(location)
                    ? uninstall.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0
                    : SameFolder(location, dir);
                if (!ours) return false;
            }
            root.DeleteSubKeyTree(path, false);
            return true;
        }

        /// <summary>The program a Windows service of this name runs (its ImagePath without the arguments), or null.</summary>
        public static string ServiceExe(string name) => ImageExe(ServiceImagePath(name));

        /// <summary>The command line Windows starts a service of this name with (its ImagePath), or null.</summary>
        public static string ServiceImagePath(string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                    return key?.GetValue("ImagePath") as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when a Windows service of this name is registered and not marked for deletion (one that is goes
        /// once nothing holds it open any more, and can no longer be started).</summary>
        public static bool ServiceRegistered(string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                    return key != null && key.GetValue("ImagePath") != null && !(key.GetValue("DeleteFlag") is int deleted && deleted != 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>True when Windows restarts the service of this name after it failed: its first recovery action is
        /// "restart" (sc failure), also when it ends with an error by itself (sc failureflag 1).</summary>
        public static bool RestartsAfterFailure(string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    // SERVICE_FAILURE_ACTIONS as Windows keeps it: reset period, two unused, the number of actions, one
                    // unused, then the actions (type, delay); type 1 = restart
                    if (!(key?.GetValue("FailureActions") is byte[] data) || data.Length < 28) return false;
                    int count = BitConverter.ToInt32(data, 12);
                    bool restarts = count > 0 && BitConverter.ToInt32(data, 20) == 1;
                    bool onErrors = key.GetValue("FailureActionsOnNonCrashFailures") is int flag && flag != 0;
                    return restarts && onErrors;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>The program of a service's command line: "C:\x\a.exe" --service, or C:\x\a.exe --service.</summary>
        public static string ImageExe(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string image = Environment.ExpandEnvironmentVariables(imagePath.Trim());
            if (image.StartsWith("\""))
            {
                int end = image.IndexOf('"', 1);
                return end > 1 ? image.Substring(1, end - 1) : null;
            }
            int exe = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? image.Substring(0, exe + 4) : image;
        }

        // ---- the .pk experiment files: a file type for the whole machine (HKLM\Software\Classes) ----

        public const string PkExtension = ".pk";
        const string PkContentType = "application/vnd.plate-kinetics.experiment+zip";
        static readonly string[] OurPkTypes = { PkProgId("PlateKineticsLive"), PkProgId("PlateKineticsLive-Archimed") };

        /// <summary>The program's file type for .pk files.</summary>
        public static string PkProgId(string name) => name + ".Experiment.1";

        /// <summary>Makes double-clicking a .pk file open it in the program ("exe" --open "file"): the program's own file
        /// type, listed among the programs that open .pk; .pk itself goes to it unless another product has it (the two
        /// Plate Kinetics Live programs hand it to the one installed last). classes: HKLM\Software\Classes (tests give
        /// another key).</summary>
        public static void RegisterPk(string name, string exe, RegistryKey classes = null)
        {
            using (RegistryKey machine = classes == null ? Machine() : null)
            using (RegistryKey own = classes == null ? machine.CreateSubKey(@"Software\Classes") : null)
            {
                RegistryKey root = classes ?? own;
                string progId = PkProgId(name);
                // "plate kinetics experiment" in Chinese, then in English
                string typeName = "\u5b54\u677f\u52a8\u529b\u5b66\u5b9e\u9a8c / Plate Kinetics experiment";
                using (RegistryKey type = root.CreateSubKey(progId))
                {
                    type.SetValue("", typeName);
                    type.SetValue("FriendlyTypeName", typeName);
                    using (RegistryKey icon = type.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                    using (RegistryKey command = type.CreateSubKey(@"shell\open\command")) command.SetValue("", "\"" + exe + "\" --open \"%1\"");
                }
                using (RegistryKey extension = root.CreateSubKey(PkExtension))
                {
                    string current = extension.GetValue("") as string;
                    bool foreign = !string.IsNullOrEmpty(current) && !OurPkTypes.Contains(current, StringComparer.OrdinalIgnoreCase) && KeyExists(root, current);
                    if (!foreign)
                    {
                        extension.SetValue("", progId);
                        extension.SetValue("Content Type", PkContentType);
                    }
                    using (RegistryKey with = extension.CreateSubKey("OpenWithProgids")) with.SetValue(progId, "");
                }
            }
            AssociationsChanged();
        }

        /// <summary>Removes what RegisterPk made for this program, when its command starts this exe: its file type and
        /// its place among the programs that open .pk; .pk itself goes to the other Plate Kinetics Live program when that
        /// one is registered, else it is left without a program. True when anything was removed.</summary>
        public static bool UnregisterPk(string name, string exe, RegistryKey classes = null)
        {
            string progId = PkProgId(name);
            using (RegistryKey machine = classes == null ? Machine() : null)
            using (RegistryKey look = classes == null ? machine.OpenSubKey(@"Software\Classes") : null)
            {
                RegistryKey readRoot = classes ?? look;
                if (readRoot == null) return false;
                string command;
                using (RegistryKey open = readRoot.OpenSubKey(progId + @"\shell\open\command")) command = open?.GetValue("") as string;
                if (command == null || command.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0) return false;    // not there, or another copy's
            }
            using (RegistryKey machine = classes == null ? Machine() : null)
            using (RegistryKey own = classes == null ? machine.OpenSubKey(@"Software\Classes", true) : null)
            {
                RegistryKey root = classes ?? own;
                root.DeleteSubKeyTree(progId, false);
                using (RegistryKey extension = root.OpenSubKey(PkExtension, true))
                    if (extension != null)
                    {
                        using (RegistryKey with = extension.OpenSubKey("OpenWithProgids", true)) with?.DeleteValue(progId, false);
                        if (string.Equals(extension.GetValue("") as string, progId, StringComparison.OrdinalIgnoreCase))
                        {
                            string other = OurPkTypes.FirstOrDefault(t => !t.Equals(progId, StringComparison.OrdinalIgnoreCase) && KeyExists(root, t));
                            if (other != null) extension.SetValue("", other);
                            else
                            {
                                extension.DeleteValue("", false);
                                extension.DeleteValue("Content Type", false);
                            }
                        }
                    }
                // nothing left of .pk (no values, at most an empty OpenWithProgids): the key goes
                bool empty = false;
                using (RegistryKey extension = root.OpenSubKey(PkExtension))
                    if (extension != null && extension.ValueCount == 0)
                    {
                        string[] subKeys = extension.GetSubKeyNames();
                        empty = subKeys.All(s => s.Equals("OpenWithProgids", StringComparison.OrdinalIgnoreCase));
                        if (empty && subKeys.Length > 0)
                            using (RegistryKey with = extension.OpenSubKey("OpenWithProgids"))
                                empty = with == null || (with.ValueCount == 0 && with.SubKeyCount == 0);
                    }
                if (empty) root.DeleteSubKeyTree(PkExtension, false);
            }
            AssociationsChanged();
            return true;
        }

        static bool KeyExists(RegistryKey root, string path)
        {
            try { using (RegistryKey key = root.OpenSubKey(path)) return key != null; }
            catch (Exception) { return false; }
        }

        /// <summary>Tells Explorer that file types changed (icons, "open with").</summary>
        static void AssociationsChanged()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { }
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        public static List<int> ProcessIds(string processName, int exceptId)
        {
            var ids = new List<int>();
            foreach (Process process in Process.GetProcessesByName(processName))
                using (process)
                    if (process.Id != exceptId) ids.Add(process.Id);
            return ids;
        }

        /// <summary>The processes of this name other than exceptId that run in a user's session, as "id (session n)":
        /// the program's windows. Session 0 is left out (the service runs there, and no window there is seen by anybody),
        /// and so are the trays ("--tray"), which follow an update by themselves.</summary>
        public static List<string> UserSessionProcesses(string processName, int exceptId, string folder = null)
        {
            var list = new List<string>();
            HashSet<int> trays = TrayProcessIds(processName);
            foreach (Process process in Process.GetProcessesByName(processName))
                using (process)
                {
                    int session = SessionOf(process);
                    if (process.Id != exceptId && session != 0 && !trays.Contains(process.Id) && InFolder(process, folder))
                        list.Add(process.Id + " (session " + session + ")");
                }
            return list;
        }

        /// <summary>Whether the process runs a program file in the folder (or no folder is given). One whose program
        /// file cannot be read counts as in it: a copy of the program elsewhere (a portable one that may be waiting
        /// to record) is left alone only when that is certain.</summary>
        static bool InFolder(Process process, string folder)
        {
            if (folder == null) return true;
            try
            {
                string path = process.MainModule.FileName;
                return path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return true; }
        }

        static int SessionOf(Process process)
        {
            try { return process.SessionId; }
            catch (Exception) { return -1; }
        }

        /// <summary>Asks the program's windows in this process's own session to close, as their close button does (each
        /// ends by itself): again every 2 s - a window may not be there yet, or be busy - for up to the seconds given,
        /// until none is left. Windows in other sessions cannot be reached from here (an installer started over SSH
        /// runs in session 0, the user's windows in session 1) and are not waited for, nor is a process in session 0.
        /// alsoExceptId: a second process to leave alone (the uninstaller's copy that waits for its elevated one).</summary>
        public static void CloseWindows(string processName, int exceptId, int seconds, string folder = null, int alsoExceptId = -1)
        {
            int session;
            using (Process me = Process.GetCurrentProcess()) session = SessionOf(me);
            if (session <= 0) return;
            HashSet<int> trays = TrayProcessIds(processName);       // no window to close: they follow an update by themselves
            var waited = Stopwatch.StartNew();
            TimeSpan nextAsk = TimeSpan.Zero;
            while (true)
            {
                bool ask = waited.Elapsed >= nextAsk, any = false;
                foreach (Process process in Process.GetProcessesByName(processName))
                    using (process)
                    {
                        if (process.Id == exceptId || process.Id == alsoExceptId || trays.Contains(process.Id) || SessionOf(process) != session ||
                            !InFolder(process, folder)) continue;
                        any = true;
                        if (ask) try { process.CloseMainWindow(); } catch (Exception) { }
                    }
                if (!any || waited.Elapsed.TotalSeconds >= seconds) return;
                if (ask) nextAsk = waited.Elapsed + TimeSpan.FromSeconds(2);
                System.Threading.Thread.Sleep(250);
            }
        }

        // ---- the tray: "<exe> --tray", one per signed-in user, started at every sign-in (HKLM ...\Run) ----

        public const string TrayOption = "--tray";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>The command line Windows runs at every sign-in for the program's tray.</summary>
        public static string TrayCommand(string exe) => "\"" + exe + "\" " + TrayOption;

        /// <summary>The program's tray starts at every sign-in, for every user of the PC (HKLM\...\Run, the value named
        /// after the program; Task Manager's "Startup apps" can switch it off for a user).</summary>
        public static void SetRunEntry(string name, string exe)
        {
            using (RegistryKey machine = Machine())
            using (RegistryKey run = machine.CreateSubKey(RunKey))
                run.SetValue(name, TrayCommand(exe));
        }

        /// <summary>The program file the sign-in entry of this name starts, or null.</summary>
        public static string RunEntryExe(string name)
        {
            try
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey run = machine.OpenSubKey(RunKey))
                    return ImageExe(run?.GetValue(name) as string);
            }
            catch (Exception) { return null; }
        }

        /// <summary>Removes the sign-in entry of this name when it starts this program file (null: whichever). True when
        /// one was removed.</summary>
        public static bool RemoveRunEntry(string name, string exe)
        {
            try
            {
                string now = RunEntryExe(name);
                if (now == null || (exe != null && !SameFile(now, exe))) return false;
                using (RegistryKey machine = Machine())
                using (RegistryKey run = machine.OpenSubKey(RunKey, true))
                    run?.DeleteValue(name, false);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The name of the event that tells the program's trays in every session to end (an uninstall sets it).</summary>
        public static string TrayQuitEvent(string appName) => @"Global\" + appName + "-tray-quit";

        /// <summary>True for a command line that starts the program's tray.</summary>
        public static bool IsTrayCommandLine(string commandLine) =>
            commandLine != null && (" " + commandLine + " ").IndexOf(" " + TrayOption + " ", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The processes of the program that are its trays ("--tray" on their command line, as Windows tells
        /// it to administrators and to the user who runs them). Empty when that cannot be told.</summary>
        public static HashSet<int> TrayProcessIds(string processName)
        {
            var ids = new HashSet<int>();
            try
            {
                string query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + processName.Replace("'", "").Replace("\\", "") + ".exe'";
                using (var searcher = new System.Management.ManagementObjectSearcher(query))
                using (System.Management.ManagementObjectCollection found = searcher.Get())
                    foreach (System.Management.ManagementBaseObject process in found)
                        using (process)
                            if (IsTrayCommandLine(process["CommandLine"] as string)) ids.Add(Convert.ToInt32(process["ProcessId"]));
            }
            catch (Exception) { }
            return ids;
        }

        /// <summary>Ends the program's trays in every session: the event they look for every 2 s is set (an administrator
        /// may make such a Global one), and those still there after the seconds given are ended (a tray holds nothing but
        /// the program file). How many there were.</summary>
        public static int StopTrays(string appName, string processName, int seconds)
        {
            HashSet<int> trays = TrayProcessIds(processName);
            if (trays.Count == 0) return 0;
            EventWaitHandle quit = null;
            try
            {
                var security = new EventWaitHandleSecurity();
                security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    EventWaitHandleRights.Synchronize, AccessControlType.Allow));
                security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
                quit = new EventWaitHandle(true, EventResetMode.ManualReset, TrayQuitEvent(appName), out bool _, security);
                quit.Set();
            }
            catch (Exception) { }          // not allowed to make it (not administrator): the trays are ended below
            try
            {
                var waited = Stopwatch.StartNew();
                while (waited.Elapsed.TotalSeconds < seconds && trays.Any(Alive)) Thread.Sleep(250);
                List<int> left = trays.Where(Alive).ToList();
                if (left.Count > 0)
                {
                    try { Process.EnterDebugMode(); } catch (Exception) { }      // trays of other users
                    foreach (int id in left)
                        try { using (Process p = Process.GetProcessById(id)) { p.Kill(); p.WaitForExit(5000); } }
                        catch (Exception) { }
                }
            }
            finally
            {
                quit?.Dispose();
            }
            return trays.Count;
        }

        static bool Alive(int id)
        {
            try { using (Process p = Process.GetProcessById(id)) return !p.HasExited; }
            catch (Exception) { return false; }
        }

        /// <summary>Starts a program as the user signed in to this session, without this process's administrator rights:
        /// through the desktop shell (Explorer's ShellExecute; what a double-click would do). The tray and the window
        /// must never run as administrator. False when there is no shell to ask (no one signed in to this session,
        /// Explorer not running): then they start at the next sign-in, or from the Start menu.</summary>
        public static bool StartAsShellUser(string file, string arguments, string folder)
        {
            object windows = null, desktop = null, browser = null, view = null, folderView = null, application = null;
            try
            {
                using (Process me = Process.GetCurrentProcess()) if (SessionOf(me) <= 0) return false;
                windows = new ShellWindowsObject();
                object location = 0, root = null;            // CSIDL_DESKTOP; VT_EMPTY
                desktop = ((IShellWindows)windows).FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
                if (desktop == null) return false;
                Guid service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");        // SID_STopLevelBrowser
                Guid browserId = typeof(IShellBrowser).GUID;
                browser = ((IComServiceProvider)desktop).QueryService(ref service, ref browserId);
                ((IShellBrowser)browser).QueryActiveShellView(out IShellView shellView);
                view = shellView;
                Guid dispatch = new Guid("00020400-0000-0000-C000-000000000046");        // IID_IDispatch
                shellView.GetItemObject(0 /* SVGIO_BACKGROUND */, ref dispatch, out folderView);
                application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
                application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
                    new object[] { file, arguments ?? "", folder ?? "", "open", 1 /* SW_SHOWNORMAL */ });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                foreach (object o in new[] { application, folderView, view, browser, desktop, windows })
                    if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch (Exception) { }
            }
        }

        [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
        class ShellWindowsObject { }

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
            void RegisterPending(int threadId, [In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int cookie);
            void Revoke(int cookie);
            void OnNavigate(int cookie, [In, MarshalAs(UnmanagedType.Struct)] ref object loc);
            void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int hwnd, int options);
            void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
            void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
        }

        // IShellBrowser and IShellView: every method up to the ones used, in their order (only their places matter)
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void GetWindow(out IntPtr hwnd);
            void ContextSensitiveHelp(int enterMode);
            void InsertMenusSB(IntPtr shared, IntPtr widths);
            void SetMenuSB(IntPtr shared, IntPtr oleMenu, IntPtr activeObject);
            void RemoveMenusSB(IntPtr shared);
            void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
            void EnableModelessSB(int enable);
            void TranslateAcceleratorSB(IntPtr msg, ushort id);
            void BrowseObject(IntPtr pidl, uint flags);
            void GetViewStateStream(uint mode, out IntPtr stream);
            void GetControlWindow(uint id, out IntPtr hwnd);
            void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
            void QueryActiveShellView(out IShellView view);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellView
        {
            void GetWindow(out IntPtr hwnd);
            void ContextSensitiveHelp(int enterMode);
            void TranslateAccelerator(IntPtr msg);
            void EnableModeless(int enable);
            void UIActivate(uint state);
            void Refresh();
            void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
            void DestroyViewWindow();
            void GetCurrentInfo(IntPtr settings);
            void AddPropertySheetPages(uint reserved, IntPtr add, IntPtr lParam);
            void SaveViewState();
            void SelectItem(IntPtr pidl, uint flags);
            void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        /// <summary>An update renames a program file that a running process holds (the exe and the WebView2 DLLs of a
        /// window still open) to "file" + this + N, then writes the new file: Windows lets a file in use be renamed,
        /// not overwritten or deleted. The next installation, or the uninstaller, removes the copy.</summary>
        public const string ReplacedSuffix = ".replaced-";

        /// <summary>The name to rename a program file in use to: "file.replaced-N", the first N that is free.</summary>
        public static string ReplacedName(string path)
        {
            for (int n = 1; ; n++)
            {
                string candidate = path + ReplacedSuffix + n;
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>The copies an update renamed aside ("file.replaced-N") of the given files of the program's folder
        /// (relative paths): in the folder itself and the folders of those files (web\). Nothing else - never the
        /// user's files kept as "file.old".</summary>
        public static List<string> ReplacedFiles(string dir, IEnumerable<string> relativeFiles)
        {
            var found = new List<string>();
            try
            {
                var byFolder = relativeFiles.Where(IsSafeRelative).Select(rel => Path.Combine(dir, rel))
                    .GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var folder in byFolder)
                {
                    var names = new HashSet<string>(folder.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (!Directory.Exists(folder.Key)) continue;
                        foreach (string path in Directory.GetFiles(folder.Key, "*" + ReplacedSuffix + "*"))
                        {
                            string name = Path.GetFileName(path);
                            int at = name.LastIndexOf(ReplacedSuffix, StringComparison.OrdinalIgnoreCase);
                            string number = at > 0 ? name.Substring(at + ReplacedSuffix.Length) : "";
                            if (number.Length > 0 && number.All(c => c >= '0' && c <= '9') && names.Contains(name.Substring(0, at)))
                                found.Add(path);
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return found;
        }

        public static bool TryDelete(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }
                catch (Exception) when (attempt < 4)
                {
                    System.Threading.Thread.Sleep(250);       // a virus scanner may hold a file for a moment
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void TryRemoveEmptyFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
            }
            catch (Exception) { }
        }

        /// <summary>Folders an uninstaller must never remove, even empty: the desktop, documents, roots and the like.</summary>
        public static bool IsSpecialFolder(string dir)
        {
            try
            {
                string full = Normal(dir);
                if (string.Equals(Path.GetPathRoot(full).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase)) return true;
                var special = new List<string> { Path.GetTempPath() };
                foreach (Environment.SpecialFolder f in new[]
                         {
                             Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                             Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                             Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.ProgramFiles,
                             Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                             Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments,
                             Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonStartMenu,
                         })
                    special.Add(Environment.GetFolderPath(f));
                special.Add(Environment.GetEnvironmentVariable("ProgramW6432"));
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) special.Add(Path.Combine(profile, "Downloads"));
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) special.Add(Path.Combine(local, "Programs"));
                return special.Any(s => !string.IsNullOrEmpty(s) && SameFolder(s, full));
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Removes files once this process has ended: a hidden cmd tries once a second for up to a minute
        /// until all of them are gone, then removes the given folders if they are empty (rd without /s).</summary>
        public static void DeleteAfterExit(IList<string> files, IList<string> folders)
        {
            string Quote(string path) => "\"" + path + "\"";
            string allGone = string.Concat(files.Select(f => "if not exist " + Quote(f) + " "));
            string removeFolders = string.Concat(folders.Select(d => "rd " + Quote(d) + " >nul 2>&1 & "));
            string loop = "for /l %i in (1,1,60) do @(del /f /q " + string.Join(" ", files.Select(Quote)) + " >nul 2>&1 & (" +
                          allGone + "(" + removeFolders + "exit)) & ping -n 2 127.0.0.1 >nul)";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + loop + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
            using (Process.Start(start)) { }
        }

        /// <summary>True for a .NET program (a PE file with a CLR header): the new program. The old one-file (Python,
        /// PyInstaller) build has none.</summary>
        public static bool IsManagedExe(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;            // "MZ"
                    stream.Position = 0x3C;
                    long pe = reader.ReadUInt32();
                    if (pe + 24 + 112 + 15 * 8 > stream.Length) return false;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550) return false;                                  // "PE\0\0"
                    stream.Position = pe + 24;
                    bool pe32Plus = reader.ReadUInt16() == 0x20B;
                    stream.Position = pe + 24 + (pe32Plus ? 108 : 92);                                    // NumberOfRvaAndSizes
                    if (reader.ReadUInt32() < 15) return false;
                    stream.Position = pe + 24 + (pe32Plus ? 112 : 96) + 14 * 8;                           // the CLR header's entry
                    uint address = reader.ReadUInt32(), size = reader.ReadUInt32();
                    return address != 0 && size != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Makes (or replaces) a shortcut to a program or a folder; icon: the file whose first icon it shows
        /// (null: the target's).</summary>
        public static void CreateShortcut(string link, string target, string workingFolder, string description, string icon = null)
        {
            var shellLink = (IShellLinkW)new ShellLinkObject();
            try
            {
                shellLink.SetPath(target);
                shellLink.SetWorkingDirectory(workingFolder);
                shellLink.SetDescription(description);
                shellLink.SetIconLocation(icon ?? target, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                ((IPersistFile)shellLink).Save(link, true);
            }
            finally
            {
                Marshal.ReleaseComObject(shellLink);
            }
        }

        /// <summary>The program a shortcut starts, or null when it cannot be read.</summary>
        public static string ShortcutTarget(string link)
        {
            IShellLinkW shellLink = null;
            try
            {
                shellLink = (IShellLinkW)new ShellLinkObject();
                ((IPersistFile)shellLink).Load(link, 0);
                var path = new StringBuilder(1024);
                shellLink.GetPath(path, path.Capacity, IntPtr.Zero, 0);
                return path.Length == 0 ? null : path.ToString();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (shellLink != null) Marshal.ReleaseComObject(shellLink);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
