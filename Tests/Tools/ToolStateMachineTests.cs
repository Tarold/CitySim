using Godot;
using NUnit.Framework;
using System.Linq;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.Tools;
using CitySim.UI;

namespace CitySim.Tests.Tools;

[TestFixture]
public class ToolStateMachineTests
{
    private const float Budget = 1_000_000f;

    private SimulationWorld _world;
    private FakeToolHost _host;

    [SetUp]
    public void Setup()
    {
        _world = SimulationWorld.Create(ScenarioType.Sandbox);
        _world.RecalculateMacroModel();
        _world.Economy.SetBalance(Budget);
        _host = new FakeToolHost(_world);
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private static ToolInput At(float x, float y, bool shift = false) => new ToolInput(new Vector2(x, y), shift);

    private Vector2 CellCenter(int x, int y) => _world.Grid.GetWorldCenter(_world.Grid.GetZoneId(x, y));

    private (RoadNode A, RoadNode B) BuildStraightRoad(Vector2 a, Vector2 b)
    {
        var nodeA = _world.RoadGraph.CreateNode(a);
        var nodeB = _world.RoadGraph.CreateNode(b);
        _world.RoadGraph.AddCurvedRoadSegment(nodeA.Id, nodeB.Id, new CurveSegment(a, b));
        _world.ApplyTopologyChange();
        return (nodeA, nodeB);
    }

    // ---------- RoadBuildTool ----------

    [Test]
    public void RoadBuild_StraightTwoClicks_BuildsRoadAndResets()
    {
        var tool = new RoadBuildTool(_host, curved: false);

        tool.OnPrimaryClick(At(320f, 320f));
        Assert.That(tool.Step, Is.EqualTo(1));
        Assert.That(_host.LastHint, Does.Contain("Step 2/2"));
        Assert.That(_host.FakePreview.RoadBuildPreviewStep, Is.EqualTo(1));

        int startId = tool.StartNode.Id;
        tool.OnPrimaryClick(At(512f, 320f));

        Assert.That(tool.Step, Is.EqualTo(0));
        Assert.That(_host.LastHint, Does.Contain("Road built"));
        Assert.That(_world.RoadGraph.AdjacencyEdges[startId].Count, Is.EqualTo(1));
        Assert.That(_world.Economy.Money, Is.LessThan(Budget));
        Assert.That(_host.FakePreview.RoadBuildPreviewStep, Is.EqualTo(-1));
    }

    [Test]
    public void RoadBuild_CurvedThreeClicks_BuildsCurvedRoad()
    {
        var tool = new RoadBuildTool(_host, curved: true);

        tool.OnPrimaryClick(At(320f, 320f));
        tool.OnPrimaryClick(At(576f, 320f));
        Assert.That(tool.Step, Is.EqualTo(2));
        Assert.That(_host.LastHint, Does.Contain("Step 3/3"));

        int startId = tool.StartNode.Id;
        tool.OnPrimaryClick(At(448f, 220f));

        Assert.That(tool.Step, Is.EqualTo(0));
        var edge = _world.RoadGraph.GetEdge(_world.RoadGraph.AdjacencyEdges[startId][0]);
        Assert.That(edge.Length, Is.GreaterThan(256f), "Curve is longer than the straight chord");
    }

    [Test]
    public void RoadBuild_EndTooClose_StaysOnEndStepWithWarning()
    {
        var tool = new RoadBuildTool(_host, curved: false);
        tool.OnPrimaryClick(At(320f, 320f));

        tool.OnPrimaryClick(At(322f, 321f));

        Assert.That(tool.Step, Is.EqualTo(1));
        Assert.That(_host.LastHint, Does.Contain("too short"));
    }

    [Test]
    public void RoadBuild_InsufficientFunds_KeepsDraftForRetry()
    {
        var tool = new RoadBuildTool(_host, curved: false);
        tool.OnPrimaryClick(At(320f, 320f));
        _world.Economy.SetBalance(0f);

        tool.OnPrimaryClick(At(704f, 320f));

        Assert.That(tool.Step, Is.EqualTo(1));
        Assert.That(_host.LastHint, Does.Contain("Insufficient funds"));
    }

    [Test]
    public void RoadBuild_Cancel_OnlyConsumedWhileDrafting()
    {
        var tool = new RoadBuildTool(_host, curved: false);
        Assert.That(tool.OnCancel(), Is.False);

        tool.OnPrimaryClick(At(320f, 320f));
        Assert.That(tool.OnCancel(), Is.True);
        Assert.That(tool.Step, Is.EqualTo(0));
        Assert.That(_host.LastHint, Does.Contain("cancelled"));
    }

    [Test]
    public void RoadBuild_StartOnExistingEdge_SplitsEdgeAndAppliesOnNextTick()
    {
        BuildStraightRoad(new Vector2(320f, 640f), new Vector2(704f, 640f));
        var tool = new RoadBuildTool(_host, curved: false);
        int nodesBefore = _world.RoadGraph.Nodes.Count;

        tool.OnPrimaryClick(At(512f, 642f));

        Assert.That(_world.RoadGraph.Nodes.Count, Is.EqualTo(nodesBefore + 1));
        Assert.That(_world.RoadGraph.GetNodeDegree(tool.StartNode.Id), Is.EqualTo(2), "Junction joins both halves");
        Assert.That(_world.Topology.HasPendingChanges, Is.True);

        _world.Tick(0f);
        Assert.That(_world.Topology.HasPendingChanges, Is.False);
    }

    [Test]
    public void RoadBuild_Hover_ShowsCostForCurrentStep()
    {
        var tool = new RoadBuildTool(_host, curved: false);
        tool.OnHover(At(320f, 320f));
        Assert.That(_host.FakePreview.RoadBuildPreviewStep, Is.EqualTo(0));

        tool.OnPrimaryClick(At(320f, 320f));
        tool.OnHover(At(512f, 320f));

        Assert.That(_host.FakePreview.RoadBuildPreviewStep, Is.EqualTo(1));
        Assert.That(_host.FakePreview.RoadBuildPreviewCost,
            Is.EqualTo(BuildRoadCommand.CalculateCost(new CurveSegment(new Vector2(320f, 320f), new Vector2(512f, 320f)))));
    }

    // ---------- DemolishTool ----------

    private (int A, int B, int C) BuildGridStreet()
    {
        int a = _world.Grid.GetZoneId(5, 5);
        int b = _world.Grid.GetZoneId(6, 5);
        int c = _world.Grid.GetZoneId(7, 5);
        foreach (int id in new[] { a, b, c })
        {
            new ZoneCellCommand(id, ZoneType.Residential).Execute(_world);
        }
        _world.RoadGraph.AddRoadSegment(a, b);
        _world.RoadGraph.AddRoadSegment(b, c);
        _world.ApplyTopologyChange();
        return (a, b, c);
    }

    [Test]
    public void Demolish_StartThenAdjacentConnectedCell_RemovesRoad()
    {
        var (a, b, _) = BuildGridStreet();
        var tool = new DemolishTool(_host);

        tool.OnPrimaryClick(new ToolInput(CellCenter(5, 5)));
        Assert.That(tool.PendingStartZoneId, Is.EqualTo(a));
        Assert.That(_host.FakePreview.DemolishTargets, Is.EqualTo(new[] { b }));

        tool.OnPrimaryClick(new ToolInput(CellCenter(6, 5)));

        Assert.That(_world.RoadGraph.HasEdge(a, b), Is.False);
        Assert.That(tool.PendingStartZoneId, Is.EqualTo(-1));
        Assert.That(_host.LastHint, Does.Contain("Road demolished"));
    }

    [Test]
    public void Demolish_NonAdjacentConnectedCell_ReselectsStart()
    {
        var (a, _, c) = BuildGridStreet();
        var tool = new DemolishTool(_host);
        tool.OnPrimaryClick(new ToolInput(CellCenter(5, 5)));

        tool.OnPrimaryClick(new ToolInput(CellCenter(7, 5)));

        Assert.That(tool.PendingStartZoneId, Is.EqualTo(c));
        Assert.That(_host.LastHint, Does.Contain("New start cell"));
        Assert.That(_world.RoadGraph.HasEdge(a, _world.Grid.GetZoneId(6, 5)), Is.True);
    }

    [Test]
    public void Demolish_ClickStartAgain_CancelsSelection()
    {
        BuildGridStreet();
        var tool = new DemolishTool(_host);
        tool.OnPrimaryClick(new ToolInput(CellCenter(5, 5)));

        tool.OnPrimaryClick(new ToolInput(CellCenter(5, 5)));

        Assert.That(tool.PendingStartZoneId, Is.EqualTo(-1));
        Assert.That(_host.LastHint, Does.Contain("Selection cancelled"));
    }

    [Test]
    public void Demolish_IsolatedZonedCell_DemolishedDirectlyAndSelectionReleased()
    {
        int id = _world.Grid.GetZoneId(9, 9);
        new ZoneCellCommand(id, ZoneType.Commercial).Execute(_world);
        var tool = new DemolishTool(_host);

        tool.OnPrimaryClick(new ToolInput(CellCenter(9, 9)));

        Assert.That(_world.Grid.GetZone(id).Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_host.ReleasedZoneSelections, Does.Contain(id));
        Assert.That(_host.CityOverviewCalls, Is.EqualTo(1));
    }

    [Test]
    public void Demolish_Reset_ClearsPendingSelection()
    {
        BuildGridStreet();
        var tool = new DemolishTool(_host);
        tool.OnPrimaryClick(new ToolInput(CellCenter(5, 5)));

        tool.Reset();

        Assert.That(tool.PendingStartZoneId, Is.EqualTo(-1));
        Assert.That(tool.OnCancel(), Is.False);
    }

    // ---------- ZoningTool ----------

    [Test]
    public void Zoning_ClickEmptyCell_ZonesCellAndShowsOverview()
    {
        var tool = new ZoningTool(_host, InteractionMode.ZoneResidential);

        tool.OnPrimaryClick(new ToolInput(CellCenter(4, 4)));

        Assert.That(_world.Grid.GetZone(_world.Grid.GetZoneId(4, 4)).Type, Is.EqualTo(ZoneType.Residential));
        Assert.That(_host.CityOverviewCalls, Is.EqualTo(1));
        Assert.That(_host.LastHint, Does.Contain("Designated cell (4, 4)"));
    }

    [Test]
    public void Dezoning_ClickZonedCell_ClearsCellAndReleasesSelection()
    {
        int id = _world.Grid.GetZoneId(4, 4);
        new ZoneCellCommand(id, ZoneType.Industrial).Execute(_world);
        var tool = new ZoningTool(_host, InteractionMode.Dezone);

        tool.OnPrimaryClick(new ToolInput(CellCenter(4, 4)));

        Assert.That(_world.Grid.GetZone(id).Type, Is.EqualTo(ZoneType.Empty));
        Assert.That(_host.ReleasedZoneSelections, Does.Contain(id));
    }

    [Test]
    public void Zoning_ClickRoadsideLot_ZonesParcelInsteadOfCell()
    {
        BuildStraightRoad(new Vector2(256f, 900f), new Vector2(832f, 900f));
        var parcel = _world.Parcels.Parcels.First();
        var tool = new ZoningTool(_host, InteractionMode.ZoneCommercial);

        tool.OnHover(new ToolInput(parcel.Center));
        Assert.That(_host.FakePreview.HoveredParcels, Does.Contain(parcel));
        Assert.That(_host.LastHint, Does.Contain($"${EconomyManager.ZoningCost:F0}"));

        tool.OnPrimaryClick(new ToolInput(parcel.Center));

        Assert.That(parcel.ZoneType, Is.EqualTo(ZoneType.Commercial));
        Assert.That(_host.LastHint, Does.Contain("Zoned roadside lot"));
    }

    [Test]
    public void Zoning_HoverOffRoad_HighlightsGridCell()
    {
        var tool = new ZoningTool(_host, InteractionMode.ZoneResidential);

        tool.OnHover(new ToolInput(CellCenter(3, 3)));

        Assert.That(_host.FakePreview.HoverZoneId, Is.EqualTo(_world.Grid.GetZoneId(3, 3)));
    }

    // ---------- BusLaneTool ----------

    [Test]
    public void BusLane_ClickRoad_TogglesLaneAndRefreshesHover()
    {
        var (a, b) = BuildStraightRoad(new Vector2(256f, 900f), new Vector2(832f, 900f));
        var tool = new BusLaneTool(_host);

        tool.OnPrimaryClick(At(544f, 902f));

        var edge = _world.RoadGraph.GetEdge(_world.RoadGraph.FindEdgeId(a.Id, b.Id));
        Assert.That(edge.HasBusLane, Is.True);
        Assert.That(_host.FakePreview.BusLaneHoverEdge, Is.Not.Null);
        Assert.That(_host.LastHint, Does.Contain("УВІМКНЕНО"));
    }

    [Test]
    public void BusLane_ClickEmptyGround_Rejected()
    {
        var tool = new BusLaneTool(_host);

        tool.OnPrimaryClick(At(544f, 300f));

        Assert.That(_host.Results.Single().Status, Is.EqualTo(CommandStatus.Rejected));
    }

    // ---------- TransitRouteTool ----------

    private (RoadNode N1, RoadNode N2, RoadNode N3) BuildTriangle()
    {
        var n1 = _world.RoadGraph.CreateNode(new Vector2(320f, 320f));
        var n2 = _world.RoadGraph.CreateNode(new Vector2(704f, 320f));
        var n3 = _world.RoadGraph.CreateNode(new Vector2(704f, 704f));
        _world.RoadGraph.AddCurvedRoadSegment(n1.Id, n2.Id, new CurveSegment(n1.WorldPosition, n2.WorldPosition));
        _world.RoadGraph.AddCurvedRoadSegment(n2.Id, n3.Id, new CurveSegment(n2.WorldPosition, n3.WorldPosition));
        _world.RoadGraph.AddCurvedRoadSegment(n3.Id, n1.Id, new CurveSegment(n3.WorldPosition, n1.WorldPosition));
        _world.ApplyTopologyChange();
        return (n1, n2, n3);
    }

    [Test]
    public void TransitRoute_ClickStops_DraftsPathAlongRoads()
    {
        var (n1, n2, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);

        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));
        tool.OnPrimaryClick(new ToolInput(n2.WorldPosition));

