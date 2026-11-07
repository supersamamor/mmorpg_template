using System;
using System.Collections.Generic;
using System.Text;

namespace Game.Content.Maps;

/// <summary>One bracketed section of a <c>.tscn</c> file and the properties under it.</summary>
public sealed class TscnSection
{
    /// <summary>Creates a section.</summary>
    public TscnSection(string tag) => Tag = tag;

    /// <summary>The word after the opening bracket: <c>gd_scene</c>, <c>ext_resource</c>, <c>sub_resource</c>, <c>node</c>, <c>connection</c>, <c>editable</c>.</summary>
    public string Tag { get; }

    /// <summary>The header's <c>key=value</c> pairs, values as written (strings keep their quotes).</summary>
    public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>key = value</c> lines below the header, values as written.</summary>
    public List<KeyValuePair<string, string>> Properties { get; } = new List<KeyValuePair<string, string>>();

    /// <summary>A header value without its quotes, or null when the header does not have it.</summary>
    public string? Text(string attribute) => Attributes.TryGetValue(attribute, out var value) ? TscnFile.Unquote(value) : null;
}

/// <summary>
/// Reads Godot's text scene format, far enough for <see cref="MapSceneHash"/>: sections, their header attributes and their properties. No engine and no I/O.
/// A value may span lines (dictionaries, long arrays), so values are scanned with their brackets and strings, not line by line.
/// </summary>
public static class TscnFile
{
    /// <summary>Parses scene text with any line endings. Throws <see cref="FormatException"/> when a section or value does not end, or text stands outside a section.</summary>
    public static List<TscnSection> Parse(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sections = new List<TscnSection>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n' || c == ' ' || c == '\t' || c == '﻿')
            {
                i++;
            }
            else if (c == ';')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '[')
            {
                var end = EndOfValue(text, i, stopAtSpace: false);
                sections.Add(ParseHeader(text.Substring(i + 1, end - i - 2)));
                i = end;
            }
            else
            {
                if (sections.Count == 0) throw new FormatException("Scene text starts with a property, not a section.");
                var equals = text.IndexOf('=', i);
                if (equals < 0) throw new FormatException("A property line has no '='.");
                var key = text.Substring(i, equals - i).Trim();
                var start = equals + 1;
                while (start < text.Length && text[start] == ' ') start++;
                var end = EndOfValue(text, start, stopAtSpace: false);
                sections[sections.Count - 1].Properties.Add(new KeyValuePair<string, string>(key, text.Substring(start, end - start).Trim()));
                i = end;
            }
        }
        return sections;
    }

    /// <summary>The text of a quoted value with its escapes undone; anything else comes back as it is.</summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return value;
        var text = new StringBuilder(value.Length);
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] == '\\' && i + 2 < value.Length) i++;
            text.Append(value[i]);
        }
        return text.ToString();
    }

    /// <summary>The value with white space removed outside strings, so a re-save that spaces it differently is the same value.</summary>
    public static string Squeeze(string value)
    {
        var text = new StringBuilder(value.Length);
        var inString = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (inString)
            {
                text.Append(c);
                if (c == '\\' && i + 1 < value.Length) text.Append(value[++i]);
                else if (c == '"') inString = false;
            }
            else if (c == '"')
            {
                inString = true;
                text.Append(c);
            }
            else if (c != ' ' && c != '\t' && c != '\n')
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    private static TscnSection ParseHeader(string header)
    {
        var i = 0;
        while (i < header.Length && header[i] != ' ' && header[i] != '\n') i++;
        var section = new TscnSection(header.Substring(0, i));
        while (i < header.Length)
        {
            if (header[i] == ' ' || header[i] == '\n')
            {
                i++;
                continue;
            }
            var equals = header.IndexOf('=', i);
            if (equals < 0) throw new FormatException($"Section [{section.Tag}] has an attribute with no '='.");
            var key = header.Substring(i, equals - i).Trim();
            var end = EndOfValue(header, equals + 1, stopAtSpace: true);
            section.Attributes[key] = header.Substring(equals + 1, end - equals - 1);
            i = end;
        }
        return section;
    }

    // One past the end of a value: the line break (or, in a header, the space) at bracket depth 0 outside any string. A value that opens with '[' ends at its closing bracket.
    private static int EndOfValue(string text, int start, bool stopAtSpace)
    {
        var depth = 0;
        var inString = false;
        var i = start;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                case '(':
                case '{':
                    depth++;
                    break;
                case ']':
                case ')':
                case '}':
                    depth--;
                    if (depth == 0 && c == ']' && text[start] == '[' && !stopAtSpace) return i + 1;
                    break;
                case '\n':
                    if (depth == 0) return i;
                    break;
                case ' ':
                    if (depth == 0 && stopAtSpace) return i;
                    break;
            }
        }
        if (inString || depth != 0) throw new FormatException("A value or section does not end before the end of the file.");
        return i;
    }
}
