using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShoalingUpstream.Control
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A small JSON reader, written rather than borrowed.
    ///
    /// JsonUtility cannot do this job: a command's <c>value</c> is polymorphic — an object for
    /// simulatePose, a number or a string or null for everything else — and JsonUtility needs
    /// the shape declared up front. A wire format that a hostile network can truncate also has
    /// to fail by returning null, never by throwing into a socket pump, so every entry point
    /// here is a Try.
    ///
    /// Deliberately not a general-purpose library. It reads exactly the subset the control bus
    /// emits (JSON.stringify output, no NaN, no comments) and nothing else.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<JsonValue> _array;
        private Dictionary<string, JsonValue> _object;

        public static readonly JsonValue Null = new() { Kind = JsonKind.Null };

        public static JsonValue Of(bool value) => new() { Kind = JsonKind.Bool, _bool = value };
        public static JsonValue Of(double value) => new() { Kind = JsonKind.Number, _number = value };
        public static JsonValue Of(string value) =>
            value is null ? Null : new JsonValue { Kind = JsonKind.String, _string = value };

        public int Count => Kind switch
        {
            JsonKind.Array => _array.Count,
            JsonKind.Object => _object.Count,
            _ => 0,
        };

        public JsonValue this[int index] =>
            Kind == JsonKind.Array && index >= 0 && index < _array.Count ? _array[index] : Null;

        /// <summary>Missing keys read as Null rather than throwing, so callers chain lookups
        /// through message shapes they have not verified.</summary>
        public JsonValue this[string key] =>
            Kind == JsonKind.Object && _object.TryGetValue(key, out var v) ? v : Null;

        public bool Has(string key) => Kind == JsonKind.Object && _object.ContainsKey(key);

        public IEnumerable<string> Keys =>
            Kind == JsonKind.Object ? _object.Keys : Array.Empty<string>();

        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? _bool : fallback;

        public double AsDouble(double fallback = 0) => Kind switch
        {
            JsonKind.Number => _number,
            JsonKind.String when double.TryParse(_string, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };

        public float AsFloat(float fallback = 0f) => (float)AsDouble(fallback);

        public string AsString(string fallback = null) => Kind switch
        {
            JsonKind.String => _string,
            JsonKind.Number => _number.ToString("R", CultureInfo.InvariantCulture),
            JsonKind.Bool => _bool ? "true" : "false",
            _ => fallback,
        };

        public IReadOnlyList<JsonValue> Items =>
            Kind == JsonKind.Array ? _array : Array.Empty<JsonValue>();

        public static bool TryParse(string text, out JsonValue value)
        {
            value = Null;
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            try
            {
                var parsed = ParseValue(text, ref i);
                SkipWhitespace(text, ref i);
                if (parsed is null) return false;
                value = parsed;
                return true;
            }
            catch (Exception)
            {
                // Any malformed input is one return, not an exception crossing the pump.
                return false;
            }
        }

        // ---------------------------------------------------------------- parsing

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return Of(ParseString(s, ref i));
                case 't': return Literal(s, ref i, "true") ? Of(true) : null;
                case 'f': return Literal(s, ref i, "false") ? Of(false) : null;
                case 'n': return Literal(s, ref i, "null") ? Null : null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                return false;
            i += word.Length;
            return true;
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            var result = new JsonValue { Kind = JsonKind.Object, _object = new Dictionary<string, JsonValue>() };
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') return null;
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') return null;
                i++;
                var value = ParseValue(s, ref i);
                if (value is null) return null;
                result._object[key] = value;

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return null;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                return null;
            }
            return null;
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            var result = new JsonValue { Kind = JsonKind.Array, _array = new List<JsonValue>() };
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (i < s.Length)
            {
                var value = ParseValue(s, ref i);
                if (value is null) return null;
                result._array.Add(value);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return null;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                return null;
            }
            return null;
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char escape = s[i++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("truncated \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw new FormatException($"unknown escape \\{escape}");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E'
                                    || ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            {
                i++;
            }
            if (i == start) return null;
            return double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value)
                ? Of(value)
                : null;
        }
    }

    /// <summary>
    /// Builds the handful of messages the device sends. A builder rather than reflection
    /// because the server's status handler merges an allow-list — anything we emit outside it
    /// is silently discarded, so it is worth being able to see every emitted key in one place.
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new();
        private bool _needsComma;

        public JsonWriter() => _sb.Append('{');

        public JsonWriter Field(string name, string value)
        {
            if (value is null) return this;
            Key(name);
            Escape(value);
            return this;
        }

        public JsonWriter Field(string name, bool value)
        {
            Key(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter Field(string name, double value)
        {
            Key(name);
            // "R" and invariant culture: a device in a French locale must not emit 9,4 into a
            // protocol that JSON.parse will reject outright.
            _sb.Append(double.IsFinite(value)
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : "null");
            return this;
        }

        public JsonWriter Field(string name, IReadOnlyList<string> values)
        {
            if (values is null) return this;
            Key(name);
            _sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) _sb.Append(',');
                Escape(values[i]);
            }
            _sb.Append(']');
            return this;
        }

        public JsonWriter NullField(string name)
        {
            Key(name);
            _sb.Append("null");
            return this;
        }

        public string Done() => _sb.Append('}').ToString();

        private void Key(string name)
        {
            if (_needsComma) _sb.Append(',');
            _needsComma = true;
            Escape(name);
            _sb.Append(':');
        }

        private void Escape(string value)
        {
            _sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ') _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
