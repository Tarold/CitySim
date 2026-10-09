using Godot;
using CitySim.Simulation;

namespace CitySim.UI;

/// <summary>
/// Main Menu screen for selecting game modes.
/// </summary>
public partial class MainMenu : Control
{
    private Button _startNewCityButton;
    private Button _loadTutorialButton;

    /// <summary>
    /// Called when the node enters the scene tree for the first time.
    /// </summary>
    public override void _Ready()
    {
        _startNewCityButton = GetNode<Button>("CenterContainer/VBoxContainer/StartNewCityButton");
        _loadTutorialButton = GetNode<Button>("CenterContainer/VBoxContainer/LoadTutorialButton");

        _startNewCityButton.Pressed += () => StartGame(false);
        _loadTutorialButton.Pressed += () => StartGame(true);
    }

    private void StartGame(bool loadTutorial)
    {
        Main.LoadTutorialMode = loadTutorial;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }
}
