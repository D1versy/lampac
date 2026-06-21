# QbitDownload

Модуль для Lampac: кнопка **«Скачать»** в Lampa → загрузка торрента через **qBittorrent** на диск → раздел **«Загрузки»** с просмотром **оффлайн** (файл отдаётся напрямую с диска, без интернета/сидов).

## Как работает
1. Кнопка «Скачать» на карточке фильма ищет раздачи через JacRed (`/api/v1.0/torrents`) и шлёт выбранный magnet в `/qdl/add`.
2. `/qdl/add` логинится в qBittorrent (WebUI API) и добавляет торрент в категорию `lampa`, сохраняя в общую папку `downloadsPath`.
3. qBittorrent качает полный файл на диск (общий том с Lampac).
4. Раздел «Загрузки» (`/qdl/list`) показывает прогресс; воспроизведение идёт через `/qdl/stream` — файл отдаётся с диска с поддержкой Range (перемотка), играет оффлайн.

## Эндпоинты
| Маршрут | Назначение |
|---|---|
| `GET /qdl.js` | клиентский плагин Lampa (кнопка + раздел «Загрузки») |
| `GET /qdl/search?query=&year=` | поиск раздач через JacRed (нормализованный JSON) |
| `GET|POST /qdl/add?magnet=\|parselink=&title=` | добавить в qBittorrent (резолвит parselink → magnet) |
| `GET /qdl/list` | список загрузок (категория `lampa`) с прогрессом |
| `GET /qdl/files?hash=` | файлы торрента |
| `GET /qdl/stream?hash=&index=` | отдать файл с диска (Range/seek, оффлайн) |
| `GET /qdl/delete?hash=&deleteFiles=` | удалить загрузку |

## Конфиг (`init.conf`)
```json
"QbitDownload": {
  "qbitHost": "http://qbittorrent:8080",
  "qbitUser": "admin",
  "qbitPass": "wertykal",
  "downloadsPath": "/downloads",
  "category": "lampa"
}
```
`downloadsPath` — путь, который видят **и** qBittorrent, **и** контейнер Lampac (общий том). В docker-compose: оба монтируют `D:\data\downloads` в `/downloads`.

## Регистрация плагина
В `Modules/LampaWeb/plugins/lampainit-invc.js` (designated-хук) добавлена строка:
`Lampa.Utils.putScriptAsync(["{localhost}/qdl.js"]);`

## Поддержка форка
Вся логика изолирована в этом модуле. Обновление с upstream:
`git fetch upstream && git rebase upstream/main` (конфликт возможен только в `lampainit-invc.js` — одна строка).