        Assert.That(tool.DraftStops, Is.EqualTo(new[] { n1.Id, n2.Id }));
        Assert.That(tool.DraftPath.First(), Is.EqualTo(n1.Id));
        Assert.That(tool.DraftPath.Last(), Is.EqualTo(n2.Id));
        Assert.That(_host.FakePreview.TransitDraftStops, Is.EqualTo(tool.DraftStops));
        Assert.That(_host.RouteDesignerStatus.Stops, Is.EqualTo(2));
    }

    [Test]
    public void TransitRoute_ClickOriginAgain_ClosesLoop()
    {
        var (n1, n2, n3) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));
        tool.OnPrimaryClick(new ToolInput(n2.WorldPosition));
        tool.OnPrimaryClick(new ToolInput(n3.WorldPosition));

        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));

        Assert.That(tool.DraftIsLoop, Is.True);
        Assert.That(_host.LastHint, Does.Contain("Closed-loop"));
    }

    [Test]
    public void TransitRoute_SameStopTwice_IgnoredWithHint()
    {
        var (n1, _, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));

        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));

        Assert.That(tool.DraftStops.Count, Is.EqualTo(1));
        Assert.That(_host.LastHint, Does.Contain("already the current stop"));
    }

    [Test]
    public void TransitRoute_MidRoadClick_InsertsStopNode()
    {
        var (n1, n2, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        int nodesBefore = _world.RoadGraph.Nodes.Count;

        tool.OnPrimaryClick(At(512f, 322f));

        Assert.That(_world.RoadGraph.Nodes.Count, Is.EqualTo(nodesBefore + 1));
        Assert.That(tool.DraftStops.Count, Is.EqualTo(1));
        Assert.That(tool.DraftStops[0], Is.Not.EqualTo(n1.Id).And.Not.EqualTo(n2.Id));
    }

    [Test]
    public void TransitRoute_Launch_CreatesRouteResetsDraftAndReturnsToInspect()
    {
        var (n1, n2, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));
        tool.OnPrimaryClick(new ToolInput(n2.WorldPosition));
        int routesBefore = _world.Transit.Routes.Count;

        var result = tool.Launch("Test Line", Colors.Cyan);

        Assert.That(result.IsApplied, Is.True);
        Assert.That(_world.Transit.Routes.Count, Is.EqualTo(routesBefore + 1));
        Assert.That(tool.DraftStops, Is.Empty);
        Assert.That(_host.ReturnToInspectCalls, Is.EqualTo(1));
        Assert.That(_host.TransitRoutesViewUpdates, Is.EqualTo(1));
    }

    [Test]
    public void TransitRoute_LaunchWithOneStop_RejectedAndDraftKept()
    {
        var (n1, _, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));

        var result = tool.Launch("Too short", Colors.Cyan);

        Assert.That(result.Status, Is.EqualTo(CommandStatus.Rejected));
        Assert.That(tool.DraftStops.Count, Is.EqualTo(1));
        Assert.That(_host.ReturnToInspectCalls, Is.EqualTo(0));
    }

    [Test]
    public void TransitRoute_Cancel_ClearsDraftOnlyWhenPresent()
    {
        var (n1, _, _) = BuildTriangle();
        var tool = new TransitRouteTool(_host);
        Assert.That(tool.OnCancel(), Is.False);

        tool.OnPrimaryClick(new ToolInput(n1.WorldPosition));
        Assert.That(tool.OnCancel(), Is.True);
        Assert.That(tool.DraftStops, Is.Empty);
        Assert.That(_host.FakePreview.TransitDraftStops, Is.Empty);
    }

    // ---------- InteractionMode helpers ----------

    [Test]
    public void InteractionModeExtensions_ClassifyModes()
    {
        Assert.That(InteractionMode.Dezone.IsZoningMode(), Is.True);
        Assert.That(InteractionMode.BuildRoad.IsZoningMode(), Is.False);
        Assert.That(InteractionMode.BuildCurvedRoad.IsRoadBuildMode(), Is.True);
        Assert.That(InteractionMode.ZoneIndustrial.GetZoneType(), Is.EqualTo(ZoneType.Industrial));
        Assert.That(InteractionMode.Dezone.GetZoneType(), Is.EqualTo(ZoneType.Empty));
    }
}
