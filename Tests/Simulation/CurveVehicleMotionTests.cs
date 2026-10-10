using Godot;
using NUnit.Framework;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.Tests.Simulation;

[TestFixture]
public class CurveVehicleMotionTests
{
    [Test]
    public void VehiclePositionAndRotation_AlongCubicBezierCurve_AccurateAndTangentAligned()
    {
        // Curved road arching from (0, 0) up through apex (50, 40) down to (100, 0)
        Vector2 p0 = new Vector2(0, 0);
        Vector2 apex = new Vector2(50, 40);
        Vector2 p3 = new Vector2(100, 0);

        var curve = CurveSegment.CreateFromThreePoints(p0, apex, p3);
        var edge = new RoadEdge
        {
            Id = 1,
            FromId = 10,
            ToId = 20,
            Length = curve.Length,
            FreeFlowSpeed = 50f,
            CurrentVolume = 100f,
            Curve = curve
        };

        float[] testProgress = { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };

        foreach (float t in testProgress)
        {
            // With zero lane offset, position must match curve evaluation exactly
            Vector2 expectedCenterline = curve.Evaluate(t);
            Vector2 carPos = CarTrafficManager.GetCarWorldPosition(edge, t, laneOffset: 0f);
            Assert.That(carPos.DistanceTo(expectedCenterline), Is.LessThan(0.001f));

            // Heading rotation must match tangent angle
            float expectedAngle = curve.GetTangent(t).Angle();
            float carRotation = CarTrafficManager.GetCarRotation(edge, t);
            Assert.That(carRotation, Is.EqualTo(expectedAngle).Within(0.001f));
        }

        // At midpoint t = 0.5, apex tangent is strictly horizontal Vector2(1, 0) -> angle 0
        float midRotation = CarTrafficManager.GetCarRotation(edge, 0.5f);
        Assert.That(midRotation, Is.EqualTo(0f).Within(0.01f));
    }

    [Test]
    public void LaneOffset_PerpendicularToCurve_ConsistencyAlongCurvedPaths()
    {
        Vector2 p0 = new Vector2(0, 0);
        Vector2 apex = new Vector2(60, 50);
        Vector2 p3 = new Vector2(120, 0);
        var curve = CurveSegment.CreateFromThreePoints(p0, apex, p3);

        var edge = new RoadEdge
        {
            Id = 2,
            FromId = 1,
            ToId = 2,
            Length = curve.Length,
            Curve = curve
        };

        float laneOffset = 4.5f;

        for (int i = 0; i <= 10; i++)
        {
            float t = i / 10.0f;
            Vector2 center = curve.Evaluate(t);
            Vector2 carPos = CarTrafficManager.GetCarWorldPosition(edge, t, laneOffset);

            // 1. Distance between centerline and vehicle position equals lane offset
            float dist = center.DistanceTo(carPos);
            Assert.That(dist, Is.EqualTo(laneOffset).Within(0.001f));

            // 2. Offset vector is perpendicular to curve tangent
            Vector2 tangent = curve.GetTangent(t);
            Vector2 offsetVec = (carPos - center).Normalized();
            float dot = tangent.Dot(offsetVec);
            Assert.That(Mathf.Abs(dot), Is.LessThan(0.001f), $"Offset vector must be perpendicular at t={t}");

            // 3. In right-hand traffic, offset lies to the right side of the travel vector
            // In 2D cross product: tangent.X * offset.Y - tangent.Y * offset.X > 0
            float cross = tangent.X * offsetVec.Y - tangent.Y * offsetVec.X;
            Assert.That(cross, Is.GreaterThan(0.99f), $"Offset must point to the right of travel direction at t={t}");
        }
    }

