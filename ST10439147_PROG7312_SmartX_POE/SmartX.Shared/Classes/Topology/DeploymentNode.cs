namespace SmartX.Shared.Topology;

/// <summary>Tier of the deployment hierarchy: Facility &gt; Zone &gt; Sub-Zone &gt; Node.</summary>
public enum NodeKind
{
    Facility = 0,
    Zone = 1,
    SubZone = 2,
    Node = 3
}

/// <summary>One vertex of the multi-tier deployment tree.</summary>
public sealed class DeploymentNode
{
    public string Name { get; set; } = string.Empty;

    public NodeKind Kind { get; set; } = NodeKind.Facility;

    /// <summary>Only a leaf <see cref="NodeKind.Node"/> may carry a sensor.</summary>
    public string? SensorId { get; set; }

    public List<DeploymentNode> Children { get; set; } = [];

    public static DeploymentNode Create(string name, NodeKind kind, params DeploymentNode[] children)
        => new() { Name = name, Kind = kind, Children = [.. children] };
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A single validation finding, addressed by its breadcrumb path.</summary>
public sealed record TopologyIssue(string Path, string Message, IssueSeverity Severity);

/// <summary>Outcome of a full tree walk.</summary>
public sealed record TopologyReport(
    bool IsValid,
    int NodesVisited,
    int MaxDepthReached,
    IReadOnlyList<TopologyIssue> Issues);
