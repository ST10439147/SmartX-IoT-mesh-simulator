using SmartX.Shared.Telemetry;

namespace SmartX.Shared.Topology;

/// <summary>
/// One wireless link between two mesh nodes. <see cref="RssiDbm"/> is the received signal
/// strength - closer to zero is stronger. <see cref="Cost"/> is the weight routing works
/// with: the relative transmit energy needed to get one packet across this link.
/// </summary>
public sealed record MeshLink(string From, string To, int RssiDbm, double Cost);

/// <summary>
/// The physical IoT mesh as an undirected, weighted graph. Each vertex is an ESP32 node
/// (keyed by its canonical MAC) or the central gateway; each edge is a radio link the two
/// ends can hear each other over.
///
/// <para><b>Adjacency list, not a matrix.</b> A mesh node only hears a handful of
/// neighbours, so the graph is sparse: V nodes but only a few links each. A V x V matrix
/// would store mostly "no link" and cost O(V) to list one node's neighbours. The adjacency
/// list stores only real links - O(V + E) memory - and hands back a node's neighbours
/// directly, which is the one operation BFS, DFS and Prim's all repeat.</para>
///
/// <para><b>Undirected.</b> Radio links work both ways, so <see cref="Connect"/> files the
/// link under both ends. Each end's list holds the link pointing away from it, so
/// <c>link.To</c> is always "the neighbour".</para>
///
/// <para>Not thread-safe: the owning service locks around it.</para>
/// </summary>
// Reference [60]: Cormen, T. H. et al. (2022). Introduction to Algorithms, 4th ed., ch. 20 "Elementary Graph Algorithms". MIT Press.
// Reference [64]: Espressif. ESP-WIFI-MESH Programming Guide. https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/esp-wifi-mesh.html
public sealed class MeshGraph
{
    /// <summary>The vertex id of the central gateway every route ends at.</summary>
    public const string GatewayId = "GATEWAY";

    // RSSI of a reference link that costs exactly 1.0; see CostOf.
    private const int ReferenceRssi = -50;

    private readonly Dictionary<string, List<MeshLink>> _adjacency = new(StringComparer.OrdinalIgnoreCase);

    public int VertexCount => _adjacency.Count;

    /// <summary>Undirected links, each counted once.</summary>
    public int LinkCount { get; private set; }

    public IReadOnlyCollection<string> Vertices => _adjacency.Keys;

    public bool Contains(string id) => _adjacency.ContainsKey(id);

    /// <summary>Adds an isolated vertex. False if it was already there.</summary>
    public bool AddVertex(string id) => _adjacency.TryAdd(id, []);

