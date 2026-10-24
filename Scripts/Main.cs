using Godot;
using System.Collections.Generic;
using CitySim.Simulation;
using CitySim.Simulation.Commands;
using CitySim.Rendering;
using CitySim.Tools;
using CitySim.UI;

namespace CitySim;

/// <summary>
/// Godot entry point of the simulation scene. Owns the <see cref="SimulationWorld"/>, the renderers,
/// the camera and the HUD; forwards map input to the active <see cref="ITool"/> and refreshes
/// rendering from world events. Simulation and editing logic live in the world, commands and tools.
/// </summary>
public partial class Main : Node2D, IToolHost
{
    private const float CameraPanSpeed = 600f;
    private const float CameraZoomStep = 1.15f;
    private const float CameraMinZoom = 0.3f;
    private const float CameraMaxZoom = 4.0f;
    private const float InspectParcelPickRadius = 24f;

    private SimulationWorld _world;

    private CityRenderer _cityRenderer;
    private RoadRenderer _roadRenderer;
    private VehicleRenderer _vehicleRenderer;
    private CommuteOverlayRenderer _commuteOverlay;
    private ToolPreviewRenderer _previewRenderer;
    private Camera2D _camera;
    private GameUI _gameUI;

    private bool _heatmapEnabled = false;

    private bool _isDragging = false;
    private bool _isZoningDragging = false;
    private int _selectedZoneId = -1;
    private int _selectedParcelId = -1;
    private InteractionMode _currentMode = InteractionMode.Inspect;

    private readonly Dictionary<InteractionMode, ITool> _tools = new Dictionary<InteractionMode, ITool>();
    private TransitRouteTool _transitRouteTool;

    /// <summary>
    /// Gets or sets the scenario to load when entering the simulation scene.
    /// </summary>
    public static ScenarioType SelectedScenario { get; set; } = ScenarioType.Tutorial;

    /// <summary>
    /// Compatibility property for tutorial city loading flag.
    /// </summary>
    public static bool LoadTutorialMode
    {
        get => SelectedScenario == ScenarioType.Tutorial;
        set => SelectedScenario = value ? ScenarioType.Tutorial : ScenarioType.Sandbox;
    }

    /// <summary>Tool for the current mode, or null in Inspect mode.</summary>
    private ITool ActiveTool => _tools.TryGetValue(_currentMode, out var tool) ? tool : null;

    public override void _Ready()
    {
        _world = SimulationWorld.Create(SelectedScenario);
        _world.UseBackgroundMacroModel = true;

        _camera = new Camera2D();
        _camera.Position = new Vector2(672f, 672f);
        _camera.Zoom = new Vector2(0.85f, 0.85f);
        AddChild(_camera);

        _cityRenderer = new CityRenderer();
        AddChild(_cityRenderer);
        _cityRenderer.Initialize(_world.Grid, _world.Parcels);

        _roadRenderer = new RoadRenderer();
        AddChild(_roadRenderer);
        _roadRenderer.Initialize(_world.RoadGraph, _world.TrafficLights);

        _commuteOverlay = new CommuteOverlayRenderer();
        AddChild(_commuteOverlay);
        _commuteOverlay.Initialize(_world.Grid, _world.Taz, _world.OdMatrix, _world.RoadGraph, _world.Parcels);

        _vehicleRenderer = new VehicleRenderer();
        AddChild(_vehicleRenderer);
        _vehicleRenderer.Initialize(_world.Transit, _world.Cars, _world.Pedestrians, _world.RoadGraph, _world.Parcels);

        _previewRenderer = new ToolPreviewRenderer();
        AddChild(_previewRenderer);
        _previewRenderer.Initialize(_world.Grid, _world.RoadGraph);
        _previewRenderer.SetParcelManager(_world.Parcels);

        _gameUI = new GameUI();
        AddChild(_gameUI);
        _gameUI.SetTransitManager(_world.Transit);
        _gameUI.SetEconomyManager(_world.Economy);

        _gameUI.SpeedChanged += (speed) => _world.Clock.Speed = speed;
        _gameUI.HeatmapToggled += (enabled) =>
        {
            _heatmapEnabled = enabled;
            _roadRenderer.HeatmapEnabled = enabled;
            _roadRenderer.Refresh();
        };

        _gameUI.CommuteInfographicsToggled += (enabled) =>
        {
            _commuteOverlay.Visible = enabled;
            _roadRenderer.CommuteInfographicsEnabled = enabled;
            _roadRenderer.Refresh();
        };

        _gameUI.TransitRoutesToggled += (enabled) =>
        {
            _vehicleRenderer.ShowTransitRoutes = enabled;
            _vehicleRenderer.QueueRedraw();
        };

        CreateTools();

        _gameUI.ModeChanged += OnInteractionModeChanged;
        _gameUI.RouteLaunchRequested += (routeName, color) => _transitRouteTool.Launch(routeName, color);
        _gameUI.RouteCancelRequested += OnRouteCancelRequested;
        _gameUI.RouteDeleted += OnRouteDeleted;

        _world.MacroModelRecalculated += OnMacroModelRecalculated;
        _world.TopologyApplied += OnTopologyApplied;
        _world.LandUseChanged += OnLandUseChanged;

        _world.RecalculateMacroModel();
        ShowCityOverview();
    }

