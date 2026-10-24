# AGENTS.md — Рендеринг та візуалізація (`Scripts/Rendering/`)

## Контекст модуля
Відповідає за 2D-відмальовку міста, криволінійних доріг, теплокарт заторів/доступності, інтерактивних прев'ю будівництва та GPU-інстансинг транспорту й пішоходів.

## Ключові файли
- `VehicleRenderer.cs` — містить `VehicleMultimeshBufferBuilder` (чистий C# пакувальник буферів) та вузол `VehicleRenderer` (інтеграція з `RenderingServer.MultimeshSetBuffer` і шейдерами `Shaders/*_instanced.gdshader`).
- `RoadRenderer.cs` — відмальовка кривих Безьє доріг, розмітки, виділених смуг ГТ, світлофорів та теплокарти `V/C` (`Shaders/road_heatmap.gdshader`).
- `CityRenderer.cs` — відмальовка сітки `CityGrid`, полігонів `RoadsideParcel` та колірної індикації зон і щільності забудови.
- `CommuteOverlayRenderer.cs` — анімовані дуги кореспонденцій та підсвічування маршрутів при інспекції зони/парцела.
- `ToolPreviewRenderer.cs` — візуалізація снепінгу (`RoadSnapResult`), кривих Безьє під час будівництва та чернеток маршрутів ГТ.

## Специфічні правила `Scripts/Rendering/`
- **Тільки GPU MultiMesh для агентів:** ніколи не малюй окремі машини, автобуси чи пішоходів через `DrawRect`/`DrawCircle` у `_Draw()`. Усі інстанси пакуються у плоскі `float[]` буфери по 12 float на об'єкт (`8 float Transform2D` + `4 float RGBA Color`) у `VehicleMultimeshBufferBuilder` і передаються одним викликом `RenderingServer.MultimeshSetBuffer()`.
- **Відокремлення логіки буферів від Godot RID:** уся математика трансформацій та кольорів інстансів має залишатися в `VehicleMultimeshBufferBuilder.BuildBuffers()`, щоб її можна було протестувати в headless-режимі без `RenderingServer`.
- **Шейдери (`Shaders/*.gdshader`):** кастомні дані інстансів передаються через `INSTANCE_CUSTOM` та `COLOR`. При зміні формату буфера в `VehicleRenderer.cs` синхронно оновлюй відповідний `.gdshader`.
