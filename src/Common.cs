using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Minou
{
    internal static class Paths
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Minou");

        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string SessionsFile { get { return Path.Combine(DataDir, "sessions.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "minou.log"); } }

        /// <summary>Per-user pipe; the default pipe DACL only lets the creating user write to it.</summary>
        public static string PipeName { get { return "Minou." + Environment.UserName; } }

        public const string AppMutex = "Local\\Minou.App";
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    var info = new FileInfo(Paths.LogFile);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        File.Delete(Paths.LogFile + ".1");
                        File.Move(Paths.LogFile, Paths.LogFile + ".1");
                    }
                    File.AppendAllText(Paths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the app (or a hook) down.
            }
        }
    }

    /// <summary>Thin helpers over JavaScriptSerializer's loosely typed output.</summary>
    internal static class Json
    {
        public static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return CreateSerializer().DeserializeObject(text) as Dictionary<string, object>;
        }

        public static string Serialize(object value)
        {
            return CreateSerializer().Serialize(value);
        }

        public static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Long(IDictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static bool Bool(IDictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || !(v is bool)) return fallback;
            return (bool)v;
        }

        public static Dictionary<string, object> Obj(IDictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        /// <summary>
        /// Indented JSON that keeps non-ASCII characters as-is (JavaScriptSerializer escapes them),
        /// so rewriting ~/.claude/settings.json doesn't scramble the user's own text.
        /// </summary>
        public static string Pretty(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is IDictionary<string, object>)
            {
                var dict = (IDictionary<string, object>)value;
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in dict)
                {
                    Indent(sb, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    WriteValue(sb, kv.Value, depth + 1);
                    if (++i < dict.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            if (value is IEnumerable)
            {
                var items = new List<object>();
                foreach (var item in (IEnumerable)value) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    Indent(sb, depth + 1);
                    WriteValue(sb, items[i], depth + 1);
                    if (i < items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            if (value is IFormattable)
            {
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            WriteString(sb, value.ToString());
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
