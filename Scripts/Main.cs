using Godot;
using System.Collections.Generic;
using System.Linq;
using CitySim.Simulation;
using CitySim.Rendering;
using CitySim.UI;

namespace CitySim;

public partial class Main : Node2D
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;
    private float[,] _distanceMatrix;
    private float[,] _walkingDistanceMatrix;
    private ODMatrix _odMatrix;
    private TrafficEngine _trafficEngine;
    private TransitManager _transitManager;
    private TrafficLightManager _trafficLights;
    private CarTrafficManager _carTrafficManager;
    private PedestrianManager _pedestrianManager;
    private ParcelManager _parcelManager;
    
    private CityRenderer _cityRenderer;
    private RoadRenderer _roadRenderer;
    private VehicleRenderer _vehicleRenderer;
    private CommuteOverlayRenderer _commuteOverlay;
    private ToolPreviewRenderer _previewRenderer;
    private Camera2D _camera;
    private GameUI _gameUI;
    
    private float _gameHour = 6.5f; // Start at 06:30 AM before the morning peak
    private int _gameDay = 1;
    private float _gameSpeed = 1.0f;
    private float _lastODRecalcHour = -10f;
    private bool _heatmapEnabled = false;
    private float _odRecalcInterval = 0.5f; // Recalculate OD every 30 game minutes
    private float _lastFinancialTickHour = 6.5f;
    private EconomyManager _economyManager;
    
    private bool _isDragging = false;
    private bool _isZoningDragging = false;
    private int _selectedZoneId = -1;
    private int _selectedParcelId = -1;
    private InteractionMode _currentMode = InteractionMode.Inspect;
    private int _pendingStartZoneId = -1;

    // Vector Road Construction state
    private int _roadBuildStep = 0;
    private RoadNode _roadStartNode;
    private Vector2 _roadStartPoint;
    private RoadNode _roadEndNode;
    private Vector2 _roadEndPoint;
    private RoadSnapResult _pendingEndSnap;
    private RoadSnapResult _currentCursorSnap;

    // Transit Route Designer draft state
    private List<int> _draftTransitStops = new List<int>();
    private List<int> _draftTransitPath = new List<int>();
    private bool _draftIsLoop = false;
    
    public static bool LoadTutorialMode { get; set; } = true;

    public override void _Ready()
    {
        _grid = new CityGrid(20, 20, 64f);
        _roadGraph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _transitManager = new TransitManager();
        _economyManager = new EconomyManager();
        _parcelManager.Initialize(_roadGraph);
        
        if (LoadTutorialMode)
        {
            TutorialCityGenerator.Generate(_grid, _roadGraph, _parcelManager, _transitManager, _economyManager);
        }
        else
        {
            _roadGraph.BuildFromGrid(_grid);
            _transitManager.CreateDefaultRoutes(_grid, _roadGraph);
            _parcelManager.RefreshParcels(_roadGraph);
        }
        
        _distanceMatrix = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);
        
        _odMatrix = new ODMatrix(_grid.ZoneCount);
        _trafficEngine = new TrafficEngine();
        
        _trafficLights = new TrafficLightManager();
        _trafficLights.BuildIntersections(_roadGraph);
        
        _carTrafficManager = new CarTrafficManager();
        _carTrafficManager.Initialize(_roadGraph, _parcelManager);
        
        _pedestrianManager = new PedestrianManager();
        _pedestrianManager.Initialize(_roadGraph, _grid, _transitManager);

        _camera = new Camera2D();
        _camera.Position = new Vector2(672f, 672f);
        _camera.Zoom = new Vector2(0.85f, 0.85f);
        AddChild(_camera);
        
        _cityRenderer = new CityRenderer();
        AddChild(_cityRenderer);
        _cityRenderer.Initialize(_grid, _parcelManager);
        
        _roadRenderer = new RoadRenderer();
        AddChild(_roadRenderer);
        _roadRenderer.Initialize(_roadGraph, _trafficLights);

        _commuteOverlay = new CommuteOverlayRenderer();
        AddChild(_commuteOverlay);
        _commuteOverlay.Initialize(_grid, _odMatrix, _roadGraph, _parcelManager);
        
        _vehicleRenderer = new VehicleRenderer();
        AddChild(_vehicleRenderer);
        _vehicleRenderer.Initialize(_transitManager, _carTrafficManager, _pedestrianManager, _roadGraph, _parcelManager);

        _previewRenderer = new ToolPreviewRenderer();
        AddChild(_previewRenderer);
        _previewRenderer.Initialize(_grid, _roadGraph);
        _previewRenderer.SetParcelManager(_parcelManager);
        
        _gameUI = new GameUI();
        AddChild(_gameUI);
        _gameUI.SetTransitManager(_transitManager);
        _gameUI.SetEconomyManager(_economyManager);
        
        _gameUI.SpeedChanged += (speed) => _gameSpeed = speed;
        _gameUI.HeatmapToggled += (enabled) => 
        {
            _heatmapEnabled = enabled;
            _roadRenderer.HeatmapEnabled = enabled;
            _roadRenderer.Refresh();
        };

        _gameUI.CommuteInfographicsToggled += (enabled) =>
        {
            _commuteOverlay.Visible = enabled;
        };

        _gameUI.TransitRoutesToggled += (enabled) =>
        {
            _vehicleRenderer.ShowTransitRoutes = enabled;
            _vehicleRenderer.QueueRedraw();
        };

        _gameUI.ModeChanged += OnInteractionModeChanged;
        _gameUI.RouteLaunchRequested += OnRouteLaunchRequested;
        _gameUI.RouteCancelRequested += OnRouteCancelRequested;
        _gameUI.RouteDeleted += OnRouteDeleted;
        
        RecalculateODMatrix();
        _gameUI.ShowCityOverview(_grid, _odMatrix, _parcelManager);
    }

    private void OnInteractionModeChanged(int modeInt)
    {
        _currentMode = (InteractionMode)modeInt;
        CancelPendingOperation();
        _previewRenderer.SetMode(_currentMode);

        if (_currentMode != InteractionMode.Inspect)
        {
            ClearInspectSelection();
            UpdateHoverTarget();
        }
        else
        {
            _previewRenderer.SetHoverZone(-1);
            _gameUI.SetToolHint("🔍 [Inspect] Click a zone on the map to view commute analytics.", new Color(0.4f, 0.8f, 1f));
        }
    }

    private void ClearInspectSelection()
    {
        _selectedZoneId = -1;
        _selectedParcelId = -1;
        _cityRenderer.SetSelectedZone(-1);
        _cityRenderer.SetSelectedParcel(-1);
        _commuteOverlay.SelectZone(-1, _distanceMatrix, _roadGraph);
        _commuteOverlay.SelectParcel(-1, _parcelManager, _roadGraph);
        _gameUI.ShowCityOverview(_grid, _odMatrix, _parcelManager);
    }

    private void CancelPendingOperation()
    {
        _roadBuildStep = 0;
        _roadStartNode = null;
        _roadEndNode = null;
        _pendingStartZoneId = -1;
        CancelTransitDraft();
        _previewRenderer?.ClearRoadBuildPreview();
        _previewRenderer?.ClearPreview();
        _cityRenderer?.Refresh();
    }

    private void CancelTransitDraft()
    {
        _draftTransitStops.Clear();
        _draftTransitPath.Clear();
        _draftIsLoop = false;
        _previewRenderer?.ClearTransitDraft();
        _gameUI?.UpdateRouteDesignerStatus(0, 0, false);
    }
    
    private void RecalculateODMatrix()
    {
        var coveredZones = _transitManager.GetCoveredZoneIds(_grid, 3, _roadGraph, _parcelManager);
        float avgPrice = 12f;
        if (_transitManager.Routes.Count > 0)
        {
            float total = 0f;
            foreach (var r in _transitManager.Routes) total += r.TicketPrice;
            avgPrice = total / _transitManager.Routes.Count;
        }

        _odMatrix.Recalculate(_gameHour, _grid, _distanceMatrix, _transitManager.Routes.Count > 0, coveredZones, avgPrice, _walkingDistanceMatrix, _transitManager, _parcelManager, _roadGraph);
        _trafficEngine.AssignFlows(_odMatrix, _roadGraph, _grid, _transitManager);
        _roadRenderer.UpdateMaxVolume(_trafficEngine.GetMaxVolume(_roadGraph));
        _carTrafficManager.RefreshBusyEdges(_roadGraph);
        _lastODRecalcHour = _gameHour;

        // Refresh selected parcel or zone overlay
        if (_selectedParcelId != -1 && _parcelManager != null && _parcelManager.ParcelMap.TryGetValue(_selectedParcelId, out var selectedParcel))
        {
            _commuteOverlay.SelectParcel(_selectedParcelId, _parcelManager, _roadGraph);
            _gameUI.ShowParcelInfographics(selectedParcel, _parcelManager, _odMatrix, _grid);
        }
        else if (_selectedZoneId != -1)
        {
            _commuteOverlay.SelectZone(_selectedZoneId, _distanceMatrix, _roadGraph);
            var zone = _grid.GetZone(_selectedZoneId);
            _gameUI.ShowZoneInfographics(zone, _grid, _odMatrix, _distanceMatrix);
        }
    }
    
    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Realistic clock speed: at 1x speed, 1 real second = 1 game minute (0.01667 hours/sec)
        // 1 full day takes 24 minutes at 1x, 2.4 minutes at 10x, ~29 seconds at 50x.
        float clockRate = (1.0f / 60.0f) * _gameSpeed;
        _gameHour += dt * clockRate;

        // Safe day rollover
        while (_gameHour >= 24f)
        {
            _gameHour -= 24f;
            _gameDay++;
            _lastODRecalcHour -= 24f;
            _lastFinancialTickHour -= 24f;
        }
        
        // Recalculate macro traffic flows when 30 game minutes elapse
        if (Mathf.Abs(_gameHour - _lastODRecalcHour) >= _odRecalcInterval)
        {
            RecalculateODMatrix();
        }

        if (Mathf.Abs(_gameHour - _lastFinancialTickHour) >= 1f)
        {
            _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
            _lastFinancialTickHour = _gameHour;
        }
        
        // Update simulation sub-systems
        _trafficLights.Update(dt, _gameSpeed);
        _carTrafficManager.Update(dt, _gameSpeed, _roadGraph, _trafficLights, _parcelManager);
        _transitManager.Update(dt, _gameSpeed, _roadGraph, _trafficLights, _gameHour);
        _pedestrianManager.Update(dt, _gameSpeed, _roadGraph, _grid, _transitManager);
        
        ProcessPopulationGrowth(dt, _gameSpeed);
        
        // Calculate transit ridership
        float transitRidership = 0f;
        foreach (var v in _transitManager.Vehicles) transitRidership += v.Passengers;
        
        // Update HUD
        int totalPop = _grid.TotalPopulation() + (_parcelManager?.TotalPopulation ?? 0);
        int totalJobs = _grid.TotalJobs() + (_parcelManager?.TotalJobs ?? 0);
        _gameUI.UpdateTime(_gameHour, _gameDay);
        _gameUI.UpdateStats(
            totalPop,
            _odMatrix.TotalTrips,
            _trafficEngine.GetAverageCongestion(_roadGraph),
            transitRidership,
            _transitManager.GetTransitCoverage(_grid, _roadGraph, _parcelManager),
            ODMatrix.GetDemandMultiplier(_gameHour),
            ODMatrix.GetDirectionalBias(_gameHour),
            totalJobs
        );
        
        _gameUI.UpdateEconomy(_economyManager.Money, _economyManager.LastDelta);
        
        HandleCameraPan(delta);
    }
    
    private void HandleCameraPan(double delta)
    {
        float panSpeed = 600f / _camera.Zoom.X;
        Vector2 pan = Vector2.Zero;
        
        if (Input.IsActionPressed("camera_pan_up")) pan.Y -= 1;
        if (Input.IsActionPressed("camera_pan_down")) pan.Y += 1;
        if (Input.IsActionPressed("camera_pan_left")) pan.X -= 1;
        if (Input.IsActionPressed("camera_pan_right")) pan.X += 1;
        
        if (pan != Vector2.Zero)
        {
            _camera.Position += pan.Normalized() * panSpeed * (float)delta;
        }
    }
    
    private float _populationGrowthTimer = 0f;

    private void ProcessPopulationGrowth(float dt, float gameSpeed)
    {
        _populationGrowthTimer += dt * gameSpeed;
        if (_populationGrowthTimer >= 1.0f) // roughly every in-game minute
        {
            _populationGrowthTimer -= 1.0f;

            var entrances = new List<int>();
            for (int i = 0; i < _grid.ZoneCount; i++)
            {
                if (_grid.GetZone(i).Type == ZoneType.Entrance)
                    entrances.Add(i);
            }

            if (entrances.Count == 0) return;

            bool uiNeedsUpdate = false;
            for (int i = 0; i < _grid.ZoneCount; i++)
            {
                var zone = _grid.GetZone(i);
                if (zone.Type == ZoneType.Residential && zone.Population < zone.ResidentialCap)
                {
                    bool isConnected = false;
                    foreach (int ent in entrances)
                    {
                        // Check if path exists from entrance to residential zone
                        if (_distanceMatrix[ent, i] < float.MaxValue)
                        {
                            isConnected = true;
                            break;
                        }
                    }

                    if (isConnected)
                    {
                        zone.Population = Mathf.Min(zone.ResidentialCap, zone.Population + 50);
                        uiNeedsUpdate = true;
                    }
                }
            }

            if (_parcelManager != null)
            {
                foreach (var parcel in _parcelManager.Parcels)
                {
                    if (parcel.ZoneType == ZoneType.Residential && parcel.Population < parcel.ResidentialCap)
                    {
                        if (parcel.EdgeId >= 0 && parcel.EdgeId < _roadGraph.Edges.Count && _roadGraph.Edges[parcel.EdgeId].FromId != -1)
                        {
                            parcel.Population = Mathf.Min(parcel.ResidentialCap, parcel.Population + 4);
                            uiNeedsUpdate = true;
                        }
                    }
                }
            }

            if (uiNeedsUpdate)
            {
                if (_selectedParcelId != -1 && _parcelManager != null && _parcelManager.ParcelMap.TryGetValue(_selectedParcelId, out var selectedParcel))
                {
                    if (selectedParcel.ZoneType == ZoneType.Residential)
                    {
                        _gameUI.ShowParcelInfographics(selectedParcel, _parcelManager, _odMatrix, _grid);
                    }
                }
                else if (_selectedZoneId != -1)
                {
                    var selectedZone = _grid.GetZone(_selectedZoneId);
                    if (selectedZone.Type == ZoneType.Residential)
                    {
                        _gameUI.ShowZoneInfographics(selectedZone, _grid, _odMatrix, _distanceMatrix);
                    }
                }
            }
        }
    }
    
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                _camera.Zoom = (_camera.Zoom * 1.15f).Clamp(new Vector2(0.3f, 0.3f), new Vector2(4.0f, 4.0f));
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                _camera.Zoom = (_camera.Zoom / 1.15f).Clamp(new Vector2(0.3f, 0.3f), new Vector2(4.0f, 4.0f));
            }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                _isDragging = mb.Pressed;
            }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isZoningDragging = ToolPreviewRenderer.IsZoningMode(_currentMode);
                    HandleMapClick();
                }
                else
                {
                    _isZoningDragging = false;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                _isZoningDragging = false;
                HandleRightClick();
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_isDragging)
            {
                _camera.Position -= mm.Relative / _camera.Zoom;
            }
            else if (_currentMode != InteractionMode.Inspect)
            {
                UpdateHoverTarget();
                if (_isZoningDragging && ToolPreviewRenderer.IsZoningMode(_currentMode))
                {
                    HandleMapClick();
                }
            }
        }
        else if (@event is InputEventKey)
        {
            if (_currentMode == InteractionMode.BuildRoad || _currentMode == InteractionMode.BuildCurvedRoad || ToolPreviewRenderer.IsZoningMode(_currentMode))
            {
                UpdateHoverTarget();
            }
        }
    }

    private static ZoneType GetZoneTypeForMode(InteractionMode mode) => mode switch
    {
        InteractionMode.ZoneResidential => ZoneType.Residential,
        InteractionMode.ZoneCommercial  => ZoneType.Commercial,
        InteractionMode.ZoneIndustrial  => ZoneType.Industrial,
        _                               => ZoneType.Empty
    };

    private void UpdateHoverTarget()
    {
        if (_currentMode == InteractionMode.BuildRoad || _currentMode == InteractionMode.BuildCurvedRoad)
        {
            UpdateRoadBuildHover();
            return;
        }

        if (_currentMode == InteractionMode.CreateTransitRoute)
        {
            Vector2 mouse = GetGlobalMousePosition();
            var snap = RoadSnappingEngine.FindSnap(mouse, _roadGraph, snapRadius: 28f);
            _previewRenderer.SetTransitHoverSnap(snap);

            if (snap.Type == SnapType.Node && snap.SnappedNode != null)
            {
                _gameUI.SetToolHint($"🚏 [Transit Stop] Snapped to Node #{snap.SnappedNode.Id}. Click to add stop.", new Color(0.3f, 0.95f, 1f));
            }
            else if (snap.Type == SnapType.Edge && snap.SnappedEdge != null)
            {
                _gameUI.SetToolHint($"🚏 [Transit Stop] Snapped to Mid-Road (Edge #{snap.SnappedEdge.Id}). Click to insert mid-road stop.", new Color(0.3f, 1f, 0.7f));
            }
            else
            {
                _gameUI.SetToolHint("🚌 [Transit Designer] Click on a road edge or intersection to place a transit stop.", new Color(0.4f, 0.8f, 1f));
            }
            return;
        }

        if (ToolPreviewRenderer.IsZoningMode(_currentMode) && _parcelManager != null)
        {
            Vector2 mouse = GetGlobalMousePosition();
            bool fullSide = Input.IsKeyPressed(Key.Shift);
            var query = _parcelManager.QueryZoningTarget(mouse, _roadGraph, fullSide);

            if (query.SideParcels.Count > 0)
            {
                _previewRenderer.SetHoverZone(-1);
                ZoneType targetType = GetZoneTypeForMode(_currentMode);
                _previewRenderer.SetHoveredParcels(query.SideParcels, targetType, query.Side);

                string sideStr = query.Side == ParcelSide.Left ? "Left" : "Right";
                string targetStr = fullSide ? $"Entire {sideStr} Side ({query.SideParcels.Count} parcels)" : $"Parcel on {sideStr} Side";
                int changedCount = query.SideParcels.Count(p => p.ZoneType != targetType);
                float cost = changedCount * EconomyManager.ZoningCost;
                string costStr = _currentMode == InteractionMode.Dezone ? "Free" : $"${cost:F0}";

                _gameUI.SetToolHint($"🏡 [{_currentMode}] Hovering {targetStr}. Cost: {costStr}. (Hold Shift for entire side)", new Color(0.3f, 0.95f, 0.65f));
                return;
            }
            else
            {
                _previewRenderer.ClearHoveredParcels();
            }
        }

        Vector2 mouseWorld = GetGlobalMousePosition();
        int gx = Mathf.FloorToInt(mouseWorld.X / _grid.CellSize);
        int gy = Mathf.FloorToInt(mouseWorld.Y / _grid.CellSize);

        if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
        {
            int zoneId = _grid.GetZoneId(gx, gy);
            _previewRenderer.SetHoverZone(zoneId);
        }
        else
        {
            _previewRenderer.SetHoverZone(-1);
        }
    }

    private void HandleRightClick()
    {
        if (_currentMode == InteractionMode.BuildRoad || _currentMode == InteractionMode.BuildCurvedRoad)
        {
            if (_roadBuildStep > 0)
            {
                _roadBuildStep = 0;
                _roadStartNode = null;
                _roadEndNode = null;
                _previewRenderer.ClearRoadBuildPreview();
                _gameUI.SetToolHint("🛣️ Road construction cancelled. Click to set new start point.", new Color(1f, 0.8f, 0.4f));
            }
            else
            {
                _gameUI.SetInteractionMode(InteractionMode.Inspect);
            }
            _cityRenderer?.Refresh();
            return;
        }

        if (_currentMode == InteractionMode.CreateTransitRoute)
        {
            if (_draftTransitStops.Count > 0)
            {
                CancelTransitDraft();
                _gameUI.SetToolHint("🚌 [Transit Designer] Чернетку маршруту скинуто. Клікніть першу зупинку.", new Color(1f, 0.8f, 0.4f));
            }
            else
            {
                _gameUI.SetInteractionMode(InteractionMode.Inspect);
            }
            _cityRenderer?.Refresh();
            return;
        }

        CancelPendingOperation();
        if (_currentMode == InteractionMode.Inspect)
        {
            ClearInspectSelection();
        }
        else
        {
            _gameUI.SetInteractionMode(InteractionMode.Inspect);
        }
        _cityRenderer?.Refresh();
    }

    private void HandleMapClick()
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        int gx = Mathf.FloorToInt(mouseWorld.X / _grid.CellSize);
        int gy = Mathf.FloorToInt(mouseWorld.Y / _grid.CellSize);

        switch (_currentMode)
        {
            case InteractionMode.Inspect:
                HandleInspectClick(gx, gy);
                break;
            case InteractionMode.BuildRoad:
            case InteractionMode.BuildCurvedRoad:
                HandleRoadBuildClick();
                break;
            case InteractionMode.Demolish:
                HandleDemolishClick(gx, gy);
                break;
            case InteractionMode.ZoneResidential:
                HandleZoneClick(gx, gy, ZoneType.Residential);
                break;
            case InteractionMode.ZoneCommercial:
                HandleZoneClick(gx, gy, ZoneType.Commercial);
                break;
            case InteractionMode.ZoneIndustrial:
                HandleZoneClick(gx, gy, ZoneType.Industrial);
                break;
            case InteractionMode.Dezone:
                HandleDezoneClick(gx, gy);
                break;
            case InteractionMode.CreateTransitRoute:
                HandleCreateTransitRouteClick(mouseWorld);
                break;
        }
    }

    private void HandleInspectClick(int gx, int gy)
    {
        Vector2 mouseWorld = GetGlobalMousePosition();

        // 1. Check roadside ribbon parcels first (higher precision click on continuous lot)
        RoadsideParcel clickedParcel = _parcelManager?.GetParcelAt(mouseWorld);
        if (clickedParcel == null && _parcelManager != null)
        {
            clickedParcel = _parcelManager.FindClosestParcel(mouseWorld, 24f);
        }

        if (clickedParcel != null && clickedParcel.ZoneType != ZoneType.Empty)
        {
            _selectedZoneId = -1;
            _selectedParcelId = clickedParcel.Id;
            _cityRenderer.SetSelectedZone(-1);
            _cityRenderer.SetSelectedParcel(clickedParcel.Id);
            _commuteOverlay.SelectParcel(clickedParcel.Id, _parcelManager, _roadGraph);
            _gameUI.ShowParcelInfographics(clickedParcel, _parcelManager, _odMatrix, _grid);
            return;
        }

        // 2. Fallback to legacy grid zones
        if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
        {
            int zoneId = _grid.GetZoneId(gx, gy);
            var zone = _grid.GetZone(zoneId);

            if (zone != null && zone.Type != ZoneType.Empty)
            {
                _selectedParcelId = -1;
                _selectedZoneId = zoneId;
                _cityRenderer.SetSelectedParcel(-1);
                _cityRenderer.SetSelectedZone(zoneId);
                _commuteOverlay.SelectZone(zoneId, _distanceMatrix, _roadGraph);
                _gameUI.ShowZoneInfographics(zone, _grid, _odMatrix, _distanceMatrix);
                return;
            }
        }

        // Clicked outside or empty zone/parcel: reset to overview
        ClearInspectSelection();
    }

    private void UpdateRoadBuildHover()
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        bool forceAngle = Input.IsKeyPressed(Key.Shift);

        if (_roadBuildStep == 0)
        {
            _currentCursorSnap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, null, null, forceAngle);
            _previewRenderer.SetRoadBuildPreview(
                0,
                _currentCursorSnap.Position,
                _currentCursorSnap.Position,
                _currentCursorSnap.Position,
                null,
                _currentCursorSnap,
                0f,
                true
            );
        }
        else if (_roadBuildStep == 1)
        {
            _currentCursorSnap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, _roadStartPoint, _roadStartNode, forceAngle);
            var curve = new CurveSegment(_roadStartPoint, _currentCursorSnap.Position);
            float cost = Mathf.Max(50f, Mathf.Round(EconomyManager.RoadSegmentCost * (curve.Length / 64f)));
            bool canAfford = _economyManager.CanAfford(cost);
            _previewRenderer.SetRoadBuildPreview(
                1,
                _roadStartPoint,
                _currentCursorSnap.Position,
                _currentCursorSnap.Position,
                curve,
                _currentCursorSnap,
                cost,
                canAfford
            );
        }
        else if (_roadBuildStep == 2)
        {
            var curve = CurveSegment.CreateFromThreePoints(_roadStartPoint, mouseWorld, _roadEndPoint);
            float cost = Mathf.Max(50f, Mathf.Round(EconomyManager.RoadSegmentCost * (curve.Length / 64f)));
            bool canAfford = _economyManager.CanAfford(cost);
            var snap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, null, null, false);
            _previewRenderer.SetRoadBuildPreview(
                2,
                _roadStartPoint,
                _roadEndPoint,
                mouseWorld,
                curve,
                snap,
                cost,
                canAfford
            );
        }
    }

    private void HandleRoadBuildClick()
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        bool forceAngle = Input.IsKeyPressed(Key.Shift);

        if (_roadBuildStep == 0)
        {
            // Step 1: Click start point
            _currentCursorSnap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, null, null, forceAngle);

            if (_currentCursorSnap.Type == SnapType.Node)
            {
                _roadStartNode = _currentCursorSnap.SnappedNode;
            }
            else if (_currentCursorSnap.Type == SnapType.Edge)
            {
                _roadStartNode = _roadGraph.SplitEdgeAtPoint(_currentCursorSnap.SnappedEdge.Id, _currentCursorSnap.Position);
            }
            else
            {
                int gx = Mathf.FloorToInt(_currentCursorSnap.Position.X / _grid.CellSize);
                int gy = Mathf.FloorToInt(_currentCursorSnap.Position.Y / _grid.CellSize);
                if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
                {
                    int zoneId = _grid.GetZoneId(gx, gy);
                    var zone = _grid.GetZone(zoneId);
                    if (zone != null && zone.Type != ZoneType.Empty && _roadGraph.NodeMap.ContainsKey(zoneId))
                    {
                        _roadStartNode = _roadGraph.NodeMap[zoneId];
                    }
                    else
                    {
                        _roadStartNode = _roadGraph.CreateNode(_currentCursorSnap.Position, -1);
                    }
                }
                else
                {
                    _roadStartNode = _roadGraph.CreateNode(_currentCursorSnap.Position, -1);
                }
            }

            _roadStartPoint = _roadStartNode.WorldPosition;
            _roadBuildStep = 1;
            UpdateRoadBuildHover();
            string stepTotal = _currentMode == InteractionMode.BuildCurvedRoad ? "3" : "2";
            _gameUI.SetToolHint($"📍 [Step 2/{stepTotal}] Start set. Click end point (Hold Shift for angle snap).", new Color(0.2f, 1f, 0.7f));
        }
        else if (_roadBuildStep == 1)
        {
            // Step 2: Click end point
            _currentCursorSnap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, _roadStartPoint, _roadStartNode, forceAngle);

            if (_roadStartPoint.DistanceTo(_currentCursorSnap.Position) < 8.0f)
            {
                _gameUI.SetToolHint("⚠️ Road segment too short. Choose a farther point.", Colors.Yellow);
                return;
            }

            if (_currentMode == InteractionMode.BuildCurvedRoad)
            {
                _roadEndPoint = _currentCursorSnap.Position;
                _pendingEndSnap = _currentCursorSnap;
                _roadBuildStep = 2;
                UpdateRoadBuildHover();
                _gameUI.SetToolHint("〰️ [Step 3/3] Move cursor to bend curvature, then click to build.", new Color(0.3f, 0.95f, 1f));
                return;
            }

            // Straight Road Mode: Build immediately
            if (_currentCursorSnap.Type == SnapType.Node)
            {
                _roadEndNode = _currentCursorSnap.SnappedNode;
            }
            else if (_currentCursorSnap.Type == SnapType.Edge)
            {
                _roadEndNode = _roadGraph.SplitEdgeAtPoint(_currentCursorSnap.SnappedEdge.Id, _currentCursorSnap.Position);
            }
            else
            {
                int gx = Mathf.FloorToInt(_currentCursorSnap.Position.X / _grid.CellSize);
                int gy = Mathf.FloorToInt(_currentCursorSnap.Position.Y / _grid.CellSize);
                if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
                {
                    int zoneId = _grid.GetZoneId(gx, gy);
                    var zone = _grid.GetZone(zoneId);
                    if (zone != null && zone.Type != ZoneType.Empty && _roadGraph.NodeMap.ContainsKey(zoneId))
                    {
                        _roadEndNode = _roadGraph.NodeMap[zoneId];
                    }
                    else
                    {
                        _roadEndNode = _roadGraph.CreateNode(_currentCursorSnap.Position, -1);
                    }
                }
                else
                {
                    _roadEndNode = _roadGraph.CreateNode(_currentCursorSnap.Position, -1);
                }
            }

            if (_roadStartNode.Id == _roadEndNode.Id)
            {
                _gameUI.SetToolHint("⚠️ Start and end points must be different.", Colors.Yellow);
                return;
            }

            if (_roadGraph.HasEdge(_roadStartNode.Id, _roadEndNode.Id))
            {
                _gameUI.SetToolHint("⚠️ Road already exists between these points.", Colors.Yellow);
                return;
            }

            var straightCurve = new CurveSegment(_roadStartNode.WorldPosition, _roadEndNode.WorldPosition);
            float cost = Mathf.Max(50f, Mathf.Round(EconomyManager.RoadSegmentCost * (straightCurve.Length / 64f)));

            if (!_economyManager.CanAfford(cost))
            {
                _gameUI.SetToolHint($"⚠️ Insufficient funds! Costs ${cost:F0}.", Colors.Coral);
                return;
            }

            bool added = _roadGraph.AddCurvedRoadSegment(_roadStartNode.Id, _roadEndNode.Id, straightCurve);
            if (added)
            {
                _economyManager.Spend(cost);
                CompleteRoadConstruction();
            }
        }
        else if (_roadBuildStep == 2)
        {
            // Step 3: Click to confirm curve bend apex
            if (_pendingEndSnap.Type == SnapType.Node)
            {
                _roadEndNode = _pendingEndSnap.SnappedNode;
            }
            else if (_pendingEndSnap.Type == SnapType.Edge)
            {
                _roadEndNode = _roadGraph.SplitEdgeAtPoint(_pendingEndSnap.SnappedEdge.Id, _roadEndPoint);
            }
            else
            {
                int gx = Mathf.FloorToInt(_roadEndPoint.X / _grid.CellSize);
                int gy = Mathf.FloorToInt(_roadEndPoint.Y / _grid.CellSize);
                if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
                {
                    int zoneId = _grid.GetZoneId(gx, gy);
                    var zone = _grid.GetZone(zoneId);
                    if (zone != null && zone.Type != ZoneType.Empty && _roadGraph.NodeMap.ContainsKey(zoneId))
                    {
                        _roadEndNode = _roadGraph.NodeMap[zoneId];
                    }
                    else
                    {
                        _roadEndNode = _roadGraph.CreateNode(_roadEndPoint, -1);
                    }
                }
                else
                {
                    _roadEndNode = _roadGraph.CreateNode(_roadEndPoint, -1);
                }
            }

            if (_roadStartNode.Id == _roadEndNode.Id)
            {
                _gameUI.SetToolHint("⚠️ Start and end points must be different.", Colors.Yellow);
                return;
            }

            if (_roadGraph.HasEdge(_roadStartNode.Id, _roadEndNode.Id))
            {
                _gameUI.SetToolHint("⚠️ Road already exists between these points.", Colors.Yellow);
                return;
            }

            var curve = CurveSegment.CreateFromThreePoints(_roadStartNode.WorldPosition, mouseWorld, _roadEndNode.WorldPosition);
            float cost = Mathf.Max(50f, Mathf.Round(EconomyManager.RoadSegmentCost * (curve.Length / 64f)));

            if (!_economyManager.CanAfford(cost))
            {
                _gameUI.SetToolHint($"⚠️ Insufficient funds! Costs ${cost:F0}.", Colors.Coral);
                return;
            }

            bool added = _roadGraph.AddCurvedRoadSegment(_roadStartNode.Id, _roadEndNode.Id, curve);
            if (added)
            {
                _economyManager.Spend(cost);
                CompleteRoadConstruction();
            }
        }
    }

    private void CompleteRoadConstruction()
    {
        _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _trafficLights.BuildIntersections(_roadGraph);
        _carTrafficManager.HandleInvalidatedEdges(_roadGraph);
        _pedestrianManager.HandleInvalidatedEdges(_roadGraph, _grid, _transitManager);
        RecalculateODMatrix();
        _parcelManager?.RefreshParcels(_roadGraph);
        _roadRenderer.Refresh();
        _cityRenderer.Refresh();
        _vehicleRenderer.QueueRedraw();

        _roadBuildStep = 0;
        _roadStartNode = null;
        _roadEndNode = null;
        _previewRenderer.ClearRoadBuildPreview();
        _gameUI.SetToolHint("✅ Road built successfully! Click to build another.", new Color(0.3f, 1f, 0.5f));
    }

    private void HandleDemolishClick(int gx, int gy)
    {
        if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
        {
            return;
        }

        int zoneId = _grid.GetZoneId(gx, gy);
        var zone = _grid.GetZone(zoneId);

        if (zone == null)
        {
            return;
        }

        if (_pendingStartZoneId == -1)
        {
            // First step: select start zone
            var validTargets = GetValidAdjacentTargets(InteractionMode.Demolish, zone.Id);
            if (validTargets.Count == 0)
            {
                _gameUI.SetToolHint($"⚠️ Zone at ({gx}, {gy}) has no road connections to demolish.", Colors.Coral);
                return;
            }

            _pendingStartZoneId = zone.Id;
            _previewRenderer.SetPreview(InteractionMode.Demolish, _pendingStartZoneId, validTargets);
            _gameUI.SetToolHint($"💥 [Demolish] Selected start cell at ({gx}, {gy}). Click an adjacent connected cell to demolish (Right-click to cancel).", new Color(1f, 0.4f, 0.3f));
        }
        else
        {
            // Second step: click adjacent target zone to demolish connecting road
            if (zone.Id == _pendingStartZoneId)
            {
                CancelPendingOperation();
                _gameUI.SetToolHint("💥 [Demolish] Selection cancelled. Click first cell to demolish road.", new Color(1f, 0.5f, 0.4f));
                return;
            }

            var startZone = _grid.GetZone(_pendingStartZoneId);
            int manhattanDist = Mathf.Abs(startZone.GridPos.X - zone.GridPos.X) + Mathf.Abs(startZone.GridPos.Y - zone.GridPos.Y);

            if (manhattanDist != 1)
            {
                var newTargets = GetValidAdjacentTargets(InteractionMode.Demolish, zone.Id);
                if (newTargets.Count > 0)
                {
                    _pendingStartZoneId = zone.Id;
                    _previewRenderer.SetPreview(InteractionMode.Demolish, _pendingStartZoneId, newTargets);
                    _gameUI.SetToolHint($"⚠️ Cells must be adjacent! New start cell set at ({gx}, {gy}). Click an adjacent connected cell.", Colors.Yellow);
                }
                else
                {
                    _gameUI.SetToolHint($"⚠️ Cells must be adjacent! Click a cell adjacent to ({startZone.GridPos.X}, {startZone.GridPos.Y}).", Colors.Yellow);
                }
                return;
            }

            // Check if road connection exists
            if (!_roadGraph.HasEdge(_pendingStartZoneId, zone.Id) && !_roadGraph.HasEdge(zone.Id, _pendingStartZoneId))
            {
                var newTargets = GetValidAdjacentTargets(InteractionMode.Demolish, zone.Id);
                if (newTargets.Count > 0)
                {
                    _pendingStartZoneId = zone.Id;
                    _previewRenderer.SetPreview(InteractionMode.Demolish, _pendingStartZoneId, newTargets);
                    _gameUI.SetToolHint($"⚠️ No road exists between these cells! New start cell set at ({gx}, {gy}).", Colors.Yellow);
                }
                else
                {
                    _gameUI.SetToolHint($"⚠️ No road exists between ({startZone.GridPos.X}, {startZone.GridPos.Y}) and ({gx}, {gy}).", Colors.Yellow);
                }
                return;
            }

            // Remove road segment
            bool removed = _roadGraph.RemoveRoadSegment(_pendingStartZoneId, zone.Id);
            if (removed)
            {
                _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
                _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
                _trafficLights.BuildIntersections(_roadGraph);
                _carTrafficManager.HandleInvalidatedEdges(_roadGraph);
                _pedestrianManager.HandleInvalidatedEdges(_roadGraph, _grid, _transitManager);
                RecalculateODMatrix();
                _parcelManager?.RefreshParcels(_roadGraph);
                _roadRenderer.Refresh();
                _cityRenderer.Refresh();
                _vehicleRenderer.QueueRedraw();

                int sx = startZone.GridPos.X;
                int sy = startZone.GridPos.Y;
                CancelPendingOperation();
                _gameUI.SetToolHint($"💥 Road demolished between ({sx}, {sy}) and ({gx}, {gy})! Click to demolish another.", new Color(1f, 0.4f, 0.3f));
            }
            else
            {
                CancelPendingOperation();
                _gameUI.SetToolHint("⚠️ Failed to demolish road segment.", Colors.Coral);
            }
        }
    }

    private void HandleZoneClick(int gx, int gy, ZoneType type)
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        bool fullSide = Input.IsKeyPressed(Key.Shift);
        if (_parcelManager != null)
        {
            var query = _parcelManager.QueryZoningTarget(mouseWorld, _roadGraph, fullSide);
            if (query.SideParcels.Count > 0)
            {
                HandleRoadsideZoneClick(query.SideParcels, query.Side, type, fullSide);
                return;
            }
        }

        if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
        {
            return;
        }

        int zoneId = _grid.GetZoneId(gx, gy);
        var zone = _grid.GetZone(zoneId);

        if (zone != null && zone.Type == type)
        {
            _gameUI.SetToolHint($"ℹ️ Cell ({gx}, {gy}) is already zoned as {type}.", Colors.LightGray);
            return;
        }

        if (!_economyManager.CanAfford(EconomyManager.ZoningCost))
        {
            _gameUI.SetToolHint($"⚠️ Insufficient funds! Zoning costs ${EconomyManager.ZoningCost}.", Colors.Coral);
            return;
        }

        bool changed = _grid.ZoneCell(zoneId, type);
        if (changed)
        {
            _economyManager.Spend(EconomyManager.ZoningCost);
            _roadGraph.EnsureNode(zoneId, _grid.GetWorldCenter(zoneId));
            _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
            _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
            RecalculateODMatrix();
            _cityRenderer.Refresh();
            _roadRenderer.Refresh();
            _vehicleRenderer.QueueRedraw();

            _gameUI.ShowCityOverview(_grid, _odMatrix);

            string typeName = type switch
            {
                ZoneType.Residential => "Residential (🏡)",
                ZoneType.Commercial  => "Commercial (🏢)",
                ZoneType.Industrial  => "Industrial (🏭)",
                _ => type.ToString()
            };
            _gameUI.SetToolHint($"✅ Designated cell ({gx}, {gy}) as {typeName}! Click more cells to expand.", new Color(0.3f, 1f, 0.5f));
        }
    }

    private void HandleDezoneClick(int gx, int gy)
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        bool fullSide = Input.IsKeyPressed(Key.Shift);
        if (_parcelManager != null)
        {
            var query = _parcelManager.QueryZoningTarget(mouseWorld, _roadGraph, fullSide);
            if (query.SideParcels.Count > 0)
            {
                HandleRoadsideDezoneClick(query.SideParcels, query.Side, fullSide);
                return;
            }
        }

        if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
        {
            return;
        }

        int zoneId = _grid.GetZoneId(gx, gy);
        var zone = _grid.GetZone(zoneId);

        if (zone == null || zone.Type == ZoneType.Empty)
        {
            _gameUI.SetToolHint($"ℹ️ Cell ({gx}, {gy}) is already empty terrain.", Colors.LightGray);
            return;
        }

        if (_selectedZoneId == zoneId)
        {
            ClearInspectSelection();
        }

        // Only dezone the cell, leaving any existing road nodes and edges intact
        _grid.DezoneCell(zoneId);

        _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
        _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
        _trafficLights.BuildIntersections(_roadGraph);
        _carTrafficManager.HandleInvalidatedEdges(_roadGraph);
        _pedestrianManager.HandleInvalidatedEdges(_roadGraph);
        RecalculateODMatrix();
        _cityRenderer.Refresh();
        _roadRenderer.Refresh();
        _vehicleRenderer.QueueRedraw();

        _gameUI.ShowCityOverview(_grid, _odMatrix);

        _gameUI.SetToolHint($"🧹 Cleared cell ({gx}, {gy}) back to empty terrain.", new Color(1f, 0.7f, 0.4f));
    }

    private void HandleRoadsideZoneClick(List<RoadsideParcel> parcels, ParcelSide side, ZoneType type, bool fullSide)
    {
        var toZone = parcels.Where(p => p.ZoneType != type).ToList();
        if (toZone.Count == 0)
        {
            _gameUI.SetToolHint($"ℹ️ Selected roadside parcel(s) are already zoned as {type}.", Colors.LightGray);
            return;
        }

        float totalCost = toZone.Count * EconomyManager.ZoningCost;
        if (!_economyManager.CanAfford(totalCost))
        {
            _gameUI.SetToolHint($"⚠️ Insufficient funds! Costs ${totalCost:F0} (${EconomyManager.ZoningCost}/parcel).", Colors.Coral);
            return;
        }

        _economyManager.Spend(totalCost);
        foreach (var p in toZone)
        {
            _parcelManager.ZoneParcel(p.Id, type, ensureAccessNode: false);
        }

        _cityRenderer.Refresh();
        _roadRenderer.Refresh();
        _previewRenderer.ClearHoveredParcels();
        UpdateHoverTarget();

        string sideStr = side == ParcelSide.Left ? "Left" : "Right";
        string typeName = type switch
        {
            ZoneType.Residential => "Residential (🏡)",
            ZoneType.Commercial  => "Commercial (🏢)",
            ZoneType.Industrial  => "Industrial (🏭)",
            _ => type.ToString()
        };

        string msg = toZone.Count == 1
            ? $"✅ Zoned roadside lot on {sideStr} side as {typeName} (-${EconomyManager.ZoningCost:F0})!"
            : $"✅ Zoned {toZone.Count} roadside lots on {sideStr} side as {typeName} (-${totalCost:F0})!";
        _gameUI.SetToolHint(msg, new Color(0.3f, 1f, 0.5f));
    }

    private void HandleRoadsideDezoneClick(List<RoadsideParcel> parcels, ParcelSide side, bool fullSide)
    {
        var toDezone = parcels.Where(p => p.ZoneType != ZoneType.Empty).ToList();
        if (toDezone.Count == 0)
        {
            _gameUI.SetToolHint("ℹ️ Selected roadside parcel(s) are already empty.", Colors.LightGray);
            return;
        }

        foreach (var p in toDezone)
        {
            _parcelManager.DezoneParcel(p.Id);
        }

        _cityRenderer.Refresh();
        _previewRenderer.ClearHoveredParcels();
        UpdateHoverTarget();

        string sideStr = side == ParcelSide.Left ? "Left" : "Right";
        string msg = toDezone.Count == 1
            ? $"🧹 Cleared roadside lot on {sideStr} side back to empty."
            : $"🧹 Cleared {toDezone.Count} roadside lots on {sideStr} side back to empty.";
        _gameUI.SetToolHint(msg, new Color(1f, 0.7f, 0.4f));
    }

    private List<int> GetValidAdjacentTargets(InteractionMode mode, int zoneId)
    {
        var list = new List<int>();
        if (zoneId < 0 || zoneId >= _grid.ZoneCount) return list;

        var zone = _grid.GetZone(zoneId);
        if (zone == null) return list;

        int gx = zone.GridPos.X;
        int gy = zone.GridPos.Y;

        (int dx, int dy)[] dirs = new (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        foreach (var (dx, dy) in dirs)
        {
            int nx = gx + dx;
            int ny = gy + dy;
            if (nx < 0 || nx >= _grid.Width || ny < 0 || ny >= _grid.Height) continue;

            int nId = _grid.GetZoneId(nx, ny);
            var nZone = _grid.GetZone(nId);
            if (nZone == null) continue;

            bool hasRoad = _roadGraph.HasEdge(zoneId, nId) || _roadGraph.HasEdge(nId, zoneId);
            if (mode == InteractionMode.BuildRoad && !hasRoad)
            {
                list.Add(nId);
            }
            else if (mode == InteractionMode.Demolish && hasRoad)
            {
                list.Add(nId);
            }
        }

        return list;
    }

    private void HandleCreateTransitRouteClick(Vector2 mouseWorld)
    {
        var snap = RoadSnappingEngine.FindSnap(mouseWorld, _roadGraph, snapRadius: 28f);
        int stopNodeId = -1;

        if (snap.Type == SnapType.Node && snap.SnappedNode != null)
        {
            stopNodeId = snap.SnappedNode.Id;
        }
        else if (snap.Type == SnapType.Edge && snap.SnappedEdge != null)
        {
            // Mid-road stop insertion: automatically split the edge at the clicked position
            var newNode = _roadGraph.SplitEdgeAtPoint(snap.SnappedEdge.Id, snap.Position);
            if (newNode == null)
            {
                _gameUI.SetToolHint("⚠️ Failed to insert mid-road stop at this position.", Colors.Coral);
                return;
            }
            stopNodeId = newNode.Id;

            // Rebuild topology and matrices
            _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
            _walkingDistanceMatrix = _roadGraph.ComputeWalkingDistanceMatrix(_grid.ZoneCount);
            _trafficLights.BuildIntersections(_roadGraph);
            _carTrafficManager.HandleInvalidatedEdges(_roadGraph);
            _pedestrianManager.HandleInvalidatedEdges(_roadGraph);
            _parcelManager?.RefreshParcels(_roadGraph);
            _roadRenderer.Refresh();
            _cityRenderer.Refresh();
            _vehicleRenderer.QueueRedraw();
        }
        else
        {
            // Fallback: check if clicked on an existing node near cursor
            int gx = Mathf.FloorToInt(mouseWorld.X / _grid.CellSize);
            int gy = Mathf.FloorToInt(mouseWorld.Y / _grid.CellSize);
            if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
            {
                int zoneId = _grid.GetZoneId(gx, gy);
                if (_roadGraph.NodeMap.ContainsKey(zoneId))
                {
                    stopNodeId = zoneId;
                }
            }

            if (stopNodeId == -1)
            {
                _gameUI.SetToolHint("⚠️ Click an intersection or anywhere along a road edge to add a transit stop.", Colors.Coral);
                return;
            }
        }

        if (_draftTransitStops.Count == 0)
        {
            // First stop: Route origin
            _draftTransitStops.Add(stopNodeId);
            _draftTransitPath.Add(stopNodeId);
            _draftIsLoop = false;
            _previewRenderer.SetTransitDraft(_draftTransitStops, _draftTransitPath, _gameUI.CurrentRouteDesignerColor, _draftIsLoop);
            _gameUI.UpdateRouteDesignerStatus(_draftTransitStops.Count, _draftTransitPath.Count, _draftIsLoop);
            _gameUI.SetToolHint($"🚏 Origin stop set at Node #{stopNodeId}. Click along roads or intersections to add stops.", new Color(0.2f, 1f, 0.7f));
        }
        else
        {
            int lastStop = _draftTransitStops[_draftTransitStops.Count - 1];
            if (stopNodeId == lastStop)
            {
                _gameUI.SetToolHint("ℹ️ This node is already the current stop. Click a different point along the road.", Colors.LightGray);
                return;
            }

            // Loop closure check: clicking back on origin stop
            if (stopNodeId == _draftTransitStops[0] && _draftTransitStops.Count >= 2)
            {
                var loopPath = _roadGraph.GetShortestNodePath(lastStop, stopNodeId);
                if (loopPath == null || loopPath.Count < 2)
                {
                    _gameUI.SetToolHint("⚠️ No road path found to close loop back to start.", Colors.Coral);
                    return;
                }

                // Append intermediate nodes (skip start node which is lastStop and end node which is origin)
                for (int i = 1; i < loopPath.Count - 1; i++)
                {
                    _draftTransitPath.Add(loopPath[i]);
                }
                _draftIsLoop = true;
                _previewRenderer.SetTransitDraft(_draftTransitStops, _draftTransitPath, _gameUI.CurrentRouteDesignerColor, _draftIsLoop);
                _gameUI.UpdateRouteDesignerStatus(_draftTransitStops.Count, _draftTransitPath.Count, _draftIsLoop);
                _gameUI.SetToolHint("🔄 Closed-loop route ready! Click 'Запустити кільце' to deploy buses.", new Color(0.3f, 1f, 0.5f));
                return;
            }

            // Normal next stop along shortest path
            var segPath = _roadGraph.GetShortestNodePath(lastStop, stopNodeId);
            if (segPath == null || segPath.Count < 2)
            {
                _gameUI.SetToolHint("⚠️ No road path found connecting to this stop. Choose a connected road.", Colors.Coral);
                return;
            }

            for (int i = 1; i < segPath.Count; i++)
            {
                _draftTransitPath.Add(segPath[i]);
            }
            _draftTransitStops.Add(stopNodeId);
            _draftIsLoop = false;
            _previewRenderer.SetTransitDraft(_draftTransitStops, _draftTransitPath, _gameUI.CurrentRouteDesignerColor, _draftIsLoop);
            _gameUI.UpdateRouteDesignerStatus(_draftTransitStops.Count, _draftTransitPath.Count, _draftIsLoop);
            _gameUI.SetToolHint($"🚏 Stop {_draftTransitStops.Count} added! Click more stops, click start to loop, or click 'Запустити маршрут'.", new Color(0.2f, 1f, 0.7f));
        }
    }

    private void OnRouteLaunchRequested(string routeName, Color color)
    {
        if (_draftTransitStops.Count < 2 || _draftTransitPath.Count < 2)
        {
            _gameUI.SetToolHint("⚠️ At least 2 stops are required to launch a transit route.", Colors.Coral);
            return;
        }

        if (!_economyManager.CanAfford(EconomyManager.TransitRouteBaseCost))
        {
            _gameUI.SetToolHint($"⚠️ Insufficient funds! Launching a route costs ${EconomyManager.TransitRouteBaseCost}.", Colors.Coral);
            return;
        }

        int optimalFleet = TransitManager.CalculateOptimalFleetSize(_draftTransitPath.Count, _draftIsLoop);
        var newRoute = _transitManager.CreateRoute(
            routeName,
            _draftTransitPath,
            _draftTransitStops,
            color,
            _draftIsLoop,
            fleetSize: optimalFleet,
            ticketPrice: 12f
        );

        _economyManager.Spend(EconomyManager.TransitRouteBaseCost);
        CancelTransitDraft();
        _gameUI.SetInteractionMode(InteractionMode.Inspect);
        RecalculateODMatrix();
        _gameUI.UpdateTransitRoutesView();
        _vehicleRenderer.QueueRedraw();
        _roadRenderer.Refresh();
        _cityRenderer.Refresh();

        _gameUI.SetToolHint($"🎉 Route '{newRoute.Name}' launched with {newRoute.FleetSize} buses! Transit coverage updated.", new Color(0.3f, 1f, 0.5f));
    }

    private void OnRouteCancelRequested()
    {
        CancelTransitDraft();
        _cityRenderer.Refresh();
    }

    private void OnRouteDeleted(int routeId)
    {
        bool removed = _transitManager.RemoveRoute(routeId);
        if (removed)
        {
            RecalculateODMatrix();
            _gameUI.UpdateTransitRoutesView();
            _vehicleRenderer.QueueRedraw();
            _roadRenderer.Refresh();
            _gameUI.SetToolHint("🗑️ Route deleted and fleet decommissioned.", new Color(1f, 0.6f, 0.4f));
        }
    }
}
