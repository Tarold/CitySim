using Godot;
using System.Collections.Generic;

namespace CitySim.Simulation;

/// <summary>
/// Manages the city's economy, including player funds, construction costs, and periodic financial ticks.
/// </summary>
public class EconomyManager
{
    /// <summary>
    /// Gets the current money balance of the city.
    /// </summary>
    public float Money { get; private set; }

    /// <summary>
    /// Gets the net income or expense (delta) calculated during the last financial tick.
    /// </summary>
    public float LastDelta { get; private set; }
    
    /// <summary>
    /// The cost to build a single road segment.
    /// </summary>
    public const float RoadSegmentCost = 100f;

    /// <summary>
    /// The cost to designate a grid cell as a zone.
    /// </summary>
    public const float ZoningCost = 50f;

    /// <summary>
    /// The base cost to launch a new transit route.
    /// </summary>
    public const float TransitRouteBaseCost = 500f;
    
    /// <summary>
    /// The maintenance cost per active road segment per financial tick.
    /// </summary>
    public const float RoadMaintenancePerSegment = 2f;

    /// <summary>
    /// The tax revenue generated per residential population per financial tick (scaled to daily).
    /// </summary>
    public float TaxPerPopulation { get; set; } = 0.05f;

    /// <summary>
    /// The tax revenue generated per filled job per financial tick (scaled to daily).
    /// </summary>
    public float TaxPerJob { get; set; } = 0.1f;

    /// <summary>
    /// Initializes a new instance of the EconomyManager class.
    /// </summary>
    /// <param name="initialMoney">The starting money balance.</param>
    public EconomyManager(float initialMoney = 50000f)
    {
        Money = initialMoney;
    }

    /// <summary>
    /// Checks if the player has sufficient funds for a transaction.
    /// </summary>
    /// <param name="amount">The cost to check.</param>
    /// <returns>True if the player can afford the amount; otherwise, false.</returns>
    public bool CanAfford(float amount)
    {
        return Money >= amount;
    }

    /// <summary>
    /// Attempts to deduct the specified amount from the player's funds.
    /// </summary>
    /// <param name="amount">The amount to spend.</param>
    /// <returns>True if the transaction was successful; otherwise, false.</returns>
    public bool Spend(float amount)
    {
        if (CanAfford(amount))
        {
            Money -= amount;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Adds income directly to the player's funds.
    /// </summary>
    /// <param name="amount">The amount of income to add.</param>
    public void AddIncome(float amount)
    {
        Money += amount;
    }

    /// <summary>
    /// Processes a periodic financial tick, deducting maintenance costs and adding tax revenues.
    /// </summary>
    /// <param name="grid">The city grid used to calculate tax revenues from zones.</param>
    /// <param name="roadGraph">The road graph used to calculate road maintenance expenses.</param>
    /// <param name="transitManager">The transit manager used to calculate transit network expenses.</param>
    public void ProcessFinancialTick(CityGrid grid, RoadGraph roadGraph, TransitManager transitManager)
    {
        float expenses = 0f;
        float income = 0f;

        // 1. Road Maintenance
        int activeSegments = 0;
        foreach (var edge in roadGraph.Edges)
        {
            if (edge.FromId != -1) activeSegments++;
        }
        activeSegments /= 2; // Because edges are bidirectional
        expenses += (activeSegments * RoadMaintenancePerSegment) / 24f;

        // 2. Transit Maintenance (Using Fleet Size)
        foreach (var route in transitManager.Routes)
        {
            expenses += (route.FleetSize * 15f) / 24f; // Fixed cost per vehicle per tick
            // Route maintenance
            expenses += 50f / 24f; 
        }
        
        // 3. Taxes
        for (int i = 0; i < grid.ZoneCount; i++)
        {
            var zone = grid.GetZone(i);
            if (zone == null || zone.Type == ZoneType.Empty || zone.Type == ZoneType.Entrance) continue;

            if (zone.Type == ZoneType.Residential)
            {
                income += (zone.Population * TaxPerPopulation) / 24f;
            }
            else if (zone.Type == ZoneType.Commercial || zone.Type == ZoneType.Industrial)
            {
                income += (zone.Jobs * TaxPerJob) / 24f;
            }
        }

        // Apply
        float delta = income - expenses;
        Money += delta;
        LastDelta = delta;
    }
}
