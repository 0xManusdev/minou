using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Minou
{
    internal enum AgentState { Idle, Working, Awaiting }

    internal sealed class Session
    {
        public string Id;
        public string Cwd;
        public AgentState State;
        public string Tool;
        public string Detail;
        public DateTime StateSince = DateTime.Now;
        public DateTime StartedAt = DateTime.Now;
        public long LastTs;
        public int ClaudePid;
        public long ClaudeCreated;
        public IntPtr HostWindow;
        public readonly HashSet<string> Subagents = new HashSet<string>();
        public Process Watched;

        public string Project
        {
            get
            {
                if (string.IsNullOrEmpty(Cwd)) return "Claude Code";
                string name = Path.GetFileName(Cwd.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? Cwd : name;
            }
        }

        /// <summary>What the notch shows: background subagents keep an otherwise idle session "working".</summary>
        public AgentState Effective
        {
            get { return State == AgentState.Idle && Subagents.Count > 0 ? AgentState.Working : State; }
        }
    }

    /// <summary>
    /// All live Claude Code sessions, fed by hook events. Lives on the UI thread.
    /// Mirrors NotchAgent for macOS (idle / working / awaiting), with Windows-specific liveness: each session is tied to
    /// its claude.exe process and disappears the moment that process exits.
    /// </summary>
    internal sealed class SessionStore
    {
        private readonly List<Session> sessions = new List<Session>();
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer saveTimer;

        public event Action Changed;
        public event Action<Session> Finished;
        public event Action<Session> Failed;
        public event Action<Session> NeedsAttention;

        public SessionStore(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };
        }

        public IList<Session> Sessions { get { return sessions; } }

        public AgentState Aggregate
        {
            get
            {
                if (sessions.Any(s => s.Effective == AgentState.Awaiting)) return AgentState.Awaiting;
                if (sessions.Any(s => s.Effective == AgentState.Working)) return AgentState.Working;
                return AgentState.Idle;
            }
        }

        /// <summary>The session the collapsed notch talks about: the one needing you, else the busiest, else the latest.</summary>
        public Session Focus
        {
            get
            {
                return sessions.Where(s => s.Effective == AgentState.Awaiting).OrderByDescending(s => s.StateSince).FirstOrDefault()
                    ?? sessions.Where(s => s.Effective == AgentState.Working).OrderByDescending(s => s.StateSince).FirstOrDefault()
                    ?? sessions.OrderByDescending(s => s.StateSince).FirstOrDefault();
            }
        }

        public void Apply(IDictionary<string, object> m)
        {
            string evt = Json.Str(m, "event");
            string id = Json.Str(m, "session_id");
            if (string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(id)) return;

            if (evt == "SessionEnd")
            {
                Remove(id);
                return;
            }

            Session s = GetOrCreate(id, m);
            long ts = Json.Long(m, "ts");
            string agentId = Json.Str(m, "agent_id");

            // Subagent bookkeeping doesn't depend on ordering.
            bool subagentsChanged = false;
            if (evt == "SubagentStart" && agentId != null) subagentsChanged = s.Subagents.Add(agentId);
            if (evt == "SubagentStop" && agentId != null) subagentsChanged = s.Subagents.Remove(agentId);

            // An async hook that lands after a newer one must not roll the state back (e.g. a late PostToolUse after Stop).
            if (ts != 0 && ts < s.LastTs)
            {
                if (subagentsChanged) RaiseChanged();
                return;
            }
            if (ts != 0) s.LastTs = ts;

            AgentState before = s.Effective;
            bool insideSubagent = agentId != null && evt != "SubagentStart" && evt != "SubagentStop";
            string tool = Json.Str(m, "tool");
            string detail = Json.Str(m, "detail");

            switch (evt)
            {
                case "SessionStart":
                    Set(s, AgentState.Idle, null, null);
                    break;
                case "UserPromptSubmit":
                case "PostCompact":
                    Set(s, AgentState.Working, null, null);
                    break;
                case "PreToolUse":
                    if (!insideSubagent) Set(s, AgentState.Working, tool, detail);
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    if (!insideSubagent) Set(s, AgentState.Working, null, null);
                    break;
                case "PreCompact":
                    Set(s, AgentState.Working, "Compact", "Compactage du contexte");
                    break;
                case "PermissionRequest":
                    Set(s, AgentState.Awaiting, tool, detail);
                    break;
                case "Notification":
                    string type = Json.Str(m, "notification_type");
                    if (type == "permission_prompt" || type == "elicitation_dialog" || type == "elicitation_url_dialog" || type == "agent_needs_input")
                        Set(s, AgentState.Awaiting, s.State == AgentState.Awaiting ? s.Tool : null, s.State == AgentState.Awaiting ? s.Detail : null);
                    break;
                case "Stop":
                    Set(s, AgentState.Idle, null, null);
                    if (before != AgentState.Idle && Finished != null) Finished(s);
                    break;
                case "StopFailure":
                    Set(s, AgentState.Idle, null, null);
                    if (Failed != null) Failed(s);
                    break;
            }

            if (s.Effective == AgentState.Awaiting && before != AgentState.Awaiting && NeedsAttention != null) NeedsAttention(s);
            RaiseChanged();
        }

        public void Remove(string id)
        {
            Session s = sessions.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            sessions.Remove(s);
            Unwatch(s);
            RaiseChanged();
        }

        private static void Set(Session s, AgentState state, string tool, string detail)
        {
            if (s.State != state) s.StateSince = DateTime.Now;
            s.State = state;
            s.Tool = tool;
            s.Detail = detail;
        }

        private Session GetOrCreate(string id, IDictionary<string, object> m)
        {
            Session s = sessions.FirstOrDefault(x => x.Id == id);
            if (s == null)
            {
                s = new Session { Id = id };
                sessions.Add(s);
            }

            string cwd = Json.Str(m, "cwd");
            if (!string.IsNullOrEmpty(cwd)) s.Cwd = cwd;

            long hwnd = Json.Long(m, "host_hwnd");
            if (hwnd != 0) s.HostWindow = new IntPtr(hwnd);

            int pid = (int)Json.Long(m, "claude_pid");
            if (pid > 0 && pid != s.ClaudePid)
            {
                // /clear and resume start a new session id inside the same claude.exe: drop the old entry.
                foreach (Session stale in sessions.Where(x => x != s && x.ClaudePid == pid).ToList())
                {
                    sessions.Remove(stale);
                    Unwatch(stale);
                }
                s.ClaudePid = pid;
                s.ClaudeCreated = Json.Long(m, "claude_created");
                Watch(s);
            }
            return s;
        }

        // --- Process liveness ------------------------------------------------------------------

        private void Watch(Session s)
        {
            Unwatch(s);
            try
            {
                Process p = Process.GetProcessById(s.ClaudePid);
                if (s.ClaudeCreated != 0 && Native.GetProcessCreationTime(s.ClaudePid) != s.ClaudeCreated)
                {
                    p.Dispose();
                    return; // PID was recycled
                }
                string id = s.Id;
                p.EnableRaisingEvents = true;
                p.Exited += (sender, e) => dispatcher.BeginInvoke(new Action(() => Remove(id)));
                s.Watched = p;
                if (p.HasExited) dispatcher.BeginInvoke(new Action(() => Remove(id)));
            }
            catch (ArgumentException)
            {
                // Already gone.
                string id = s.Id;
                dispatcher.BeginInvoke(new Action(() => Remove(id)));
            }
            catch (Exception ex)
            {
                Log.Write("watch " + s.ClaudePid + ": " + ex.Message);
            }
        }

        private static void Unwatch(Session s)
        {
            if (s.Watched == null) return;
            try { s.Watched.EnableRaisingEvents = false; s.Watched.Dispose(); }
            catch { }
            s.Watched = null;
        }

        private void RaiseChanged()
        {
            saveTimer.Stop();
            saveTimer.Start();
            if (Changed != null) Changed();
        }

        // --- Persistence: survive an app restart without losing sessions -----------------------

        private void Save()
        {
            try
            {
                var list = sessions.Where(s => s.ClaudePid > 0).Select(s => (object)new Dictionary<string, object>
                {
                    { "id", s.Id }, { "cwd", s.Cwd }, { "pid", s.ClaudePid }, { "created", s.ClaudeCreated },
                    { "hwnd", s.HostWindow.ToInt64() }, { "started", s.StartedAt.ToString("o") }
                }).ToList();
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.SessionsFile, Json.Serialize(list));
            }
            catch (Exception ex)
            {
                Log.Write("sessions save: " + ex.Message);
            }
        }

        public void Restore()
        {
            try
            {
                if (!File.Exists(Paths.SessionsFile)) return;
                var list = Json.CreateSerializer().DeserializeObject(File.ReadAllText(Paths.SessionsFile)) as object[];
                if (list == null) return;
                foreach (var d in list.OfType<Dictionary<string, object>>())
                {
                    int pid = (int)Json.Long(d, "pid");
                    long created = Json.Long(d, "created");
                    if (pid <= 0 || Native.GetProcessCreationTime(pid) != created) continue;
                    DateTime started;
                    var s = new Session
                    {
                        Id = Json.Str(d, "id"),
                        Cwd = Json.Str(d, "cwd"),
                        ClaudePid = pid,
                        ClaudeCreated = created,
                        HostWindow = new IntPtr(Json.Long(d, "hwnd")),
                        StartedAt = DateTime.TryParse(Json.Str(d, "started"), null, System.Globalization.DateTimeStyles.RoundtripKind, out started) ? started : DateTime.Now
                    };
                    if (string.IsNullOrEmpty(s.Id) || sessions.Any(x => x.Id == s.Id)) continue;
                    sessions.Add(s);
                    Watch(s);
                }
                if (sessions.Count > 0) RaiseChanged();
            }
            catch (Exception ex)
            {
                Log.Write("sessions restore: " + ex.Message);
            }
        }
    }
}
