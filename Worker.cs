using Microsoft.Extensions.Options;

namespace ConfigDeploy;

public sealed class Worker : BackgroundService
{
    private readonly IOptionsMonitor<DeploymentOptions> _options;
    private readonly RepositoryDeployer _repositoryDeployer;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IOptionsMonitor<DeploymentOptions> options,
        RepositoryDeployer repositoryDeployer,
        ILogger<Worker> logger)
    {
        _options = options;
        _repositoryDeployer = repositoryDeployer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConfigDeploy service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            var delay = TimeSpan.FromSeconds(Math.Max(5, options.PollIntervalSeconds));

            try
            {
                await DeployConfiguredRepositoriesAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Repository deployment cycle failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task DeployConfiguredRepositoriesAsync(
        DeploymentOptions options,
        CancellationToken cancellationToken)
    {
        var enabledRepositories = options.Repositories
            .Where(repository => repository.Enabled)
            .ToList();

        if (enabledRepositories.Count == 0)
        {
            _logger.LogWarning("No enabled repositories are configured under the ConfigDeploy section.");
            return;
        }

        foreach (var repository in enabledRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _repositoryDeployer.DeployAsync(repository, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Deployment failed for repository {RepositoryName} branch {Branch} to {DestinationPath}.",
                    string.IsNullOrWhiteSpace(repository.Name) ? repository.RepositoryUrl : repository.Name,
                    repository.Branch,
                    repository.DestinationPath);
            }
        }
    }
}
