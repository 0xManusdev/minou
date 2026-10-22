using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Minou
{
    /// <summary>One-shot snapshot of the process table, used to walk from the hook up to Claude Code and its terminal.</summary>
    internal sealed class ProcessTree
    {
        private sealed class Entry
        {
            public int Pid;
            public int ParentPid;
            public string Exe;
        }

        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        // Shells and launchers that may sit between Claude Code and the hook, or between the terminal and Claude Code.
        private static readonly HashSet<string> Intermediates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash.exe", "sh.exe", "zsh.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe",
            "openconsole.exe", "node.exe", "minou.exe", "wsl.exe", "wslhost.exe"
        };

        // Never treat these as "the terminal": focusing them would open the wrong thing.
        private static readonly HashSet<string> NotHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "minou.exe"
        };

        public static ProcessTree Capture()
        {
            var tree = new ProcessTree();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return tree;
            try
            {
                var pe = new Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32)) };
                if (!Native.Process32FirstW(snap, ref pe)) return tree;
                do
                {
                    tree.entries[pe.th32ProcessID] = new Entry { Pid = pe.th32ProcessID, ParentPid = pe.th32ParentProcessID, Exe = pe.szExeFile };
                } while (Native.Process32NextW(snap, ref pe));
            }
            finally { Native.CloseHandle(snap); }
            return tree;
        }

        public string ExeOf(int pid)
        {
            Entry e;
            return entries.TryGetValue(pid, out e) ? e.Exe : null;
        }

        /// <summary>Ancestors of <paramref name="pid"/>, nearest first. Stops at a recycled PID (parent younger than child).</summary>
        public IEnumerable<int> Ancestors(int pid)
        {
            long childCreated = Native.GetProcessCreationTime(pid);
            for (int depth = 0; depth < 16; depth++)
            {
                Entry e;
                if (!entries.TryGetValue(pid, out e) || e.ParentPid <= 4) yield break;
                int parent = e.ParentPid;
                long parentCreated = Native.GetProcessCreationTime(parent);
                if (parentCreated != 0 && childCreated != 0 && parentCreated > childCreated) yield break;
                yield return parent;
                pid = parent;
                childCreated = parentCreated;
            }
        }

        /// <summary>
        /// The Claude Code process that fired the hook: the nearest claude.exe ancestor, or, for npm installs
        /// running under node, the nearest ancestor that isn't a shell.
        /// </summary>
        public int FindClaude(int hookPid)
        {
            int fallback = 0;
            foreach (int pid in Ancestors(hookPid))
            {
                string exe = ExeOf(pid) ?? "";
                if (exe.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return pid;
                if (fallback == 0 && exe.Equals("node.exe", StringComparison.OrdinalIgnoreCase)) fallback = pid;
            }
            return fallback;
        }

        /// <summary>The window the user sees Claude Code in: its console window, or the nearest ancestor with a real window
        /// (Windows Terminal, VS Code, JetBrains IDEs, ...).</summary>
        public IntPtr FindHostWindow(int claudePid)
        {
            if (claudePid <= 0) return IntPtr.Zero;

            // Classic conhost consoles: the console window itself. Under ConPTY (Windows Terminal, IDEs) it is a
            // "PseudoConsoleWindow" that claims to be visible but never is, so fall through to the ancestor walk.
            if (Native.AttachConsole(claudePid))
            {
                IntPtr console = Native.GetConsoleWindow();
                Native.FreeConsole();
                if (console != IntPtr.Zero && Native.IsWindowVisible(console) && Native.GetClassName(console) != "PseudoConsoleWindow")
                    return console;
            }

            Dictionary<int, IntPtr> windows = TopLevelWindowsByProcess();
            foreach (int pid in Ancestors(claudePid))
            {
                string exe = ExeOf(pid) ?? "";
                if (NotHosts.Contains(exe)) break;
                IntPtr hWnd;
                if (windows.TryGetValue(pid, out hWnd)) return hWnd;
            }
            return IntPtr.Zero;
        }

        private static Dictionary<int, IntPtr> TopLevelWindowsByProcess()
        {
            var map = new Dictionary<int, IntPtr>();
            Native.EnumWindows((hWnd, _) =>
            {
                if (!Native.IsWindowVisible(hWnd) || Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero) return true;
                if (Native.GetWindowTextLength(hWnd) == 0) return true;
                int pid;
                Native.GetWindowThreadProcessId(hWnd, out pid);
                if (!map.ContainsKey(pid)) map[pid] = hWnd;
                return true;
            }, IntPtr.Zero);
            return map;
        }
    }
}
