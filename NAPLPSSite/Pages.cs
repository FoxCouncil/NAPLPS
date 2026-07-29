// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Text;

namespace NAPLPSSite;

public static class Pages
{
    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Markup gets laid out properly; xml, css and js are written as-is.
        var text = relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? Indenter.Apply(content)
            : content;

        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    public static void WriteGallery(string root, string baseUrl, List<RenderInfo> renders, List<CommitInfo> commits, string sha, DateTimeOffset at)
    {
        var totalFrames = renders.Sum(r => (long)r.FrameCount);

        var description = $"Every one of the {renders.Count:N0} NAPLPS artworks the toolbox renders, "
                        + $"{totalFrames:N0} frames in total, kept as byte-exact visual baselines so any change to the renderer is visible.";

        var ld = Html.BreadcrumbLd(baseUrl, ("Gallery", "index.html"));

        var sb = new StringBuilder();
        sb.Append(Html.Head(Html.SiteName, description, "index.html", baseUrl, renders.FirstOrDefault()?.PosterAsset, 0, ld,
            "NAPLPS, Telidon, Prodigy, videotex, ANSI X3.110, retrocomputing, vector graphics"));

        sb.AppendLine("<h1>NAPLPS Visual Corpus</h1>");
        sb.AppendLine($"<p class='lede'>{Html.Encode(description)}</p>");

        sb.AppendLine("<div class='stats'>");
        sb.AppendLine($"<span><b>{renders.Count:N0}</b> renders</span>");
        sb.AppendLine($"<span><b>{totalFrames:N0}</b> frames</span>");
        sb.AppendLine($"<span><b>{renders.Select(r => r.Collection).Distinct().Count()}</b> collections</span>");

        if (commits.Count > 0)
        {
            sb.AppendLine($"<span><a href='changes/index.html'><b>{commits.Count}</b> recent changes</a></span>");
        }

        sb.AppendLine("</div>");

        sb.AppendLine("<input id='filter' class='filter' type='search' placeholder='Filter by name or collection...' oninput='filterGrid(this.value)'>");

        foreach (var group in renders.GroupBy(r => r.Collection).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"<h2 id='{Html.Encode(group.Key.Replace(' ', '-').ToLowerInvariant())}'>{Html.Encode(group.Key)} <span class='count'>{group.Count()}</span></h2>");
            sb.AppendLine("<div class='grid'>");

            foreach (var r in group.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"<a class='card' href='r/{r.Slug}.html' data-name='{Html.Encode((r.Title + " " + r.Collection).ToLowerInvariant())}' data-apng='{r.ApngAsset}'>");
                // Stills by default - 372 animated APNGs loading at once would be brutal. The full
                // render is swapped in on hover, so the animation is paid for only when wanted.
                sb.AppendLine($"<img src='{r.ThumbAsset}' width='320' height='240' loading='lazy' decoding='async' alt='{Html.Encode(r.Title)} rendered'>");
                sb.AppendLine($"<span class='name'>{Html.Encode(r.Title)}</span>");
                sb.AppendLine($"<span class='meta'>{r.FrameCount:N0} frames &middot; {Html.Encode(r.SystemType)}</span>");
                sb.AppendLine("</a>");
            }

