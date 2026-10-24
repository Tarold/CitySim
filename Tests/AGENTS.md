# AGENTS.md — Автоматизоване тестування (`Tests/`)

## Контекст модуля
Набір модульних та інтеграційних тестів на **NUnit 3** (`net8.0`), що перевіряє симуляційне ядро, економіку, маршрутизацію та побудову буферів рендерингу без запуску рушія Godot.

## Структура тестів
- `Tests/Simulation/` — модульні тести підсистем симуляції (еталон: `Tests/Simulation/TazClusteringAndOptimizedODTests.cs`).
- `Tests/*.cs` — інтеграційні та сценарні тести (`Phase4LutiAccessibilityAndDensityTests.cs`, `GameplayEconomyTests.cs`, `GameplayRoutingTests.cs`, `MapEditingVisualTests.cs`, `RoadConstructionTests.cs`, `TransitDesignerTests.cs`, `ZoningAndExpansionTests.cs`).

## Специфічні правила `Tests/`
- **Без залежності від Godot Runtime:** тести запускаються через `dotnet test Tests/Tests.csproj` у звичайному процесі .NET. Не викликай у тестах методи Godot, які потребують ініціалізованого С++ ядра рушія (`Node.AddChild`, `RenderingServer`, `ResourceLoader`).
- **Тестування рендерингу через Builder:** для перевірки візуалізації агентів тестуй `VehicleMultimeshBufferBuilder` напряму (перевіряй `ActiveCarInstances`, `ActiveBusInstances`, `ActivePedestrianInstances` та значення у `float[]` буферах), як у `Tests/Simulation/Phase3GpuInstancingAndDodTests.cs`.
- **Детермінованість:** уникай `Thread.Sleep` та залежності від системного часу. Передавай фіксовані `delta` або кількість тіків у методи `Update(dt)` / `Recalculate()`.
- **Іменування:** використовуй формат `MethodOrScenario_Condition_ExpectedBehavior` та атрибут `[Test]`.
