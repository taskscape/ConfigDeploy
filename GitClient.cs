using System.Diagnostics;
using System.Text;

namespace ConfigDeploy;

public sealed class GitClient
{
    private readonly ILogger<GitClient> _logger;

    public GitClient(ILogger<GitClient> logger)
    {
        _logger = logger;
    }

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        GitCredentialOptions? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var askPass = AskPassScope.Create(credentials);
        using var process = new Process();

        process.StartInfo.FileName = "git";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (askPass is not null)
        {
            process.StartInfo.Environment["GIT_ASKPASS"] = askPass.ScriptPath;
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["CONFIGDEPLOY_GIT_USERNAME"] = askPass.Username;
            process.StartInfo.Environment["CONFIGDEPLOY_GIT_PASSWORD"] = askPass.Password;
        }

        _logger.LogDebug("Running git {Arguments}", string.Join(' ', arguments.Select(MaskForLog)));

        var output = new StringBuilder();
        var errors = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                errors.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Git command timed out after {timeout.TotalSeconds:N0} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var command = string.Join(' ', arguments.Select(MaskForLog));
            throw new InvalidOperationException(
                $"Git command failed with exit code {process.ExitCode}: git {command}{Environment.NewLine}{errors}");
        }

        return output.ToString().Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }

    private static string MaskForLog(string argument)
    {
        if (argument.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(argument, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            var builder = new UriBuilder(uri)
            {
                UserName = "***",
                Password = "***"
            };

            return builder.Uri.ToString();
        }

        return argument;
    }

    private sealed class AskPassScope : IDisposable
    {
        private AskPassScope(string scriptPath, string username, string password)
        {
            ScriptPath = scriptPath;
            Username = username;
            Password = password;
        }

        public string ScriptPath { get; }

        public string Username { get; }

        public string Password { get; }

        public static AskPassScope? Create(GitCredentialOptions? credentials)
        {
            if (credentials is null)
            {
                return null;
            }

            var username = GetValue(credentials.Username, credentials.UsernameEnvironmentVariable);
            var password = GetValue(credentials.Password, credentials.PasswordEnvironmentVariable);
            var token = GetValue(credentials.PersonalAccessToken, credentials.PersonalAccessTokenEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(token))
            {
                password = token;
                username = string.IsNullOrWhiteSpace(username) ? "git" : username;
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), $"configdeploy-git-askpass-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(
                scriptPath,
                """
                @echo off
                powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$prompt = $args -join ' '; if ($prompt -match 'Username') { [Console]::Out.WriteLine($env:CONFIGDEPLOY_GIT_USERNAME) } else { [Console]::Out.WriteLine($env:CONFIGDEPLOY_GIT_PASSWORD) }" -- %*
                """);

            return new AskPassScope(scriptPath, username, password);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(ScriptPath);
            }
            catch
            {
                // Best effort cleanup of a temporary credentials helper.
            }
        }

        private static string? GetValue(string? literalValue, string? environmentVariable)
        {
            if (!string.IsNullOrWhiteSpace(environmentVariable))
            {
                var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
                if (!string.IsNullOrWhiteSpace(environmentValue))
                {
                    return environmentValue;
                }
            }

            return literalValue;
        }
    }
}
