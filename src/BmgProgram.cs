// Plate Kinetics Live (until 2026-11-23: SAAControl) for BMG Omega plate readers: the entry point (Python: BMG/app.py main()). The host (Common/Host.cs)
// does the window, the local server, the service, the data folder and the single instance; this file only builds the
// watcher (its records go to the data folder's System\runs) and says how a record lost at a power cut is made again from
// Omega's own database.
//
//   PlateKineticsLive.exe [--bmg-root <folder>]... [--assume-running] [--data-dir <folder>] [--port <n>] [--no-window]
//   PlateKineticsLive.exe --open <file.pk>      (a double-click on an experiment file: shown in the window's page)
//   (and --service, --install-service, --uninstall-service, --start-service, --uninstall: see Common/ServiceMode.cs)
using System;
using System.Collections.Generic;
using System.Globalization;
using SAAControl.Bmg;

namespace SAAControl
{
    static class BmgProgram
    {
        static IList<Install> found = new List<Install>();
        static bool checksAdded;

        [STAThread]
        static int Main(string[] argv) => Host.Run("PlateKineticsLive", 47815, argv, new[] { "--bmg-root" },
            context =>
            {
                var installs = Installs(context.Args);
                Log.Info(Runs.StartedLogLine(installs));
                // Omega versions that are not verified are said (a problem that can be closed, and in GET /api/health);
                // the disks Omega saves to are looked at with the PC's other checks
                found = installs;
                OmegaVersions.ShowProblems(installs);
                if (!checksAdded)
                {
                    checksAdded = true;
                    Health.Checks.Add(() => OmegaVersions.Checks(found));
                }
                foreach (Install install in installs) PcChecks.WatchDrive(install.Root, install.Key, "Omega");
                // the records in System\runs, the experiment files (.pk, .xlsx) in the data folder's Experiments
                return new Watcher(installs, context.Layout.SystemDir, assumeRunning: context.Args.Has("--assume-running"), experiments: context.Experiments);
            },
            "Follows the BMG Omega plate readers' runs (it only reads Omega's own files) and keeps a record of every run; " +
            "the Plate Kinetics Live window shows them.",
            Rebuild);

        static IList<Install> Installs(Host.Args args)
        {
            var roots = args.All("--bmg-root");
            return OmegaLive.Discover(roots.Count > 0 ? roots : null);
        }

        /// <summary>A record lost at a power cut, made again from Omega's database (where Omega keeps a run once it has
        /// ended): the run of that installation that started at that second. null when Omega has no such run.</summary>
        static Dictionary<string, object> Rebuild(Host.Context context, string runId)
        {
            var history = new History(Installs(context.Args));
            foreach (object item in history.List())
            {
                if (!(item is Dictionary<string, object> run) || !(run.TryGetValue("startedAt", out object s) && s is string startedAt)) continue;
                DateTime? start = Naming.Start(startedAt);
                string install = run.TryGetValue("install", out object i) ? i as string : null;
                if (start == null || install == null || install + "-" + start.Value.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture) != runId) continue;
                ExperimentInfo info = history.Describe(run["key"] as string);
                if (info == null || info.RunId != runId) continue;
                return Record(info, install, "made again from Omega's database after the record was lost");
            }
            return null;
        }

        /// <summary>A record as the watcher writes one, from the description of a run.</summary>
        static Dictionary<string, object> Record(ExperimentInfo info, string key, string why)
        {
            var run = info.Run;
            object Field(string name) => run.TryGetValue(name, out object v) ? v : null;
            long wells = 0;
            if (Field("channels") is List<object> channels)
                foreach (object c in channels)
                    if (c is Dictionary<string, object> channel && channel.TryGetValue("wells", out object w) && w is List<object> list) wells += list.Count;
            var first = info.Channels.Count > 0 ? info.Channels[0] : new ChannelInfo();
            return new Dictionary<string, object>
            {
                ["summary"] = new Dictionary<string, object>
                {
                    ["id"] = info.RunId, ["protocol"] = info.Protocol, ["startedAt"] = info.StartedAt, ["plate"] = Field("plate"),
                    ["plannedCycles"] = info.Planned > 0 ? (object)info.Planned : null, ["cycleSeconds"] = null,
                    ["cyclesStarted"] = info.Done, ["cyclesComplete"] = info.Done, ["wells"] = wells, ["wellsInCurrentCycle"] = wells,
                    ["temperature"] = null, ["excitation"] = first.Excitation ?? "", ["emission"] = first.Emission ?? "", ["gain"] = first.Gain,
                    ["targetTemperature"] = info.TargetTemperature, ["dataRev"] = Field("dataRev") ?? 1L, ["updatedAt"] = null, ["settled"] = Field("settled"),
                },
                ["data"] = run,
                ["label"] = key + (info.Serial.Length > 0 ? " \u00b7 " + info.Serial : ""),
                ["instrument"] = new Dictionary<string, object> { ["key"] = key, ["serial"] = info.Serial, ["software"] = info.SoftwareVersion, ["model"] = info.Model },
                ["status"] = info.Status, ["endedAt"] = info.EndedAt, ["experiment"] = info.Name,
                ["events"] = new List<object> { new List<object> { PyTime.Now(), why } },
            };
        }
    }
}
