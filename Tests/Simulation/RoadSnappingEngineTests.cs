using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class RoadSnappingEngineTests
{
    private RoadGraph _graph;

    [SetUp]
    public void Setup()
    {
        _graph = new RoadGraph();
    }

    [Test]
    public void FindSnap_NearExistingNode_ReturnsNodeSnap()
    {
        var node = _graph.CreateNode(new Vector2(100, 100));

        // Cursor is 10px away (within default 24px radius)
        Vector2 cursor = new Vector2(106, 108);
        var result = RoadSnappingEngine.FindSnap(cursor, _graph);

        Assert.That(result.Type, Is.EqualTo(SnapType.Node));
        Assert.That(result.Position, Is.EqualTo(node.WorldPosition));
        Assert.That(result.SnappedNode, Is.EqualTo(node));
    }

    [Test]
    public void FindSnap_NearRoadCurve_ReturnsEdgeSnap()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(100, 0));
        _graph.AddRoadSegment(n1.Id, n2.Id);

        // Cursor at (50, 12), which is 12px away from the edge curve along y=0
        Vector2 cursor = new Vector2(50, 12);
        var result = RoadSnappingEngine.FindSnap(cursor, _graph);

        Assert.That(result.Type, Is.EqualTo(SnapType.Edge));
        Assert.That(result.Position.X, Is.EqualTo(50f).Within(1.0f));
        Assert.That(result.Position.Y, Is.EqualTo(0f).Within(0.1f));
        Assert.That(result.SnappedEdge, Is.Not.Null);
    }

    [Test]
    public void FindSnap_AngleSnap_SnapsTo45DegreesWhenRequested()
    {
        Vector2 origin = new Vector2(0, 0);
        // Vector at approx 42 degrees (close to 45 deg)
        float len = 100f;
        float angleRad = Mathf.DegToRad(42f);
        Vector2 cursor = origin + new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad)) * len;

        var result = RoadSnappingEngine.FindSnap(cursor, _graph, startPoint: origin, forceAngleSnap: true);

        Assert.That(result.Type, Is.EqualTo(SnapType.Angle));
        Assert.That(result.AngleDegrees, Is.EqualTo(45f).Within(0.1f));
        // X and Y should be roughly equal (cos(45) == sin(45))
        Assert.That(result.Position.X, Is.EqualTo(result.Position.Y).Within(0.1f));
    }

    [Test]
    public void FindSnap_TangentSnap_AlignsWithRoadContinuation()
    {
        // Existing road coming from (0,0) to junction at (100, 0)
        var nA = _graph.CreateNode(new Vector2(0, 0));
        var junction = _graph.CreateNode(new Vector2(100, 0));
        _graph.AddRoadSegment(nA.Id, junction.Id);

        // Building from junction continuing roughly forward towards (200, 5) (angle close to 0 deg continuation)
        Vector2 cursor = new Vector2(200, 8);
        var result = RoadSnappingEngine.FindSnap(cursor, _graph, startPoint: junction.WorldPosition, startNode: junction);

        Assert.That(result.Type, Is.EqualTo(SnapType.Tangent));
        // Tangent should snap along exactly +X direction (1, 0) continuing the road smoothly
        Assert.That(result.Position.Y, Is.EqualTo(0f).Within(0.01f));
        Assert.That(result.Position.X, Is.GreaterThan(100f));
    }

    [Test]
    public void FindSnap_FarFromAnyObject_ReturnsNoneFreeform()
    {
        _graph.CreateNode(new Vector2(0, 0));

        Vector2 farCursor = new Vector2(500, 500);
        var result = RoadSnappingEngine.FindSnap(farCursor, _graph);

        Assert.That(result.Type, Is.EqualTo(SnapType.None));
        Assert.That(result.Position, Is.EqualTo(farCursor));
    }
}
