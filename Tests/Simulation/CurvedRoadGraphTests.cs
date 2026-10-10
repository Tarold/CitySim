using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class CurvedRoadGraphTests
{
    private RoadGraph _graph;

    [SetUp]
    public void Setup()
    {
        _graph = new RoadGraph();
    }

    [Test]
    public void CreateNode_ArbitraryContinuousWorldPositions_AssignsUniqueIds()
    {
        var n1 = _graph.CreateNode(new Vector2(123.4f, 567.8f));
        var n2 = _graph.CreateNode(new Vector2(987.6f, 543.2f));

        Assert.That(n1.Id, Is.Not.EqualTo(n2.Id));
        Assert.That(n1.WorldPosition, Is.EqualTo(new Vector2(123.4f, 567.8f)));
        Assert.That(n2.WorldPosition, Is.EqualTo(new Vector2(987.6f, 543.2f)));
        Assert.That(_graph.NodeMap[n1.Id], Is.EqualTo(n1));
        Assert.That(_graph.NodeMap[n2.Id], Is.EqualTo(n2));
    }

    [Test]
    public void AddCurvedRoadSegment_EstablishesBidirectionalEdgesWithTrueArcLengths()
    {
        var n1 = _graph.CreateNode(new Vector2(0, 0));
        var n2 = _graph.CreateNode(new Vector2(100, 0));

        Vector2 apex = new Vector2(50, 40);
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);

        bool added = _graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, 1500f);

        Assert.That(added, Is.True);
        Assert.That(_graph.HasEdge(n1.Id, n2.Id), Is.True);
        Assert.That(_graph.HasEdge(n2.Id, n1.Id), Is.True);

        int fwdId = _graph.FindEdgeId(n1.Id, n2.Id);
        int revId = _graph.FindEdgeId(n2.Id, n1.Id);

        var fwd = _graph.Edges[fwdId];
        var rev = _graph.Edges[revId];

        Assert.That(fwd.Length, Is.EqualTo(curve.Length).Within(0.01f));
        Assert.That(rev.Length, Is.EqualTo(curve.Length).Within(0.01f));
        Assert.That(fwd.Length, Is.GreaterThan(100f)); // curved length > straight Euclidean distance

        Assert.That(fwd.Curve, Is.Not.Null);
        Assert.That(rev.Curve, Is.Not.Null);
        Assert.That(rev.Curve.P0, Is.EqualTo(curve.P3));
        Assert.That(rev.Curve.P3, Is.EqualTo(curve.P0));
    }

    [Test]
    public void Dijkstra_CurvedRoads_UsesArcLengthAndChoosesShorterArc()
    {
        // Create 3 nodes: Source (0,0), Target (200,0)
        // Path 1: direct curved road through apex (100, 150) -> very long arc
        // Path 2: straight road Source -> Intermediate (100,0) -> Target -> length 200
        var src = _graph.CreateNode(new Vector2(0, 0));
        var mid = _graph.CreateNode(new Vector2(100, 0));
        var dst = _graph.CreateNode(new Vector2(200, 0));

        // Path 1: very long curve directly between src and dst
        var longCurve = CurveSegment.CreateFromThreePoints(src.WorldPosition, new Vector2(100, 200), dst.WorldPosition);
        _graph.AddCurvedRoadSegment(src.Id, dst.Id, longCurve, 1000f);

        // Path 2: straight segments src -> mid -> dst
        _graph.AddRoadSegment(src.Id, mid.Id, 1000f);
        _graph.AddRoadSegment(mid.Id, dst.Id, 1000f);

        // Dijkstra shortest path should choose the straight two-segment path because longCurve has a much larger arc length!
        var path = _graph.GetShortestPath(src.Id, dst.Id);

        Assert.That(path, Is.Not.Null);
        Assert.That(path.Count, Is.EqualTo(2)); // passes through mid node
        Assert.That(_graph.Edges[path[0]].ToId, Is.EqualTo(mid.Id));
        Assert.That(_graph.Edges[path[1]].ToId, Is.EqualTo(dst.Id));
    }

    [Test]
    public void SplitEdgeAtPoint_InsertsJunctionAndPreservesTopology()
    {
        var nA = _graph.CreateNode(new Vector2(0, 0));
        var nB = _graph.CreateNode(new Vector2(100, 0));

        Vector2 apex = new Vector2(50, 40);
        var originalCurve = CurveSegment.CreateFromThreePoints(nA.WorldPosition, apex, nB.WorldPosition);
        _graph.AddCurvedRoadSegment(nA.Id, nB.Id, originalCurve, 1200f);

        int originalFwdId = _graph.FindEdgeId(nA.Id, nB.Id);
        int originalRevId = _graph.FindEdgeId(nB.Id, nA.Id);

        // Split at the apex (50, 40) which corresponds to t = 0.5
        var junction = _graph.SplitEdgeAtPoint(originalFwdId, apex);

        Assert.That(junction, Is.Not.Null);
        Assert.That(junction.WorldPosition.DistanceTo(apex), Is.LessThan(0.5f));

        // Original edges should now be disconnected
        Assert.That(_graph.Edges[originalFwdId].FromId, Is.EqualTo(-1));
        Assert.That(_graph.Edges[originalRevId].FromId, Is.EqualTo(-1));

        // Direct edge between nA and nB should no longer exist
        Assert.That(_graph.HasEdge(nA.Id, nB.Id), Is.False);
        Assert.That(_graph.HasEdge(nB.Id, nA.Id), Is.False);

        // New edges must exist: nA <-> junction and junction <-> nB
        Assert.That(_graph.HasEdge(nA.Id, junction.Id), Is.True);
        Assert.That(_graph.HasEdge(junction.Id, nA.Id), Is.True);
        Assert.That(_graph.HasEdge(junction.Id, nB.Id), Is.True);
        Assert.That(_graph.HasEdge(nB.Id, junction.Id), Is.True);

        // Sum of subdivided lengths should match original curve arc length
        int eidAJ = _graph.FindEdgeId(nA.Id, junction.Id);
        int eidJB = _graph.FindEdgeId(junction.Id, nB.Id);
        float splitLengthSum = _graph.Edges[eidAJ].Length + _graph.Edges[eidJB].Length;
        Assert.That(splitLengthSum, Is.EqualTo(originalCurve.Length).Within(0.2f));

        // Dijkstra route between nA and nB must now cleanly travel through the junction
        var nodePath = _graph.GetShortestNodePath(nA.Id, nB.Id);
        Assert.That(nodePath, Is.Not.Null);
        Assert.That(nodePath.Count, Is.EqualTo(3));
        Assert.That(nodePath[0], Is.EqualTo(nA.Id));
        Assert.That(nodePath[1], Is.EqualTo(junction.Id));
        Assert.That(nodePath[2], Is.EqualTo(nB.Id));
    }

    [Test]
    public void SplitEdge_CanConnectThirdRoadToCreateTJunction()
    {
        var nA = _graph.CreateNode(new Vector2(0, 0));
        var nB = _graph.CreateNode(new Vector2(100, 0));
        _graph.AddRoadSegment(nA.Id, nB.Id);

        int edgeId = _graph.FindEdgeId(nA.Id, nB.Id);
        var junction = _graph.SplitEdge(edgeId, 0.5f);

        // Add a branch road connecting a new node nC to this junction (T-Junction)
        var nC = _graph.CreateNode(new Vector2(50, 50));
        bool branchAdded = _graph.AddRoadSegment(nC.Id, junction.Id);

        Assert.That(branchAdded, Is.True);
        Assert.That(_graph.GetNodeDegree(junction.Id), Is.EqualTo(3)); // nA, nB, and nC connections
        Assert.That(_graph.HasEdge(nC.Id, junction.Id), Is.True);

        // Shortest path from nC to nA travels through junction
        var path = _graph.GetShortestNodePath(nC.Id, nA.Id);
        Assert.That(path, Is.EqualTo(new System.Collections.Generic.List<int> { nC.Id, junction.Id, nA.Id }));
    }

    [Test]
    public void SplitEdge_SequentialSplits_MaintainsGraphConnectivityAndPathfinding()
    {
        var nA = _graph.CreateNode(new Vector2(0, 0));
        var nB = _graph.CreateNode(new Vector2(300, 0));
        _graph.AddRoadSegment(nA.Id, nB.Id);

        // 1st split: split original segment at t = 0.5 (x ~ 150)
        int edge1 = _graph.FindEdgeId(nA.Id, nB.Id);
        var j1 = _graph.SplitEdge(edge1, 0.5f);
        Assert.That(j1, Is.Not.Null);

        // 2nd split: split first half (nA <-> j1) at t = 0.5 (x ~ 75)
        int edge2 = _graph.FindEdgeId(nA.Id, j1.Id);
        var j2 = _graph.SplitEdge(edge2, 0.5f);
        Assert.That(j2, Is.Not.Null);

        // Verify full path traversal from nA to nB visits j2 -> j1 -> nB
        var path = _graph.GetShortestNodePath(nA.Id, nB.Id);
        Assert.That(path, Is.Not.Null);
        Assert.That(path.Count, Is.EqualTo(4));
        Assert.That(path[0], Is.EqualTo(nA.Id));
        Assert.That(path[1], Is.EqualTo(j2.Id));
        Assert.That(path[2], Is.EqualTo(j1.Id));
        Assert.That(path[3], Is.EqualTo(nB.Id));
    }

    [Test]
    public void SplitEdge_InvalidEdgeId_ReturnsNull()
    {
        Assert.That(_graph.SplitEdge(-1, 0.5f), Is.Null);
        Assert.That(_graph.SplitEdge(9999, 0.5f), Is.Null);
        Assert.That(_graph.SplitEdgeAtPoint(-1, Vector2.Zero), Is.Null);
    }
}
