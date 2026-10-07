using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Rendering;
using CitySim.UI;

namespace CitySim;

public partial class Main : Node2D
{
    private CityGrid _grid;
    private RoadGraph _roadGraph;
    private float[,] _distanceMatrix;
    private ODMatrix _odMatrix;
    private TrafficEngine _trafficEngine;
    private TransitManager _transitManager;
    private TrafficLightManager _trafficLights;
    private CarTrafficManager _carTrafficManager;
    
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
    
    private bool _isDragging = false;
    private int _selectedZoneId = -1;
    private InteractionMode _currentMode = InteractionMode.Inspect;
    private int _pendingStartZoneId = -1;
    
    public override void _Ready()
    {
        _grid = new CityGrid(20, 20, 64f);
        _grid.GenerateDefaultCity();
        
        _roadGraph = new RoadGraph();
        _roadGraph.BuildFromGrid(_grid);
        
        _distanceMatrix = _roadGraph.ComputeDistanceMatrix(_grid.ZoneCount);
        _roadGraph.BuildPathCache(_grid.ZoneCount);
        
        _odMatrix = new ODMatrix(_grid.ZoneCount);
        _trafficEngine = new TrafficEngine();
        
        _trafficLights = new TrafficLightManager();
        _trafficLights.BuildIntersections(_roadGraph);
        
        _carTrafficManager = new CarTrafficManager();
        _carTrafficManager.Initialize(_roadGraph);
        
        _transitManager = new TransitManager();
        _transitManager.CreateDefaultRoutes(_grid, _roadGraph);
        
        _camera = new Camera2D();
        _camera.Position = new Vector2(_grid.Width * _grid.CellSize / 2f, _grid.Height * _grid.CellSize / 2f);
        _camera.Zoom = new Vector2(0.85f, 0.85f);
        AddChild(_camera);
        
        _cityRenderer = new CityRenderer();
        AddChild(_cityRenderer);
        _cityRenderer.Initialize(_grid);
        
        _roadRenderer = new RoadRenderer();
        AddChild(_roadRenderer);
        _roadRenderer.Initialize(_roadGraph, _trafficLights);

        _commuteOverlay = new CommuteOverlayRenderer();
        AddChild(_commuteOverlay);
        _commuteOverlay.Initialize(_grid, _odMatrix, _roadGraph);
        
        _vehicleRenderer = new VehicleRenderer();
        AddChild(_vehicleRenderer);
        _vehicleRenderer.Initialize(_transitManager, _carTrafficManager, _roadGraph);

        _previewRenderer = new ToolPreviewRenderer();
        AddChild(_previewRenderer);
        _previewRenderer.Initialize(_grid);
        
        _gameUI = new GameUI();
        AddChild(_gameUI);
        _gameUI.SetTransitManager(_transitManager);
        
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

        _gameUI.ModeChanged += OnInteractionModeChanged;
        
        RecalculateODMatrix();
        _gameUI.ShowCityOverview(_grid, _odMatrix);
    }

    private void OnInteractionModeChanged(int modeInt)
    {
        _currentMode = (InteractionMode)modeInt;
        CancelPendingOperation();

        if (_currentMode != InteractionMode.Inspect)
        {
            ClearInspectSelection();
        }
        else
        {
            _gameUI.SetToolHint("🔍 [Inspect] Click a zone on the map to view commute analytics.", new Color(0.4f, 0.8f, 1f));
        }
    }

    private void ClearInspectSelection()
    {
        _selectedZoneId = -1;
        _cityRenderer.SetSelectedZone(-1);
        _commuteOverlay.SelectZone(-1, _distanceMatrix, _roadGraph);
        _gameUI.ShowCityOverview(_grid, _odMatrix);
    }

    private void CancelPendingOperation()
    {
        _pendingStartZoneId = -1;
        _previewRenderer.ClearPreview();
    }
    
    private void RecalculateODMatrix()
    {
        _odMatrix.Recalculate(_gameHour, _grid, _distanceMatrix, _transitManager.Routes.Count > 0);
        _trafficEngine.AssignFlows(_odMatrix, _roadGraph, _grid);
        _roadRenderer.UpdateMaxVolume(_trafficEngine.GetMaxVolume(_roadGraph));
        _carTrafficManager.RefreshBusyEdges(_roadGraph);
        _lastODRecalcHour = _gameHour;

        // Refresh selected zone overlay
        if (_selectedZoneId != -1)
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
        }
        
