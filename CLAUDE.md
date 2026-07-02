# CLAUDE.md — Форк Lampac (приложение)

**Форк Lampac** (.NET 10) для домашнего Lampa-медиасервера.
- origin: `github.com/D1versy/lampac` · upstream: `github.com/lampac-nextgen/lampac` (remote `upstream` добавлен)
- Собирается в Docker-образ **`lampac-custom:latest`**, который запускает медиасервер из `E:\Media-server`.

## Что здесь «наше» (диф против upstream)
Наш код изолирован — почти всё в одном модуле:
- **`Modules/QbitDownload/`** — наш модуль: фича «Скачать» → qBittorrent → раздел «Загрузки» (оффлайн-просмотр).
  - `Controller.cs` — эндпоинты (`/qdl/search|add|list|files|stream|hls|watch`, уведомления, аудиодорожки)
  - `plugins/qdl.js` — клиентский плагин Lampa (кнопка «Скачать», грид «Загрузки»)
  - `ModInit.cs` · `ModuleConf.cs` · `SqlContext.cs` (SQLite `qdl.db`) · `manifest.json`
- **`Tests/QbitDownload.Tests/`** (C#/xUnit) + **`Tests/js/`** (JS) — наши тесты.
- Правки upstream-файлов (**минимальные**): `Modules/LampaWeb/plugins/lampainit-invc.js` (регистрация `qdl.js` + скрытие вкладки CUB), `Modules/LampaWeb/Controllers/ApiController.cs` (5 строк).
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
- Фронтенд Lampa НЕ в репо — качается в рантайме (`Modules/LampaWeb/Services/LampaCron.cs` тянет `yumata/lampa` в `wwwroot/lampa-main/`). JS-плагины цепляются через `lampainit-invc.js` (`{localhost}` → host).

## Сборка / деплой / синк
- Билд: `docker build -t lampac-custom:latest .` → образ подхватит медиасервер (`docker compose up -d lampac`).
- Синк с upstream делать **из репо медиасервера**: `E:\Media-server\scripts\sync-lampac-fork.ps1`.
- При rebase конфликт возможен только в `lampainit-invc.js` (одна строка) — оставить нашу `putScriptAsync(["{localhost}/qdl.js"])`.

## Полная документация (живёт в репо медиасервера)
`E:\Media-server\claude\` — вся база знаний по проекту:
- `03-lampac-fork.md` — детально про этот форк, модуль и эндпоинты
- `06-fixes-and-gotchas.md` — боевой лог всех неочевидных багов/фиксов (резолв magnet, qBit v5, HLS-транскод, пути, докачка серий…)
- `05-credentials.md` — секция `QbitDownload` в `init.conf` (пароли/логины)

## Правила
- ⚠️ В коммитах **НЕ указывать соавторство Anthropic** (требование владельца).
- Общение и язык контента — русский.
