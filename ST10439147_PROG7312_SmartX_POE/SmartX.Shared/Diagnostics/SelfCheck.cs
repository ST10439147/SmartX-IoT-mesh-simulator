using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SmartX.Shared.Telemetry;
using SmartX.Shared.Topology;

namespace SmartX.Shared.Diagnostics;

/// <summary>
/// One runnable check over the non-trivial logic: ring wraparound, operator overloading,
/// the jagged-to-List hand-off, recursive validation under hostile depth, and the claim
/// that none of it boxes.
///
/// <para>Run it with <c>dotnet run --project src/SmartX.Api -- --selftest</c>.
/// Non-zero exit means something below is broken.</para>
/// </summary>
public static class SelfCheck
{
    public static IReadOnlyList<string> Run()
    {
        var log = new List<string>();

        CircularBufferWrapsAndKeepsOrder(log);
        OperatorsAggregateAndCompare(log);
        GenericPacketCarriesAllThreePayloadTypesWithoutBoxing(log);
        JaggedStagingFlushesIntoList(log);
        RecursiveValidationAcceptsAGoodTree(log);
        RecursiveValidationRejectsBadNesting(log);
        RecursiveValidationSurvivesPathologicalDepth(log);
        RecursiveValidationDetectsCycles(log);
        PacketSurvivesJsonRoundTrip(log);
        EverySensorIsASpecificAgriculturalDevice(log);

        return log;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [12]: Wikipedia. Circular buffer.
    //   https://en.wikipedia.org/wiki/Circular_buffer
    private static void CircularBufferWrapsAndKeepsOrder(List<string> log)
    {
        var buffer = new CircularTelemetryBuffer<int>(4);

        for (var i = 1; i <= 6; i++)
            buffer.Add(new TelemetryPacket<int>("meter-1", i, SensorCategory.PumpPower));

        Assert(buffer.Count == 4, "buffer should cap at its capacity");
        Assert(buffer.Capacity == 4, "capacity should not drift");
        Assert(buffer.TotalWritten == 6, "lifetime counter should include overwritten packets");

        // 1 and 2 were overwritten; what remains is 3,4,5,6 oldest-first.
        var values = buffer.ToList().Select(p => p.Value).ToArray();
        Assert(values.SequenceEqual([3, 4, 5, 6]), $"expected 3,4,5,6 after wrap but got {string.Join(",", values)}");

        Assert(buffer[0].Value == 3, "indexer 0 must be the oldest retained packet");
        Assert(buffer.TryGetNewest(out var newest) && newest.Value == 6, "newest packet should be 6");
        Assert(buffer.TakeLast(2).Select(p => p.Value).SequenceEqual([5, 6]), "TakeLast should return the tail");

        log.Add("ring buffer: wraps, preserves order, indexes oldest-first");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [3]: Microsoft. Generic math (System.Numerics.INumber<T>). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/standard/generics/math
    // Reference [8]: Microsoft. Operator overloading - predefined unary, arithmetic and comparison operators. C# language reference, Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/operators/operator-overloading
    private static void OperatorsAggregateAndCompare(List<string> log)
    {
        var earlier = DateTimeOffset.UtcNow.AddSeconds(-5);
        var later = DateTimeOffset.UtcNow;

        var meter1 = new SensorReading<float>("meter-1", 240.5f, later);
        var meter2 = new SensorReading<float>("meter-2", 118.25f, earlier);

        var combined = meter1 + meter2;
        Assert(Math.Abs(combined.Value - 358.75f) < 0.001f, "operator + should aggregate the two loads");
        Assert(combined.SensorId == "meter-1+meter-2", "aggregate should name its sources");
        Assert(combined.TimestampUtc == earlier, "an aggregate is only as fresh as its stalest input");

        var delta = meter1 - meter2;
        Assert(Math.Abs(delta.Value - 122.25f) < 0.001f, "operator - should give the delta");
        Assert(Math.Abs((-meter1).Value + 240.5f) < 0.001f, "unary negation should flip the sign");

        Assert(meter1 > meter2, "meter1 draws more than meter2");
        Assert(meter2 < meter1, "the mirrored comparison must agree");
        Assert(meter1 > 200f, "comparison straight against a bare float should work");
        Assert(meter2 <= 118.25f, "<= against a bare float should be inclusive");

        var meter1Again = new SensorReading<float>("meter-1", 240.5f, later);
        Assert(meter1 >= meter1Again, ">= must hold for equal readings");
        Assert(meter1 == meter1Again, "== should compare by value");
        Assert(meter1 != meter2, "!= must be the negation of ==");
        Assert(meter1.GetHashCode() == meter1Again.GetHashCode(), "equal readings must hash equally");
        Assert((float)meter1 == 240.5f, "explicit conversion should yield the raw value");

        // Same operators, a different numeric type - generic math, not per-type overloads.
        var wattA = new SensorReading<int>("inverter-a", 900);
        var wattB = new SensorReading<int>("inverter-b", 350);
        Assert((wattA + wattB).Value == 1250, "operator + should work identically for int");

        var farm = new[] { meter1, meter2, new SensorReading<float>("meter-3", 41.25f, later) };
        Assert(Math.Abs(farm.Sum().Value - 400f) < 0.001f, "fold over operator + should total the farm");

        log.Add("operators: +, -, unary -, ==, !=, <, >, <=, >= over float and int");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [1]: Microsoft. Generics in C#. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/fundamentals/types/generics
    private static void GenericPacketCarriesAllThreePayloadTypesWithoutBoxing(List<string> log)
    {
        var moisture = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);
        var wattage = new TelemetryPacket<int>("meter-1", 2400, SensorCategory.PumpPower);
        var valve = new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve);

        Assert(moisture.Value == 41.8f && wattage.Value == 2400 && valve.Value, "one wrapper, three payload types");
        Assert(valve == new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve, valve.TimestampUtc),
            "packet equality should compare id, value and timestamp");

        // The no-boxing claim, measured rather than asserted in a comment. Filling a
        // pre-allocated ring with 50 000 value-type packets must not allocate heap.
        // If TelemetryPacket<T> were a class, or if T were boxed into object anywhere
        // on this path, this would allocate megabytes.
        var buffer = new CircularTelemetryBuffer<float>(512);
        var packet = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);

