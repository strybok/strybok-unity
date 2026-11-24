using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Strybok
{
    /// <summary>
    /// Minimal, reflection-free JSON reader and writer (safe under IL2CPP and code
    /// stripping). It exists because <c>JsonUtility</c> cannot read arbitrary objects
    /// such as a link payload.
    ///
    /// Parsed values are <c>Dictionary&lt;string, object&gt;</c>, <c>List&lt;object&gt;</c>,
    /// <c>string</c>, <c>bool</c>, <c>long</c> (integers that fit), <c>double</c> and <c>null</c>.
    /// </summary>
    public static class StrybokJson
    {
        private const int MaxDepth = 64;

        /// <summary>Parses a complete JSON document. Returns false for anything that is not valid JSON.</summary>
        public static bool TryParse(string json, out object value)
        {
            value = null;
            if (json == null) return false;
            var reader = new Reader(json);
            try
            {
                reader.SkipWhitespace();
                object parsed = reader.ReadValue(0);
                reader.SkipWhitespace();
                if (!reader.AtEnd) return false;
                value = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes dictionaries, lists, strings, booleans, numbers and null as JSON.
        /// Any other object is written as its <c>ToString()</c> text.
        /// </summary>
        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value, 0);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("JSON value is nested too deeply");
            if (value == null)
            {
                builder.Append("null");
            }
            else if (value is string)
            {
                WriteString(builder, (string)value);
            }
            else if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
            }
            else if (value is double || value is float)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) builder.Append("null");
                else builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is long || value is int || value is short || value is byte ||
                     value is ulong || value is uint || value is ushort || value is sbyte ||
                     value is decimal)
            {
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
            }
            else if (value is IEnumerable<KeyValuePair<string, object>>)
            {
                builder.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in (IEnumerable<KeyValuePair<string, object>>)value)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key ?? string.Empty);
                    builder.Append(':');
                    Write(builder, pair.Value, depth + 1);
                }
                builder.Append('}');
            }
            else if (value is IEnumerable)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Write(builder, item, depth + 1);
                }
                builder.Append(']');
            }
            else
            {
                WriteString(builder, value.ToString());
            }
        }

        internal static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private struct Reader
        {
            private readonly string _text;
            private int _position;

            public Reader(string text)
            {
                _text = text;
                // Tolerate a byte order mark left in by a careless decoder.
                _position = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            }

            public bool AtEnd
            {
                get { return _position >= _text.Length; }
            }

            public void SkipWhitespace()
            {
                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
                    _position++;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("Nested too deeply");
                if (AtEnd) throw new FormatException("Unexpected end");
                char c = _text[_position];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': ReadLiteral("true"); return true;
                    case 'f': ReadLiteral("false"); return false;
                    case 'n': ReadLiteral("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException("Unexpected character");
                }
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _position++; // {
                SkipWhitespace();
                if (TryConsume('}')) return result;
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_position] != '"') throw new FormatException("Expected a key");
                    string key = ReadString();
                    SkipWhitespace();
                    if (!TryConsume(':')) throw new FormatException("Expected ':'");
                    SkipWhitespace();
                    result[key] = ReadValue(depth + 1); // A repeated key keeps its last value.
                    SkipWhitespace();
                    if (TryConsume(',')) continue;
                    if (TryConsume('}')) return result;
                    throw new FormatException("Expected ',' or '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var result = new List<object>();
                _position++; // [
                SkipWhitespace();
                if (TryConsume(']')) return result;
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (TryConsume(',')) continue;
                    if (TryConsume(']')) return result;
                    throw new FormatException("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _position++; // opening quote
                StringBuilder builder = null;
                int start = _position;
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated string");
                    char c = _text[_position];
                    if (c == '"')
                    {
                        string tail = _text.Substring(start, _position - start);
                        _position++;
                        return builder == null ? tail : builder.Append(tail).ToString();
                    }
                    if (c < 0x20) throw new FormatException("Control character in string");
                    if (c != '\\')
                    {
                        _position++;
                        continue;
                    }

                    if (builder == null) builder = new StringBuilder();
                    builder.Append(_text, start, _position - start);
                    _position++;
                    if (AtEnd) throw new FormatException("Unterminated escape");
                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': builder.Append(ReadUnicodeEscape()); break;
                        default: throw new FormatException("Unknown escape");
                    }
                    start = _position;
                }
            }

            private char ReadUnicodeEscape()
            {
                if (_position + 4 > _text.Length) throw new FormatException("Short unicode escape");
                int code = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = _text[_position++];
                    int digit;
                    if (c >= '0' && c <= '9') digit = c - '0';
                    else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                    else throw new FormatException("Bad unicode escape");
                    code = (code << 4) | digit;
                }
                return (char)code;
            }

            private object ReadNumber()
            {
                int start = _position;
                bool integral = true;
                TryConsume('-');
                if (TryConsume('0'))
                {
                    // A leading zero must stand alone.
                }
                else if (ReadDigits() == 0)
                {
                    throw new FormatException("Bad number");
                }
                if (TryConsume('.'))
                {
                    integral = false;
                    if (ReadDigits() == 0) throw new FormatException("Bad fraction");
                }
                if (!AtEnd && (_text[_position] == 'e' || _text[_position] == 'E'))
                {
                    integral = false;
                    _position++;
                    if (!TryConsume('+')) TryConsume('-');
                    if (ReadDigits() == 0) throw new FormatException("Bad exponent");
                }

                string text = _text.Substring(start, _position - start);
                long asLong;
                if (integral && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out asLong))
                {
                    return asLong;
                }
                double asDouble;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble))
                {
                    return asDouble;
                }
                throw new FormatException("Bad number");
            }

            private int ReadDigits()
            {
                int count = 0;
                while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9')
                {
                    _position++;
                    count++;
                }
                return count;
            }

            private void ReadLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("Unexpected token");
                }
                _position += literal.Length;
            }

            private bool TryConsume(char expected)
            {
                if (_position < _text.Length && _text[_position] == expected)
                {
                    _position++;
                    return true;
                }
                return false;
            }
        }
    }
}