    /// <summary>
    /// Links two nodes, adding either vertex if it is new. Linking an existing pair again
    /// updates the signal reading rather than adding a parallel edge. O(degree).
    /// </summary>
    public void Connect(string a, string b, int rssiDbm)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"A node cannot link to itself ({a}).");

        AddVertex(a);
        AddVertex(b);

        var cost = CostOf(rssiDbm);
        var added = Upsert(_adjacency[a], new MeshLink(a, b, rssiDbm, cost));
        Upsert(_adjacency[b], new MeshLink(b, a, rssiDbm, cost));
        if (added) LinkCount++;
    }

    // Replaces the link to the same neighbour if there is one. True if it was new.
    private static bool Upsert(List<MeshLink> links, MeshLink link)
    {
        var index = links.FindIndex(l => string.Equals(l.To, link.To, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            links[index] = link;
            return false;
        }

        links.Add(link);
        return true;
    }

    /// <summary>Removes a vertex and every link touching it, from both ends. O(degree of its neighbours).</summary>
    public bool RemoveVertex(string id)
    {
        if (!_adjacency.Remove(id, out var links)) return false;

        foreach (var link in links)
            _adjacency[link.To].RemoveAll(l => string.Equals(l.To, id, StringComparison.OrdinalIgnoreCase));

        LinkCount -= links.Count;
        return true;
    }

    /// <summary>The links leaving <paramref name="id"/>; <c>To</c> on each is the neighbour. Empty if unknown.</summary>
    public IReadOnlyList<MeshLink> Neighbours(string id)
        => _adjacency.TryGetValue(id, out var links) ? links : [];

    public int DegreeOf(string id) => Neighbours(id).Count;

    /// <summary>The link from <paramref name="a"/> to <paramref name="b"/>, or null if they cannot hear each other. O(degree).</summary>
    public MeshLink? LinkBetween(string a, string b)
        => Neighbours(a).FirstOrDefault(l => string.Equals(l.To, b, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every link once, not once per end.</summary>
    public IEnumerable<MeshLink> Links
        => _adjacency.Values.SelectMany(l => l).Where(l => string.Compare(l.From, l.To, StringComparison.OrdinalIgnoreCase) < 0);

    /// <summary>
    /// Breadth-first search from <paramref name="root"/>: every vertex it can reach, in the
    /// order BFS discovers them, with its hop count and the neighbour it was reached from.
    ///
    /// <para>Nodes in <paramref name="down"/> are treated as gone: never visited and never
    /// passed through. A dead node cannot relay a packet, so anything that could only be
    /// reached through it drops out of the result - that is how the mesh finds the nodes a
    /// failure has cut off.</para>
    ///
    /// <para><b>Why BFS.</b> It explores in rings: everything 1 hop away, then 2 hops, and so
    /// on. So the first time it reaches a node is along a path with the fewest hops - the
    /// hop count is exact, and the <c>Parent</c> chain is a shortest-hop route back to the
    /// root. Each vertex is queued once and each link looked at twice (once from each end):
    /// O(V + E).</para>
    /// </summary>
    // Reference [60]: Cormen, T. H. et al. (2022). Introduction to Algorithms, 4th ed., s. 20.2 "Breadth-first search". MIT Press.
    // Reference [52]: Microsoft. Queue<T> Class. https://learn.microsoft.com/dotnet/api/system.collections.generic.queue-1
    public BfsTree BreadthFirst(string root, IReadOnlySet<string>? down = null)
    {
        var order = new List<string>();
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Vertex ids ignore case, so the down set must too, whatever comparer the caller built it with.
        var blocked = new HashSet<string>(down ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);

        if (!_adjacency.ContainsKey(root) || blocked.Contains(root))
            return new BfsTree(root, order, hops, parent);

        // `hops` doubles as the visited set: a node gets its hop count the moment it is
        // queued, so it can never be queued twice.
        var queue = new Queue<string>();
        queue.Enqueue(root);
        hops[root] = 0;

        while (queue.TryDequeue(out var current))
        {
            order.Add(current);

            foreach (var link in _adjacency[current])
            {
                if (hops.ContainsKey(link.To) || blocked.Contains(link.To)) continue;

                hops[link.To] = hops[current] + 1;
                parent[link.To] = current;
                queue.Enqueue(link.To);
            }
        }

        return new BfsTree(root, order, hops, parent);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Prim's algorithm: the minimum spanning tree of everything reachable from
    /// <paramref name="root"/>, skipping nodes in <paramref name="down"/>. Of all the ways
    /// to connect those nodes with no loops, it is the set of links whose total
    /// <see cref="MeshLink.Cost"/> - total transmit energy - is lowest.
    ///
    /// <para><b>How it grows.</b> Start with just the root. Every link from the tree to a
    /// node outside it goes on a <see cref="MinHeap{TElement,TPriority}"/> keyed by cost. Pop
    /// the cheapest: if its far end is already in the tree it would close a loop, so drop it;
    /// otherwise take the link, add its far end, and push that node's links. Repeat until
    /// the heap is empty. Taking the cheapest crossing link is always safe (the "cut
    /// property"), so the greedy choice ends up globally optimal.</para>
    ///
    /// <para><b>Lazy deletion.</b> A node can be on the heap several times through different
    /// links. Rather than finding and updating the old entry (a heap cannot search), the
    /// stale ones are just skipped when they come out. The heap holds at most E entries, so
    /// the whole run is O(E log E).</para>
    ///
    /// <para>Each tree link is returned pointing away from the root (<c>From</c> is the
    /// parent), so the result is a routing tree rooted at the gateway.</para>
    /// </summary>
    // Reference [65]: Prim, R. C. (1957). Shortest connection networks and some generalizations. Bell System Technical Journal, 36(6), 1389-1401.
    // Reference [60]: Cormen, T. H. et al. (2022). Introduction to Algorithms, 4th ed., s. 21.2 "The algorithms of Kruskal and Prim". MIT Press.
    public MstTree MinimumSpanningTree(string root, IReadOnlySet<string>? down = null)
    {
        var blocked = new HashSet<string>(down ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var edges = new List<MeshLink>();
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_adjacency.ContainsKey(root) || blocked.Contains(root))
            return new MstTree(root, order, edges, parent, 0);

        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Priority is (cost, push order): equal-cost links come out in the order they were
        // found, so the same mesh always gives the same tree.
        var heap = new MinHeap<MeshLink, (double Cost, long Sequence)>();
        long sequence = 0;

        void Take(string id)
        {
            inTree.Add(id);
            order.Add(id);
            foreach (var link in _adjacency[id])
                if (!inTree.Contains(link.To) && !blocked.Contains(link.To))
                    heap.Push(link, (link.Cost, sequence++));
        }

        Take(root);

        while (heap.TryPop(out var cheapest, out _))
        {
            if (inTree.Contains(cheapest.To)) continue;   // stale: would close a loop

            edges.Add(cheapest);
            parent[cheapest.To] = cheapest.From;
            Take(cheapest.To);
        }

        return new MstTree(root, order, edges, parent, Math.Round(edges.Sum(e => e.Cost), 2));
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Dijkstra's algorithm: the lowest possible energy from <paramref name="root"/> to every
    /// node it can reach, skipping <paramref name="down"/>. Used as a yardstick for the MST
    /// routes - an MST minimises the energy of the whole network's links, which does not
    /// guarantee every single node its own cheapest path; this says how close each one gets.
    ///
    /// <para>Pops the closest unsettled node from the <see cref="MinHeap{TElement,TPriority}"/>,
    /// settles it at that cost, and pushes its neighbours at cost + link. With non-negative
    /// link costs the first time a node is popped is its cheapest, so later (stale) entries
    /// are skipped - the same lazy deletion as Prim's. O(E log E).</para>
    /// </summary>
    // Reference [66]: Dijkstra, E. W. (1959). A note on two problems in connexion with graphs. Numerische Mathematik, 1, 269-271.
    public IReadOnlyDictionary<string, double> CheapestCosts(string root, IReadOnlySet<string>? down = null)
    {
        var blocked = new HashSet<string>(down ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var settled = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_adjacency.ContainsKey(root) || blocked.Contains(root)) return settled;

        var heap = new MinHeap<string, double>();
        heap.Push(root, 0);

        while (heap.TryPop(out var node, out var cost))
        {
            if (!settled.TryAdd(node, cost)) continue;   // stale: already settled more cheaply

            foreach (var link in _adjacency[node])
                if (!settled.ContainsKey(link.To) && !blocked.Contains(link.To))
                    heap.Push(link.To, cost + link.Cost);
        }

        return settled;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Relative transmit energy for one packet over a link with this RSSI. dBm is a log
    /// scale - every 10 dB weaker means ten times less power arrives - so the radio has to
    /// put out ten times more to be heard reliably. A -50 dBm link costs 1.0, -60 costs 10,
    /// -70 costs 100. Summing these along a route gives its total energy, which is what the
    /// power-efficient routing in section 4 minimises.
    /// </summary>
    // Reference [67]: Rappaport, T. S. (2002). Wireless Communications: Principles and Practice, 2nd ed., ch. 4 "Path loss". Prentice Hall.
    public static double CostOf(int rssiDbm) => Math.Round(Math.Pow(10, (ReferenceRssi - rssiDbm) / 10.0), 2);
}
//----------------------------------------------------------------------------------------------------------------------//
/// <summary>
/// What a breadth-first search found. <see cref="Order"/> is the discovery order,
/// <see cref="Hops"/> the fewest hops from the root to each vertex, and <see cref="Parent"/>
/// the neighbour each vertex was first reached from (the root has none).
/// </summary>
public sealed record BfsTree(
    string Root,
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, int> Hops,
    IReadOnlyDictionary<string, string> Parent)
{
    public bool Reached(string id) => Hops.ContainsKey(id);

    /// <summary>
    /// The route from <paramref name="id"/> back to <see cref="Root"/>, both ends included:
    /// follow <see cref="Parent"/> one hop at a time until the root, which has none. Because
    /// BFS reached each node first by its fewest-hop path, this is a shortest-hop route.
    /// O(hops). Null if the search never reached <paramref name="id"/>.
    /// </summary>
    public IReadOnlyList<string>? PathFrom(string id) => Reached(id) ? ParentChain.Walk(Parent, id) : null;
}

/// <summary>Walking a rooted tree's parent pointers, shared by the BFS tree and the MST.</summary>
internal static class ParentChain
{
    /// <summary><paramref name="id"/>, its parent, its parent's parent... up to the root, which has no parent. O(depth).</summary>
    public static IReadOnlyList<string> Walk(IReadOnlyDictionary<string, string> parent, string id)
    {
        var path = new List<string> { id };
        while (parent.TryGetValue(path[^1], out var next)) path.Add(next);
        return path;
    }
}

/// <summary>
/// What Prim's algorithm built. <see cref="Order"/> is the order vertices joined the tree,
/// <see cref="Edges"/> the chosen links (each pointing from parent to child, in the order
/// they were taken), <see cref="Parent"/> each vertex's parent towards the root, and
/// <see cref="TotalCost"/> the energy of every chosen link added up.
/// </summary>
public sealed record MstTree(
    string Root,
    IReadOnlyList<string> Order,
    IReadOnlyList<MeshLink> Edges,
    IReadOnlyDictionary<string, string> Parent,
    double TotalCost)
{
    /// <summary>True if <paramref name="id"/> is in the tree: the root, or any vertex with a parent.</summary>
    public bool Spans(string id) => Parent.ContainsKey(id) || (Order.Count > 0 && string.Equals(id, Root, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The route from <paramref name="id"/> to the root along the tree's links: the
    /// power-efficient route the gateway uses. A tree has exactly one path between any two
    /// vertices, so there is nothing to search - just follow the parents. Null if not spanned.
    /// </summary>
    public IReadOnlyList<string>? PathFrom(string id) => Spans(id) ? ParentChain.Walk(Parent, id) : null;
}

/// <summary>
/// The minimum spanning tree as the API shows it, with what it is being compared against.
/// <see cref="BfsTreeCost"/> is the energy of the fewest-hop routing tree from section 3;
/// <see cref="MeshCost"/> is every link in the mesh. <see cref="Down"/> were left out of the
/// tree; <see cref="Unreached"/> are up but could not be connected to the root at all.
/// </summary>
public sealed record MstReport(
    string Root,
    IReadOnlyList<MeshVertex> Vertices,
    IReadOnlyList<MeshLink> Edges,
    double TotalCost,
    double BfsTreeCost,
    double MeshCost,
    int MeshLinks,
    IReadOnlyList<MeshVertex> Down,
    IReadOnlyList<MeshVertex> Unreached);

/// <summary>A node the search reached, and how many hops it sits from where the search began.</summary>
public sealed record ReachableNode(string Id, string Name, string DeploymentPath, int Hops);

/// <summary>
/// Which nodes can still talk to <see cref="From"/>. <see cref="Down"/> have gone silent;
/// <see cref="Stranded"/> are still running but every path they had went through a down
/// node, so nothing they send can get through. <see cref="Subnet"/>, when set, is the
/// deployment-path prefix the lists were narrowed to.
/// </summary>
public sealed record ReachabilityReport(
    string From,
    string? Subnet,
    IReadOnlyList<ReachableNode> Reachable,
    IReadOnlyList<MeshVertex> Down,
    IReadOnlyList<MeshVertex> Stranded,
    IReadOnlyList<string> VisitOrder);

/// <summary>
/// A route through the mesh, node first and gateway last. <c>Links[i]</c> is the radio hop
/// from <c>Hops[i]</c> to <c>Hops[i + 1]</c>; <see cref="TotalCost"/> adds up their energy.
/// </summary>
public sealed record MeshRoute(IReadOnlyList<MeshVertex> Hops, IReadOnlyList<MeshLink> Links, double TotalCost)
{
    public int HopCount => Links.Count;
}

/// <summary>Where a node's traffic stands right now.</summary>
public enum RouteStatus
{
    /// <summary>Getting through on its usual route.</summary>
    Online,
    /// <summary>A node on its usual route is down; traffic has healed onto a detour.</summary>
    Rerouted,
    /// <summary>The node itself is silent.</summary>
    Down,
    /// <summary>The node is running, but every path it had runs through a down node.</summary>
    Stranded
}

/// <summary>
/// One node's route to the gateway.
/// <list type="bullet">
/// <item><see cref="Usual"/>: the route along the healthy minimum spanning tree - where the
/// node's packets go when nothing is down.</item>
/// <item><see cref="Efficient"/>: the route along the live minimum spanning tree (Prim's,
/// down nodes excluded) - where its packets go now. Null exactly when <see cref="Current"/> is.</item>
/// <item><see cref="Current"/>: fewest hops with down nodes taken out (BFS), for comparison;
/// null when there is no route at all.</item>
/// </list>
/// <see cref="FailedOnUsual"/> are the down nodes that broke the usual route.
/// <see cref="NextHop"/> is the neighbour to hand packets to now, taken from the efficient route.
/// <see cref="CheapestCost"/> is the lowest energy any single route could reach (Dijkstra),
/// to measure the efficient route against. <see cref="RestoredBy"/> - for a stranded node -
/// lists the down nodes that would each reconnect it on their own if brought back.
/// </summary>
public sealed record RouteReport(
    MeshVertex Node,
    RouteStatus Status,
    MeshRoute? Usual,
    MeshRoute? Current,
    MeshRoute? Efficient,
    IReadOnlyList<MeshVertex> FailedOnUsual,
    string? NextHop,
    double? CheapestCost,
    IReadOnlyList<MeshVertex> RestoredBy);

/// <summary>A vertex as the API shows it: the node plus what the dashboard needs to draw it.</summary>
public sealed record MeshVertex(string Id, string Name, string DeploymentPath, bool IsGateway, int Degree);

/// <summary>The whole mesh in one response: every vertex and every link once.</summary>
public sealed record MeshView(IReadOnlyList<MeshVertex> Vertices, IReadOnlyList<MeshLink> Links);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
