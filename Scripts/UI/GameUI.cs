using Godot;
using System;
using System.Collections.Generic;
using CitySim.Simulation;

namespace CitySim.UI;

/// <summary>
/// Determines how mouse clicks on the game map are interpreted.
/// </summary>
public enum InteractionMode
{
    /// <summary>Click zones to inspect commuters, mode split, and analytics.</summary>
    Inspect,
    /// <summary>Click or drag between adjacent grid cells to construct road segments.</summary>
    BuildRoad,
    /// <summary>Click an existing road connection to tear it down.</summary>
    Demolish,
    /// <summary>Designate empty or existing cells as Residential district.</summary>
    ZoneResidential,
    /// <summary>Designate empty or existing cells as Commercial district.</summary>
    ZoneCommercial,
    /// <summary>Designate empty or existing cells as Industrial district.</summary>
    ZoneIndustrial,
    /// <summary>Clear existing zoned cell back to empty terrain.</summary>
    Dezone,
    /// <summary>Design and construct a new public transit route with custom stops.</summary>
    CreateTransitRoute
}

public partial class GameUI : CanvasLayer
{
    [Signal] public delegate void SpeedChangedEventHandler(float speed);
    [Signal] public delegate void HeatmapToggledEventHandler(bool enabled);
    [Signal] public delegate void CommuteInfographicsToggledEventHandler(bool enabled);
    [Signal] public delegate void ModeChangedEventHandler(int mode);
    [Signal] public delegate void RouteLaunchRequestedEventHandler(string routeName, Color routeColor);
    [Signal] public delegate void RouteCancelRequestedEventHandler();
    [Signal] public delegate void RouteDeletedEventHandler(int routeId);

    /// <summary>The currently active interaction mode.</summary>
    public InteractionMode CurrentMode { get; private set; } = InteractionMode.Inspect;

    public static readonly (string Name, Color Color)[] RouteColorPalette = new[]
    {
        ("🟣 Purple", new Color(0.65f, 0.25f, 0.95f)),
        ("🟠 Orange", new Color(1.0f, 0.55f, 0.1f)),
        ("🔷 Cyan", new Color(0.1f, 0.85f, 0.95f)),
        ("🌸 Magenta", new Color(0.95f, 0.2f, 0.65f)),
        ("🟡 Amber", new Color(1.0f, 0.8f, 0.15f)),
        ("🟢 Lime", new Color(0.45f, 0.85f, 0.2f))
    };

    private Button _inspectBtn;
    private Button _buildRoadBtn;
    private Button _demolishBtn;
    private Button _zoneResBtn;
    private Button _zoneComBtn;
    private Button _zoneIndBtn;
    private Button _dezoneBtn;
    private Button _createRouteBtn;
    
    private Label _timeLabel;
    private Label _popLabel;
    private Label _tripsLabel;
    private Label _congLabel;
    
    private Label _ridershipLabel;
    private Label _coverageLabel;
    private Label _demandLabel;
    private Label _biasLabel;

    private PanelContainer _toolHintPanel;
    private Label _toolHintLabel;

    // Transit Route Designer Panel & Controls
    private PanelContainer _routeDesignerPanel;
    private LineEdit _routeNameEdit;
    private OptionButton _routeColorOption;
    private Label _routeDesignerStatsLabel;
    private Button _launchRouteBtn;
    private Button _cancelRouteBtn;

    // Infographic side panel
    private PanelContainer _infoPanel;
    private Button _tabCommuteBtn;
    private Button _tabTransitBtn;
    private VBoxContainer _commuteViewContainer;
    private VBoxContainer _transitViewContainer;

    // Commute inspector labels
    private Label _infoTitleLabel;
    private Label _infoStat1Label;
    private Label _infoStat2Label;
    private Label _infoWorkplaceBreakdown;
    private Label _infoModeSplit;
    private Label _infoTopDestinations;
    private Label _infoHint;

    // Dynamic Transit Routes Analytics
    private Label _transitOverviewLabel;
    private VBoxContainer _routesCardContainer;
    private Label _transitFinancialSummaryLabel;

    private TransitManager _cachedTransitManager;

    public Color CurrentRouteDesignerColor
    {
        get
        {
            if (_routeColorOption != null && _routeColorOption.Selected >= 0 && _routeColorOption.Selected < RouteColorPalette.Length)
                return RouteColorPalette[_routeColorOption.Selected].Color;
            return RouteColorPalette[0].Color;
        }
    }

    public string CurrentRouteDesignerName => _routeNameEdit?.Text ?? "Line 4 - Express";

    public bool IsInfographicsVisible => _infoPanel.Visible;