        buffer.Add(packet); // warm up, so JIT and first-touch costs are not counted

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50_000; i++) buffer.Add(in packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert(allocated < 4096, $"ingest path allocated {allocated} bytes for 50 000 packets - something is boxing");

        log.Add($"generics: float/int/bool through one wrapper, {allocated} bytes allocated over 50 000 ingests");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [13]: Microsoft. Jagged arrays (C# programming guide). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/programming-guide/arrays/jagged-arrays
    // Reference [14]: Microsoft. Multidimensional arrays (C# programming guide). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/arrays
    private static void JaggedStagingFlushesIntoList(List<string> log)
    {
        var archive = new TelemetryBatchArchive(cycleCapacity: 3);
        var now = DateTimeOffset.UtcNow;

        // Deliberately ragged: the mesh delivered 2, then 4, then 1 packet.
        archive.StageCycle([1f, 2f], now);
        archive.StageCycle([3f, 4f, 5f, 6f], now.AddSeconds(1));
        archive.StageCycle([7f], now.AddSeconds(2));

        Assert(archive.CycleCount == 3, "three cycles staged");
        Assert(archive.WidestCycle == 4, "widest cycle should be 4 samples");

        var jagged = archive.ToJagged();
        Assert(jagged.Length == 3 && jagged[0].Length == 2 && jagged[1].Length == 4 && jagged[2].Length == 1,
            "jagged rows should keep their individual lengths");

        var packets = archive.Flush("probe-7", SensorCategory.SoilMoisture);
        Assert(packets.Count == 7, $"7 raw samples should become 7 packets, got {packets.Count}");
        Assert(packets is List<TelemetryPacket<float>>, "flush target must be a generic List<T>");
        Assert(Math.Abs(packets[^1].Value - 7f) < 0.001f, "last packet should be the newest cycle's sample");

        var dense = archive.ToDenseMatrix();
        Assert(dense.GetLength(0) == 3 && dense.GetLength(1) == 4, "dense projection should be 3x4");
        Assert(float.IsNaN(dense[0, 2]), "short rows must be padded with NaN, not silently zeroed");
        Assert(Math.Abs(dense[1, 3] - 6f) < 0.001f, "the widest row should survive the projection intact");

        // Fourth cycle overwrites the first.
        archive.StageCycle([8f, 9f], now.AddSeconds(3));
        Assert(archive.CycleCount == 3, "archive is a ring and stays bounded");
        Assert(Math.Abs(archive.ToJagged()[0][0] - 3f) < 0.001f, "oldest cycle should have been evicted");

        log.Add("arrays: ragged float[][] staging, float[,] projection, flush into List<T>");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static DeploymentNode GoodTree() =>
        DeploymentNode.Create("Facility A", NodeKind.Facility,
            DeploymentNode.Create("Zone 1", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone B", NodeKind.SubZone,
                    new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "A4:CF:12:9B:04:7E" },
                    new DeploymentNode { Name = "node-4", Kind = NodeKind.Node, SensorId = "A4:CF:12:9B:04:7F" })));
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationAcceptsAGoodTree(List<string> log)
    {
        var report = TopologyValidator.Validate(GoodTree());

        Assert(report.IsValid, "a well-formed Facility/Zone/Sub-Zone/Node tree should validate: "
                               + string.Join("; ", report.Issues.Select(i => $"{i.Path}: {i.Message}")));
        Assert(report.NodesVisited == 5, $"expected 5 nodes visited, got {report.NodesVisited}");
        Assert(report.MaxDepthReached == 4, $"expected depth 4, got {report.MaxDepthReached}");
        Assert(TopologyValidator.PathExists(GoodTree(), "Facility A/Zone 1/Sub-Zone B/node-3"),
            "the documented path should resolve");
        Assert(!TopologyValidator.PathExists(GoodTree(), "Facility A/Zone 9/node-3"),
            "a path through a zone that does not exist must not resolve");

        log.Add("recursion: valid four-tier tree accepted, breadcrumb path resolves");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationRejectsBadNesting(List<string> log)
    {
        // A Node hung straight off the Facility, skipping Zone and Sub-Zone.
        var bad = DeploymentNode.Create("Facility A", NodeKind.Facility,
            new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "AA:BB:CC:DD:EE:FF" });

        var report = TopologyValidator.Validate(bad);
        Assert(!report.IsValid, "a Node directly inside a Facility should be rejected");
        Assert(report.Issues.Any(i => i.Path == "Facility A/node-3" && i.Severity == IssueSeverity.Error),
            "the issue should be addressed by its breadcrumb path");

        // Same sensor mounted at two places in the tree.
        var duplicated = DeploymentNode.Create("Facility A", NodeKind.Facility,
            DeploymentNode.Create("Zone 1", NodeKind.Zone,
                DeploymentNode.Create("Sub-Zone B", NodeKind.SubZone,
                    new DeploymentNode { Name = "node-3", Kind = NodeKind.Node, SensorId = "DUP" },
                    new DeploymentNode { Name = "node-4", Kind = NodeKind.Node, SensorId = "DUP" })));

        Assert(!TopologyValidator.Validate(duplicated).IsValid, "one sensor cannot be mounted twice");

        log.Add("recursion: tier-skipping and duplicate sensor mounts rejected with paths");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [16]: Microsoft. StackOverflowException Class (cannot be caught by a try/catch). Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/api/system.stackoverflowexception
    private static void RecursiveValidationSurvivesPathologicalDepth(List<string> log)
    {
        // 50 000 tiers. Without the depth guard this is a StackOverflowException, which
        // .NET cannot catch - the process dies outright and takes the gateway with it.
        var root = new DeploymentNode { Name = "Facility A", Kind = NodeKind.Facility };
        var cursor = root;
        for (var i = 0; i < 50_000; i++)
        {
            var child = new DeploymentNode { Name = $"tier-{i}", Kind = NodeKind.Zone };
            cursor.Children.Add(child);
            cursor = child;
        }

        var report = TopologyValidator.Validate(root);

        Assert(!report.IsValid, "a 50 000-tier payload should be reported as invalid");
        Assert(report.MaxDepthReached <= TopologyValidator.MaxDepth,
            $"walk went {report.MaxDepthReached} deep, past the {TopologyValidator.MaxDepth} ceiling");
        Assert(report.Issues.Any(i => i.Message.Contains("ceiling")), "the depth ceiling should be the reported reason");

        log.Add($"recursion: 50 000-tier payload stopped at depth {report.MaxDepthReached}, no stack overflow");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void RecursiveValidationDetectsCycles(List<string> log)
    {
        var root = DeploymentNode.Create("Facility A", NodeKind.Facility);
        var zone = DeploymentNode.Create("Zone 1", NodeKind.Zone);
        root.Children.Add(zone);
        zone.Children.Add(root); // wired back into its own ancestor

        var report = TopologyValidator.Validate(root);
        Assert(report.Issues.Any(i => i.Message.Contains("Cycle")), "a self-referencing tree should be caught, not looped over");

        log.Add("recursion: cycle in the deployment graph detected and reported");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // Reference [2]: Microsoft. How to serialize and deserialize JSON using System.Text.Json. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/how-to
    private static void PacketSurvivesJsonRoundTrip(List<string> log)
    {
        var original = new TelemetryPacket<float>("probe-7", 41.8f, SensorCategory.SoilMoisture);
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<TelemetryPacket<float>>(json);

        Assert(restored == original, $"generic packet should survive the wire; got back {json}");

        var boolPacket = new TelemetryPacket<bool>("valve-3", true, SensorCategory.IrrigationValve);
        Assert(JsonSerializer.Deserialize<TelemetryPacket<bool>>(JsonSerializer.Serialize(boolPacket)) == boolPacket,
            "the bool payload should survive the wire too");

        log.Add("serialisation: TelemetryPacket<float> and <bool> round-trip through JSON");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void EverySensorIsASpecificAgriculturalDevice(List<string> log)
    {
        // Spec() throws for a category with no row, so an enum member added without its
        // payload and unit fails here instead of on the first registration of that type.
        var categories = Enum.GetValues<SensorCategory>();
        var payloads = categories.Select(c => c.Spec().Payload).Distinct().Count();
        Assert(payloads == 3, "the categories must still exercise float, int and bool payloads");

        var pump = new SensorRegistration { Category = SensorCategory.PumpPower };
        Assert(pump.Payload == PayloadKind.Integer && pump.Unit == "W", "payload and unit must come from the category");

        // Enum JSON accepts bare numbers, so an undefined category has to die in validation.
        var bogus = new SensorRegistration
        {
            MacAddress = "A4:CF:12:9B:04:7E",
            DisplayName = "Mystery probe",
            DeploymentPath = "Facility A/Zone 1",
            Category = (SensorCategory)99
        };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(bogus, new ValidationContext(bogus), results, validateAllProperties: true);
        Assert(results.Count == 1 && results[0].MemberNames.Contains(nameof(SensorRegistration.Category)),
            "an undefined category must be the one thing validation rejects");

        log.Add($"categories: {categories.Length} agricultural sensor types, payload and unit fixed per type, unknown types rejected");
    }
    //----------------------------------------------------------------------------------------------------------------------//
    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new SelfCheckFailure(because);
    }
}

public sealed class SelfCheckFailure(string message) : Exception(message);
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
