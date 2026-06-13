using System.Collections.Generic;
using System.Text;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Minimal hand-rolled JSON writer for the analytics event payload. Needed because Unity's
    /// JsonUtility (a) cannot serialize dictionaries (the events schema's <c>attributes</c> is a
    /// JSON object) and (b) serializes absent optional strings as <c>""</c>, which the server's
    /// <c>level</c> enum would reject — this writer omits absent fields entirely. No reflection,
    /// fully IL2CPP/AOT-safe. Allocates only on the (rare, user-initiated) TrackEvent path.
    /// </summary>
    internal static class TombstackJson
    {
        /// <summary>Append <c>"value"</c> with standard JSON escaping (quotes, backslash, control chars).</summary>
        internal static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Append <c>,"name":"value"</c> (comma-prefixed when not the first member).</summary>
        internal static void AppendField(StringBuilder sb, string name, string value, ref bool first)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, name);
            sb.Append(':');
            AppendString(sb, value);
        }

        /// <summary>Append a numeric JSON field (unquoted, invariant-culture). Used by TrackMetric —
        /// invariant culture so a comma-decimal locale can't corrupt the JSON, and "G17" formatting so
        /// the sample's full double precision survives (Microsoft's recommended round-trip specifier).</summary>
        internal static void AppendNumberField(StringBuilder sb, string key, double value, ref bool first)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":")
              .Append(value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Append <c>,"attributes":{...}</c> from string props, clamped to the server contract
        /// (at most <paramref name="maxEntries"/> entries; key/value length clamps). Empty or
        /// null keys are skipped; null values become <c>""</c>.
        /// </summary>
        internal static void AppendAttributes(
            StringBuilder sb,
            Dictionary<string, string> props,
            int maxEntries,
            int maxKeyLength,
            int maxValueLength,
            ref bool first)
        {
            if (props == null || props.Count == 0) return;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("\"attributes\":{");
            bool firstAttr = true;
            int written = 0;
            foreach (var pair in props)
            {
                if (written >= maxEntries) break;
                if (string.IsNullOrEmpty(pair.Key)) continue;
                if (!firstAttr) sb.Append(',');
                firstAttr = false;
                AppendString(sb, clamp(pair.Key, maxKeyLength));
                sb.Append(':');
                AppendString(sb, clamp(pair.Value ?? string.Empty, maxValueLength));
                written++;
            }
            sb.Append('}');
        }

        private static string clamp(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max);
    }
}
