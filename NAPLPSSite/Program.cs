// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Text;
using NAPLPS;
using NAPLPSSite;

// naplps-site --repo <root> --out <dir> [--base-url <url>] [--history 5]
var repo = ArgOr(args, "--repo", Directory.GetCurrentDirectory());
var outDir = ArgOr(args, "--out", Path.Combine(repo, "_site"));
var baseUrl = ArgOr(args, "--base-url", "https://foxcouncil.github.io/NAPLPS");
var history = int.Parse(ArgOr(args, "--history", "5"));

var baselinesDir = Path.Combine(repo, "NAPLPSTests", "Visual", "Baselines");
var examplesDir = Path.Combine(repo, "Examples");
const string BaselinesRepoPath = "NAPLPSTests/Visual/Baselines";

if (!Directory.Exists(baselinesDir))
{
    Console.Error.WriteLine($"baselines not found at {baselinesDir}");
    return 1;
}

if (Directory.Exists(outDir))
{
    Directory.Delete(outDir, recursive: true);
}

Directory.CreateDirectory(outDir);

var git = new Git(repo);
var assets = new Assets(outDir);
var builtSha = git.CurrentSha();
var builtAt = DateTimeOffset.UtcNow;

Console.WriteLine($"repo   : {repo}");
Console.WriteLine($"out    : {outDir}");
Console.WriteLine($"base   : {baseUrl}");

// ---------------------------------------------------------------- corpus

// Mirrors VisualTestContext.ForcedProdigyDirs: these collections are known-Prodigy even when the
// file lacks the A1 C8 domain marker, so reporting the auto-detected type would mislabel them.
var forcedProdigy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Ads From Preview Disks", "Screens From Preview Disks", "Anthony Wetzel", "Cyd Gorman 1", "Cyd Gorman 2",
};

var renders = new List<RenderInfo>();
var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var file in Directory.GetFiles(baselinesDir, "*.apng", SearchOption.AllDirectories).OrderBy(f => f))
{
    var rel = Path.GetRelativePath(baselinesDir, file).Replace('\\', '/');
    var sourceRel = rel[..^".apng".Length];
    var collection = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "Root";

    var bytes = File.ReadAllBytes(file);

    byte[] poster, thumb;
    int w, h;
    uint frames;

    try
    {
        (poster, thumb, w, h, frames) = Assets.RepresentativeFrame(bytes);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  skip {rel}: {ex.Message}");
        continue;
    }

    // Parse the source for metadata before building the record, so the page can say what the file
    // actually is rather than just how big its render came out.
    var sourcePath = Path.Combine(examplesDir, sourceRel.Replace('/', Path.DirectorySeparatorChar));
    var systemType = "Unknown";
    var commandCount = 0;
    long sourceBytes = 0;

    if (File.Exists(sourcePath))
    {
        sourceBytes = new FileInfo(sourcePath).Length;

        try
        {
            var top = sourceRel.Contains('/') ? sourceRel[..sourceRel.IndexOf('/')] : "";
            var forced = forcedProdigy.Contains(top) ? NaplpsSystemType.Prodigy : (NaplpsSystemType?)null;
            var parsed = NaplpsFormat.FromFile(sourcePath, forced);

            systemType = parsed.SystemType.ToString();
            commandCount = parsed.Commands.Count;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  meta {rel}: {ex.Message}");
        }
    }

    renders.Add(new RenderInfo
    {
        BaselineRelative = rel,
        SourceRelative = sourceRel,
        Slug = Slug(sourceRel, slugs),
        Title = Path.GetFileName(sourceRel),
        Collection = collection,
        Width = w,
        Height = h,
        FrameCount = frames,
        ApngBytes = bytes.Length,
        SourceBytes = sourceBytes,
        SystemType = systemType,
        CommandCount = commandCount,
        ApngAsset = assets.Store(bytes, "r", ".apng"),
        PosterAsset = assets.Store(poster, "p", ".png"),
        ThumbAsset = assets.Store(thumb, "t", ".png"),
    });
}

