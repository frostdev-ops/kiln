// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Text;

namespace Kiln.Analyzers;

/// <summary>A JSON value with where it sits in its file: what KILN124 needs to point at a data pack's key and its fix to move
/// it (Kiln's data files take // and /* */ comments and trailing commas). Null for text that does not read as JSON.</summary>
internal sealed class JsonSpans
{
    public enum Kind { Object, Array, String, Number, Literal }

    public Kind Type;
    public int Start, End;                 // the value's text, [Start, End)
    public string Text = "";               // a string's value, a number's or literal's text
    public List<Member> Members = new();   // an object's
    public List<JsonSpans> Items = new();  // an array's

    public sealed class Member
    {
        public string Key = "";
        public int KeyStart, KeyEnd;       // the key's quoted text
        public int Start;                  // where the member starts (its key)
        public JsonSpans Value = null!;
    }

    public Member? Find(string key)
    {
        foreach (var m in Members)
            if (m.Key == key) return m;
        return null;
    }

    public static JsonSpans? Parse(string text)
    {
        var p = new Reader(text);
        try
        {
            p.Skip();
            var v = p.Value(0);
            p.Skip();
            return p.At == text.Length ? v : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Reader(string s)
    {
        public int At;

        public void Skip()
        {
            while (At < s.Length)
            {
                char c = s[At];
                if (char.IsWhiteSpace(c)) At++;
                else if (c == '/' && At + 1 < s.Length && s[At + 1] == '/') { while (At < s.Length && s[At] != '\n') At++; }
                else if (c == '/' && At + 1 < s.Length && s[At + 1] == '*')
                {
                    int end = s.IndexOf("*/", At + 2, StringComparison.Ordinal);
                    At = end < 0 ? throw new FormatException() : end + 2;
                }
                else break;
            }
        }

        public JsonSpans Value(int depth)
        {
            if (depth > 64 || At >= s.Length) throw new FormatException();
            var v = new JsonSpans { Start = At };
            char c = s[At];
            if (c == '{')
            {
                v.Type = Kind.Object;
                At++;
                while (true)
                {
                    Skip();
                    if (At < s.Length && s[At] == '}') { At++; break; }
                    if (At >= s.Length || s[At] != '"') throw new FormatException();
                    var m = new Member { Start = At, KeyStart = At };
                    m.Key = Str();
                    m.KeyEnd = At;
                    Skip();
                    if (At >= s.Length || s[At] != ':') throw new FormatException();
                    At++;
                    Skip();
                    m.Value = Value(depth + 1);
                    v.Members.Add(m);
                    Skip();
                    if (At < s.Length && s[At] == ',') { At++; continue; }
                    if (At < s.Length && s[At] == '}') { At++; break; }
                    throw new FormatException();
                }
            }
            else if (c == '[')
            {
                v.Type = Kind.Array;
                At++;
                while (true)
                {
                    Skip();
                    if (At < s.Length && s[At] == ']') { At++; break; }
                    v.Items.Add(Value(depth + 1));
                    Skip();
                    if (At < s.Length && s[At] == ',') { At++; continue; }
                    if (At < s.Length && s[At] == ']') { At++; break; }
                    throw new FormatException();
                }
            }
            else if (c == '"')
            {
                v.Type = Kind.String;
                v.Text = Str();
            }
            else
            {
                int start = At;
                while (At < s.Length && (char.IsLetterOrDigit(s[At]) || s[At] is '-' or '+' or '.')) At++;
                if (At == start) throw new FormatException();
                v.Text = s.Substring(start, At - start);
                v.Type = v.Text is "true" or "false" or "null" ? Kind.Literal : Kind.Number;
            }
            v.End = At;
            return v;
        }

        private string Str()
        {
            At++;  // "
            var sb = new StringBuilder();
            while (At < s.Length && s[At] != '"')
            {
                if (s[At] == '\\')
                {
                    if (At + 1 >= s.Length) throw new FormatException();
                    char e = s[At + 1];
                    if (e == 'u' && At + 5 < s.Length)
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(At + 2, 4), 16));
                        At += 6;
                        continue;
                    }
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => e });
                    At += 2;
                    continue;
                }
                sb.Append(s[At++]);
            }
            if (At >= s.Length) throw new FormatException();
            At++;  // "
            return sb.ToString();
        }
    }
}
