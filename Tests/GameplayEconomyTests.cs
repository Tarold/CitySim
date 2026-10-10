using Godot;
using NUnit.Framework;
using CitySim.Simulation;

namespace CitySim.Tests;

[TestFixture]
public class GameplayEconomyTests
{
    private CityGrid _grid = null!;
    private RoadGraph _roadGraph = null!;
    private ParcelManager _parcelManager = null!;
    private TransitManager _transitManager = null!;
    private EconomyManager _economyManager = null!;

    [SetUp]
    public void Setup()
    {
        _grid = new CityGrid(20, 20, 64f);
        _roadGraph = new RoadGraph();
        _parcelManager = new ParcelManager();
        _parcelManager.Initialize(_roadGraph);
        _transitManager = new TransitManager();
        _economyManager = new EconomyManager();

        EconomyTestCityGenerator.Generate(_grid, _roadGraph, _parcelManager, _transitManager, _economyManager);
    }

    [Test]
    public void EconomyTestCity_InitialState_GeneratesExpectedDistrictsAndDemographics()
    {
        Assert.That(_economyManager.Money, Is.EqualTo(100000f));
        Assert.That(_roadGraph.Edges.Count, Is.GreaterThan(0));
        Assert.That(_parcelManager.Parcels.Count, Is.GreaterThan(0));
        Assert.That(_parcelManager.TotalPopulation, Is.GreaterThan(500), "Residential district should have sizable population");
        Assert.That(_parcelManager.TotalJobs, Is.GreaterThan(300), "Commercial and industrial districts should have jobs");
        Assert.That(_transitManager.Routes.Count, Is.EqualTo(2), "Expected 2 benchmark transit routes");
    }

    [Test]
    public void FinancialTick_GeneratesTaxesTransitFareAndOperatingExpenses()
    {
        float startBalance = _economyManager.Money;
        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);

        Assert.That(_economyManager.LastPopulationTax, Is.GreaterThan(0f));
        Assert.That(_economyManager.LastJobTax, Is.GreaterThan(0f));
        Assert.That(_economyManager.LastTaxRevenue, Is.EqualTo(_economyManager.LastPopulationTax + _economyManager.LastJobTax).Within(0.01f));
        Assert.That(_economyManager.LastTransitRevenue, Is.GreaterThan(0f));
        Assert.That(_economyManager.LastExpenses, Is.GreaterThan(0f));

        float expectedTotalIncome = _economyManager.LastTaxRevenue + _economyManager.LastTransitRevenue;
        float expectedDelta = expectedTotalIncome - _economyManager.LastExpenses;

        Assert.That(_economyManager.LastDelta, Is.EqualTo(expectedDelta).Within(0.01f));
        Assert.That(_economyManager.Money, Is.EqualTo(startBalance + expectedDelta).Within(0.01f));
    }

    [Test]
    public void MultiDaySimulation_TreasurySustainability_TracksSolvency()
    {
        float initialMoney = _economyManager.Money;

        // Simulate 72 hourly ticks (3 full in-game days)
        for (int tick = 0; tick < 72; tick++)
        {
            _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
            Assert.That(_economyManager.Money, Is.GreaterThan(0f), $"Treasury must remain solvent at tick {tick}");
        }

        // On a balanced benchmark economy with healthy population and transit revenue, treasury accumulates surplus
        Assert.That(_economyManager.Money, Is.GreaterThan(initialMoney));
    }

    [Test]
    public void TaxRateSensitivity_AlteringRates_ScalesTaxRevenueProportionally()
    {
        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
        float baseTaxRev = _economyManager.LastTaxRevenue;
        float basePopTax = _economyManager.LastPopulationTax;
        float baseJobTax = _economyManager.LastJobTax;

        // 1. Double the tax rates
        _economyManager.TaxPerPopulation *= 2f;
        _economyManager.TaxPerJob *= 2f;
        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);

        Assert.That(_economyManager.LastPopulationTax, Is.EqualTo(basePopTax * 2f).Within(0.01f));
        Assert.That(_economyManager.LastJobTax, Is.EqualTo(baseJobTax * 2f).Within(0.01f));
        Assert.That(_economyManager.LastTaxRevenue, Is.EqualTo(baseTaxRev * 2f).Within(0.02f));

        // 2. Zero taxes
        _economyManager.TaxPerPopulation = 0f;
        _economyManager.TaxPerJob = 0f;
        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);

        Assert.That(_economyManager.LastPopulationTax, Is.EqualTo(0f));
        Assert.That(_economyManager.LastJobTax, Is.EqualTo(0f));
        Assert.That(_economyManager.LastTaxRevenue, Is.EqualTo(0f));
    }

    [Test]
    public void TransitFareAndFleetExpenses_AffectsOperatingExpensesAndFareIncome()
    {
        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
        float baseExpenses = _economyManager.LastExpenses;
        float baseTransitRevenue = _economyManager.LastTransitRevenue;

        // Increase fleet size on Route 1
        var route1 = _transitManager.Routes[0];
        int originalFleet = route1.FleetSize;
        route1.FleetSize += 4; // Add 4 vehicles -> extra (4 * 15f) / 24f = 2.5f per tick

        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
        float expectedExpenseIncrease = (4 * 15f) / 24f;
        Assert.That(_economyManager.LastExpenses, Is.EqualTo(baseExpenses + expectedExpenseIncrease).Within(0.01f));

        // Double ticket price and revenue
        route1.FleetSize = originalFleet;
        route1.DailyRevenue *= 2f;

        _economyManager.ProcessFinancialTick(_grid, _roadGraph, _transitManager, _parcelManager);
        Assert.That(_economyManager.LastTransitRevenue, Is.GreaterThan(baseTransitRevenue));
    }
}
