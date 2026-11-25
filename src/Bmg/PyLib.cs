// What the BMG data side needs of Python itself, so that the C# port does exactly what the Python modules did:
// the exception kinds (they decide which failure is caught where; str(exception) reaches the page as
// "Reading failed: ..."), truthiness, ==, <, slicing and indexing of values read from JSON records, repr() (it feeds
// runs.settled and the start-up log line), str.strip / casefold, hashlib.blake2b, zlib.decompress, os.stat times,
// os.replace, glob, and the conversions of datetime. Nothing here is specific to BMG.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAControl.Bmg
{
    /// <summary>A Python exception. Kind is its class name; Message is what str(exception) gives in Python.</summary>
    public class PyException : Exception
    {
        public string Kind { get; }
        public PyException(string kind, string message, Exception inner = null) : base(message, inner) { Kind = kind; }
    }

    /// <summary>OSError and its subclasses. NotFound: FileNotFoundError (errno ENOENT).</summary>
    public class PyOSError : PyException
    {
        public int WinError { get; }
        public string StrError { get; }
        public bool NotFound { get; }
        public PyOSError(string message, string strerror, int winerror = 0, bool notFound = false, Exception inner = null)
            : base(notFound ? "FileNotFoundError" : "OSError", message, inner)
        {
            StrError = strerror;
            WinError = winerror;
            NotFound = notFound;
        }
    }

    /// <summary>ValueError and its subclasses (absdb.AbsError, json.JSONDecodeError, UnicodeError).</summary>
    public class PyValueError : PyException
    {
        public PyValueError(string message, Exception inner = null) : base("ValueError", message, inner) { }
        protected PyValueError(string kind, string message, Exception inner) : base(kind, message, inner) { }
    }

    public class PyKeyError : PyException { public PyKeyError(string message) : base("KeyError", message) { } }
    public class PyTypeError : PyException { public PyTypeError(string message) : base("TypeError", message) { } }
    public class PyIndexError : PyException { public PyIndexError(string message) : base("IndexError", message) { } }
    public class PyAttributeError : PyException { public PyAttributeError(string message) : base("AttributeError", message) { } }
    /// <summary>struct.error (not a ValueError: Python's handlers that catch ValueError let it through).</summary>
    public class PyStructError : PyException { public PyStructError(string message) : base("error", message) { } }

    /// <summary>The exception classes a Python <c>except (...)</c> clause names.</summary>
    [Flags]
    public enum PyKinds { None = 0, OSError = 1, ValueError = 2, KeyError = 4, TypeError = 8, IndexError = 16 }

    public static class Py
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------ exceptions

        /// <summary>Whether a Python handler <c>except (kinds)</c> catches this exception. .NET exceptions of the file and
        /// text APIs count as what Python raises for the same failure.</summary>
        public static bool Is(Exception e, PyKinds kinds) => (KindsOf(e) & kinds) != 0;

        static PyKinds KindsOf(Exception e)
        {
            switch (e)
            {
                case PyOSError _: return PyKinds.OSError;
                case PyValueError _: return PyKinds.ValueError;
                case PyKeyError _: return PyKinds.KeyError;
                case PyTypeError _: return PyKinds.TypeError;
                case PyIndexError _: return PyKinds.IndexError;
                case PyException _: return PyKinds.None;                     // AttributeError, struct.error, ...
                case DecoderFallbackException _: case EncoderFallbackException _: return PyKinds.ValueError;   // UnicodeError
                case FormatException _: return PyKinds.ValueError;           // Json.Parse: json.JSONDecodeError
                case IOException _: case UnauthorizedAccessException _: case NotSupportedException _:
                case System.Security.SecurityException _:
                    return PyKinds.OSError;
                case ArgumentException _: return PyKinds.OSError | PyKinds.ValueError;   // a path .NET refuses (Python: OSError, ValueError for NUL)
                default: return PyKinds.None;
            }
        }

        /// <summary>FileNotFoundError.</summary>
        public static bool IsNotFound(Exception e) =>
            e is PyOSError o ? o.NotFound : e is FileNotFoundException || e is DirectoryNotFoundException || e is DriveNotFoundException;

        /// <summary>str(exception) as Python would give it.</summary>
        public static string ExceptionText(Exception e)
        {
            if (e is PyException) return e.Message;
            int code = Win32Code(e);
            if (code != 0) return "[WinError " + code + "] " + WindowsErrMessage(code);
            return e.Message;
        }

        /// <summary><c>error.strerror or error</c> of an OSError.</summary>
        public static string StrErrorOr(Exception e)
        {
            if (e is PyOSError o && !string.IsNullOrEmpty(o.StrError)) return o.StrError;
            int code = Win32Code(e);
            if (code != 0) return WindowsErrMessage(code);
            return ExceptionText(e);
        }

        static int Win32Code(Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException)) return 0;
            int h = Marshal.GetHRForException(e);
            if ((h & 0xFFFF0000) == unchecked((int)0x80070000)) return h & 0xFFFF;
            return e is UnauthorizedAccessException ? 5 : 0;
        }

        /// <summary>ctypes.WinError(code): the system text stripped of white space at both ends.</summary>
        public static PyOSError CtypesWinError(int code)
        {
            string text = FormatSystemMessage(code);
            string descr = text == null ? "<no description>" : Strip(text);
            return new PyOSError("[WinError " + code + "] " + descr, descr, code, NotFoundCode(code));
        }

        /// <summary>PyErr_SetFromWindowsErr(code) (os.stat, os.replace, mmap ...): trailing blanks and dots removed.</summary>
        public static PyOSError WindowsErr(int code, string filename = null, string filename2 = null)
        {
            string message = WindowsErrMessage(code);
            string text = "[WinError " + code + "] " + message;
            if (filename != null) text += ": " + Repr(filename) + (filename2 != null ? " -> " + Repr(filename2) : "");
            return new PyOSError(text, message, code, NotFoundCode(code));
        }

        static string WindowsErrMessage(int code)
        {
            string text = FormatSystemMessage(code);
            if (text == null) return "Windows Error 0x" + code.ToString("x", Inv);
            int n = text.Length;
            while (n > 0 && (text[n - 1] <= ' ' || text[n - 1] == '.')) n--;
            return text.Substring(0, n);
        }

        // winerror_to_errno(): the codes Python reports as FileNotFoundError
        static bool NotFoundCode(int code) =>
            code == 2 || code == 3 || code == 15 || code == 18 || code == 53 || code == 67 || code == 161 || code == 206;

        static string FormatSystemMessage(int code)
        {
            var buffer = new StringBuilder(2048);
            int n = Native.FormatMessageW(0x1000 | 0x200, IntPtr.Zero, code, 0x0400, buffer, buffer.Capacity, IntPtr.Zero);
            if (n == 0) return null;
            return buffer.ToString(0, Math.Min(n, buffer.Length));
        }

        // ------------------------------------------------------------ values (as json.loads gives them)

        public static bool IsNumber(object v) => v is long || v is int || v is double || v is bool;

        public static string TypeName(object v)
        {
            switch (v)
            {
                case null: return "NoneType";
                case bool _: return "bool";
                case long _: case int _: case short _: case byte _: return "int";
                case double _: case float _: return "float";
                case string _: return "str";
                case object[] _: return "tuple";
                case IDictionary _: return "dict";
                case IList _: return "list";
                default: return v.GetType().Name;
            }
        }

        public static bool Truthy(object v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case string s: return s.Length > 0;
                case long l: return l != 0;
                case int i: return i != 0;
                case double d: return d != 0.0;            // NaN is true, as in Python
                case ICollection c: return c.Count > 0;
                default: return true;
            }
        }

        /// <summary>Python's <c>a or b</c> (b is evaluated by the caller: use only where that has no effect).</summary>
        public static object Or(object a, object b) => Truthy(a) ? a : b;

        /// <summary>dict.get(key, fallback); AttributeError for anything that is not a dict.</summary>
        public static object Get(object dict, string key, object fallback = null)
        {
            if (dict is IDictionary<string, object> d) return d.TryGetValue(key, out object value) ? value : fallback;
            throw new PyAttributeError("'" + TypeName(dict) + "' object has no attribute 'get'");
        }

        /// <summary>container[key] with a str key.</summary>
        public static object Item(object container, string key)
        {
            switch (container)
            {
                case IDictionary<string, object> d:
                    if (d.TryGetValue(key, out object value)) return value;
                    throw new PyKeyError(Repr(key));
                case string _: throw new PyTypeError("string indices must be integers, not 'str'");
                case object[] _: throw new PyTypeError("tuple indices must be integers or slices, not str");
                case IList _: throw new PyTypeError("list indices must be integers or slices, not str");
                default: throw new PyTypeError("'" + TypeName(container) + "' object is not subscriptable");
            }
        }

        /// <summary>seq[:stop].</summary>
        public static object Slice(object seq, object stop)
        {
            switch (seq)
            {
                case string _: case IList _: break;
                case IDictionary _: throw new PyKeyError("slice(None, " + Repr(stop) + ", None)");
                default: throw new PyTypeError("'" + TypeName(seq) + "' object is not subscriptable");
            }
            int length = seq is string text ? text.Length : ((IList)seq).Count;
            long end;
            switch (stop)
            {
                case null: end = length; break;
                case bool b: end = b ? 1 : 0; break;
                case long l: end = l; break;
                case int i: end = i; break;
                default: throw new PyTypeError("slice indices must be integers or None or have an __index__ method");
            }
            if (end < 0) end = Math.Max(0, length + end);
            if (end > length) end = length;
            int count = (int)end;
            if (seq is string s) return s.Substring(0, count);
            if (seq is object[] tuple)
            {
                var part = new object[count];
                Array.Copy(tuple, part, count);
                return part;
            }
            var list = (IList)seq;
            var result = new List<object>(count);
            for (int k = 0; k < count; k++) result.Add(list[k]);
            return result;
        }

        /// <summary>The items <c>for x in value</c> goes through (taken at once, as iter() fails at once).</summary>
        public static List<object> Iter(object value)
        {
            var items = new List<object>();
            switch (value)
            {
                case string s:
                    for (int i = 0; i < s.Length; i++)
                    {
                        int width = char.IsSurrogatePair(s, i) ? 2 : 1;
                        items.Add(s.Substring(i, width));
                        i += width - 1;
                    }
                    return items;
                case IDictionary d:
                    foreach (object key in d.Keys) items.Add(key);
                    return items;
                case IList l:
                    foreach (object item in l) items.Add(item);
                    return items;
                default: throw new PyTypeError("'" + TypeName(value) + "' object is not iterable");
            }
        }

        /// <summary>Python's ==.</summary>
        public static bool Eq(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (IsNumber(a) && IsNumber(b)) return NumCompare(a, b) == 0;
            switch (a)
            {
                case string sa: return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
                case object[] ta: return b is object[] tb && SeqEq(ta, tb);
                case IDictionary da:
                    if (!(b is IDictionary db) || da.Count != db.Count) return false;
                    foreach (DictionaryEntry e in da)
                        if (!db.Contains(e.Key) || !Eq(e.Value, db[e.Key])) return false;
                    return true;
                case IList la: return b is IList lb && !(b is object[]) && SeqEq(la, lb);
            }
            return a.Equals(b);
        }

        static bool SeqEq(IList a, IList b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!Eq(a[i], b[i])) return false;
            return true;
        }

        static double AsDouble(object v) => v is double d ? d : v is bool b ? (b ? 1 : 0) : Convert.ToDouble(v, Inv);
        static long AsLong(object v) => v is bool b ? (b ? 1 : 0) : Convert.ToInt64(v, Inv);

        /// <summary>Exact comparison of two numbers (an int and a float compare exactly, as in Python); null with NaN.</summary>
        static int? NumCompare(object a, object b)
        {
            if (!(a is double) && !(b is double)) return AsLong(a).CompareTo(AsLong(b));
            if (a is double && b is double) { double x = (double)a, y = (double)b; return double.IsNaN(x) || double.IsNaN(y) ? (int?)null : x.CompareTo(y); }
            bool flip = a is double;
            long l = AsLong(flip ? b : a);
            double d = (double)(flip ? a : b);
            int? c = LongVersusDouble(l, d);
            return c == null ? null : flip ? -c : c;
        }

        static int? LongVersusDouble(long l, double d)
        {
            if (double.IsNaN(d)) return null;
            if (d >= 9223372036854775808.0) return -1;
            if (d < -9223372036854775808.0) return 1;
            double f = Math.Floor(d);
            long fl = (long)f;
            if (l != fl) return l < fl ? -1 : 1;
            return d > f ? -1 : 0;
        }

        static int? Order(object a, object b, string op)
        {
            if (IsNumber(a) && IsNumber(b)) return NumCompare(a, b);
            if (a is string sa && b is string sb) return CompareStr(sa, sb);
            if (a is IList la && b is IList lb && (a is object[]) == (b is object[]))
            {
                for (int i = 0; i < la.Count && i < lb.Count; i++)
                    if (!Eq(la[i], lb[i])) return Order(la[i], lb[i], op);
                return la.Count.CompareTo(lb.Count);
            }
            throw new PyTypeError("'" + op + "' not supported between instances of '" + TypeName(a) + "' and '" + TypeName(b) + "'");
        }

        public static bool Lt(object a, object b) => Order(a, b, "<") is int c && c < 0;
        public static bool Le(object a, object b) => Order(a, b, "<=") is int c && c <= 0;
        public static bool Gt(object a, object b) => Order(a, b, ">") is int c && c > 0;
        public static bool Ge(object a, object b) => Order(a, b, ">=") is int c && c >= 0;

        /// <summary>min(a, b): b only when b &lt; a.</summary>
        public static object Min(object a, object b) => Lt(b, a) ? b : a;

        /// <summary>value + 1 (Python's <c>x += 1</c>).</summary>
        public static object AddOne(object v)
        {
            switch (v)
            {
                case long l: return l + 1;
                case int i: return (long)i + 1;
                case bool b: return (b ? 1L : 0L) + 1;
                case double d: return d + 1.0;
                case string _: return Throw("can only concatenate str (not \"int\") to str");
                case object[] _: return Throw("can only concatenate tuple (not \"int\") to tuple");
                case IList _: return Throw("can only concatenate list (not \"int\") to list");
                default: return Throw("unsupported operand type(s) for +=: '" + TypeName(v) + "' and 'int'");
            }
        }

        static object Throw(string message) => throw new PyTypeError(message);

        /// <summary>Python's string order: by code points (not UTF-16 units).</summary>
        public static int CompareStr(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                char x = a[i], y = b[i];
                if (x == y) continue;
                if (x >= 0xD800 && y >= 0xD800)
                {
                    x = (char)(x >= 0xE000 ? x - 0x800 : x + 0x2000);
                    y = (char)(y >= 0xE000 ? y - 0x800 : y + 0x2000);
                }
                return x < y ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        public sealed class StrComparer : IComparer<string>
        {
            public static readonly StrComparer Instance = new StrComparer();
            public int Compare(string a, string b) => CompareStr(a, b);
        }

        // ------------------------------------------------------------ str and repr

        /// <summary>str(value).</summary>
        public static string Str(object v)
        {
            switch (v)
            {
                case null: return "None";
                case string s: return s;
                case bool b: return b ? "True" : "False";
                case long l: return l.ToString(Inv);
                case int i: return i.ToString(Inv);
                case double d: return FloatRepr(d);
                default: return Repr(v);
            }
        }

        /// <summary>repr(value) of the values the records hold (tuples are object[]).</summary>
        public static string Repr(object v)
        {
            switch (v)
            {
                case null: return "None";
                case string s: return StrRepr(s);
                case bool b: return b ? "True" : "False";
                case long l: return l.ToString(Inv);
                case int i: return i.ToString(Inv);
                case double d: return FloatRepr(d);
                case object[] tuple:
                    if (tuple.Length == 1) return "(" + Repr(tuple[0]) + ",)";
                    return "(" + JoinRepr(tuple) + ")";
                case IDictionary dict:
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(Repr(e.Key)).Append(": ").Append(Repr(e.Value));
                    }
                    return sb.Append('}').ToString();
                case IList list: return "[" + JoinRepr(list) + "]";
                default: return v.ToString();
            }
        }

        static string JoinRepr(IList items)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Repr(items[i]));
            }
            return sb.ToString();
        }

        /// <summary>repr(str): quotes and escapes as Python chooses them.</summary>
        public static string StrRepr(string s)
        {
            char quote = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
            var sb = new StringBuilder(s.Length + 2);
            sb.Append(quote);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == quote || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < ' ' || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
                else if (c < 0x7f) sb.Append(c);
                else
                {
                    int cp = c;
                    int width = 1;
                    if (char.IsSurrogatePair(s, i)) { cp = char.ConvertToUtf32(s, i); width = 2; }
                    if (Printable(s, i, cp)) sb.Append(s, i, width);
                    else if (cp <= 0xff) sb.Append("\\x").Append(cp.ToString("x2", Inv));
                    else if (cp <= 0xffff) sb.Append("\\u").Append(cp.ToString("x4", Inv));
                    else sb.Append("\\U").Append(cp.ToString("x8", Inv));
                    i += width - 1;
                }
            }
            return sb.Append(quote).ToString();
        }

        static bool Printable(string s, int i, int cp)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;      // a lone surrogate
            switch (CharUnicodeInfo.GetUnicodeCategory(s, i))
            {
                case UnicodeCategory.Control: case UnicodeCategory.Format: case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse: case UnicodeCategory.OtherNotAssigned: case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator: case UnicodeCategory.SpaceSeparator:
                    return false;
                default: return true;
            }
        }

        /// <summary>repr(float): the shortest digits that read back as the same double, laid out as Python does.</summary>
        public static string FloatRepr(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsPositiveInfinity(d)) return "inf";
            if (double.IsNegativeInfinity(d)) return "-inf";
            if (d == 0) return 1 / d < 0 ? "-0.0" : "0.0";
            if (Math.Abs(d) < 1e16 && Math.Floor(d) == d)          // a whole number below 10**16: its own digits
                return ((long)d).ToString(Inv) + ".0";
            string digits = ShortestDigits(Math.Abs(d), out int point);
            var sb = new StringBuilder();
            if (d < 0) sb.Append('-');
            if (point <= -4 || point > 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                int exponent = point - 1;
                sb.Append('e').Append(exponent < 0 ? '-' : '+').Append(Math.Abs(exponent).ToString("00", Inv));
            }
            else if (point <= 0) sb.Append("0.").Append('0', -point).Append(digits);
            else if (point >= digits.Length) sb.Append(digits).Append('0', point - digits.Length).Append(".0");
            else sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
            return sb.ToString();
        }

        /// <summary>Shortest round-trip digits of a positive finite double (Steele &amp; White / Burger &amp; Dybvig with
        /// exact integers; the last digit rounded half to even, as David Gay's dtoa that Python uses). value = 0.digits * 10**point.</summary>
        static string ShortestDigits(double v, out int point)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            int be = (int)((bits >> 52) & 0x7FF);
            ulong f = (ulong)(bits & 0xFFFFFFFFFFFFFL);
            int e;
            if (be == 0) e = -1074;
            else { f |= 1UL << 52; e = be - 1075; }
            bool even = (f & 1) == 0;
            bool lowerCloser = be > 1 && f == 1UL << 52;   // at a power of two the gap below is half the gap above
            BigNat r, s, mPlus, mMinus;
            if (e >= 0)
            {
                if (!lowerCloser) { r = BigNat.From(f).Shl(e + 1); s = BigNat.From(2); mPlus = BigNat.From(1).Shl(e); mMinus = BigNat.From(1).Shl(e); }
                else { r = BigNat.From(f).Shl(e + 2); s = BigNat.From(4); mPlus = BigNat.From(1).Shl(e + 1); mMinus = BigNat.From(1).Shl(e); }
            }
            else
            {
                if (!lowerCloser) { r = BigNat.From(f).Shl(1); s = BigNat.From(1).Shl(1 - e); mPlus = BigNat.From(1); mMinus = BigNat.From(1); }
                else { r = BigNat.From(f).Shl(2); s = BigNat.From(1).Shl(2 - e); mPlus = BigNat.From(2); mMinus = BigNat.From(1); }
            }
            int k = (int)Math.Ceiling(Math.Log10(v) - 1e-10);
            if (k >= 0) s = s.MulPow10(k);
            else { r = r.MulPow10(-k); mPlus = mPlus.MulPow10(-k); mMinus = mMinus.MulPow10(-k); }
            // fix the estimate: r + m+ must stay below s (or not reach it when the boundary itself is excluded)
            while (true)
            {
                int c = BigNat.Compare(r.Add(mPlus), s);
                if (c > 0 || (even && c == 0)) { s = s.MulSmall(10); k++; continue; }
                int c10 = BigNat.Compare(r.Add(mPlus).MulSmall(10), s);
                if (c10 < 0 || (!even && c10 == 0)) { r = r.MulSmall(10); mPlus = mPlus.MulSmall(10); mMinus = mMinus.MulSmall(10); k--; continue; }
                break;
            }
            var digits = new StringBuilder();
            while (true)
            {
                r = r.MulSmall(10); mPlus = mPlus.MulSmall(10); mMinus = mMinus.MulSmall(10);
                int digit = 0;
                while (BigNat.Compare(r, s) >= 0) { r = r.Sub(s); digit++; }
                int low = BigNat.Compare(r, mMinus);
                int high = BigNat.Compare(r.Add(mPlus), s);
                bool tc1 = even ? low <= 0 : low < 0;
                bool tc2 = even ? high >= 0 : high > 0;
                if (!tc1 && !tc2) { digits.Append((char)('0' + digit)); continue; }
                if (tc1 && tc2)
                {
                    int half = BigNat.Compare(r.MulSmall(2), s);
                    if (half > 0 || (half == 0 && (digit & 1) == 1)) digit++;
                }
                else if (tc2) digit++;
                digits.Append((char)('0' + digit));
                break;
            }
            // (a digit is rounded up only while r + m+ stayed below s, so it never becomes 10)
            point = k;
            return digits.ToString().TrimEnd('0');
        }

        /// <summary>A minimal unsigned big integer for ShortestDigits (immutable).</summary>
        sealed class BigNat
        {
            readonly uint[] w;     // little-endian 32-bit words, no leading zero words
            BigNat(uint[] words) { w = words; }

            static BigNat Make(uint[] words, int length)
            {
                while (length > 0 && words[length - 1] == 0) length--;
                if (length == words.Length) return new BigNat(words);
                var trimmed = new uint[length];
                Array.Copy(words, trimmed, length);
                return new BigNat(trimmed);
            }

            public static BigNat From(ulong value) => Make(new[] { (uint)value, (uint)(value >> 32) }, 2);

            public BigNat Shl(int bits)
            {
                int words = bits / 32, shift = bits % 32;
                var result = new uint[w.Length + words + 1];
                for (int i = 0; i < w.Length; i++)
                {
                    ulong x = (ulong)w[i] << shift;
                    result[i + words] |= (uint)x;
                    result[i + words + 1] |= (uint)(x >> 32);
                }
                return Make(result, result.Length);
            }

            public BigNat MulSmall(uint factor)
            {
                var result = new uint[w.Length + 1];
                ulong carry = 0;
                for (int i = 0; i < w.Length; i++)
                {
                    ulong x = (ulong)w[i] * factor + carry;
                    result[i] = (uint)x;
                    carry = x >> 32;
                }
                result[w.Length] = (uint)carry;
                return Make(result, result.Length);
            }

            public BigNat MulPow10(int n)
            {
                BigNat x = this;
                for (; n >= 9; n -= 9) x = x.MulSmall(1000000000);
                for (; n > 0; n--) x = x.MulSmall(10);
                return x;
            }

            public BigNat Add(BigNat other)
            {
                int n = Math.Max(w.Length, other.w.Length);
                var result = new uint[n + 1];
                ulong carry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong x = (ulong)(i < w.Length ? w[i] : 0) + (i < other.w.Length ? other.w[i] : 0) + carry;
                    result[i] = (uint)x;
                    carry = x >> 32;
                }
                result[n] = (uint)carry;
                return Make(result, result.Length);
            }

            public BigNat Sub(BigNat other)            // this >= other
            {
                var result = new uint[w.Length];
                long borrow = 0;
                for (int i = 0; i < w.Length; i++)
                {
                    long x = (long)w[i] - (i < other.w.Length ? other.w[i] : 0) - borrow;
                    borrow = x < 0 ? 1 : 0;
                    result[i] = (uint)(x + (borrow << 32));
                }
                return Make(result, result.Length);
            }

            public static int Compare(BigNat a, BigNat b)
            {
                if (a.w.Length != b.w.Length) return a.w.Length < b.w.Length ? -1 : 1;
                for (int i = a.w.Length - 1; i >= 0; i--)
                    if (a.w[i] != b.w[i]) return a.w[i] < b.w[i] ? -1 : 1;
                return 0;
            }
        }

        // ------------------------------------------------------------ text

        // str.isspace(): what str.strip() removes and what \s matches in Python's re
        static readonly char[] SpaceChars =
        {
            (char)0x09, (char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x1C, (char)0x1D, (char)0x1E, (char)0x1F, (char)0x20,
            (char)0x85, (char)0xA0, (char)0x1680, (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004, (char)0x2005,
            (char)0x2006, (char)0x2007, (char)0x2008, (char)0x2009, (char)0x200A, (char)0x2028, (char)0x2029, (char)0x202F, (char)0x205F,
            (char)0x3000,
        };
        static readonly string Space = new string(SpaceChars);

        /// <summary>The regex class for Python's \s (and, negated, \S): regex escapes, the source stays ASCII.</summary>
        public const string SpaceClass = @"\t\n\v\f\r\x1c-\x1f \x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000";

        public static bool IsSpace(char c) => Space.IndexOf(c) >= 0;

        /// <summary>str.strip().</summary>
        public static string Strip(string s) => s.Trim(SpaceChars);

        /// <summary>value.strip() of a value that should be a str (AttributeError otherwise).</summary>
        public static string StripValue(object value)
        {
            if (value is string s) return Strip(s);
            throw new PyAttributeError("'" + TypeName(value) + "' object has no attribute 'strip'");
        }

        /// <summary><c>(value or "").strip()</c>.</summary>
        public static string OrEmptyStrip(object value) => StripValue(Truthy(value) ? value : "");

        /// <summary>str.casefold() for the names compared here (program files, folders, tables, fields).</summary>
        public static string Casefold(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case (char)0x00DF: case (char)0x1E9E: sb.Append("ss"); break;
                    case (char)0x03C2: sb.Append((char)0x03C3); break;
                    case (char)0x00B5: sb.Append((char)0x03BC); break;
                    case (char)0x017F: sb.Append('s'); break;
                    default: sb.Append(char.ToLowerInvariant(c)); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>len(str): code points.</summary>
        public static int Len(string s)
        {
            int n = s.Length;
            for (int i = 0; i + 1 < s.Length; i++)
                if (char.IsSurrogatePair(s[i], s[i + 1])) { n--; i++; }
            return n;
        }

        /// <summary>int() of the digits a regex \d matched (any Unicode decimal digits).</summary>
        public static long ParseInt(string digits)
        {
            long value = 0;
            foreach (char c in digits) value = checked(value * 10 + CharUnicodeInfo.GetDecimalDigitValue(c));
            return value;
        }

        /// <summary>Digits a regex \d+ matched, as a key that orders like int() of them.</summary>
        public static string NumericKey(string digits)
        {
            var sb = new StringBuilder(digits.Length);
            foreach (char c in digits) sb.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(c)));
            string plain = sb.ToString().TrimStart('0');
            return plain.Length.ToString("D10", Inv) + plain;
        }

        /// <summary>bytes.decode("gb18030", "replace"), exactly as CPython's decoder (Modules/cjkcodecs/_codecs_cn.c with
        /// multibytecodec's error handling) goes through the bytes: an illegal sequence is replaced by U+FFFD and only its
        /// first byte is skipped; a sequence cut short by the end replaces all the remaining bytes with one U+FFFD. The
        /// characters themselves come from Windows' GB18030 tables, which give the same as Python's for every two-byte
        /// sequence and every four-byte one of the BMP (checked one by one); the other planes are computed.</summary>
        public static string DecodeGb18030(byte[] data, int offset, int count)
        {
            const char Replacement = (char)0xFFFD;
            var sb = new StringBuilder(count);
            int i = offset, end = offset + count;
            while (i < end)
            {
                int c = data[i];
                if (c < 0x80)
                {
                    sb.Append((char)c);
                    i++;
                    continue;
                }
                if (end - i < 2) { sb.Append(Replacement); break; }                 // incomplete multibyte sequence
                int c2 = data[i + 1];
                if (c2 >= 0x30 && c2 <= 0x39)                                         // four bytes
                {
                    if (end - i < 4) { sb.Append(Replacement); break; }
                    int c3 = data[i + 2], c4 = data[i + 3];
                    if (c >= 0x81 && c <= 0xFE && c3 >= 0x81 && c3 <= 0xFE && c4 >= 0x30 && c4 <= 0x39)
                    {
                        int b1 = c - 0x81, b2 = c2 - 0x30, b3 = c3 - 0x81, b4 = c4 - 0x30;
                        if (b1 < 4)
                        {
                            int lseq = (b1 * 10 + b2) * 1260 + b3 * 10 + b4;
                            if (lseq < 39420)
                            {
                                sb.Append(Gb18030Tables.FourByte[lseq]);
                                i += 4;
                                continue;
                            }
                        }
                        else if (b1 >= 15)
                        {
                            int lseq = 0x10000 + ((b1 - 15) * 10 + b2) * 1260 + b3 * 10 + b4;
                            if (lseq <= 0x10FFFF)
                            {
                                sb.Append(char.ConvertFromUtf32(lseq));
                                i += 4;
                                continue;
                            }
                        }
                    }
                    sb.Append(Replacement);                                           // illegal: skip one byte
                    i++;
                    continue;
                }
                char two = c >= 0x81 && c <= 0xFE && c2 >= 0x40 && c2 <= 0xFE ? Gb18030Tables.TwoByte[(c - 0x81) * 191 + (c2 - 0x40)] : '\0';
                if (two == '\0')
                {
                    sb.Append(Replacement);
                    i++;
                    continue;
                }
                sb.Append(two);
                i += 2;
            }
            return sb.ToString();
        }

        /// <summary>The PC's ANSI code page (GetACP): Omega, a program of that code page, writes its text in it. Tests
        /// may set another.</summary>
        public static int AnsiCodePage = AnsiCodePageOfThisPc();

        static int AnsiCodePageOfThisPc()
        {
            try { return GetACP(); }
            catch (Exception) { return 936; }
        }

        [DllImport("kernel32.dll")]
        static extern int GetACP();

        /// <summary>Text Omega wrote (its tables, settings, names): in the PC's ANSI code page. On a Chinese PC (936)
        /// as GB18030, exactly as the Python programs decoded it; on another, in that code page (Western Europe: 1252,
        /// where "\u00b5mol" must not turn into Chinese).</summary>
        public static string DecodeAnsi(byte[] data, int offset, int count)
        {
            int page = AnsiCodePage;
            if (page == 936 || page == 54936) return DecodeGb18030(data, offset, count);
            try
            {
                Encoding codec = Encoding.GetEncoding(page, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
                return codec.GetString(data, offset, count);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
            {
                return DecodeGb18030(data, offset, count);
            }
        }

        static class Gb18030Tables
        {
            /// <summary>Lead 0x81-0xFE, trail 0x40-0xFE: the character, '\0' where there is none.</summary>
            public static readonly char[] TwoByte = new char[126 * 191];
            /// <summary>The four-byte sequences of the BMP, by their linear number.</summary>
            public static readonly char[] FourByte = new char[39420];

            static Gb18030Tables()
            {
                var codec = Encoding.GetEncoding(54936, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("?"));
                var pair = new byte[2];
                for (int c = 0x81; c <= 0xFE; c++)
                    for (int c2 = 0x40; c2 <= 0xFE; c2++)
                    {
                        pair[0] = (byte)c;
                        pair[1] = (byte)c2;
                        string s = c2 == 0x7F ? "" : codec.GetString(pair);
                        TwoByte[(c - 0x81) * 191 + (c2 - 0x40)] = s.Length == 1 && s[0] != '?' ? s[0] : '\0';
                    }
                var four = new byte[4];
                for (int lseq = 0; lseq < FourByte.Length; lseq++)
                {
                    four[0] = (byte)(0x81 + lseq / 12600);
                    four[1] = (byte)(0x30 + lseq / 1260 % 10);
                    four[2] = (byte)(0x81 + lseq / 10 % 126);
                    four[3] = (byte)(0x30 + lseq % 10);
                    string s = codec.GetString(four);
                    FourByte[lseq] = s.Length == 1 ? s[0] : (char)0xFFFD;
                }
            }
        }

        /// <summary>bytes.decode("latin-1").</summary>
        public static string DecodeLatin1(byte[] data, int offset, int count)
        {
            var chars = new char[count];
            for (int i = 0; i < count; i++) chars[i] = (char)data[offset + i];
            return new string(chars);
        }

        static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        // ------------------------------------------------------------ files

        /// <summary>The whole file, opened with full sharing (never in Omega's way).</summary>
        public static byte[] ReadBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        /// <summary>Path.read_text("utf-8") (strict: undecodable bytes are a ValueError).</summary>
        public static string ReadText(string path) => Utf8Strict.GetString(ReadBytes(path));

        /// <summary>Path.write_text(text, "utf-8").</summary>
        public static void WriteText(string path, string text)
        {
            byte[] bytes = Utf8Strict.GetBytes(text);
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                int code = Win32Code(e);
                if (e is UnauthorizedAccessException || code == 5 || code == 32 || code == 33)
                    throw new PyOSError("[Errno 13] Permission denied: " + Repr(path), "Permission denied", code, false, e);
                if (code != 0)
                {
                    PyOSError error = WindowsErr(code, path);
                    throw new PyOSError(error.Message, error.StrError, code, error.NotFound, e);
                }
                throw new PyOSError(e.Message + ": " + Repr(path), e.Message, 0, IsNotFound(e), e);
            }
        }

        /// <summary>os.replace(source, target): MoveFileExW with MOVEFILE_REPLACE_EXISTING.</summary>
        public static void Replace(string source, string target)
        {
            if (!Native.MoveFileExW(source, target, 1)) throw WindowsErr(Marshal.GetLastWin32Error(), source, target);
        }

        public struct StatResult
        {
            public long MtimeNs;      // st_mtime_ns
            public double Mtime;      // st_mtime
            public long Size;         // st_size
            public bool IsDirectory;
        }

        /// <summary>os.stat(path) (the fields used here).</summary>
        public static StatResult Stat(string path)
        {
            if (!Native.GetFileAttributesExW(path, 0, out Native.FileAttributeData data))
                throw WindowsErr(Marshal.GetLastWin32Error(), path);
            long fileTime = ((long)data.WriteHigh << 32) | data.WriteLow;
            long nsec = fileTime % 10000000 * 100;
            long sec = fileTime / 10000000 - 11644473600L;
            return new StatResult
            {
                MtimeNs = sec * 1000000000L + nsec,
                Mtime = sec + nsec * 1e-9,
                Size = ((long)data.SizeHigh << 32) | data.SizeLow,
                IsDirectory = (data.Attributes & 0x10) != 0,
            };
        }

        /// <summary>Path(folder).glob(prefix + "*" + suffix): the entries (files and folders) whose names match,
        /// ignoring case as Python does on Windows, in the order the file system lists them. Nothing when the folder
        /// cannot be listed.</summary>
        public static List<string> Glob(string folder, string prefix, string suffix)
        {
            var found = new List<string>();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder); }
            catch (Exception e) when (Is(e, PyKinds.OSError)) { return found; }
            try
            {
                foreach (string path in entries)
                {
                    string name = Path.GetFileName(path);
                    if (name.Length >= prefix.Length + suffix.Length
                        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        found.Add(path);
                }
            }
            catch (Exception e) when (Is(e, PyKinds.OSError)) { }
            return found;
        }

        // ------------------------------------------------------------ hashlib, zlib

        /// <summary>hashlib.blake2b(data, digest_size=size).hexdigest().</summary>
        public static string Blake2bHex(byte[] data, int size) => Hex(new Blake2b(size).Update(data, 0, data.Length).Digest());

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", Inv));
            return sb.ToString();
        }

        static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int FormatMessageW(int flags, IntPtr source, int messageId, int languageId, StringBuilder buffer, int size, IntPtr arguments);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool MoveFileExW(string existing, string target, int flags);

            [StructLayout(LayoutKind.Sequential)]
            public struct FileAttributeData
            {
                public uint Attributes;
                public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
                public uint SizeHigh, SizeLow;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetFileAttributesExW(string name, int level, out FileAttributeData data);
        }
    }

    /// <summary>BLAKE2b (RFC 7693), unkeyed, as hashlib.blake2b(digest_size=n).</summary>
    public sealed class Blake2b
    {
        static readonly ulong[] IV =
        {
            0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
            0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
        };

        static readonly byte[][] Sigma =
        {
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            new byte[] { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
            new byte[] { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
            new byte[] { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
            new byte[] { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
            new byte[] { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
            new byte[] { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
            new byte[] { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
            new byte[] { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
            new byte[] { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
            new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            new byte[] { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        };

        readonly ulong[] h = new ulong[8];
        readonly ulong[] v = new ulong[16];
        readonly ulong[] m = new ulong[16];
        readonly byte[] block = new byte[128];
        readonly int size;
        int filled;
        ulong t0, t1;

        public Blake2b(int digestSize)
        {
            if (digestSize < 1 || digestSize > 64) throw new ArgumentOutOfRangeException(nameof(digestSize));
            size = digestSize;
            Array.Copy(IV, h, 8);
            h[0] ^= 0x01010000UL ^ (ulong)digestSize;
        }

        public Blake2b Update(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                if (filled == 128)                       // a full block is compressed once more data follows it
                {
                    Count(128);
                    Compress(false);
                    filled = 0;
                }
                int n = Math.Min(128 - filled, count);
                Buffer.BlockCopy(data, offset, block, filled, n);
                filled += n;
                offset += n;
                count -= n;
            }
            return this;
        }

        public Blake2b Update(byte[] data) => Update(data, 0, data.Length);

        public byte[] Digest()
        {
            Count((ulong)filled);
            Array.Clear(block, filled, 128 - filled);
            Compress(true);
            var output = new byte[size];
            for (int i = 0; i < size; i++) output[i] = (byte)(h[i >> 3] >> (8 * (i & 7)));
            return output;
        }

        public string HexDigest() => Py.Hex(Digest());

        void Count(ulong n)
        {
            t0 += n;
            if (t0 < n) t1++;
        }

        void Compress(bool last)
        {
            for (int i = 0; i < 16; i++) m[i] = BitConverter.ToUInt64(block, i * 8);
            for (int i = 0; i < 8; i++) { v[i] = h[i]; v[i + 8] = IV[i]; }
            v[12] ^= t0;
            v[13] ^= t1;
            if (last) v[14] = ~v[14];
            for (int round = 0; round < 12; round++)
            {
                byte[] s = Sigma[round];
                G(0, 4, 8, 12, m[s[0]], m[s[1]]);
                G(1, 5, 9, 13, m[s[2]], m[s[3]]);
                G(2, 6, 10, 14, m[s[4]], m[s[5]]);
                G(3, 7, 11, 15, m[s[6]], m[s[7]]);
                G(0, 5, 10, 15, m[s[8]], m[s[9]]);
                G(1, 6, 11, 12, m[s[10]], m[s[11]]);
                G(2, 7, 8, 13, m[s[12]], m[s[13]]);
                G(3, 4, 9, 14, m[s[14]], m[s[15]]);
            }
            for (int i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
        }

        void G(int a, int b, int c, int d, ulong x, ulong y)
        {
            v[a] = v[a] + v[b] + x;
            v[d] = Rotr(v[d] ^ v[a], 32);
            v[c] = v[c] + v[d];
            v[b] = Rotr(v[b] ^ v[c], 24);
            v[a] = v[a] + v[b] + y;
            v[d] = Rotr(v[d] ^ v[a], 16);
            v[c] = v[c] + v[d];
            v[b] = Rotr(v[b] ^ v[c], 63);
        }

        static ulong Rotr(ulong x, int n) => (x >> n) | (x << (64 - n));
    }

    /// <summary>zlib.decompress(data) (zlib header, deflate, Adler-32; data after the stream is ignored, as zlib does).
    /// A port of zlib's puff.c, with zlib's own checks. Throws ZlibError where zlib.decompress raises zlib.error.</summary>
    public static class Zlib
    {
        public sealed class ZlibError : Exception
        {
            public ZlibError(string message) : base(message) { }
        }

        /// <summary>The output grew past `limit` (the caller knows it cannot be right; zlib would go on).</summary>
        public sealed class TooLong : Exception { }

        sealed class State
        {
            public byte[] Input;
            public int InPos;
            public int BitBuf, BitCnt;
            public byte[] Output = new byte[4096];
            public int OutCnt;
            public long Limit;
        }

        sealed class Huffman
        {
            public readonly short[] Count = new short[16];
            public readonly short[] Symbol;
            public Huffman(int symbols) { Symbol = new short[symbols]; }
        }

        static readonly short[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly short[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly short[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly short[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly short[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        public static byte[] Decompress(byte[] data, long limit = long.MaxValue)
        {
            if (data.Length < 2) throw new ZlibError("incomplete or truncated stream");
            int cmf = data[0], flg = data[1];
            if (((cmf << 8) + flg) % 31 != 0) throw new ZlibError("incorrect header check");
            if ((cmf & 0x0F) != 8) throw new ZlibError("unknown compression method");
            if ((cmf >> 4) > 7) throw new ZlibError("invalid window size");
            if ((flg & 0x20) != 0) throw new ZlibError("need dictionary");
            var s = new State { Input = data, InPos = 2, Limit = limit };
            bool last;
            do
            {
                last = Bits(s, 1) == 1;
                int type = Bits(s, 2);
                if (type == 0) Stored(s);
                else if (type == 1) Fixed(s);
                else if (type == 2) Dynamic(s);
                else throw new ZlibError("invalid block type");
            } while (!last);
            s.BitBuf = 0;                                        // the check value starts at the next byte
            s.BitCnt = 0;
            if (s.InPos + 4 > data.Length) throw new ZlibError("incomplete or truncated stream");
            uint expected = (uint)(data[s.InPos] << 24 | data[s.InPos + 1] << 16 | data[s.InPos + 2] << 8 | data[s.InPos + 3]);
            byte[] output = new byte[s.OutCnt];
            Buffer.BlockCopy(s.Output, 0, output, 0, s.OutCnt);
            if (Adler32(output) != expected) throw new ZlibError("incorrect data check");
            return output;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int end = Math.Min(data.Length, i + 5552);
                for (; i < end; i++) { a += data[i]; b += a; }
                a %= 65521;
                b %= 65521;
            }
            return b << 16 | a;
        }

        static int Bits(State s, int need)
        {
            int value = s.BitBuf;
            while (s.BitCnt < need)
            {
                if (s.InPos >= s.Input.Length) throw new ZlibError("incomplete or truncated stream");
                value |= s.Input[s.InPos++] << s.BitCnt;
                s.BitCnt += 8;
            }
            s.BitBuf = value >> need;
            s.BitCnt -= need;
            return value & ((1 << need) - 1);
        }

        static void Put(State s, byte b)
        {
            if (s.OutCnt >= s.Limit) throw new TooLong();
            if (s.OutCnt == s.Output.Length) Array.Resize(ref s.Output, s.Output.Length * 2);
            s.Output[s.OutCnt++] = b;
        }

        static void Stored(State s)
        {
            s.BitBuf = 0;
            s.BitCnt = 0;
            if (s.InPos + 4 > s.Input.Length) throw new ZlibError("incomplete or truncated stream");
            int len = s.Input[s.InPos] | s.Input[s.InPos + 1] << 8;
            int nlen = s.Input[s.InPos + 2] | s.Input[s.InPos + 3] << 8;
            s.InPos += 4;
            if (len != (~nlen & 0xffff)) throw new ZlibError("invalid stored block lengths");
            if (s.InPos + len > s.Input.Length) throw new ZlibError("incomplete or truncated stream");
            for (int i = 0; i < len; i++) Put(s, s.Input[s.InPos++]);
        }

        static int Decode(State s, Huffman h)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                code |= Bits(s, 1);
                int count = h.Count[len];
                if (code - count < first) return h.Symbol[index + (code - first)];
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            return -1;                                           // a code that the table does not have
        }

        static int Construct(Huffman h, short[] length, int offset, int n)
        {
            for (int len = 0; len <= 15; len++) h.Count[len] = 0;
            for (int symbol = 0; symbol < n; symbol++) h.Count[length[offset + symbol]]++;
            if (h.Count[0] == n) return 0;                      // no codes: complete, but decoding fails
            int left = 1;
            for (int len = 1; len <= 15; len++)
            {
                left <<= 1;
                left -= h.Count[len];
                if (left < 0) return left;                       // over-subscribed
            }
            var offs = new short[16];
            for (int len = 1; len < 15; len++) offs[len + 1] = (short)(offs[len] + h.Count[len]);
            for (int symbol = 0; symbol < n; symbol++)
                if (length[offset + symbol] != 0) h.Symbol[offs[length[offset + symbol]]++] = (short)symbol;
            return left;                                         // 0: complete; more: incomplete
        }

        static void Codes(State s, Huffman lencode, Huffman distcode)
        {
            int symbol;
            do
            {
                symbol = Decode(s, lencode);
                if (symbol < 0) throw new ZlibError("invalid literal/length code");
                if (symbol < 256) Put(s, (byte)symbol);
                else if (symbol > 256)
                {
                    symbol -= 257;
                    if (symbol >= 29) throw new ZlibError("invalid literal/length code");
                    int len = LengthBase[symbol] + Bits(s, LengthExtra[symbol]);
                    symbol = Decode(s, distcode);
                    if (symbol < 0 || symbol >= 30) throw new ZlibError("invalid distance code");
                    int dist = DistBase[symbol] + Bits(s, DistExtra[symbol]);
                    if (dist > s.OutCnt) throw new ZlibError("invalid distance too far back");
                    for (; len > 0; len--) Put(s, s.Output[s.OutCnt - dist]);
                    symbol = 0;
                }
            } while (symbol != 256);
        }

        static Huffman fixedLength, fixedDistance;

        static void Fixed(State s)
        {
            if (fixedLength == null)
            {
                var lengths = new short[288];
                for (int symbol = 0; symbol < 144; symbol++) lengths[symbol] = 8;
                for (int symbol = 144; symbol < 256; symbol++) lengths[symbol] = 9;
                for (int symbol = 256; symbol < 280; symbol++) lengths[symbol] = 7;
                for (int symbol = 280; symbol < 288; symbol++) lengths[symbol] = 8;
                var lencode = new Huffman(288);
                Construct(lencode, lengths, 0, 288);
                var distLengths = new short[30];
                for (int symbol = 0; symbol < 30; symbol++) distLengths[symbol] = 5;
                var distcode = new Huffman(30);
                Construct(distcode, distLengths, 0, 30);
                fixedDistance = distcode;
                fixedLength = lencode;
            }
            Codes(s, fixedLength, fixedDistance);
        }

        static void Dynamic(State s)
        {
            var lengths = new short[316];
            int nlen = Bits(s, 5) + 257;
            int ndist = Bits(s, 5) + 1;
            int ncode = Bits(s, 4) + 4;
            if (nlen > 286 || ndist > 30) throw new ZlibError("too many length or distance symbols");
            int index;
            for (index = 0; index < ncode; index++) lengths[CodeOrder[index]] = (short)Bits(s, 3);
            for (; index < 19; index++) lengths[CodeOrder[index]] = 0;
            var lencode = new Huffman(286);
            var distcode = new Huffman(30);
            if (Construct(lencode, lengths, 0, 19) != 0) throw new ZlibError("invalid code lengths set");
            index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(s, lencode);
                if (symbol < 0) throw new ZlibError("invalid code lengths set");
                if (symbol < 16) lengths[index++] = (short)symbol;
                else
                {
                    short len = 0;
                    if (symbol == 16)
                    {
                        if (index == 0) throw new ZlibError("invalid bit length repeat");
                        len = lengths[index - 1];
                        symbol = 3 + Bits(s, 2);
                    }
                    else if (symbol == 17) symbol = 3 + Bits(s, 3);
                    else symbol = 11 + Bits(s, 7);
                    if (index + symbol > nlen + ndist) throw new ZlibError("invalid bit length repeat");
                    while (symbol-- > 0) lengths[index++] = len;
                }
            }
            if (lengths[256] == 0) throw new ZlibError("invalid code -- missing end-of-block");
            int err = Construct(lencode, lengths, 0, nlen);
            if (err != 0 && (err < 0 || nlen != lencode.Count[0] + lencode.Count[1])) throw new ZlibError("invalid literal/lengths set");
            err = Construct(distcode, lengths, nlen, ndist);
            if (err != 0 && (err < 0 || ndist != distcode.Count[0] + distcode.Count[1])) throw new ZlibError("invalid distances set");
            Codes(s, lencode, distcode);
        }
    }

    /// <summary>The datetime and time conversions the Python code used.</summary>
    public static class PyTime
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>time.time().</summary>
        public static double Time() => (DateTime.UtcNow.Ticks - Epoch.Ticks) / 1e7;

        /// <summary>time.monotonic() (seconds since the PC started, as Python's on Windows).</summary>
        public static double Monotonic() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>datetime.fromtimestamp(seconds).replace(microsecond=0): local time in whole seconds (the
        /// microseconds rounded half to even first, as Python does, which can carry into the next second).</summary>
        public static DateTime FromTimestamp(double seconds)
        {
            double whole = Math.Truncate(seconds);
            double fraction = seconds - whole;
            double micro = RoundHalfEven(fraction * 1e6);
            if (micro >= 1e6) whole += 1;
            else if (micro < 0) whole -= 1;
            return Epoch.AddTicks((long)whole * 10000000L).ToLocalTime();
        }

        static double RoundHalfEven(double x)
        {
            double rounded = Math.Round(x, MidpointRounding.AwayFromZero);
            if (Math.Abs(x - rounded) == 0.5) rounded = 2.0 * Math.Round(x / 2.0, MidpointRounding.AwayFromZero);
            return rounded;
        }

        /// <summary>datetime.isoformat() of a time without microseconds: "2026-11-16T00:05:54".</summary>
        public static string Iso(DateTime t) => t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", Inv);

        /// <summary>datetime.now().replace(microsecond=0).isoformat().</summary>
        public static string Now() => Iso(DateTime.Now);

        /// <summary>.timestamp() of a naive local datetime with microseconds.</summary>
        static double LocalTimestamp(int year, int month, int day, int hour, int minute, int second, int micro)
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            long whole = (local.ToUniversalTime().Ticks - Epoch.Ticks) / 10000000L;
            return whole + micro / 1e6;
        }

        static readonly Regex StrptimeFormat = new Regex(
            @"\G(\d\d\d\d)/(1[0-2]|0[1-9]|[1-9])/(3[01]|[12]\d|0[1-9]|[1-9]| [1-9])[" + Py.SpaceClass + @"]+(2[0-3]|[0-1]\d|\d):([0-5]\d|\d):(6[0-1]|[0-5]\d|\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>datetime.strptime(text, "%Y/%m/%d %H:%M:%S").timestamp(); ValueError as strptime raises it.</summary>
        public static double StrptimeTimestamp(string text)
        {
            Match found = StrptimeFormat.Match(text);
            if (!found.Success)
                throw new PyValueError("time data " + Py.Repr(text) + " does not match format '%Y/%m/%d %H:%M:%S'");
            if (found.Length != text.Length) throw new PyValueError("unconverted data remains: " + text.Substring(found.Length));
            int year = (int)Py.ParseInt(found.Groups[1].Value), month = (int)Py.ParseInt(found.Groups[2].Value);
            int day = (int)Py.ParseInt(Py.Strip(found.Groups[3].Value)), hour = (int)Py.ParseInt(found.Groups[4].Value);
            int minute = (int)Py.ParseInt(found.Groups[5].Value), second = (int)Py.ParseInt(found.Groups[6].Value);
            if (year < 1) throw new PyValueError("year " + year + " is out of range");
            if (day > DateTime.DaysInMonth(year, month)) throw new PyValueError("day is out of range for month");
            if (second > 59) throw new PyValueError("second must be in 0..59");
            return LocalTimestamp(year, month, day, hour, minute, second, 0);
        }

        static readonly Regex OmegaDate = new Regex(@"^\D*(\d{4})\D+(\d{1,2})\D+(\d{1,2})\D*$", RegexOptions.CultureInvariant);
        static readonly Regex OmegaClock = new Regex(@"^\D*(\d{1,2})\D+(\d{1,2})(?:\D+(\d{1,2}))?\D*$", RegexOptions.CultureInvariant);

        /// <summary>.timestamp() of the Date and Time of a row of Omega's run table. Omega writes them with Delphi's
        /// FormatDateTime('yyyy/mm/dd', 'hh:nn:ss'), which puts in the separators of the PC's regional settings:
        /// "2026/11/16", "2007.07.05", "2012-02-29"; so any separator is taken (year, month, day; hour, minute,
        /// second). ValueError when they are not such a date and time, TypeError when they are not text.</summary>
        public static double OmegaTimestamp(object date, object time)
        {
            if (!(date is string dateText) || !(time is string timeText)) throw new PyTypeError("the date and the time of a run must be text");
            Match day = OmegaDate.Match(dateText), clock = OmegaClock.Match(timeText);
            if (!day.Success || !clock.Success)
                throw new PyValueError("time data " + Py.Repr(dateText + " " + timeText) + " is not a date and a time");
            int year = (int)Py.ParseInt(day.Groups[1].Value), month = (int)Py.ParseInt(day.Groups[2].Value), dayOfMonth = (int)Py.ParseInt(day.Groups[3].Value);
            int hour = (int)Py.ParseInt(clock.Groups[1].Value), minute = (int)Py.ParseInt(clock.Groups[2].Value);
            int second = clock.Groups[3].Success ? (int)Py.ParseInt(clock.Groups[3].Value) : 0;
            if (year < 1 || month < 1 || month > 12) throw new PyValueError("month must be in 1..12");
            if (dayOfMonth < 1 || dayOfMonth > DateTime.DaysInMonth(year, month)) throw new PyValueError("day is out of range for month");
            if (hour > 23 || minute > 59 || second > 59) throw new PyValueError("the time is out of range");
            return LocalTimestamp(year, month, dayOfMonth, hour, minute, second, 0);
        }

        /// <summary>datetime.fromisoformat(value).timestamp() for the forms records hold ("2026-11-16T00:05:54", with
        /// any separator, optional minutes/seconds/fraction and offset; also the basic forms without '-' and ':').</summary>
        public static double FromIsoformatTimestamp(object value)
        {
            if (!(value is string text)) throw new PyTypeError("fromisoformat: argument must be str");
            var invalid = new PyValueError("Invalid isoformat string: " + Py.Repr(text));
            int pos;
            int year, month, day;
            if (text.Length >= 10 && text[4] == '-' && text[7] == '-' && Digits(text, 0, 4) && Digits(text, 5, 2) && Digits(text, 8, 2))
            {
                year = Num(text, 0, 4); month = Num(text, 5, 2); day = Num(text, 8, 2); pos = 10;
            }
            else if (text.Length >= 8 && Digits(text, 0, 8))
            {
                year = Num(text, 0, 4); month = Num(text, 4, 2); day = Num(text, 6, 2); pos = 8;
            }
            else throw invalid;
            if (year < 1) throw new PyValueError("year " + year + " is out of range");
            if (month < 1 || month > 12) throw new PyValueError("month must be in 1..12");
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) throw new PyValueError("day is out of range for month");
            int hour = 0, minute = 0, second = 0, micro = 0;
            long? offsetSeconds = null;
            if (pos < text.Length)
            {
                pos++;                                       // the separator: any one character
                string time = text.Substring(pos);
                int zone = time.IndexOfAny(new[] { '+', '-', 'Z' });
                string clock = zone >= 0 ? time.Substring(0, zone) : time;
                if (!ParseClock(clock, out hour, out minute, out second, out micro)) throw invalid;
                if (zone >= 0)
                {
                    string tz = time.Substring(zone);
                    if (tz == "Z") offsetSeconds = 0;
                    else
                    {
                        if (!ParseClock(tz.Substring(1), out int oh, out int om, out int os, out int ou) || ou != 0 || tz.Length < 3) throw invalid;
                        long seconds = oh * 3600L + om * 60L + os;
                        offsetSeconds = tz[0] == '-' ? -seconds : seconds;
                    }
                }
                if (hour > 23) throw new PyValueError("hour must be in 0..23");
                if (minute > 59) throw new PyValueError("minute must be in 0..59");
                if (second > 59) throw new PyValueError("second must be in 0..59");
            }
            if (offsetSeconds == null) return LocalTimestamp(year, month, day, hour, minute, second, micro);
            var wall = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            long totalMicro = (wall.Ticks - Epoch.Ticks) / 10 + micro - offsetSeconds.Value * 1000000L;
            return totalMicro / 1e6;
        }

        static bool ParseClock(string clock, out int hour, out int minute, out int second, out int micro)
        {
            hour = minute = second = micro = 0;
            string fraction = null;
            int dot = clock.IndexOfAny(new[] { '.', ',' });
            if (dot >= 0) { fraction = clock.Substring(dot + 1); clock = clock.Substring(0, dot); }
            string[] parts;
            if (clock.IndexOf(':') >= 0) parts = clock.Split(':');
            else
            {
                if (clock.Length % 2 != 0 || clock.Length == 0 || clock.Length > 6) return false;
                parts = new string[clock.Length / 2];
                for (int i = 0; i < parts.Length; i++) parts[i] = clock.Substring(2 * i, 2);
            }
            if (parts.Length > 3) return false;
            foreach (string part in parts) if (part.Length != 2 || !Digits(part, 0, 2)) return false;
            hour = Num(parts[0], 0, 2);
            if (parts.Length > 1) minute = Num(parts[1], 0, 2);
            if (parts.Length > 2) second = Num(parts[2], 0, 2);
            if (fraction != null)
            {
                if (parts.Length != 3 || fraction.Length == 0 || !Digits(fraction, 0, fraction.Length)) return false;
                string six = (fraction.Length > 6 ? fraction.Substring(0, 6) : fraction).PadRight(6, '0');
                micro = Num(six, 0, 6);
            }
            return true;
        }

        static bool Digits(string s, int start, int count)
        {
            if (start + count > s.Length) return false;
            for (int i = start; i < start + count; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        static int Num(string s, int start, int count) => int.Parse(s.Substring(start, count), NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
