// Port of BMG/paradox.py: minimal reader for Paradox .DB tables, the format Omega keeps its test protocols in.
//
// Enough to list the protocols and their settings (TestDef.DB) and to see what a running test uses (TestAkt.DB).
// Text, short, long and number fields are decoded; other field types are skipped. Reading only. The file is opened
// with full sharing, so Omega can go on writing or deleting it.
//
// Values are as Python had them: str, long (Python int), double (float), bool, null (blank). Failures raise what
// Python raised: PyStructError (struct.error), PyIndexError, PyValueError, ZeroDivisionError, and the file errors.
using System;
using System.Collections.Generic;

namespace SAAControl.Bmg
{
    public static class Paradox
    {
        public const int Alpha = 1, Short = 3, Long = 4, Number = 6, Logical = 9, AutoInc = 22;

        /// <summary>A field of the table: Python's tuple (name, type, size).</summary>
        public sealed class FieldInfo
        {
            public string Name { get; }
            public int Kind { get; }
            public int Size { get; }
            public FieldInfo(string name, int kind, int size) { Name = name; Kind = kind; Size = size; }
        }

        /// <summary>Python's {'table': name, 'fields': [(name, type, size)], 'rows': [dict]}.</summary>
        public sealed class TableFile
        {
            public string Table { get; }
            public List<FieldInfo> Fields { get; }
            public List<Dictionary<string, object>> Rows { get; }
            public TableFile(string table, List<FieldInfo> fields, List<Dictionary<string, object>> rows) { Table = table; Fields = fields; Rows = rows; }
        }

        /// <summary>Paradox stores numbers big-endian with the sign bit flipped; all-zero bytes mean 'blank'.</summary>
        static object Signed(byte[] raw, char form)
        {
            bool any = false;
            foreach (byte b in raw) if (b != 0) { any = true; break; }
            if (!any) return null;
            int need = form == 'h' ? 2 : form == 'i' ? 4 : 8;
            var x = (byte[])raw.Clone();
            if (form == 'd' && (raw[0] & 0x80) == 0)                 // negative double: all bits inverted
                for (int i = 0; i < x.Length; i++) x[i] ^= 0xFF;
            else x[0] ^= 0x80;
            if (x.Length != need) throw new PyStructError("unpack requires a buffer of " + need + " bytes");
            switch (form)
            {
                case 'h': return (long)(short)(x[0] << 8 | x[1]);
                case 'i': return (long)(x[0] << 24 | x[1] << 16 | x[2] << 8 | x[3]);
                default:
                    Array.Reverse(x);
                    return BitConverter.ToDouble(x, 0);
            }
        }

        /// <summary>The table, fields and rows of a Paradox table file.</summary>
        public static TableFile Read(string path) => Parse(Py.ReadBytes(path));

        public static TableFile Parse(byte[] data)
        {
            NeedBytes(data, 0, 4);                                            // struct.unpack_from("<HH", data, 0)
            int recordSize = U16(data, 0), headerSize = U16(data, 2);
            int blockSize = At(data, 5) * 1024;
            int fieldCount = U16(data, 0x21);
            int version = At(data, 0x39);
            int info = version >= 5 ? 0x78 : 0x58;
            var kinds = new List<(int Kind, int Size)>();
            for (int i = 0; i < fieldCount; i++) kinds.Add((At(data, info + 2 * i), At(data, info + 2 * i + 1)));
            int position = info + 2 * fieldCount + 4 + 4 * fieldCount;       // past table-name and field-name pointers
            int end = IndexOfNul(data, position);
            string table = Py.DecodeLatin1(data, position, end - position);
            position += version >= 12 ? 261 : 79;
            var names = new List<string>();
            for (int i = 0; i < fieldCount; i++)
            {
                end = IndexOfNul(data, position);
                names.Add(Py.DecodeLatin1(data, position, end - position));
                position = end + 1;
            }

            var rows = new List<Dictionary<string, object>>();
            if (blockSize == 0) throw new PyValueError("range() arg 3 must not be zero");
            for (long offset = headerSize; offset < data.Length - 5; offset += blockSize)
            {
                int last = (short)U16(data, (int)offset + 4);                  // offset of the last record in the block
                if (last < 0) continue;
                if (recordSize == 0) throw new PyException("ZeroDivisionError", "integer division or modulo by zero");
                for (int index = 0; index < last / recordSize + 1; index++)
                {
                    long start = offset + 6 + (long)index * recordSize;
                    byte[] raw = Slice(data, start, start + recordSize);
                    int at = 0;
                    var row = new Dictionary<string, object>();
                    for (int k = 0; k < names.Count; k++)
                    {
                        string name = names[k];
                        var (kind, size) = kinds[k];
                        byte[] chunk = Slice(raw, at, at + size);
                        at += size;
                        if (kind == Alpha)
                        {
                            int nul = Array.IndexOf(chunk, (byte)0);
                            row[name] = Py.DecodeAnsi(chunk, 0, nul >= 0 ? nul : chunk.Length);
                        }
                        else if (kind == Short) row[name] = Signed(chunk, 'h');
                        else if (kind == Long || kind == AutoInc) row[name] = Signed(chunk, 'i');
                        else if (kind == Number) row[name] = Signed(chunk, 'd');
                        else if (kind == Logical)
                        {
                            if (chunk.Length == 0) throw new PyIndexError("index out of range");
                            row[name] = chunk[0] == 0 ? null : (object)((chunk[0] & 1) != 0);
                        }
                    }
                    rows.Add(row);
                }
            }
            var fields = new List<FieldInfo>();
            for (int k = 0; k < names.Count; k++) fields.Add(new FieldInfo(names[k], kinds[k].Kind, kinds[k].Size));
            return new TableFile(table, fields, rows);
        }

        static int At(byte[] data, int index)
        {
            if (index < 0 || index >= data.Length) throw new PyIndexError("index out of range");
            return data[index];
        }

        static int U16(byte[] data, int offset)
        {
            NeedBytes(data, offset, 2);
            return data[offset] | data[offset + 1] << 8;
        }

        static void NeedBytes(byte[] data, int offset, int size)
        {
            if (offset + size > data.Length)
                throw new PyStructError("unpack_from requires a buffer of at least " + (offset + size) + " bytes for unpacking " + size
                                        + " bytes at offset " + offset + " (actual buffer size is " + data.Length + ")");
        }

        /// <summary>data.index(NUL, start): ValueError when there is none.</summary>
        static int IndexOfNul(byte[] data, int start)
        {
            int found = start < data.Length ? Array.IndexOf(data, (byte)0, Math.Max(0, start)) : -1;
            if (found < 0) throw new PyValueError("subsection not found");
            return found;
        }

        /// <summary>data[start:end] (clamped, as Python slices).</summary>
        static byte[] Slice(byte[] data, long start, long end)
        {
            start = Math.Max(0, Math.Min(start, data.Length));
            end = Math.Max(start, Math.Min(end, data.Length));
            var part = new byte[end - start];
            Buffer.BlockCopy(data, (int)start, part, 0, part.Length);
            return part;
        }
    }
}