    public override void _ExitTree()
    {
        _world?.Dispose();
    }

    private void CreateTools()
    {
        _transitRouteTool = new TransitRouteTool(this);
        _tools[InteractionMode.BuildRoad] = new RoadBuildTool(this, curved: false);
        _tools[InteractionMode.BuildCurvedRoad] = new RoadBuildTool(this, curved: true);
        _tools[InteractionMode.Demolish] = new DemolishTool(this);
        _tools[InteractionMode.ToggleBusLane] = new BusLaneTool(this);
        _tools[InteractionMode.CreateTransitRoute] = _transitRouteTool;
        foreach (var mode in new[] { InteractionMode.ZoneResidential, InteractionMode.ZoneCommercial, InteractionMode.ZoneIndustrial, InteractionMode.Dezone })
        {
            _tools[mode] = new ZoningTool(this, mode);
        }
    }

    #region IToolHost

    SimulationWorld IToolHost.World => _world;

    IToolPreview IToolHost.Preview => _previewRenderer;

    Color IToolHost.RouteDesignerColor => _gameUI.CurrentRouteDesignerColor;

    /// <summary>
    /// Executes a player edit on the world. Topology edits refresh the map through
    /// <see cref="OnTopologyApplied"/>; other applied edits refresh it here. The caller shows the
    /// result with <see cref="ShowResult"/> after its own UI follow-ups.
    /// </summary>
    public CommandResult Execute(IWorldCommand command)
    {
        var result = command.Execute(_world);
        if (result.IsApplied && result.Topology == null)
        {
            RefreshMapLayers();
        }
        return result;
    }

