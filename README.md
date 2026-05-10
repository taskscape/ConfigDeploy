# ConfigDeploy

ConfigDeploy is a .NET Worker Service that can run as a Windows Service. It periodically fetches configured Git repository branches and completely replaces configured local directories with the files from those branches.

## Behavior

- Supports multiple repository, branch, and destination mappings.
- Uses the installed `git` command line client.
- Works with existing Git Credential Manager credentials.
- Can also pass configured username/password or personal access token credentials through Git's askpass flow.
- Replaces the entire destination directory, so files deleted from the repository are also removed locally.
- Keeps a service-managed clone cache outside the destination directories.

## Configuration

Edit `appsettings.json` after publishing, or provide equivalent environment variables.

```json
{
  "ConfigDeploy": {
    "CacheDirectory": "C:\\ProgramData\\ConfigDeploy\\cache",
    "PollIntervalSeconds": 60,
    "GitTimeoutSeconds": 300,
    "Repositories": [
      {
        "Name": "Production config",
        "RepositoryUrl": "https://github.com/contoso/configuration.git",
        "Branch": "main",
        "DestinationPath": "C:\\Deployments\\production-config",
        "RedeployWhenCommitUnchanged": true,
        "Enabled": true
      },
      {
        "Name": "Release config",
        "RepositoryUrl": "https://github.com/contoso/private-configuration.git",
        "Branch": "release",
        "DestinationPath": "C:\\Deployments\\release-config",
        "RedeployWhenCommitUnchanged": true,
        "Enabled": true,
        "Credentials": {
          "Username": "git",
          "PersonalAccessTokenEnvironmentVariable": "CONFIGDEPLOY_RELEASE_PAT"
        }
      }
    ]
  }
}
```

Set `RedeployWhenCommitUnchanged` to `true` when the local destination must be repaired even if no new commit exists. Set it to `false` when the service should only replace the destination after the branch commit changes.

For credentials, prefer environment variable references:

- `UsernameEnvironmentVariable`
- `PasswordEnvironmentVariable`
- `PersonalAccessTokenEnvironmentVariable`

Literal `Username`, `Password`, and `PersonalAccessToken` values are supported, but storing secrets in `appsettings.json` is usually not appropriate for production.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\ConfigDeploy
```

## Install as a Windows Service

Run PowerShell as Administrator:

```powershell
New-Service `
  -Name "ConfigDeploy" `
  -DisplayName "ConfigDeploy" `
  -BinaryPathName "C:\Services\ConfigDeploy\ConfigDeploy.exe" `
  -StartupType Automatic

Start-Service ConfigDeploy
```

To remove the service:

```powershell
Stop-Service ConfigDeploy
sc.exe delete ConfigDeploy
```

## Permissions

The service account must have:

- Read access to each configured Git repository.
- Modify access to every `DestinationPath`.
- Modify access to `CacheDirectory`.
- Network access to the Git server.

Do not point `DestinationPath` at a drive root or at a directory that overlaps the ConfigDeploy cache. The service rejects those unsafe paths.
