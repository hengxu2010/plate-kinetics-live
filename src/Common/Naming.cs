// The names of the experiment files in Experiments\<yyyy-MM>\:
//   "<yyyy-MM-dd HH-mm-ss> <experiment name> - <instrument model> <serial>"
// from the run's start. The experiment name is what the user typed or chose (BMG: ID1 typed in Omega, else the
// protocol; Archimed: the experiment file's name), never a name our lab gave an instrument. Characters Windows forbids
// in file names are removed, a name has at most 120 characters, and a name another run already has gets " (2)", " (3)".
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAControl
{
    public static class Naming
    {
        public const int MaxLength = 120;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly char[] Forbidden = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>The text without the characters Windows forbids in a file name (and control characters), with
        /// whitespace runs made one space, and without spaces and dots at the ends (Windows drops trailing ones).</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (c < 0x20 || c == 0x7f || Array.IndexOf(Forbidden, c) >= 0)
                {
                    if (c == '\t' || c == '\r' || c == '\n') space = sb.Length > 0;
                    continue;
                }
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString().Trim(' ', '.');
        }

        /// <summary>The first `length` characters, without cutting a character made of two (a surrogate pair).</summary>
        static string Head(string text, int length)
        {
            if (length <= 0) return "";
            if (text.Length <= length) return text;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }

        /// <summary>A run's start ("2026-01-20T09:30:00", as records hold it) as "2026-01-20 09-30-00"; null when it is not
        /// such a time.</summary>
        public static string Stamp(string startedAt)
        {
            DateTime? start = Start(startedAt);
            return start?.ToString("yyyy'-'MM'-'dd' 'HH'-'mm'-'ss", Inv);
        }

        public static DateTime? Start(string startedAt)
        {
            if (string.IsNullOrEmpty(startedAt) || startedAt.Length < 19) return null;
            if (DateTime.TryParseExact(startedAt.Substring(0, 19), "yyyy'-'MM'-'dd'T'HH':'mm':'ss", Inv, DateTimeStyles.None, out DateTime t)) return t;
            if (DateTime.TryParseExact(startedAt.Substring(0, 19), "yyyy'-'MM'-'dd' 'HH':'mm':'ss", Inv, DateTimeStyles.None, out t)) return t;
            return null;
        }

        /// <summary>The month folder of a run, "2026-11" (from its start; "undated" when the start is not known).</summary>
        public static string MonthFolder(string startedAt)
        {
            DateTime? start = Start(startedAt);
            return start.HasValue ? start.Value.ToString("yyyy'-'MM", Inv) : "undated";
        }

        /// <summary>"<yyyy-MM-dd HH-mm-ss> <experiment> - <model> <serial>", at most maxLength characters: a long
        /// experiment name is shortened (to no less than 10 characters), the start and the instrument stay.</summary>
        public static string BaseName(string startedAt, string experiment, string model, string serial, int maxLength = MaxLength)
        {
            string stamp = Stamp(startedAt) ?? (Clean(startedAt).Length > 0 ? Clean(startedAt) : "undated");
            string name = Clean(experiment);
            if (name.Length == 0) name = "Experiment";
            string instrument = Clean(((model ?? "") + " " + (serial ?? "")).Trim());
            string prefix = stamp + " ", suffix = instrument.Length > 0 ? " - " + instrument : "";
            if (suffix.Length > MaxLength / 2) suffix = Head(suffix, MaxLength / 2).TrimEnd(' ', '.');
            name = Head(name, Math.Max(10, Math.Min(maxLength, MaxLength) - prefix.Length - suffix.Length)).TrimEnd(' ', '.');
            if (name.Length == 0) name = "E";
            return prefix + name + suffix;
        }

        /// <summary>Excel opens no workbook whose full path is longer than this.</summary>
        public const int MaxWorkbookPath = 218;

        /// <summary>BaseName for the files of a run in `folder`, shortened so that the workbook - with " (n)" after its
        /// name - keeps within the 218 characters of a full path Excel opens: a folder deep down (a copy of the program
        /// unpacked into a folder in a folder, the users' own subfolders of Experiments) would else give workbooks Excel
        /// cannot open, or files Windows cannot write at all (260 characters).</summary>
        public static string BaseNameIn(string folder, string startedAt, string experiment, string model, string serial)
        {
            int room = MaxLength;
            try { room = MaxWorkbookPath - (Path.GetFullPath(folder).TrimEnd('\\').Length + 1) - ".xlsx".Length - " (99)".Length; }
            catch (Exception) { }
            return BaseName(startedAt, experiment, model, serial, Math.Min(MaxLength, room));
        }

        /// <summary>BMG's ID1: whether it is one the user typed (not empty, and not only a date and/or a time that Omega
        /// filled in, such as "2026/6/20", "15:36:48" or "2026/6/20,15:36:48").</summary>
        public static bool IsTypedId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return !AutomaticId.IsMatch(id.Trim());
        }

        static readonly Regex AutomaticId = new Regex(
            @"^(?:(?:\d{1,4}[./-]\d{1,2}[./-]\d{1,4})(?:[\s,;T]+\d{1,2}:\d{1,2}(?::\d{1,2})?)?|\d{1,2}:\d{1,2}(?::\d{1,2})?)$",
            RegexOptions.CultureInvariant);

        /// <summary>A reader's model as Omega's database names it, without a trailing "(...)":
        /// "FLUOstar Omega (2023)" -> "FLUOstar Omega".</summary>
        public static string ReaderModel(string reader)
        {
            if (string.IsNullOrWhiteSpace(reader)) return "";
            string text = reader.Trim();
            var found = Regex.Match(text, @"^(.*?)\s*\([^()]*\)$");
            if (found.Success && found.Groups[1].Value.Trim().Length > 0) text = found.Groups[1].Value.Trim();
            return text;
        }

        /// <summary>A base name that no other run's file in the folder has: name, "name (2)", "name (3)", ... taken(base)
        /// tells whether a base name is taken (its files exist and are not this run's).</summary>
        public static string Unique(string baseName, Func<string, bool> taken)
        {
            if (!taken(baseName)) return baseName;
            for (int n = 2; n < 10000; n++)
            {
                string candidate = baseName + " (" + n.ToString(Inv) + ")";
                if (!taken(candidate)) return candidate;
            }
            return baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
        }

        /// <summary>Whether a file's base name is the generated one or that with " (n)" after it (so the user did not
        /// rename it).</summary>
        public static bool IsGenerated(string fileBase, string generated)
        {
            if (string.Equals(fileBase, generated, StringComparison.OrdinalIgnoreCase)) return true;
            if (!fileBase.StartsWith(generated + " (", StringComparison.OrdinalIgnoreCase) || !fileBase.EndsWith(")")) return false;
            string number = fileBase.Substring(generated.Length + 2, fileBase.Length - generated.Length - 3);
            return number.Length > 0 && number.All(char.IsLetterOrDigit);
        }
    }
}
