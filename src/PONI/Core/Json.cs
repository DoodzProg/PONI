using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Poni.Core
{
    /// <summary>An ordered JSON object for writing: new JsonObject { { "Name", "x" }, { "N", 1 } }.</summary>
    public sealed class JsonObject : List<KeyValuePair<string, object?>>
    {
        public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));
    }

    /// <summary>
    /// JSON without third-party libraries (PONI must stay a single exe, and the .NET Framework
    /// serializers drag in System.Web or need attributes everywhere).
    /// Reading: a small strict RFC 8259 parser (objects become Dictionary, arrays List, numbers
    /// long or double), wrapped with tolerant, case-insensitive accessors. Writing: an indented
    /// writer (keeps the files readable and hand-editable, like v1).
    /// </summary>
    public static class Json
    {
        /// <summary>Parses any JSON text (a leading BOM is ignored). Throws FormatException on invalid JSON.</summary>
        public static object? Parse(string text) => new JsonParser(text).ParseDocument();

        // ------------------------------------------------------------ tolerant accessors

        public static IDictionary<string, object?>? AsObject(object? value) => value as IDictionary<string, object?>
            ?? (value is IDictionary<string, object> d ? ToNullable(d) : null);

        /// <summary>Case-insensitive property lookup (v1 files come from PowerShell, casing may vary).</summary>
        public static object? Get(IDictionary<string, object?>? obj, string key)
        {
            if (obj == null) return null;
            if (obj.TryGetValue(key, out var direct)) return direct;
            foreach (var pair in obj)
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        public static bool Has(IDictionary<string, object?>? obj, string key)
        {
            if (obj == null) return false;
            foreach (var pair in obj)
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>String value; numbers and booleans are converted; null / empty -> null.</summary>
        public static string? AsString(object? value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s.Length == 0 ? null : s;
                case bool b: return b ? "true" : "false";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        /// <summary>Integer from a number or a numeric string; null if not an integer.</summary>
        public static int? AsInt(object? value)
        {
            switch (value)
            {
                case int i: return i;
                case long l when l >= int.MinValue && l <= int.MaxValue: return (int)l;
                case decimal m when m == decimal.Truncate(m) && m >= int.MinValue && m <= int.MaxValue: return (int)m;
                case double d when d == Math.Truncate(d) && d >= int.MinValue && d <= int.MaxValue: return (int)d;
                case string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed): return parsed;
                default: return null;
            }
        }

        public static bool? AsBool(object? value)
        {
            switch (value)
            {
                case bool b: return b;
                case string s when bool.TryParse(s.Trim(), out var parsed): return parsed;
                default: return null;
            }
        }

        /// <summary>Array items; a single non-array value is treated as a one-item list (PowerShell unwraps 1-item arrays).</summary>
        public static IList<object?> AsList(object? value)
        {
            var list = new List<object?>();
            switch (value)
            {
                case null: break;
                case string s: list.Add(s); break;
                case IDictionary _: list.Add(value); break;
                case IDictionary<string, object> _: list.Add(value); break;
                case IEnumerable items: foreach (var item in items) list.Add(item); break;
                default: list.Add(value); break;
            }
            return list;
        }

        private static IDictionary<string, object?> ToNullable(IDictionary<string, object> source)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in source) copy[pair.Key] = pair.Value;
            return copy;
        }

        // ------------------------------------------------------------ writer

        /// <summary>Serializes JsonObject / dictionaries / lists / strings / numbers / booleans / null, indented with 2 spaces.</summary>
        public static string Write(object? value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value, int depth)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    WriteString(sb, e.ToString());
                    break;
                case JsonObject obj:
                    WriteObject(sb, obj, depth);
                    break;
                case IDictionary<string, object?> dict:
                    var ordered = new JsonObject();
                    foreach (var pair in dict) ordered.Add(pair.Key, pair.Value);
                    WriteObject(sb, ordered, depth);
                    break;
                case IEnumerable items:
                    WriteArray(sb, items, depth);
                    break;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, JsonObject obj, int depth)
        {
            if (obj.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            for (int i = 0; i < obj.Count; i++)
            {
                Indent(sb, depth + 1);
                WriteString(sb, obj[i].Key);
                sb.Append(": ");
                WriteValue(sb, obj[i].Value, depth + 1);
                if (i < obj.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable items, int depth)
        {
            var list = new List<object?>();
            foreach (var item in items) list.Add(item);
            if (list.Count == 0) { sb.Append("[]"); return; }

            // Short arrays of scalars stay on one line: "Dns": ["1.1.1.1", "8.8.8.8"]
            bool scalars = list.TrueForAll(x => x == null || x is string || x is bool || x is ValueType);
            if (scalars)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteValue(sb, list[i], depth);
                }
                sb.Append(']');
                return;
            }

            sb.Append("[\n");
            for (int i = 0; i < list.Count; i++)
            {
                Indent(sb, depth + 1);
                WriteValue(sb, list[i], depth + 1);
                if (i < list.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth);
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);
    }
}
