// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Diagnostics;

namespace NAPLPSSite;

/// <summary>
/// Reads baseline history straight out of the repository. Old baseline versions do not need to be
/// stored anywhere: every one of them is already in git, so the site is rebuilt from history rather
/// than from an accumulating pile of published artefacts.
/// </summary>
public sealed class Git(string repoRoot)
{
    public sealed record Commit(string Sha, string ShortSha, string Subject, string Author, DateTimeOffset Date);

    private string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");

        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        return p.ExitCode == 0 ? stdout : string.Empty;
    }

    private byte[] RunBinary(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        using var ms = new MemoryStream();

        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();

        return p.ExitCode == 0 ? ms.ToArray() : [];
    }

    /// <summary>The most recent commits that touched <paramref name="path"/>, newest first.</summary>
    public List<Commit> CommitsTouching(string path, int count)
    {
        var raw = Run("log", $"-{count}", "--format=%H%x1f%h%x1f%s%x1f%an%x1f%aI", "--", path);
        var commits = new List<Commit>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\x1f');

            if (parts.Length < 5)
            {
                continue;
            }

            commits.Add(new Commit(parts[0], parts[1], parts[2], parts[3], DateTimeOffset.Parse(parts[4])));
        }

        return commits;
    }

    /// <summary>Repo-relative paths under <paramref name="path"/> changed by a commit.</summary>
    public List<string> ChangedFiles(string sha, string path)
    {
        // A merge commit has no single diff, so compare against its first parent - that is the
        // change the merge actually introduced to this branch.
        var raw = Run("diff-tree", "--no-commit-id", "--name-only", "-r", "-m", "--first-parent", sha, "--", path);

        return [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct()];
    }

    /// <summary>File content at a commit, or null when the file did not exist there.</summary>
    public byte[]? FileAt(string sha, string repoRelativePath)
    {
        var bytes = RunBinary("show", $"{sha}:{repoRelativePath}");

        return bytes.Length == 0 ? null : bytes;
    }

    public string CurrentSha() => Run("rev-parse", "HEAD").Trim();
}
