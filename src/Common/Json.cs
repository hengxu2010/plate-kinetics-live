// JSON the way the Python programs read and write it (json.loads / json.dumps), so records written by either
// program read the same in the other.
//
// Values: Dictionary<string, object> (an object; the order of its keys is the order they were added),
// List<object> (an array), string, long (an integer), double (any other number), bool, null.
// Parse also takes NaN, Infinity and -Infinity, as Python's json.loads does; Serialize writes them the same way
// (Python's default allow_nan=True), so callers that answer the browser must keep them out, as the Python code does.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SAAControl
{
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipSpace();
            object value = p.Value();
            p.SkipSpace();
            if (p.Pos != text.Length) throw p.Fail("extra data");
            return value;
        }

        /// <summary>Python's json.dumps(value, ensure_ascii=ensureAscii, indent=indent, sort_keys=sortKeys) with its
        /// default separators: ", " and ": " without indent, "," and ": " with it.</summary>
        public static string Serialize(object value, bool ensureAscii = true, int? indent = null, bool sortKeys = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, ensureAscii, indent, sortKeys, 0);
            return sb.ToString();
        }

        // ---------- writing ----------

        static void Write(StringBuilder sb, object value, bool ascii, int? indent, bool sort, int level)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s, ascii); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: sb.Append(FormatDouble(d)); return;
                case float f: sb.Append(FormatDouble(f)); return;
                case decimal m: sb.Append(FormatDouble((double)m)); return;
                case long _: case int _: case short _: case byte _: case sbyte _: case ushort _: case uint _: case ulong _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return;
                case IDictionary dict: WriteObject(sb, dict, ascii, indent, sort, level); return;
                case IEnumerable list: WriteArray(sb, list, ascii, indent, sort, level); return;
                default: throw new ArgumentException("not a JSON value: " + value.GetType().FullName);
            }
        }

        static void NewLine(StringBuilder sb, int? indent, int level)
        {
            sb.Append('\n');
            sb.Append(' ', indent.Value * level);
        }

        static void WriteObject(StringBuilder sb, IDictionary dict, bool ascii, int? indent, bool sort, int level)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            var keys = new List<object>();
            foreach (DictionaryEntry e in dict) keys.Add(e.Key);
            if (sort) keys.Sort((a, b) => string.CompareOrdinal(KeyText(a), KeyText(b)));
            sb.Append('{');
            bool first = true;
            foreach (object key in keys)
            {
                if (!first) sb.Append(indent.HasValue ? "," : ", ");
                first = false;
                if (indent.HasValue) NewLine(sb, indent, level + 1);
                WriteString(sb, KeyText(key), ascii);
                sb.Append(": ");
                Write(sb, dict[key], ascii, indent, sort, level + 1);
            }
            if (indent.HasValue) NewLine(sb, indent, level);
            sb.Append('}');
        }

        static string KeyText(object key)
        {
            switch (key)
            {
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return FormatDouble(d);
                case null: return "null";
                default: return Convert.ToString(key, CultureInfo.InvariantCulture);
            }
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, bool ascii, int? indent, bool sort, int level)
        {
            bool first = true;
            sb.Append('[');
            foreach (object item in list)
            {
                if (!first) sb.Append(indent.HasValue ? "," : ", ");
                first = false;
                if (indent.HasValue) NewLine(sb, indent, level + 1);
                Write(sb, item, ascii, indent, sort, level + 1);
            }
            if (first) { sb.Append(']'); return; }
            if (indent.HasValue) NewLine(sb, indent, level);
            sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s, bool ascii)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Python escapes control characters always, and with ensure_ascii everything outside ' '..'~'
                        if (c < 0x20 || (ascii && c > 0x7e))
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>A double as Python's repr writes it: "3.0", "0.1", "1e-05", "1.5e+16", "NaN", "Infinity".</summary>
        public static string FormatDouble(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return (1 / d) < 0 ? "-0.0" : "0.0";
            string r = ShortestRoundTrip(d);
            // split the round-trip text into sign, significant digits and the decimal exponent of the first digit
            bool negative = r[0] == '-';
            if (negative) r = r.Substring(1);
            int exp10 = 0;
            int e = r.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0) { exp10 = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture); r = r.Substring(0, e); }
            int dot = r.IndexOf('.');
            string intPart = dot >= 0 ? r.Substring(0, dot) : r, fracPart = dot >= 0 ? r.Substring(dot + 1) : "";
            string digits = (intPart + fracPart).TrimStart('0');
            int lead = (intPart + fracPart).Length - digits.Length;           // zeros in front of the first digit
            int point = intPart.Length + exp10 - lead;                       // digits before the decimal point
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) return negative ? "-0.0" : "0.0";
            int sciExp = point - 1;
            var sb = new StringBuilder();
            if (negative) sb.Append('-');
            if (sciExp < -4 || sciExp >= 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                sb.Append('e').Append(sciExp < 0 ? '-' : '+').Append(Math.Abs(sciExp).ToString("00", CultureInfo.InvariantCulture));
            }
            else if (point <= 0)
            {
                sb.Append("0.").Append('0', -point).Append(digits);
            }
            else if (point >= digits.Length)
            {
                sb.Append(digits).Append('0', point - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
            }
            return sb.ToString();
        }

        /// <summary>The fewest significant digits that read back as the same double, and of those the ones nearest to it -
        /// what Python's repr writes (dtoa mode 0) - worked out with exact integer arithmetic: .NET Framework's own
        /// formatting and parsing are not exact beyond 15 digits, nor at very small and very large exponents. For a normal
        /// double only 15, 16 and 17 digits need trying: a double's rounding interval is narrower than half a unit in the
        /// 15th digit, so when a shorter form reads back, the nearest 15-digit form is that form with zeros after it; and
        /// 17 digits always read back. Returned in "d.dddE+x" form for FormatDouble to rewrite.</summary>
        static string ShortestRoundTrip(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            bool negative = bits < 0;
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            long m = rawExp == 0 ? frac : frac | (1L << 52);
            int e = rawExp == 0 ? -1074 : rawExp - 1075;          // |d| = m * 2^e = num / den
            BigInteger num = e >= 0 ? (BigInteger)m << e : m, den = e >= 0 ? BigInteger.One : BigInteger.One << -e;
            bool evenM = (m & 1) == 0;                            // even: a number exactly halfway still reads back as d
            bool narrowBelow = frac == 0 && rawExp > 1;           // a power of two: the neighbour below is nearer
            // k: the decimal exponent of the first digit, 10^k <= |d| < 10^(k+1)
            int k = (int)Math.Floor(Math.Log10(Math.Abs(d)));
            while (Compare(num, den, Pow10(k)) < 0) k--;
            while (Compare(num, den, Pow10(k + 1)) >= 0) k++;
            for (int n = rawExp == 0 ? 1 : 15; n <= 17; n++)
            {
                int s = n - 1 - k;                                 // |d| * 10^s has n digits before the point
                BigInteger N = s >= 0 ? num * Pow10Value(s) : num, D = s >= 0 ? den : den * Pow10Value(s);
                BigInteger q = BigInteger.DivRem(N, D, out BigInteger r);
                int half = (r * 2).CompareTo(D);
                if (half > 0 || (half == 0 && !q.IsEven)) q += 1;  // the nearest n-digit number, ties to even
                if (ReadsBack(q, n, k, num, den, e, evenM, narrowBelow, out string text)) return (negative ? "-" : "") + text;
                // at a power of two the nearest one may lie below the (narrower) lower half of the interval while the
                // next one up still lies inside the upper half
                if (narrowBelow && ReadsBack(q + 1, n, k, num, den, e, evenM, narrowBelow, out text)) return (negative ? "-" : "") + text;
            }
            return d.ToString("R", CultureInfo.InvariantCulture);  // (not reached: 17 digits always read back)
        }

        /// <summary>Whether q * 10^(k-n+1) reads back as the double num/den = m * 2^e; if so, its "d.dddE+x" text.</summary>
        static bool ReadsBack(BigInteger q, int n, int k, BigInteger num, BigInteger den, int e, bool evenM, bool narrowBelow, out string text)
        {
            text = null;
            if (q == Pow10Value(n)) { q = Pow10Value(n - 1); k++; }   // 9.99... rounded up to 10.0
            int t = k - n + 1;                                         // the candidate is q * 10^t
            BigInteger cN = t >= 0 ? q * Pow10Value(t) : q, cD = t >= 0 ? BigInteger.One : Pow10Value(t);
            BigInteger diffN = cN * den - num * cD, diffD = cD * den;  // candidate - |d| = diffN / diffD
            // half the distance to the neighbouring double on the candidate's side: 2^(e-1), or 2^(e-2) below a power of two
            int gapExp = (diffN.Sign >= 0 || !narrowBelow) ? e - 1 : e - 2;
            BigInteger gN = gapExp >= 0 ? BigInteger.One << gapExp : BigInteger.One, gD = gapExp >= 0 ? BigInteger.One : BigInteger.One << -gapExp;
            int c = (BigInteger.Abs(diffN) * gD).CompareTo(gN * diffD);
            if (!(c < 0 || (c == 0 && evenM))) return false;
            string digits = q.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            string mant = digits.Length > 1 ? digits[0] + "." + digits.Substring(1) : digits;
            text = mant + "E" + (k >= 0 ? "+" : "-") + Math.Abs(k).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>A number as Python's float() reads it: the double nearest to its decimal value, ties to even (too
        /// large: infinity). .NET Framework's own reading can be one step off (beyond 15 digits, and at very small and very
        /// large exponents) and reads "-0.0" as 0, so its answer is checked with exact integer arithmetic and moved to the
        /// neighbouring double when needed.</summary>
        public static bool TryParseDouble(string s, out double d)
        {
            d = 0;
            // the decimal value: sign, digits (without the point) and the power of ten that goes with them
            int i = 0;
            bool negative = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; i++; }
            var digits = new StringBuilder();
            int fractionDigits = 0, mantissaDigits = 0;
            bool point = false;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { digits.Append(c); mantissaDigits++; if (point) fractionDigits++; }
                else if (c == '.' && !point) point = true;
                else break;
            }
            if (mantissaDigits == 0) return false;
            long exp = 0;
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool expNegative = false;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) { expNegative = s[i] == '-'; i++; }
                int expStart = i;
                for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
                    if (exp < 100000000) exp = exp * 10 + (s[i] - '0');
                if (i == expStart) return false;
                if (expNegative) exp = -exp;
            }
            if (i != s.Length) return false;
            string ds = digits.ToString().TrimStart('0');
            double zero = negative ? -0.0 : 0.0;
            if (ds.Length == 0) { d = zero; return true; }
            long e10 = exp - fractionDigits;                       // value = ds * 10^e10
            long first = e10 + ds.Length - 1;                      // the power of ten of the first digit
            if (first > 309) { d = negative ? double.NegativeInfinity : double.PositiveInfinity; return true; }
            if (first < -325) { d = zero; return true; }           // below half the smallest double
            BigInteger M = BigInteger.Parse(ds, CultureInfo.InvariantCulture);
            int E = (int)e10;

            // a first guess from .NET (it gives up near and beyond the largest double), then the exact check
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double guess) || double.IsInfinity(guess))
                guess = double.MaxValue;
            guess = Math.Abs(guess);
            for (int step = 0; step < 64; step++)
            {
                long bits = BitConverter.DoubleToInt64Bits(guess);
                int rawExp = (int)((bits >> 52) & 0x7FF);
                long frac = bits & 0xFFFFFFFFFFFFFL;
                long m = rawExp == 0 ? frac : frac | (1L << 52);
                int e = rawExp == 0 ? -1074 : rawExp - 1075;      // guess = m * 2^e
                bool narrowBelow = frac == 0 && rawExp > 1;
                // everything times 10^p10 * 2^p2, so that all are whole numbers
                int p10 = Math.Max(-E, 0), p2 = Math.Max(2 - e, 0);
                BigInteger scale10 = Pow10Value(p10);
                BigInteger value = M * (E > 0 ? Pow10Value(E) : BigInteger.One) << p2;
                BigInteger at = ((BigInteger)m << (e + p2)) * scale10;
                BigInteger halfUp = (BigInteger.One << (e - 1 + p2)) * scale10;
                BigInteger halfDown = narrowBelow ? (BigInteger.One << (e - 2 + p2)) * scale10 : halfUp;
                BigInteger diff = value - at;
                bool odd = (m & 1) == 1;
                int up = diff.CompareTo(halfUp), down = (-diff).CompareTo(halfDown);
                if (up > 0 || (up == 0 && odd))
                {
                    if (rawExp == 0x7FE && frac == 0xFFFFFFFFFFFFFL) { guess = double.PositiveInfinity; break; }
                    guess = BitConverter.Int64BitsToDouble(bits + 1);
                }
                else if (down > 0 || (down == 0 && odd)) guess = BitConverter.Int64BitsToDouble(bits - 1);
                else break;
            }
            d = negative ? -guess : guess;
            return true;
        }

        static BigInteger Pow10Value(int k) => BigInteger.Pow(10, Math.Abs(k));

        /// <summary>10^k as a fraction (numerator, denominator).</summary>
        static (BigInteger, BigInteger) Pow10(int k) => k >= 0 ? (Pow10Value(k), BigInteger.One) : (BigInteger.One, Pow10Value(k));

        /// <summary>num/den compared with a fraction.</summary>
        static int Compare(BigInteger num, BigInteger den, (BigInteger, BigInteger) f) => (num * f.Item2).CompareTo(f.Item1 * den);

        // ---------- reading ----------

        sealed class Parser
        {
            readonly string t;
            public int Pos;
            public Parser(string text) { t = text; }

            public FormatException Fail(string what) => new FormatException("JSON: " + what + " at " + Pos);

            public void SkipSpace()
            {
                while (Pos < t.Length && (t[Pos] == ' ' || t[Pos] == '\t' || t[Pos] == '\n' || t[Pos] == '\r')) Pos++;
            }

            public object Value()
            {
                if (Pos >= t.Length) throw Fail("unexpected end");
                char c = t[Pos];
                switch (c)
                {
                    case '{': return ObjectValue();
                    case '[': return ArrayValue();
                    case '"': return StringValue();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    case 'N': Word("NaN"); return double.NaN;
                    case 'I': Word("Infinity"); return double.PositiveInfinity;
                    default:
                        if (c == '-' && Pos + 1 < t.Length && t[Pos + 1] == 'I') { Pos++; Word("Infinity"); return double.NegativeInfinity; }
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw Fail("unexpected character '" + c + "'");
                }
            }

            void Word(string w)
            {
                if (string.CompareOrdinal(t, Pos, w, 0, w.Length) != 0) throw Fail("expected " + w);
                Pos += w.Length;
            }

            object Number()
            {
                int start = Pos;
                if (t[Pos] == '-') Pos++;
                bool real = false;
                while (Pos < t.Length)
                {
                    char c = t[Pos];
                    if (c >= '0' && c <= '9') { Pos++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { real = true; Pos++; continue; }
                    break;
                }
                string s = t.Substring(start, Pos - start);
                if (!real && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                if (TryParseDouble(s, out double d)) return d;
                throw Fail("bad number " + s);
            }

            string StringValue()
            {
                Pos++;   // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= t.Length) throw Fail("unterminated string");
                    char c = t[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c < ' ') { Pos--; throw Fail("invalid control character in a string"); }   // Python's strict json.loads
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= t.Length) throw Fail("unterminated escape");
                    char x = t[Pos++];
                    switch (x)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > t.Length) throw Fail("bad \\u escape");
                            sb.Append((char)int.Parse(t.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw Fail("bad escape \\" + x);
                    }
                }
            }

            Dictionary<string, object> ObjectValue()
            {
                Pos++;   // {
                var d = new Dictionary<string, object>();
                SkipSpace();
                if (Pos < t.Length && t[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipSpace();
                    if (Pos >= t.Length || t[Pos] != '"') throw Fail("expected a key");
                    string key = StringValue();
                    SkipSpace();
                    if (Pos >= t.Length || t[Pos] != ':') throw Fail("expected ':'");
                    Pos++;
                    SkipSpace();
                    d[key] = Value();   // a repeated key keeps the last value, as in Python
                    SkipSpace();
                    if (Pos >= t.Length) throw Fail("unterminated object");
                    if (t[Pos] == ',') { Pos++; continue; }
                    if (t[Pos] == '}') { Pos++; return d; }
                    throw Fail("expected ',' or '}'");
                }
            }

            List<object> ArrayValue()
            {
                Pos++;   // [
                var list = new List<object>();
                SkipSpace();
                if (Pos < t.Length && t[Pos] == ']') { Pos++; return list; }
                while (true)
                {
                    SkipSpace();
                    list.Add(Value());
                    SkipSpace();
                    if (Pos >= t.Length) throw Fail("unterminated array");
                    if (t[Pos] == ',') { Pos++; continue; }
                    if (t[Pos] == ']') { Pos++; return list; }
                    throw Fail("expected ',' or ']'");
                }
            }
        }
    }
}
