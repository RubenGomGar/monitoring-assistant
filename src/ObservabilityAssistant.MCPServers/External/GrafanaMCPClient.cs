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
        var args = new[]
        {
            "run",
            "--rm",
            "-i",
            "-e",
            "GRAFANA_URL",
            "-e",
            "GRAFANA_SERVICE_ACCOUNT_TOKEN",
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
