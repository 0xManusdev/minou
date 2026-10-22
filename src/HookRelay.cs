using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace NotchAgent
{
    /// <summary>
    /// <c>NotchAgent.exe hook</c>: invoked by Claude Code for every hook event. Reads the event from stdin, adds what only
    /// the hook can know (which claude.exe fired it, which window hosts it, when it fired), and forwards one JSON line to
    /// the app over a named pipe. Fire-and-forget; always exits 0 so it can never block Claude.
    /// </summary>
    internal static class HookRelay
    {
        // Events after which the notch should exist even if the app isn't running yet.
        private static readonly HashSet<string> LaunchEvents = new HashSet<string>
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "Notification"
        };

        public static int Run()
        {
            try
            {
                // The hook's own start time orders events: async hooks can reach the app out of order.
                long firedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUnixTimeMilliseconds();

                string raw;
                using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                    raw = reader.ReadToEnd();

                Dictionary<string, object> input = Json.ParseObject(raw);
                if (input == null) return 0;

                string evt = Json.Str(input, "hook_event_name");
                string sessionId = Json.Str(input, "session_id");
                if (string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(sessionId)) return 0;

                var message = new Dictionary<string, object>();
                message["event"] = evt;
                message["session_id"] = sessionId;
                message["cwd"] = Json.Str(input, "cwd");
                message["ts"] = firedAt;

                string tool = Json.Str(input, "tool_name");
                if (tool != null)
                {
                    message["tool"] = tool;
                    message["detail"] = ToolDetail.Describe(tool, Json.Obj(input, "tool_input"));
                }
                CopyIfPresent(input, message, "notification_type");
                CopyIfPresent(input, message, "agent_id");
                CopyIfPresent(input, message, "agent_type");
                CopyIfPresent(input, message, "source");

                // A few milliseconds per event, and it lets the app pick up a session at any point (e.g. after a restart).
                ProcessTree tree = ProcessTree.Capture();
                int claudePid = tree.FindClaude(Process.GetCurrentProcess().Id);
                if (claudePid > 0)
                {
                    message["claude_pid"] = claudePid;
                    message["claude_created"] = Native.GetProcessCreationTime(claudePid);
                    message["host_hwnd"] = tree.FindHostWindow(claudePid).ToInt64();
                }

                string line = Json.Serialize(message);
                if (Send(line, 250)) return 0;

                if (!LaunchEvents.Contains(evt)) return 0;
                Launcher.StartApp();
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    Thread.Sleep(250);
                    if (Send(line, 250)) return 0;
                }
                Log.Write("hook: app unreachable, dropped " + evt);
            }
            catch (Exception ex)
            {
                Log.Write("hook: " + ex.Message);
            }
            return 0;
        }

        public static bool Send(string line, int timeoutMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.Out))
                {
                    pipe.Connect(timeoutMs);
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    return true;
                }
            }
            catch (TimeoutException) { return false; }
            catch (IOException) { return false; }
        }

        private static void CopyIfPresent(Dictionary<string, object> from, Dictionary<string, object> to, string key)
        {
            string v = Json.Str(from, key);
            if (!string.IsNullOrEmpty(v)) to[key] = v;
        }
    }

    /// <summary>Starts the app outside Claude Code's job object, so it outlives the hook that launched it.</summary>
    internal static class Launcher
    {
        public static void StartApp()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            if (TryCreate(exe, Native.CREATE_BREAKAWAY_FROM_JOB | Native.DETACHED_PROCESS | Native.CREATE_NEW_PROCESS_GROUP)) return;

            // The job forbids breakaway: let WMI create the process, which parents it under WmiPrvSE instead.
            try
            {
                using (var cls = new System.Management.ManagementClass("Win32_Process"))
                {
                    var args = cls.GetMethodParameters("Create");
                    args["CommandLine"] = "\"" + exe + "\"";
                    args["CurrentDirectory"] = Path.GetDirectoryName(exe);
                    cls.InvokeMethod("Create", args, null);
                }
            }
            catch (Exception ex)
            {
                Log.Write("launch via WMI failed: " + ex.Message);
                TryCreate(exe, Native.DETACHED_PROCESS);
            }
        }

        private static bool TryCreate(string exe, uint flags)
        {
            var si = new Native.STARTUPINFO();
            si.cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.STARTUPINFO));
            Native.PROCESS_INFORMATION pi;
            bool ok = Native.CreateProcess(exe, new StringBuilder("\"" + exe + "\""), IntPtr.Zero, IntPtr.Zero, false,
                flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi);
            if (!ok) return false;
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return true;
        }
    }
}
