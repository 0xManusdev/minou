using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;

// Targeting 4.8 (not the 4.0 default csc assumes) turns on WPF's per-monitor DPI scaling.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Minou")]
[assembly: AssemblyProduct("Minou")]
[assembly: AssemblyDescription("Live Claude Code status in a notch at the top of your screen.")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace Minou
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            // A GUI-subsystem exe has no console: borrow the caller's so install messages show up.
            if (command.StartsWith("--", StringComparison.Ordinal)) Native.AttachConsole(-1);
            switch (command)
            {
                case "hook": return HookRelay.Run();
                case "--install-hooks": return HooksConfig.Install();
                case "--uninstall-hooks": return HooksConfig.Uninstall();
                case "--quit": HookRelay.Send("{\"event\":\"__quit\"}", 1000); return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, Paths.AppMutex, out created))
            {
                if (!created) return 0;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Write("unhandled: " + e.Exception);
                    e.Handled = true;
                };
                var core = new AppCore(app);
                app.Startup += (s, e) => core.Start();
                app.Run();
            }
            return 0;
        }
    }

    /// <summary>
    /// Adds or removes Minou's hooks in ~/.claude/settings.json without touching anything else.
    /// Hooks run in exec form (no shell) and in the background, so they never slow Claude Code down.
    /// </summary>
    internal static class HooksConfig
    {
        private static readonly string[] Events =
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
            "Notification", "SubagentStart", "SubagentStop", "PreCompact", "PostCompact", "Stop", "StopFailure", "SessionEnd"
        };

        private static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"); }
        }

        public static int Install()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName.Replace('\\', '/');
            return Edit(hooks =>
            {
                foreach (string evt in Events)
                {
                    var hook = new Dictionary<string, object>
                    {
                        { "type", "command" },
                        { "command", exe },
                        { "args", new object[] { "hook" } },
                    };
                    // SessionEnd runs as Claude Code exits: keep it synchronous so it isn't cut off.
                    if (evt == "SessionEnd") hook["timeout"] = 5;
                    else hook["async"] = true;

                    List<object> entries = Entries(hooks, evt);
                    entries.Add(new Dictionary<string, object> { { "hooks", new object[] { hook } } });
                    hooks[evt] = entries.ToArray();
                }
                Console.WriteLine("Hooks Minou installés pour " + Events.Length + " événements.");
            });
        }

        public static int Uninstall()
        {
            return Edit(hooks => Console.WriteLine("Hooks Minou retirés."));
        }

        /// <summary>Loads settings.json, strips every Minou entry (current and the earlier script version), applies
        /// <paramref name="change"/>, drops emptied events and saves. A one-time backup is kept next to the file.</summary>
        private static int Edit(Action<Dictionary<string, object>> change)
        {
            try
            {
                string path = SettingsPath;
                Dictionary<string, object> settings = File.Exists(path)
                    ? Json.ParseObject(File.ReadAllText(path)) ?? new Dictionary<string, object>()
                    : new Dictionary<string, object>();

                string backup = path + ".before-minou";
                if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);

                Dictionary<string, object> hooks = Json.Obj(settings, "hooks") ?? new Dictionary<string, object>();
                foreach (string evt in hooks.Keys.ToList())
                {
                    List<object> kept = Entries(hooks, evt);
                    if (kept.Count == 0) hooks.Remove(evt);
                    else hooks[evt] = kept.ToArray();
                }

                change(hooks);

                if (hooks.Count > 0) settings["hooks"] = hooks;
                else settings.Remove("hooks");

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Pretty(settings));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Échec de la mise à jour de settings.json : " + ex.Message);
                return 1;
            }
        }

        /// <summary>The event's matcher groups, minus any hook that belongs to Minou.</summary>
        private static List<object> Entries(Dictionary<string, object> hooks, string evt)
        {
            var result = new List<object>();
            object raw;
            if (!hooks.TryGetValue(evt, out raw) || !(raw is object[])) return result;
            foreach (object group in (object[])raw)
            {
                var g = group as Dictionary<string, object>;
                object[] list = g != null && g.ContainsKey("hooks") ? g["hooks"] as object[] : null;
                if (g == null || list == null)
                {
                    result.Add(group);
                    continue;
                }
                object[] others = list.Where(h => !IsOurs(h as Dictionary<string, object>)).ToArray();
                if (others.Length == 0) continue;
                g["hooks"] = others;
                result.Add(g);
            }
            return result;
        }

        private static bool IsOurs(Dictionary<string, object> hook)
        {
            string cmd = Json.Str(hook, "command") ?? "";
            // Also matches the names Minou shipped under before: NotchAgent.exe and the ~/.claude/notch script.
            return cmd.IndexOf("Minou.exe", StringComparison.OrdinalIgnoreCase) >= 0
                || cmd.IndexOf("NotchAgent.exe", StringComparison.OrdinalIgnoreCase) >= 0
                || cmd.Replace('\\', '/').IndexOf(".claude/notch/hook.sh", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
