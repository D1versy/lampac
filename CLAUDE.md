# CLAUDE.md — Форк Lampac (приложение)

**Форк Lampac** (.NET 10) для домашнего Lampa-медиасервера.
- origin: `github.com/D1versy/lampac` · upstream: `github.com/lampac-nextgen/lampac` (remote `upstream` добавлен)
- Собирается в Docker-образ **`lampac-custom:latest`**, который запускает медиасервер из `E:\Media-server`.

## Что здесь «наше» (диф против upstream)
Наш код изолирован — почти всё в одном модуле:
- **`Modules/QbitDownload/`** — наш модуль: фича «Скачать» → qBittorrent → раздел «Загрузки» (оффлайн-просмотр).
  - `Controller.cs` — эндпоинты (`/qdl/search|add|list|files|stream|hls|watch`, уведомления, аудиодорожки) + `GET /d1vision/hosts.json` (OTA-список хостов и бренд для клиентов, см. ниже)
  - `plugins/qdl.js` — клиентский плагин Lampa (кнопка «Скачать», грид «Загрузки», экран серий `qdl_episodes`, Rec/эфир, фикс селектбокса — устройство: `E:\Media-server\claude\03`, грабли: `claude/06` §AO)
  - `AppReplace.cs` — **серверный патчер бандла Lampa** (`AppPatch`): подписка на `EventListener.AppReplace` («appjs»), при отдаче `app.min.js` режет по строковым якорям upstream-колокольчик уведомлений (иконка Notice + NoticeCub-опрос), пункты меню Релизы/Расписание/Подписки, фоновый TimeTable-парсер и ряды «Скоро выйдут»/«Недавно вышли». Якорь не найден (сменился tree) → warn в лог + CSS-фолбэк в `lampainit-invc.js`. Наши уведомления qdl это не трогает.
  - `CatalogWarmup.cs` — почасовой прогрев каталога главной (qdl 2.15): учёт клиентских `/cub/tmdb.*` через `EventListener.Middleware` + локальный реплей с оригинальным Host (ключ Staticache = Scheme+Host+Path+Query). Карта внешних источников — медиасервер `claude/11`.
  - `ModInit.cs` (+ хук `MyLocalIp`: внешний IP = DNS своего домена вместо api.ipify.org) · `ModuleConf.cs` · `SqlContext.cs` (SQLite `qdl.db`) · `manifest.json` (⚠️ поле `tree` — ЯВНЫЙ список .cs; новый файл без записи в tree = compilation error и крашлуп контейнера)
