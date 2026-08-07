// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Text;
using System.Text.Json;

namespace NAPLPSSite;

/// <summary>
/// Page rendering. Every page carries a full metadata block - canonical URL, description, Open
/// Graph, Twitter card and JSON-LD - because the point of publishing the corpus is that these
/// renders become findable. A gallery that crawlers cannot index is just a local viewer on a
/// server.
/// </summary>
public static class Html
{
    public const string SiteName = "Telidraw Visual Corpus";

    public const string Repo = "https://github.com/FoxCouncil/NAPLPS";

    public static string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <param name="depth">How many directory levels below the site root this page sits.</param>
    public static string Head(string title, string description, string canonicalPath, string baseUrl, string? ogImagePath, int depth, string? jsonLd = null, string? keywords = null)
    {
        var up = depth == 0 ? "" : string.Concat(Enumerable.Repeat("../", depth));
        var canonical = $"{baseUrl.TrimEnd('/')}/{canonicalPath.TrimStart('/')}";
        var ogImage = ogImagePath is null ? null : $"{baseUrl.TrimEnd('/')}/{ogImagePath.TrimStart('/')}";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<!-- Woof, you're looking and found a foxie! -->");
        sb.AppendLine("<html lang='en'>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset='utf-8'>");
        sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.AppendLine($"<title>{Encode(title)}</title>");
        sb.AppendLine($"<meta name='description' content='{Encode(description)}'>");

        if (keywords is not null)
        {
            sb.AppendLine($"<meta name='keywords' content='{Encode(keywords)}'>");
        }

        sb.AppendLine($"<link rel='canonical' href='{Encode(canonical)}'>");
        sb.AppendLine("<meta name='robots' content='index, follow, max-image-preview:large'>");
        sb.AppendLine($"<meta name='generator' content='Telidraw'>");

        // Open Graph
        sb.AppendLine("<meta property='og:type' content='website'>");
        sb.AppendLine($"<meta property='og:site_name' content='{Encode(SiteName)}'>");
        sb.AppendLine($"<meta property='og:title' content='{Encode(title)}'>");
        sb.AppendLine($"<meta property='og:description' content='{Encode(description)}'>");
        sb.AppendLine($"<meta property='og:url' content='{Encode(canonical)}'>");

        if (ogImage is not null)
        {
            sb.AppendLine($"<meta property='og:image' content='{Encode(ogImage)}'>");
            sb.AppendLine("<meta property='og:image:type' content='image/png'>");
            sb.AppendLine($"<meta property='og:image:alt' content='{Encode(title)}'>");
            sb.AppendLine("<meta name='twitter:card' content='summary_large_image'>");
            sb.AppendLine($"<meta name='twitter:image' content='{Encode(ogImage)}'>");
        }
        else
        {
            sb.AppendLine("<meta name='twitter:card' content='summary'>");
        }

        sb.AppendLine($"<meta name='twitter:title' content='{Encode(title)}'>");
        sb.AppendLine($"<meta name='twitter:description' content='{Encode(description)}'>");

        if (jsonLd is not null)
        {
            sb.AppendLine($"<script type='application/ld+json'>{jsonLd}</script>");
        }

        sb.AppendLine($"<link rel='stylesheet' href='{up}style.css'>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<header class='site'>");
        sb.AppendLine($"<a class='brand' href='{up}index.html'>Telidraw <span>NAPLPS toolkit</span></a>");
        sb.AppendLine($"<nav><a href='{up}gallery/index.html'>Gallery</a><a href='{up}changes/index.html'>Changes</a><a href='{up}editor/'>Editor</a>"
                    + $"<a href='{Repo}/releases'>Releases</a><a href='{Repo}/issues'>Issues</a><a href='{Repo}'>Repo</a></nav>");
        sb.AppendLine("</header>");
        sb.AppendLine("<main>");

        return sb.ToString();
    }

    public static string Foot(string builtSha, DateTimeOffset builtAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("</main>");
        sb.AppendLine("<footer class='site'>");
        sb.AppendLine($"<p>Rendered by the Telidraw at <code>{Encode(builtSha[..Math.Min(8, builtSha.Length)])}</code> on {builtAt:yyyy-MM-dd}. ");
        sb.AppendLine("Artwork is the property of its original authors; this corpus exists to verify the renderer.</p>");
        sb.AppendLine("</footer>");
        sb.AppendLine("<!-- Yip, Fox loves you! -->");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    public static string BreadcrumbLd(string baseUrl, params (string Name, string Path)[] crumbs)
    {
        var items = crumbs.Select((c, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = c.Name,
            ["item"] = $"{baseUrl.TrimEnd('/')}/{c.Path.TrimStart('/')}",
        }).ToArray();

        return Json(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "BreadcrumbList",
            ["itemListElement"] = items,
        });
    }

    public static string ImageObjectLd(string baseUrl, RenderInfo r, string canonicalPath)
    {
        return Json(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "ImageObject",
            ["name"] = r.Title,
            ["description"] = Describe(r),
            ["contentUrl"] = $"{baseUrl.TrimEnd('/')}/{r.ApngAsset}",
            ["thumbnailUrl"] = $"{baseUrl.TrimEnd('/')}/{r.ThumbAsset}",
            ["url"] = $"{baseUrl.TrimEnd('/')}/{canonicalPath}",
            ["width"] = r.Width,
            ["height"] = r.Height,
            ["encodingFormat"] = "image/apng",
            ["isPartOf"] = new Dictionary<string, object>
            {
                ["@type"] = "Collection",
                ["name"] = SiteName,
                ["url"] = baseUrl,
            },
        });
    }

    public static string Describe(RenderInfo r)
    {
        var frames = r.FrameCount == 1 ? "a single frame" : $"{r.FrameCount:N0} frames";

        // Prodigy and Telidon qualify the format; plain NAPLPS would otherwise stutter as
        // "NAPLPS NAPLPS artwork".
        var kind = r.SystemType.Equals("NAPLPS", StringComparison.OrdinalIgnoreCase)
            ? "NAPLPS"
            : $"{r.SystemType} NAPLPS";

        return $"{r.Title}, {kind} artwork from the {r.Collection} collection, "
             + $"decoded from {r.CommandCount:N0} coded commands and rendered to {frames} at {r.Width}x{r.Height}.";
    }

    public static string Bytes(long n)
    {
        return n >= 1048576 ? $"{n / 1048576.0:N1} MB" : n >= 1024 ? $"{n / 1024.0:N0} KB" : $"{n} B";
    }
}