        // Recalculate macro traffic flows when 30 game minutes elapse
        if (Mathf.Abs(_gameHour - _lastODRecalcHour) >= _odRecalcInterval)
        {
            RecalculateODMatrix();
        }
        
        // Update simulation sub-systems
        _trafficLights.Update(dt, _gameSpeed);
        _carTrafficManager.Update(dt, _gameSpeed, _roadGraph, _trafficLights);
        _transitManager.Update(dt, _gameSpeed, _roadGraph, _trafficLights);
        
        // Calculate transit ridership
        float transitRidership = 0f;
        foreach (var v in _transitManager.Vehicles) transitRidership += v.Passengers;
        
        // Update HUD
        _gameUI.UpdateTime(_gameHour, _gameDay);
        _gameUI.UpdateStats(
            _grid.TotalPopulation(),
            _odMatrix.TotalTrips,
            _trafficEngine.GetAverageCongestion(_roadGraph),
            transitRidership,
            _transitManager.GetTransitCoverage(_grid),
            ODMatrix.GetDemandMultiplier(_gameHour),
            ODMatrix.GetDirectionalBias(_gameHour)
        );
        
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
            else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                HandleMapClick();
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                HandleRightClick();
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_isDragging)
            {
                _camera.Position -= mm.Relative / _camera.Zoom;
            }
            else if (_currentMode != InteractionMode.Inspect && _pendingStartZoneId != -1)
            {
                UpdateHoverTarget();
            }
        }
    }

    private void UpdateHoverTarget()
    {
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
        if (_currentMode == InteractionMode.Inspect)
        {
            ClearInspectSelection();
        }
        else if (_currentMode == InteractionMode.BuildRoad)
        {
            CancelPendingOperation();
            _gameUI.SetToolHint("🛣️ [Build Road] Click first cell, then adjacent cell to build road.", new Color(0.4f, 0.95f, 0.6f));
        }
        else if (_currentMode == InteractionMode.Demolish)
        {
            CancelPendingOperation();
            _gameUI.SetToolHint("💥 [Demolish] Click first cell, then adjacent connected cell to demolish road.", new Color(1f, 0.5f, 0.4f));
        }
    }

    private void HandleMapClick()
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        int gx = Mathf.FloorToInt(mouseWorld.X / _grid.CellSize);
        int gy = Mathf.FloorToInt(mouseWorld.Y / _grid.CellSize);

        if (_currentMode == InteractionMode.Inspect)
        {
            HandleInspectClick(gx, gy);
        }
        else if (_currentMode == InteractionMode.BuildRoad)
        {
            HandleBuildRoadClick(gx, gy);
        }
        else if (_currentMode == InteractionMode.Demolish)
        {
            HandleDemolishClick(gx, gy);
        }
    }

    private void HandleInspectClick(int gx, int gy)
    {
        if (gx >= 0 && gx < _grid.Width && gy >= 0 && gy < _grid.Height)
        {
            int zoneId = _grid.GetZoneId(gx, gy);
            var zone = _grid.GetZone(zoneId);

            if (zone != null && zone.Type != ZoneType.Empty)
            {
                _selectedZoneId = zoneId;
                _cityRenderer.SetSelectedZone(zoneId);
                _commuteOverlay.SelectZone(zoneId, _distanceMatrix, _roadGraph);
                _gameUI.ShowZoneInfographics(zone, _grid, _odMatrix, _distanceMatrix);
                return;
            }
        }

        // Clicked outside or empty zone: reset to overview
        ClearInspectSelection();
    }

    private void HandleBuildRoadClick(int gx, int gy)
    {
        if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
        {
            return;
        }

        int zoneId = _grid.GetZoneId(gx, gy);
        var zone = _grid.GetZone(zoneId);

        if (zone == null || zone.Type == ZoneType.Empty)
        {
            _gameUI.SetToolHint("⚠️ Cannot build road on empty terrain. Click an active zone.", Colors.Coral);
            return;
        }

        if (_pendingStartZoneId == -1)
        {
            // First step: select start zone
            _pendingStartZoneId = zone.Id;
            var validTargets = GetValidAdjacentTargets(InteractionMode.BuildRoad, _pendingStartZoneId);
            _previewRenderer.SetPreview(InteractionMode.BuildRoad, _pendingStartZoneId, validTargets);
            _gameUI.SetToolHint($"🛣️ [Build Road] Selected start cell at ({gx}, {gy}). Click an adjacent cell to build road (Right-click to cancel).", new Color(0.2f, 1f, 0.7f));
        }
        else
        {
            // Second step: click adjacent target zone
            if (zone.Id == _pendingStartZoneId)
            {
                // Clicked same cell: cancel/deselect
                CancelPendingOperation();
                _gameUI.SetToolHint("🛣️ [Build Road] Selection cancelled. Click first cell to build road.", new Color(0.4f, 0.95f, 0.6f));
                return;
            }

            var startZone = _grid.GetZone(_pendingStartZoneId);
            int manhattanDist = Mathf.Abs(startZone.GridPos.X - zone.GridPos.X) + Mathf.Abs(startZone.GridPos.Y - zone.GridPos.Y);

            if (manhattanDist != 1)
            {
                // Non-adjacent cell: update selection to clicked cell as new start
                _pendingStartZoneId = zone.Id;
                var validTargets = GetValidAdjacentTargets(InteractionMode.BuildRoad, _pendingStartZoneId);
                _previewRenderer.SetPreview(InteractionMode.BuildRoad, _pendingStartZoneId, validTargets);
                _gameUI.SetToolHint($"⚠️ Cells must be adjacent! New start cell set at ({gx}, {gy}). Click an adjacent cell.", Colors.Yellow);
                return;
            }

            // Check if road already exists
            if (_roadGraph.HasEdge(_pendingStartZoneId, zone.Id) || _roadGraph.HasEdge(zone.Id, _pendingStartZoneId))
            {
                // Road already exists: update start cell to clicked cell
                _pendingStartZoneId = zone.Id;
                var validTargets = GetValidAdjacentTargets(InteractionMode.BuildRoad, _pendingStartZoneId);
                _previewRenderer.SetPreview(InteractionMode.BuildRoad, _pendingStartZoneId, validTargets);
                _gameUI.SetToolHint($"⚠️ Road already exists between these cells! New start cell set at ({gx}, {gy}).", Colors.Yellow);
                return;
            }

            // Create road segment in both directions
            bool added = _roadGraph.AddRoadSegment(_pendingStartZoneId, zone.Id);
            if (added)
            {
                _distanceMatrix = _roadGraph.RebuildAfterTopologyChange(_grid.ZoneCount);
                _trafficLights.BuildIntersections(_roadGraph);
                RecalculateODMatrix();
                _roadRenderer.Refresh();
                _cityRenderer.Refresh();
                _vehicleRenderer.QueueRedraw();

                int sx = startZone.GridPos.X;
                int sy = startZone.GridPos.Y;
                CancelPendingOperation();
                _gameUI.SetToolHint($"✅ Road built between ({sx}, {sy}) and ({gx}, {gy})! Click to build another.", new Color(0.3f, 1f, 0.5f));
            }
            else
            {
                CancelPendingOperation();
                _gameUI.SetToolHint("⚠️ Failed to build road segment.", Colors.Coral);
            }
        }
    }

    private void HandleDemolishClick(int gx, int gy)
    {
        if (gx < 0 || gx >= _grid.Width || gy < 0 || gy >= _grid.Height)
        {
            return;
        }

        int zoneId = _grid.GetZoneId(gx, gy);
        var zone = _grid.GetZone(zoneId);

        if (zone == null || zone.Type == ZoneType.Empty)
        {
            _gameUI.SetToolHint("⚠️ Cannot demolish on empty terrain. Click an active zone.", Colors.Coral);
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
                _trafficLights.BuildIntersections(_roadGraph);
                _carTrafficManager.HandleInvalidatedEdges(_roadGraph);
                RecalculateODMatrix();
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

    private List<int> GetValidAdjacentTargets(InteractionMode mode, int zoneId)
    {
        var list = new List<int>();
        if (zoneId < 0 || zoneId >= _grid.ZoneCount) return list;

        var zone = _grid.GetZone(zoneId);
        if (zone == null || zone.Type == ZoneType.Empty) return list;

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
            if (nZone == null || nZone.Type == ZoneType.Empty) continue;

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
}
