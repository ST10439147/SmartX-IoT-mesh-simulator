using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Owns the gateway's picture of the physical mesh: a <see cref="MeshGraph"/> whose
/// vertices are the registered ESP32 nodes plus the central gateway, and whose edges are
/// the radio links between them.
///
/// <para><b>Where links come from.</b> Real ESP-WIFI-MESH or Zigbee nodes report which
/// neighbours they can hear and how strongly. Here the <see cref="MeshSimulator"/> plays
/// that part and reports the seeded farm's links through <see cref="Link"/> at boot.</para>
///
/// <para><b>Kept in step with the registry.</b> Every read first syncs the graph against
/// <see cref="TelemetryStore.Sensors"/>: a deleted node drops out with all its links, and
/// a node registered from the dashboard - which no simulator has reported links for - is
/// linked to the nodes it would physically be near (see <see cref="AutoLink"/>), so it
/// never shows up as an island.</para>
/// </summary>
public sealed class MeshNetwork(TelemetryStore store)
{
    /// <summary>Where the gateway physically sits: the pump house, with mains power and the uplink.</summary>
    public const string GatewayPath = "Facility A/Zone 2/Sub-Zone C";

    // Assumed signal for an auto-linked node: a neighbour in the same sub-zone is close,
    // one elsewhere in the zone is further, the gateway is the last resort.
    private const int SameSubZoneRssi = -60;
    private const int SameZoneRssi = -70;
    private const int GatewayFallbackRssi = -75;
    private const int AutoLinkPeers = 2;

    private readonly MeshGraph _graph = new();
    private readonly Lock _gate = new();

