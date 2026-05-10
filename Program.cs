using ConfigDeploy;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ConfigDeploy";
});

builder.Services.Configure<DeploymentOptions>(
    builder.Configuration.GetSection(DeploymentOptions.SectionName));

builder.Services.AddSingleton<GitClient>();
builder.Services.AddSingleton<RepositoryDeployer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