    public override void _Ready()
    {
        var panelTheme = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.07f, 0.90f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.3f, 0.35f, 0.45f, 0.5f)
        };

        // =========================================================================
        // TOP HUD BAR
        // =========================================================================
        var topPanel = new PanelContainer { AnchorRight = 1, CustomMinimumSize = new Vector2(0, 52) };
        topPanel.AddThemeStyleboxOverride("panel", panelTheme);
        AddChild(topPanel);

        var topHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        topHBox.AddThemeConstantOverride("separation", 10);
        topPanel.AddChild(topHBox);

        _timeLabel = new Label { Text = "Day 1 | 06:30", ThemeTypeVariation = "HeaderLarge" };
        _popLabel = new Label { Text = "Pop: 1,008,000" };
        _tripsLabel = new Label { Text = "Trips: 0/h" };
        _congLabel = new Label { Text = "Congestion: 0%" };

        topHBox.AddChild(_timeLabel);
        topHBox.AddChild(_popLabel);
        topHBox.AddChild(_tripsLabel);
        topHBox.AddChild(_congLabel);

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        topHBox.AddChild(spacer);

        // Speed buttons
        float[] speeds = { 0f, 1f, 3f, 10f, 50f };
        string[] speedLabels = { "⏸", "▶", "▶▶", "▶▶▶", "⏩" };
        for (int i = 0; i < speeds.Length; i++)
        {
            float s = speeds[i];
            var btn = new Button { Text = speedLabels[i], CustomMinimumSize = new Vector2(40, 36) };
            btn.Pressed += () => EmitSignal(SignalName.SpeedChanged, s);
            topHBox.AddChild(btn);
        }

        var heatmapBtn = new Button { Text = "🔥 Heatmap", ToggleMode = true, CustomMinimumSize = new Vector2(100, 36) };
        heatmapBtn.Toggled += (bool pressed) => EmitSignal(SignalName.HeatmapToggled, pressed);
        topHBox.AddChild(heatmapBtn);

        var infoBtn = new Button { Text = "📊 Аналітика", ToggleMode = true, ButtonPressed = true, CustomMinimumSize = new Vector2(110, 36) };
        infoBtn.Toggled += (bool pressed) => 
        {
            _infoPanel.Visible = pressed;
            EmitSignal(SignalName.CommuteInfographicsToggled, pressed);
        };
        topHBox.AddChild(infoBtn);

        // ---- Interaction Mode Buttons (mutually exclusive via ButtonGroup) ----
        var modeSpacer = new VSeparator();
        topHBox.AddChild(modeSpacer);

        var modeGroup = new ButtonGroup();

        _inspectBtn = new Button
        {
            Text = "🔍 Inspect",
            TooltipText = "Inspect Zone Analytics and Commute Paths",
            ToggleMode = true,
            ButtonPressed = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(85, 36)
        };
        _inspectBtn.Pressed += () => SetInteractionMode(InteractionMode.Inspect);
        topHBox.AddChild(_inspectBtn);

        _zoneResBtn = new Button
        {
            Text = "🏡 Res",
            TooltipText = "Zone Residential District (Green)",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(72, 36)
        };
        _zoneResBtn.Pressed += () => SetInteractionMode(InteractionMode.ZoneResidential);
        topHBox.AddChild(_zoneResBtn);

        _zoneComBtn = new Button
        {
            Text = "🏢 Com",
            TooltipText = "Zone Commercial District (Blue)",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(72, 36)
        };
        _zoneComBtn.Pressed += () => SetInteractionMode(InteractionMode.ZoneCommercial);
        topHBox.AddChild(_zoneComBtn);

        _zoneIndBtn = new Button
        {
            Text = "🏭 Ind",
            TooltipText = "Zone Industrial District (Amber)",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(72, 36)
        };
        _zoneIndBtn.Pressed += () => SetInteractionMode(InteractionMode.ZoneIndustrial);
        topHBox.AddChild(_zoneIndBtn);

        _dezoneBtn = new Button
        {
            Text = "🧹 Dezone",
            TooltipText = "Clear / Dezone Grid Tile to Empty Terrain",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(85, 36)
        };
        _dezoneBtn.Pressed += () => SetInteractionMode(InteractionMode.Dezone);
        topHBox.AddChild(_dezoneBtn);

        _buildRoadBtn = new Button
        {
            Text = "🛣️ Road",
            TooltipText = "Construct Road Between Adjacent Cells",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(75, 36)
        };
        _buildRoadBtn.Pressed += () => SetInteractionMode(InteractionMode.BuildRoad);
        topHBox.AddChild(_buildRoadBtn);

        _demolishBtn = new Button
        {
            Text = "💥 Demolish",
            TooltipText = "Demolish Road Connection",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(92, 36)
        };
        _demolishBtn.Pressed += () => SetInteractionMode(InteractionMode.Demolish);
        topHBox.AddChild(_demolishBtn);

        _createRouteBtn = new Button
        {
            Text = "🚌 New Route",
            TooltipText = "Design and Launch Custom Public Transit Route",
            ToggleMode = true,
            ButtonGroup = modeGroup,
            CustomMinimumSize = new Vector2(110, 36)
        };
        _createRouteBtn.Pressed += () => SetInteractionMode(InteractionMode.CreateTransitRoute);
        topHBox.AddChild(_createRouteBtn);

        // Tool Instructions Banner (Centered beneath Top Panel)
        _toolHintPanel = new PanelContainer();
        _toolHintPanel.AnchorLeft = 0.5f;
        _toolHintPanel.AnchorRight = 0.5f;
        _toolHintPanel.OffsetLeft = -320;
        _toolHintPanel.OffsetRight = 320;
        _toolHintPanel.OffsetTop = 58;
        _toolHintPanel.OffsetBottom = 92;

        var hintStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.90f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.25f, 0.35f, 0.50f, 0.6f)
        };
        _toolHintPanel.AddThemeStyleboxOverride("panel", hintStyle);
        AddChild(_toolHintPanel);

        _toolHintLabel = new Label
        {
            Text = "🔍 [Inspect] Click a zone on the map to view commute analytics.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _toolHintLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.8f, 1f));
        _toolHintPanel.AddChild(_toolHintLabel);

        // Dedicated Transit Route Designer Toolbar
        _routeDesignerPanel = new PanelContainer();
        _routeDesignerPanel.AnchorLeft = 0.5f;
        _routeDesignerPanel.AnchorRight = 0.5f;
        _routeDesignerPanel.OffsetLeft = -400;
        _routeDesignerPanel.OffsetRight = 400;
        _routeDesignerPanel.OffsetTop = 96;
        _routeDesignerPanel.OffsetBottom = 142;
        _routeDesignerPanel.Visible = false;

        var designerStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.12f, 0.95f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.4f, 0.5f, 0.8f, 0.8f)
        };
        _routeDesignerPanel.AddThemeStyleboxOverride("panel", designerStyle);
        AddChild(_routeDesignerPanel);

        var designerHBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        designerHBox.AddThemeConstantOverride("separation", 8);
        _routeDesignerPanel.AddChild(designerHBox);

        designerHBox.AddChild(new Label { Text = "🚌 Маршрут:" });

        _routeNameEdit = new LineEdit
        {
            Text = "Line 4 - Express",
            CustomMinimumSize = new Vector2(130, 30)
        };
        designerHBox.AddChild(_routeNameEdit);

        _routeColorOption = new OptionButton { CustomMinimumSize = new Vector2(110, 30) };
        for (int i = 0; i < RouteColorPalette.Length; i++)
        {
            _routeColorOption.AddItem(RouteColorPalette[i].Name, i);
        }
        _routeColorOption.Selected = 0;
        designerHBox.AddChild(_routeColorOption);

        _routeDesignerStatsLabel = new Label
        {
            Text = "Зупинок: 0 | Вузлів: 0"
        };
        _routeDesignerStatsLabel.AddThemeColorOverride("font_color", new Color(0.3f, 0.9f, 1f));
        designerHBox.AddChild(_routeDesignerStatsLabel);

        _launchRouteBtn = new Button
        {
            Text = "🚀 Запустити маршрут",
            Disabled = true,
            CustomMinimumSize = new Vector2(150, 30)
        };
        _launchRouteBtn.Pressed += () => EmitSignal(SignalName.RouteLaunchRequested, _routeNameEdit.Text, CurrentRouteDesignerColor);
        designerHBox.AddChild(_launchRouteBtn);

        _cancelRouteBtn = new Button
        {
            Text = "✖ Скасувати",
            CustomMinimumSize = new Vector2(90, 30)
        };
        _cancelRouteBtn.Pressed += () => 
        {
            EmitSignal(SignalName.RouteCancelRequested);
            SetInteractionMode(InteractionMode.Inspect);
        };
        designerHBox.AddChild(_cancelRouteBtn);

        // =========================================================================
        // BOTTOM-LEFT STATS PANEL
        // =========================================================================
        var botPanel = new PanelContainer();
        botPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        botPanel.OffsetLeft = 15;
        botPanel.OffsetBottom = -15;
        botPanel.OffsetTop = -170;
        botPanel.OffsetRight = 240;
        botPanel.AddThemeStyleboxOverride("panel", panelTheme);
        AddChild(botPanel);

        var botVBox = new VBoxContainer();
        botVBox.AddThemeConstantOverride("separation", 6);
        botPanel.AddChild(botVBox);

        _ridershipLabel = new Label { Text = "Transit Ridership: 0" };
        _coverageLabel = new Label { Text = "Transit Coverage: 0%" };
        _demandLabel = new Label { Text = "Demand: 1.0x" };
        _biasLabel = new Label { Text = "Direction: -" };

        botVBox.AddChild(_ridershipLabel);
        botVBox.AddChild(_coverageLabel);
        botVBox.AddChild(_demandLabel);
        botVBox.AddChild(_biasLabel);

        // =========================================================================
        // RIGHT-SIDE ANALYTICS PANEL (TABS: COMMUTE & TRANSIT ROUTES)
        // =========================================================================
        _infoPanel = new PanelContainer();
        _infoPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _infoPanel.OffsetTop = 62;
        _infoPanel.OffsetRight = -15;
        _infoPanel.OffsetLeft = -400;
        _infoPanel.OffsetBottom = 540;
        _infoPanel.AddThemeStyleboxOverride("panel", panelTheme);
        AddChild(_infoPanel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 8);
        _infoPanel.AddChild(mainVBox);

        // Top Navigation Tabs
        var tabHBox = new HBoxContainer();
        tabHBox.AddThemeConstantOverride("separation", 8);
        mainVBox.AddChild(tabHBox);

        _tabCommuteBtn = new Button { Text = "🧭 Шляхи та Робота", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _tabTransitBtn = new Button { Text = "🚌 Обороти Маршрутів", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        _tabCommuteBtn.Pressed += SwitchToCommuteTab;
        _tabTransitBtn.Pressed += SwitchToTransitTab;

        tabHBox.AddChild(_tabCommuteBtn);
        tabHBox.AddChild(_tabTransitBtn);

        var tabSep = new HSeparator();
        mainVBox.AddChild(tabSep);

        // Container 1: Commute Paths View
        _commuteViewContainer = new VBoxContainer();
        _commuteViewContainer.AddThemeConstantOverride("separation", 8);
        mainVBox.AddChild(_commuteViewContainer);

        _infoTitleLabel = new Label { Text = "📊 Шляхи: Дім ➔ Робота", ThemeTypeVariation = "HeaderMedium" };
        _infoTitleLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        _commuteViewContainer.AddChild(_infoTitleLabel);

        _infoStat1Label = new Label { Text = "Населення: 1,008,000" };
        _infoStat2Label = new Label { Text = "Робочі місця: 510,000" };
        _commuteViewContainer.AddChild(_infoStat1Label);
        _commuteViewContainer.AddChild(_infoStat2Label);

        _infoWorkplaceBreakdown = new Label { Text = "🏢 Офіси Центру: 42%\n🏭 Заводи Сходу: 58%", AutowrapMode = TextServer.AutowrapMode.Word };
        _commuteViewContainer.AddChild(_infoWorkplaceBreakdown);

        var sep2 = new HSeparator();
        _commuteViewContainer.AddChild(sep2);

        _infoModeSplit = new Label { Text = "🚗 Авто: 65%   🚌 Громадський транспорт: 35%" };
        _commuteViewContainer.AddChild(_infoModeSplit);

        _infoTopDestinations = new Label 
        { 
            Text = "Головний напрямок поїздок:\nЗахідні спальники ➔ Центр ➔ Східні заводи",
            AutowrapMode = TextServer.AutowrapMode.Word
        };
        _commuteViewContainer.AddChild(_infoTopDestinations);

        _infoHint = new Label 
        { 
            Text = "💡 Клікніть на будь-яку ділянку мапи, щоб побачити точний дорожній шлях робітників!",
            AutowrapMode = TextServer.AutowrapMode.Word
        };
        _infoHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 0.95f));
        _commuteViewContainer.AddChild(_infoHint);

        // Container 2: Municipal Transit Routes Analytics View
        _transitViewContainer = new VBoxContainer();
        _transitViewContainer.AddThemeConstantOverride("separation", 8);
        _transitViewContainer.Visible = false;
        mainVBox.AddChild(_transitViewContainer);

        var transitHeaderHBox = new HBoxContainer();
        transitHeaderHBox.AddThemeConstantOverride("separation", 8);
        _transitViewContainer.AddChild(transitHeaderHBox);

        _transitOverviewLabel = new Label { Text = "🚌 Транспортна Мережа", ThemeTypeVariation = "HeaderMedium" };
        _transitOverviewLabel.AddThemeColorOverride("font_color", new Color(0.3f, 0.8f, 1f));
        _transitOverviewLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        transitHeaderHBox.AddChild(_transitOverviewLabel);

        var newRouteTabBtn = new Button { Text = "➕ Новий", TooltipText = "Побудувати новий маршрут" };
        newRouteTabBtn.Pressed += () => SetInteractionMode(InteractionMode.CreateTransitRoute);
        transitHeaderHBox.AddChild(newRouteTabBtn);

        var routeScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 290),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _transitViewContainer.AddChild(routeScroll);

        _routesCardContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _routesCardContainer.AddThemeConstantOverride("separation", 6);
        routeScroll.AddChild(_routesCardContainer);

        _transitViewContainer.AddChild(new HSeparator());

        _transitFinancialSummaryLabel = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
        _transitViewContainer.AddChild(_transitFinancialSummaryLabel);
    }

    private void SwitchToCommuteTab()
    {
        _commuteViewContainer.Visible = true;
        _transitViewContainer.Visible = false;
    }

    private void SwitchToTransitTab()
    {
        _commuteViewContainer.Visible = false;
        _transitViewContainer.Visible = true;
        UpdateTransitRoutesView();
    }

    public void SetTransitManager(TransitManager tm)
    {
        _cachedTransitManager = tm;
        UpdateTransitRoutesView();
    }

    public void UpdateTransitRoutesView()
    {
        if (_routesCardContainer == null) return;

        // Clear existing cards
        foreach (Node child in _routesCardContainer.GetChildren())
        {
            _routesCardContainer.RemoveChild(child);
            child.QueueFree();
        }

        if (_cachedTransitManager == null || _cachedTransitManager.Routes.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "Немає активних маршрутів.\nНатисніть '➕ Новий', щоб прокласти свій перший маршрут!",
                AutowrapMode = TextServer.AutowrapMode.Word
            };
            emptyLabel.AddThemeColorOverride("font_color", Colors.LightGray);
            _routesCardContainer.AddChild(emptyLabel);

            if (_transitFinancialSummaryLabel != null)
            {
                _transitFinancialSummaryLabel.Text = "🏛️ СУМАРНИЙ БЮДЖЕТ ТРАНСПОРТУ: 0 ₴ (0 маршрутів)";
            }
            return;
        }

        float totalPass = 0f;
        float totalRev = 0f;
        float totalCost = 0f;

        foreach (var r in _cachedTransitManager.Routes)
        {
            totalPass += r.DailyPassengers;
            totalRev += r.DailyRevenue;
            totalCost += r.DailyOperatingCost;

            var card = new PanelContainer();
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.10f, 0.15f, 0.90f),
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 6,
                ContentMarginBottom = 6,
                BorderWidthLeft = 3,
                BorderColor = r.RouteColor
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var cardVBox = new VBoxContainer();
            cardVBox.AddThemeConstantOverride("separation", 4);
            card.AddChild(cardVBox);

            // Card Header: Badge, Name, Type, Delete Button
            var cardHeader = new HBoxContainer();
            cardHeader.AddThemeConstantOverride("separation", 6);
            cardVBox.AddChild(cardHeader);

            var colorBadge = new Label { Text = "●" };
            colorBadge.AddThemeColorOverride("font_color", r.RouteColor);
            cardHeader.AddChild(colorBadge);

            var nameLabel = new Label
            {
                Text = r.Name,
                ThemeTypeVariation = "HeaderSmall",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            nameLabel.AddThemeColorOverride("font_color", Colors.White);
            cardHeader.AddChild(nameLabel);

            var typeBadge = new Label
            {
                Text = r.IsLoop ? "🔄 Кільце" : "↔ Лінія"
            };
            typeBadge.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 0.9f));
            cardHeader.AddChild(typeBadge);

            int routeId = r.Id;
            var delBtn = new Button
            {
                Text = "🗑️",
                TooltipText = $"Видалити маршрут {r.Name}",
                CustomMinimumSize = new Vector2(28, 24)
            };
            delBtn.Pressed += () => EmitSignal(SignalName.RouteDeleted, routeId);
            cardHeader.AddChild(delBtn);

            // Card Body: Metrics
            string sign = r.NetDailyProfit >= 0 ? "+" : "";
            var detailsLabel = new Label
            {
                Text = $"  🔄 Оборот: {r.RoundTripTimeMinutes:F0} хв | Рухомий склад: {r.FleetSize} авт.\n" +
                       $"  👥 Пасажиропотік: {r.DailyPassengers:N0} пас/добу\n" +
                       $"  💰 Дохід: {r.DailyRevenue:N0} ₴ (тариф {r.TicketPrice:F0} ₴)\n" +
                       $"  ⛽ Витрати: {r.DailyOperatingCost:N0} ₴ | Прибуток: {sign}{r.NetDailyProfit:N0} ₴\n" +
                       $"  📊 Заповненість: {r.AverageOccupancy:F0}%",
                AutowrapMode = TextServer.AutowrapMode.Word
            };
            cardVBox.AddChild(detailsLabel);

            _routesCardContainer.AddChild(card);
        }

        float totalProfit = totalRev - totalCost;
        string profitSign = totalProfit >= 0 ? "+" : "";
        _transitFinancialSummaryLabel.Text = 
            $"🏛️ СУМАРНИЙ БЮДЖЕТ ТРАНСПОРТУ ({_cachedTransitManager.Routes.Count} маршрутів):\n" +
            $"  Всього перевезено: {totalPass:N0} пасажирів\n" +
            $"  Денний оборот мережі: {totalRev:N0} ₴\n" +
            $"  Чистий прибуток департаменту: {profitSign}{totalProfit:N0} ₴/добу";
        _transitFinancialSummaryLabel.AddThemeColorOverride(
            "font_color",
            totalProfit >= 0 ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.4f, 0.4f)
        );
    }

    public void ShowZoneInfographics(Zone zone, CityGrid grid, ODMatrix od, float[,] distances)
    {
        SwitchToCommuteTab();

        if (zone == null || zone.Type == ZoneType.Empty)
        {
            ShowCityOverview(grid, od);
            return;
        }

        if (zone.Type == ZoneType.Residential)
        {
            _infoTitleLabel.Text = $"🏡 Житловий квартал ({zone.GridPos.X}, {zone.GridPos.Y})";
            _infoTitleLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));

            int workers = Mathf.RoundToInt(zone.Population * 0.50f);
            _infoStat1Label.Text = $"Населення: {zone.Population:N0} чол.";
            _infoStat2Label.Text = $"Працездатні: {workers:N0} працівників";

            // Destination calculation
            float comTrips = 0f;
            float indTrips = 0f;

            for (int j = 0; j < grid.ZoneCount; j++)
            {
                var dest = grid.GetZone(j);
                if (dest == null) continue;
                float t = od.Trips[zone.Id, j];
                if (dest.Type == ZoneType.Commercial) comTrips += t;
                else if (dest.Type == ZoneType.Industrial) indTrips += t;
            }

            float totalWorkTrips = comTrips + indTrips;
            float comPct = totalWorkTrips > 0f ? (comTrips / totalWorkTrips * 100f) : 40f;
            float indPct = totalWorkTrips > 0f ? (indTrips / totalWorkTrips * 100f) : 60f;

            _infoWorkplaceBreakdown.Text = 
                $"Де працюють мешканці цього кварталу:\n" +
                $"  🏢 Діловий Центр (Офіси): {comPct:F0}%\n" +
                $"  🏭 Східна Промзона (Заводи): {indPct:F0}%";

            _infoModeSplit.Text = $"🚗 На власному авто: 65%    🚌 На автобусі: 35%";
            _infoTopDestinations.Text = 
                $"Маршрути до робочих місць:\n" +
                $"  • На заводи ➔ через вул. 10 (Синя лінія) / вул. 14\n" +
                $"  • В офіси ➔ прямі артерії до центру міста";
            _infoHint.Text = "✨ Підсвічені вулиці на мапі показують точний дорожній шлях працівників!";
        }
        else if (zone.Type == ZoneType.Industrial)
        {
            _infoTitleLabel.Text = $"🏭 Заводський комплекс ({zone.GridPos.X}, {zone.GridPos.Y})";
            _infoTitleLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.70f, 0.2f));

            _infoStat1Label.Text = $"Робочих місць: {zone.Jobs:N0}";
            _infoStat2Label.Text = $"Завантаженість цехів: 100% заповнено";

            _infoWorkplaceBreakdown.Text = 
                $"Звідки добираються працівники на цей завод:\n" +
                $"  🏡 Західні спальні квартали: 95%\n" +
                $"  🏪 Сусідні райони: 5%";

            _infoModeSplit.Text = $"Транспортні артерії: Автомагістраль 10 + Синя лінія";
            _infoTopDestinations.Text = $"Час прибуття зміни: 06:45–08:30 ранку\nЧас виїзду зміни: 16:30–18:30 вечора";
            _infoHint.Text = "✨ Підсвічені зелені вулиці показують шлях працівників зі спальних районів!";
        }
        else if (zone.Type == ZoneType.Commercial)
        {
            _infoTitleLabel.Text = $"🏢 Бізнес-центр ({zone.GridPos.X}, {zone.GridPos.Y})";
            _infoTitleLabel.AddThemeColorOverride("font_color", new Color(0.3f, 0.7f, 1f));

            _infoStat1Label.Text = $"Офісних місць: {zone.Jobs:N0}";
            _infoStat2Label.Text = $"Ємність торгівлі: {zone.CommercialCap:N0}";

            _infoWorkplaceBreakdown.Text = 
                $"Звідки добираються співробітники офісів:\n" +
                $"  🏡 Західний сектор міста: 90%\n" +
                $"  🔄 Інші квартали: 10%";

            _infoModeSplit.Text = $"Транспорт: Синя магістраль + Зелене кільце";
            _infoTopDestinations.Text = $"Години пікового навантаження: 08:00–18:00";
            _infoHint.Text = "✨ Підсвічені вулиці показують шляхи прибуття офісних співробітників!";
        }
    }

    public void ShowCityOverview(CityGrid grid, ODMatrix od)
    {
        _infoTitleLabel.Text = "📊 Шляхи: Місто на 1M+";
        _infoTitleLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));

        int totalPop = grid.TotalPopulation();
        int totalJobs = grid.TotalJobs();
        _infoStat1Label.Text = $"Населення: {totalPop:N0}";
        _infoStat2Label.Text = $"Робочі місця: {totalJobs:N0}";

        _infoWorkplaceBreakdown.Text = 
            $"Структура зайнятості міста:\n" +
            $"  🏢 Офіси й торгівля (Центр): 42%\n" +
            $"  🏭 Важка індустрія (Схід): 58%";

        _infoModeSplit.Text = $"🚗 Автомобіль: 65%   🚌 Громадський транспорт: 35%";
        _infoTopDestinations.Text = 
            $"Головні транспортні артерії:\n" +
            $"Захід (Дім) ➔ Схід (Заводи) та Центр (Офіси)";
        _infoHint.Text = "💡 Клікніть на будь-який сектор мапи, щоб побачити точний дорожній маршрут працівників!";
    }

    public void UpdateTime(float hour, int day)
    {
        int h = Mathf.FloorToInt(hour);
        int m = Mathf.FloorToInt((hour - h) * 60f);
        _timeLabel.Text = $"Day {day} | {h:D2}:{m:D2}";
    }

    public void UpdateStats(int population, float totalTrips, float avgCongestion, float transitRidership, float coverage, float demandMult, float dirBias)
    {
        _popLabel.Text = $"Pop: {population:N0}";
        _tripsLabel.Text = $"Trips: {totalTrips:N0}/h";

        float congPercent = avgCongestion * 100f;
        _congLabel.Text = $"Congestion: {congPercent:F1}%";
        if (congPercent < 50f) _congLabel.AddThemeColorOverride("font_color", Colors.Green);
        else if (congPercent < 80f) _congLabel.AddThemeColorOverride("font_color", Colors.Yellow);
        else _congLabel.AddThemeColorOverride("font_color", Colors.Red);

        _ridershipLabel.Text = $"Transit Ridership: {transitRidership:N0}";
        _coverageLabel.Text = $"Transit Coverage: {coverage * 100f:F1}%";

        _demandLabel.Text = $"Demand: {demandMult:F2}x";
        Color demandColor = Colors.DeepSkyBlue.Lerp(Colors.Crimson, Mathf.Clamp((demandMult - 0.5f) / 1.5f, 0f, 1f));
        _demandLabel.AddThemeColorOverride("font_color", demandColor);

        string dirStr = dirBias > 0.6f ? "Res → Com/Ind" : dirBias < 0.4f ? "Com/Ind → Res" : "Balanced";
        string arrow = dirBias > 0.6f ? "🌅" : dirBias < 0.4f ? "🌆" : "⚖";
        _biasLabel.Text = $"Direction: {dirStr} {arrow}";

        // Refresh transit view if active
        if (_transitViewContainer.Visible)
        {
            UpdateTransitRoutesView();
        }
    }

    /// <summary>
    /// Updates the tool instruction / hint text displayed in the HUD banner.
    /// </summary>
    public void SetToolHint(string hint, Color? color = null)
    {
        if (_toolHintLabel != null)
        {
            _toolHintLabel.Text = hint;
            _toolHintLabel.AddThemeColorOverride("font_color", color ?? new Color(0.9f, 0.95f, 1.0f));
        }
    }

    /// <summary>
    /// Sets the active interaction mode, updates button toggle state, and emits <see cref="ModeChanged"/>.
    /// </summary>
    public void SetInteractionMode(InteractionMode mode)
    {
        CurrentMode = mode;
        if (mode == InteractionMode.Inspect && _inspectBtn != null) _inspectBtn.ButtonPressed = true;
        else if (mode == InteractionMode.BuildRoad && _buildRoadBtn != null) _buildRoadBtn.ButtonPressed = true;
        else if (mode == InteractionMode.Demolish && _demolishBtn != null) _demolishBtn.ButtonPressed = true;
        else if (mode == InteractionMode.ZoneResidential && _zoneResBtn != null) _zoneResBtn.ButtonPressed = true;
        else if (mode == InteractionMode.ZoneCommercial && _zoneComBtn != null) _zoneComBtn.ButtonPressed = true;
        else if (mode == InteractionMode.ZoneIndustrial && _zoneIndBtn != null) _zoneIndBtn.ButtonPressed = true;
        else if (mode == InteractionMode.Dezone && _dezoneBtn != null) _dezoneBtn.ButtonPressed = true;
        else if (mode == InteractionMode.CreateTransitRoute && _createRouteBtn != null) _createRouteBtn.ButtonPressed = true;

        if (_routeDesignerPanel != null)
        {
            _routeDesignerPanel.Visible = (mode == InteractionMode.CreateTransitRoute);
            if (mode == InteractionMode.CreateTransitRoute)
            {
                ResetRouteDesignerDefaults();
            }
        }

        UpdateDefaultHintForMode(mode);
        EmitSignal(SignalName.ModeChanged, (int)mode);
    }

    /// <summary>
    /// Updates the status text and launch button state in the Route Designer toolbar.
    /// </summary>
    public void UpdateRouteDesignerStatus(int stopsCount, int pathNodeCount, bool isLoop)
    {
        if (_routeDesignerStatsLabel != null)
        {
            string typeStr = isLoop ? "🔄 Кільце" : "↔ Лінія";
            _routeDesignerStatsLabel.Text = $"Зупинок: {stopsCount} | Вузлів: {pathNodeCount} ({typeStr})";
        }

        if (_launchRouteBtn != null)
        {
            _launchRouteBtn.Disabled = (stopsCount < 2 || pathNodeCount < 2);
            _launchRouteBtn.Text = isLoop ? "🚀 Запустити кільце" : "🚀 Запустити маршрут";
        }
    }

    /// <summary>
    /// Populates the route designer toolbar with a default route name and color.
    /// </summary>
    public void ResetRouteDesignerDefaults()
    {
        int routeNum = (_cachedTransitManager?.Routes.Count ?? 3) + 1;
        if (_routeNameEdit != null)
        {
            _routeNameEdit.Text = $"Line {routeNum} - Express";
        }
        if (_routeColorOption != null)
        {
            int colorIdx = (_cachedTransitManager?.Routes.Count ?? 3) % RouteColorPalette.Length;
            _routeColorOption.Selected = colorIdx;
        }
        UpdateRouteDesignerStatus(0, 0, false);
    }

    private void UpdateDefaultHintForMode(InteractionMode mode)
    {
        switch (mode)
        {
            case InteractionMode.Inspect:
                SetToolHint("🔍 [Inspect] Click a zone on the map to view commute analytics.", new Color(0.4f, 0.8f, 1f));
                break;
            case InteractionMode.BuildRoad:
                SetToolHint("🛣️ [Build Road] Click first cell, then adjacent cell to build road.", new Color(0.4f, 0.95f, 0.6f));
                break;
            case InteractionMode.Demolish:
                SetToolHint("💥 [Demolish] Click first cell, then adjacent connected cell to demolish road.", new Color(1f, 0.5f, 0.4f));
                break;
            case InteractionMode.ZoneResidential:
                SetToolHint("🏡 [Residential Zone] Click on any grid cell to designate Residential district.", new Color(0.35f, 0.95f, 0.5f));
                break;
            case InteractionMode.ZoneCommercial:
                SetToolHint("🏢 [Commercial Zone] Click on any grid cell to designate Commercial district.", new Color(0.35f, 0.75f, 1.0f));
                break;
            case InteractionMode.ZoneIndustrial:
                SetToolHint("🏭 [Industrial Zone] Click on any grid cell to designate Industrial district.", new Color(1.0f, 0.75f, 0.25f));
                break;
            case InteractionMode.Dezone:
                SetToolHint("🧹 [Dezone] Click on any zoned cell to clear it back to Empty terrain.", new Color(1.0f, 0.45f, 0.45f));
                break;
            case InteractionMode.CreateTransitRoute:
                SetToolHint("🚌 [Transit Designer] Клікніть на перехрестя/вузли дороги, щоб додати зупинки та побудувати лінію автобуса.", new Color(0.3f, 0.9f, 1.0f));
                break;
        }
    }
}