    [Test]
    public void TransitVehicle_SplineInterpolation_BetweenPathNodes()
    {
        var graph = new RoadGraph();
        var n1 = graph.CreateNode(new Vector2(0, 0));
        var n2 = graph.CreateNode(new Vector2(100, 0));

        // Create curved edge arching to Y = 60
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(50, 60), n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        var route = new TransitRoute
        {
            Id = 1,
            PathNodeIds = new List<int> { n1.Id, n2.Id },
            RouteColor = Colors.CadetBlue
        };

        var bus = new TransitVehicle
        {
            Id = 1,
            RouteId = 1,
            CurrentPathIndex = 0,
            ProgressToNext = 0.5f,
            Forward = true
        };

        // Bus position at ProgressToNext = 0.5
        Vector2 busPos = bus.GetWorldPosition(route, graph);

        // A straight line chord would have Y = 0 (plus lane offset Y = 5) -> Y ~ 5
        // A curved Bézier evaluation passes through apex Y = 60 (plus lane offset Y = 5) -> Y ~ 65
        Assert.That(busPos.X, Is.EqualTo(50f).Within(1.0f));
        Assert.That(busPos.Y, Is.GreaterThan(50f), "Bus position must follow the curved road arc, not straight chord");

        // Rotation at apex t = 0.5 is horizontal (angle 0)
        float busRotation = bus.GetRotation(route, graph);
        Assert.That(busRotation, Is.EqualTo(0f).Within(0.01f));
    }

    [Test]
    public void TransitStop_PositionedAlongOuterRoadShoulder_UsingCurveNormal()
    {
        var graph = new RoadGraph();
        var n1 = graph.CreateNode(new Vector2(0, 0));
        var n2 = graph.CreateNode(new Vector2(100, 0));

        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(50, 40), n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        var route = new TransitRoute
        {
            Id = 1,
            PathNodeIds = new List<int> { n1.Id, n2.Id },
            StopNodeIds = new HashSet<int> { n1.Id, n2.Id }
        };

        float shoulderOffset = 8.5f;
        Vector2 stopPos1 = TransitManager.GetStopWorldPosition(n1.Id, route, graph, shoulderOffset);

        // Distance from node position to stop position equals shoulderOffset
        Assert.That(n1.WorldPosition.DistanceTo(stopPos1), Is.EqualTo(shoulderOffset).Within(0.01f));

        // Stop is placed along the curve's perpendicular normal
        Vector2 normalAt0 = curve.GetNormal(0f);
        Vector2 expectedPos = n1.WorldPosition + normalAt0 * shoulderOffset;
        Assert.That(stopPos1.DistanceTo(expectedPos), Is.LessThan(0.001f));
    }

    [Test]
    public void Pedestrian_SidewalkOffsetAndTangentAlignment()
    {
        Vector2 p0 = new Vector2(10, 10);
        Vector2 apex = new Vector2(60, 80);
        Vector2 p3 = new Vector2(110, 10);
        var curve = CurveSegment.CreateFromThreePoints(p0, apex, p3);

        var edge = new RoadEdge
        {
            Id = 5,
            FromId = 1,
            ToId = 2,
            Length = curve.Length,
            Curve = curve
        };

        float sidewalkOffset = 7.5f;
        float t = 0.4f;

        Vector2 center = curve.Evaluate(t);
        Vector2 rightSidewalk = PedestrianManager.GetSidewalkPosition(edge, t, side: 1, sidewalkOffset);
        Vector2 leftSidewalk = PedestrianManager.GetSidewalkPosition(edge, t, side: -1, sidewalkOffset);

        // Right sidewalk is at sidewalkOffset
        Assert.That(center.DistanceTo(rightSidewalk), Is.EqualTo(sidewalkOffset).Within(0.001f));
        // Left sidewalk is at sidewalkOffset
        Assert.That(center.DistanceTo(leftSidewalk), Is.EqualTo(sidewalkOffset).Within(0.001f));

        // Left and right sidewalks are symmetrical across centerline
        Vector2 rightVec = rightSidewalk - center;
        Vector2 leftVec = leftSidewalk - center;
        Assert.That(rightVec.DistanceTo(-leftVec), Is.LessThan(0.001f));

        // Pedestrian rotation matches curve tangent
        float expectedAngle = curve.GetTangent(t).Angle();
        float pedRotation = PedestrianManager.GetSidewalkRotation(edge, t);
        Assert.That(pedRotation, Is.EqualTo(expectedAngle).Within(0.001f));
    }