    /// <summary>Records a radio link two nodes reported. Either end may be <see cref="MeshGraph.GatewayId"/>.</summary>
    public void Link(string a, string b, int rssiDbm)
    {
        lock (_gate) _graph.Connect(TelemetryStore.KeyFor(a), TelemetryStore.KeyFor(b), rssiDbm);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>The whole mesh, gateway first, then nodes grouped by where they are deployed.</summary>
    public MeshView View()
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            Sync(sensors);

            var vertices = Ordered(_graph.Vertices.Select(id => VertexFor(id, byId))).ToList();
            return new MeshView(vertices, _graph.Links.ToList());
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Runs a BFS from <paramref name="from"/> (the gateway by default) with every node in
    /// <paramref name="down"/> taken out of the mesh, and sorts every other node into
    /// reachable or stranded. Null if <paramref name="from"/> is not on the graph.
    /// </summary>
    /// <param name="down">Nodes that cannot send or relay - the gateway's disconnect set.</param>
    /// <param name="subnet">Optional deployment-path prefix, e.g. <c>Facility A/Zone 1</c>, to narrow the lists to one area.</param>
    public ReachabilityReport? Reachability(string from, IReadOnlySet<string> down, string? subnet = null)
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        from = TelemetryStore.KeyFor(from);
        down = IgnoringCase(down);

        lock (_gate)
        {
            Sync(sensors);
            if (!_graph.Contains(from)) return null;

            var tree = _graph.BreadthFirst(from, down);

            // The subnet filter only narrows what is reported. The search itself always runs
            // over the whole mesh, because a Zone 1 node's way home can run through Zone 2.
            bool InSubnet(MeshVertex v) => subnet is null || v.DeploymentPath.StartsWith(subnet, StringComparison.OrdinalIgnoreCase);

            var everyone = Ordered(_graph.Vertices.Select(id => VertexFor(id, byId))).Where(InSubnet).ToList();

            var reachable = tree.Order
                .Select(id => VertexFor(id, byId))
                .Where(InSubnet)
                .Select(v => new ReachableNode(v.Id, v.Name, v.DeploymentPath, tree.Hops[v.Id]))
                .ToList();

            return new ReachabilityReport(
                from,
                subnet,
                reachable,
                Down: everyone.Where(v => down.Contains(v.Id)).ToList(),
                Stranded: everyone.Where(v => !down.Contains(v.Id) && !tree.Reached(v.Id)).ToList(),
                tree.Order);
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Traces one node's route to the gateway by MAC or id, and what an outage has done to it.
    /// Null if the node is not on the mesh (or is the gateway itself).
    ///
    /// <para>Two BFS runs from the gateway: one over the healthy mesh gives the
    /// <b>usual</b> route, one with the down nodes taken out gives the <b>current</b> route.
    /// If the usual route runs through a down node and a current route still exists, the
    /// mesh has healed around the failure and <see cref="RouteReport.NextHop"/> says
    /// where the node should now send its packets. If no current route exists, the node is
    /// stranded, and one more BFS per down node finds which repairs would reconnect it.</para>
    /// </summary>
    // ponytail: fewest hops, not least energy. The power-efficient (MST) routing in
    // section 4 swaps the BFS tree for a minimum spanning tree.
    public RouteReport? Route(string macOrId, IReadOnlySet<string> down)
    {
        var sensors = store.Sensors;
        var byId = sensors.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var id = TelemetryStore.KeyFor(macOrId);
        down = IgnoringCase(down);

        lock (_gate)
        {
            Sync(sensors);
            if (!_graph.Contains(id) || id == MeshGraph.GatewayId) return null;

            var node = VertexFor(id, byId);
            var usual = RouteOf(_graph.BreadthFirst(MeshGraph.GatewayId), id, byId);
            var failed = usual?.Hops.Where(h => down.Contains(h.Id) && h.Id != id).ToList() ?? [];

            if (down.Contains(id))
                return new RouteReport(node, RouteStatus.Down, usual, null, failed, null, []);

            var current = RouteOf(_graph.BreadthFirst(MeshGraph.GatewayId, down), id, byId);

            if (current is null)
            {
                // Try bringing each down node back on its own: any that reconnects this node
                // on its own is a repair worth doing first.
                var restoredBy = down
                    .Where(_graph.Contains)
                    .Where(d => _graph.BreadthFirst(MeshGraph.GatewayId, down.Where(o => o != d).ToHashSet()).Reached(id))
                    .Select(d => VertexFor(d, byId))
                    .OrderBy(v => v.Name)
                    .ToList();

                return new RouteReport(node, RouteStatus.Stranded, usual, null, failed, null, restoredBy);
            }

            var status = failed.Count > 0 ? RouteStatus.Rerouted : RouteStatus.Online;
            return new RouteReport(node, status, usual, current, failed, current.Hops[1].Id, []);
        }
    }

    // Turns a BFS tree's parent chain into hops plus the links between them. Caller holds _gate.
    private MeshRoute? RouteOf(BfsTree tree, string id, IReadOnlyDictionary<string, SensorRegistration> byId)
    {
        var path = tree.PathFrom(id);
        if (path is null) return null;

        var links = path.Zip(path.Skip(1), (a, b) => _graph.LinkBetween(a, b)!).ToList();
        return new MeshRoute(
            path.Select(p => VertexFor(p, byId)).ToList(),
            links,
            Math.Round(links.Sum(l => l.Cost), 2));
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private MeshVertex VertexFor(string id, IReadOnlyDictionary<string, SensorRegistration> byId)
        => byId.TryGetValue(id, out var s)
            ? new MeshVertex(id, s.DisplayName, s.DeploymentPath, false, _graph.DegreeOf(id))
            : new MeshVertex(id, "Central gateway", GatewayPath, true, _graph.DegreeOf(id));

    // Vertex ids ignore case, so a caller's down set must too, whatever comparer it was built with.
    private static HashSet<string> IgnoringCase(IEnumerable<string> ids)
        => ids.Select(TelemetryStore.KeyFor).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Gateway first, then nodes grouped by where they are deployed.
    private static IEnumerable<MeshVertex> Ordered(IEnumerable<MeshVertex> vertices)
        => vertices.OrderByDescending(v => v.IsGateway).ThenBy(v => v.DeploymentPath).ThenBy(v => v.Name);
    //----------------------------------------------------------------------------------------------------------------------//
    // Caller holds _gate.
    private void Sync(IReadOnlyCollection<SensorRegistration> sensors)
    {
        _graph.AddVertex(MeshGraph.GatewayId);

        var registered = sensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _graph.Vertices.ToList())
            if (id != MeshGraph.GatewayId && !registered.Contains(id))
                _graph.RemoveVertex(id);

        foreach (var sensor in sensors)
            if (!_graph.Contains(sensor.Id))
                AutoLink(sensor, sensors);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Links a node nobody has reported links for to up to two nodes already in the mesh:
    /// same sub-zone first, then same zone. With neither nearby, it links straight to the
    /// gateway, so every registered node is always reachable on the graph.
    /// </summary>
    // ponytail: placement is guessed from the deployment path. Replace with the node's own
    // neighbour/RSSI report once real hardware is in the loop.
    private void AutoLink(SensorRegistration sensor, IReadOnlyCollection<SensorRegistration> sensors)
    {
        var zone = ZoneOf(sensor.DeploymentPath);

        var peers = sensors
            .Where(s => s.Id != sensor.Id && _graph.Contains(s.Id))
            .Select(s => (s.Id, Rssi:
                string.Equals(s.DeploymentPath, sensor.DeploymentPath, StringComparison.OrdinalIgnoreCase) ? SameSubZoneRssi
                : string.Equals(ZoneOf(s.DeploymentPath), zone, StringComparison.OrdinalIgnoreCase) ? SameZoneRssi
                : (int?)null))
            .Where(p => p.Rssi is not null)
            .OrderByDescending(p => p.Rssi)
            .Take(AutoLinkPeers)
            .ToList();

        if (peers.Count == 0)
        {
            _graph.Connect(sensor.Id, MeshGraph.GatewayId, GatewayFallbackRssi);
            return;
        }

        foreach (var (id, rssi) in peers) _graph.Connect(sensor.Id, id, rssi!.Value);
    }

    // "Facility A/Zone 1/Sub-Zone B" -> "Facility A/Zone 1"
    private static string ZoneOf(string path) => string.Join('/', path.Split('/').Take(2));
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
