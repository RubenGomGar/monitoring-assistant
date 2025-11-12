using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;
namespace ObservabilityAssistant.MCPServers.Kubernetes;
public class KubernetesMCPServer : MCPServer
{
    private readonly IKubernetes _kubernetes;
    private readonly string _defaultNamespace;
    public KubernetesMCPServer(
        IKubernetes kubernetes,
        string defaultNamespace,
        ILogger<KubernetesMCPServer> logger) 
        : base(logger)
    {
        _kubernetes = kubernetes;
        _defaultNamespace = defaultNamespace;
    }
    public override async Task InitializeAsync()
    {
        Logger.LogInformation("Initializing Kubernetes MCP Server with default namespace: {Namespace}", 
            _defaultNamespace);
        try
        {
            var version = await _kubernetes.Version.GetCodeAsync();
            Logger.LogInformation("Connected to Kubernetes cluster version: {Version}", version.GitVersion);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to Kubernetes cluster");
            throw;
        }
        RegisterTools();
        Logger.LogInformation("Registered {ToolCount} Kubernetes tools", Tools.Count);
    }
    private void RegisterTools()
    {
        RegisterTool(
            "list_pods",
            "Lists all pods in a given namespace. Returns pod names, status, and basic information.",
            async (args) =>
            {
                var ns = args.GetValueOrDefault("namespace")?.ToString() ?? _defaultNamespace;
                try
                {
                    var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(ns);
                    var podList = pods.Items.Select(p => new
                    {
                        Name = p.Metadata.Name,
                        Namespace = p.Metadata.NamespaceProperty,
                        Status = p.Status.Phase,
                        Ready = $"{p.Status.ContainerStatuses?.Count(c => c.Ready) ?? 0}/{p.Status.ContainerStatuses?.Count ?? 0}",
                        Restarts = p.Status.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0,
                        Age = (DateTime.UtcNow - p.Metadata.CreationTimestamp)?.ToString(@"d\.hh\:mm\:ss") ?? "Unknown",
                        Node = p.Spec.NodeName
                    }).ToList();
                    return MCPResponse.Ok(podList, new Dictionary<string, object>
                    {
                        ["total_pods"] = podList.Count,
                        ["namespace"] = ns
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to list pods in namespace: {Namespace}", ns);
                    return MCPResponse.Fail($"Failed to list pods: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "namespace", Type = "string", Required = false, Description = "Namespace to list pods from. Defaults to 'default'" }
            }
        );
        RegisterTool(
            "get_pod_logs",
            "Gets logs from a specific pod. Optionally filter by container and number of lines.",
            async (args) =>
            {
                var podName = args.GetValueOrDefault("pod_name")?.ToString();
                var ns = args.GetValueOrDefault("namespace")?.ToString() ?? _defaultNamespace;
                var container = args.GetValueOrDefault("container")?.ToString();
                var tailLines = 100;
                if (args.GetValueOrDefault("tail_lines") != null)
                {
                    var value = args["tail_lines"];
                    if (value is System.Text.Json.JsonElement jsonElement)
                        tailLines = jsonElement.GetInt32();
                    else
                        tailLines = Convert.ToInt32(value);
                }
                if (string.IsNullOrEmpty(podName))
                {
                    return MCPResponse.Fail("pod_name parameter is required");
                }
                try
                {
                    var logsStream = await _kubernetes.CoreV1.ReadNamespacedPodLogAsync(
                        podName,
                        ns,
                        container: container,
                        tailLines: tailLines);
                    string logs;
                    using (var reader = new StreamReader(logsStream))
                    {
                        logs = await reader.ReadToEndAsync();
                    }
                    return MCPResponse.Ok(new
                    {
                        pod = podName,
                        @namespace = ns,
                        container = container ?? "default",
                        lines = tailLines,
                        logs
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get logs for pod: {PodName}", podName);
                    return MCPResponse.Fail($"Failed to get logs: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "pod_name", Type = "string", Required = true, Description = "Name of the pod" },
                new() { Name = "namespace", Type = "string", Required = false, Description = "Namespace of the pod. Defaults to 'default'" },
                new() { Name = "container", Type = "string", Required = false, Description = "Specific container name (if pod has multiple containers)" },
                new() { Name = "tail_lines", Type = "number", Required = false, Description = "Number of lines to retrieve from the end of logs. Defaults to 100" }
            }
        );
        RegisterTool(
            "describe_pod",
            "Gets detailed information about a specific pod including status, conditions, events, and resource usage.",
            async (args) =>
            {
                var podName = args.GetValueOrDefault("pod_name")?.ToString();
                var ns = args.GetValueOrDefault("namespace")?.ToString() ?? _defaultNamespace;
                if (string.IsNullOrEmpty(podName))
                {
                    return MCPResponse.Fail("pod_name parameter is required");
                }
                try
                {
                    var pod = await _kubernetes.CoreV1.ReadNamespacedPodAsync(podName, ns);
                    var events = await _kubernetes.CoreV1.ListNamespacedEventAsync(
                        ns,
                        fieldSelector: $"involvedObject.name={podName}");
                    var podInfo = new
                    {
                        name = pod.Metadata.Name,
                        @namespace = pod.Metadata.NamespaceProperty,
                        status = pod.Status.Phase,
                        node = pod.Spec.NodeName,
                        start_time = pod.Status.StartTime,
                        labels = pod.Metadata.Labels,
                        annotations = pod.Metadata.Annotations,
                        containers = pod.Spec.Containers.Select(c => new
                        {
                            name = c.Name,
                            image = c.Image,
                            ports = c.Ports?.Select(p => $"{p.ContainerPort}/{p.Protocol}").ToList(),
                            resources = new
                            {
                                requests = c.Resources?.Requests?.ToDictionary(x => x.Key, x => x.Value.ToString()),
                                limits = c.Resources?.Limits?.ToDictionary(x => x.Key, x => x.Value.ToString())
                            }
                        }).ToList(),
                        container_statuses = pod.Status.ContainerStatuses?.Select(cs => new
                        {
                            name = cs.Name,
                            ready = cs.Ready,
                            restart_count = cs.RestartCount,
                            state = cs.State?.Running != null ? "Running" :
                                    cs.State?.Waiting != null ? $"Waiting: {cs.State.Waiting.Reason}" :
                                    cs.State?.Terminated != null ? $"Terminated: {cs.State.Terminated.Reason}" : "Unknown"
                        }).ToList(),
                        conditions = pod.Status.Conditions?.Select(c => new
                        {
                            type = c.Type,
                            status = c.Status,
                            reason = c.Reason,
                            message = c.Message
                        }).ToList(),
                        recent_events = events.Items
                            .OrderByDescending(e => e.LastTimestamp)
                            .Take(10)
                            .Select(e => new
                            {
                                type = e.Type,
                                reason = e.Reason,
                                message = e.Message,
                                timestamp = e.LastTimestamp
                            }).ToList()
                    };
                    return MCPResponse.Ok(podInfo);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to describe pod: {PodName}", podName);
                    return MCPResponse.Fail($"Failed to describe pod: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "pod_name", Type = "string", Required = true, Description = "Name of the pod" },
                new() { Name = "namespace", Type = "string", Required = false, Description = "Namespace of the pod. Defaults to 'default'" }
            }
        );
        RegisterTool(
            "scale_deployment",
            "Scales a deployment to the specified number of replicas.",
            async (args) =>
            {
                var deploymentName = args.GetValueOrDefault("deployment_name")?.ToString();
                var ns = args.GetValueOrDefault("namespace")?.ToString() ?? _defaultNamespace;
                var replicas = 1;
                if (args.GetValueOrDefault("replicas") != null)
                {
                    var value = args["replicas"];
                    if (value is System.Text.Json.JsonElement jsonElement)
                        replicas = jsonElement.GetInt32();
                    else
                        replicas = Convert.ToInt32(value);
                }
                if (string.IsNullOrEmpty(deploymentName))
                {
                    return MCPResponse.Fail("deployment_name parameter is required");
                }
                try
                {
                    var deployment = await _kubernetes.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, ns);
                    var patch = new V1Deployment
                    {
                        Spec = new V1DeploymentSpec
                        {
                            Replicas = replicas
                        }
                    };
                    var patchedDeployment = await _kubernetes.AppsV1.PatchNamespacedDeploymentAsync(
                        new V1Patch(patch, V1Patch.PatchType.MergePatch),
                        deploymentName,
                        ns);
                    return MCPResponse.Ok(new
                    {
                        deployment = deploymentName,
                        @namespace = ns,
                        previous_replicas = deployment.Spec.Replicas,
                        new_replicas = replicas,
                        status = "Scaled successfully"
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to scale deployment: {DeploymentName}", deploymentName);
                    return MCPResponse.Fail($"Failed to scale deployment: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "deployment_name", Type = "string", Required = true, Description = "Name of the deployment to scale" },
                new() { Name = "namespace", Type = "string", Required = false, Description = "Namespace of the deployment. Defaults to 'default'" },
                new() { Name = "replicas", Type = "number", Required = true, Description = "Number of replicas to scale to" }
            }
        );
        RegisterTool(
            "get_namespaces",
            "Lists all namespaces in the Kubernetes cluster.",
            async (args) =>
            {
                try
                {
                    var namespaces = await _kubernetes.CoreV1.ListNamespaceAsync();
                    var nsList = namespaces.Items.Select(ns => new
                    {
                        name = ns.Metadata.Name,
                        status = ns.Status.Phase,
                        created = ns.Metadata.CreationTimestamp,
                        labels = ns.Metadata.Labels
                    }).ToList();
                    return MCPResponse.Ok(nsList, new Dictionary<string, object>
                    {
                        ["total_namespaces"] = nsList.Count
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to list namespaces");
                    return MCPResponse.Fail($"Failed to list namespaces: {ex.Message}");
                }
            }
        );
    }
}