Console.WriteLine($"corpus : {renders.Count} renders");

// ---------------------------------------------------------------- history

var commits = new List<CommitInfo>();

foreach (var commit in git.CommitsTouching(BaselinesRepoPath, history))
{
    var info = new CommitInfo { Commit = commit, Slug = commit.ShortSha };
    var changed = git.ChangedFiles(commit.Sha, BaselinesRepoPath);

    foreach (var repoPath in changed)
    {
        if (!repoPath.EndsWith(".apng", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var rel = repoPath[(BaselinesRepoPath.Length + 1)..];
        var after = git.FileAt(commit.Sha, repoPath);

        if (after is null)
        {
            info.Removed.Add(Path.GetFileName(rel[..^".apng".Length]));
            continue;
        }

        var before = git.FileAt($"{commit.Sha}^", repoPath);

        var change = new ChangeInfo
        {
            BaselineRelative = rel,
            Slug = Slug(rel[..^".apng".Length], slugs, register: false),
            Title = Path.GetFileName(rel[..^".apng".Length]),
            AfterApngAsset = assets.Store(after, "r", ".apng"),
            IsNew = before is null,
        };

        if (before is not null)
        {
            change.BeforeApngAsset = assets.Store(before, "r", ".apng");

            try
            {
                var (diff, total, changedFrames, bf, af) = Assets.Compare(before, after);
                change.DiffPixels = total;
                change.ChangedFrames = changedFrames;
                change.BeforeFrames = bf;
                change.AfterFrames = af;

                if (diff is not null)
                {
                    change.DiffAsset = assets.Store(diff, "d", ".png");
                }
                else if (total == 0 && bf != af)
                {
                    change.Note = "frame count changed; overlapping frames identical";
                }
            }
            catch (Exception ex)
            {
                change.Note = $"diff unavailable: {ex.Message}";
            }
        }

        info.Changes.Add(change);
    }

    info.Changes.Sort((x, y) => y.DiffPixels.CompareTo(x.DiffPixels));
    commits.Add(info);
    Console.WriteLine($"commit : {commit.ShortSha} {info.Changes.Count} changed");
}

// ---------------------------------------------------------------- emit

Pages.WriteStyle(outDir);
Pages.WritePlayer(outDir);
Home.Write(outDir, baseUrl, repo, renders, builtSha, builtAt);
Pages.WriteGallery(outDir, baseUrl, renders, commits, builtSha, builtAt);
Pages.WriteRenderPages(outDir, baseUrl, renders, builtSha, builtAt);
Pages.WriteChangeIndex(outDir, baseUrl, commits, builtSha, builtAt);
Pages.WriteCommitPages(outDir, baseUrl, commits, builtSha, builtAt);
Pages.WriteSitemap(outDir, baseUrl, renders, commits);
Pages.WriteRobots(outDir, baseUrl);
Pages.WriteFeed(outDir, baseUrl, commits);

var siteBytes = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
Console.WriteLine($"assets : {assets.Count} files, {Html.Bytes(assets.BytesWritten)}");
Console.WriteLine($"site   : {Html.Bytes(siteBytes)} total");

return 0;

static string ArgOr(string[] args, string name, string fallback)
{
    var i = Array.IndexOf(args, name);

    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static string Slug(string relative, HashSet<string> taken, bool register = true)
{
    var s = new StringBuilder();

    foreach (var c in relative.ToLowerInvariant())
    {
        s.Append(char.IsLetterOrDigit(c) ? c : '-');
    }

    var basis = s.ToString().Trim('-');

    while (basis.Contains("--"))
    {
        basis = basis.Replace("--", "-");
    }

    if (!register)
    {
        return basis;
    }

    var candidate = basis;
    int n = 2;

    while (!taken.Add(candidate))
    {
        candidate = $"{basis}-{n++}";
    }

    return candidate;
}
