using Godot;
using CitySim.Simulation;

namespace CitySim.UI;

/// <summary>
/// Main Menu screen for selecting game modes and test scenarios.
/// </summary>
public partial class MainMenu : Control
{
    private Button _startNewCityButton;
    private Button _loadTutorialButton;
    private Button _loadEconomyTestButton;
    private Button _loadRoutingTestButton;
    private Button _loadMapEditingTestButton;

    /// <summary>
    /// Called when the node enters the scene tree for the first time.
    /// </summary>
    public override void _Ready()
    {
        _startNewCityButton = GetNode<Button>("CenterContainer/VBoxContainer/StartNewCityButton");
        _loadTutorialButton = GetNode<Button>("CenterContainer/VBoxContainer/LoadTutorialButton");
        _loadEconomyTestButton = GetNodeOrNull<Button>("CenterContainer/VBoxContainer/LoadEconomyTestButton");
        _loadRoutingTestButton = GetNodeOrNull<Button>("CenterContainer/VBoxContainer/LoadRoutingTestButton");
        _loadMapEditingTestButton = GetNodeOrNull<Button>("CenterContainer/VBoxContainer/LoadMapEditingTestButton");

        _startNewCityButton.Pressed += () => StartGame(ScenarioType.Sandbox);
        _loadTutorialButton.Pressed += () => StartGame(ScenarioType.Tutorial);
        if (_loadEconomyTestButton != null)
            _loadEconomyTestButton.Pressed += () => StartGame(ScenarioType.EconomyTest);
        if (_loadRoutingTestButton != null)
            _loadRoutingTestButton.Pressed += () => StartGame(ScenarioType.RoutingTest);
        if (_loadMapEditingTestButton != null)
            _loadMapEditingTestButton.Pressed += () => StartGame(ScenarioType.MapEditingTest);
    }

    private void StartGame(ScenarioType scenario)
    {
        Main.SelectedScenario = scenario;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }
}
