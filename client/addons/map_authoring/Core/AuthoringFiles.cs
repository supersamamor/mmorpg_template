using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Game.Client.MapAuthoring;

/// <summary>One line of the export's report: tab-separated, so the editor can read what the separate headless process found.</summary>
/// <param name="Ok">True for the line that says a map was exported, false for a problem.</param>
/// <param name="Map">The map's file stem.</param>
/// <param name="Path">The node to look at; may be empty.</param>
/// <param name="Message">The text.</param>
public sealed record ExportReportLine(bool Ok, string Map, string Path, string Message)
{
    private const string OkWord = "ok";
    private const string ErrorWord = "error";

    /// <summary>The line's text.</summary>
    public string Format() => string.Join("\t", Ok ? OkWord : ErrorWord, Map, Path, Message.Replace('\t', ' ').Replace('\n', ' '));

    /// <summary>Reads a report's text; lines it cannot read are dropped.</summary>
    public static List<ExportReportLine> ParseAll(string text)
    {
        var lines = new List<ExportReportLine>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 4) lines.Add(new ExportReportLine(parts[0] == OkWord, parts[1], parts[2], parts[3]));
        }
        return lines;
    }
}

/// <summary>Text the plugin writes: the first version of an authoring scene.</summary>
public static class AuthoringTemplate
{
    /// <summary>An authoring scene that instances the visual scene and has an empty <c>Markers</c> node.</summary>
    public static string Scene(string mapId, int width, int height, double slopeLimitDegrees, string visualScenePath)
    {
        var text = new StringBuilder();
        text.Append("[gd_scene format=3]\n\n");
        text.Append("[ext_resource type=\"PackedScene\" path=\"").Append(visualScenePath).Append("\" id=\"1_visual\"]\n\n");
        text.Append("[node name=\"MapAuthoring\" type=\"Node3D\"]\n");
        text.Append("metadata/").Append(MapMeta.MapId).Append(" = \"").Append(mapId).Append("\"\n");
        text.Append("metadata/").Append(MapMeta.Width).Append(" = ").Append(width.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("metadata/").Append(MapMeta.Height).Append(" = ").Append(height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("metadata/").Append(MapMeta.SlopeLimit).Append(" = ").Append(slopeLimitDegrees.ToString("0.0###", CultureInfo.InvariantCulture)).Append("\n\n");
        text.Append("[node name=\"Visual\" parent=\".\" instance=ExtResource(\"1_visual\")]\n\n");
        text.Append("[node name=\"Markers\" type=\"Node3D\" parent=\".\"]\n");
        return text.ToString();
    }

    /// <summary>The file stem of an authoring scene path, or null when the path is not one.</summary>
    public static string? StemOf(string authoringPath)
    {
        var suffix = Game.Content.Maps.MapSceneHash.AuthoringSuffix;
        if (!authoringPath.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var start = authoringPath.LastIndexOf('/') + 1;
        return authoringPath.Substring(start, authoringPath.Length - start - suffix.Length);
    }
}
