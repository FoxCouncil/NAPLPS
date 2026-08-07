// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Text;
using System.Text.RegularExpressions;

namespace NAPLPSSite;

/// <summary>
/// Lays generated markup out with one tab per level of nesting.
///
/// The pages are assembled a line at a time, which produces correct but completely flat HTML.
/// Anyone who opens View Source deserves better than that, and the repo's .editorconfig says tabs.
///
/// Script, style and pre blocks are shifted as a unit but keep their own internal formatting:
/// re-flowing their contents would change what they mean.
/// </summary>
public static partial class Indenter
{
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>Elements whose contents are not markup.</summary>
    private static readonly string[] RawElements = ["script", "style", "pre", "textarea"];

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)[^>]*?(/?)>")]
    private static partial Regex TagPattern();

    public static string Apply(string markup)
    {
        var lines = markup.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        int depth = 0;
        string? rawTag = null;
        int rawDepth = 0;
        var rawBlock = new List<string>();

        foreach (var original in lines)
        {
            var line = original.TrimEnd();

            if (rawTag is not null)
            {
                if (line.Contains($"</{rawTag}>", StringComparison.OrdinalIgnoreCase))
                {
                    EmitRaw(sb, rawBlock, rawDepth + 1);
                    rawBlock.Clear();
                    rawTag = null;
                    sb.Append('\t', rawDepth).Append(line.TrimStart()).Append('\n');
                    // Opening the block pushed a level for its contents; closing it takes that
                    // level back. Without this every script, style or pre block shifts the whole
                    // rest of the document one tab deeper.
                    depth = rawDepth;
                }
                else
                {
                    rawBlock.Add(line);
                }

                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                sb.Append('\n');

                continue;
            }

            // A line that starts by closing something belongs one level further out.
            if (trimmed.StartsWith("</", StringComparison.Ordinal))
            {
                depth = Math.Max(0, depth - 1);
            }

            sb.Append('\t', depth).Append(trimmed).Append('\n');

            var opened = OpenedRawElement(trimmed);

            if (opened is not null)
            {
                rawTag = opened;
                rawDepth = depth;
                depth++;

                continue;
            }

            depth = Math.Max(0, depth + NetDepth(trimmed));
        }

        return sb.ToString();
    }

    private static void EmitRaw(StringBuilder sb, List<string> block, int depth)
    {
        if (block.Count == 0)
        {
            return;
        }

        // Strip the common leading whitespace so the block sits at our indent while keeping the
        // relative shape of its contents.
        int common = block.Where(l => l.Trim().Length > 0)
                          .Select(l => l.Length - l.TrimStart().Length)
                          .DefaultIfEmpty(0)
                          .Min();

        foreach (var line in block)
        {
            if (line.Trim().Length == 0)
            {
                sb.Append('\n');

                continue;
            }

            sb.Append('\t', depth).Append(line[common..]).Append('\n');
        }
    }

    /// <summary>Name of a raw-content element opened but not closed on this line.</summary>
    private static string? OpenedRawElement(string line)
    {
        foreach (var tag in RawElements)
        {
            if (line.Contains($"<{tag}", StringComparison.OrdinalIgnoreCase)
                && !line.Contains($"</{tag}>", StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>How much a line changes nesting depth: elements opened minus elements closed.</summary>
    private static int NetDepth(string line)
    {
        int net = 0;

        foreach (Match m in TagPattern().Matches(line))
        {
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value;
            var selfClosed = m.Groups[3].Value == "/";

            if (selfClosed || Void.Contains(name) || name.StartsWith('!'))
            {
                continue;
            }

            net += closing ? -1 : 1;
        }

        // The leading close was already accounted for before the line was written.
        if (line.StartsWith("</", StringComparison.Ordinal))
        {
            net += 1;
        }

        return net;
    }
}