    public void ShowResult(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            _gameUI.SetToolHint(result.Message, GetToneColor(result.Tone));
        }
    }

    void IToolHost.SetHint(string text, Color color) => _gameUI.SetToolHint(text, color);

    void IToolHost.ReturnToInspect() => _gameUI.SetInteractionMode(InteractionMode.Inspect);

    void IToolHost.ReleaseZoneSelection(int zoneId)
    {
        if (_selectedZoneId == zoneId)
        {
            ClearInspectSelection();
        }
    }

    public void ShowCityOverview() => _gameUI.ShowCityOverview(_world.Grid, _world.OdMatrix, _world.Parcels);

    void IToolHost.UpdateRouteDesignerStatus(int stopsCount, int pathNodeCount, bool isLoop) =>
        _gameUI?.UpdateRouteDesignerStatus(stopsCount, pathNodeCount, isLoop);

    void IToolHost.UpdateTransitRoutesView() => _gameUI.UpdateTransitRoutesView();

    #endregion

    private static Color GetToneColor(CommandTone tone) => tone switch
    {
        CommandTone.Success    => new Color(0.3f, 1f, 0.5f),
        CommandTone.Demolition => new Color(1f, 0.4f, 0.3f),
        CommandTone.Clearing   => new Color(1f, 0.7f, 0.4f),
        CommandTone.Caution    => new Color(1f, 0.8f, 0.4f),
        CommandTone.Warning    => Colors.Yellow,
        CommandTone.Error      => Colors.Coral,
        _                      => Colors.LightGray
    };

    private ToolInput CurrentToolInput() => new ToolInput(GetGlobalMousePosition(), Input.IsKeyPressed(Key.Shift));

    private void OnInteractionModeChanged(int modeInt)
    {
        _currentMode = (InteractionMode)modeInt;
        ResetAllTools();
        _previewRenderer.SetMode(_currentMode);

        if (_currentMode != InteractionMode.Inspect)
        {
            ClearInspectSelection();
            ActiveTool?.OnHover(CurrentToolInput());
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
        _commuteOverlay.SelectZone(-1, _world.RoadGraph);
        _commuteOverlay.SelectParcel(-1, _world.Parcels, _world.RoadGraph);
        ShowCityOverview();
    }

    /// <summary>Discards in-progress operations of every tool (mode switch, cancel).</summary>
    private void ResetAllTools()
    {
        foreach (var tool in _tools.Values)
        {
            tool.Reset();
        }
        _cityRenderer?.Refresh();
    }

    private void RefreshMapLayers()
    {
        _roadRenderer.Refresh();
        _cityRenderer.Refresh();
        _vehicleRenderer.QueueRedraw();
    }

    private void OnTopologyApplied(TopologyChangeResult result)
    {
        RefreshMapLayers();
        if (result.Transit.HasChanges)
        {
            _gameUI.UpdateTransitRoutesView();
        }
    }

    private void OnMacroModelRecalculated()
    {
        _commuteOverlay.SetMacroModel(_world.Taz, _world.OdMatrix);
        _roadRenderer.UpdateMaxVolume(_world.TrafficEngine.GetMaxVolume(_world.RoadGraph));

        // Refresh selected parcel or zone overlay
        if (TryGetSelectedParcel(out var selectedParcel))
        {
            _commuteOverlay.SelectParcel(_selectedParcelId, _world.Parcels, _world.RoadGraph);
            _gameUI.ShowParcelInfographics(selectedParcel, _world.Parcels, _world.Taz, _world.OdMatrix, _world.Grid);
        }
        else if (_selectedZoneId != -1)
        {
            _commuteOverlay.SelectZone(_selectedZoneId, _world.RoadGraph);
            ShowSelectedZoneInfographics();
        }
        else
        {
            // Results arrive asynchronously in background mode; keep the overview panel current
            ShowCityOverview();
        }
    }

    private void OnLandUseChanged(LutiGrowthResult growth)
    {
        if (growth.DensityChanged)
        {
            _cityRenderer?.Refresh();
        }

        if (growth.LandUseChanged)
        {
            if (TryGetSelectedParcel(out var selectedParcel))
            {
                _gameUI.ShowParcelInfographics(selectedParcel, _world.Parcels, _world.Taz, _world.OdMatrix, _world.Grid);
            }
            else if (_selectedZoneId != -1)
            {
                ShowSelectedZoneInfographics();
            }
        }
    }

    private bool TryGetSelectedParcel(out RoadsideParcel parcel)
    {
        parcel = null;
        return _selectedParcelId != -1 && _world.Parcels.ParcelMap.TryGetValue(_selectedParcelId, out parcel);
    }

    private void ShowSelectedZoneInfographics()
    {
        var zone = _world.Grid.GetZone(_selectedZoneId);
        _gameUI.ShowZoneInfographics(zone, _world.Grid, _world.Taz, _world.OdMatrix);
    }

    public override void _Process(double delta)
    {
        _world.Tick((float)delta);

        // Update HUD from the cached macro-tick snapshot (full scans happen in RecalculateMacroModel)
        var stats = _world.Stats;
        float hour = _world.Clock.Hour;
        _gameUI.UpdateTime(hour, _world.Clock.Day);
        _gameUI.UpdateStats(
            stats.Population,
            stats.TotalTrips,
            stats.AverageCongestion,
            _world.TransitRidership,
            stats.TransitCoverage,
            DemandCurve.GetDemandMultiplier(hour),
            DemandCurve.GetDirectionalBias(hour),
            stats.Jobs
        );

        _gameUI.UpdateEconomy(_world.Economy.Money, _world.Economy.LastDelta);

        HandleCameraPan(delta);
    }

    private void HandleCameraPan(double delta)
    {
        float panSpeed = CameraPanSpeed / _camera.Zoom.X;
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
        var minZoom = new Vector2(CameraMinZoom, CameraMinZoom);
        var maxZoom = new Vector2(CameraMaxZoom, CameraMaxZoom);

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                _camera.Zoom = (_camera.Zoom * CameraZoomStep).Clamp(minZoom, maxZoom);
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                _camera.Zoom = (_camera.Zoom / CameraZoomStep).Clamp(minZoom, maxZoom);
            }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                _isDragging = mb.Pressed;
            }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isZoningDragging = _currentMode.IsZoningMode();
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
            else if (ActiveTool != null)
            {
                ActiveTool.OnHover(CurrentToolInput());
                if (_isZoningDragging && _currentMode.IsZoningMode())
                {
                    HandleMapClick();
                }
            }
        }
        else if (@event is InputEventKey)
        {
            // Shift changes angle snap / whole-side zoning previews
            if (_currentMode.IsRoadBuildMode() || _currentMode.IsZoningMode())
            {
                ActiveTool?.OnHover(CurrentToolInput());
            }
        }
    }

    private void HandleRightClick()
    {
        if (ActiveTool != null && ActiveTool.OnCancel())
        {
            _cityRenderer?.Refresh();
            return;
        }

        if (_currentMode == InteractionMode.Inspect)
        {
            ResetAllTools();
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
        if (ActiveTool != null)
        {
            ActiveTool.OnPrimaryClick(CurrentToolInput());
            return;
        }

        HandleInspectClick(GetGlobalMousePosition());
    }

    private void HandleInspectClick(Vector2 mouseWorld)
    {
        var parcels = _world.Parcels;

        // 1. Check roadside ribbon parcels first (higher precision click on continuous lot)
        RoadsideParcel clickedParcel = parcels.GetParcelAt(mouseWorld) ?? parcels.FindClosestParcel(mouseWorld, InspectParcelPickRadius);

        if (clickedParcel != null && clickedParcel.ZoneType != ZoneType.Empty)
        {
            _selectedZoneId = -1;
            _selectedParcelId = clickedParcel.Id;
            _cityRenderer.SetSelectedZone(-1);
            _cityRenderer.SetSelectedParcel(clickedParcel.Id);
            _commuteOverlay.SelectParcel(clickedParcel.Id, parcels, _world.RoadGraph);
            _gameUI.ShowParcelInfographics(clickedParcel, parcels, _world.Taz, _world.OdMatrix, _world.Grid);
            return;
        }

        // 2. Fallback to legacy grid zones
        int zoneId = ToolHelpers.GetCellAt(_world.Grid, mouseWorld);
        var zone = zoneId >= 0 ? _world.Grid.GetZone(zoneId) : null;
        if (zone != null && zone.Type != ZoneType.Empty)
        {
            _selectedParcelId = -1;
            _selectedZoneId = zoneId;
            _cityRenderer.SetSelectedParcel(-1);
            _cityRenderer.SetSelectedZone(zoneId);
            _commuteOverlay.SelectZone(zoneId, _world.RoadGraph);
            ShowSelectedZoneInfographics();
            return;
        }

        // Clicked outside or empty zone/parcel: reset to overview
        ClearInspectSelection();
    }

    private void OnRouteCancelRequested()
    {
        _transitRouteTool.Reset();
        _cityRenderer.Refresh();
    }

    private void OnRouteDeleted(int routeId)
    {
        var result = Execute(new DeleteTransitRouteCommand(routeId));
        if (result.IsApplied)
        {
            _gameUI.UpdateTransitRoutesView();
        }
        ShowResult(result);
    }
}
