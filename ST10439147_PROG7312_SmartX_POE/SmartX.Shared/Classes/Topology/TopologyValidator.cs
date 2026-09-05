using SmartX.Shared.Topology;

namespace SmartX.Shared.Classes.Topology;

/// <summary>
/// Recursive validator for the deployment tree. Answers the question the brief poses:
/// is <c>node-3</c> safely configured within Sub-Zone B, inside Zone 1, inside Facility A?
///
/// <para>Recursion is the honest shape here - the tree is arbitrarily deep and each tier
/// is validated identically against its parent. The guards are what stop it becoming a
/// crash:</para>
/// <list type="bullet">
///   <item><b>Base case</b>: a node with no children returns after its own checks.</item>
///   <item><b>Depth ceiling</b>: <see cref="MaxDepth"/> tiers, after which the walk reports
///   and unwinds instead of exhausting the stack. A malformed or hostile payload with
///   50 000 nesting levels produces an <c>Error</c> issue, not a StackOverflowException
///   (which .NET cannot catch - the process just dies).</item>
///   <item><b>Cycle guard</b>: a node already on the current path is reported and not
///   re-entered, so a tree that has been wired into a graph terminates.</item>
/// </list>
/// </summary>
public static class TopologyValidator
{
    /// <summary>
    /// Deepest legal nesting. Four tiers are meaningful (Facility/Zone/Sub-Zone/Node);
    /// 64 leaves generous headroom for nested sub-zones while keeping stack use trivial.
    /// </summary>
    public const int MaxDepth = 64;

    public static TopologyReport Validate(DeploymentNode? root)
    {
        var issues = new List<TopologyIssue>();

        if (root is null)
        {
            issues.Add(new TopologyIssue("(root)", "No deployment tree was supplied.", IssueSeverity.Error));
            return new TopologyReport(false, 0, 0, issues);
        }

        var state = new WalkState();
        Walk(root, parent: null, path: string.Empty, depth: 0, state, issues);

        var valid = !issues.Any(i => i.Severity == IssueSeverity.Error);
        return new TopologyReport(valid, state.Visited, state.MaxDepth, issues);
    }

    /// <summary>Recursive step. One node, its own rules, then each child one tier deeper.</summary>
    private static void Walk(
        DeploymentNode node,
        DeploymentNode? parent,
        string path,
        int depth,
        WalkState state,
        List<TopologyIssue> issues)
    {
        var here = string.IsNullOrEmpty(path) ? node.Name : $"{path}/{node.Name}";

        // --- guard: depth ceiling. Checked before any recursion, so the stack never
        // grows past MaxDepth frames no matter what the payload claims.
        if (depth >= MaxDepth)
        {
            issues.Add(new TopologyIssue(
                here,
                $"Nesting exceeds the {MaxDepth}-tier ceiling; the subtree below this point was not validated.",
                IssueSeverity.Error));
            return;
        }

        // --- guard: cycle. onPath holds the ancestors of the current node only, so a
        // node legitimately reachable twice from different branches is fine; a node
        // that is its own ancestor is not.
        if (!state.OnPath.Add(node))
        {
            issues.Add(new TopologyIssue(
                here,
                "Cycle detected: this node appears inside its own subtree.",
                IssueSeverity.Error));
            return;
        }

        state.Visited++;
        if (depth + 1 > state.MaxDepth) state.MaxDepth = depth + 1;

        ValidateNode(node, parent, here, issues, state);

        // --- recursive descent
        foreach (var child in node.Children)
            Walk(child, node, here, depth + 1, state, issues);

        // --- base case: a leaf simply has nothing to iterate, and we unwind here.
        state.OnPath.Remove(node);
    }

    private static void ValidateNode(
        DeploymentNode node,
        DeploymentNode? parent,
        string here,
        List<TopologyIssue> issues,
        WalkState state)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
            issues.Add(new TopologyIssue(here, "Node has no name; it cannot be addressed.", IssueSeverity.Error));

        // Tiers must descend strictly by one. Facility -> Node with no Zone between
        // them is the classic mis-provisioning this whole walk exists to catch.
        if (parent is null)
        {
            if (node.Kind != NodeKind.Facility)
                issues.Add(new TopologyIssue(here, $"Root must be a Facility but is a {node.Kind}.", IssueSeverity.Error));
        }
        else if (node.Kind != parent.Kind + 1)
        {
            issues.Add(new TopologyIssue(
                here,
                $"A {node.Kind} cannot sit directly inside a {parent.Kind}; expected a {parent.Kind + 1}.",
                IssueSeverity.Error));
        }

        // Sensors belong on leaves only.
        if (node.Kind == NodeKind.Node)
        {
            if (string.IsNullOrWhiteSpace(node.SensorId))
                issues.Add(new TopologyIssue(here, "Leaf node carries no sensor id.", IssueSeverity.Warning));
            else if (!state.SensorIds.Add(node.SensorId))
                issues.Add(new TopologyIssue(here, $"Sensor '{node.SensorId}' is already mounted elsewhere in this tree.", IssueSeverity.Error));

            if (node.Children.Count > 0)
                issues.Add(new TopologyIssue(here, "A Node is a leaf and cannot contain children.", IssueSeverity.Error));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(node.SensorId))
                issues.Add(new TopologyIssue(here, $"Only a Node may carry a sensor, but this {node.Kind} does.", IssueSeverity.Error));

            if (node.Children.Count == 0)
                issues.Add(new TopologyIssue(here, $"This {node.Kind} is empty and monitors nothing.", IssueSeverity.Warning));
        }

        // Sibling names must be unique or the breadcrumb path stops being a unique address.
        var duplicates = node.Children
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicate in duplicates)
            issues.Add(new TopologyIssue(here, $"Contains {node.Children.Count(c => string.Equals(c.Name, duplicate, StringComparison.OrdinalIgnoreCase))} children named '{duplicate}'.", IssueSeverity.Error));
    }

    /// <summary>
    /// Checks that a slash-delimited path such as
    /// <c>Facility A/Zone 1/Sub-Zone B/node-3</c> actually resolves in the tree.
    /// Recursive, one path segment per frame.
    /// </summary>
    public static bool PathExists(DeploymentNode root, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return false;
        if (!string.Equals(root.Name, segments[0], StringComparison.OrdinalIgnoreCase)) return false;
        return Descend(root, segments, 1);
    }

    private static bool Descend(DeploymentNode node, string[] segments, int index)
    {
        // Base case: every segment consumed.
        if (index >= segments.Length) return true;
        if (index >= MaxDepth) return false;

        foreach (var child in node.Children)
        {
            if (string.Equals(child.Name, segments[index], StringComparison.OrdinalIgnoreCase)
                && Descend(child, segments, index + 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Mutable bookkeeping carried down the recursion.</summary>
    private sealed class WalkState
    {
        public int Visited;
        public int MaxDepth;
        // DeploymentNode does not override Equals, so the default comparer is
        // reference equality - which is exactly what a cycle check needs.
        public readonly HashSet<DeploymentNode> OnPath = [];
        public readonly HashSet<string> SensorIds = new(StringComparer.OrdinalIgnoreCase);
    }
}
