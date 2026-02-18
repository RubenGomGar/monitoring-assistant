using Microsoft.Extensions.Logging;
namespace ObservabilityAssistant.MCPServers.External;
public class GrafanaMCPClient : ExternalMCPClient
{
    public GrafanaMCPClient(
        string command,
        string[] args,
        ILogger<GrafanaMCPClient> logger) 
        : base(command, args, logger)
    {
    }
    public static GrafanaMCPClient CreateWithDocker(
        string grafanaUrl,
        string grafanaApiKey,
        ILogger<GrafanaMCPClient> logger)
    {
        // Convert localhost to host.docker.internal for Docker networking
        var dockerGrafanaUrl = grafanaUrl.Replace("localhost", "host.docker.internal");
        
        var args = new[]
        {
            "run",
            "--rm",
            "-i",
            "--add-host=host.docker.internal:host-gateway",
            "-e",
            $"GRAFANA_URL={dockerGrafanaUrl}",
            "-e",
            $"GRAFANA_SERVICE_ACCOUNT_TOKEN={grafanaApiKey}",
            "mcp/grafana",
            "-t",
            "stdio"
        };
        return new GrafanaMCPClient("docker", args, logger);
    }
    public static GrafanaMCPClient CreateWithBinary(
        string binaryPath,
        string grafanaUrl,
        string grafanaApiKey,
        ILogger<GrafanaMCPClient> logger)
    {
        var args = Array.Empty<string>();
        return new GrafanaMCPClient(binaryPath, args, logger);
    }
}
