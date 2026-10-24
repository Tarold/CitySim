---
name: add-simulation-feature
description: >-
  Покрокова процедура додавання нової симуляційної фічі або механіки в CitySim
  (від проєктування headless C# класу в Scripts/Simulation/ до інтеграції через
  SimulationWorld, команди та інструменти й покриття NUnit-тестами в Tests/).
---

# Додавання симуляційної фічі в CitySim

Використовуй цей skill, коли потрібно реалізувати нову механіку симуляції, транспортної моделі, економіки або міського планування.

## Крок 1: Проєктування у `Scripts/Simulation/` (Headless-first)
1. Створи або розшир клас у просторі імен `CitySim.Simulation` (еталон: `Scripts/Simulation/TazManager.cs`).
2. Переконайся, що клас **не залежить** від `SceneTree`, `RenderingServer` чи вузлів `Node2D` — використовуй лише структури даних `Godot.Vector2`, `Godot.Vector2I`, `Godot.Mathf` та стандартний .NET 8.
3. Якщо фіча працює в гарячому циклі (`Update` або ітерації MSA/OD):
   - Використовуй преалоковані масиви або пули структур (`struct`).
   - Для просторових запитів використовуй `SpatialGridIndex` із переданим буфером `List<int>`.

## Крок 2: Інтеграція (`SimulationWorld` → команди → інструменти)
1. Підключи підсистему в `SimulationWorld` (створення в `Create`, виклик у `Tick` або `RecalculateMacroModel`). Якщо вона кешує дані, залежні від топології `RoadGraph`, додай її оновлення в `TopologyChangeCoordinator`, а не на місце виклику.
2. Нову дію гравця оформи як `IWorldCommand` у `Scripts/Simulation/Commands/` (валідація, вартість, `world.ApplyTopologyChange()` або `world.RecalculateMacroModel()`, `CommandResult`).
3. Покрокову взаємодію з мапою оформи як `ITool` у `Scripts/Tools/` і зареєструй її в `Main.CreateTools()`. `Main.cs` лише передає ввід і оновлює рендеринг за подіями світу.
4. За потреби додай візуалізацію даних у `Scripts/Rendering/` (для агентів — через `VehicleMultimeshBufferBuilder`, для оверлеїв — у `CityRenderer` / `RoadRenderer`).

## Крок 3: Написання NUnit-тестів у `Tests/`
1. Створи тестовий файл `Tests/Simulation/<FeatureName>Tests.cs` (еталон: `Tests/Simulation/TazClusteringAndOptimizedODTests.cs`); інтеграційні сценарії — через `SimulationWorld` (`Tests/Simulation/SimulationWorldTests.cs`), команди — `Tests/Simulation/Commands/`, інструменти — через `FakeToolHost` (`Tests/Tools/`).
2. Покрий базовий сценарій, граничні умови (порожній граф, розірвані ребра, нульовий бюджет/попит) та інваріанти продуктивності.

## Крок 4: Перевірка готовності
Запусти збірку та повний набір тестів:
```bash
dotnet build CitySim.csproj && dotnet test Tests/Tests.csproj
```
