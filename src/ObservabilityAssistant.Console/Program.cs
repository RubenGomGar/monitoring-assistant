using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.Console.UI;
using ObservabilityAssistant.Core.AI;
using ObservabilityAssistant.Core.Configuration;
using ObservabilityAssistant.MCPServers.Base;
using ObservabilityAssistant.MCPServers.Kubernetes;
using ObservabilityAssistant.MCPServers.Prometheus;
using ObservabilityAssistant.MCPServers.Loki;
using ObservabilityAssistant.MCPServers.Tempo;
using ObservabilityAssistant.MCPServers.External;
using Spectre.Console;
namespace ObservabilityAssistant.Console;
class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddUserSecrets<Program>()
                .AddEnvironmentVariables()
                .Build();
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    var config = configuration.Get<ObservabilityAssistantConfiguration>() 
                        ?? throw new InvalidOperationException("Failed to load configuration");
                    services.AddSingleton(config);
                    services.AddSingleton(config.AzureOpenAI);
                    services.AddSingleton(config.Kubernetes);
                    services.AddSingleton<IKubernetes>(sp =>
                    {
                        var k8sConfig = sp.GetRequiredService<KubernetesConfiguration>();
                        var configPath = k8sConfig.ConfigPath.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                        if (!File.Exists(configPath))
                        {
                            throw new FileNotFoundException($"Kubernetes config not found at: {configPath}");
                        }
                        return new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(configPath));
                    });
                    services.AddSingleton<MCPServer, KubernetesMCPServer>(sp =>
                    {
                        var k8s = sp.GetRequiredService<IKubernetes>();
                        var config = sp.GetRequiredService<KubernetesConfiguration>();
                        var logger = sp.GetRequiredService<ILogger<KubernetesMCPServer>>();
                        return new KubernetesMCPServer(k8s, config.DefaultNamespace, logger);
                    });
                    var grafanaConfig = config.GrafanaMCP;
                    if (grafanaConfig.UseCustomMCPs)
                    {
                        services.AddSingleton<MCPServer, PrometheusMCPServer>(sp =>
                        {
                            var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                            var logger = sp.GetRequiredService<ILogger<PrometheusMCPServer>>();
                            return new PrometheusMCPServer(cfg.Observability.PrometheusUrl, logger);
                        });
                        services.AddSingleton<MCPServer, LokiMCPServer>(sp =>
                        {
                            var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                            var logger = sp.GetRequiredService<ILogger<LokiMCPServer>>();
                            return new LokiMCPServer(cfg.Observability.LokiUrl, logger);
                        });
                        services.AddSingleton<MCPServer, TempoMCPServer>(sp =>
                        {
                            var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                            var logger = sp.GetRequiredService<ILogger<TempoMCPServer>>();
                            return new TempoMCPServer(cfg.Observability.TempoUrl, logger);
                        });
                    }
                    else if (grafanaConfig.Enabled)
                    {
                        services.AddSingleton<MCPServer, GrafanaMCPClient>(sp =>
                        {
                            var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                            var logger = sp.GetRequiredService<ILogger<GrafanaMCPClient>>();
                            Environment.SetEnvironmentVariable("GRAFANA_URL", cfg.GrafanaMCP.GrafanaUrl);
                            Environment.SetEnvironmentVariable("GRAFANA_SERVICE_ACCOUNT_TOKEN", cfg.GrafanaMCP.GrafanaApiKey);
                            return GrafanaMCPClient.CreateWithDocker(
                                cfg.GrafanaMCP.GrafanaUrl,
                                cfg.GrafanaMCP.GrafanaApiKey,
                                logger);
                        });
                    }
                    services.AddSingleton<OpenAIService>();
                    services.AddSingleton<AIOrchestrator>();
                    services.AddSingleton<ChatInterface>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Error);
                    logging.AddFilter("Microsoft", LogLevel.Error);
                    logging.AddFilter("System", LogLevel.Error);
                    logging.AddFilter("ObservabilityAssistant", LogLevel.Error);
                })
                .Build();
            var mcpServers = host.Services.GetServices<MCPServer>();
            foreach (var server in mcpServers)
            {
                await server.InitializeAsync();
            }
            var chatInterface = host.Services.GetRequiredService<ChatInterface>();
            await chatInterface.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
