// The experiment file ".pk", format "plate-kinetics-live/1": a plain zip (any zip program opens it) with
//   README.txt     one page on what the file is (Chinese, then English; UTF-8 with a byte order mark, Windows line ends)
//   info.json      {"format", "program", "version", "experimentId", "runId", "name",
//                   "instrument": {"model", "serial", "software", "softwareVersion"}, "startedAt", "endedAt" (ISO 8601 with
//                   the UTC offset), "timeZone", "status": measuring|completed|stopped|interrupted|unknown,
//                   "statusText": {"en", "zh"}, "cycles": {"done", "planned"}, "interruption": {"at", "reason", "detail"}|null,
//                   "operator", "runSha256" (SHA-256 of the bytes of run.json), "sealedAt" (when the run ended and its data were
//                   sealed; null while measuring), "analysisSavedAt"}
//   run.json       the run exactly as GET /api/run returns it. Once sealed it only gains cycles that were missing (from the
//                  instrument's own files), never changed values; the hash is renewed then.
//   analysis.json  optional: the saved analysis {"format": "plate-kinetics-live-analysis/1", "savedAt", "savedBy",
//                  "operator", "samples", "groups", "notes", "settings", "results", "rev" (its revision on the PC that
//                  recorded the run), then what else the page keeps}
// Entries in this order; the JSON as Python's json.dumps writes it (info.json and analysis.json indented by one space).
// Readers ignore fields and entries they do not know, and refuse a file of a higher major format
// ("plate-kinetics-live/2"): "Made by a newer version of Plate Kinetics Live - please update".
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SAAControl
{
    /// <summary>A file of a newer format than this program reads (a FormatException: InvalidDataException is sealed).</summary>
    public sealed class NewerFormatException : FormatException
    {
        public NewerFormatException() : base(PkFile.NewerText) { }
        public string MessageZh => PkFile.NewerTextZh;
    }

    public static class PkFile
    {
        public const string FormatPrefix = "plate-kinetics-live/";
        public const int FormatMajor = 1;
        public const string Format = FormatPrefix + "1";
        public const string AnalysisPrefix = "plate-kinetics-live-analysis/";
        public const string AnalysisFormat = AnalysisPrefix + "1";
        public const string Extension = ".pk";
        public const string ReadmeEntry = "README.txt", InfoEntry = "info.json", RunEntry = "run.json", AnalysisEntry = "analysis.json";
        const long MaxEntry = 512L << 20;            // an entry beyond this is not one of ours
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public const string NewerText = "Made by a newer version of Plate Kinetics Live - please update";
        // "This file was made by a newer version of Plate Kinetics Live - please update the program first."
        public const string NewerTextZh = "\u6b64\u6587\u4ef6\u7531\u66f4\u65b0\u7248\u672c\u7684 Plate Kinetics Live \u751f\u6210\uff0c\u8bf7\u5148\u66f4\u65b0\u7a0b\u5e8f\u3002";

        /// <summary>What a .pk holds. RunBytes and AnalysisBytes are the entries' bytes as stored (a rewrite copies them).</summary>
        public sealed class Contents
        {
            public Dictionary<string, object> Info;
            public byte[] RunBytes;
            public Dictionary<string, object> Run;
            public byte[] AnalysisBytes;
            public Dictionary<string, object> Analysis;
            /// <summary>Entries this program does not know (kept as they are when the file is written again).</summary>
            public List<KeyValuePair<string, byte[]>> Others = new List<KeyValuePair<string, byte[]>>();

            public void Deconstruct(out Dictionary<string, object> info, out object run) { info = Info; run = Run; }
            public bool Sealed => Info != null && Info.TryGetValue("sealedAt", out object s) && s is string at && at.Length > 0;
            public string RunId => Info != null && Info.TryGetValue("runId", out object r) ? r as string : null;
        }

        // ---- writing

        /// <summary>The bytes of a .pk: README.txt, info.json, run.json (its bytes as given), analysis.json (when given),
        /// then the other entries.</summary>
        public static byte[] Build(Dictionary<string, object> info, byte[] runBytes, byte[] analysisBytes = null,
                                   IEnumerable<KeyValuePair<string, byte[]>> others = null)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Add(zip, ReadmeEntry, ReadmeBytes());
                    Add(zip, InfoEntry, InfoBytes(info));
                    Add(zip, RunEntry, runBytes);
                    if (analysisBytes != null) Add(zip, AnalysisEntry, analysisBytes);
                    foreach (var other in others ?? Enumerable.Empty<KeyValuePair<string, byte[]>>())
                        if (!Known(other.Key)) Add(zip, other.Key, other.Value);
                }
                return memory.ToArray();
            }
        }

        /// <summary>A .pk of a run (its info.json with runSha256 set from the run).</summary>
        public static byte[] Build(Dictionary<string, object> info, object run)
        {
            byte[] runBytes = RunBytes(run);
            info["runSha256"] = Sha256(runBytes);
            return Build(info, runBytes);
        }

        static bool Known(string name) => name == ReadmeEntry || name == InfoEntry || name == RunEntry || name == AnalysisEntry;

        static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>run.json: the run as Python's json.dumps(run, ensure_ascii=False) writes it.</summary>
        public static byte[] RunBytes(object run) => Utf8.GetBytes(Json.Serialize(run, ensureAscii: false));

        public static byte[] InfoBytes(Dictionary<string, object> info) => Utf8.GetBytes(Json.Serialize(info, ensureAscii: false, indent: 1));

        public static byte[] AnalysisBytes(Dictionary<string, object> analysis) => Utf8.GetBytes(Json.Serialize(analysis, ensureAscii: false, indent: 1));

        /// <summary>The SHA-256 of bytes, as 64 lowercase hex digits.</summary>
        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static byte[] ReadmeBytes() => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(ReadmeText.Replace("\n", "\r\n"))).ToArray();

        public const string ReadmeText =
            "Plate Kinetics Live \u5b9e\u9a8c\u6587\u4ef6\uff08.pk\uff09\n" +
            "\n" +
            "\u8fd9\u662f\u4e00\u4e2a\u666e\u901a\u7684 zip \u538b\u7f29\u5305\uff0c\u4efb\u4f55\u89e3\u538b\u8f6f\u4ef6\u90fd\u80fd\u6253\u5f00\u3002\u53cc\u51fb\u4f1a\u7528 Plate Kinetics Live \u6253\u5f00\u3002\n" +
            "  info.json      \u5b9e\u9a8c\u4fe1\u606f\uff1a\u540d\u79f0\u3001\u4eea\u5668\u3001\u5f00\u59cb\u548c\u7ed3\u675f\u65f6\u95f4\u3001\u72b6\u6001\u3001\u8f6e\u6570\u3001\u6821\u9a8c\u7801\n" +
            "  run.json       \u539f\u59cb\u6570\u636e\uff08\u6bcf\u4e2a\u5b54\u6bcf\u4e00\u8f6e\u7684\u8bfb\u6570\u3001\u65f6\u95f4\u3001\u6e29\u5ea6\uff09\uff0c\u5b9e\u9a8c\u7ed3\u675f\u65f6\u5c01\u5b58\n" +
            "  analysis.json  \u4fdd\u5b58\u7684\u5206\u6790\uff1a\u6837\u672c\u540d\u3001\u5206\u7ec4\u3001\u5907\u6ce8\u3001\u5206\u6790\u8bbe\u7f6e\u548c\u7ed3\u679c\uff08\u4fdd\u5b58\u8fc7\u624d\u6709\uff09\n" +
            "  README.txt     \u672c\u8bf4\u660e\n" +
            "\n" +
            "\u6821\u9a8c\uff1arun.json \u7684 SHA-256 \u5e94\u7b49\u4e8e info.json \u91cc\u7684 runSha256\uff1b\u4e0d\u76f8\u7b49\uff0c\u8bf4\u660e\u539f\u59cb\u6570\u636e\u5728\u8bb0\u5f55\u4e4b\u540e\u88ab\u6539\u52a8\u8fc7\u3002\n" +
            "\u683c\u5f0f\uff1aplate-kinetics-live/1\n" +
            "\n" +
            "\n" +
            "Plate Kinetics Live experiment file (.pk)\n" +
            "\n" +
            "This is an ordinary zip archive: any zip program opens it. A double-click opens it in Plate Kinetics Live.\n" +
            "  info.json      About the experiment: name, instrument, start and end, status, cycles, checksum\n" +
            "  run.json       The raw data (the reading of every well in every cycle, times, temperatures), sealed when the\n" +
            "                 experiment ended\n" +
            "  analysis.json  The saved analysis: sample names, groups, notes, analysis settings and results (after a save)\n" +
            "  README.txt     This text\n" +
            "\n" +
            "Check: the SHA-256 of run.json must equal runSha256 in info.json. If it does not, the raw data were changed after\n" +
            "they were recorded.\n" +
            "Format: plate-kinetics-live/1\n";

        // ---- reading

        /// <summary>Throws when the bytes are not a complete .pk of a format this program reads: every entry readable,
        /// info.json and run.json JSON objects, run.json's hash the one in info.json.</summary>
        public static void Validate(byte[] bytes)
        {
            using (var memory = new MemoryStream(bytes, false))
            {
                Contents read = Read(memory, true, true);
                CheckHash(read);
            }
        }

        /// <summary>Throws InvalidDataException when run.json's bytes do not have the hash info.json gives (a file of an
        /// earlier version without the hash passes).</summary>
        public static void CheckHash(Contents read)
        {
            if (read.Info.TryGetValue("runSha256", out object hash) && hash is string expected && expected.Length > 0
                && read.RunBytes != null && !string.Equals(Sha256(read.RunBytes), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("run.json does not have the checksum in info.json: the raw data were changed");
        }

        /// <summary>A .pk's entries. Throws InvalidDataException (not a zip, not one of ours, an entry missing),
        /// NewerFormatException (a newer format), FormatException (JSON), or the file's IOException.</summary>
        public static Contents ReadFile(string path, bool withRun, bool withAnalysis = false, bool all = false)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                return Read(stream, withRun, withAnalysis, all);
        }

        public static Contents Read(Stream stream, bool withRun, bool withAnalysis = false, bool all = false)
        {
            try
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    var read = new Contents();
                    read.Info = Json.Parse(Utf8.GetString(Bytes(zip, InfoEntry, true))) as Dictionary<string, object>
                                ?? throw new InvalidDataException("info.json is not a JSON object");
                    CheckFormat(read.Info);
                    if (withRun || all)
                    {
                        read.RunBytes = Bytes(zip, RunEntry, true);
                        if (withRun)
                            read.Run = Json.Parse(Utf8.GetString(read.RunBytes)) as Dictionary<string, object>
                                       ?? throw new InvalidDataException("run.json is not a JSON object");
                    }
                    if (withAnalysis || all)
                    {
                        read.AnalysisBytes = Bytes(zip, AnalysisEntry, false);
                        if (read.AnalysisBytes != null && withAnalysis)
                        {
                            read.Analysis = Json.Parse(Utf8.GetString(read.AnalysisBytes)) as Dictionary<string, object>
                                            ?? throw new InvalidDataException("analysis.json is not a JSON object");
                            CheckAnalysisFormat(read.Analysis);
                        }
                    }
                    if (all)
                        foreach (ZipArchiveEntry entry in zip.Entries)
                            if (!Known(entry.FullName) && entry.FullName.Length > 0 && !entry.FullName.EndsWith("/"))
                                read.Others.Add(new KeyValuePair<string, byte[]>(entry.FullName, Bytes(zip, entry.FullName, true)));
                    return read;
                }
            }
            catch (DecoderFallbackException error) { throw new InvalidDataException("not UTF-8 text: " + error.Message, error); }
        }

        static byte[] Bytes(ZipArchive zip, string name, bool required)
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            if (entry == null)
            {
                if (required) throw new InvalidDataException("not a Plate Kinetics Live experiment file: no " + name);
                return null;
            }
            if (entry.Length > MaxEntry) throw new InvalidDataException(name + " is too large");
            using (Stream data = entry.Open())
            using (var memory = new MemoryStream())
            {
                data.CopyTo(memory);
                return memory.ToArray();
            }
        }

        /// <summary>Throws NewerFormatException for a higher major format, InvalidDataException for a file that is not one
        /// of ours.</summary>
        public static void CheckFormat(Dictionary<string, object> info)
        {
            string format = info != null && info.TryGetValue("format", out object f) ? f as string : null;
            int major = Major(format, FormatPrefix);
            if (major < 0) throw new InvalidDataException("not a Plate Kinetics Live experiment file (format " + (format ?? "none") + ")");
            if (major > FormatMajor) throw new NewerFormatException();
        }

        /// <summary>An analysis of a higher major format is refused (NewerFormatException); one without a format is taken
        /// as format 1.</summary>
        public static void CheckAnalysisFormat(Dictionary<string, object> analysis)
        {
            if (analysis == null || !analysis.TryGetValue("format", out object f) || f == null) return;
            int major = Major(f as string, AnalysisPrefix);
            if (major < 0) throw new InvalidDataException("not an analysis of Plate Kinetics Live (format " + f + ")");
            if (major > 1) throw new NewerFormatException();
        }

        /// <summary>"plate-kinetics-live/1" -> 1, "plate-kinetics-live/1.2" -> 1; -1 when it is not of that prefix.</summary>
        static int Major(string format, string prefix)
        {
            if (format == null || !format.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            string rest = format.Substring(prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot >= 0) rest = rest.Substring(0, dot);
            return int.TryParse(rest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major) ? major : -1;
        }

        /// <summary>The .pk again with another analysis (null: none): README.txt and info.json made again (its
        /// analysisSavedAt from the analysis), run.json and the other entries copied byte for byte.</summary>
        public static byte[] WithAnalysis(Contents read, Dictionary<string, object> analysis)
        {
            var info = new Dictionary<string, object>(read.Info);
            info["analysisSavedAt"] = analysis != null && analysis.TryGetValue("savedAt", out object at) ? at : null;
            return Build(info, read.RunBytes, analysis != null ? AnalysisBytes(analysis) : null, read.Others);
        }

        /// <summary>The .pk again with info.json changed by `change`; everything else copied byte for byte.</summary>
        public static byte[] WithInfo(Contents read, Action<Dictionary<string, object>> change)
        {
            var info = new Dictionary<string, object>(read.Info);
            change(info);
            return Build(info, read.RunBytes, read.AnalysisBytes, read.Others);
        }
    }
}
