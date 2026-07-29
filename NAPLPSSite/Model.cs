// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSSite;

/// <summary>One corpus render: the committed baseline plus what we know about its source file.</summary>
public sealed class RenderInfo
{
    /// <summary>Path relative to the baselines root, e.g. "Anthony Wetzel/AUDI5.nap.apng".</summary>
    public required string BaselineRelative { get; init; }

    /// <summary>Path relative to Examples/, e.g. "Anthony Wetzel/AUDI5.nap".</summary>
    public required string SourceRelative { get; init; }

    /// <summary>URL-safe slug used for the per-file page.</summary>
    public required string Slug { get; init; }

    public required string Title { get; init; }

    public required string Collection { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public uint FrameCount { get; init; }

    public long ApngBytes { get; init; }

    public long SourceBytes { get; init; }

    public string SystemType { get; init; } = "Unknown";

    public int CommandCount { get; init; }

    /// <summary>Content-addressed asset paths, relative to the site root.</summary>
    public required string ApngAsset { get; set; }

    public required string PosterAsset { get; set; }

    public required string ThumbAsset { get; set; }
}

/// <summary>A file that changed in one of the covered commits.</summary>
public sealed class ChangeInfo
{
    public required string BaselineRelative { get; init; }

    public required string Slug { get; init; }

    public required string Title { get; init; }

    /// <summary>Null when the baseline was added in this commit.</summary>
    public string? BeforeApngAsset { get; set; }

    public required string AfterApngAsset { get; set; }

    public string? DiffAsset { get; set; }

    public uint BeforeFrames { get; set; }

    public uint AfterFrames { get; set; }

    public long DiffPixels { get; set; }

    public int ChangedFrames { get; set; }

    public bool IsNew { get; set; }

    public string? Note { get; set; }
}

public sealed class CommitInfo
{
    public required Git.Commit Commit { get; init; }

    public required string Slug { get; init; }

    public List<ChangeInfo> Changes { get; } = [];

    /// <summary>Baselines removed by this commit. They have no "after" to show, but a commit that
    /// only drops a baseline would otherwise render as an empty page.</summary>
    public List<string> Removed { get; } = [];

    public long TotalDiffPixels => Changes.Sum(c => c.DiffPixels);
}
