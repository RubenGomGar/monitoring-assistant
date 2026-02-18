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
                        if (File.Exists(configPath))
                        {
                            return new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(configPath));
                        }

                        // Fallback to in-cluster config if running inside Kubernetes
                        var inClusterHost = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
                        if (!string.IsNullOrEmpty(inClusterHost))
                        {
                            return new k8s.Kubernetes(KubernetesClientConfiguration.InClusterConfig());
                        }

                        throw new FileNotFoundException($"Kubernetes config not found at: {configPath}");
                    });
                    services.AddSingleton<MCPServer, KubernetesMCPServer>(sp =>
                    {
                        var k8s = sp.GetRequiredService<IKubernetes>();
                        var config = sp.GetRequiredService<KubernetesConfiguration>();
                        var logger = sp.GetRequiredService<ILogger<KubernetesMCPServer>>();
                        return new KubernetesMCPServer(k8s, config.DefaultNamespace, logger);
                    });
                    var grafanaConfig = config.GrafanaMCP;
                    // Registrar MCP de Grafana (remoto o local)
                    if (grafanaConfig.Enabled)
                    {
                        if (grafanaConfig.UseRemoteMCP && !string.IsNullOrWhiteSpace(grafanaConfig.RemoteMCPUrl))
                        {
                            // Usar cliente HTTP para conectarse al MCP server en AKS
                            services.AddSingleton<MCPServer, HttpMCPClient>(sp =>
                            {
                                var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                                var logger = sp.GetRequiredService<ILogger<HttpMCPClient>>();
                                return new HttpMCPClient(
                                    cfg.GrafanaMCP.RemoteMCPUrl!,
                                    cfg.GrafanaMCP.RemoteMCPEndpoint,
                                    logger);
                            });
                        }
                        else
                        {
                            // Usar Docker local
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
                    }
                    
                    // Registrar cliente Tempo HTTP para consultas directas vía proxy de Grafana
                    services.AddSingleton<MCPServer, TempoHttpClient>(sp =>
                    {
                        var cfg = sp.GetRequiredService<ObservabilityAssistantConfiguration>();
                        var logger = sp.GetRequiredService<ILogger<TempoHttpClient>>();
                        // Usar Grafana como proxy para consultar Tempo (datasource UID: af4e0hjmotreob)
                        return new TempoHttpClient(
                            cfg.GrafanaMCP.GrafanaUrl, 
                            cfg.GrafanaMCP.GrafanaApiKey, 
                            "af4e0hjmotreob", // UID del datasource Tempo en Grafana
                            logger);
                    });

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
