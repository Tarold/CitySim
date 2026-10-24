# AGENTS.md — Симуляційне ядро (`Scripts/Simulation/`)

## Контекст модуля
Папка містить математичне ядро симуляції: граф криволінійних доріг, парцеляцію, макромодель TAZ/OD/MSA/LUTI, економіку та мікроскопічні пули агентів.

## Ключові підсистеми та еталони
- **Граф та геометрія доріг:** `RoadGraph.cs`, `CurveSegment.cs` (квадратичні криві Безьє), `RoadSnappingEngine.cs`, `SpatialGridIndex.cs` ($O(1)$ просторовий хеш).
- **Землекористування та LUTI:** `ParcelManager.cs`, `RoadsideParcel.cs` (придорожні ділянки, динамічна щільність `Low`/`Medium`/`High` від індексу доступності Гансена).
- **Макромодель попиту і трафіку:** `TazManager.cs` (кластеризація у 320px макрозони, пересадки ГТ) → `OptimizedODMatrix.cs` (плоскі `float[]`, `Parallel.For`, Logit-спліт Car/Transit/Walk) → `TrafficEngine.cs` (MSA-рівновага, BPR-імпеданс, `ShortestPathTreeBuffer`).
- **Мікроскопічний шар (DOD):** `CarTrafficManager.cs` (`CarAgent` struct pool, до 2000 авто), `PedestrianManager.cs` (`PedestrianAgent` struct pool, до 5000 пішоходів), `TransitManager.cs`, `TrafficLightManager.cs`.
- **Економіка та сценарії:** `EconomyManager.cs`, генератори сценаріїв `*CityGenerator.cs` (еталон: `TutorialCityGenerator.cs`).

## Специфічні правила `Scripts/Simulation/`
- **Zero-GC у розрахунках:** у `OptimizedODMatrix`, `TrafficEngine`, `CarTrafficManager` та `PedestrianManager` перевикористовуй наявні масиви та `ShortestPathTreeBuffer`. Не створюй нових колекцій під час ітерацій MSA або щокадрового `Update()`.
- **Просторові запити:** для пошуку найближчих вузлів, ребер чи парцелів використовуй `SpatialGridIndex` (`QueryRadius`, `QueryAABB`) із переданим буфером `List<int>`, а не лінійний перебір `Edges` чи `Parcels`.
- **Криволінійна геометрія:** позицію та дотичну на дорозі обчислюй через `edge.Curve.Evaluate(t)` і `edge.Curve.GetTangent(t)`, а не лінійною інтерполяцією між `FromId` та `ToId`.
- **Інвалідація після зміни топології:** єдина точка — `TopologyChangeCoordinator` (підписаний на події `RoadGraph`, прапорець `HasPendingChanges`). Статистику HUD рахуй у `CityStatsSnapshot` на макро-тіку, а не щокадрово.
- **Мутація ребер графа:** при розбитті ребра (`SplitEdgeAtPoint`) або видаленні дороги перенось/очищуй прив'язані `RoadsideParcel` через `ParcelManager` і викликай `TazManager.MarkDirty()`.
