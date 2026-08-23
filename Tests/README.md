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

Текущий статус: **2019 C# + 788 JS = 2807 тестов, все зелёные.**

⚠️ Отсюда их можно (и нужно) гонять одной командой вместе с клиентами:
гейтом `test-all.ps1` из репозитория оркестрации (`E:\Media-server\scripts\`):
он встроен блокирующим шагом в `sync-lampac-fork.ps1` ПЕРЕД сборкой образа, чтобы битый
ребейз ловился до 15-минутного билда и крашлупа контейнера.
Канон покрытия — `E:\Media-server\claude\15-tests.md`.

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

## Что добавилось 23.08.2026

- **`Core/Middlewares/D1VPerimeter.cs`** — весь внешний периметр (46 тестов). Гоняется на
  `DefaultHttpContext`: поднимать TestServer значило бы тянуть весь Startup (Roslyn-компиляция
  модулей, SQLite, Playwright, Kestrel) ради 145 строк middleware. Покрыто: admin-пути закрыты
  снаружи ВСЕГДА (даже с валидным ключом), fail-closed на подделку edge-маркера, приоритет
  источников ключа, атрибуты cookie, `publicPrefixes` только на GET, пустой словарь ключей и
  пустая строка-ключ — отказ, стелс-404 неотличим для «нет ключа» и «ключ неверный».
- **`Modules/QbitDownload/Live.cs`** — 78 тестов (`LiveTests.cs`, доступ через `LiveAccess.cs`).
  🔴 Файл был ЕДИНСТВЕННЫМ в модуле вне сборки тестов: csproj утверждал, что он «тянет за собой
  контроллер». `git log -S` показал, что `Controller.cs` линкован с первого тестового коммита,
  а комментарий добавлен позже; линковка проходит чисто. Покрыто: наивный UTC регистратора,
  локальные сутки в двух UTC-датах, дыра перевода часов, подпись сегментных строк двумя
  плейсхолдерами (`?o=` + ключ), анти-traversal имён сегментов, гейт прав раздела.
  **Найден и починен баг:** аллоулист имён сегментов принимал имя с завершающим переводом
  строки — в .NET якорь `$` его разрешает. Заменён на `\A…\z`.
- **`Modules/QbitDownload/Admin.cs`** — 28 тестов (`AdminTests.cs`): анти-CSRF (маркер
  `X-D1V-Admin` + совпадение Origin с Host), выдача и отзыв прав по разделам, вырожденные uid,
  бэкфилл истории закрыт на реплике.
- **`ModuleRegistryTests.cs`** — сверка `manifest.json → tree` со списком файлов на диске
  и с `<Compile Include>` в csproj. 🔴 Расхождение здесь не ловится ни сборкой образа, ни
  сборкой тестов — оно проявляется только в проде крашлупом контейнера. Прогнан на файле,
  не вписанном в манифест: краснеет.
- **`SelfCheckTests.cs`** — канарейка гейта (`test-all.ps1 -SelfCheck`).

## Что покрыто

**C#** — парсинг серий (`ParseEp` и ключи/подписи), матчер озвучек (`StudioOf/StudioId/DubsForVideo/NaturalCompare`),
анти-path-traversal (`ConfinedCombine`), анти-SSRF (`IsPrivateHost/IsLoopbackSelf`), bencode-детект, форматтеры
(`HumanSize/QualityFromTitle/MimeType/LangName/SeriesKey/MagnetHash`), SQLite-слой (`SqlContext`/уникальные индексы/дедуп),
qBit-хелперы (`QbitAddMagnet/ResolveFile/ResolveDubFile`) через фейковый HTTP.

**JS** — `slimCard` (детект tv/movie), `cleanName`, `videoFiles`, `esc`, `streamUrl`/`isBrowser` (HLS vs direct),
`relTime`, premium-разблокировка и снятие бренда « - CUB» в `lampainit-invc.js`; матчинг «карточка ↔ загрузка»
(`findDownload`/`normTitle`/`isSerialName` + jsdom-интеграция `addButton`, `qdl-match.test.js`) — строгий id+media_type,
имя только для безметочных раздач (регрессия «Дюна ↔ Дюна: Пророчество»); UX-гарды и очередь транскода
(`qdl-guards.test.js`): `confirmPartial` (гейт недокачанного), двухступенчатое удаление, `dropAudioPref`,
бейдж «⚠ HEVC» в поиске, `pollTranscode` со статусом queued (полл не умирает на очереди).

**C# (§Z)** — `CodecFromTitle`, `PurgeCache` (чистка сирот: порядок seriesKey→watch→db→файлы, гарды re-grab
дублей и вырожденного ключа), очередь транскода (`EnqueueTranscode`/`QueuePosition`/выживаемость воркера),
`CleanupTranscodeParts` — `ImprovementsTests.cs`.

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
