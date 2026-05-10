using System.Security.Cryptography;
using System.Text;

namespace ConfigDeploy;

public sealed class RepositoryDeployer
{
    private const string LocalBranchName = "__configdeploy__";

    private readonly GitClient _gitClient;
    private readonly ILogger<RepositoryDeployer> _logger;

    public RepositoryDeployer(GitClient gitClient, ILogger<RepositoryDeployer> logger)
    {
        _gitClient = gitClient;
        _logger = logger;
    }

    public async Task DeployAsync(
        RepositoryDeployment repository,
        DeploymentOptions options,
        CancellationToken cancellationToken)
    {
        Validate(repository);

        var timeout = TimeSpan.FromSeconds(Math.Max(30, options.GitTimeoutSeconds));
        var cacheRoot = TrimEndingDirectorySeparator(options.CacheDirectory);
        var destinationPath = TrimEndingDirectorySeparator(repository.DestinationPath);

        ValidateDestinationPath(destinationPath, cacheRoot);

        var deploymentId = GetDeploymentId(repository);
        var repositoryCacheRoot = Path.Combine(cacheRoot, "repositories", deploymentId);
        var workTreePath = Path.Combine(repositoryCacheRoot, "worktree");
        var statePath = Path.Combine(cacheRoot, "state", $"{deploymentId}.commit");

        Directory.CreateDirectory(repositoryCacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        if (!Directory.Exists(Path.Combine(workTreePath, ".git")))
        {
            if (Directory.Exists(workTreePath))
            {
                DeleteDirectory(workTreePath);
            }

            _logger.LogInformation(
                "Cloning {RepositoryName} branch {Branch} into service cache.",
                GetDisplayName(repository),
                repository.Branch);

            await _gitClient.RunAsync(
                ["clone", "--no-checkout", repository.RepositoryUrl, workTreePath],
                workingDirectory: null,
                repository.Credentials,
                timeout,
                cancellationToken);
        }
        else
        {
            await _gitClient.RunAsync(
                ["remote", "set-url", "origin", repository.RepositoryUrl],
                workTreePath,
                repository.Credentials,
                timeout,
                cancellationToken);
        }

        await _gitClient.RunAsync(
            ["fetch", "--prune", "origin", $"+refs/heads/{repository.Branch}:refs/remotes/origin/{repository.Branch}"],
            workTreePath,
            repository.Credentials,
            timeout,
            cancellationToken);

        var remoteRef = $"refs/remotes/origin/{repository.Branch}";

        await _gitClient.RunAsync(
            ["checkout", "--force", "-B", LocalBranchName, remoteRef],
            workTreePath,
            repository.Credentials,
            timeout,
            cancellationToken);

        await _gitClient.RunAsync(
            ["reset", "--hard", remoteRef],
            workTreePath,
            repository.Credentials,
            timeout,
            cancellationToken);

        await _gitClient.RunAsync(
            ["clean", "-xffd"],
            workTreePath,
            repository.Credentials,
            timeout,
            cancellationToken);

        var commit = await _gitClient.RunAsync(
            ["rev-parse", "HEAD"],
            workTreePath,
            repository.Credentials,
            timeout,
            cancellationToken);

        if (!repository.RedeployWhenCommitUnchanged &&
            File.Exists(statePath) &&
            string.Equals(await File.ReadAllTextAsync(statePath, cancellationToken), commit, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Skipping {RepositoryName}; branch {Branch} is already deployed at commit {Commit}.",
                GetDisplayName(repository),
                repository.Branch,
                commit);
            return;
        }

        ReplaceDestination(workTreePath, destinationPath);
        await File.WriteAllTextAsync(statePath, commit, cancellationToken);

        _logger.LogInformation(
            "Deployed {RepositoryName} branch {Branch} commit {Commit} to {DestinationPath}.",
            GetDisplayName(repository),
            repository.Branch,
            commit,
            destinationPath);
    }

    private static void Validate(RepositoryDeployment repository)
    {
        if (string.IsNullOrWhiteSpace(repository.RepositoryUrl))
        {
            throw new InvalidOperationException("RepositoryUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(repository.Branch))
        {
            throw new InvalidOperationException($"Branch is required for repository '{GetDisplayName(repository)}'.");
        }

        if (string.IsNullOrWhiteSpace(repository.DestinationPath))
        {
            throw new InvalidOperationException($"DestinationPath is required for repository '{GetDisplayName(repository)}'.");
        }
    }

    private static void ValidateDestinationPath(string destinationPath, string cacheRoot)
    {
        var root = Path.GetPathRoot(destinationPath);
        var normalizedDestination = TrimEndingDirectorySeparator(destinationPath);
        var normalizedCacheRoot = TrimEndingDirectorySeparator(cacheRoot);

        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(normalizedDestination, TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to deploy to unsafe destination path '{destinationPath}'.");
        }

        if (PathsOverlap(normalizedDestination, normalizedCacheRoot))
        {
            throw new InvalidOperationException("DestinationPath must not overlap the ConfigDeploy cache directory.");
        }
    }

    private static bool PathsOverlap(string firstPath, string secondPath)
    {
        return IsSameOrChild(firstPath, secondPath) || IsSameOrChild(secondPath, firstPath);
    }

    private static bool IsSameOrChild(string path, string possibleParent)
    {
        return path.Equals(possibleParent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(possibleParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(possibleParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEndingDirectorySeparator(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void ReplaceDestination(string sourcePath, string destinationPath)
    {
        var destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"DestinationPath '{destinationPath}' has no parent directory.");

        Directory.CreateDirectory(destinationParent);

        var stagingPath = Path.Combine(
            destinationParent,
            $".configdeploy-staging-{Path.GetFileName(destinationPath)}-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(
            destinationParent,
            $".configdeploy-backup-{Path.GetFileName(destinationPath)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");

        try
        {
            CopyRepositoryFiles(sourcePath, stagingPath);

            if (Directory.Exists(destinationPath))
            {
                Directory.Move(destinationPath, backupPath);
            }
            else if (File.Exists(destinationPath))
            {
                File.Move(destinationPath, backupPath);
            }

            Directory.Move(stagingPath, destinationPath);

            if (Directory.Exists(backupPath))
            {
                DeleteDirectory(backupPath);
            }
            else if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            if (!Directory.Exists(destinationPath) && !File.Exists(destinationPath))
            {
                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, destinationPath);
                }
                else if (File.Exists(backupPath))
                {
                    File.Move(backupPath, destinationPath);
                }
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                DeleteDirectory(stagingPath);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static void CopyRepositoryFiles(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (IsGitDirectory(directory, sourcePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (IsUnderGitDirectory(file, sourcePath))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourcePath, file);
            var destinationFile = Path.Combine(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }

    private static bool IsGitDirectory(string directory, string repositoryRoot)
    {
        return string.Equals(Path.GetFileName(directory), ".git", StringComparison.OrdinalIgnoreCase) ||
            IsUnderGitDirectory(directory, repositoryRoot);
    }

    private static bool IsUnderGitDirectory(string path, string repositoryRoot)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, path);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDeploymentId(RepositoryDeployment repository)
    {
        var input = $"{repository.RepositoryUrl}|{repository.Branch}|{repository.DestinationPath}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string GetDisplayName(RepositoryDeployment repository)
    {
        return string.IsNullOrWhiteSpace(repository.Name)
            ? $"{repository.RepositoryUrl}#{repository.Branch}"
            : repository.Name;
    }
}