    [Test]
    public void TrafficLight_AngleAwarePhaseDetermination_Orthogonal4WayJunction()
    {
        var graph = new RoadGraph();
        var center = graph.CreateNode(new Vector2(100, 100));
        var north = graph.CreateNode(new Vector2(100, 20));
        var south = graph.CreateNode(new Vector2(100, 180));
        var east = graph.CreateNode(new Vector2(180, 100));
        var west = graph.CreateNode(new Vector2(20, 100));

        graph.AddRoadSegment(center.Id, north.Id);
        graph.AddRoadSegment(center.Id, south.Id);
        graph.AddRoadSegment(center.Id, east.Id);
        graph.AddRoadSegment(center.Id, west.Id);

        var trafficLights = new TrafficLightManager();
        trafficLights.BuildIntersections(graph);

        Assert.That(trafficLights.Intersections.ContainsKey(center.Id), Is.True, "4-way junction must have traffic light");
        var light = trafficLights.Intersections[center.Id];

        // North and South approaches share Phase 0 (NorthSouthGreen)
        // East and West approaches share Phase 1 (EastWestGreen)
        light.CurrentState = SignalState.NorthSouthGreen;
        Assert.That(light.IsApproachGreen(north.WorldPosition), Is.True, "North approach must be green on NS phase");
        Assert.That(light.IsApproachGreen(south.WorldPosition), Is.True, "South approach must be green on NS phase");
        Assert.That(light.IsApproachGreen(east.WorldPosition), Is.False, "East approach must be red on NS phase");
        Assert.That(light.IsApproachGreen(west.WorldPosition), Is.False, "West approach must be red on NS phase");

        light.CurrentState = SignalState.EastWestGreen;
        Assert.That(light.IsApproachGreen(north.WorldPosition), Is.False, "North approach must be red on EW phase");
        Assert.That(light.IsApproachGreen(south.WorldPosition), Is.False, "South approach must be red on EW phase");
        Assert.That(light.IsApproachGreen(east.WorldPosition), Is.True, "East approach must be green on EW phase");
        Assert.That(light.IsApproachGreen(west.WorldPosition), Is.True, "West approach must be green on EW phase");
    }

    [Test]
    public void TrafficLight_AngleAwarePhaseDetermination_NonOrthogonalAndCurvedRoads()
    {
        // 3-way T/Y junction with non-orthogonal angles:
        // Main corridor through (center -> branchA at ~15°, center -> branchB at ~195° - near collinear within 45°)
        // Crossing branch (center -> branchC at ~105° - crossing by ~90°)
        var graph = new RoadGraph();
        var center = graph.CreateNode(new Vector2(0, 0));

        Vector2 dirA = new Vector2(Mathf.Cos(15f * Mathf.Pi / 180f), Mathf.Sin(15f * Mathf.Pi / 180f)) * 80f;
        Vector2 dirB = new Vector2(Mathf.Cos(195f * Mathf.Pi / 180f), Mathf.Sin(195f * Mathf.Pi / 180f)) * 80f;
        Vector2 dirC = new Vector2(Mathf.Cos(105f * Mathf.Pi / 180f), Mathf.Sin(105f * Mathf.Pi / 180f)) * 80f;

        var nodeA = graph.CreateNode(dirA);
        var nodeB = graph.CreateNode(dirB);
        var nodeC = graph.CreateNode(dirC);

        graph.AddRoadSegment(center.Id, nodeA.Id);
        graph.AddRoadSegment(center.Id, nodeB.Id);
        graph.AddRoadSegment(center.Id, nodeC.Id);

        var trafficLights = new TrafficLightManager();
        trafficLights.BuildIntersections(graph);

        Assert.That(trafficLights.Intersections.ContainsKey(center.Id), Is.True, "Non-orthogonal 3-way junction must have traffic light");
        var light = trafficLights.Intersections[center.Id];

        // Approaches A and B are collinear within ~45° and must share the same phase
        // Approach C is crossing and must alternate in the opposing phase
        bool aGreenOnState0 = light.IsApproachGreen(nodeA.WorldPosition);
        bool bGreenOnState0 = light.IsApproachGreen(nodeB.WorldPosition);
        bool cGreenOnState0 = light.IsApproachGreen(nodeC.WorldPosition);

        Assert.That(aGreenOnState0, Is.EqualTo(bGreenOnState0), "Collinear approaches A and B must share the same green phase");
        Assert.That(cGreenOnState0, Is.Not.EqualTo(aGreenOnState0), "Crossing approach C must be in the opposing phase");

        // When signal flips, states alternate
        light.CurrentState = (light.CurrentState == SignalState.NorthSouthGreen)
            ? SignalState.EastWestGreen
            : SignalState.NorthSouthGreen;

        Assert.That(light.IsApproachGreen(nodeA.WorldPosition), Is.EqualTo(!aGreenOnState0));
        Assert.That(light.IsApproachGreen(nodeB.WorldPosition), Is.EqualTo(!bGreenOnState0));
        Assert.That(light.IsApproachGreen(nodeC.WorldPosition), Is.EqualTo(!cGreenOnState0));
    }

