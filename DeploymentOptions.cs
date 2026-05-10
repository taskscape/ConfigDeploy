namespace ConfigDeploy;

public sealed class DeploymentOptions
{
    public const string SectionName = "ConfigDeploy";

    public string CacheDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "cache");

    public int PollIntervalSeconds { get; set; } = 60;

    public int GitTimeoutSeconds { get; set; } = 300;

    public List<RepositoryDeployment> Repositories { get; set; } = [];
}

public sealed class RepositoryDeployment
{
    public string Name { get; set; } = string.Empty;

    public string RepositoryUrl { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    public string DestinationPath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool RedeployWhenCommitUnchanged { get; set; } = true;

    public GitCredentialOptions? Credentials { get; set; }
}

public sealed class GitCredentialOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? PersonalAccessToken { get; set; }

    public string? UsernameEnvironmentVariable { get; set; }

    public string? PasswordEnvironmentVariable { get; set; }

    public string? PersonalAccessTokenEnvironmentVariable { get; set; }
}
