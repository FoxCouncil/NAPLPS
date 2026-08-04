// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NAPLPSApp.Editor;

/// <summary>
/// Reads/writes layer membership in Telidraw (.td) source via structured line comments.
///
/// Format:
///   // layer: name="Background" visible=1
///   ...commands in that layer...
///   // layer: name="Ink" visible=1
///   ...commands in that layer...
///
/// A marker applies to every subsequent command until the next marker. The Telidraw
/// lexer already skips // lines so these directives are compile-transparent.
/// </summary>
public static class LayerSerializer
{
    private static readonly Regex Marker = new(
        @"^\s*//\s*layer:\s*name=""(?<n>(?:\\.|[^""\\])*)""\s+visible=(?<v>[01])\s*$",
        RegexOptions.Compiled);

    /// <summary>Inject layer markers into a decompiled source. For each command in order,
    /// if its layer differs from the previous command's, a marker is inserted above it.</summary>
    public static string InjectLayerMarkers(string tdSource, NaplpsFormat format, LayerManager manager)
    {
        if (manager.Layers.Count == 0) { return tdSource; }

        // Build a flat list of "start of this layer run" positions keyed to command indices.
        // We walk the rendered source line-by-line and, on the first line that belongs to
        // each new run, emit the marker above it. Command-index → source-line mapping is
        // non-trivial since Decompiler output may have prologue lines — we do it by counting
        // non-comment, non-directive, non-blank lines that start with an opcode token.
        //
        // Simpler + good-enough: rebuild the source by prepending *all* markers at the very
        // top in sequence, followed by a command-by-command emit. Downside: we'd have to
        // re-decompile per command. That's invasive.
        //
        // Middle path: emit a SINGLE header with the layer table, then post-hoc inline
        // markers using a best-effort line walk. The compiler ignores unmatched markers
        // anyway, so this is robust across Decompiler format changes.

        var sb = new StringBuilder();

        // Layer-table header — describes every layer so even "empty" layers survive a
        // roundtrip. Runs that change the active layer are marked inline below.
        sb.Append("// layers-begin\n");
        foreach (var layer in manager.Layers)
        {
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "//   layer-def: id={0} name=\"{1}\" visible={2}\n",
                layer.Id, Escape(layer.Name), layer.IsVisible ? 1 : 0));
        }
        sb.Append("// layers-end\n");

        // Inline markers at layer-change boundaries. Only correct when the original source
        // emits one command per non-blank line; that's how NAPLPS's Telidraw decompiler
        // produces output today. If that changes, the markers degrade to "at the top of
        // the file" but parsing still succeeds.
        int cmdIdx = 0;
        int? lastLayerId = null;
        foreach (var line in tdSource.Split('\n'))
        {
            var trimmed = line.TrimStart();
            bool isCommandLine = !string.IsNullOrWhiteSpace(trimmed)
                                  && !trimmed.StartsWith("//")
                                  && !trimmed.StartsWith("#");

            if (isCommandLine && cmdIdx < format.Commands.Count)
            {
                int layerId = manager.GetLayerId(format.Commands[cmdIdx]);
                if (layerId != lastLayerId)
                {
                    var layer = manager.GetLayer(layerId);
                    if (layer != null)
                    {
                        sb.Append(string.Format(CultureInfo.InvariantCulture,
                            "// layer: name=\"{0}\" visible={1}\n",
                            Escape(layer.Name), layer.IsVisible ? 1 : 0));
                    }
                    lastLayerId = layerId;
                }
                cmdIdx++;
            }

            sb.Append(line);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Pull layer definitions + per-command assignments from a decorated source.
    /// Returns the layer table and a parallel list of layer ids (one per command in the
    /// compiled format). If no markers are present, returns null — caller should fall back
    /// to a single "Background" layer.</summary>
    public static LayerSpec? Extract(string tdSource)
    {
        var result = new LayerSpec();
        bool inDefs = false;
        var defLine = new Regex(@"^\s*//\s*layer-def:\s*id=(?<i>\d+)\s+name=""(?<n>(?:\\.|[^""\\])*)""\s+visible=(?<v>[01])\s*$", RegexOptions.Compiled);

        string? currentLayerName = null;
        bool currentVisible = true;

        foreach (var rawLine in tdSource.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("// layers-begin")) { inDefs = true; continue; }
            if (trimmed.StartsWith("// layers-end"))   { inDefs = false; continue; }

            if (inDefs)
            {
                var dm = defLine.Match(line);
                if (dm.Success)
                {
                    result.Defs.Add(new LayerDef(
                        int.Parse(dm.Groups["i"].Value, CultureInfo.InvariantCulture),
                        Unescape(dm.Groups["n"].Value),
                        dm.Groups["v"].Value == "1"));
                }
                continue;
            }

            var mm = Marker.Match(line);
            if (mm.Success)
            {
                currentLayerName = Unescape(mm.Groups["n"].Value);
                currentVisible   = mm.Groups["v"].Value == "1";
                continue;
            }

            bool isCommandLine = !string.IsNullOrWhiteSpace(trimmed)
                                  && !trimmed.StartsWith("//")
                                  && !trimmed.StartsWith("#");
            if (isCommandLine)
            {
                result.CommandLayerNames.Add(currentLayerName);
                result.CommandLayerVisible.Add(currentLayerName == null ? true : currentVisible);
            }
        }

        return result.Defs.Count > 0 || result.CommandLayerNames.Any(n => n != null) ? result : null;
    }

    private static string Escape(string s) => s.Replace(@"\", @"\\").Replace("\"", "\\\"");
    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace(@"\\", @"\");

    public readonly record struct LayerDef(int Id, string Name, bool Visible);

    public class LayerSpec
    {
        public List<LayerDef> Defs { get; } = [];
        public List<string?> CommandLayerNames { get; } = [];
        public List<bool> CommandLayerVisible { get; } = [];
    }
}
