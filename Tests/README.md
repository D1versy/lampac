# Tests — форк-функционал (QbitDownload + Lampa-плагины)

Автотесты покрывают **только уникальный код форка** (модуль `QbitDownload` и два браузерных плагина).
Апстримный код Lampac не тестируется.

## Как запустить

```bash
# C# (xUnit, net10) — требуется .NET 10 SDK
dotnet test Tests/QbitDownload.Tests/QbitDownload.Tests.csproj

# JS (встроенный node:test) — требуется Node 18+.
# Один файл (cub-tabs.test.js) использует jsdom → перед первым прогоном:
( cd Tests/js && npm install )
node --test Tests/js/*.test.js

# C# с отчётом о покрытии модуля (cobertura в TestResults/)
dotnet test Tests/QbitDownload.Tests/QbitDownload.Tests.csproj \
  --settings Tests/QbitDownload.Tests/coverlet.runsettings --collect:"XPlat Code Coverage"
```

Текущий статус: **626 C# + 204 JS = 830 тестов, все зелёные.**

## Как это устроено

Модуль `QbitDownload` компилируется хостом в рантайме (Roslyn) — готового DLL нет. Поэтому тестовый
проект **линкует исходники модуля** (`<Compile Include="..\..\Modules\QbitDownload\*.cs" Link=…>`) и
ссылается на `Shared` + `Microsoft.AspNetCore.App`. Тестируется **реальный production-код**, он не меняется.

- `Access.cs` — reflection-шлюз к `private static` методам `QbitController` + обёртка `EpView` над вложенным `Ep`.
- `TestEnv.cs` — общий setup (`EnsureConf`, `SetListen`, `FreshCache`) и отключение параллелизма (общие статики).
- `Fakes.cs` — `FakeHttpMessageHandler`/`FakeQbit` для qBit-хелперов, принимающих `HttpClient` (без сети).
- `Tests/js/harness.js` — грузит плагины в `vm`-песочницу с мок-глобалами (Lampa/document/localStorage);
  у `qdl.js` авто-старт срезается, внутренние чистые функции экспортируются.
- `Tests/js/cub-tabs.test.js` — тесты на **jsdom** (реальный `querySelectorAll` + реальный `MutationObserver`)
  для фикса «мелькающей вкладки CUB» в модалке «Уведомления»: доказывают и скрытие CUB-таба, и что это
  происходит в immediate-проходе observer'а (до кадра, без мерцания). Требует `npm install` в `Tests/js`.

## Что покрыто

**C#** — парсинг серий (`ParseEp` и ключи/подписи), матчер озвучек (`StudioOf/StudioId/DubsForVideo/NaturalCompare`),
анти-path-traversal (`ConfinedCombine`), анти-SSRF (`IsPrivateHost/IsLoopbackSelf`), bencode-детект, форматтеры
(`HumanSize/QualityFromTitle/MimeType/LangName/SeriesKey/MagnetHash`), SQLite-слой (`SqlContext`/уникальные индексы/дедуп),
qBit-хелперы (`QbitAddMagnet/ResolveFile/ResolveDubFile`) через фейковый HTTP.

**JS** — `slimCard` (детект tv/movie), `cleanName`, `videoFiles`, `esc`, `streamUrl`/`isBrowser` (HLS vs direct),
`relTime`, premium-разблокировка и снятие бренда « - CUB» в `lampainit-invc.js`; матчинг «карточка ↔ загрузка»
(`findDownload`/`normTitle`/`isSerialName` + jsdom-интеграция `addButton`, `qdl-match.test.js`) — строгий id+media_type,
имя только для безметочных раздач (регрессия «Дюна ↔ Дюна: Пророчество»).

Покрытие ядра `QbitController` (синхронная логика): **~57% строк / ~79% веток**; qBit-хелперы 89–100% веток;
`Ep` и `SqlContext` — 100% строк. Непокрыто намеренно: HTTP-экшены контроллера (нужен end-to-end хост + фейк qBit)
и ffmpeg/ffprobe-процессы (`ProbeAudio/StartHls`).

## Найденные latent-нюансы (зафиксированы тестами как текущее поведение, `// BUG?:`)

1. `ParseEp`: «Creditless OP» классифицируется как `NCED` — подстрока «ED» внутри «cr**ed**itless». → `ParseEpTests.cs`
2. `ParseEp`: ветка `OP/ED/PV/CM/Trailer…` теряет пойманный номер («OP2» → `ep=-1`). → `ParseEpTests.cs`
3. `IsLoopbackSelf`/`IsSelfResolver`/`IsPrivateHost`: `::1` не распознаётся — `Uri.Host` возвращает `[::1]` со скобками, а сравнение идёт с `"::1"`. → `SsrfTests.cs`
4. `StudioOf`: generic-тег в скобках `[dub]`/`[rus]` протекает через ветку «остаток после общего префикса» (там нет проверки `IsGenericFolder`). → `StudioHelpersTests.cs`
5. `StudioOf`: ветка «суффикс после имени видео» использует `CleanStudio` без `StripNoise` → тех-шум качества остаётся в названии студии. → `StudioHelpersTests.cs`
6. `names()` (qdl.js): при `x.name === ''` тернарник возвращает весь объект вместо пустой строки. → `qdl-card.test.js`
7. `relTime()` (qdl.js): невалидный вход даёт `'NaN.NaN.NaN'` вместо `''` (catch не срабатывает). → `qdl-urls.test.js`

Все пункты — низкого приоритета; тесты ассертят фактическое поведение, поэтому набор зелёный.
