// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kiln.GameIds;

/// <summary>
/// A small JSON reader for the catalog (no System.Text.Json in a netstandard2.0 generator without shipping it).
/// Objects are <see cref="Dictionary{TKey,TValue}"/>, arrays <see cref="List{T}"/>, numbers <see cref="double"/>.
/// <see cref="SkipValue"/> walks a value without building it, so unwanted catalog sections (12k templates, loca) cost
/// one pass over their text.
/// </summary>
internal sealed class MiniJson
{
    private readonly string _s;
    private int _i;

    public MiniJson(string text)
    {
        _s = text;
        _i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
    }

    public int Position => _i;

    public static Exception Error(string what, int at) => new FormatException($"catalog JSON: {what} at offset {at}");

    public void Ws()
    {
        while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\n' || _s[_i] == '\r' || _s[_i] == '\t')) _i++;
    }

    public char Peek()
    {
        Ws();
        return _i < _s.Length ? _s[_i] : '\0';
    }

    public void Expect(char c)
    {
        if (Peek() != c) throw Error($"expected '{c}'", _i);
        _i++;
    }

    /// <summary>Walks the members of an object: <paramref name="member"/> is called with each key, positioned at its
    /// value, and must read or skip it.</summary>
    public void Object(Action<string> member)
    {
        Expect('{');
        if (Peek() == '}')
        {
            _i++;
            return;
        }
        while (true)
        {
            var key = String();
            Expect(':');
            member(key);
            var c = Peek();
            _i++;
            if (c == '}') return;
            if (c != ',') throw Error("expected ',' or '}'", _i - 1);
        }
    }

    /// <summary>Walks the elements of an array: <paramref name="element"/> must read or skip each.</summary>
    public void Array(Action element)
    {
        Expect('[');
        if (Peek() == ']')
        {
            _i++;
            return;
        }
        while (true)
        {
            element();
            var c = Peek();
            _i++;
            if (c == ']') return;
            if (c != ',') throw Error("expected ',' or ']'", _i - 1);
        }
    }

    public object? Value()
    {
        var c = Peek();
        switch (c)
        {
            case '{':
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                Object(k => d[k] = Value());
                return d;
            }
            case '[':
            {
                var l = new List<object?>();
                Array(() => l.Add(Value()));
                return l;
            }
            case '"':
                return String();
            case 't':
                Literal("true");
                return true;
            case 'f':
                Literal("false");
                return false;
            case 'n':
                Literal("null");
                return null;
            default:
                return Number();
        }
    }

    public void SkipValue()
    {
        var c = Peek();
        if (c == '"')
        {
            SkipString();
            return;
        }
        if (c != '{' && c != '[')
        {
            Value();
            return;
        }
        // brackets, string-aware
        var depth = 0;
        while (_i < _s.Length)
        {
            var ch = _s[_i];
            if (ch == '"')
            {
                SkipString();
                continue;
            }
            _i++;
            if (ch == '{' || ch == '[') depth++;
            else if (ch == '}' || ch == ']')
            {
                if (--depth == 0) return;
            }
        }
        throw Error("unterminated value", _i);
    }

    private void SkipString()
    {
        _i++; // opening quote
        while (_i < _s.Length)
        {
            var ch = _s[_i++];
            if (ch == '\\') _i++;
            else if (ch == '"') return;
        }
        throw Error("unterminated string", _i);
    }

    private void Literal(string lit)
    {
        if (string.CompareOrdinal(_s, _i, lit, 0, lit.Length) != 0) throw Error($"expected {lit}", _i);
        _i += lit.Length;
    }

    private double Number()
    {
        var start = _i;
        while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
        if (start == _i) throw Error("unexpected character", _i);
        return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public string String()
    {
        if (Peek() != '"') throw Error("expected a string", _i);
        _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length)
        {
            var ch = _s[_i++];
            if (ch == '"') return sb.ToString();
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }
            if (_i >= _s.Length) break;
            var e = _s[_i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (_i + 4 > _s.Length) throw Error("bad \\u escape", _i);
                    sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
        throw Error("unterminated string", _i);
    }
}
