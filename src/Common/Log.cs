// The program's log, exactly as the Python programs wrote it (logging.basicConfig with
// "%(asctime)s %(levelname)s %(message)s"): appended, UTF-8 without BOM, Windows line ends (CRLF), every line flushed,
// "2026-11-22 16:46:37,124 INFO started; ..." (local time, milliseconds cut off). The file stays open for the
// program's lifetime with read/write sharing but without delete sharing - as Python's handle did - so another start
// cannot rename the data folder from under a running instance. A failure to write is ignored: the log must never
// stop the program.
//
// Its size is limited: beyond 10 MB the file becomes <name>.1 (the earlier .1 becomes .2, and so on up to .4; the
// oldest is dropped), and a new file is begun.
using System;
using System.IO;
using System.Text;

namespace SAAControl
{
    public static class Log
    {
        static readonly object Gate = new object();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static FileStream stream;
        static string file;
        static long nextCheck;                 // (after a rotation that failed: tried again a megabyte later)

        /// <summary>A log file is begun again beyond this size; this many earlier ones are kept.</summary>
        public static long MaxBytes = 10L << 20;
        public static int Kept = 4;

        public static void Open(string path)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    file = path;
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) Rotate();
                    stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    nextCheck = 0;
                }
                catch (Exception) { stream = null; }
            }
        }

        /// <summary>The file the log goes to (null: none).</summary>
        public static string CurrentFile => file;

        public static void Close()
        {
            lock (Gate)
            {
                try { stream?.Dispose(); } catch (Exception) { }
                stream = null;
            }
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warning(string message) => Write("WARNING", message, null);
        public static void Error(string message, Exception error = null) => Write("ERROR", message, error);

        static void Write(string level, string message, Exception error)
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append(',').Append(now.Millisecond.ToString("000"))
              .Append(' ').Append(level).Append(' ').Append(message);
            if (error != null) sb.Append('\n').Append("Traceback (C#):").Append('\n').Append(error);
            string text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            byte[] bytes = Utf8.GetBytes(text);
            lock (Gate)
            {
                if (stream == null) return;
                try
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    if (stream.Length > MaxBytes && stream.Length >= nextCheck)
                    {
                        stream.Dispose();
                        stream = null;
                        bool rotated = Rotate();
                        stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        nextCheck = rotated ? 0 : stream.Length + (1L << 20);
                    }
                }
                catch (Exception)
                {
                    try { if (stream == null && file != null) stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); } catch (Exception) { }
                    if (stream != null) nextCheck = stream.Length + (1L << 20);    // (tried again after another megabyte)
                }
            }
        }

        /// <summary>file -> file.1 -> ... -> file.N (the oldest dropped). A part another process holds stays: the log goes on
        /// in the file it has.</summary>
        static bool Rotate()
        {
            try
            {
                string oldest = file + "." + Kept;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = Kept - 1; i >= 1; i--)
                    if (File.Exists(file + "." + i)) File.Move(file + "." + i, file + "." + (i + 1));
                File.Move(file, file + ".1");
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
