using System;
using System.Collections.Generic;
using System.IO;

namespace NotchAgent
{
    /// <summary>Turns a tool call into short, human labels: "Terminal" + "npm run build", "Modification" + "page.tsx".</summary>
    internal static class ToolDetail
    {
        private const int MaxDetail = 72;

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bash", "Terminal" },
            { "PowerShell", "PowerShell" },
            { "Read", "Lecture" },
            { "Edit", "Modification" },
            { "MultiEdit", "Modification" },
            { "Write", "Écriture" },
            { "NotebookEdit", "Notebook" },
            { "Grep", "Recherche" },
            { "Glob", "Fichiers" },
            { "WebFetch", "Web" },
            { "WebSearch", "Recherche web" },
            { "Agent", "Agent" },
            { "Task", "Agent" },
            { "TodoWrite", "Plan" },
            { "Skill", "Skill" },
            { "ToolSearch", "Outils" },
        };

        public static string Label(string tool)
        {
            if (string.IsNullOrEmpty(tool)) return null;
            string label;
            if (Labels.TryGetValue(tool, out label)) return label;
            if (tool.StartsWith("mcp__", StringComparison.Ordinal))
            {
                string[] parts = tool.Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? parts[1] + " · " + parts[2] : tool;
            }
            return tool;
        }

        public static string Describe(string tool, Dictionary<string, object> input)
        {
            if (input == null) return null;
            string detail = null;
            switch (tool)
            {
                case "Bash":
                case "PowerShell":
                    detail = Json.Str(input, "description") ?? FirstLine(Json.Str(input, "command"));
                    break;
                case "Read":
                case "Edit":
                case "MultiEdit":
                case "Write":
                    detail = FileName(Json.Str(input, "file_path"));
                    break;
                case "NotebookEdit":
                    detail = FileName(Json.Str(input, "notebook_path"));
                    break;
                case "Grep":
                case "Glob":
                    detail = Json.Str(input, "pattern");
                    break;
                case "WebFetch":
                    detail = Host(Json.Str(input, "url"));
                    break;
                case "WebSearch":
                    detail = Json.Str(input, "query");
                    break;
                case "Agent":
                case "Task":
                    detail = Json.Str(input, "description");
                    break;
                case "Skill":
                    detail = Json.Str(input, "skill");
                    break;
            }
            return Clean(detail);
        }

        private static string FirstLine(string s)
        {
            if (s == null) return null;
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl >= 0 ? s.Substring(0, nl) : s;
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFileName(path.TrimEnd('\\', '/')); }
            catch (ArgumentException) { return path; }
        }

        private static string Host(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : url;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return s.Length > MaxDetail ? s.Substring(0, MaxDetail - 1) + "…" : s;
        }
    }
}
