// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace NAPLPSSite;

/// <summary>
/// The landing page: a hero over the repo's README rendered to HTML. README.md is the single
/// source of truth for what the project is, so the site reads it rather than keeping a second
/// copy that drifts. The markdown is authored for GitHub, though, so every link and image in it
/// points somewhere that does not exist on this site - see <see cref="Rewrite"/>.
/// </summary>
public static class Home
{
    private const string RepoBlob = "https://github.com/FoxCouncil/NAPLPS/blob/main/";
    private const string RepoTree = "https://github.com/FoxCouncil/NAPLPS/tree/main/";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static void Write(string root, string baseUrl, string repoRoot, List<RenderInfo> renders, string sha, DateTimeOffset at)
    {
        var readmePath = Path.Combine(repoRoot, "README.md");

        if (!File.Exists(readmePath))
        {
            Console.WriteLine("home   : no README.md, skipped");
            return;
        }

        var markdown = File.ReadAllText(readmePath);
        var body = Rewrite(Markdown.ToHtml(markdown, Pipeline), baseUrl, renders);

        const string description = "Telidraw is a from-scratch NAPLPS toolkit for .NET: parser, renderer, encoder, "
                                 + "and a browser-based editor for ANSI X3.110-1983 videotex artwork.";

        var ld = Html.BreadcrumbLd(baseUrl, ("Home", "index.html"));

        var sb = new StringBuilder();
        sb.Append(Html.Head("Telidraw — NAPLPS toolkit and editor", description, "index.html", baseUrl,
            renders.FirstOrDefault()?.PosterAsset, 0, ld,
            "NAPLPS, Telidraw, Telidon, Prodigy, videotex, ANSI X3.110, retrocomputing, vector graphics"));

        sb.AppendLine("<section class='hero'>");
        sb.AppendLine("<h1>Telidraw</h1>");
        sb.AppendLine("<p class='tagline'>A NAPLPS toolkit and editor for the modern web. Parse, render, author and export "
                    + "ANSI X3.110-1983 videotex artwork.</p>");
        sb.AppendLine("<p class='cta'>");
        sb.AppendLine("<a class='button primary' href='editor/'>Open the editor</a>");
        sb.AppendLine($"<a class='button' href='gallery/index.html'>Browse {renders.Count:N0} renders</a>");
        sb.AppendLine("</p>");
        sb.AppendLine("</section>");

        sb.AppendLine("<article class='readme'>");
        sb.AppendLine(body);
        sb.AppendLine("</article>");

        sb.Append(Html.Foot(sha, at));
        Pages.WriteFile(root, "index.html", sb.ToString());

        Console.WriteLine($"home   : README.md rendered, {Html.Bytes(Encoding.UTF8.GetByteCount(body))} of HTML");
    }

    /// <summary>
    /// README.md is written against the repo, so its relative links resolve to files that were
    /// never published. Baseline APNGs are re-pointed at the copies the site already stores;
    /// everything else relative goes back to GitHub rather than 404ing here.
    /// </summary>
    private static string Rewrite(string html, string baseUrl, List<RenderInfo> renders)
    {
        // Baseline APNGs referenced from the README's tile table. Keyed on file name because the
        // README spells the repo path and the site stores content-addressed assets.
        var byBaseline = new Dictionary<string, RenderInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in renders)
        {
            byBaseline[Path.GetFileName(r.SourceRelative) + ".apng"] = r;
        }

        html = Regex.Replace(html, @"(?<attr>src|href)=""NAPLPSTests/Visual/Baselines/(?<file>[^""]+)""", m =>
        {
            var file = m.Groups["file"].Value;
            var attr = m.Groups["attr"].Value.ToLowerInvariant();

            if (!byBaseline.TryGetValue(file, out var r))
            {
                return $"{attr}=\"{RepoBlob}NAPLPSTests/Visual/Baselines/{file}\"";
            }

            // href goes to the render's own page; src stays an image, and uses the still so the
            // landing page does not pull six full animations before the reader has scrolled.
            return attr == "href"
                ? $"href=\"r/{r.Slug}.html\""
                : $"src=\"{r.ThumbAsset}\"";
        }, RegexOptions.IgnoreCase);

        // The README links the published site absolutely, because on GitHub it has to. Here those
        // are self-links: strip the origin so they stay inside whatever build this is.
        html = html.Replace(baseUrl.TrimEnd('/') + "/", string.Empty, StringComparison.OrdinalIgnoreCase);

        // The pre-domain URL, for builds that predate the move or run with a different --base-url.
        html = html.Replace("https://foxcouncil.github.io/NAPLPS/", "gallery/index.html", StringComparison.OrdinalIgnoreCase);

        // "You are here" in the doc map points at README.md itself, which is this page.
        html = Regex.Replace(html, @"href=""README\.md""", "href=\"index.html\"", RegexOptions.IgnoreCase);

        // Everything else relative is repo content: docs, LICENSE, IDEAS.md, tools/. Send it to
        // GitHub. Directory links (trailing slash) need the tree view, files need blob.
        html = Regex.Replace(html, @"href=""(?![a-z]+:|//|#|/)(?<path>[^""]+)""", m =>
        {
            var path = m.Groups["path"].Value;

            if (path.StartsWith("index.html", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("gallery/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("r/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("editor/", StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            var prefix = path.EndsWith('/') ? RepoTree : RepoBlob;

            return $"href=\"{prefix}{path}\"";
        }, RegexOptions.IgnoreCase);

        return html;
    }
}
