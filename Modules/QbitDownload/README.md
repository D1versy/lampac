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
| `GET /qdl/live/cameras?date=` | **D1VERSY LIVE**: камеры, у которых ЕСТЬ записи за локальный день (+ сколько камер всего) |
| `GET /qdl/live/recordings?camera=&date=` | записи одной камеры за день (время локальное, длительность, размер) |
| `GET /qdl/live/days[?back=]` | дни, за которые записи вообще есть (для выбора даты) |
| `GET /qdl/live/stream?id=` | mp4 записи с видеорегистратора (прокси с Range/seek) |
| `GET /qdl/live/thumb?id=` | кадр-превью записи (прокси) |

## D1VERSY LIVE — записи видеорегистратора (`Live.cs`)
Отдельный домашний проект-регистратор (**IPCamLive**, `C:\IPCamLive`: nginx + FastAPI + SQLite) живёт
только в LAN. Модуль его **не меняет и не рестартит** — ходит read-only GET-ами и проксирует наружу
через наш origin, поэтому пункт меню работает и снаружи (периметр D1Vision закрывает пути сам,
нативные плееры подписывают URL через `D1VAuth`), а LAN-адрес регистратора клиенту не виден.

Грабли, зашитые в реализацию:
- Регистратор пишет **наивный UTC** (его контейнер поднят с `TZ=UTC`). Всё, что уходит клиенту,
  переводится в локальную зону, а «локальные сутки» разворачиваются в UTC-окно, которое задевает
  **две** UTC-даты регистратора → его `by-date` спрашивается за обе и режется окном.
- Записи — готовые mp4 c `moov` в начале (`+faststart` до создания строки в БД), поэтому играем
  их напрямую: Range работает, HLS-обвязка и подпись сегментов не нужны. Непрерывность суток даёт
  `Lampa.Player.playlist()` — плеер сам переходит к следующему куску.
- `stitched.m3u8` регистратора намеренно **не используется**: каждый запрос запускает ремукс всех
  суток и дублирует их на диске.

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
