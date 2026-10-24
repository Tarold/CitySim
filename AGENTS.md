# AGENTS.md

## Проєкт
**CitySim** — симулятор міського трафіку, громадського транспорту та землекористування (LUTI) для мегаполіса на 1 000 000+ жителів.
Стек: **Godot 4.7** (`Godot.NET.Sdk/4.7.2`), **C# 12 / .NET 8** (`net8.0`), **NUnit 3**.

## Команди
```bash
dotnet build CitySim.csproj                                       # збірка гри
dotnet test Tests/Tests.csproj                                    # запуск усіх тестів (headless)
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Name" # запуск конкретного тесту
```

## Структура
- `Scripts/Simulation/` — макромодель (TAZ, OD-матриця, MSA-трафік, економіка, LUTI) та мікроагенти. Правила: `Scripts/Simulation/AGENTS.md`
- `Scripts/Rendering/` та `Shaders/` — 2D-рендеринг, теплокарти, GPU `MultiMesh`-інстансинг. Правила: `Scripts/Rendering/AGENTS.md`
- `Scripts/Simulation/SimulationWorld.cs` — власник підсистем і ігрового циклу (`Tick`), `Scripts/Simulation/Commands/` — команди редагування світу.
- `Scripts/Tools/` — headless-інструменти взаємодії (`ITool`: дороги, знесення, зонування, смуги ГТ, маршрути).
- `Scripts/UI/` — інтерфейс гравця (`GameUI.cs`, `MainMenu.cs`).
- `Scripts/Main.cs` — Godot-адаптер: камера, рендерери, HUD, режим Inspect, передача вводу активному інструменту.
- `Scenes/` — сцени Godot (`Main.tscn`, `MainMenu.tscn`).
- `Tests/` — модульні та інтеграційні тести NUnit. Правила: `Tests/AGENTS.md`
- `.agents/agents/` — ролі субагентів Outer/Inner Loop (`product_manager.md`, `developer.md`, `qa_engineer.md`).
- `.agents/skills/` — повторювані процедури розробки (Agent Skills).
- `docs/` — детальна документація проєкту:
  - Архітектура та системи: `docs/architecture.md`
  - План розвитку (Roadmap 1M+): `docs/roadmap.md`
  - Аудит масштабування: `docs/scaling-audit.md`
  - **План продовження масштабування (етапи C–F, як перевіряти): `docs/scaling-plan.md`** — читати перед роботою над продуктивністю, ГТ чи макромоделлю.

## Правила
- **Headless-сумісність симуляції:** класи в `Scripts/Simulation/` не повинні залежати від активного `SceneTree` або візуальних вузлів Godot, бо `Tests/Tests.csproj` тестує їх напряму через `dotnet test` без запуску бінарника Godot.
- **Ізоляція тестів у збірці:** не видаляй `<Compile Remove="Tests/**" />` із `CitySim.csproj` — інакше Godot SDK намагатиметься скомпільовувати NUnit-тести у основну збірку гри.
- **Макромодель попиту:** логіку кореспонденцій, доступності (Hansen LUTI) та модального спліту додавай у `OptimizedODMatrix.cs` + `TazManager.cs`; добову криву попиту бери з `DemandCurve`. Стару клітинкову `ODMatrix` видалено — не відтворюй щільних матриць зона×зона (O(N²)), працюй на рівні TAZ.
- **Зміни топології доріг:** після будь-якої зміни ребер/вузлів у `RoadGraph` викликай `TopologyChangeCoordinator.ApplyChanges()` (через `SimulationWorld.ApplyTopologyChange()`). Координатор у фіксованому порядку оновлює кеші шляхів, `TrafficLightManager`, `ParcelManager`, маршрути `TransitManager.RevalidateRoutes()`, `CarTrafficManager`, `PedestrianManager` і `TazManager.MarkDirty()`. Не дублюй цей ланцюжок вручну; нову підсистему, залежну від топології, підключай у координатор.
- **Продуктивність гарячого циклу (`_Process`):** уникай алокацій у купі (`new` класів, LINQ, замикань) у щокадрових методах `CarTrafficManager`, `PedestrianManager`, `VehicleMultimeshBufferBuilder` та `OptimizedODMatrix`. Використовуй плоскі масиви та пули структур.
- **Еталон нової фічі:** додавай систему за зразком `Scripts/Simulation/TazManager.cs` разом із тестами за зразком `Tests/Simulation/TazClusteringAndOptimizedODTests.cs` (див. skill `.agents/skills/add-simulation-feature/SKILL.md`).
- **Шари:** логіку симуляції додавай у `SimulationWorld`/підсистеми, редагування гравця — як `IWorldCommand`, покрокову взаємодію — як `ITool` у `Scripts/Tools/` (без Godot `Input`, через `ToolInput`/`IToolHost`). `Main.cs` залишається тонким адаптером: не додавай у нього правил симуляції чи валідації.
- **Стиль коду:** `PascalCase` для публічних членів і типів, `_camelCase` для приватних полів, іменовані константи замість магічних чисел. Не видаляй наявні коментарі та XML-докстрінги поза межами своїх змін.

## Перевірка готовності
Задача завершена лише коли обидві команди проходять без помилок (`0 Error(s)`):
```bash
dotnet build CitySim.csproj && dotnet test Tests/Tests.csproj
```