    [Test]
    public void CarTrafficManager_DiscreteCars_UpdateAndStopAtRedLight()
    {
        var graph = new RoadGraph();
        var n1 = graph.CreateNode(new Vector2(0, 0));
        var n2 = graph.CreateNode(new Vector2(100, 0)); // Intersection node
        var n3 = graph.CreateNode(new Vector2(100, 100));
        var n4 = graph.CreateNode(new Vector2(200, 0));

        graph.AddRoadSegment(n1.Id, n2.Id);
        graph.AddRoadSegment(n2.Id, n3.Id);
        graph.AddRoadSegment(n2.Id, n4.Id);

        int incomingEdgeId = graph.FindEdgeId(n1.Id, n2.Id);
        var edge = graph.Edges[incomingEdgeId];
        edge.CurrentVolume = 200f; // Ensure demand

        var trafficLights = new TrafficLightManager();
        trafficLights.BuildIntersections(graph);
        var light = trafficLights.Intersections[n2.Id];

        // Set light such that incoming edge from n1 is RED
        // Incoming from n1 is East-West (from 0,0 to 100,0)
        light.CurrentState = SignalState.NorthSouthGreen; // E-W is red
        Assert.That(light.IsApproachGreen(n1.WorldPosition), Is.False, "Incoming approach should be red");

        var carMgr = new CarTrafficManager();
        var car = carMgr.CreateCar(incomingEdgeId, progress: 0.84f, speed: 40f);

        // Update car manager approaching red light
        carMgr.Update(0.1f, 1.0f, graph, trafficLights);
        Assert.That(car.IsStopped, Is.True, "Car must stop at red light");
        Assert.That(car.Progress, Is.EqualTo(0.84f), "Car progress must freeze at red light");

        // Turn light GREEN for incoming approach
        light.CurrentState = SignalState.EastWestGreen;
        Assert.That(light.IsApproachGreen(n1.WorldPosition), Is.True, "Incoming approach should be green");

        // Update car manager again
        carMgr.Update(0.1f, 1.0f, graph, trafficLights);
        Assert.That(car.IsStopped, Is.False, "Car should resume moving when light turns green");
        Assert.That(car.Progress, Is.GreaterThan(0.84f), "Car progress should advance");
    }

    [Test]
    public void DiscreteAgents_DirectPositionAndRotationEvaluation()
    {
        var graph = new RoadGraph();
        var n1 = graph.CreateNode(new Vector2(0, 0));
        var n2 = graph.CreateNode(new Vector2(100, 0));
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, new Vector2(50, 40), n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve);

        int edgeId = graph.FindEdgeId(n1.Id, n2.Id);

        var carMgr = new CarTrafficManager();
        var car = carMgr.CreateCar(edgeId, progress: 0.5f);
        Vector2 carPos = car.GetWorldPosition(graph);
        float carRot = car.GetRotation(graph);

        Assert.That(carPos.X, Is.EqualTo(50f).Within(0.01f));
        Assert.That(carPos.Y, Is.EqualTo(40f + CarTrafficManager.DefaultLaneOffset).Within(0.01f));
        Assert.That(carRot, Is.EqualTo(0f).Within(0.01f));

        var pedMgr = new PedestrianManager();
        var ped = pedMgr.CreatePedestrian(edgeId, progress: 0.5f, side: 1);
        Vector2 pedPos = ped.GetWorldPosition(graph);
        float pedRot = ped.GetRotation(graph);

        Assert.That(pedPos.X, Is.EqualTo(50f).Within(0.01f));
        Assert.That(pedPos.Y, Is.EqualTo(40f + PedestrianManager.DefaultSidewalkOffset).Within(0.01f));
        Assert.That(pedRot, Is.EqualTo(0f).Within(0.01f));
    }
}
