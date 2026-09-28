using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Poni.Core
{
    /// <summary>
    /// Minimal strict JSON parser (RFC 8259). Objects -> Dictionary&lt;string, object?&gt; (last
    /// duplicate key wins), arrays -> List&lt;object?&gt;, integers -> long, other numbers -> double.
    /// Errors -> FormatException with the position.
    /// </summary>
    internal sealed class JsonParser
    {
        private const int MaxDepth = 64;

        private readonly string _text;
        private int _pos;
        private int _depth;

        public JsonParser(string text)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            if (_text.Length > 0 && _text[0] == '﻿') _pos = 1; // BOM (v1 files are UTF-8 with BOM)
        }

        public object? ParseDocument()
        {
            SkipWhitespace();
            var value = ParseValue();
            SkipWhitespace();
            if (_pos < _text.Length) throw Error("unexpected text after the JSON value");
            return value;
        }

        private object? ParseValue()
        {
            if (_pos >= _text.Length) throw Error("unexpected end of text");
            char c = _text[_pos];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw Error("unexpected character '" + c + "'");
            }
        }

        private Dictionary<string, object?> ParseObject()
        {
            Enter();
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            _pos++; // {
            SkipWhitespace();
            if (Peek() == '}') { _pos++; _depth--; return obj; }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("property name expected");
                var key = ParseString();
                SkipWhitespace();
                if (Peek() != ':') throw Error("':' expected");
                _pos++;
                SkipWhitespace();
                obj[key] = ParseValue();
                SkipWhitespace();
                char c = Peek();
                _pos++;
                if (c == ',') continue;
                if (c == '}') break;
                _pos--;
                throw Error("',' or '}' expected");
            }
            _depth--;
            return obj;
        }

        private List<object?> ParseArray()
        {
            Enter();
            var list = new List<object?>();
            _pos++; // [
            SkipWhitespace();
            if (Peek() == ']') { _pos++; _depth--; return list; }
            while (true)
            {
                SkipWhitespace();
                list.Add(ParseValue());
                SkipWhitespace();
                char c = Peek();
                _pos++;
                if (c == ',') continue;
                if (c == ']') break;
                _pos--;
                throw Error("',' or ']' expected");
            }
            _depth--;
            return list;
        }

        private string ParseString()
        {
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw Error("unterminated string");
                char c = _text[_pos++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw Error("control character in string");
                if (c != '\\') { sb.Append(c); continue; }

                if (_pos >= _text.Length) throw Error("unterminated escape");
                char e = _text[_pos++];
                switch (e)
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
                        if (_pos + 4 > _text.Length) throw Error("truncated \\u escape");
                        var hex = _text.Substring(_pos, 4);
                        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            throw Error("invalid \\u escape");
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default:
                        throw Error("invalid escape '\\" + e + "'");
                }
            }
        }

        private object ParseNumber()
        {
            int start = _pos;
            if (Peek() == '-') _pos++;
            if (Peek() == '0') _pos++;
            else if (IsDigit(Peek())) while (IsDigit(Peek())) _pos++;
            else throw Error("invalid number");

            bool isInteger = true;
            if (Peek() == '.')
            {
                isInteger = false;
                _pos++;
                if (!IsDigit(Peek())) throw Error("invalid number");
                while (IsDigit(Peek())) _pos++;
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                isInteger = false;
                _pos++;
                if (Peek() == '+' || Peek() == '-') _pos++;
                if (!IsDigit(Peek())) throw Error("invalid number");
                while (IsDigit(Peek())) _pos++;
            }

            var token = _text.Substring(start, _pos - start);
            if (isInteger && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                return l;
            return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0) throw Error("invalid literal");
            _pos += literal.Length;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Error("nesting too deep");
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private void SkipWhitespace()
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
                else break;
            }
        }

        private FormatException Error(string message) =>
            new FormatException("Invalid JSON at position " + _pos.ToString(CultureInfo.InvariantCulture) + ": " + message + ".");
    }
}
