// The part of BMG/winutil.py the data side uses: the names of the running programs (is an Omega open), through the
// Windows API. Only looked at, never changed. (The window functions of winutil.py belong to the host.)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SAAControl.Bmg
{
    public static class WinUtil
    {
        const uint TH32CS_SNAPPROCESS = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        /// <summary>(name of the program file, process id, id of the process that started it) of every running process.
        /// When Windows cannot give the list (the snapshot fails) this throws Win32Exception: no list is not an empty
        /// list, which would read as "Omega is not running".</summary>
        public static List<(string Name, uint Id, uint Parent)> EachProcess()
        {
            var found = new List<(string, uint, uint)>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var entry = new ProcessEntry { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                bool more = Process32FirstW(snapshot, ref entry);
                if (!more) throw new Win32Exception(Marshal.GetLastWin32Error());   // (a list always has Windows' own processes)
                while (more)
                {
                    found.Add((entry.szExeFile ?? "", entry.th32ProcessID, entry.th32ParentProcessID));
                    more = Process32NextW(snapshot, ref entry);
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return found;
        }

        /// <summary>{process id: id of the process that started it} for the running processes of that program file
        /// (Win32Exception: no list could be had, see EachProcess).</summary>
        public static Dictionary<uint, uint> Processes(string exeName)
        {
            string wanted = Py.Casefold(exeName);
            var found = new Dictionary<uint, uint>();
            foreach (var (name, id, parent) in EachProcess())
                if (Py.Casefold(name) == wanted) found[id] = parent;
            return found;
        }

        /// <summary>Lower-case (casefolded) names of the program files of all running processes (Win32Exception: no list
        /// could be had, see EachProcess).</summary>
        public static HashSet<string> ProcessNames()
        {
            var names = new HashSet<string>();
            foreach (var (name, _, _) in EachProcess()) names.Add(Py.Casefold(name));
            return names;
        }
    }
}
