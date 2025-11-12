namespace ObservabilityAssistant.Core.Configuration;
public class AzureOpenAIConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new InvalidOperationException("Azure OpenAI Endpoint is not configured");
        if (string.IsNullOrWhiteSpace(DeploymentName))
            throw new InvalidOperationException("Azure OpenAI DeploymentName is not configured");      
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Azure OpenAI ApiKey is not configured");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Azure OpenAI Endpoint is not a valid URI");
    }
}
public class KubernetesConfiguration
{
    public string ConfigPath { get; set; } = "~/.kube/config";
    public string DefaultNamespace { get; set; } = "default";
}
public class ObservabilityConfiguration
{
    public string PrometheusUrl { get; set; } = "http://localhost:9090";
    public string LokiUrl { get; set; } = "http://localhost:3100";
    public string TempoUrl { get; set; } = "http://localhost:3200";
}
public class GrafanaMCPConfiguration
{
    public bool Enabled { get; set; } = true;
    public string NodePath { get; set; } = "npx";
    public string GrafanaUrl { get; set; } = "http://localhost:3000";
    public string GrafanaApiKey { get; set; } = string.Empty;
    public bool UseCustomMCPs { get; set; } = false;
    public void Validate()
    {
        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(GrafanaUrl))
            throw new InvalidOperationException("Grafana URL is not configured");
        if (string.IsNullOrWhiteSpace(GrafanaApiKey))
            throw new InvalidOperationException("Grafana API Key is not configured");
        if (!Uri.TryCreate(GrafanaUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Grafana URL is not a valid URI");
    }
}
public class ObservabilityAssistantConfiguration
{
    public AzureOpenAIConfiguration AzureOpenAI { get; set; } = new();
    public KubernetesConfiguration Kubernetes { get; set; } = new();
    public ObservabilityConfiguration Observability { get; set; } = new();
    public GrafanaMCPConfiguration GrafanaMCP { get; set; } = new();
}
