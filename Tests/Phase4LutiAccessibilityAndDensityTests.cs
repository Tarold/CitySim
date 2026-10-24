using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests;

[TestFixture]
public class Phase4LutiAccessibilityAndDensityTests
{
    private static int AddRoadBetween(RoadGraph graph, RoadNode n1, RoadNode n2, float capacity = 1200f)
    {
        int edgeIdxBefore = graph.Edges.Count;
        Vector2 apex = (n1.WorldPosition + n2.WorldPosition) * 0.5f + new Vector2(0f, 8f);
        var curve = CurveSegment.CreateFromThreePoints(n1.WorldPosition, apex, n2.WorldPosition);
        graph.AddCurvedRoadSegment(n1.Id, n2.Id, curve, capacity);
        return edgeIdxBefore;
    }

    [Test]
    public void HansenAccessibility_ComputesFromRowScoreSums_AndNormalizesToZeroTwoRange()
    {
        var grid = new CityGrid(20, 20, 64f);
        var roadGraph = new RoadGraph();
        var parcelManager = new ParcelManager();
        parcelManager.Initialize(roadGraph);
        var transitManager = new TransitManager();
        var tazManager = new TazManager { CellSize = 256f };
        var odMatrix = new OptimizedODMatrix();

        // Build a corridor of 4 nodes: N0 (hub) -- N1 (mid) -- N2 (distant) -- N3 (far periphery)
        var n0 = roadGraph.CreateNode(new Vector2(100f, 100f));
        var n1 = roadGraph.CreateNode(new Vector2(360f, 100f));
        var n2 = roadGraph.CreateNode(new Vector2(900f, 100f));
        var n3 = roadGraph.CreateNode(new Vector2(1700f, 100f));

        int edge0Id = AddRoadBetween(roadGraph, n0, n1, capacity: 2500f);
        int edge1Id = AddRoadBetween(roadGraph, n1, n2, capacity: 1200f);
        int edge2Id = AddRoadBetween(roadGraph, n2, n3, capacity: 1200f);

        parcelManager.RefreshParcels(roadGraph);

        var edge0Parcels = parcelManager.Parcels.Where(p => p.EdgeId == edge0Id || p.EdgeId == edge0Id + 1).ToList();
        var edge1Parcels = parcelManager.Parcels.Where(p => p.EdgeId == edge1Id || p.EdgeId == edge1Id + 1).ToList();
        var edge2Parcels = parcelManager.Parcels.Where(p => p.EdgeId == edge2Id || p.EdgeId == edge2Id + 1).ToList();

        Assert.That(edge0Parcels.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(edge1Parcels.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(edge2Parcels.Count, Is.GreaterThanOrEqualTo(1));

        // Place major commercial employment center near N0/N1 and residential districts along corridor
        var comParcel = edge0Parcels[0];
        comParcel.SetZone(ZoneType.Commercial, jobs: 300, commercialCap: 600);
        comParcel.AccessNodeId = n0.Id;

        var resHub = edge0Parcels[1];
        resHub.SetZone(ZoneType.Residential, population: 200, residentialCap: 250);
        resHub.AccessNodeId = n0.Id;

        var resMid = edge1Parcels[0];
        resMid.SetZone(ZoneType.Residential, population: 200, residentialCap: 250);
        resMid.AccessNodeId = n1.Id;

        var resFar = edge2Parcels[0];
        resFar.SetZone(ZoneType.Residential, population: 200, residentialCap: 250);
        resFar.AccessNodeId = n3.Id;

        // Add transit route serving hub & mid (N0 <-> N1)
        transitManager.RegisterStop(n0.Id, -1, "Hub Station");
        transitManager.RegisterStop(n1.Id, -1, "Mid Station");
        var stops = new List<int> { n0.Id, n1.Id };
        transitManager.CreateRoute("Metro Express", stops, stops, Colors.DodgerBlue, isLoop: false, fleetSize: 6, ticketPrice: 10f);

        tazManager.Rebuild(grid, parcelManager, roadGraph, transitManager);
        odMatrix.Recalculate(tazManager, roadGraph, currentHour: 8f);

        Assert.That(tazManager.ActiveZones.Count, Is.GreaterThanOrEqualTo(2));

        foreach (var taz in tazManager.ActiveZones)
        {
            Assert.That(taz.AccessibilityScore, Is.InRange(0.0f, 2.0f),
                $"TAZ #{taz.Id} AccessibilityScore must be normalized to [0.0, 2.0]");
            Assert.That(taz.RawAccessibility, Is.GreaterThanOrEqualTo(0f));
        }

        // Hub parcel near major jobs and transit must have strictly higher accessibility than far periphery parcel
        Assert.That(resHub.AccessibilityScore, Is.GreaterThan(resFar.AccessibilityScore),
            "District near transit & employment hub must have higher Hansen AccessibilityScore than distant periphery");
    }

    [Test]
    public void DensityEvolution_HighAccessibilityDistrict_GrowsFasterAndUpgradesLowToMediumToHigh()
    {
        var roadGraph = new RoadGraph();
        var parcelManager = new ParcelManager();
        parcelManager.Initialize(roadGraph);

        var n0 = roadGraph.CreateNode(new Vector2(100f, 100f));
        var n1 = roadGraph.CreateNode(new Vector2(300f, 100f));
        var n2 = roadGraph.CreateNode(new Vector2(800f, 100f));
        int edge0Id = AddRoadBetween(roadGraph, n0, n1, capacity: 2500f); // High-accessibility transit arterial
        int edge1Id = AddRoadBetween(roadGraph, n1, n2, capacity: 1200f); // Low-accessibility periphery

        parcelManager.RefreshParcels(roadGraph);

        var highAccParcel = parcelManager.Parcels.First(p => p.EdgeId == edge0Id || p.EdgeId == edge0Id + 1);
        highAccParcel.SetZone(ZoneType.Residential, population: 70, residentialCap: 100);
        highAccParcel.SetDensity(DensityLevel.Low);
        int baseLowCap = highAccParcel.ResidentialCap;
        highAccParcel.Population = Mathf.RoundToInt(baseLowCap * 0.70f);
        highAccParcel.AccessibilityScore = 1.60f;
        highAccParcel.LocalCongestion = 0.40f;

        var lowAccParcel = parcelManager.Parcels.First(p => p.EdgeId == edge1Id || p.EdgeId == edge1Id + 1);
        lowAccParcel.SetZone(ZoneType.Residential, population: 70, residentialCap: 100);
        lowAccParcel.SetDensity(DensityLevel.Low);
        lowAccParcel.Population = Mathf.RoundToInt(lowAccParcel.ResidentialCap * 0.70f);
        lowAccParcel.AccessibilityScore = 0.45f;
        lowAccParcel.LocalCongestion = 0.40f;

        int initialHighPop = highAccParcel.Population;
        int initialLowPop = lowAccParcel.Population;

        // Tick 1: High accessibility parcel grows faster than low accessibility parcel
        parcelManager.ProcessLutiEvolution(roadGraph, tazManager: null, reachableEntranceNodes: null);

        int highDelta = highAccParcel.Population - initialHighPop;
        int lowDelta = lowAccParcel.Population - initialLowPop;
        Assert.That(highDelta, Is.GreaterThan(lowDelta),
            "High accessibility district must accumulate population faster than low accessibility district");

        // Simulate ticks until high-accessibility parcel upgrades Low -> Medium -> High
        for (int tick = 0; tick < 20; tick++)
        {
            parcelManager.ProcessLutiEvolution(roadGraph, tazManager: null, reachableEntranceNodes: null);
        }

        Assert.That(highAccParcel.Density, Is.EqualTo(DensityLevel.High),
            "High accessibility parcel (AccessibilityScore >= 1.25) should automatically upgrade Low -> Medium -> High");
        Assert.That(highAccParcel.ResidentialCap, Is.EqualTo(baseLowCap * 4),
            "High density parcel should have 4x base residential capacity");
        Assert.That(lowAccParcel.Density, Is.EqualTo(DensityLevel.Low),
            "Low accessibility parcel (AccessibilityScore < 0.85) should remain at Low density");
    }

    [Test]
    public void CongestionStagnation_WhenCongestionExceeds1Point5_HaltsGrowthAndReducesTaxAttractiveness()
    {
        var grid = new CityGrid(16, 16, 64f);
        var roadGraph = new RoadGraph();
        var parcelManager = new ParcelManager();
        parcelManager.Initialize(roadGraph);
        var transitManager = new TransitManager();
        var economyManager = new EconomyManager();

        var n0 = roadGraph.CreateNode(new Vector2(100f, 100f));
        var n1 = roadGraph.CreateNode(new Vector2(360f, 100f));
        int edgeId = AddRoadBetween(roadGraph, n0, n1, capacity: 1000f);

        parcelManager.RefreshParcels(roadGraph);
        var edgeParcels = parcelManager.Parcels.Where(p => p.EdgeId == edgeId || p.EdgeId == edgeId + 1).ToList();
        Assert.That(edgeParcels.Count, Is.GreaterThanOrEqualTo(2));

        var resParcel = edgeParcels[0];
        resParcel.SetZone(ZoneType.Residential, population: 92, residentialCap: 100);
        resParcel.SetDensity(DensityLevel.Low);
        resParcel.Population = Mathf.RoundToInt(resParcel.ResidentialCap * 0.92f);
        resParcel.AccessibilityScore = 1.50f;

        var comParcel = edgeParcels[1];
        comParcel.SetZone(ZoneType.Commercial, jobs: 80, commercialCap: 100);
        comParcel.SetDensity(DensityLevel.Low);
        comParcel.AccessibilityScore = 1.50f;

        // 1. Measure baseline tax revenue under free-flow conditions (R_congestion < 1.5)
        foreach (var e in roadGraph.Edges)
        {
            e.CurrentVolume = Mathf.RoundToInt(e.Capacity * 0.5f); // R = 0.5
        }
        economyManager.ProcessFinancialTick(grid, roadGraph, transitManager, parcelManager);
        float freeFlowTaxRevenue = economyManager.LastTaxRevenue;
        Assert.That(economyManager.LastCongestionTaxLoss, Is.EqualTo(0f).Within(0.001f));

        // 2. Induce severe gridlock on the road (R_congestion = 2.0 > 1.5)
        foreach (var e in roadGraph.Edges)
        {
            e.CurrentVolume = Mathf.RoundToInt(e.Capacity * 2.0f); // R = 2.0 > 1.5
        }
        int popBeforeTick = resParcel.Population;
        DensityLevel densityBeforeTick = resParcel.Density;

        var (popChanged, densityUpgraded) = parcelManager.ProcessLutiEvolution(roadGraph, tazManager: null, reachableEntranceNodes: null);

        Assert.That(popChanged, Is.False, "Gridlocked parcel (R > 1.5) must not grow population");
        Assert.That(densityUpgraded, Is.False, "Gridlocked parcel (R > 1.5) must not upgrade density");
        Assert.That(resParcel.IsStagnant, Is.True, "Parcel on road with R_congestion > 1.5 must be marked IsStagnant");
        Assert.That(resParcel.Population, Is.EqualTo(popBeforeTick), "Stagnant parcel population must not increase");
        Assert.That(resParcel.Density, Is.EqualTo(densityBeforeTick), "Stagnant parcel density must not upgrade");

        // 3. Verify tax attractiveness loss in EconomyManager when R_congestion > 1.5
        economyManager.ProcessFinancialTick(grid, roadGraph, transitManager, parcelManager);
        float congestedTaxRevenue = economyManager.LastTaxRevenue;

        Assert.That(congestedTaxRevenue, Is.LessThan(freeFlowTaxRevenue),
            "Districts gridlocked in traffic (R > 1.5) must lose tax attractiveness and generate lower tax revenue");
        Assert.That(economyManager.LastCongestionTaxLoss, Is.GreaterThan(0f),
            "EconomyManager should track tax revenue lost due to severe traffic congestion");
    }
}
