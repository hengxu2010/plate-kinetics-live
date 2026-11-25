// The program's own window (replaces the Edge --app window of the Python programs): a WinForms window with
// Windows' own web control (WebView2) showing the local page http://127.0.0.1:<port>/. Each Windows user's window keeps
// its size in its own folder (System\users\<user>) and its web profile (what the page saves: language, analysis
// settings, its autosave) where WebProfile says: the installed program's in the user's own local application data, kept
// small (no add-on downloads, caches removed before each start); at its first start the profile of an earlier version
// is carried over.
//
// The window and its page talk through the web control's messages:
//   page -> window   {cmd: "openFolder", file}       Explorer on the Experiments folder ("Open data folder"), or on the
//                                                    folder of one of its files with the file selected ("Show in folder";
//                                                    file relative to Experiments, '/' between folders)
//                    {cmd: "recycle", file}          the .pk (and its .xlsx) to the Recycle Bin; answered {cmd: "recycled",
//                                                    file, ok, error}
//                    {cmd: "currentExperiment", file} the experiment the page shows (file relative to Experiments, "" for
//                                                    none): every download's "Save as" dialog opens in its folder
//                    {cmd: "saveAnalysisTo", path, analysis}  a .pk opened from outside Experiments: the window writes the
//                                                    analysis into it; answered {cmd: "analysisSaved", path, ok, savedAt, error}
//                    {cmd: "help", lang}             the manual
//   window -> page   {cmd: "openRun", name, run, path, analysis, info}  an experiment file (.pk) was opened (a double-click
//                                                    on it): its run as GET /api/run gives one, its saved analysis (or null)
//                    {cmd: "focus"}                  the window was brought to the front by a second start
// Before the page's own script runs, window.PKHost = {window: true, user, pc, edition, recordsHere} tells the page where
// it is shown.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SAAControl
{
    public sealed class MainWindow : Form
    {
        readonly string appName;
        readonly string url;
        readonly string userDir;               // this Windows user's window folder
        readonly string profile;               // the web control's folder in it
        readonly IList<string> carryOverFrom;
        readonly string placementFile, firstPlacement;
        WebView2 view;
        bool closeConfirmed;                   // the page has been left (or may be): the window may close
        bool closeAsked;                       // the page is being asked whether it may be left
        bool stopAsked;                        // the user agreed that closing stops recording
        readonly Timer closeTimeout = new Timer { Interval = 8000 };
        ulong? pageNavigation;                 // the latest navigation to the page itself
        readonly Timer reload = new Timer { Interval = 3000 };    // the page could not be loaded: it is tried again
        readonly List<string> toOpen = new List<string>();        // experiment files to show once the page is there
        bool pageReady;                        // the page has loaded
        readonly Timer opening = new Timer { Interval = 700 };    // (the page's own start is given a moment)
        string currentFolder;                  // the folder of the experiment the page shows: downloads are saved there
        readonly List<DateTime> failures = new List<DateTime>();  // the web control's failures (it is made again a few times)

        /// <summary>The Experiments folder of the program's data folder (the page asks the window to open it in Explorer).</summary>
        public string ExperimentsFolder { get; set; }

        /// <summary>This process records (the program without its service): closing the window stops recording, so the
        /// user is asked first.</summary>
        public bool RecordsHere { get; set; }

        // "Leave this page? Unsaved changes may be lost." (asked instead of Edge's "Leave site?")
        const string LeaveQuestion = "\u79bb\u5f00\u6b64\u9875\uff1f\u672a\u4fdd\u5b58\u7684\u66f4\u6539\u53ef\u80fd\u4f1a\u4e22\u5931\u3002\n" +
                                     "Leave this page? Unsaved changes may be lost.";
        // "Closing this window stops recording: runs that are measuring are not recorded until the program is started
        // again (the instrument software goes on). Close it anyway?"
        const string StopQuestion = "\u5173\u95ed\u8fd9\u4e2a\u7a97\u53e3\u4f1a\u505c\u6b62\u8bb0\u5f55\uff1a\u5728\u518d\u6b21\u542f\u52a8\u672c\u7a0b\u5e8f\u4e4b\u524d\uff0c" +
                                    "\u6b63\u5728\u6d4b\u91cf\u7684\u5b9e\u9a8c\u4e0d\u4f1a\u88ab\u8bb0\u5f55\uff08\u4eea\u5668\u8f6f\u4ef6\u7167\u5e38\u8fd0\u884c\uff09\u3002\u4ecd\u7136\u5173\u95ed\u5417\uff1f\n\n" +
                                    "Closing this window stops recording: runs that are measuring are not recorded until the program is started " +
                                    "again (the instrument software goes on). Close it anyway?";

        /// <summary>Set when the web control could not be started: the host then shows the page in the default browser.</summary>
        public Exception StartFailure { get; private set; }

        /// <param name="userDir">this Windows user's window folder (System\users\&lt;user&gt;)</param>
        /// <param name="profile">the web control's folder in it: "webview" for the program's own window, another for a
        /// window opened on another running instance</param>
        /// <param name="carryOverFrom">profiles of earlier versions, the first that is there is carried over (null: none)</param>
        /// <param name="firstPlacement">the window's size and place of an earlier version, used while this user has none</param>
        public MainWindow(string appName, string url, string userDir, string profile = "webview", IList<string> carryOverFrom = null, string firstPlacement = null)
        {
            this.appName = appName;
            this.url = url;
            this.userDir = userDir;
            this.profile = profile;
            this.carryOverFrom = carryOverFrom ?? new List<string>();
            this.firstPlacement = firstPlacement;
            placementFile = Path.Combine(userDir, profile == "webview" ? "window.json" : profile + "-window.json");
            Text = InstallShared.DisplayName(appName);   // the program's name stays the window's title (the page's names the analysis page)
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            var area = Screen.PrimaryScreen.WorkingArea;
            // the window counts the screen's own pixels (it follows the screen's scale, see app.manifest), the Edge
            // window's sizes were for a screen at 100 %: they grow with the scale, but never beyond the screen
            Size Scaled(int width, int height) =>
                new Size(Math.Min(width * DeviceDpi / 96, area.Width), Math.Min(height * DeviceDpi / 96, area.Height));
            Size = Scaled(1500, 950);              // the Edge window's first size
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = Scaled(640, 480);
            RestorePlacement();
            view = NewView();
            Controls.Add(view);
            Load += async (s, e) => await StartAsync();
            FormClosing += OnClosing;
            closeTimeout.Tick += (s, e) => { closeTimeout.Stop(); closeConfirmed = true; Close(); };   // a page that does not answer
            reload.Tick += (s, e) =>
            {
                reload.Stop();
                if (closeAsked || closeConfirmed || IsDisposed) return;
                try { view.CoreWebView2?.Navigate(url); } catch (Exception) { }
            };
            opening.Tick += (s, e) =>
            {
                opening.Stop();
                if (!pageReady || closeAsked || closeConfirmed || IsDisposed) return;
                var files = toOpen.ToList();
                toOpen.Clear();
                foreach (string file in files) ShowRun(file);
            };
        }

        static WebView2 NewView() => new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };

        /// <summary>Show experiment files (.pk) in the page, once it is there.</summary>
        public void Open(IEnumerable<string> files)
        {
            foreach (string file in files ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(file)) toOpen.Add(file);
            if (toOpen.Count > 0 && pageReady && IsHandleCreated) opening.Start();
        }

        void Post(Dictionary<string, object> message)
        {
            try { view.CoreWebView2?.PostWebMessageAsJson(Json.Serialize(message, ensureAscii: false)); }
            catch (Exception error) { Log.Warning("a message to the page could not be sent (" + error.Message + ")"); }
        }

        /// <summary>The run of an experiment file to the page: {cmd: "openRun", name: the file's name, run, path, analysis, info}.</summary>
        void ShowRun(string file)
        {
            try
            {
                var read = PkFile.ReadFile(file, true, true);
                Post(new Dictionary<string, object>
                {
                    ["cmd"] = "openRun", ["name"] = Path.GetFileNameWithoutExtension(file), ["run"] = read.Run, ["path"] = file,
                    ["analysis"] = read.Analysis, ["info"] = read.Info,
                });
                Log.Info("the experiment file " + file + " is shown in the page");
            }
            catch (Exception error)
            {
                Log.Warning("the experiment file " + file + " cannot be shown (" + error.Message + ")");
                string why = error is NewerFormatException newer ? newer.MessageZh + "\n" + error.Message : error.Message;
                // "This file cannot be opened:"
                MessageBox.Show(this, "\u65e0\u6cd5\u6253\u5f00\u8fd9\u4e2a\u6587\u4ef6\uff1a\n" + file + "\n\n" + "This file cannot be opened:\n" + file +
                                "\n\n" + why, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Explorer on the Experiments folder, or on the folder of one of its files with the file selected.</summary>
        void OpenFolder(string file)
        {
            string folder = ExperimentsFolder;
            if (string.IsNullOrEmpty(folder)) return;
            string arguments;
            if (string.IsNullOrWhiteSpace(file))
            {
                try { Directory.CreateDirectory(folder); } catch (Exception) { }
                arguments = "\"" + (Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder)) + "\"";
            }
            else
            {
                string full = Experiments.ResolveIn(folder, file);
                if (full == null) return;              // not in Experiments: not opened
                if (File.Exists(full)) arguments = "/select,\"" + full + "\"";
                else if (Directory.Exists(full)) arguments = "\"" + full + "\"";
                else if (Directory.Exists(Path.GetDirectoryName(full))) arguments = "\"" + Path.GetDirectoryName(full) + "\"";
                else return;
            }
            try { Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
            catch (Exception error) { Log.Warning("Explorer could not be opened (" + error.Message + ")"); }
        }

        /// <summary>An experiment of Experiments (its .pk and .xlsx, with their .bak) to the Recycle Bin.</summary>
        void Recycle(string file)
        {
            string full = Experiments.ResolveIn(ExperimentsFolder, file ?? "");
            string error = null;
            if (full == null || !full.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase)) error = "not a .pk file in the Experiments folder";
            else if (!File.Exists(full)) error = "the file is not there";
            else
            {
                var files = Experiments.Companions(full);
                if (!RecycleBin.Send(files)) error = "the files could not be moved to the Recycle Bin";
                else Log.Info("moved to the Recycle Bin: " + string.Join(", ", files.Select(Path.GetFileName)));
            }
            Post(new Dictionary<string, object> { ["cmd"] = "recycled", ["file"] = file, ["ok"] = error == null, ["error"] = error });
        }

        /// <summary>The analysis into a .pk that lies outside Experiments (opened from a stick, a share, an e-mail): the
        /// window, as the user, writes it (raw data copied byte for byte). One in Experiments is the program's to write
        /// (POST /api/analysis), so that its copy in System stays the same.</summary>
        void SaveAnalysisTo(string path, Dictionary<string, object> analysis)
        {
            string error = null, savedAt = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || analysis == null) error = "no file or no analysis";
                else
                {
                    string full = Path.GetFullPath(path);
                    string inside = string.IsNullOrEmpty(ExperimentsFolder) ? null : Path.GetFullPath(ExperimentsFolder).TrimEnd('\\') + "\\";
                    if (!full.EndsWith(PkFile.Extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) error = "not a .pk file";
                    else if (inside != null && full.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) error = "inside the Experiments folder: saved by the program (POST /api/analysis)";
                    else
                    {
                        var read = PkFile.ReadFile(full, true, false, true);
                        var kept = Experiments.Normalized(analysis, Session.UserOnMachine(Session.ThisUser()));
                        SafeFile.Write(full, PkFile.WithAnalysis(read, kept), PkFile.Validate, keepBackup: true, hideBackup: true);
                        savedAt = kept["savedAt"] as string;
                        Log.Info("the analysis was saved into " + full);
                    }
                }
            }
            catch (Exception failure)
            {
                error = failure is NewerFormatException newer ? newer.Message : failure.Message;
                Log.Warning("the analysis could not be saved into " + path + " (" + failure.Message + ")");
            }
            Post(new Dictionary<string, object> { ["cmd"] = "analysisSaved", ["path"] = path, ["ok"] = error == null, ["savedAt"] = savedAt, ["error"] = error });
        }

        /// <summary>A message of the page (only of the program's own page).</summary>
        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (!e.Source.StartsWith(url, StringComparison.OrdinalIgnoreCase)) return;
                if (!(Json.Parse(e.WebMessageAsJson) is Dictionary<string, object> message)) return;
                if (!message.TryGetValue("cmd", out object cmd) || !(cmd is string command)) return;
                string Field(string key) => message.TryGetValue(key, out object v) ? v as string : null;
                switch (command)
                {
                    case "openFolder": OpenFolder(Field("file")); break;
                    case "recycle": Recycle(Field("file")); break;
                    case "currentExperiment":
                    {
                        string full = string.IsNullOrWhiteSpace(Field("file")) ? null : Experiments.ResolveIn(ExperimentsFolder, Field("file"));
                        currentFolder = full != null ? Path.GetDirectoryName(full) : null;
                        break;
                    }
                    case "saveAnalysisTo": SaveAnalysisTo(Field("path"), message.TryGetValue("analysis", out object a) ? a as Dictionary<string, object> : null); break;
                    case "help":
                        if (!Help.Open(Field("lang")))
                            // "The manual is not there (the Help folder beside the program)."
                            MessageBox.Show(this, "\u627e\u4e0d\u5230\u8bf4\u660e\u4e66\uff08\u7a0b\u5e8f\u65c1\u8fb9\u7684 Help \u6587\u4ef6\u5939\uff09\u3002\n" +
                                            "The manual is not there (the Help folder beside the program).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                }
            }
            catch (Exception error) { Log.Warning("a message of the page could not be handled (" + error.Message + ")"); }
        }

        /// <summary>Every download (an export, a figure, a copy of the page) asks where it goes: a "Save as" dialog in the
        /// folder of the experiment the page shows (else in Experiments). Shown after the event has returned; a deferral
        /// holds the download meanwhile.</summary>
        void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            var deferral = e.GetDeferral();
            string suggested = e.ResultFilePath;
            Action ask = () =>
            {
                try
                {
                    string name = Path.GetFileName(suggested) ?? "download";
                    string extension = Path.GetExtension(name);
                    string folder = currentFolder != null && Directory.Exists(currentFolder) ? currentFolder
                                  : !string.IsNullOrEmpty(ExperimentsFolder) && Directory.Exists(ExperimentsFolder) ? ExperimentsFolder : null;
                    using (var dialog = new SaveFileDialog
                    {
                        FileName = name, OverwritePrompt = true, AddExtension = true, RestoreDirectory = true,
                        Filter = (extension.Length > 1 ? extension.Substring(1).ToUpperInvariant() + " (*" + extension + ")|*" + extension + "|" : "") +
                                 "*.*|*.*",
                    })
                    {
                        if (folder != null) dialog.InitialDirectory = folder;
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            e.ResultFilePath = dialog.FileName;
                            e.Handled = true;          // (no download bubble: the user chose the place)
                            Log.Info("a download is saved as " + dialog.FileName);
                        }
                        else e.Cancel = true;
                    }
                }
                catch (Exception error) { Log.Warning("the download could not be asked about (" + error.Message + ")"); }
                finally { try { deferral.Complete(); } catch (Exception) { } }
            };
            try { BeginInvoke(ask); }
            catch (Exception) { try { deferral.Complete(); } catch (Exception) { } }
        }

        /// <summary>The web control's page (or the web control itself) failed - out of memory, a crash: the page is loaded
        /// again; a web control whose browser process ended is made again (at most 5 times in 10 minutes).</summary>
        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            var kind = e.ProcessFailedKind;
            Log.Warning("the web control's process failed (" + kind + "): the page is loaded again");
            BeginInvoke(new Action(() =>
            {
                if (closeAsked || closeConfirmed || IsDisposed) return;
                failures.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(10));
                failures.Add(DateTime.UtcNow);
                if (failures.Count > 5)
                {
                    Log.Error("the web control failed " + failures.Count + " times in 10 minutes; it is not made again");
                    return;
                }
                if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    try
                    {
                        var old = view;
                        Controls.Remove(old);
                        try { old.Dispose(); } catch (Exception) { }
                        view = NewView();
                        Controls.Add(view);
                        pageReady = false;
                        var _ = StartAsync();
                    }
                    catch (Exception error) { Log.Error("the web control could not be made again", error); }
                }
                else
                {
                    try { view.CoreWebView2?.Navigate(url); } catch (Exception) { }
                }
            }));
        }

        /// <summary>Closing the window leaves the page first, as closing the Edge window did: the page gets its chance to
        /// warn about unsaved work ("Leave site?") and to finish saving. Windows shutting down is never held up. A window
        /// that records (the program without its service) asks first whether recording may stop.</summary>
        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!closeConfirmed) SavePlacement();
            if (closeConfirmed || view.CoreWebView2 == null || e.CloseReason == CloseReason.WindowsShutDown
                || e.CloseReason == CloseReason.TaskManagerClosing || StartFailure != null) return;
            if (RecordsHere && !stopAsked && !closeAsked)
            {
                if (MessageBox.Show(this, StopQuestion, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                stopAsked = true;
            }
            if (closeAsked)
            {
                // closing again while the page has not let go for a while: the page is stuck, close anyway
                if ((DateTime.UtcNow - closeAskedAt).TotalSeconds > 8) { closeConfirmed = true; return; }
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            closeAsked = true;
            closeAskedAt = DateTime.UtcNow;
            closeTimeout.Start();              // stopped as soon as the page starts leaving (it may then ask "Leave site?")
            view.CoreWebView2.Navigate("about:blank");
        }

        DateTime closeAskedAt;

        void OnLeft(bool left)
        {
            if (!closeAsked) return;
            closeAsked = false;
            closeTimeout.Stop();
            if (!left)
            {
                stopAsked = false;             // the user chose to stay on the page
                return;
            }
            closeConfirmed = true;
            BeginInvoke(new Action(Close));
        }

        /// <summary>The page's own dialogs - "Leave site?" when it holds unsaved work, alert, confirm, prompt - as this
        /// window's message boxes. They are shown after the event has returned (a deferral holds the page meanwhile):
        /// the web control must not wait inside its own event.</summary>
        void OnScriptDialog(object sender, CoreWebView2ScriptDialogOpeningEventArgs e)
        {
            var kind = e.Kind;
            bool leaving = kind == CoreWebView2ScriptDialogKind.Beforeunload;
            if (leaving) closeTimeout.Stop();  // the user is asked now: no forced close while the question is open
            var deferral = e.GetDeferral();
            Action ask = () =>
            {
                try
                {
                    if (closeConfirmed || IsDisposed) return;   // the window is closing anyway
                    string text = leaving ? LeaveQuestion : e.Message;
                    if (kind == CoreWebView2ScriptDialogKind.Prompt && !string.IsNullOrEmpty(e.DefaultText)) text += "\n\n" + e.DefaultText;
                    var buttons = kind == CoreWebView2ScriptDialogKind.Alert ? MessageBoxButtons.OK : MessageBoxButtons.OKCancel;
                    if (MessageBox.Show(this, text, Text, buttons, leaving ? MessageBoxIcon.Warning : MessageBoxIcon.None) == DialogResult.OK)
                    {
                        if (kind == CoreWebView2ScriptDialogKind.Prompt) e.ResultText = e.DefaultText;   // a message box takes no input
                        if (kind != CoreWebView2ScriptDialogKind.Alert) e.Accept();
                    }
                    else if (leaving)
                    {
                        closeAsked = false;    // the user stays on the page: the window stays open
                        stopAsked = false;
                        closeTimeout.Stop();
                    }
                }
                catch (Exception) { }          // the window or the web control went away meanwhile
                finally { try { deferral.Complete(); } catch (Exception) { } }
            };
            try { BeginInvoke(ask); }
            catch (Exception) { try { deferral.Complete(); } catch (Exception) { } }
        }

        void RestorePlacement()
        {
            foreach (string file in new[] { placementFile, firstPlacement })
            {
                try
                {
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                    if (!(Json.Parse(File.ReadAllText(file)) is Dictionary<string, object> p)) continue;
                    var r = new Rectangle(Convert.ToInt32(p["x"]), Convert.ToInt32(p["y"]), Convert.ToInt32(p["width"]), Convert.ToInt32(p["height"]));
                    if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height) continue;   // (the minimum at the screen's scale)
                    bool visible = false;
                    foreach (var s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) { var i = Rectangle.Intersect(s.WorkingArea, r); visible |= i.Width >= 200 && i.Height >= 120; }
                    if (!visible) continue;        // the screen it was on is gone
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (p.TryGetValue("maximized", out var m) && m is bool max && max) WindowState = FormWindowState.Maximized;
                    return;
                }
                catch (Exception) { }
            }
        }

        void SavePlacement()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var p = new Dictionary<string, object>
                {
                    ["x"] = (long)r.X, ["y"] = (long)r.Y, ["width"] = (long)r.Width, ["height"] = (long)r.Height,
                    ["maximized"] = WindowState == FormWindowState.Maximized,
                };
                Directory.CreateDirectory(userDir);
                File.WriteAllText(placementFile, Json.Serialize(p));
            }
            catch (Exception) { }
        }

        async Task StartAsync()
        {
            try
            {
                // the web profile (see WebProfile): installed, in the user's own local application data; only what the
                // page keeps is left from one start to the next, and Edge downloads no add-on components into it
                string folder = WebProfile.Folder(userDir, profile, Runtime.Edition == "installed", InstallShared.DisplayName(appName));
                try
                {
                    string moved = WebProfile.MoveOld(Path.Combine(userDir, profile), folder);
                    if (moved != null) Log.Info(moved);
                }
                catch (Exception error) { Log.Warning("the window's web profile could not be moved to " + folder + " (" + error.Message + ")"); }
                string source = carryOverFrom.FirstOrDefault(Directory.Exists);
                if (source != null) CarryOverEdgeProfile(source, Path.Combine(folder, "EBWebView"), appName);
                try
                {
                    long freed = WebProfile.Trim(folder);
                    if (freed >= 1048576) Log.Info("the window's web profile: " + (freed / 1048576) + " MB of caches and add-ons removed");
                }
                catch (Exception error) { Log.Warning("the window's web profile could not be trimmed (" + error.Message + ")"); }
                var env = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions(WebProfile.BrowserArguments));
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                // the page's dialogs are this window's own (see OnScriptDialog): "Stay" must keep the window open
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.ScriptDialogOpening += OnScriptDialog;
                core.WebMessageReceived += OnWebMessage;
                core.DownloadStarting += OnDownload;
                core.ProcessFailed += OnProcessFailed;
                // where the page is shown: the program's window, for which user
                var host = new Dictionary<string, object>
                {
                    ["window"] = true, ["user"] = Session.ThisUser(), ["pc"] = Session.Machine(), ["edition"] = Runtime.Edition, ["recordsHere"] = RecordsHere,
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.PKHost = " + Json.Serialize(host) + ";");
                // links that open a new window (a shared room's web address) go to the default browser
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch (Exception) { }
                };
                // leaving the page for about:blank is how the window closes (see OnClosing)
                core.NavigationCompleted += (s, e) =>
                {
                    if (closeAsked)
                    {
                        // (a load of the page itself that the closing cut short is no answer)
                        if (e.NavigationId != pageNavigation) OnLeft(e.IsSuccess && core.Source == "about:blank");
                        return;
                    }
                    // the page could not be loaded (the service may still be starting): try again in 3 s, until it loads
                    if (!e.IsSuccess && !closeConfirmed && e.NavigationId == pageNavigation
                        && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) reload.Start();
                    // the page is there: experiment files waiting to be shown go to it
                    if (e.IsSuccess && e.NavigationId == pageNavigation)
                    {
                        pageReady = true;
                        if (toOpen.Count > 0) opening.Start();
                    }
                };
                core.NavigationStarting += (s, e) =>
                {
                    if (closeAsked && e.Uri == "about:blank") { closeTimeout.Stop(); return; }   // the page answers: no forced close
                    bool own = e.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase);
                    if (own) { pageNavigation = e.NavigationId; reload.Stop(); pageReady = false; }
                    // the page must not wander off to another site inside the program's window
                    if (closeAsked || e.Uri == "about:blank" || own) return;
                    e.Cancel = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch (Exception) { }
                };
                core.Navigate(url);
            }
            catch (Exception error)
            {
                StartFailure = error;
                Log.Error("the window's web control could not be started: showing the page in the default browser", error);
                BeginInvoke(new Action(Close));
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr window);

        /// <summary>Bring the window to the front (a second start of the program asks for this) and tell the page.</summary>
        public void ShowToFront()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
            try { SetForegroundWindow(Handle); } catch (Exception) { }
            if (pageReady) Post(new Dictionary<string, object> { ["cmd"] = "focus" });
        }

        const int CarryOverTries = 5;          // starts that try the carry-over before it is given up

        /// <summary>The window of an earlier version kept its web profile elsewhere (the Python programs: the Edge profile
        /// "browser"; the window up to 2.2: one "webview" for every user). Before the web control first starts, a copy of
        /// that profile becomes this user's own (the same web engine, the same address), so what the page saved there -
        /// language, analysis settings, its autosave - is still there. The old profile is left as it was. Caches are not
        /// copied. The carry-over is done only once the copy is in place: until then a marker beside it stays and the next
        /// start tries again (the profile the web control made meanwhile is put aside, not deleted).</summary>
        public static void CarryOverEdgeProfile(string edgeProfile, string webviewProfile, string appName = null)
        {
            string pending = webviewProfile + ".carry-over";   // begun, not done yet; holds the number of tries
            if (!Directory.Exists(edgeProfile) || (Directory.Exists(webviewProfile) && !File.Exists(pending))) return;
            try
            {
                // two starts at the same moment must not copy into the same folder: the second waits, then finds it done
                string name = "Local\\" + (appName ?? Path.GetFileNameWithoutExtension(Application.ExecutablePath)) + "-carry-over";
                using (var gate = new System.Threading.Mutex(false, name))
                {
                    bool owned;
                    try { owned = gate.WaitOne(60000); }
                    catch (System.Threading.AbandonedMutexException) { owned = true; }   // the other start ended without letting go
                    if (!owned)
                    {
                        Log.Warning("another start is still carrying over the old Edge profile; this window does not wait for it");
                        return;
                    }
                    try { CarryOver(edgeProfile, webviewProfile, pending); }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception error) { Log.Error("carrying over the old Edge profile failed", error); }
        }

        static void CarryOver(string edgeProfile, string webviewProfile, string pending)
        {
            if (!Directory.Exists(edgeProfile) || (Directory.Exists(webviewProfile) && !File.Exists(pending))) return;   // done meanwhile
            int tries = 0;
            try { if (File.Exists(pending)) int.TryParse(File.ReadAllText(pending).Trim(), out tries); } catch (Exception) { }
            if (tries >= CarryOverTries)
            {
                try { File.Delete(pending); } catch (Exception) { }
                Log.Error("carrying over the old Edge profile failed at " + tries + " starts; it is not tried again");
                return;
            }
            string partial = webviewProfile + ".copying", aside = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pending));
                File.WriteAllText(pending, (tries + 1).ToString());
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
                CopyTree(new DirectoryInfo(edgeProfile), new DirectoryInfo(partial));
                if (Directory.Exists(webviewProfile))
                {
                    // the profile the web control made after an earlier try had failed
                    aside = webviewProfile + ".unused-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Retried(() => Directory.Move(webviewProfile, aside));
                }
                Retried(() => Directory.Move(partial, webviewProfile));
            }
            catch (Exception error)
            {
                Log.Error("carrying over the old Edge profile failed (try " + (tries + 1) + " of " + CarryOverTries
                          + "); the window starts with a new one", error);
                try { if (aside != null && !Directory.Exists(webviewProfile)) Directory.Move(aside, webviewProfile); } catch (Exception) { }
                try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch (Exception) { }
                return;
            }
            try { Retried(() => File.Delete(pending)); }
            catch (Exception error) { Log.Error("the carry-over marker " + pending + " could not be removed: the next start carries the profile over again", error); }
            Log.Info("the window's settings were carried over from the old Edge profile (" + edgeProfile + ")"
                     + (aside != null ? "; the window's profile of the earlier start is kept in " + aside : ""));
        }

        /// <summary>A rename or delete that a virus scanner or the search indexer may hold up for a moment (it still has a
        /// file just written open): tried up to 10 times, 300 ms apart.</summary>
        static void Retried(Action action)
        {
            for (int i = 1; ; i++)
            {
                try { action(); return; }
                catch (Exception error) when (i < 10 && (error is IOException || error is UnauthorizedAccessException))
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
        }

        static readonly string[] Skip = { "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache",
                                          "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache", "Crashpad", "BrowserMetrics",
                                          "Service Worker" };

        static void CopyTree(DirectoryInfo from, DirectoryInfo to)
        {
            to.Create();
            foreach (var f in from.GetFiles())
            {
                if (f.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || f.Name == "lockfile" || f.Name == "LOCK") continue;
                try { f.CopyTo(Path.Combine(to.FullName, f.Name), false); }
                catch (IOException) { }               // a file still in use (should not be: the old program is closed)
            }
            foreach (var d in from.GetDirectories())
            {
                if (Skip.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                CopyTree(d, new DirectoryInfo(Path.Combine(to.FullName, d.Name)));
            }
        }
    }
}
