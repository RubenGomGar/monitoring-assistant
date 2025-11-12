# Observability Assistant

An AI-powered assistant for Kubernetes and observability systems that provides natural language interaction with your infrastructure and monitoring tools.

## What It Does

Observability Assistant is a console application that combines Azure OpenAI with Model Context Protocol (MCP) servers to help you:

- **Query Kubernetes clusters**: List pods, deployments, services, and get resource status
- **Access logs**: Query logs from Loki
- **View metrics**: Retrieve time-series data from Prometheus
- **Analyze traces**: Access distributed tracing data from Tempo
- **Natural language interface**: Ask questions in plain English instead of writing kubectl or API queries

The assistant intelligently routes your requests to the appropriate tool based on what you're asking.

## Configuration

Edit the `appsettings.json` file in the Console project with your settings:

### Required Settings

**Azure OpenAI** (Required for AI functionality):
```json
"AzureOpenAI": {
  "Endpoint": "https://your-openai-endpoint.openai.azure.com/",
  "DeploymentName": "your-deployment-name",
  "ApiKey": "your-api-key"
}
```

**Kubernetes** (Required for cluster access):
```json
"Kubernetes": {
  "ConfigPath": "~/.kube/config",
  "DefaultNamespace": "default"
}
```

### Optional Settings

**Observability Tools** (Required if using custom MCPs):
```json
"Observability": {
  "PrometheusUrl": "http://your-prometheus:9090",
  "LokiUrl": "http://your-loki:3100",
  "TempoUrl": "http://your-tempo:3200"
}
```

**Grafana MCP** (For official Grafana MCP integration):
```json
"GrafanaMCP": {
  "Enabled": true,
  "NodePath": "docker",
  "GrafanaUrl": "http://your-grafana:3000",
  "GrafanaApiKey": "your-grafana-api-key",
  "UseCustomMCPs": false
}
```

- Set `UseCustomMCPs: true` to use built-in MCP servers (requires Observability URLs)
- Set `UseCustomMCPs: false` to use the official Grafana MCP via Docker (requires GrafanaMCP settings)

## Getting Started

1. Configure your `appsettings.json` with the required settings
2. Ensure your Kubernetes config file exists at the configured path
3. Run the application:
   ```bash
   dotnet run --project src/ObservabilityAssistant.Console
   ```

## Example Queries

- "List all pods in the default namespace"
- "Show me error logs from my application"
- "What deployments are running?"
- "Show CPU metrics for the last hour"

Type `exit` to quit, `clear` to clear the screen, `reset` to start a new conversation, or `help` for more commands.
