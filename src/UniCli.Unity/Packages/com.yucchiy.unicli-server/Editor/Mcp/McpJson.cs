using System;
using System.Text;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Just enough JSON for the MCP envelope.
    ///
    /// The server package has no dependencies, which is worth keeping — pulling in a JSON library
    /// to add one transport would make every consumer carry it. JSON-RPC's envelope is a fixed
    /// shape that <see cref="UnityEngine.JsonUtility"/> could almost handle, except for two things
    /// it cannot do: leave an arbitrary subtree (a tool call's arguments) untouched, and round-trip
    /// an id that may be either a number or a string.
    ///
    /// So this reads raw values out of a JSON object without interpreting them, and escapes strings
    /// on the way out. It is not a parser and does not validate: malformed input yields null, which
    /// the caller turns into a JSON-RPC parse error.
    /// </summary>
    internal static class McpJson
    {
        /// <summary>
        /// The raw text of a top-level property's value — <c>{"a":{"b":1}}</c> gives <c>{"b":1}</c>
        /// for "a". Null when the property is absent or the text is malformed. Used to pass a tool
        /// call's arguments through untouched, and to echo a request id back in whatever form it
        /// arrived.
        /// </summary>
        public static string ExtractRaw(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
                return null;

            var i = FindPropertyValueStart(json, property);
            if (i < 0)
                return null;

            var end = SkipValue(json, i);
            return end < 0 ? null : json.Substring(i, end - i).Trim();
        }

        /// <summary>The unescaped text of a string-valued property, or null.</summary>
        public static string ExtractString(string json, string property)
        {
            var raw = ExtractRaw(json, property);
            if (raw == null || raw.Length < 2 || raw[0] != '"')
                return null;

            return Unescape(raw.Substring(1, raw.Length - 2));
        }

        /// <summary>
        /// Finds the value of a property at the object's top level. Nested objects are skipped
        /// wholesale, so a "name" inside an argument object is not mistaken for the call's own.
        /// </summary>
        private static int FindPropertyValueStart(string json, string property)
        {
            var depth = 0;
            var i = 0;

            while (i < json.Length)
            {
                var c = json[i];

                if (c == '"')
                {
                    var stringEnd = SkipString(json, i);
                    if (stringEnd < 0)
                        return -1;

                    // A property name at the top level of the object we were handed.
                    if (depth == 1)
                    {
                        var name = json.Substring(i + 1, stringEnd - i - 2);
                        var afterName = SkipWhitespace(json, stringEnd);
                        if (afterName < json.Length && json[afterName] == ':')
                        {
                            var valueStart = SkipWhitespace(json, afterName + 1);
                            if (name == property)
                                return valueStart < json.Length ? valueStart : -1;

                            i = SkipValue(json, valueStart);
                            if (i < 0)
                                return -1;
                            continue;
                        }
                    }

                    i = stringEnd;
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;

                i++;
            }

            return -1;
        }

        /// <summary>Index just past the value starting at <paramref name="start"/>, or -1.</summary>
        private static int SkipValue(string json, int start)
        {
            var i = SkipWhitespace(json, start);
            if (i >= json.Length)
                return -1;

            var c = json[i];

            if (c == '"')
                return SkipString(json, i);

            if (c == '{' || c == '[')
            {
                var open = c;
                var close = c == '{' ? '}' : ']';
                var depth = 0;

                while (i < json.Length)
                {
                    var ch = json[i];
                    if (ch == '"')
                    {
                        i = SkipString(json, i);
                        if (i < 0) return -1;
                        continue;
                    }

                    if (ch == open) depth++;
                    else if (ch == close)
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }

                    i++;
                }

                return -1;
            }

            // Number, true, false, null: runs until a structural character.
            while (i < json.Length && ",}] \t\r\n".IndexOf(json[i]) < 0)
                i++;

            return i;
        }

        /// <summary>Index just past the string starting at the quote at <paramref name="start"/>.</summary>
        private static int SkipString(string json, int start)
        {
            var i = start + 1;
            while (i < json.Length)
            {
                if (json[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                if (json[i] == '"')
                    return i + 1;

                i++;
            }

            return -1;
        }

        private static int SkipWhitespace(string json, int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i]))
                i++;
            return i;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0)
                return s;

            var builder = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length)
                {
                    builder.Append(s[i]);
                    continue;
                }

                i++;
                switch (s[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '/': builder.Append('/'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u' when i + 4 < s.Length:
                        if (ushort.TryParse(s.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: builder.Append(s[i]); break;
                }
            }

            return builder.ToString();
        }

        /// <summary>Escapes a string's contents; the caller supplies the surrounding quotes.</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            var builder = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        public static string Quote(string s) => "\"" + Escape(s) + "\"";
    }
}
