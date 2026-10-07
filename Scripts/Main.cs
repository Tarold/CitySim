using Godot;
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
        
        RecalculateODMatrix();
        _gameUI.ShowCityOverview(_grid, _odMatrix);
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
                // Clear selection on right click
                _selectedZoneId = -1;
                _cityRenderer.SetSelectedZone(-1);
                _commuteOverlay.SelectZone(-1, _distanceMatrix, _roadGraph);
                _gameUI.ShowCityOverview(_grid, _odMatrix);
            }
        }
        else if (@event is InputEventMouseMotion mm && _isDragging)
        {
            _camera.Position -= mm.Relative / _camera.Zoom;
        }
    }

    private void HandleMapClick()
    {
        Vector2 mouseWorld = GetGlobalMousePosition();
        int gx = Mathf.FloorToInt(mouseWorld.X / _grid.CellSize);
        int gy = Mathf.FloorToInt(mouseWorld.Y / _grid.CellSize);

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
        _selectedZoneId = -1;
        _cityRenderer.SetSelectedZone(-1);
        _commuteOverlay.SelectZone(-1, _distanceMatrix, _roadGraph);
        _gameUI.ShowCityOverview(_grid, _odMatrix);
    }
}
