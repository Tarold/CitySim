namespace CitySim.Simulation.Commands;

/// <summary>
/// Whether a command changed the world.
/// </summary>
public enum CommandStatus
{
    /// <summary>The edit was applied (money spent, world changed, macro model recalculated).</summary>
    Applied,

    /// <summary>Nothing to do: the target is already in the requested state.</summary>
    NoChange,

    /// <summary>The edit was refused (invalid target, insufficient funds); the world is unchanged.</summary>
    Rejected
}

/// <summary>
/// Presentation hint for the result message; the UI maps it to a hint color.
/// </summary>
public enum CommandTone
{
    Success,
    Demolition,
    Clearing,
    Caution,
    Neutral,
    Warning,
    Error
}

/// <summary>
/// Outcome of <see cref="IWorldCommand.Execute"/>: status, a player-facing message and side-effect details.
/// </summary>
public sealed class CommandResult
{
    private CommandResult(CommandStatus status, string message, CommandTone tone, float cost, TopologyChangeResult? topology, int entityId)
    {
        Status = status;
        Message = message ?? string.Empty;
        Tone = tone;
        Cost = cost;
        Topology = topology;
        EntityId = entityId;
    }

    public CommandStatus Status { get; }
    public bool IsApplied => Status == CommandStatus.Applied;

    /// <summary>Player-facing status message (may be empty).</summary>
    public string Message { get; }

    public CommandTone Tone { get; }

    /// <summary>Money spent by the command.</summary>
    public float Cost { get; }

    /// <summary>Set when the command changed road topology and ran the invalidation chain.</summary>
    public TopologyChangeResult? Topology { get; }

    /// <summary>Id of the created entity (road node, transit route), or -1.</summary>
    public int EntityId { get; }

    public static CommandResult Applied(
        string message,
        CommandTone tone = CommandTone.Success,
        float cost = 0f,
        TopologyChangeResult? topology = null,
        int entityId = -1) =>
        new CommandResult(CommandStatus.Applied, message, tone, cost, topology, entityId);

    public static CommandResult NoChange(string message) =>
        new CommandResult(CommandStatus.NoChange, message, CommandTone.Neutral, 0f, null, -1);

    public static CommandResult Rejected(string message, CommandTone tone = CommandTone.Error) =>
        new CommandResult(CommandStatus.Rejected, message, tone, 0f, null, -1);
}
