// Protocol details: the settings of a run that matter to repeat it, in readable words, made from the run's record for
// the page ("protocolDetails" of GET /api/run, /api/pk, /api/pk-file) and for the workbook's sheet "Protocol".
//
// The one shape for both programs (the page only renders it; it does not know Omega's or Archimed's field names):
//   {"sections": [{"title", "titleZh", "rows": [{"label", "labelZh", "value", "valueZh" (only when the Chinese differs)}]}],
//    "steps": (PCR only) [{"stage", "kind", "repeats", "label", "labelZh",
//                          "steps": [{"temperature", "hold", "ramp", "collect"}]}]}
// or null when nothing is known of the run's protocol (an old record, a record of another PC's run, a damaged record).
//
// Where it comes from:
//   BMG: record["settings"], every field of the row of Omega's TestAkt.DB the run was started with (Runs.TestSettings),
//        and the target temperature of record["summary"].
//   PCR: record["pcrProtocol"], what the experiment file (*.qps / *.qpt) had: tcprotocol.xml and the detectors and dyes of
//        plate_setup.xml (Archimed.ArchimedData.ReadProtocol), stored once when the file was found.
// The meanings of Omega's codes are in OmegaCodes below: the only place to edit when a meaning is confirmed. A code not
// in the tables is shown as it is ("Omega code 1", in Chinese "Omega dai-ma 1"), never guessed. The user accounts Omega keeps with
// a protocol (UserW, UserB) are never shown.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SAAControl
{
    public static class ProtocolDetails
    {
        /// <summary>The record's key of an Archimed run's protocol.</summary>
        public const string PcrKey = "pcrProtocol";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- the tables of codes (add a confirmed meaning here; everything else follows)

        /// <summary>Omega's codes with a confirmed meaning: field (or group of fields) -> code as Omega stores it -> words.
        /// A code missing here is shown as "Omega code N". Groups: "ShakeMode" holds the codes of Shake1Mode, Shake2Mode and
        /// Shake3Mode.</summary>
        public static readonly Dictionary<string, Dictionary<string, (string En, string Zh)>> OmegaCodes =
            new Dictionary<string, Dictionary<string, (string En, string Zh)>>(StringComparer.Ordinal)
            {
                ["Mode"] = new Dictionary<string, (string, string)>
                {
                    ["10"] = ("Endpoint", "\u7ec8\u70b9\u6cd5"),
                    ["20"] = ("Plate mode kinetic", "\u677f\u6a21\u5f0f\u52a8\u529b\u5b66"),
                    ["30"] = ("Well mode kinetic", "\u5b54\u6a21\u5f0f\u52a8\u529b\u5b66"),
                },
                ["ShakeMode"] = new Dictionary<string, (string, string)>
                {
                    ["11"] = ("Shake between readings", "\u8bfb\u6570\u4e4b\u95f4\u632f\u677f"),
                },
                // confirmed 2026-11-25 against Omega's own protocol window (a 384-well run: Bottom optic,
                // shaking Double orbital at 900 rpm) and the settings the program stored for that run
                ["TopBottom"] = new Dictionary<string, (string, string)>
                {
                    ["1"] = ("Bottom optic", "\u5e95\u8bfb"),
                },
                ["IdleMode"] = new Dictionary<string, (string, string)>
                {
                    ["9"] = ("Double orbital", "\u53cc\u8f68\u9053"),
                },
                ["IdleFrequency"] = new Dictionary<string, (string, string)>
                {
                    ["8"] = ("900 rpm", "900 rpm"),
                },
                // not confirmed yet (shown as codes): the reading direction, the speed
                ["ReadingDir"] = new Dictionary<string, (string, string)>(),
                ["SlowFast"] = new Dictionary<string, (string, string)>(),
            };

        /// <summary>Fields of Omega's settings that are never shown (the user accounts of the protocol).</summary>
        public static readonly HashSet<string> OmegaHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UserW", "UserB", "User" };

        /// <summary>Archimed's codes with a known meaning: field -> code -> words (others are shown as they are).</summary>
        public static readonly Dictionary<string, Dictionary<string, (string En, string Zh)>> ArchimedCodes =
            new Dictionary<string, Dictionary<string, (string En, string Zh)>>(StringComparer.Ordinal)
            {
                ["StageFlag"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CYCLING"] = ("Cycling", "\u5faa\u73af"),
                    ["HOLD"] = ("Hold", "\u4fdd\u6301"),
                    ["MELT"] = ("Melt curve", "\u7194\u89e3\u66f2\u7ebf"),
                    ["MELTING"] = ("Melt curve", "\u7194\u89e3\u66f2\u7ebf"),
                    ["MELT_CURVE"] = ("Melt curve", "\u7194\u89e3\u66f2\u7ebf"),
                },
                ["RampRateUnit"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DEGREES_PER_SECOND"] = ("\u00b0C/s", "\u00b0C/s"),
                    ["PERCENT"] = ("%", "%"),
                    ["PERCENTAGE"] = ("%", "%"),
                },
                ["RunMode"] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Standard"] = ("Standard", "\u6807\u51c6"),
                },
                // not known (shown as codes)
                ["BlockID"] = new Dictionary<string, (string, string)>(),
                ["TubeType"] = new Dictionary<string, (string, string)>(),
            };

        // ---- building

        /// <summary>The protocol details of a run from its record; null when the record has none (or cannot be read).</summary>
        public static Dictionary<string, object> Build(Dictionary<string, object> record)
        {
            if (record == null) return null;
            try
            {
                var summary = Get(record, "summary") as Dictionary<string, object>;
                if (Get(record, "settings") is Dictionary<string, object> settings && settings.Count > 0) return Omega(settings, summary);
                if (Get(record, PcrKey) is Dictionary<string, object> pcr && pcr.Count > 0) return Pcr(pcr);
            }
            catch (Exception error)
            {
                Log.Warning("the protocol details of a record could not be made (" + error.Message + ")");
            }
            return null;
        }

        /// <summary>Omega's settings of a run as stored, without the fields never shown (for the workbook's "Omega original
        /// values"); null when the record has none.</summary>
        public static List<KeyValuePair<string, object>> OmegaValues(Dictionary<string, object> record)
        {
            if (!(Get(record, "settings") is Dictionary<string, object> settings) || settings.Count == 0) return null;
            return settings.Where(entry => !OmegaHidden.Contains(entry.Key)).ToList();
        }

        // ---- BMG Omega

        static Dictionary<string, object> Omega(Dictionary<string, object> s, Dictionary<string, object> summary)
        {
            var sections = new List<object>();

            // the protocol
            var rows = new List<object>();
            AddRow(rows, "Protocol name", "\u65b9\u6848\u540d\u79f0", Text(Get(s, "tstname")));
            string plate = Text(Get(s, "mpname"));
            long? plateRows = Whole(Get(s, "YAnz")), plateCols = Whole(Get(s, "XAnz"));
            string format = plateRows > 0 && plateCols > 0 ? plateRows.Value.ToString(Inv) + " \u00d7 " + plateCols.Value.ToString(Inv) : null;
            AddRow(rows, "Plate", "\u677f\u578b", plate != null && format != null ? plate + " (" + format + ")" : plate ?? format);
            if (Get(s, "Mode") != null)
            {
                var (modeEn, modeZh) = OmegaCode("Mode", Get(s, "Mode"));
                AddRow(rows, "Mode", "\u6d4b\u91cf\u6a21\u5f0f", modeEn, modeZh);
            }
            string saved = string.Join(" ", new[] { Text(Get(s, "EditDate")), Text(Get(s, "EditTime")) }.Where(t => t != null));
            AddRow(rows, "Protocol last saved", "\u65b9\u6848\u6700\u540e\u4fdd\u5b58", saved.Length > 0 ? saved : null);
            AddRow(rows, "Omega version", "Omega \u7248\u672c", Text(Get(s, "Version")));
            AddSection(sections, "Protocol", "\u65b9\u6848", rows);

            // the measurement
            int channels = ChannelCount(s);
            rows = new List<object>();
            long? cycles = Whole(Get(s, "Kinetics"));
            double? cycleTime = Number(Get(s, "Kintime"));
            if (cycles > 0)
            {
                if (cycleTime > 0)
                    AddRow(rows, "Cycles \u00d7 cycle time", "\u5faa\u73af\u6b21\u6570 \u00d7 \u6bcf\u8f6e\u65f6\u95f4", cycles.Value.ToString(Inv) + " \u00d7 " + Plain(cycleTime.Value) + " s",
                           cycles.Value.ToString(Inv) + " \u00d7 " + Plain(cycleTime.Value) + " \u79d2");
                else AddRow(rows, "Cycles", "\u5faa\u73af\u6b21\u6570", cycles.Value.ToString(Inv));
            }
            var flashes = new List<string>();
            for (int n = 1; n <= Math.Max(channels, 1); n++)
            {
                object value = n == 1 ? Get(s, "Flashes") ?? Get(s, "Flashes1") : Get(s, "Flashes" + n.ToString(Inv));
                if (value != null) flashes.Add(Value(value));
            }
            if (flashes.Count > 0)
                AddRow(rows, "Flashes per well", "\u6bcf\u5b54\u95ea\u5149\u6b21\u6570", flashes.Distinct().Count() == 1 ? flashes[0] : string.Join(" / ", flashes));
            AddCodeRow(rows, s, "TopBottom", "Read from (top/bottom)", "\u8bfb\u677f\u4f4d\u7f6e");
            AddCodeRow(rows, s, "ReadingDir", "Reading direction", "\u8bfb\u677f\u65b9\u5411");
            // Posdelay: Omega's "Settling time" (0.0 ... 1.0 s) in tenths of a second (1 = 0.1 s, confirmed 2026-11-25)
            double? settling = Number(Get(s, "Posdelay"));
            if (settling.HasValue && settling.Value >= 0)
                AddRow(rows, "Settling time", "\u7a33\u5b9a\u65f6\u95f4", Plain(settling.Value / 10) + " s", Plain(settling.Value / 10) + " \u79d2");
            object flying = Get(s, "FlyMeas");
            if (flying is bool || Whole(flying).HasValue)
            {
                bool on = flying is bool f ? f : Whole(flying) != 0;
                AddRow(rows, "Flying mode", "\u98de\u884c\u6a21\u5f0f", on ? "On" : "Off", on ? "\u5f00" : "\u5173");
            }
            AddCodeRow(rows, s, "SlowFast", "Speed", "\u901f\u5ea6");
            AddSection(sections, "Measurement", "\u6d4b\u91cf", rows);

            // the channels: excitation / emission, gain
            rows = new List<object>();
            for (int n = 1; n <= channels; n++)
            {
                string ex = Text(Get(s, "ExFilt" + n.ToString(Inv))), em = Text(Get(s, "EmFilt" + n.ToString(Inv)));
                object gain = Get(s, "Gain" + n.ToString(Inv));
                string filters = ex != null && em != null ? ex + " / " + em : ex != null ? "Ex " + ex : em != null ? "Em " + em : null;
                string gainText = gain != null ? Value(gain) : null;
                if (filters == null && gainText == null) continue;
                string en = string.Join(" \u00b7 ", new[] { filters, gainText != null ? "gain " + gainText : null }.Where(t => t != null));
                string zh = string.Join(" \u00b7 ", new[] { filters, gainText != null ? "\u589e\u76ca " + gainText : null }.Where(t => t != null));
                AddRow(rows, "Channel " + n.ToString(Inv) + " (Ex / Em)", "\u901a\u9053 " + n.ToString(Inv) + "\uff08\u6fc0\u53d1 / \u53d1\u5c04\uff09", en, zh);
            }
            AddSection(sections, "Channels", "\u901a\u9053", rows);

            // shaking: only when a shake mode is set
            rows = new List<object>();
            var modes = new List<(int K, object Code)>();
            for (int k = 1; k <= 3; k++)
            {
                object code = Get(s, "Shake" + k.ToString(Inv) + "Mode");
                if (IsSet(code)) modes.Add((k, code));
            }
            if (modes.Count > 0)
            {
                foreach (var (k, code) in modes)
                {
                    var (en, zh) = OmegaCode("ShakeMode", code);
                    string suffix = modes.Count > 1 || k > 1 ? " (" + k.ToString(Inv) + ")" : "";
                    AddRow(rows, "When to shake" + suffix, "\u632f\u677f\u65f6\u673a" + suffix, en, zh);
                }
                AddCodeRow(rows, s, "IdleMode", "Shaking mode", "\u632f\u677f\u65b9\u5f0f");
                AddCodeRow(rows, s, "IdleFrequency", "Shaking frequency", "\u632f\u677f\u9891\u7387");
                double? on = Number(Get(s, "IdleOnTime")), off = Number(Get(s, "IdleOffTime"));
                if (on.HasValue) AddRow(rows, "Shaking time", "\u632f\u677f\u65f6\u95f4", Plain(on.Value) + " s", Plain(on.Value) + " \u79d2");
                if (off.HasValue) AddRow(rows, "Pause", "\u6682\u505c\u65f6\u95f4", Plain(off.Value) + " s", Plain(off.Value) + " \u79d2");
                AddSection(sections, "Shaking", "\u632f\u677f", rows);
            }

            // the temperature
            rows = new List<object>();
            double? target = Number(Get(summary, "targetTemperature"));
            if (target.HasValue) AddRow(rows, "Target temperature", "\u76ee\u6807\u6e29\u5ea6", OneDecimal(target.Value) + " \u00b0C");
            AddSection(sections, "Temperature", "\u6e29\u5ea6", rows);

            return sections.Count > 0 ? new Dictionary<string, object> { ["sections"] = sections } : null;
        }

        /// <summary>The channels Omega measured: AnzChroma when the row has it as a number (0: none shown), else the
        /// channels 1, 2, ... up to the first without a filter (Omega's row has ExFilt1..8 and EmFilt1..8, those of unused
        /// channels empty: an empty filter is no channel).</summary>
        static int ChannelCount(Dictionary<string, object> s)
        {
            long? count = Whole(Get(s, "AnzChroma"));
            if (count.HasValue) return (int)Math.Max(0, Math.Min(count.Value, 8));
            int n = 0;
            while (n < 8 && (Text(Get(s, "ExFilt" + (n + 1).ToString(Inv))) != null || Text(Get(s, "EmFilt" + (n + 1).ToString(Inv))) != null)) n++;
            return n;
        }

        /// <summary>The words of an Omega code: the table's when the meaning is confirmed, else "Omega code N".</summary>
        public static (string En, string Zh) OmegaCode(string table, object code)
        {
            string key = Value(code);
            if (key.Length == 0) key = "?";
            if (OmegaCodes.TryGetValue(table, out var known) && known.TryGetValue(key, out var words)) return words;
            return ("Omega code " + key, "Omega \u4ee3\u7801 " + key);
        }

        static void AddCodeRow(List<object> rows, Dictionary<string, object> s, string field, string label, string labelZh)
        {
            object code = Get(s, field);
            if (code == null || Value(code).Length == 0) return;
            var (en, zh) = OmegaCode(field, code);
            AddRow(rows, label, labelZh, en, zh);
        }

        static bool IsSet(object code) =>
            code != null && !(code is bool b && !b) && !(Number(code) is double d && d == 0) && !(code is string t && t.Trim().Length == 0);

        // ---- PCR Archimed

        static Dictionary<string, object> Pcr(Dictionary<string, object> p)
        {
            var sections = new List<object>();
            var rows = new List<object>();
            double? volume = Number(Get(p, "sampleVolume"));
            if (volume.HasValue) AddRow(rows, "Sample volume", "\u53cd\u5e94\u4f53\u79ef", OneDecimal(volume.Value) + " \u00b5L");
            double? cover = Number(Get(p, "coverTemperature"));
            if (cover.HasValue) AddRow(rows, "Cover temperature", "\u70ed\u76d6\u6e29\u5ea6", OneDecimal(cover.Value) + " \u00b0C");
            string mode = Text(Get(p, "runMode"));
            if (mode != null)
            {
                var (en, zh) = ArchimedCode("RunMode", mode, false);
                AddRow(rows, "Run mode", "\u8fd0\u884c\u6a21\u5f0f", en, zh);
            }
            object delay = Get(p, "delay");
            if (delay != null) AddRow(rows, "Delay", "\u5ef6\u8fdf", Value(delay) + " (unit not confirmed)", Value(delay) + "\uff08\u5355\u4f4d\u5f85\u786e\u8ba4\uff09");
            if (Text(Get(p, "blockId")) is string block)
            {
                var (en, zh) = ArchimedCode("BlockID", block, true);
                AddRow(rows, "Block", "\u6a21\u5757", en, zh);
            }
            if (Text(Get(p, "tubeType")) is string tube)
            {
                var (en, zh) = ArchimedCode("TubeType", tube, true);
                AddRow(rows, "Tube type", "\u7ba1\u578b", en, zh);
            }
            string detectors = Names(Get(p, "detectors")), dyes = Names(Get(p, "dyes"));
            AddRow(rows, "Detectors", "\u68c0\u6d4b\u9879\u76ee", detectors);
            AddRow(rows, "Dyes", "\u67d3\u6599", dyes);
            AddSection(sections, "Reaction", "\u53cd\u5e94", rows);

            // the temperature program: one line per stage, and the same as structured steps
            var steps = new List<object>();
            var lines = new List<(string Label, string LabelZh, List<(string Temperature, string Hold, string Ramp, bool Collect)> Steps)>();
            int number = 0;
            foreach (object item in Get(p, "stages") as List<object> ?? new List<object>())
            {
                if (!(item is Dictionary<string, object> stage)) continue;
                number++;
                string flag = Text(Get(stage, "flag")) ?? "";
                long? repeats = Whole(Get(stage, "repeats"));
                var (kindEn, kindZh) = ArchimedCode("StageFlag", flag, false);
                bool cycling = string.Equals(flag, "CYCLING", StringComparison.OrdinalIgnoreCase);
                string label = "Stage " + number.ToString(Inv), labelZh = "\u9636\u6bb5 " + number.ToString(Inv);
                if (kindEn.Length > 0)
                {
                    label += " \u00b7 " + kindEn;
                    labelZh += " \u00b7 " + kindZh;
                }
                if (repeats.HasValue && (cycling || repeats.Value > 1))
                {
                    label += " \u00d7 " + repeats.Value.ToString(Inv);
                    labelZh += cycling ? " " + repeats.Value.ToString(Inv) + " \u6b21" : " \u00d7 " + repeats.Value.ToString(Inv);
                }
                var shownSteps = new List<(string, string, string, bool)>();
                var stepItems = new List<object>();
                foreach (object s in Get(stage, "steps") as List<object> ?? new List<object>())
                {
                    if (!(s is Dictionary<string, object> step)) continue;
                    string temperature = Temperature(Get(step, "temperatures"));
                    string hold = Hold(Get(step, "holdSeconds"));
                    string ramp = Ramp(Get(step, "rampRate"), Get(step, "rampRateUnit"));
                    bool collect = Get(step, "collect") is bool c && c;
                    shownSteps.Add((temperature, hold, ramp, collect));
                    stepItems.Add(new Dictionary<string, object> { ["temperature"] = temperature, ["hold"] = hold, ["ramp"] = ramp, ["collect"] = collect });
                }
                lines.Add((label, labelZh, shownSteps));
                steps.Add(new Dictionary<string, object>
                {
                    ["stage"] = (long)number, ["kind"] = flag, ["repeats"] = repeats.HasValue ? (object)repeats.Value : null,
                    ["label"] = label, ["labelZh"] = labelZh, ["steps"] = stepItems,
                });
            }
            rows = new List<object>();
            var ramps = lines.SelectMany(line => line.Steps).Select(step => step.Ramp).Distinct().ToList();
            bool oneRamp = ramps.Count == 1;
            if (oneRamp && ramps[0] != null) AddRow(rows, "Ramp rate", "\u5347\u964d\u6e29\u901f\u7387", ramps[0]);
            foreach (var line in lines)
            {
                string Step((string Temperature, string Hold, string Ramp, bool Collect) step, bool zh)
                {
                    string text = string.Join(" ", new[] { step.Temperature, step.Hold }.Where(t => t != null));
                    var notes = new List<string>();
                    if (!oneRamp && step.Ramp != null) notes.Add(step.Ramp);
                    if (step.Collect) notes.Add(zh ? "\u91c7\u5149" : "data collected");
                    if (notes.Count > 0) text += (text.Length > 0 ? " " : "") + "(" + string.Join(", ", notes) + ")";
                    return text.Length > 0 ? text : "?";
                }
                string en = line.Steps.Count > 0 ? string.Join(" \u2192 ", line.Steps.Select(step => Step(step, false))) : "-";
                string zh = line.Steps.Count > 0 ? string.Join(" \u2192 ", line.Steps.Select(step => Step(step, true))) : "-";
                AddRow(rows, line.Label, line.LabelZh, en, zh);
            }
            AddSection(sections, "Temperature program", "\u6e29\u5ea6\u7a0b\u5e8f", rows);

            if (sections.Count == 0) return null;
            var details = new Dictionary<string, object> { ["sections"] = sections };
            if (steps.Count > 0) details["steps"] = steps;
            return details;
        }

        /// <summary>The words of an Archimed code: the table's, else the code itself (asCode: "Archimed code N").</summary>
        static (string En, string Zh) ArchimedCode(string table, string code, bool asCode)
        {
            if (ArchimedCodes.TryGetValue(table, out var known) && known.TryGetValue(code, out var words)) return words;
            return asCode ? ("Archimed code " + code, "Archimed \u4ee3\u7801 " + code) : (code, code);
        }

        /// <summary>"80.0 \u00b0C" (the zones all alike) or "79.5\u201380.5 \u00b0C" (they differ); null when there is none.</summary>
        static string Temperature(object value)
        {
            var numbers = (value as List<object> ?? new List<object> { value }).Select(Number).Where(n => n.HasValue).Select(n => n.Value).ToList();
            if (numbers.Count == 0) return null;
            double low = numbers.Min(), high = numbers.Max();
            return low == high ? OneDecimal(low) + " \u00b0C" : OneDecimal(low) + "\u2013" + OneDecimal(high) + " \u00b0C";
        }

        /// <summary>A hold time in seconds as m:ss ("10:00"); a negative one (held until stopped) as "\u221e".</summary>
        static string Hold(object value)
        {
            double? seconds = Number(value);
            if (!seconds.HasValue) return value is string text && text.Trim().Length > 0 ? text.Trim() : null;
            if (seconds.Value < 0) return "\u221e";
            long whole = (long)Math.Round(seconds.Value);
            return (whole / 60).ToString(Inv) + ":" + (whole % 60).ToString("00", Inv);
        }

        static string Ramp(object rate, object unit)
        {
            double? number = Number(rate);
            if (!number.HasValue) return null;
            string u = Text(unit) ?? "";
            var (en, _) = u.Length > 0 ? ArchimedCode("RampRateUnit", u, false) : ("", "");
            return (OneDecimal(number.Value) + " " + en).Trim();
        }

        static string Names(object value)
        {
            if (!(value is List<object> list)) return Text(value);
            var names = list.Select(Text).Where(n => n != null).Distinct().ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        // ---- the shape

        static void AddRow(List<object> rows, string label, string labelZh, string value, string valueZh = null)
        {
            if (value == null || value.Trim().Length == 0) return;
            var row = new Dictionary<string, object> { ["label"] = label, ["labelZh"] = labelZh, ["value"] = value };
            if (valueZh != null && valueZh != value) row["valueZh"] = valueZh;
            rows.Add(row);
        }

        static void AddSection(List<object> sections, string title, string titleZh, List<object> rows)
        {
            if (rows.Count == 0) return;
            sections.Add(new Dictionary<string, object> { ["title"] = title, ["titleZh"] = titleZh, ["rows"] = rows });
        }

        // ---- values

        static object Get(Dictionary<string, object> d, string key) => d != null && key != null && d.TryGetValue(key, out object v) ? v : null;

        /// <summary>Text of a value, trimmed; null for none or empty.</summary>
        static string Text(object value)
        {
            if (value == null) return null;
            string text = value is string s ? s : Value(value);
            text = text.Trim();
            return text.Length > 0 ? text : null;
        }

        /// <summary>A value as Omega stores it: numbers as written, true/false as True/False.</summary>
        public static string Value(object value)
        {
            switch (value)
            {
                case null: return "";
                case string s: return s.Trim();
                case bool b: return b ? "True" : "False";
                case double d: return Json.FormatDouble(d);
                case float f: return Json.FormatDouble(f);
                case IConvertible _: return Convert.ToString(value, Inv);
                default: return "";              // (a list or an object: not a value of a setting)
            }
        }

        static double? Number(object value)
        {
            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? (double?)null : f;
                case decimal m: return (double)m;
                case string s:
                    return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed)
                        ? parsed : (double?)null;
                default: return null;
            }
        }

        static long? Whole(object value)
        {
            double? number = Number(value);
            return number.HasValue && number.Value == Math.Floor(number.Value) && Math.Abs(number.Value) < 1e15 ? (long)number.Value : (long?)null;
        }

        /// <summary>A number with at least one decimal: 80 -> "80.0", 37.25 -> "37.25".</summary>
        static string OneDecimal(double value) => value.ToString("0.0###", Inv);

        /// <summary>A number as short as it goes: 900 -> "900", 0.5 -> "0.5".</summary>
        static string Plain(double value) => value.ToString("0.###", Inv);
    }
}
