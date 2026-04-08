using System.Diagnostics;

namespace pg_deploy.ScriptGeneration;

/// <summary>
/// Detects git info (remotes, current branch) for a given directory.
/// </summary>
public static class GitInfo
{
    public sealed class GitDetails
    {
        public string? Branch { get; init; }
        public List<string> Remotes { get; init; } = [];
        public bool IsGitRepo { get; init; }
    }

    public static GitDetails Detect(string folderPath)
    {
        if (!IsGitRepository(folderPath))
            return new GitDetails { IsGitRepo = false };

        var branch = RunGit(folderPath, "rev-parse --abbrev-ref HEAD")?.Trim();
        var remotesRaw = RunGit(folderPath, "remote -v")?.Trim();

        var remotes = new List<string>();
        if (!string.IsNullOrEmpty(remotesRaw))
        {
            foreach (var line in remotesRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = line.Trim();
                if (!remotes.Contains(normalized))
                    remotes.Add(normalized);
            }
        }

        return new GitDetails
        {
            IsGitRepo = true,
            Branch = branch,
            Remotes = remotes
        };
    }

    private static bool IsGitRepository(string path)
    {
        var result = RunGit(path, "rev-parse --is-inside-work-tree");
        return result?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? RunGit(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
