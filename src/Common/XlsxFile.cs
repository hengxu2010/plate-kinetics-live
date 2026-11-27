// The experiment's Excel file (.xlsx), written at the end of a run and after an analysis is saved: plain SpreadsheetML in
// a zip, no library.
//   sheet "Full record": a two-column block of everything known about the run (experiment, its id, instrument, software,
//                        protocol, start, end, time zone, status - with the Chinese words in column C -, why it was
//                        interrupted, cycles, target temperature, filters and gain per channel, operator, run id, the raw
//                        data's checksum, the saved analysis), the samples (well, content, and the saved names and groups:
//                        with two grouping layers or more a group column per layer, its name in the heading),
//                        then per channel the table Cycle, Time (s), Temperature (C), one column per well (with the saved
//                        sample names in the row under the well ids), for an Archimed run everything its log had (the
//                        calibration, every optical channel and well raw, the dark readings, the block temperatures), the
//                        saved analysis' results, then the events.
//   sheet "Basic data":  only the tables (Cycle, Time (s), Temperature (C), one column per well); one block per channel,
//                        with a title row when there are several.
//   sheet "Protocol":    the protocol details (ProtocolDetails.cs): per section its rows (English label, Chinese label,
//                        value, the value in Chinese when it differs), for an Archimed run the temperature program step by
//                        step, for a BMG run "Omega original values": every field of Omega's settings as stored (but the
//                        user accounts); "No record" when the run has none.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace SAAControl
{
    public static class XlsxFile
    {
        public const string Extension = ".xlsx";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        static readonly string[] Parts =
        {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml",
            "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/worksheets/sheet3.xml",
        };

        /// <summary>The bytes of the workbook of a run; analysis: the saved analysis (analysis.json), null for none.</summary>
        public static byte[] Build(ExperimentInfo info, string program, Dictionary<string, object> analysis = null)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Text(zip, Parts[0],
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
                        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet3.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "</Types>");
                    Text(zip, Parts[1],
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"" + Pkg + "\">" +
                        "<Relationship Id=\"rId1\" Type=\"" + Rel + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Text(zip, Parts[2],
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"" + Main + "\" xmlns:r=\"" + Rel + "\">" +
                        "<sheets><sheet name=\"Full record\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Basic data\" sheetId=\"2\" r:id=\"rId2\"/>" +
                        "<sheet name=\"Protocol\" sheetId=\"3\" r:id=\"rId4\"/></sheets></workbook>");
                    Text(zip, Parts[3],
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"" + Pkg + "\">" +
                        "<Relationship Id=\"rId1\" Type=\"" + Rel + "/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                        "<Relationship Id=\"rId2\" Type=\"" + Rel + "/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                        "<Relationship Id=\"rId3\" Type=\"" + Rel + "/styles\" Target=\"styles.xml\"/>" +
                        "<Relationship Id=\"rId4\" Type=\"" + Rel + "/worksheet\" Target=\"worksheets/sheet3.xml\"/></Relationships>");
                    Text(zip, Parts[4],
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<styleSheet xmlns=\"" + Main + "\">" +
                        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
                        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
                        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                    var channels = Channels(info.Run);
                    using (var sheet = new Sheet(zip, Parts[5], 26)) FullRecord(sheet, info, program, channels, analysis);
                    using (var sheet = new Sheet(zip, Parts[6], 0)) BasicData(sheet, info.Run, channels);
                    using (var sheet = new Sheet(zip, Parts[7], 30)) Protocol(sheet, info);
                }
                return memory.ToArray();
            }
        }

        static void Text(ZipArchive zip, string name, string xml)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            byte[] bytes = Utf8.GetBytes(xml);
            using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Throws when the bytes are not a complete workbook of ours: every part there and well-formed XML.</summary>
        public static void Validate(byte[] bytes)
        {
            using (var memory = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                foreach (string part in Parts)
                {
                    ZipArchiveEntry entry = zip.GetEntry(part) ?? throw new InvalidDataException("the workbook has no " + part);
                    using (Stream stream = entry.Open())
                    using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        while (reader.Read()) { }
                }
            }
        }

        // ---- the run's tables

        sealed class Channel
        {
            public string Name;
            public List<string> Wells = new List<string>();
            public List<string> Contents = new List<string>();
            public List<List<object>> Values = new List<List<object>>();
        }

        static List<Channel> Channels(Dictionary<string, object> run)
        {
            var result = new List<Channel>();
            if (run == null || !(Get(run, "channels") is List<object> channels)) return result;
            foreach (object item in channels)
            {
                if (!(item is Dictionary<string, object> channel)) continue;
                var c = new Channel { Name = Str(Get(channel, "channel")) };
                if (Get(channel, "wells") is List<object> wells)
                    foreach (object w in wells)
                    {
                        if (!(w is Dictionary<string, object> well)) continue;
                        c.Wells.Add(Str(Get(well, "id")));
                        c.Contents.Add(Str(Get(well, "content")));
                        c.Values.Add(Get(well, "y") as List<object> ?? new List<object>());
                    }
                result.Add(c);
            }
            return result;
        }

        static void Table(Sheet sheet, Dictionary<string, object> run, Channel channel, Dictionary<string, string> names = null)
        {
            var seconds = Get(run, "seconds") as List<object> ?? new List<object>();
            var temperature = Get(run, "temperature") as List<object> ?? new List<object>();
            int rows = seconds.Count;
            foreach (var values in channel.Values) rows = Math.Max(rows, values.Count);
            var header = new List<object> { "Cycle", "Time (s)", "Temperature (C)" };
            header.AddRange(channel.Wells);
            sheet.Row(header, true);
            if (names != null && names.Count > 0)
            {
                // the saved sample names under the well ids
                var sample = new List<object> { "Sample", null, null };
                sample.AddRange(channel.Wells.Select(w => names.TryGetValue(w, out string n) && n.Length > 0 ? (object)n : null));
                sheet.Row(sample, false, true);
            }
            for (int i = 0; i < rows; i++)
            {
                var cells = new List<object>(channel.Wells.Count + 3)
                {
                    (long)(i + 1), i < seconds.Count ? seconds[i] : null, i < temperature.Count ? temperature[i] : null,
                };
                foreach (var values in channel.Values) cells.Add(i < values.Count ? values[i] : null);
                sheet.Row(cells);
            }
        }

        static string Title(Channel channel) => "Channel " + (string.IsNullOrEmpty(channel.Name) ? "1" : channel.Name);

        static void FullRecord(Sheet sheet, ExperimentInfo info, string program, List<Channel> channels, Dictionary<string, object> analysis)
        {
            void Item(string label, object value, object zh = null) =>
                sheet.Row(zh == null ? new List<object> { label, value } : new List<object> { label, value, zh }, false, true);
            Item("Experiment", info.Name);
            Item("Experiment ID", info.ExperimentId);
            Item("Instrument model", info.Model);
            Item("Serial number", info.Serial);
            Item("Software version", info.Software);
            Item("Protocol", info.Protocol);
            Item("Start", Shown(info.StartedAt));
            Item("End", Shown(info.EndedAt));
            Item("Time zone", TimeZoneText(info.StartedAt));
            var (statusEn, statusZh) = ExperimentInfo.StatusText(info.Status, info.Done, info.Planned, info.Interruption, info.StartedAt);
            Item("Status", statusEn, statusZh);
            if (info.Interruption != null)
            {
                var (whyEn, whyZh) = Interruptions.Text(info.Interruption, info.StartedAt);
                Item("Interruption", whyEn, whyZh);
            }
            Item("Cycles done", info.Done);
            Item("Cycles planned", info.Planned > 0 ? (object)info.Planned : null);
            Item("Target temperature (C)", info.TargetTemperature);
            foreach (ChannelInfo c in info.Channels ?? new List<ChannelInfo>())
            {
                string name = "Channel " + (string.IsNullOrEmpty(c.Name) ? "1" : c.Name);
                Item(name + " excitation", c.Excitation);
                Item(name + " emission", c.Emission);
                Item(name + " gain", c.Gain);
            }
            Item("Operator", string.IsNullOrEmpty(info.Operator) ? null : info.Operator);
            Item("Run ID", info.RunId);
            Item("Raw data checksum (SHA-256 of run.json)", info.RunSha256);
            Item("Experiment file", info.FileName);
            if (analysis != null)
            {
                string by = Text(Get(analysis, "savedBy"));
                Item("Analysis saved", (Shown(Text(Get(analysis, "savedAt"))) ?? "") + (by.Length > 0 ? " (" + by + ")" : ""));
                string notes = Text(Get(analysis, "notes"));
                if (notes.Length > 0) Item("Notes", notes);
            }
            Item("Recorded by", program);
            Item("Note", "This workbook is a copy made from the experiment file (.pk); changes made here are not read back.");

            // the samples: the content of the wells (of the first channel that has the well), and the saved names and groups
            // (with two grouping layers or more a group column per layer, headed with its name, and the Chinese headings
            // in a row under them)
            var names = Samples(analysis);
            var layers = GroupColumns(analysis);
            var samples = new List<(string Well, string Content)>();
            var seen = new HashSet<string>();
            foreach (Channel channel in channels)
                for (int i = 0; i < channel.Wells.Count; i++)
                    if (seen.Add(channel.Wells[i])) samples.Add((channel.Wells[i], channel.Contents[i]));
            bool named = names.Count > 0 || layers.Any(l => l.Groups.Count > 0);
            sheet.Row(new List<object>());
            sheet.Row(new List<object> { "Sample names" }, true);
            var head = named ? new List<object> { "Well", "Sample", "Name" } : new List<object> { "Well", "Sample" };
            if (named) head.AddRange(layers.Select(l => (object)l.Heading));
            sheet.Row(head, true);
            if (named && layers.Count > 1)
            {
                var zh = new List<object> { "\u5b54\u4f4d", "\u6837\u54c1", "\u540d\u79f0" };          // well, sample, name
                zh.AddRange(layers.Select(l => (object)l.HeadingZh));
                sheet.Row(zh, true);
            }
            foreach (var (well, content) in samples)
            {
                if (!named) { sheet.Row(new List<object> { well, content }); continue; }
                var row = new List<object> { well, content, names.TryGetValue(well, out string n) ? n : null };
                row.AddRange(layers.Select(l => l.Groups.TryGetValue(well, out string g) ? (object)g : null));
                sheet.Row(row);
            }

            foreach (Channel channel in channels)
            {
                sheet.Row(new List<object>());
                sheet.Row(new List<object> { Title(channel) }, true);
                Table(sheet, info.Run, channel, names);
            }

            if (Get(info.Run, "raw") is Dictionary<string, object> raw) Raw(sheet, raw);

            if (Get(analysis, "results") is List<object> results && results.Count > 0) Results(sheet, results, Get(analysis, "resultColumns") as List<object>);

            sheet.Row(new List<object>());
            sheet.Row(new List<object> { "Events" }, true);
            sheet.Row(new List<object> { "Time", "Event" }, true);
            foreach (object item in info.Events ?? new List<object>())
            {
                if (item is List<object> parts && parts.Count >= 2) sheet.Row(new List<object> { Shown(Str(parts[0])), Str(parts[1]) });
                else if (item != null) sheet.Row(new List<object> { null, Str(item) });
            }
        }

        /// <summary>Everything the instrument's log had of the run (the run's "raw" block, Archimed): the calibration it was
        /// computed with, then per optical channel the raw values of every well with the dark reading of each collection,
        /// then the block temperatures at each read.</summary>
        static void Raw(Sheet sheet, Dictionary<string, object> raw)
        {
            List<object> L(string key) => Get(raw, key) as List<object> ?? new List<object>();
            object At(List<object> list, int i, int j) => i < list.Count && list[i] is List<object> inner && j < inner.Count ? inner[j] : null;
            var wells = L("wells");
            var collected = L("collectedAt");
            var dark = L("dark");
            var values = L("values");
            var temperatures = L("blockTemperatures");
            var channels = L("channels");
            var b = L("bValues");
            sheet.Row(new List<object>());
            sheet.Row(new List<object> { "Raw data" + (Get(raw, "source") is string source ? " (" + source + ")" : "") }, true);
            sheet.Row(new List<object> { "Dark correction", Get(raw, "darkCorrection") }, false, true);
            for (int ch = 0; ch < channels.Count; ch++)
                sheet.Row(new List<object> { "B value, optical channel " + Str(channels[ch]), ch < b.Count ? b[ch] : null }, false, true);
            sheet.Row(new List<object> { "Formula", Get(raw, "formula") }, false, true);
            for (int ch = 0; ch < channels.Count; ch++)
            {
                sheet.Row(new List<object>());
                sheet.Row(new List<object> { "Optical channel " + Str(channels[ch]) + " (raw)" }, true);
                var header = new List<object> { "Cycle", "Collected at", "Dark" };
                header.AddRange(wells);
                sheet.Row(header, true);
                for (int i = 0; i < values.Count; i++)
                {
                    var cells = new List<object> { (long)(i + 1), i < collected.Count ? Str(collected[i]).Replace('T', ' ') : null, At(dark, i, ch) };
                    var line = At(values, i, ch) as List<object> ?? new List<object>();
                    for (int w = 0; w < wells.Count; w++) cells.Add(w < line.Count ? line[w] : null);
                    sheet.Row(cells);
                }
            }
            int reads = temperatures.Select(t => t is List<object> list ? list.Count : 0).DefaultIfEmpty(0).Max();
            if (reads == 0) return;
            sheet.Row(new List<object>());
            sheet.Row(new List<object> { "Block temperatures (C)" }, true);
            var head = new List<object> { "Cycle", "Collected at" };
            for (int r = 0; r < reads; r++)
                for (int k = 1; k <= 3; k++) head.Add("Read " + (r + 1) + ", block " + k);
            sheet.Row(head, true);
            for (int i = 0; i < temperatures.Count; i++)
            {
                var cells = new List<object> { (long)(i + 1), i < collected.Count ? Str(collected[i]).Replace('T', ' ') : null };
                for (int r = 0; r < reads; r++)
                {
                    var read = At(temperatures, i, r) as List<object> ?? new List<object>();
                    for (int k = 0; k < 3; k++) cells.Add(k < read.Count ? read[k] : null);
                }
                sheet.Row(cells);
            }
        }

        /// <summary>The results the page computed, as it sent them: one row per well; the columns Well, Name, Group, then
        /// every other item in the order they first appear. A column is headed as the page names it in "resultColumns"
        /// ({key, label, labelZh, unit}: "Lag time (h)"), with a row of the Chinese names under the header; a column the
        /// page does not name, by its key. With two grouping layers or more the page sends the group of every layer
        /// ("group1", "group2", ... named "Group (layer)") instead of "group": no Group column then.</summary>
        static void Results(Sheet sheet, List<object> results, List<object> columns)
        {
            bool layered = results.OfType<Dictionary<string, object>>().Any(r => r.Keys.Any(LayerGroupKey));
            var keys = layered ? new List<string> { "well", "name" } : new List<string> { "well", "name", "group" };
            foreach (object item in results)
                if (item is Dictionary<string, object> row)
                    foreach (string key in row.Keys)
                        if (!keys.Contains(key)) keys.Add(key);
            // the page's own names (key -> English, Chinese, unit), and those of the columns every result has
            var named = new Dictionary<string, (string En, string Zh, string Unit)>(StringComparer.Ordinal)
            {
                ["well"] = ("Well", "\u5b54\u4f4d", ""), ["name"] = ("Name", "\u540d\u79f0", ""), ["group"] = ("Group", "\u5206\u7ec4", ""),
                ["call"] = ("Call", "\u5224\u5b9a", ""), ["observedUntil"] = ("Observed until", "\u89c2\u5bdf\u81f3", "h"),
            };
            bool chinese = false;
            foreach (object item in columns ?? new List<object>())
            {
                if (!(item is Dictionary<string, object> column) || !(Get(column, "key") is string key) || key.Length == 0) continue;
                string en = Text(Get(column, "label")), zh = Text(Get(column, "labelZh")), unit = Text(Get(column, "unit"));
                if (en.Length == 0) continue;
                named[key] = (en, zh.Length > 0 ? zh : en, unit);
                chinese |= zh.Length > 0 && zh != en;
            }
            string Heading(string key, bool zh) =>
                !named.TryGetValue(key, out var n) ? key : (zh ? n.Zh : n.En) + (n.Unit.Length > 0 ? " (" + n.Unit + ")" : "");
            sheet.Row(new List<object>());
            sheet.Row(new List<object> { "Results" }, true);
            sheet.Row(keys.Select(k => (object)Heading(k, false)).ToList(), true);
            if (chinese) sheet.Row(keys.Select(k => (object)Heading(k, true)).ToList(), true);
            foreach (object item in results)
            {
                if (!(item is Dictionary<string, object> row)) continue;
                sheet.Row(keys.Select(k => Cell(Get(row, k))).ToList());
            }
        }

        /// <summary>A value as a cell: numbers, text and true/false as they are, anything else as its JSON.</summary>
        static object Cell(object value)
        {
            switch (value)
            {
                case null: return null;
                case string _: case bool _: case long _: case double _: return value;
                default: return Json.Serialize(value, ensureAscii: false);
            }
        }

        /// <summary>The saved sample names: well -> name.</summary>
        public static Dictionary<string, string> Samples(Dictionary<string, object> analysis)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Get(analysis, "samples") is Dictionary<string, object> samples)
                foreach (var entry in samples)
                    if (entry.Value is string name && name.Length > 0) names[entry.Key] = name;
            return names;
        }

        /// <summary>A key of the results that holds the group of a well in one grouping layer: "group1", "group2", ....</summary>
        static bool LayerGroupKey(string key) =>
            key.Length > 5 && key.StartsWith("group", StringComparison.Ordinal) && key.Substring(5).All(c => c >= '0' && c <= '9');

        /// <summary>The saved groups as columns of the sample names: with two grouping layers or more (the page's
        /// "page": {"groupingSets": [{"name", "groups"}]}) one per layer in their order, headed "Group (layer)" and
        /// its Chinese, the same whichever layer was the active one; else the one of "groups", headed
        /// "Group".</summary>
        public static List<(string Heading, string HeadingZh, Dictionary<string, string> Groups)> GroupColumns(Dictionary<string, object> analysis)
        {
            var columns = new List<(string Heading, string HeadingZh, Dictionary<string, string> Groups)>();
            if (Get(analysis, "page") is Dictionary<string, object> page && Get(page, "groupingSets") is List<object> sets)
            {
                var layers = sets.OfType<Dictionary<string, object>>().ToList();
                if (layers.Count > 1)
                {
                    foreach (var layer in layers)
                    {
                        string name = Text(Get(layer, "name"));
                        columns.Add(("Group (" + name + ")", "\u5206\u7ec4\uff08" + name + "\uff09", Groups(layer)));
                    }
                    return columns;
                }
            }
            columns.Add(("Group", "\u5206\u7ec4", Groups(analysis)));
            return columns;
        }

        /// <summary>The saved groups: well -> group name (a well in two groups: the first).</summary>
        public static Dictionary<string, string> Groups(Dictionary<string, object> analysis)
        {
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Get(analysis, "groups") is List<object> list)
                foreach (object item in list)
                {
                    if (!(item is Dictionary<string, object> group) || !(Get(group, "wells") is List<object> wells)) continue;
                    string name = Text(Get(group, "name"));
                    foreach (object well in wells)
                        if (well is string w && !groups.ContainsKey(w)) groups[w] = name;
                }
            return groups;
        }

        static string Text(object value) => value as string ?? (value == null ? "" : Str(value));

        /// <summary>"China Standard Time (UTC+08:00)": the PC's time zone, with the offset it had at the start.</summary>
        static string TimeZoneText(string startedAt)
        {
            string zone = Clock.TimeZoneId;
            if (zone == null) return null;
            DateTime? start = Clock.Local(startedAt);
            if (!start.HasValue) return zone;
            return zone + " (UTC" + Clock.Offset(TimeZoneInfo.Local.GetUtcOffset(start.Value)) + ")";
        }

        /// <summary>The sheet "Protocol": the protocol details as the page shows them (English label, Chinese label, value,
        /// the value in Chinese when it differs), the temperature program step by step (Archimed), and every field of
        /// Omega's settings as stored but the user accounts (BMG).</summary>
        static void Protocol(Sheet sheet, ExperimentInfo info)
        {
            sheet.Row(new List<object> { "Protocol details", "\u65b9\u6848\u8be6\u60c5" }, true);
            var details = info.ProtocolDetails;
            var sections = Get(details, "sections") as List<object>;
            if (sections == null || sections.Count == 0)
            {
                sheet.Row(new List<object> { "No record", "\u6ca1\u6709\u8bb0\u5f55" });
                sheet.Row(new List<object> { "The program has no protocol details of this run (an earlier version recorded it, or the instrument software's files did not have them).",
                                             "\u7a0b\u5e8f\u6ca1\u6709\u8fd9\u4e2a\u5b9e\u9a8c\u7684\u65b9\u6848\u8be6\u60c5\uff08\u5b9e\u9a8c\u662f\u65e7\u7248\u672c\u8bb0\u5f55\u7684\uff0c\u6216\u8005\u4eea\u5668\u8f6f\u4ef6\u7684\u6587\u4ef6\u91cc\u6ca1\u6709\uff09\u3002" });
            }
            else
            {
                foreach (object item in sections)
                {
                    if (!(item is Dictionary<string, object> section)) continue;
                    sheet.Row(new List<object>());
                    sheet.Row(new List<object> { Text(Get(section, "title")), Text(Get(section, "titleZh")) }, true);
                    foreach (object r in Get(section, "rows") as List<object> ?? new List<object>())
                    {
                        if (!(r is Dictionary<string, object> row)) continue;
                        object zh = Get(row, "valueZh");
                        sheet.Row(new List<object> { Text(Get(row, "label")), Text(Get(row, "labelZh")), Text(Get(row, "value")), zh != null ? Text(zh) : null }, false, true);
                    }
                }
                if (Get(details, "steps") is List<object> stages && stages.Count > 0)
                {
                    sheet.Row(new List<object>());
                    sheet.Row(new List<object> { "Temperature program, step by step", "\u6e29\u5ea6\u7a0b\u5e8f\uff08\u9010\u6b65\uff09" }, true);
                    sheet.Row(new List<object> { "Stage", "Kind", "Repeats", "Step", "Temperature", "Hold (m:ss)", "Ramp", "Data collected" }, true);
                    sheet.Row(new List<object> { "\u9636\u6bb5", "\u7c7b\u578b", "\u91cd\u590d\u6b21\u6570", "\u6b65\u9aa4", "\u6e29\u5ea6", "\u4fdd\u6301\u65f6\u95f4", "\u5347\u964d\u6e29\u901f\u7387", "\u91c7\u5149" }, true);
                    foreach (object s in stages)
                    {
                        if (!(s is Dictionary<string, object> stage)) continue;
                        int number = 0;
                        foreach (object st in Get(stage, "steps") as List<object> ?? new List<object>())
                        {
                            if (!(st is Dictionary<string, object> step)) continue;
                            number++;
                            sheet.Row(new List<object>
                            {
                                Get(stage, "stage"), Text(Get(stage, "kind")), Get(stage, "repeats"), (long)number, Text(Get(step, "temperature")),
                                Text(Get(step, "hold")), Text(Get(step, "ramp")), Get(step, "collect") is bool c ? (c ? "yes / \u662f" : "no / \u5426") : null,
                            });
                        }
                    }
                }
            }
            if (info.OmegaValues != null && info.OmegaValues.Count > 0)
            {
                sheet.Row(new List<object>());
                sheet.Row(new List<object> { "Omega original values", "Omega \u539f\u59cb\u503c" }, true);
                sheet.Row(new List<object> { "Field", "Value" }, true);
                foreach (var entry in info.OmegaValues) sheet.Row(new List<object> { entry.Key, Cell(entry.Value) }, false, true);
            }
        }

        static void BasicData(Sheet sheet, Dictionary<string, object> run, List<Channel> channels)
        {
            bool several = channels.Count > 1;
            for (int i = 0; i < channels.Count; i++)
            {
                if (i > 0) sheet.Row(new List<object>());
                if (several) sheet.Row(new List<object> { Title(channels[i]) }, true);
                Table(sheet, run, channels[i]);
            }
        }

        /// <summary>A time for the sheet: "2026-01-20 09:30:00" (the local time; the time zone has a row of its own).</summary>
        static string Shown(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            DateTime? local = Clock.Local(iso);
            return local.HasValue ? local.Value.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss", Inv) : iso.Replace('T', ' ');
        }

        static object Get(Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out object v) ? v : null;

        static string Str(object value)
        {
            switch (value)
            {
                case null: return "";
                case string s: return s;
                case double d: return Json.FormatDouble(d);
                case bool b: return b ? "true" : "false";
                default: return Convert.ToString(value, Inv);
            }
        }

        /// <summary>One worksheet, written row by row into its zip entry.</summary>
        sealed class Sheet : IDisposable
        {
            readonly StreamWriter writer;
            int row;

            public Sheet(ZipArchive zip, string name, double firstColumnWidth)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                writer = new StreamWriter(entry.Open(), Utf8, 1 << 16);
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"" + Main + "\" xmlns:r=\"" + Rel + "\">");
                if (firstColumnWidth > 0)
                    writer.Write("<cols><col min=\"1\" max=\"1\" width=\"" + firstColumnWidth.ToString(Inv) + "\" customWidth=\"1\"/></cols>");
                writer.Write("<sheetData>");
            }

            /// <summary>The next row: strings as text, numbers as numbers, null as an empty cell. boldFirst: only the
            /// first cell bold (the item names of the two-column block).</summary>
            public void Row(IList<object> cells, bool bold = false, bool boldFirst = false)
            {
                row++;
                if (cells.Count == 0) return;            // an empty row is just a row number that is skipped
                writer.Write("<row r=\"");
                writer.Write(row.ToString(Inv));
                writer.Write("\">");
                for (int i = 0; i < cells.Count; i++)
                {
                    object value = cells[i];
                    if (value == null) continue;
                    string reference = Column(i) + row.ToString(Inv);
                    string style = bold || (boldFirst && i == 0) ? " s=\"1\"" : "";
                    switch (value)
                    {
                        case string s:
                            if (s.Length == 0) continue;
                            writer.Write("<c r=\"" + reference + "\" t=\"inlineStr\"" + style + "><is><t xml:space=\"preserve\">");
                            writer.Write(Escape(s));
                            writer.Write("</t></is></c>");
                            break;
                        case bool b:
                            writer.Write("<c r=\"" + reference + "\" t=\"b\"" + style + "><v>" + (b ? "1" : "0") + "</v></c>");
                            break;
                        case double d:
                            if (double.IsNaN(d) || double.IsInfinity(d)) continue;
                            writer.Write("<c r=\"" + reference + "\"" + style + "><v>" + d.ToString("R", Inv) + "</v></c>");
                            break;
                        case float f:
                            if (float.IsNaN(f) || float.IsInfinity(f)) continue;
                            writer.Write("<c r=\"" + reference + "\"" + style + "><v>" + ((double)f).ToString("R", Inv) + "</v></c>");
                            break;
                        case long _: case int _: case short _: case byte _: case uint _: case ushort _: case sbyte _: case ulong _: case decimal _:
                            writer.Write("<c r=\"" + reference + "\"" + style + "><v>" + Convert.ToString(value, Inv) + "</v></c>");
                            break;
                        default:
                            string text = Str(value);
                            if (text.Length == 0) continue;
                            writer.Write("<c r=\"" + reference + "\" t=\"inlineStr\"" + style + "><is><t xml:space=\"preserve\">" + Escape(text) + "</t></is></c>");
                            break;
                    }
                }
                writer.Write("</row>");
            }

            public void Dispose()
            {
                writer.Write("</sheetData></worksheet>");
                writer.Dispose();
            }
        }

        /// <summary>"A", "B", ..., "Z", "AA", ... for a 0-based column.</summary>
        public static string Column(int index)
        {
            var sb = new StringBuilder();
            for (int n = index + 1; n > 0; n = (n - 1) / 26) sb.Insert(0, (char)('A' + (n - 1) % 26));
            return sb.ToString();
        }

        /// <summary>Text for an XML element: escaped, without the characters XML cannot hold, at most what a cell holds.</summary>
        static string Escape(string text)
        {
            if (text.Length > 32767) text = text.Substring(0, char.IsHighSurrogate(text[32766]) ? 32766 : 32767);
            var sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[i + 1]); i++; }
                        }
                        else if (char.IsLowSurrogate(c)) { }
                        else if (c >= 0x20 ? c != 0xFFFE && c != 0xFFFF : c == '\t' || c == '\n' || c == '\r') sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
