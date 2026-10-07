# CitySim — Feature Specification & Architecture Documentation

> **CitySim** is a high-fidelity urban traffic, commute, and transit simulation engine built with **Godot 4.7** and **C# (.NET 8)**, specifically engineered to simulate macroscopic and microscopic mobility patterns for metropolises of **1,000,000+ population**.

---

## Table of Contents

1. [Overview & Core Architecture](#1-overview--core-architecture)
   - [Engine & Framework](#engine--framework)
   - [Core Simulation Premise](#core-simulation-premise)
   - [Multi-Tier Architecture Pipeline](#multi-tier-architecture-pipeline)
2. [Interactive Tools & Gameplay Features](#2-interactive-tools--gameplay-features)
   - [Interactive Road Network Construction & Demolition](#interactive-road-network-construction--demolition)
   - [Interactive Zoning & District Expansion](#interactive-zoning--district-expansion)
   - [Interactive Transit Route Designer & Operations](#interactive-transit-route-designer--operations)
   - [Commute & Traffic Analytics](#commute--traffic-analytics)
3. [Controls & Navigation Guide](#3-controls--navigation-guide)
   - [Keyboard & Mouse Shortcuts](#keyboard--mouse-shortcuts)
   - [Interaction Modes Reference](#interaction-modes-reference)
4. [Automated Test Suite](#4-automated-test-suite)
   - [Test Suite Summary (37 Tests)](#test-suite-summary-37-tests)
   - [Road Construction Tests (`RoadConstructionTests.cs`)](#road-construction-tests)
   - [Zoning & District Expansion Tests (`ZoningAndExpansionTests.cs`)](#zoning--district-expansion-tests)
   - [Transit Route Designer Tests (`TransitDesignerTests.cs`)](#transit-route-designer-tests)
   - [Running the Test Suite](#running-the-test-suite)

---

## 1. Overview & Core Architecture

### Engine & Framework
- **Game Engine**: Godot Engine 4.7 (`Godot.NET.Sdk/4.7.2`)
- **Language & Runtime**: C# 12 / .NET 8 (`net8.0`)
- **Graphics Pipeline**: Godot 2D CanvasItem vector rendering (`_Draw()` batch primitives)
- **Target Platform**: Cross-platform desktop (Linux, Windows, macOS)

### Core Simulation Premise
CitySim models a living city with over 1,000,000 residents distributed across residential neighborhoods, commercial business districts, and heavy industrial hubs. Unlike basic agent-based models that bottleneck under high entity counts, CitySim combines a **macroscopic four-step travel demand model** (OD Matrix + Gravity Model + Bureau of Public Roads congestion curves) with a **microscopic visual agent layer** (traffic light intersections, car traffic, and public transit bus fleets).

### Multi-Tier Architecture Pipeline

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        CITYSIM SIMULATION PIPELINE                     │
│                                                                        │
│  [ CityGrid ] ─────────► [ RoadGraph ] ───────► [ TrafficLightManager ] │
│  Zoning & Pop           Topology & Edges         3-way/4-way signals   │
│         │                      │                         ▲             │
│         │                      ▼                         │             │
│         │              [ Dijkstra / BPR ]                │             │
│         │              Travel Time & Paths               │             │
│         ▼                      │                         │             │
│  [ ODMatrix ] ◄────────────────┘                         │             │
│  Diurnal Gravity Demand                                  │             │
│  Mode Split (Car / Transit)                              │             │
│         │                                                │             │
│         ├──────────────────────────────┐                 │             │
│         ▼                              ▼                 │             │
│  [ TrafficEngine ]            [ TransitManager ]         │             │
│  Macro Flow Assignment        Bus Routes, Stops & Fleets │             │
│         │                              │                 │             │
│         ▼                              ▼                 │             │
│  [ CarTrafficManager ]        [ Transit Vehicles ] ──────┘             │
│  Microscopic Visual Cars      Microscopic Buses                        │
│         │                              │                               │
│         └──────────────┬───────────────┘                               │
│                        ▼                                               │
│             [ Rendering & GameUI ]                                     │
│             Heatmap, Overlays, Inspector                               │
└────────────────────────────────────────────────────────────────────────┘
```

1. **Demographic & Grid Layer (`CityGrid`, `Zone`)**:
   - Represents the physical city on a $20 \times 20$ cell grid with configurable cell dimensions ($64\text{ px}$).
   - Tracks active zones, district types (`Residential`, `Commercial`, `Industrial`, `Empty`), resident population, jobs, and commercial capacity.
2. **Road Topology & Graph Layer (`RoadGraph`, `RoadNode`, `RoadEdge`)**:
   - Maintains an in-memory directed graph of nodes and bidirectional edges.
   - Computes shortest paths and full distance matrices using Dijkstra's algorithm.
   - Models travel impedance via the **Bureau of Public Roads (BPR)** formula:
     $$T = T_0 \cdot \left(1 + 0.15 \cdot \left(\frac{V}{C}\right)^4\right)$$
     where $T_0 = \frac{\text{Length}}{\text{FreeFlowSpeed}}$, $V = \text{CurrentVolume}$, and $C = \text{Capacity}$.
3. **Macro Commute Demand Layer (`ODMatrix`)**:
   - Generates realistic Origin-Destination trip matrices between residential zones and workplace destinations.
   - Gravity model impedance:
     $$\text{Impedance} = \frac{1}{(T_{ij} + 2.0)^{1.25}}$$
   - Diurnal travel demand curve with morning rush (07:00–09:00) and evening rush (16:30–18:30).
   - Directional bias shifts flow from home $\to$ work in the morning and work $\to$ home in the evening.
4. **Traffic Assignment Layer (`TrafficEngine`)**:
   - Assigns OD trips onto the shortest paths across the road network.
   - Calculates edge link volumes, volume-to-capacity (V/C) ratios, and city-wide congestion metrics.
5. **Microscopic Traffic & Signals Layer (`TrafficLightManager`, `CarTrafficManager`)**:
   - Automatically detects 3-way (T-junction) and 4-way intersections with crossing traffic.
   - Runs synchronized 2-phase green/red light cycles with staggered offsets across the grid.
   - Manages up to 160 visual private cars that respect signals, lane positions, and congested edges.
6. **Public Transit Network Layer (`TransitManager`)**:
   - Manages linear and circular transit routes, bus stop passenger queues, and vehicle fleets.
   - Calculates fare revenues, fuel/driver operating expenses, passenger boarding/alighting, and layovers.
   - Feeds transit coverage metrics back into `ODMatrix` to calculate realistic modal shifts.
7. **Rendering & HUD Layer (`Rendering/*`, `GameUI`)**:
   - High-performance 2D drawing of roads, zone tiles, traffic lights, and vehicles with right-hand driving offsets.
   - Commute overlay with animated pulsing arcs and illuminated street paths.
   - Congestion heatmap calibrated to the standard *Cities: Skylines* color palette.

---

## 2. Interactive Tools & Gameplay Features

### Interactive Road Network Construction & Demolition

Players can dynamically expand or remodel their road infrastructure in real time without pausing the simulation:

- **Two-Click Road Construction (`InteractionMode.BuildRoad`)**:
  - Click any active zone cell to set the starting intersection.
  - Interactive green outline and highlighted target markers indicate valid adjacent orthogonal neighbors (Manhattan distance $= 1$).
  - Clicking an adjacent valid neighbor establishes a bidirectional road connection with dual directed `RoadEdge` segments.
  - Automatically rejects diagonal placements, out-of-bounds clicks, and redundant duplicate edges.
- **Interactive Demolition Tool (`InteractionMode.Demolish`)**:
  - Click any intersection with existing roads to select the origin.
  - Connected neighboring cells are highlighted with red demolition targeting indicators.
  - Clicking a connected neighbor tears down the bidirectional connection.
  - Graph edge indices remain stable (marked disconnected with zero capacity) while adjacency lists are updated.
- **Real-Time Network Topology Rebuilding**:
  - Immediately triggers `RoadGraph.RebuildAfterTopologyChange()`, clearing stale path caches and re-executing Dijkstra across all zone pairs.
  - Signals `TrafficLightManager.BuildIntersections()` to recalculate intersection configurations (adding or removing traffic lights).
  - Triggers `CarTrafficManager.HandleInvalidatedEdges()`, safely respawning any visual vehicles currently traversing demolished road segments to prevent crashes or orphaned entities.
  - Refreshes macro traffic assignments via `ODMatrix.Recalculate()` and `TrafficEngine.AssignFlows()`.

---

### Interactive Zoning & District Expansion

Urban planning tools allow players to zone land and expand their city outward into empty terrain:

- **Four Dedicated Zoning Modes**:
  - **🏡 Residential (`InteractionMode.ZoneResidential`)**:
    - Designates land as residential housing (emerald green styling).
    - Automatically populates with **7,000 residents** per cell.
    - Serves as trip origins during morning commute and destinations during evening return.
  - **🏢 Commercial (`InteractionMode.ZoneCommercial`)**:
    - Designates high-density commercial office and retail space (sky blue styling).
    - Creates **4,500 jobs** and **3,000 commercial capacity** per cell.
    - High-capacity arterial road connectivity ($C = 2,000\text{ vehicles/hr}$).
  - **🏭 Industrial (`InteractionMode.ZoneIndustrial`)**:
    - Designates manufacturing and logistics factories (industrial amber styling).
    - Creates **3,800 jobs** per cell.
    - High-capacity road connectivity ($C = 2,000\text{ vehicles/hr}$).
  - **🧹 Dezone / Clear (`InteractionMode.Dezone`)**:
    - Clears existing zoned cells back to empty terrain.
    - Automatically resets population, jobs, and commercial capacity to 0.
- **Seamless Graph Node Management**:
  - **`RoadGraph.EnsureNode(zoneId, worldPos)`**: Allocates a new node in the graph when zoning empty land so roads can be built immediately to connect it.
  - **`RoadGraph.DetachAndRemoveNode(zoneId)`**: When dezoning, all incoming and outgoing road edges are safely severed, path caches are purged, and the node is removed from the active graph.
- **Dynamic Demand & Demographic Recalculation**:
  - Updates total population and job counters in real time.
  - Recomputes gravity model attraction scores and re-assigns travel flows across the road network.

---

### Interactive Transit Route Designer & Operations

A full-fledged public transit system allowing players to design bus routes, manage vehicle fleets, and optimize municipal mobility:

```text
┌────────────────────────────────────────────────────────┐
│              TRANSIT ROUTE DESIGNER TOOLBAR            │
│  [ Line 4 - Express ] [ 🟣 Purple ▾ ]                  │
│  Stops: 5 | Nodes: 14 | Mode: Closed Loop 🔄           │
│  [ ▶ Запустити кільце ] [ ✕ Скасувати ]               │
└────────────────────────────────────────────────────────┘
```

- **In-Game Route Creation Tool (`InteractionMode.CreateTransitRoute`)**:
  - **Origin Designation**: Click any road intersection to establish the route terminus/origin stop.
  - **Intelligent Sequential Path Tracing**: Click subsequent road intersections; the engine automatically traces the shortest road path using `RoadGraph.GetShortestNodePath(lastStop, nextStop)` and adds all intermediate road nodes to the path.
  - **Closed-Loop Auto-Detection**: Clicking the origin stop when at least 2 stops are placed closes the loop (`IsLoop = true`), tracing the return segment and enabling continuous circular vehicle operations.
  - **Visual Draft Preview**: Real-time dashed preview path colored with the selected palette color, complete with numbered stop pins and loop indicators.
  - **Customizable Color Palette**: Choose from Purple, Orange, Cyan, Magenta, Amber, or Lime.
- **Transit Stops & Queue Management (`TransitStop`)**:
  - Designated stations where passengers assemble to wait for buses.
  - Dynamic passenger generation feeding waiting queues based on nearby residential density.
- **Realistic Vehicle Fleet Simulation (`TransitVehicle`)**:
  - **Even Fleet Spacing**: Fleet vehicles are automatically deployed with uniform headway spacing along linear or circular routes (`DeployLineVehicles` / `DeployLoopVehicles`).
  - **Three Operational States**:
    - `Moving`: Vehicles advance along road segments at realistic speeds, offset into the right-hand traffic lane.
    - `AtStop`: Dwells at designated stops for $2.5\text{ seconds}$; $20\%\text{ to }40\%$ of passengers alight, and waiting stop passengers board up to the vehicle capacity ($75\text{ passengers}$).
    - `TerminusLayover`: On linear routes, reaching the end of the line forces $100\%$ passenger disembarkation, a $4.0\text{ second}$ layover dwell, and an automatic reverse direction trip.
  - **Signal Compliance**: Buses monitor traffic lights and come to a stop at red lights before entering intersections.
- **Route Economics & Fleet Sizing**:
  - **Dynamic Fleet Sizing**: Increase or decrease fleet size per route, instantly redeploying vehicles with balanced headway.
  - **Fare Pricing**: Set ticket fares (default $12\text{ ₴/\$}$ per passenger trip).
  - **Real-Time Financial Turnover**:
    - Daily Ridership: Tracks total passengers transported.
    - Daily Revenue: $\text{DailyRevenue} = \text{Passengers} \times \text{TicketPrice}$.
    - Daily Operating Cost: Simulates continuous fuel, driver salaries, and bus maintenance costs ($\approx 6.5\text{ ₴/\$ per game minute}$).
    - Net Daily Profit: $\text{NetProfit} = \text{DailyRevenue} - \text{DailyOperatingCost}$.
- **Dynamic Municipal Transit Coverage & Modal Split Shifts**:
  - **Transit Catchment Area**: Computes the set of zones within walking distance ($\le 3$ grid steps) of any active transit stop.
  - **City Coverage Metric**: Calculates the exact percentage of residential zones covered by public transit.
  - **Modal Split Shift in OD Matrix**:
    - When both origin and destination zones have transit access, **$40\%$ of commuters take public transit** and only $60\%$ drive personal cars.
    - If only one end is covered, $15\%$ take transit.
    - If neither is covered, $100\%$ drive personal cars.
    - Expanding transit routes directly removes thousands of cars from congested roadways!

---

### Commute & Traffic Analytics

Comprehensive visual analytics give immediate insight into municipal transportation health:

- **Zone Inspector & Analytics Panel**:
  - Click any active zone in `Inspect` mode to view:
    - Demographic breakdown (Residents, Jobs, Commercial Capacity).
    - Travel demand and outbound/inbound trip volume.
    - Commuter modal split (e.g. $60\%$ Car, $40\%$ Public Transit).
    - Detailed destination ranking: lists top workplaces or worker neighborhoods with exact percentages and trip counts.
- **Commute Overlay Renderer (`CommuteOverlayRenderer`)**:
  - Illuminates the exact street paths used by commuters traveling to or from the inspected zone.
  - Animated pulsing arcs connect origins to destinations, colored by destination type (Blue for Commercial, Amber for Industrial, Green for Residential).
- **Traffic Congestion Heatmap (`RoadRenderer`)**:
  - Toggleable city-wide heatmap overlaying road segments.
  - Uses the industry-standard *Cities: Skylines* color scale based on volume-to-capacity ($V/C$) ratio:
    - 🟢 **Green** ($V/C < 0.50$): Free-flow traffic.
    - 🟡 **Yellow-Orange** ($0.50 \le V/C < 0.85$): Moderate volume.
    - 🟠 **Orange-Red** ($0.85 \le V/C < 1.00$): Near capacity.
    - 🔴 **Crimson Red** ($V/C \ge 1.00$): Severe bottleneck and gridlock.
- **24-Hour Diurnal Rush-Hour Simulation**:
  - Time progresses smoothly with day/hour rollover.
  - Morning rush peak at 07:00–09:00 with $90\%$ directional flow towards commercial and industrial zones.
  - Evening rush peak at 16:30–18:30 with $90\%$ directional flow returning back to residential suburbs.
  - Lunchtime mini-peak at 12:00–13:00.
  - Quiet night periods (00:00–05:00) with minimal travel demand ($< 1.5\%$).

---

## 3. Controls & Navigation Guide

### Keyboard & Mouse Shortcuts

| Input | Action | Details |
| :--- | :--- | :--- |
| **`W` / `↑`** | Pan Camera Up | Smooth camera movement scaled inversely with zoom level |
| **`S` / `↓`** | Pan Camera Down | Smooth camera movement |
| **`A` / `←`** | Pan Camera Left | Smooth camera movement |
| **`D` / `→`** | Pan Camera Right | Smooth camera movement |
| **Mouse Wheel Up** | Zoom In | Smooth zoom clamped between $0.3\times$ and $4.0\times$ |
| **Mouse Wheel Down** | Zoom Out | Smooth zoom clamped between $0.3\times$ and $4.0\times$ |
| **Middle Mouse Drag** | Pan Viewport | Drag to pan the camera across the city |
| **Left Mouse Click** | Primary Action | Dependent on active interaction mode (see table below) |
| **Right Mouse Click** | Cancel / Deselect | Clears pending road build/demolish, resets transit draft, or deselects inspected zone |

### Interaction Modes Reference

| Mode | HUD Button | Left-Click Action |
| :--- | :--- | :--- |
| **Inspect** | `🔍 Inspect` | Click any zoned cell to open detailed commute infographics and illuminated travel paths. |
| **Build Road** | `🛣️ Road` | Click first cell to select start, then click an adjacent orthogonal cell to build a road. |
| **Demolish** | `💥 Demolish` | Click an intersection, then click an adjacent connected intersection to demolish the road. |
| **Zone Residential** | `🏡 Res` | Click any empty or existing cell to designate a Residential district ($7,000$ pop). |
| **Zone Commercial** | `🏢 Com` | Click any empty or existing cell to designate a Commercial district ($4,500$ jobs). |
| **Zone Industrial** | `🏭 Ind` | Click any empty or existing cell to designate an Industrial district ($3,800$ jobs). |
| **Dezone** | `🧹 Dezone` | Click any active cell to clear it back to empty terrain and safely sever road connections. |
| **New Route** | `🚌 New Route` | Click road intersections in sequence to designate bus stops; click origin to close loop; launch route from toolbar. |

---

## 4. Automated Test Suite

CitySim includes a comprehensive automated test suite consisting of **37 unit and integration tests** built with **NUnit 3** running on .NET 8.

```text
Passed!  - Failed: 0, Passed: 37, Skipped: 0, Total: 37, Duration: 76 ms
```

### Test Suite Summary (37 Tests)

| Test File | Test Count | Focus Area |
| :--- | :---: | :--- |
| [`RoadConstructionTests.cs`](file:///home/user/CitySim/Tests/RoadConstructionTests.cs) | **12** | Road segment creation, deletion, Dijkstra matrix recalculation, Manhattan adjacency, traffic signals, vehicle edge invalidation |
| [`ZoningAndExpansionTests.cs`](file:///home/user/CitySim/Tests/ZoningAndExpansionTests.cs) | **14** | Residential/Commercial/Industrial zoning, dezoning, demographic metrics, node allocation/detachment, gravity model OD recalculation |
| [`TransitDesignerTests.cs`](file:///home/user/CitySim/Tests/TransitDesignerTests.cs) | **11** | Shortest-path node tracing, linear/loop route creation, fleet deployment, passenger boarding/dwells, route deletion, transit coverage OD mode split |

---

### Road Construction Tests
Located at [`Tests/RoadConstructionTests.cs`](file:///home/user/CitySim/Tests/RoadConstructionTests.cs):

1. `AddRoadSegment_ValidNodes_CreatesBidirectionalEdges`: Verifies that adding a road segment between valid nodes creates two directed edges with matching lengths and capacities.
2. `AddRoadSegment_Duplicate_ReturnsFalse`: Ensures duplicate road segments between already connected nodes are prevented.
3. `AddRoadSegment_UnknownNode_ReturnsFalse`: Verifies safe failure when attempting to build a road to a non-existent node ID.
4. `RemoveRoadSegment_ExistingConnection_RemovesBothDirections`: Verifies that removing a road segment cleans up both directions in adjacency lists and invalidates affected path caches.
5. `RemoveRoadSegment_NonExistentConnection_ReturnsFalse`: Verifies safe return value when demolishing a non-existent connection.
6. `RemoveRoadSegment_UnknownNodes_ReturnsFalseSafely`: Ensures demolishing between unknown node IDs handles safely.
7. `RebuildAfterTopologyChange_RecalculatesDistanceAndPathCache`: Validates that adding a road immediately updates the Dijkstra distance matrix and path cache.
8. `TrafficLightManager_BuildIntersections_UpdatesOnTopologyChange`: Verifies traffic lights are dynamically created at 3-way/4-way junctions and removed when intersections drop below 3 connections.
9. `CarTrafficManager_HandleInvalidatedEdges_SafelyRespawnsVehicles`: Confirms that visual cars on demolished road edges are safely respawned without throwing exceptions or corrupting simulation state.
10. `CarTrafficManager_AllEdgesDemolished_DoesNotThrow`: Verifies car manager stability when all road connections are demolished.
11. `DemolishAndReconstruct_RestoresConnectivityProperly`: Tests a full demolish-then-rebuild lifecycle, ensuring distances and travel times are cleanly restored.
12. `ManhattanDistance_AdjacentCells_IsOne`: Tests Manhattan distance math for orthogonal cell adjacency validation.

---

### Zoning & District Expansion Tests
Located at [`Tests/ZoningAndExpansionTests.cs`](file:///home/user/CitySim/Tests/ZoningAndExpansionTests.cs):

1. `ZoneCell_EmptyToResidential_UpdatesStateAndDemographics`: Verifies residential zoning populates 7,000 residents and updates city population.
2. `ZoneCell_EmptyToCommercial_UpdatesJobsAndCapacities`: Verifies commercial zoning sets 4,500 jobs and 3,000 commercial capacity.
3. `ZoneCell_EmptyToIndustrial_UpdatesJobs`: Verifies industrial zoning sets 3,800 manufacturing jobs.
4. `DezoneCell_ActiveZone_ResetsStateAndMetrics`: Ensures clearing a cell resets type to `Empty` and zeroes out population and jobs.
5. `Rezone_ResidentialToCommercial_UpdatesMetricsAccurately`: Tests rezoning a residential cell to commercial, ensuring population clears and jobs populate.
6. `RoadGraph_EnsureNode_CreatesNodeForNewZone`: Verifies that newly zoned cells receive an allocated node in the road graph.
7. `RoadGraph_DetachAndRemoveNode_RemovesAllConnectedRoadsAndNode`: Verifies that dezoning an active cell severs all connected roads, cleans path caches, and deletes the node.
8. `RoadGraph_DetachIsolatedNode_ReturnsTrueAndRemovesNode`: Confirms safe detachment of isolated nodes with zero road connections.
9. `ODMatrix_Recalculate_WithNewlyZonedResidentialAndCommercial_GeneratesCommuteTrips`: Validates gravity model trip generation between newly expanded residential and commercial districts.
10. `ODMatrix_Recalculate_AfterDezoning_CleansUpTrips`: Verifies that trips originating from or heading to a dezoned cell are completely cleared from the OD matrix.
11. `TrafficEngine_AssignFlows_AfterExpansion_PopulatesVolumes`: Verifies macro traffic volume assignment across road segments following district expansion.
12. `CarTrafficManager_HandlesDetachedDezonedEdgesSafely`: Confirms visual car manager safely handles dezoned and severed road edges.
13. `InteractionMode_ZoningModes_EnumAndToolPreview`: Verifies zoning interaction mode enum values and helper methods.
14. `Zoning_BoundsChecking_SafeHandling`: Tests boundary and negative coordinate handling for zoning operations.
15. `Rezone_ConnectedCell_UpdatesODTripsDirectionAndDemand`: Verifies that rezoning a connected cell dynamically reverses commute trip directions in the OD matrix.

---

### Transit Route Designer Tests
Located at [`Tests/TransitDesignerTests.cs`](file:///home/user/CitySim/Tests/TransitDesignerTests.cs):

1. `RoadGraph_GetShortestNodePath_ResolvesSequentialNodes`: Verifies that `GetShortestNodePath` returns the complete ordered list of node IDs along the shortest route between two intersections.
2. `RoadGraph_GetShortestNodePath_SameNode_ReturnsSingleNodeList`: Verifies shortest path query between identical nodes returns a single-element list.
3. `RoadGraph_GetShortestNodePath_DisconnectedNodes_ReturnsNull`: Ensures shortest path returns `null` safely when nodes are disconnected.
4. `CreateRoute_LinearRoute_RegistersStopsAndDeploysVehicles`: Tests creating a linear transit route, verifying stop registration, vehicle count, and path node initialization.
5. `CreateRoute_ClosedLoop_SetsIsLoopTrueAndSpacesVehiclesEvenly`: Tests circular route creation, ensuring `IsLoop = true` and circular vehicle spacing.
6. `TransitManager_Update_AdvancesVehiclesAndBoardsPassengers`: Verifies that running transit simulation ticks moves vehicles, dwells at stops, and boards waiting passengers.
7. `RemoveRoute_DeallocatesVehicles_AndCleansOrphanedStopsWhilePreservingSharedStops`: Verifies that deleting a route removes its vehicle fleet, cleans up orphaned stops, and preserves stops shared with remaining routes.
8. `Route_FleetSizeAndTicketPrice_CanBeAdjusted`: Verifies dynamic adjustment of fleet size and ticket fare pricing.
9. `DynamicTransitCoverage_ExpandsCoverageAndShiftsODModeSplit`: Tests municipal transit catchment area calculation and verifies that opening a new transit route shifts commuter trips from car to transit in the OD matrix.
10. `GameUI_CreateTransitRoute_PaletteAndInteractionModeDefined`: Verifies UI route designer palette definitions and mode configurations.

---

### Running the Test Suite

Execute the entire test suite via the .NET CLI:

```bash
# Run all tests
dotnet test Tests/Tests.csproj

# Run tests with detailed console output
dotnet test Tests/Tests.csproj --logger "console;verbosity=normal"

# Run a specific test suite
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TransitDesignerTests"
```