- **`Modules/LampaWeb/widgets/samsung/`** — наш кастомный Tizen-виджет D1Vision (задел под Samsung TV): бренд + мульти-хост `loader.js`, сервер собирает `.wgt` по `GET /samsung.wgt`.
- **`Tests/QbitDownload.Tests/`** (C#/xUnit) + **`Tests/js/`** (JS) — наши тесты.
- Правки upstream-файлов (**минимальные**): `Modules/LampaWeb/plugins/lampainit-invc.js` (регистрация `qdl.js` **и `music.js`** + скрытие вкладки CUB + ранний платформенный блок D1Vision, см. ниже), `Modules/LampaWeb/Controllers/ApiController.cs` (5 строк), `Modules/Music/manifest.json` (**флип `enable: true`** — у upstream модуль Music выключен по умолчанию; без флипа `/music.js` отдаёт 404, см. медиасервер `claude/06` §AP), `Core/Startup.cs` (~27 строк: подключение `UseD1VPerimeter` в нужной точке пайплайна, `EnableForHttps` для сжатия, `Cache-Control` статике в `OnPrepareResponse`).
- `README.md`, этот `CLAUDE.md`.

## Что фиксить здесь, а что — в медиасервере

| Что | Где |
|---|---|
| Логика модуля, эндпоинты, клиентский плагин `qdl.js`, тесты | ✅ **здесь** (`E:\lampac`) |
| docker-compose, порты, тома, другие сервисы, скрипты деплоя, общая документация | ➡️ репо медиасервера **`E:\Media-server`** |
| **Значения** настроек модуля (пароли/пути/хосты) | ➡️ `D:\docker\config\lampac\init.conf` (на диске, вне git) |

## Как устроен модуль Lampac (главное)
- Модули в `Modules/<Name>/` = `manifest.json` + `.cs`. **Компилируются в РАНТАЙМЕ через Roslyn** при старте контейнера (в логах: `compilation <Name>` / `loaded module: <Name>`). Пересборка SDK не нужна, но в образ `.cs` копируются (`Core/Core.csproj` глобит `Modules/**`).
- Быстрая итерация без полного билда: `docker cp Modules/QbitDownload lampac:/lampac/module/QbitDownload && docker restart lampac`.
- Ошибки компиляции модуля видны в логах: `docker logs lampac | grep -i qbit`.
- Фронтенд Lampa НЕ в этом репо. В бою (compose медиасервера) он **вендорен**: `E:\Media-server\lampa-vendor\lampa-main` → бинд-том `:ro` в `/lampac/wwwroot/lampa-main` + `LampaWeb.autoupdate: false` в `init.conf` — GitHub не нужен вообще; обновление фронта — осознанный runbook (`E:\Media-server\claude\04-operations.md`). Механизм скачивания (`Modules/LampaWeb/Services/LampaCron.cs`, тянет `yumata/lampa` в `wwwroot/lampa-main/`) остаётся фолбэком для голого запуска. JS-плагины цепляются через `lampainit-invc.js` (`{localhost}` → host).

## OTA-обновления клиентов и платформы (D1Vision)
Все клиенты (D1Vision mac/iOS, LAMPA-App Android TV, задел Tizen, браузер) — тонкие WebView-оболочки: грузят живой веб-интерфейс с этого сервера. **Канонический документ — `E:\Media-server\claude\08-clients.md`.**

- **Серверное (OTA, у всех клиентов мгновенно, без пересборки бинарей)**: весь UI Lampa, все JS-плагины (`qdl.js`, `lampainit-invc.js`), платформенные форс-ключи, бренд, актуальный список хостов. Доставка: рестарт контейнера → новый `?v={cacheVersion}` → клиенты перекачивают JS.
- **Бинарное (пересборка клиентов)**: bootstrap-список хостов, UA-токен, нативный мост AndroidJS/плеер, WebView-обвязка.
- ⚠️ **В бинарь клиентов НЕЛЬЗЯ зашивать**: платформенные форс-ключи, бренд, UI/плагины, список хостов сверх минимального bootstrap — всё это живёт здесь, на сервере, и обновляется по воздуху.
- **Платформенный блок** в `Modules/LampaWeb/plugins/lampainit-invc.js` (ранний, исполняется ДО загрузки Lampa): парсит UA-токен ` d1vision_<platform>/<версия>` (mac|ios|android|tizen|windows; tizen вместо UA сеет `localStorage['d1vision_platform']='tizen'` в loader.js виджета) → `window.d1vision_platform` + `localStorage['d1vision_platform']`; для mac/ios/android/windows форсит `platform/player/player_torrent/player_iptv=android` + `internal_torrclient=true`. Старые бинари с `lampa_client` без токена → fallback android (идемпотентно). Web (без токена) — ничего не форсится.
- **Эндпоинт `GET /d1vision/hosts.json`** (наш модуль, `Modules/QbitDownload/Controller.cs`) → `{"ver":1,"brand":"D1Vision","hosts":[...]}`. Значения — поля `brand`/`clientHosts` секции QbitDownload в `init.conf` (перечитываются на лету, без рестарта). Клиенты кэшируют список нативно и только **ДОПОЛНЯЮТ** им свой зашитый bootstrap (никогда не заменяют — защита от окирпичивания).
- **Самообновление бинарей** (OTA app updates): `GET /d1vision/apps/{platform}/{**file}` (`Controller.cs`) отдаёт билды клиентов (APK/DMG) + манифесты из тома `client-builds` (поле `clientBuildsPath`, дефолт `/client-builds`; смонтирован из репо медиасервера `./client-builds`). `PhysicalFile(..., enableRangeProcessing:true)`, защита пути `ConfinedCombine`, no-cache на `.json`/`.xml`, MIME по расширению (`.apk`/`.dmg`/…). Android-приложение тянет `/d1vision/apps/android/manifest.json` и ставит новее по `versionCode`; Mac — Sparkle-appcast `/d1vision/apps/mac/appcast.xml`. Канон — `E:\Media-server\claude\08-clients.md`.
- **Фолбек-хосты клиентов (приоритет)**: кастомный из настроек → `http://192.168.87.24:9118` (LAN, primary) → `http://tv.d1versy.com:9118` → `http://tv2.d1versy.com:9118` (DNS обоих фолбек-доменов пока НЕ заведён — задел) → OTA-кэш. Проба: `GET <host>/lampainit.js`, таймаут 2.5 с, успех = 200.
- **Tizen-виджет** `Modules/LampaWeb/widgets/samsung/` (index.html + loader.js + config.xml + вендоренный app.js) — теперь наш кастом: бренд D1Vision + мульти-хост перебор + кэш hosts.json/app.js в localStorage. Сервер собирает и подписывает `.wgt` по `GET /samsung.wgt` (нужен флаг `widgets.samsung: true` в `init.conf`, секция LampaWeb).

## Сборка / деплой / синк
- Билд: `docker build -t lampac-custom:latest .` → образ подхватит медиасервер (`docker compose up -d lampac`).
- Синк с upstream делать **из репо медиасервера**: `E:\Media-server\scripts\sync-lampac-fork.ps1`. Страховочный паттерн (бэкап-ветка + бэкап-тег образа + тесты между ребейзом и билдом + headless-проверки) — `claude/06` §AP; после ребейза пушить `--force-with-lease`.
- При rebase конфликты вероятны только в наших точках правок upstream: `lampainit-invc.js` — оставить наши ДВЕ строки `putScriptAsync` (`{localhost}/qdl.js` и `{localhost}/music.js`); `Modules/Music/manifest.json` — оставить наш `"enable": true`.

## Полная документация (живёт в репо медиасервера)
`E:\Media-server\claude\` — вся база знаний по проекту:
- `03-lampac-fork.md` — детально про этот форк, модуль и эндпоинты
- `06-fixes-and-gotchas.md` — боевой лог всех неочевидных багов/фиксов (резолв magnet, qBit v5, HLS-транскод, пути, докачка серий…)
- `05-credentials.md` — секция `QbitDownload` в `init.conf` (пароли/логины)

## Правила
- ⚠️ В коммитах **НЕ указывать соавторство Anthropic** (требование владельца).
- Общение и язык контента — русский.
- ⚠️ **При каждом фиксе клиентских плагинов** (`qdl.js`, `lampainit-invc.js`) **бампать минор `window.qdl_fork_version`** в `lampainit-invc.js` (требование владельца — маркер актуальности кода у клиента). Сам сброс браузерного кэша автоматический: `?v={cacheVersion}` (тики старта процесса) на URL `lampainit.js` (Index) и `qdl.js` (LamInit) в `ApiController.cs` — каждый рестарт контейнера обновляет URL. Если клиент показывает старое поведение — сперва сверить `window.qdl_fork_version` в консоли.
