// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Globalization;
using System.Text.RegularExpressions;

namespace NAPLPSApp.Editor;

/// <summary>
/// Reads/writes a single-line `// ref-image:` directive embedded in Telidraw (.td) source.
/// The directive is a plain-old line comment — the compiler's lexer already skips // lines,
/// so the same file stays compile-clean whether or not the editor understands the marker.
/// Format:
///   // ref-image: path="/abs/path.png" x=0.1 y=0.05 w=0.8 h=0.6 opacity=0.5 visible=1
/// Paths with quotes or backslashes are escaped in the usual C way.
/// </summary>
public static class ReferenceImageSerializer
{
    public readonly record struct Spec(string Path, float X, float Y, float Width, float Height, double Opacity, bool Visible);

    private static readonly Regex Line = new(
        @"^\s*//\s*ref-image:\s*path=""(?<p>(?:\\.|[^""\\])*)""\s+x=(?<x>[-0-9.eE+]+)\s+y=(?<y>[-0-9.eE+]+)\s+w=(?<w>[-0-9.eE+]+)\s+h=(?<h>[-0-9.eE+]+)\s+opacity=(?<o>[-0-9.eE+]+)\s+visible=(?<v>[01])",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static string Prepend(string tdSource, ReferenceImage? image)
    {
        if (image == null || string.IsNullOrEmpty(image.SourcePath))
        {
            return tdSource;
        }

        var esc = image.SourcePath.Replace(@"\", @"\\").Replace("\"", "\\\"");
        var line = string.Format(CultureInfo.InvariantCulture,
            "// ref-image: path=\"{0}\" x={1} y={2} w={3} h={4} opacity={5} visible={6}\n",
            esc, image.X, image.Y, image.Width, image.Height, image.Opacity, image.IsVisible ? 1 : 0);

        return line + tdSource;
    }

    public static Spec? Extract(string tdSource)
    {
        var m = Line.Match(tdSource);
        if (!m.Success) { return null; }

        try
        {
            var path = m.Groups["p"].Value.Replace("\\\"", "\"").Replace(@"\\", @"\");
            return new Spec(
                path,
                float.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["o"].Value, CultureInfo.InvariantCulture),
                m.Groups["v"].Value == "1");
        }
        catch
        {
            return null;
        }
    }
}
