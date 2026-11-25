// Port of BMG/absdb.py: reader for "Absolute Database" files (.abs), the single-file database BMG Omega stores
// readings in.
//
// Only what is needed to read a table out of a snapshot of the file: no writing, no indexes, no BLOBs, no encryption.
// The layout was established from Omega's own files (see work/live_research):
//
//   file   = header ... pages                       page i sits at  base + i * page_size
//   header   "ABS0LUTEDATABASE", uint16 page_size @26, uint32 page_count @30;  base = len - size * count
//   page     "ABSP", uint16 type @8, uint32 owner table id @22
//   type 6   table directory, packed: 272-byte entries  (name, table id @256, schema page @260)
//   type 8   table schema, packed:    uint32 field count, then name + 45-byte descriptor per field
//   type 10  records:                 slot bitmap @40, then fixed-size rows = null bitmap + field bytes
//   packed   uint32 stored size @41, uint32 raw size @45, byte zlib flag @49, data @50
//            (longer than a page: continued from offset 40 of the page named by the uint32 @10)
//
// Values are as Python had them: str, long (int), double (float), null. Text is read in the PC's ANSI code page
// (Py.DecodeAnsi; GB18030 on a Chinese PC, as before).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SAAControl.Bmg
{
    public static class AbsDb
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ABS0LUTEDATABASE");
        public static readonly byte[] PageMagic = Encoding.ASCII.GetBytes("ABSP");
        public const int PageDirectory = 6, PageSchema = 8, PageRecords = 10;
        public const int RecordArea = 40;          // record pages: slot bitmap starts here
        public const int StringType = 3;

        // field type -> (struct format, byte size); type 3 is a fixed-length string, handled separately
        static readonly Dictionary<int, (char Format, int Size)> NumberTypes = new Dictionary<int, (char, int)>
        {
            [6] = ('h', 2), [7] = ('i', 4), [13] = ('d', 8), [21] = ('h', 2),
        };
        static readonly Dictionary<int, int> OpaqueSizes = new Dictionary<int, int> { [19] = 6 };   // read past, value not decoded

        /// <summary>The bytes are not a complete, readable Absolute Database snapshot (a ValueError, as in Python).</summary>
        public class AbsError : PyValueError
        {
            public AbsError(string message, Exception inner = null) : base("AbsError", message, inner) { }
        }

        public sealed class Field
        {
            public string Name { get; }
            public int Kind { get; }
            public int Size { get; }
            public Field(string name, int kind, int size) { Name = name; Kind = kind; Size = size; }
        }

        public sealed class Table
        {
            public string Name { get; }
            public long TableId { get; }
            public IReadOnlyList<Field> Fields { get; }
            /// <summary>Each row: field name -> value (null when the field is null).</summary>
            public IReadOnlyList<Dictionary<string, object>> Rows { get; }
            public Table(string name, long tableId, IReadOnlyList<Field> fields, IReadOnlyList<Dictionary<string, object>> rows)
            {
                Name = name; TableId = tableId; Fields = fields; Rows = rows;
            }
        }

        /// <summary>The bytes of a database: a byte array, or a read-only view of a file (OmegaLive.SharedView).</summary>
        public abstract class Content : IDisposable
        {
            public abstract long Length { get; }
            /// <summary>content[index]; IndexError outside.</summary>
            public abstract byte this[long index] { get; }
            /// <summary>content[start:end], clamped as Python slices.</summary>
            public abstract byte[] Slice(long start, long end);
            public virtual void Dispose() { }

            protected void Clamp(ref long start, ref long end)
            {
                start = Math.Max(0, Math.Min(start, Length));
                end = Math.Max(start, Math.Min(end, Length));
            }
        }

        public sealed class ArrayContent : Content
        {
            readonly byte[] data;
            public ArrayContent(byte[] data) { this.data = data ?? throw new ArgumentNullException(nameof(data)); }
            public override long Length => data.Length;
            public override byte this[long index]
            {
                get
                {
                    if (index < 0 || index >= data.Length) throw new PyIndexError("index out of range");
                    return data[index];
                }
            }
            public override byte[] Slice(long start, long end)
            {
                Clamp(ref start, ref end);
                var part = new byte[end - start];
                Buffer.BlockCopy(data, (int)start, part, 0, part.Length);
                return part;
            }
        }

        public sealed class Database
        {
            readonly Content content;
            public long PageSize { get; }
            public long PageCount { get; }
            public long Base { get; }
            /// <summary>page number -> (offset, type, owner table id), in page order.</summary>
            public Dictionary<long, (long Offset, int Kind, long Owner)> Pages { get; } = new Dictionary<long, (long, int, long)>();

            public Database(byte[] content) : this(new ArrayContent(content)) { }

            public Database(Content content)
            {
                this.content = content;
                if (content.Length < 80 || !Same(content.Slice(0, 16), Magic)) throw new AbsError("not an Absolute Database file");
                PageSize = U16(26);
                PageCount = U32(30);
                if (PageSize < 512 || PageCount < 1) throw new AbsError("implausible page size or count");
                Base = content.Length - PageSize * PageCount;
                if (!(80 <= Base && Base <= 1 << 20)) throw new AbsError("file length does not match its page count (still being written?)");
                for (long number = 0; number < PageCount; number++)
                {
                    long offset = Base + number * PageSize;
                    if (Same(content.Slice(offset, offset + 4), PageMagic))
                        Pages[number] = (offset, U16(offset + 8), U32(offset + 22));
                }
            }

            static bool Same(byte[] a, byte[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }

            int U16(long position)
            {
                Need(position, 2);
                return content[position] | content[position + 1] << 8;
            }

            long U32(long position)
            {
                Need(position, 4);
                return (uint)(content[position] | content[position + 1] << 8 | content[position + 2] << 16 | content[position + 3] << 24);
            }

            void Need(long position, int size)
            {
                if (position + size > content.Length)
                    throw new PyStructError("unpack_from requires a buffer of at least " + (position + size) + " bytes for unpacking " + size
                                            + " bytes at offset " + position + " (actual buffer size is " + content.Length + ")");
            }

            /// <summary>A packed block that starts in the page at `offset`.
            ///
            /// A block larger than a page continues in the page whose number stands at offset 10 of the page header
            /// (0xFFFFFFFF: none), from that page's offset 40 on. Seen in a database with 361 tables, whose directory
            /// (98,192 bytes) spans two pages of 65,535 bytes.</summary>
            byte[] Packed(long offset)
            {
                long stored = U32(offset + 41), raw = U32(offset + 45);
                int flag = content[offset + 49];
                if (stored > 64 << 20 || raw > 64 << 20 || (flag != 0 && flag != 1)) throw new AbsError("unreadable packed page");
                var data = new List<byte>(content.Slice(offset + 50, offset + Math.Min(PageSize, 50 + stored)));
                long page = offset;
                int kind = U16(offset + 8);
                for (long i = 0; i < PageCount; i++)
                {
                    if (data.Count >= stored) break;
                    if (!Pages.TryGetValue(U32(page + 10), out var following) || following.Kind != kind)
                        throw new AbsError("packed page is incomplete");
                    page = following.Offset;
                    data.AddRange(content.Slice(page + RecordArea, page + Math.Min(PageSize, RecordArea + stored - data.Count)));
                }
                if (data.Count != stored) throw new AbsError("packed page is incomplete");
                byte[] bytes = data.ToArray();
                if (flag != 0)
                {
                    try { bytes = Zlib.Decompress(bytes, Math.Max(raw + 1, 256L << 20)); }     // (a bound only far beyond any page)
                    catch (Zlib.ZlibError error) { throw new AbsError("packed page is incomplete", error); }
                    catch (Zlib.TooLong) { throw new AbsError("packed page has the wrong length"); }
                }
                if (bytes.Length != raw) throw new AbsError("packed page has the wrong length");
                return bytes;
            }

            /// <summary>{name: (table id, schema page number)} for every table in the directory.</summary>
            public Dictionary<string, (long TableId, long SchemaPage)> TableNames()
            {
                var found = new Dictionary<string, (long, long)>();
                var directory = new List<(long Number, long Offset)>();
                foreach (var page in Pages)
                    if (page.Value.Kind == PageDirectory) directory.Add((page.Key, page.Value.Offset));
                var continued = new HashSet<long>();                  // second and later pages of a block
                foreach (var (_, offset) in directory) continued.Add(U32(offset + 10));
                foreach (var (number, offset) in directory)
                {
                    if (continued.Contains(number)) continue;
                    byte[] data = Packed(offset);
                    if (data.Length % 272 != 0) throw new AbsError("unexpected table directory layout");
                    for (int position = 0; position < data.Length; position += 272)
                    {
                        int length = data[position];
                        if (length == 0) continue;
                        string name = Py.DecodeAnsi(data, position + 1, length);
                        found[name] = (LE32(data, position + 256), LE32(data, position + 260));
                    }
                }
                return found;
            }

            static long LE32(byte[] data, int position) =>
                (uint)(data[position] | data[position + 1] << 8 | data[position + 2] << 16 | data[position + 3] << 24);

            List<Field> Fields(long schemaPageNumber)
            {
                if (!Pages.TryGetValue(schemaPageNumber, out var page) || page.Kind != PageSchema)
                    throw new AbsError("table schema page is missing");
                byte[] schema = Packed(page.Offset);
                if (schema.Length < 4) throw new AbsError("table schema is empty");
                long count = LE32(schema, 0);
                if (!(1 <= count && count <= 10000)) throw new AbsError("implausible field count");
                var fields = new List<Field>();
                int position = 4;
                for (long i = 0; i < count; i++)
                {
                    if (position >= schema.Length) throw new AbsError("table schema is truncated");
                    int length = schema[position];
                    int descriptor = position + 1 + length;
                    if (length == 0 || descriptor + 45 > schema.Length) throw new AbsError("unexpected field descriptor");
                    string name = Py.DecodeAnsi(schema, position + 1, length);
                    int kind = schema[descriptor + 4];
                    long size;
                    if (kind == StringType) size = LE32(schema, descriptor + 6) + 1;
                    else if (NumberTypes.TryGetValue(kind, out var number)) size = number.Size;
                    else if (OpaqueSizes.TryGetValue(kind, out int opaque)) size = opaque;
                    else throw new AbsError("field type " + kind + " of '" + name + "' is not supported");
                    if (!(0 < size && size <= 1 << 20)) throw new AbsError("implausible field size");
                    fields.Add(new Field(name, kind, (int)size));
                    position = descriptor + 45 + (kind == 19 ? 6 : 0);
                }
                return fields;
            }

            /// <summary>The table called `name` (case-insensitive), or null when the file has no such table.</summary>
            public Table Table(string name)
            {
                string wanted = Py.Casefold(name);
                var matches = new List<KeyValuePair<string, (long TableId, long SchemaPage)>>();
                foreach (var entry in TableNames())
                    if (Py.Casefold(entry.Key) == wanted) matches.Add(entry);
                if (matches.Count == 0) return null;
                if (matches.Count > 1) throw new AbsError("more than one table named " + name);
                string realName = matches[0].Key;
                long tableId = matches[0].Value.TableId;
                List<Field> fields = Fields(matches[0].Value.SchemaPage);

                int nullBytes = (fields.Count + 7) / 8;
                long rowSize = nullBytes;
                foreach (Field field in fields) rowSize += field.Size;
                long room = PageSize - RecordArea;
                long slots = room * 8 / (rowSize * 8 + 1);
                while (slots != 0 && slots * rowSize + (slots + 7) / 8 > room) slots--;
                if (slots < 1) throw new AbsError("rows larger than a page are not supported");
                long bitmapBytes = (slots + 7) / 8;

                var rows = new List<Dictionary<string, object>>();
                foreach (var entry in Pages)                       // in page order
                {
                    var (offset, kind, owner) = entry.Value;
                    if (kind != PageRecords || owner != tableId) continue;
                    byte[] page = content.Slice(offset, offset + PageSize);    // every row lies within its page
                    for (long slot = 0; slot < slots; slot++)
                    {
                        if ((page[RecordArea + (slot >> 3)] & (1 << (int)(slot & 7))) == 0) continue;
                        long start = RecordArea + bitmapBytes + slot * rowSize;
                        long position = start + nullBytes;
                        var row = new Dictionary<string, object>();
                        for (int ordinal = 0; ordinal < fields.Count; ordinal++)
                        {
                            Field field = fields[ordinal];
                            object value = null;
                            if ((page[start + (ordinal >> 3)] & (1 << (ordinal & 7))) == 0)
                            {
                                if (field.Kind == StringType)
                                {
                                    int at = (int)position, n = 0;
                                    while (n < field.Size && page[at + n] != 0) n++;
                                    value = Py.DecodeAnsi(page, at, n);
                                }
                                else if (NumberTypes.TryGetValue(field.Kind, out var number))
                                {
                                    switch (number.Format)
                                    {
                                        case 'h': value = (long)BitConverter.ToInt16(page, (int)position); break;
                                        case 'i': value = (long)BitConverter.ToInt32(page, (int)position); break;
                                        default: value = BitConverter.ToDouble(page, (int)position); break;
                                    }
                                }
                            }
                            row[field.Name] = value;
                            position += field.Size;
                        }
                        rows.Add(row);
                    }
                }
                return new Table(realName, tableId, fields, rows);
            }
        }

        /// <summary>Convenience: new Database(content).Table(name). Raises AbsError when the snapshot is not readable.</summary>
        public static Table ReadTable(byte[] content, string name)
        {
            try { return new Database(new ArrayContent(content)).Table(name); }
            catch (Exception error) when (error is PyStructError || error is PyIndexError)
            {
                throw new AbsError("file is truncated", error);
            }
        }
    }
}