            sb.AppendLine("</div>");
        }

        sb.AppendLine("""
            <script>
            function filterGrid(q) {
              q = q.trim().toLowerCase();
              document.querySelectorAll('.card').forEach(c => {
                c.style.display = !q || c.dataset.name.includes(q) ? '' : 'none';
              });
              document.querySelectorAll('h2').forEach(h => {
                const grid = h.nextElementSibling;
                if (!grid || !grid.classList.contains('grid')) return;
                const any = [...grid.querySelectorAll('.card')].some(c => c.style.display !== 'none');
                h.style.display = any ? '' : 'none';
                grid.style.display = any ? '' : 'none';
              });
            }

            // Swap the still for the full animation while pointing at a card. Deliberately loaded
            // on demand rather than up front: the whole corpus is ~31MB of APNG and the grid holds
            // 372 of them. Reassigning src restarts the animation, so each hover plays from frame 0.
            (function () {
              if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

              document.querySelectorAll('.card').forEach(card => {
                const img = card.querySelector('img');
                const still = img.getAttribute('src');
                const anim = card.dataset.apng;
                if (!anim) return;

                let pending;
                const play = () => {
                  // Small delay so sweeping the pointer across the grid does not fetch everything.
                  pending = setTimeout(() => { img.src = anim; card.classList.add('playing'); }, 120);
                };
                const stop = () => {
                  clearTimeout(pending);
                  if (img.getAttribute('src') !== still) { img.src = still; }
                  card.classList.remove('playing');
                };

                card.addEventListener('mouseenter', play);
                card.addEventListener('mouseleave', stop);
                card.addEventListener('focus', play);
                card.addEventListener('blur', stop);
              });
            })();
            </script>
            """);

        sb.Append(Html.Foot(sha, at));
        Write(root, "index.html", sb.ToString());
    }

    public static void WriteRenderPages(string root, string baseUrl, List<RenderInfo> renders, string sha, DateTimeOffset at)
    {
        foreach (var r in renders)
        {
            var canonical = $"r/{r.Slug}.html";
            var description = Html.Describe(r);
            var ld = Html.ImageObjectLd(baseUrl, r, canonical);

            var sb = new StringBuilder();
            sb.Append(Html.Head($"{r.Title} — {Html.SiteName}", description, canonical, baseUrl, r.PosterAsset, 1, ld,
                $"{r.Title}, {r.Collection}, {r.SystemType}, NAPLPS, videotex, retrocomputing"));

            sb.AppendLine($"<!-- *licks* {Html.Encode(r.Title)}, YUM! -->");
            sb.AppendLine($"<nav class='crumb'><a href='../index.html'>Gallery</a> / <span>{Html.Encode(r.Collection)}</span> / <span>{Html.Encode(r.Title)}</span></nav>");
            sb.AppendLine($"<h1>{Html.Encode(r.Title)}</h1>");
            sb.AppendLine($"<p class='lede'>{Html.Encode(description)}</p>");

            // Canvas-based player so the drawing sequence can be scrubbed. The <img> underneath is
            // the fallback: without JavaScript, or if decoding fails, the browser still animates
            // the APNG on its own - just without transport controls.
            sb.AppendLine($"<figure class='stage player' data-src='../{r.ApngAsset}'>");
            sb.AppendLine($"<canvas width='{r.Width}' height='{r.Height}' aria-label='{Html.Encode(r.Title)} render'></canvas>");
            sb.AppendLine($"<noscript><img src='../{r.ApngAsset}' width='{r.Width}' height='{r.Height}' alt='{Html.Encode(r.Title)} animated render'></noscript>");
            sb.AppendLine($"<img class='p-fallback' hidden src='../{r.ApngAsset}' width='{r.Width}' height='{r.Height}' alt='{Html.Encode(r.Title)} animated render'>");

            sb.AppendLine("<div class='transport'>");
            sb.AppendLine("<button class='p-start' type='button' disabled title='Home' aria-label='First frame'>&#124;&laquo;</button>");
            sb.AppendLine("<button class='p-prev' type='button' disabled title='Left arrow' aria-label='Previous frame'>&laquo;</button>");
            sb.AppendLine("<button class='p-play' type='button' disabled title='Space'>&#9205; Play</button>");
            sb.AppendLine("<button class='p-next' type='button' disabled title='Right arrow' aria-label='Next frame'>&raquo;</button>");
            sb.AppendLine("<button class='p-end' type='button' disabled title='End' aria-label='Last frame'>&raquo;&#124;</button>");
            sb.AppendLine($"<input class='p-scrub' type='range' min='0' max='{Math.Max(0, r.FrameCount - 1)}' value='0' step='1' disabled aria-label='Scrub frames'>");
            sb.AppendLine("<button class='p-loop' type='button' disabled aria-pressed='false' title='Play once'>&#8635; Loop</button>");
            sb.AppendLine("<select class='p-rate' disabled aria-label='Playback rate'>");
            sb.AppendLine("<option value='0.25'>0.25x</option><option value='0.5'>0.5x</option><option value='1' selected>1x</option>");
            sb.AppendLine("<option value='2'>2x</option><option value='4'>4x</option><option value='8'>8x</option>");
            sb.AppendLine("</select>");
            sb.AppendLine("<span class='p-frame'>&mdash;</span>");
            sb.AppendLine("<span class='p-time'></span>");
            sb.AppendLine("</div>");

            sb.AppendLine($"<figcaption>Animated PNG &middot; {r.FrameCount:N0} frames &middot; {Html.Bytes(r.ApngBytes)} &middot; space to play/pause, arrows to step</figcaption>");
            sb.AppendLine("</figure>");

            sb.AppendLine("<table class='facts'>");
            Row(sb, "Source", r.SourceRelative);
            Row(sb, "Collection", r.Collection);
            Row(sb, "System", r.SystemType);
            Row(sb, "Coded commands", $"{r.CommandCount:N0}");
            Row(sb, "Frames", $"{r.FrameCount:N0}");
            Row(sb, "Canvas", $"{r.Width} x {r.Height}");
            Row(sb, "Source size", r.SourceBytes > 0 ? Html.Bytes(r.SourceBytes) : "n/a");
            Row(sb, "Render size", Html.Bytes(r.ApngBytes));
            sb.AppendLine("</table>");

            sb.AppendLine("<p class='links'>");
            sb.AppendLine($"<a href='../{r.ApngAsset}' download='{Html.Encode(r.Title)}.apng'>Download APNG</a>");
            sb.AppendLine($"<a href='../{r.PosterAsset}'>Poster frame PNG</a>");
            sb.AppendLine($"<a href='https://github.com/FoxCouncil/NAPLPS/blob/main/Examples/{Uri.EscapeDataString(r.SourceRelative).Replace("%2F", "/")}'>Source file</a>");
            sb.AppendLine("</p>");

            sb.AppendLine($"<script src='../{Player.FileName}' defer></script>");
            sb.Append(Html.Foot(sha, at));
            Write(root, canonical, sb.ToString());
        }

        static void Row(StringBuilder sb, string k, string v)
        {
            sb.AppendLine($"<tr><th>{Html.Encode(k)}</th><td>{Html.Encode(v)}</td></tr>");
        }
    }

    public static void WriteChangeIndex(string root, string baseUrl, List<CommitInfo> commits, string sha, DateTimeOffset at)
    {
        var description = commits.Count == 0
            ? "No recent baseline changes."
            : $"The last {commits.Count} commits that changed how the renderer draws, with before and after for every affected artwork.";

        var ld = Html.BreadcrumbLd(baseUrl, ("Gallery", "index.html"), ("Changes", "changes/index.html"));

        var sb = new StringBuilder();
        sb.Append(Html.Head($"Recent changes — {Html.SiteName}", description, "changes/index.html", baseUrl, null, 1, ld,
            "NAPLPS renderer changes, visual regression, before and after"));

        sb.AppendLine("<nav class='crumb'><a href='../index.html'>Gallery</a> / <span>Changes</span></nav>");
        sb.AppendLine("<h1>Recent baseline changes</h1>");
        sb.AppendLine($"<p class='lede'>{Html.Encode(description)}</p>");

        sb.AppendLine("<ol class='commits'>");

        foreach (var c in commits)
        {
            sb.AppendLine("<li>");
            sb.AppendLine($"<a class='subject' href='{c.Slug}.html'>{Html.Encode(c.Commit.Subject)}</a>");
            sb.AppendLine($"<span class='meta'><code>{Html.Encode(c.Commit.ShortSha)}</code> &middot; {Html.Encode(c.Commit.Author)} &middot; <time datetime='{c.Commit.Date:O}'>{c.Commit.Date:yyyy-MM-dd}</time></span>");
            var removedNote = c.Removed.Count > 0 ? $" &middot; {c.Removed.Count:N0} removed" : "";
            sb.AppendLine($"<span class='meta'>{c.Changes.Count:N0} artwork(s) changed &middot; {c.TotalDiffPixels:N0} pixels differ{removedNote}</span>");
            sb.AppendLine("</li>");
        }

        sb.AppendLine("</ol>");
        sb.Append(Html.Foot(sha, at));
        Write(root, "changes/index.html", sb.ToString());
    }

    public static void WriteCommitPages(string root, string baseUrl, List<CommitInfo> commits, string sha, DateTimeOffset at)
    {
        foreach (var c in commits)
        {
            var canonical = $"changes/{c.Slug}.html";
            var description = $"{c.Commit.Subject} — {c.Changes.Count:N0} artworks changed, {c.TotalDiffPixels:N0} pixels different, committed {c.Commit.Date:yyyy-MM-dd}.";
            var ld = Html.BreadcrumbLd(baseUrl, ("Gallery", "index.html"), ("Changes", "changes/index.html"), (c.Commit.ShortSha, canonical));

            var sb = new StringBuilder();
            sb.Append(Html.Head($"{c.Commit.Subject} — {Html.SiteName}", description, canonical, baseUrl,
                c.Changes.FirstOrDefault()?.DiffAsset, 1, ld, "NAPLPS, visual diff, renderer change"));

            sb.AppendLine("<nav class='crumb'><a href='../index.html'>Gallery</a> / <a href='index.html'>Changes</a> / <span>" + Html.Encode(c.Commit.ShortSha) + "</span></nav>");
            sb.AppendLine($"<h1>{Html.Encode(c.Commit.Subject)}</h1>");
            sb.AppendLine($"<p class='lede'><code>{Html.Encode(c.Commit.Sha)}</code><br>{Html.Encode(c.Commit.Author)} &middot; <time datetime='{c.Commit.Date:O}'>{c.Commit.Date:yyyy-MM-dd HH:mm}</time> &middot; {c.Changes.Count:N0} artwork(s)</p>");
            sb.AppendLine($"<p class='links'><a href='https://github.com/FoxCouncil/NAPLPS/commit/{Html.Encode(c.Commit.Sha)}'>View commit on GitHub</a></p>");

            if (c.Removed.Count > 0)
            {
                sb.AppendLine($"<p class='note'>Removed: {Html.Encode(string.Join(", ", c.Removed))}</p>");
            }

            if (c.Changes.Count == 0)
            {
                sb.AppendLine("<p class='meta'>No artwork renders changed in this commit.</p>");
            }

            foreach (var ch in c.Changes)
            {
                sb.AppendLine("<section class='change'>");
                sb.AppendLine($"<h2>{Html.Encode(ch.Title)}</h2>");

                if (ch.IsNew)
                {
                    sb.AppendLine("<p class='meta'>New baseline &mdash; no previous render to compare.</p>");
                    sb.AppendLine("<div class='pair'>");
                    sb.AppendLine($"<figure><figcaption>Added</figcaption><img src='../{ch.AfterApngAsset}' loading='lazy' alt='{Html.Encode(ch.Title)}'></figure>");
                    sb.AppendLine("</div>");
                }
                else
                {
                    var frameNote = ch.BeforeFrames != ch.AfterFrames
                        ? $" &middot; frames {ch.BeforeFrames:N0} &rarr; {ch.AfterFrames:N0}"
                        : "";

                    sb.AppendLine($"<p class='meta'>{ch.DiffPixels:N0} pixels differ across {ch.ChangedFrames:N0} frame(s){frameNote}</p>");

                    if (ch.Note is not null)
                    {
                        sb.AppendLine($"<p class='note'>{Html.Encode(ch.Note)}</p>");
                    }

                    sb.AppendLine("<div class='pair'>");
                    sb.AppendLine($"<figure><figcaption>Before</figcaption><img src='../{ch.BeforeApngAsset}' loading='lazy' alt='{Html.Encode(ch.Title)} before'></figure>");
                    sb.AppendLine($"<figure><figcaption>After</figcaption><img src='../{ch.AfterApngAsset}' loading='lazy' alt='{Html.Encode(ch.Title)} after'></figure>");

                    if (ch.DiffAsset is not null)
                    {
                        sb.AppendLine($"<figure><figcaption>Most-changed frame</figcaption><img src='../{ch.DiffAsset}' loading='lazy' alt='{Html.Encode(ch.Title)} difference'></figure>");
                    }

                    sb.AppendLine("</div>");
                }

                sb.AppendLine($"<p class='links'><a href='../r/{ch.Slug}.html'>Open {Html.Encode(ch.Title)}</a></p>");
                sb.AppendLine("</section>");
            }

            sb.Append(Html.Foot(sha, at));
            Write(root, canonical, sb.ToString());
        }
    }

    public static void WriteSitemap(string root, string baseUrl, List<RenderInfo> renders, List<CommitInfo> commits)
    {
        var b = baseUrl.TrimEnd('/');
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='UTF-8'?>".Replace('\'', '"'));
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\">");

        sb.AppendLine($"<url><loc>{b}/index.html</loc><lastmod>{today}</lastmod><changefreq>weekly</changefreq><priority>1.0</priority></url>");
        sb.AppendLine($"<url><loc>{b}/changes/index.html</loc><lastmod>{today}</lastmod><changefreq>weekly</changefreq><priority>0.8</priority></url>");

        foreach (var r in renders)
        {
            // Image sitemap entries: the artwork is the point of the page, so declare it explicitly.
            sb.AppendLine($"<url><loc>{b}/r/{r.Slug}.html</loc><lastmod>{today}</lastmod><changefreq>monthly</changefreq><priority>0.6</priority>"
                        + $"<image:image><image:loc>{b}/{r.PosterAsset}</image:loc>"
                        + $"<image:title>{System.Security.SecurityElement.Escape(r.Title)}</image:title>"
                        + $"<image:caption>{System.Security.SecurityElement.Escape(Html.Describe(r))}</image:caption>"
                        + "</image:image></url>");
        }

        foreach (var c in commits)
        {
            sb.AppendLine($"<url><loc>{b}/changes/{c.Slug}.html</loc><lastmod>{c.Commit.Date:yyyy-MM-dd}</lastmod><changefreq>never</changefreq><priority>0.4</priority></url>");
        }

        sb.AppendLine("</urlset>");
        Write(root, "sitemap.xml", sb.ToString());
    }

    public static void WriteRobots(string root, string baseUrl)
    {
        Write(root, "robots.txt", $"User-agent: *\nAllow: /\n\nSitemap: {baseUrl.TrimEnd('/')}/sitemap.xml\n");
    }

    /// <summary>RSS of recent renderer changes, so the corpus can be watched without polling git.</summary>
    public static void WriteFeed(string root, string baseUrl, List<CommitInfo> commits)
    {
        var b = baseUrl.TrimEnd('/');
        var sb = new StringBuilder();

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<rss version=\"2.0\"><channel>");
        sb.AppendLine($"<title>{System.Security.SecurityElement.Escape(Html.SiteName)}</title>");
        sb.AppendLine($"<link>{b}/index.html</link>");
        sb.AppendLine("<description>Changes to how the NAPLPS Toolbox renders its visual corpus.</description>");

        foreach (var c in commits)
        {
            sb.AppendLine("<item>");
            sb.AppendLine($"<title>{System.Security.SecurityElement.Escape(c.Commit.Subject)}</title>");
            sb.AppendLine($"<link>{b}/changes/{c.Slug}.html</link>");
            sb.AppendLine($"<guid isPermaLink=\"false\">{c.Commit.Sha}</guid>");
            sb.AppendLine($"<pubDate>{c.Commit.Date:R}</pubDate>");
            sb.AppendLine($"<description>{System.Security.SecurityElement.Escape($"{c.Changes.Count} artwork(s) changed, {c.TotalDiffPixels:N0} pixels different.")}</description>");
            sb.AppendLine("</item>");
        }

        sb.AppendLine("</channel></rss>");
        Write(root, "feed.xml", sb.ToString());
    }

    public static void WritePlayer(string root)
    {
        Write(root, Player.FileName, Player.Script);
    }

    public static void WriteStyle(string root)
    {
        Write(root, "style.css", """
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body { margin: 0; font: 15px/1.55 -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #0d1117; color: #c9d1d9; }
            a { color: #58a6ff; text-decoration: none; }
            a:hover { text-decoration: underline; }
            header.site { display: flex; align-items: center; gap: 24px; padding: 14px 24px; border-bottom: 1px solid #30363d; background: #161b22; position: sticky; top: 0; z-index: 5; }
            header.site .brand { font-weight: 700; color: #f0f6fc; font-size: 17px; }
            header.site .brand span { color: #8b949e; font-weight: 400; }
            header.site nav { margin-left: auto; display: flex; gap: 18px; }
            main { max-width: 1200px; margin: 0 auto; padding: 28px 24px 60px; }
            h1 { color: #f0f6fc; font-size: 26px; margin: 0 0 10px; }
            h2 { color: #f0f6fc; font-size: 18px; margin: 34px 0 12px; border-bottom: 1px solid #21262d; padding-bottom: 6px; }
            h2 .count { color: #8b949e; font-weight: 400; font-size: 14px; }
            .lede { color: #8b949e; margin: 0 0 18px; max-width: 74ch; }
            .crumb { color: #8b949e; margin-bottom: 12px; font-size: 14px; }
            .stats { display: flex; gap: 20px; flex-wrap: wrap; margin-bottom: 20px; padding: 12px 16px; background: #161b22; border: 1px solid #30363d; border-radius: 8px; }
            .stats b { color: #f0f6fc; font-size: 17px; }
            .filter { width: 100%; padding: 9px 12px; margin-bottom: 8px; background: #0d1117; border: 1px solid #30363d; border-radius: 8px; color: #c9d1d9; font-size: 14px; }
            .filter:focus { outline: none; border-color: #58a6ff; }
            .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 14px; }
            .card { display: block; background: #161b22; border: 1px solid #30363d; border-radius: 8px; overflow: hidden; transition: border-color .12s; }
            .card:hover, .card:focus { border-color: #58a6ff; text-decoration: none; outline: none; }
            .card.playing { border-color: #58a6ff; }
            .card.playing .name { color: #58a6ff; }
            .card img { display: block; width: 100%; height: auto; background: #000; image-rendering: pixelated; }
            .card .name { display: block; padding: 8px 10px 0; color: #f0f6fc; font-size: 14px; word-break: break-all; }
            .card .meta { display: block; padding: 2px 10px 9px; color: #8b949e; font-size: 12px; }
            .stage { margin: 0 0 22px; }
            .stage img { max-width: 100%; height: auto; border: 1px solid #30363d; border-radius: 8px; background: #000; image-rendering: pixelated; }
            .stage figcaption { color: #8b949e; font-size: 13px; margin-top: 8px; }
            .stage canvas { max-width: 100%; height: auto; border: 1px solid #30363d; border-radius: 8px; background: #000; image-rendering: pixelated; display: block; }
            .stage.failed canvas { display: none; }
            .transport { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 10px; padding: 9px 11px; background: #161b22; border: 1px solid #30363d; border-radius: 8px; }
            .transport button, .transport select { background: #0d1117; color: #c9d1d9; border: 1px solid #30363d; border-radius: 6px; padding: 5px 10px; cursor: pointer; font-size: 13px; }
            .transport button:hover:not(:disabled), .transport select:hover:not(:disabled) { border-color: #58a6ff; }
            .transport button:disabled, .transport select:disabled { opacity: .45; cursor: default; }
            .transport .p-play { min-width: 88px; font-weight: 600; }
            .transport .p-loop.on { background: #1f6feb; border-color: #1f6feb; color: #fff; }
            .transport .p-scrub { flex: 1; min-width: 160px; accent-color: #58a6ff; }
            .transport .p-frame { color: #f0f6fc; font-size: 13px; min-width: 150px; text-align: right; font-variant-numeric: tabular-nums; }
            .transport .p-time { color: #8b949e; font-size: 13px; min-width: 110px; text-align: right; font-variant-numeric: tabular-nums; }
            .facts { border-collapse: collapse; margin-bottom: 20px; }
            .facts th { text-align: left; color: #8b949e; font-weight: 400; padding: 5px 22px 5px 0; vertical-align: top; white-space: nowrap; }
            .facts td { padding: 5px 0; color: #f0f6fc; word-break: break-all; }
            .links { display: flex; gap: 18px; flex-wrap: wrap; }
            .commits { list-style: none; padding: 0; margin: 0; }
            .commits li { padding: 14px 16px; margin-bottom: 10px; background: #161b22; border: 1px solid #30363d; border-radius: 8px; }
            .commits .subject { display: block; color: #f0f6fc; font-size: 16px; font-weight: 600; margin-bottom: 4px; }
            .commits .meta, .change .meta { display: block; color: #8b949e; font-size: 13px; }
            .change { margin: 26px 0; padding-bottom: 20px; border-bottom: 1px solid #21262d; }
            .change h2 { margin-top: 0; border: none; }
            .note { color: #d29922; font-size: 13px; }
            .pair { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 14px; margin: 12px 0; }
            .pair figure { margin: 0; }
            .pair figcaption { color: #8b949e; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; margin-bottom: 5px; }
            .pair img { width: 100%; height: auto; border: 1px solid #30363d; border-radius: 6px; background: #000; image-rendering: pixelated; }
            footer.site { border-top: 1px solid #30363d; padding: 18px 24px; color: #8b949e; font-size: 13px; }
            code { font-family: 'Cascadia Code', ui-monospace, monospace; font-size: .92em; }
            """);
    }
}
