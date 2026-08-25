# Multi-platform Dockerfile for linux/amd64 and linux/arm64
# Build with: docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile .
#
# ══════════════════════════════════════════════════════════════════════════════
#  ЛОКАЛЬНОЕ ХРАНИЛИЩЕ ЗАВИСИМОСТЕЙ (контекст `assets`)
# ══════════════════════════════════════════════════════════════════════════════
# Требование владельца: «все библиотеки и всё, что нужно для сборки, хранится локально;
# из интернета качаем только когда сами обновили версию». Всё, что раньше тянулось из
# сети на каждой холодной сборке — SDK, ASP.NET-рантайм, ffmpeg, Chrome, пакеты apt и
# NuGet — теперь берётся из каталога на диске, который подключается ИМЕНОВАННЫМ
# контекстом сборки:
#
#   docker buildx build --build-context assets=docker-image://lampac-assets:latest -t lampac-custom .
#
# Собирает и обновляет его `scripts\build-assets.ps1` в репозитории оркестрации
# (E:\Media-server); он же читает отсюда ARG'и версий — это единственный источник правды.
# Хранилище — ОБРАЗ, а не папка: перенос файлов Windows↔ВМ докера на этой машине идёт
# ~250 КБ/с, разбор — claude/06 §CG. Папку подключить тоже можно, Dockerfile не различает.
#
# 🔴 Контекст НЕОБЯЗАТЕЛЬНЫЙ, и это принципиально. `FROM scratch AS assets` ниже —
#    пустая заглушка: без флага каждый шаг видит пустой /assets, не находит своего файла
#    и честно качает из сети, как раньше. Поэтому голый `docker build .` на чужой машине
#    (и arm64, для которого файлов в хранилище нет) продолжает работать.
#    А вот если хранилище ЗАЯВЛЕНО (см. ARG ASSETS_STRICT ниже) — тихого ухода в сеть быть
#    не должно: это была бы сборка, которая «пользуется хранилищем» только на бумаге.
#
# Порядок слоёв здесь — не стиль, а цена каждой пересборки:
#   • всё, что не зависит от кода, стоит ВЫШЕ `COPY . .` и переживает правку исходника;
#   • restore — отдельный слой, зависящий только от файлов проектов;
#   • в runner вывод разложен по слоям от самого стабильного к самому горячему, иначе
#     экспорт образа упаковывает и распаковывает 800 МБ ради правки одного .cs.

# Global ARGs
ARG DOTNET_VERSION=10.0.11
ARG DOTNET_SDK_VERSION=10.0.400

# 🔴 Строгий режим хранилища. Его выставляет build-image.ps1 РОВНО ТОГДА, когда сам же
# подключил контекст `assets`: если хранилище заявлено, но зависимость из него взять не
# вышло — сборка обязана упасть, а не тихо уйти в сеть.
# Цена молчания измерена: первая же версия сборщика хранилища теряла .deb от curl, офлайн-
# установка в runner падала на «Unable to fetch some archives», сборка оставалась ЗЕЛЁНОЙ и
# качала 387 пакетов. Заметить это можно было только по секундомеру — то есть никогда.
# Без флага (голый `docker build .`) поведение прежнее: чего нет — качаем.
ARG ASSETS_STRICT=0

# ── Хранилище зависимостей ───────────────────────────────────────────────────
# Переопределяется флагом `--build-context assets=<путь>`; без него — пусто.
FROM scratch AS assets

# Builder image — platform set by buildx
FROM --platform=$BUILDPLATFORM debian:13-slim AS builder

ARG BUILDARCH
ARG TARGETARCH
ARG DOTNET_VERSION
ARG DOTNET_SDK_VERSION
ARG ASSETS_STRICT

RUN mkdir -p /out

# apt для самой сборки. Локальные .deb и списки пакетов лежат в /assets/apt/builder;
# `--no-download` заставляет apt взять всё из /var/cache/apt/archives и НЕ ходить в сеть.
# Если офлайн-установка не сложилась (список пакетов разошёлся с хранилищем, другая
# архитектура, пустой /assets) — обычная установка из сети, как раньше.
# Копия .deb внутрь кеша apt живёт только внутри этого RUN: слой — это разница на его
# конце, а к концу и архивы, и списки уже удалены.
COPY docker/apt-builder.pkgs /apt.pkgs
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    PKGS="$(grep -vE '^[[:space:]]*(#|$)' /apt.pkgs | tr '\n' ' ')"; \
    offline=0; \
    if [ -d /assets/apt/builder/archives ] && [ -d /assets/apt/builder/lists ]; then \
    cp -a /assets/apt/builder/lists/. /var/lib/apt/lists/ 2>/dev/null || true; \
    mkdir -p /var/cache/apt/archives; \
    cp -a /assets/apt/builder/archives/. /var/cache/apt/archives/ 2>/dev/null || true; \
    if apt-get install -y --no-install-recommends --no-download $PKGS; then \
    offline=1; echo "[assets] apt builder: установлено офлайн"; \
    else \
    echo "[assets] apt builder: офлайн не сложился, идём в сеть"; \
    dpkg --configure -a || true; \
    fi; \
    fi; \
    if [ "$offline" = "0" ]; then \
    if [ "$ASSETS_STRICT" = "1" ]; then \
    echo "🔴 хранилище подключено, но пакеты стадии builder из него не встали." >&2; \
    echo "   пересобрать хранилище:  .\\scripts\\build-assets.ps1" >&2; \
    echo "   собрать из сети:        .\\scripts\\build-image.ps1 -AllowNetworkFallback" >&2; \
    exit 1; \
    fi; \
    echo "[network] apt builder: apt-get update + install"; \
    apt-get update; \
    apt-get install -y --no-install-recommends $PKGS; \
    fi; \
    rm -f /apt.pkgs; \
    apt-get clean; \
    rm -rf /var/lib/apt/lists/* /var/cache/apt/archives/*.deb /var/cache/apt/archives/partial

# RID нужен и здесь, и в publish, а переменные оболочки между RUN не живут — кладём в файл.
RUN case "$TARGETARCH" in \
    arm64) echo "linux-arm64" > /rid ;; \
    amd64) echo "linux-x64" > /rid ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac

# SDK — только для publish, в образ не едет (стадия builder выбрасывается целиком).
# Распаковывается в /sdk, а не в /out: прежний `rm -rf /out/usr/share/dotnet` был нужен
# ровно потому, что SDK и runtime делили один каталог.
# Из хранилища распаковывается ПРЯМО с примонтированного файла — лишней копии нет.
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    case "$BUILDARCH" in \
    arm64) SDK_FILE="dotnet-sdk-${DOTNET_SDK_VERSION}-linux-arm64.tar.gz" ;; \
    amd64) SDK_FILE="dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" ;; \
    *) echo "Unsupported BUILDARCH: $BUILDARCH" >&2; exit 1 ;; \
    esac; \
    mkdir -p /sdk; \
    if [ -f "/assets/dotnet/$SDK_FILE" ]; then \
    echo "[assets] SDK из хранилища: $SDK_FILE"; \
    tar -oxzf "/assets/dotnet/$SDK_FILE" -C /sdk; \
    else \
    if [ "$ASSETS_STRICT" = "1" ]; then \
    echo "🔴 хранилище подключено, но SDK .NET в нём не найден." >&2; \
    echo "   пересобрать хранилище:  .\\scripts\\build-assets.ps1" >&2; \
    echo "   собрать из сети:        .\\scripts\\build-image.ps1 -AllowNetworkFallback" >&2; \
    exit 1; \
    fi; \
    echo "[network] SDK качается: $SDK_FILE"; \
    curl -fSL --no-progress-meter -o /tmp/dotnet-sdk.tar.gz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/${SDK_FILE}"; \
    tar -oxzf /tmp/dotnet-sdk.tar.gz -C /sdk; \
    rm /tmp/dotnet-sdk.tar.gz; \
    fi

# ASP.NET Core runtime — сразу на своё место в будущем образе.
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    case "$TARGETARCH" in \
    arm64) RT_FILE="aspnetcore-runtime-${DOTNET_VERSION}-linux-arm64.tar.gz" ;; \
    amd64) RT_FILE="aspnetcore-runtime-${DOTNET_VERSION}-linux-x64.tar.gz" ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    mkdir -p /out/usr/share/dotnet; \
    if [ -f "/assets/dotnet/$RT_FILE" ]; then \
    echo "[assets] ASP.NET runtime из хранилища: $RT_FILE"; \
    tar -oxzf "/assets/dotnet/$RT_FILE" -C /out/usr/share/dotnet; \
    else \
    if [ "$ASSETS_STRICT" = "1" ]; then \
    echo "🔴 хранилище подключено, но рантайм .NET в нём не найден." >&2; \
    echo "   пересобрать хранилище:  .\\scripts\\build-assets.ps1" >&2; \
    echo "   собрать из сети:        .\\scripts\\build-image.ps1 -AllowNetworkFallback" >&2; \
    exit 1; \
    fi; \
    echo "[network] ASP.NET runtime качается: $RT_FILE"; \
    curl -fSL --no-progress-meter -o /tmp/dotnet-runtime.tar.gz "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${DOTNET_VERSION}/${RT_FILE}"; \
    tar -oxzf /tmp/dotnet-runtime.tar.gz -C /out/usr/share/dotnet; \
    rm /tmp/dotnet-runtime.tar.gz; \
    fi

# FFmpeg & FFprobe — в /ffmpeg. В образ уезжают ОТДЕЛЬНЫМ слоем из runner: 281 МБ
# статических бинарей не должны лежать в одном слое с горячим кодом.
# ⚠️ URL у BtbN — «latest», версии в имени нет. Хранилище фиксирует то, что однажды
#    скачали: обновление ffmpeg — это осознанный `build-assets.ps1 -Refresh`, а не
#    побочный эффект случайной пересборки (иначе плеер мог бы поменяться посреди ночи).
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    case "$TARGETARCH" in \
    arm64) FF_FILE="ffmpeg-master-latest-linuxarm64-gpl.tar.xz" ;; \
    amd64) FF_FILE="ffmpeg-master-latest-linux64-gpl.tar.xz" ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    mkdir -p /ffmpeg; \
    if [ -f "/assets/ffmpeg/$FF_FILE" ]; then \
    echo "[assets] ffmpeg из хранилища: $FF_FILE"; \
    tar -xJf "/assets/ffmpeg/$FF_FILE" -C /ffmpeg --wildcards "*/bin/ffmpeg" "*/bin/ffprobe" --strip-components=2; \
    else \
    if [ "$ASSETS_STRICT" = "1" ]; then \
    echo "🔴 хранилище подключено, но ffmpeg в нём не найден." >&2; \
    echo "   пересобрать хранилище:  .\\scripts\\build-assets.ps1" >&2; \
    echo "   собрать из сети:        .\\scripts\\build-image.ps1 -AllowNetworkFallback" >&2; \
    exit 1; \
    fi; \
    echo "[network] ffmpeg качается: $FF_FILE"; \
    curl -fSL --no-progress-meter -o /tmp/ffmpeg.tar.xz "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FF_FILE}"; \
    tar -xJf /tmp/ffmpeg.tar.xz -C /ffmpeg --wildcards "*/bin/ffmpeg" "*/bin/ffprobe" --strip-components=2; \
    rm /tmp/ffmpeg.tar.xz; \
    fi; \
    chmod +x /ffmpeg/ffmpeg /ffmpeg/ffprobe

WORKDIR /build

# ── Слой восстановления зависимостей ─────────────────────────────────────────
# Раньше restore жил ВНУТРИ publish, то есть ниже `COPY . .`, и пакеты NuGet качались
# заново на каждую пересборку: правка одного .cs инвалидировала слой, а вместе с ним и
# всё восстановление. Теперь restore — отдельный слой, зависящий ТОЛЬКО от файлов
# проектов. Их правят редко, поэтому обычная правка исходника его не трогает вовсе.
#
# Три рубежа обороны, в порядке дешевизны:
#   1. слой закеширован — restore не выполняется вообще;
#   2. `--mount=type=cache` — каталог пакетов лежит вне образа и переживает инвалидацию
#      слоя; restore отрабатывает, но по сети тянет только реально новое;
#   3. `/assets/nuget` — плоский фид .nupkg на диске: даже на ПОЛНОСТЬЮ холодной машине
#      (снесли и слои, и кеш-маунт) restore идёт с локального диска, а не с nuget.org.
#      Не хватило пакета — молча уходим в обычный restore, то есть новая зависимость
#      подтягивается сама и сборка не краснеет.
# ⚠️ Кеш-маунты требуют BuildKit; он включён по умолчанию с Docker 23 (здесь 29.6).
#
# Публикуется только Core.csproj, а он ссылается ровно на Shared.csproj — поэтому и файлов
# проектов нужно два. Остальные ~120 модулей сервер компилирует сам, Roslyn'ом, на старте.
COPY Core/Core.csproj Core/
COPY Shared/Shared.csproj Shared/

RUN --mount=type=bind,from=assets,target=/assets \
    --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    set -e; \
    RID="$(cat /rid)"; \
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1; \
    if [ -d /assets/nuget ] && [ -n "$(ls -A /assets/nuget 2>/dev/null)" ]; then \
    echo "[assets] restore из локального фида /assets/nuget"; \
    if /sdk/dotnet restore --runtime "$RID" --source /assets/nuget \
    -p:Configuration=Release -p:PlaywrightPlatform="$RID" Core/Core.csproj; then \
    exit 0; \
    fi; \
    echo "[assets] локального фида не хватило — обычный restore"; \
    fi; \
    /sdk/dotnet restore --runtime "$RID" \
    -p:Configuration=Release -p:PlaywrightPlatform="$RID" Core/Core.csproj

# 🔴 Точка инвалидации кеша. Всё, что выше, правку исходников переживает.
COPY . .

# Build the application
# --no-restore: зависимости уже восстановлены слоем выше, второй раз графы не считаем.
# Тот же кеш-маунт подключён и сюда — обязательно: пакеты лежат в кеше, а не в слое,
# и без маунта publish их просто не найдёт.
# ⚠️ Единственный способ это сломать — снести ИМЕННО кеш-маунт, не тронув слои
# (`docker builder prune --filter type=exec.cachemount`): тогда restore-слой считается
# готовым, а пакетов нет. Лечится `docker build --no-cache`. Обычный `docker builder prune`
# сносит и слои, поэтому restore честно отработает заново.
#
# Разнос вывода по «температуре» (см. блок слоёв в runner): то, что publish выкладывает,
# но что от нашего кода не зависит — данные апстрима и восстановленные сборки NuGet —
# уезжает в /stage. Иначе оно лежало бы в одном слое с module/ и переупаковывалось на
# каждую правку .cs.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    set -e; \
    RID="$(cat /rid)"; \
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1; \
    /sdk/dotnet publish --no-restore --configuration Release --runtime "$RID" \
    --output /out/lampac -p:PlaywrightPlatform="$RID" Core/Core.csproj; \
    touch /out/lampac/isdocker; \
    mkdir -p /stage; \
    if [ -d /out/lampac/data ];     then mv /out/lampac/data     /stage/data;     fi; \
    if [ -d /out/lampac/runtimes ]; then mv /out/lampac/runtimes /stage/runtimes; fi; \
    mkdir -p /stage/data /stage/runtimes

# ══════════════════════════════════════════════════════════════════════════════
#  Сбор локального хранилища зависимостей (обычной сборкой НЕ выполняется)
# ══════════════════════════════════════════════════════════════════════════════
# Эти стадии существуют ради одной команды из репозитория оркестрации:
#
#   docker buildx build --target assets-image -t lampac-assets:latest .      (scripts\build-assets.ps1)
#
# Результат — образ, чей корень выглядит РОВНО как /assets выше: dotnet\, ffmpeg\,
# chrome\, apt\builder\, apt\runner\, nuget\. Его и подключают обратно:
#
#   docker buildx build --build-context assets=docker-image://lampac-assets:latest .
#
# 🔴 Почему образ, а не папка на диске. Обмен файлами между Windows и ВМ докера на этой
#    машине идёт ~250 КБ/с (замерено: 100 МБ контекста — 6 минут; тот же файл с самого
#    Windows качается на 70 МБ/с). Хранилище-папка пришлось бы переливать через эту
#    границу на каждой инвалидации снимка контекста — то есть «ускорение» временами
#    оборачивалось бы часом ожидания. Образ живёт ВНУТРИ ВМ: сборка читает его как
#    обычные слои, границу не пересекает никто.
#    Папка при этом продолжает работать (Dockerfile не знает, что ему подключили) —
#    build-assets.ps1 умеет выгрузить хранилище на диск флагом -Export.
#
# 🔴 Списки пакетов читаются из тех же docker/apt-*.pkgs, что и установка. Дублировать их
#    в скрипте было нельзя: разъехавшись, хранилище молча собиралось бы не под тот образ,
#    офлайн-ветка каждый раз падала бы в сеть, и «ускорение» жило бы только на бумаге.
# ⚠️ Стадии стоят ВЫШЕ runner намеренно: `docker build` без --target собирает ПОСЛЕДНЮЮ
#    стадию файла, и последней обязан остаться runner.

# Архивы, у которых нет своего пакетного менеджера. Берутся из уже существующего
# хранилища, если оно подключено (пересборка хранилища не перекачивает то, что уже в нём),
# иначе из сети.
FROM debian:13-slim AS blob-harvest
ARG DOTNET_VERSION
ARG DOTNET_SDK_VERSION
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    case "$TARGETARCH" in \
    arm64) ARCH=arm64; FFARCH=linuxarm64; DEBARCH=arm64 ;; \
    *)     ARCH=x64;   FFARCH=linux64;    DEBARCH=amd64 ;; \
    esac; \
    SDK="dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${ARCH}.tar.gz"; \
    RT="aspnetcore-runtime-${DOTNET_VERSION}-linux-${ARCH}.tar.gz"; \
    FF="ffmpeg-master-latest-${FFARCH}-gpl.tar.xz"; \
    CH="google-chrome-stable_current_${DEBARCH}.deb"; \
    mkdir -p /harvest/dotnet /harvest/ffmpeg /harvest/chrome; \
    grab() { \
    if [ -f "/assets/$1" ]; then echo "[assets] $1"; cp "/assets/$1" "/harvest/$1"; \
    else echo "[network] $2"; curl -fSL --no-progress-meter -o "/harvest/$1" "$2"; fi; \
    }; \
    grab "dotnet/$SDK" "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/${SDK}"; \
    grab "dotnet/$RT"  "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${DOTNET_VERSION}/${RT}"; \
    grab "ffmpeg/$FF"  "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FF}"; \
    grab "chrome/$CH"  "https://dl.google.com/linux/direct/${CH}"; \
    du -sh /harvest/*

FROM debian:13-slim AS apt-harvest-builder
COPY docker/apt-builder.pkgs /apt.pkgs
# Досбор, а не пересбор: то, что уже лежит в подключённом хранилище, кладётся в кеш apt,
# и `--download-only` качает ТОЛЬКО недостающее. Иначе каждое обновление хранилища тянуло
# бы все пакеты заново — а на этой машине контейнерная сеть медленная (claude/06 §CG).
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    PKGS="$(grep -vE '^[[:space:]]*(#|$)' /apt.pkgs | tr '\n' ' ')"; \
    mkdir -p /var/cache/apt/archives; \
    if [ -d /assets/apt/builder/archives ]; then \
    cp -a /assets/apt/builder/archives/. /var/cache/apt/archives/ 2>/dev/null || true; \
    fi; \
    apt-get update; \
    apt-get -o APT::Keep-Downloaded-Packages=true install -y --no-install-recommends \
    --download-only $PKGS; \
    mkdir -p /harvest/archives /harvest/lists; \
    cp -a /var/cache/apt/archives/*.deb /harvest/archives/; \
    cp -a /var/lib/apt/lists/. /harvest/lists/; \
    rm -rf /harvest/lists/partial /harvest/lists/auxfiles; \
    rm -f /var/cache/apt/archives/*.deb; \
    echo "собрано .deb: $(ls -1 /harvest/archives | wc -l)"

FROM debian:13-slim AS apt-harvest-runner
COPY docker/apt-runner.pkgs /apt.pkgs
# 🔴 Chrome приезжает готовым из blob-harvest, а не качается здесь. Прежняя форма ставила
#    ради curl «ca-certificates curl» — и apt, у которого APT::Keep-Downloaded-Packages по
#    умолчанию false, УДАЛЯЛ их .deb сразу после установки. В хранилище этих пакетов не
#    оказывалось, офлайн-установка в runner падала на «Unable to fetch some archives» и
#    молча уходила в сеть: сборка зелёная, 387 пакетов качаются, «локальное хранилище»
#    существует только на бумаге. Теперь единственная команда apt здесь — --download-only,
#    которая ничего не устанавливает и, значит, ничего не удаляет.
COPY --from=blob-harvest /harvest/chrome/ /chrome/
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    PKGS="$(grep -vE '^[[:space:]]*(#|$)' /apt.pkgs | tr '\n' ' ')"; \
    CHROME_DEB="$(ls /chrome/*.deb | head -1)"; \
    mkdir -p /var/cache/apt/archives; \
    if [ -d /assets/apt/runner/archives ]; then \
    cp -a /assets/apt/runner/archives/. /var/cache/apt/archives/ 2>/dev/null || true; \
    fi; \
    apt-get update; \
    apt-get -o APT::Keep-Downloaded-Packages=true install -y --no-install-recommends \
    --download-only $PKGS "$CHROME_DEB"; \
    mkdir -p /harvest/archives /harvest/lists; \
    cp -a /var/cache/apt/archives/*.deb /harvest/archives/; \
    cp -a /var/lib/apt/lists/. /harvest/lists/; \
    rm -rf /harvest/lists/partial /harvest/lists/auxfiles; \
    rm -f /var/cache/apt/archives/*.deb; \
    echo "собрано .deb: $(ls -1 /harvest/archives | wc -l)"

# Пакеты NuGet берём из кеш-маунта той же сборки: там лежит ровно то, что понадобилось
# restore. `cp -n` — чтобы повторный сбор не переписывал уже собранное.
FROM builder AS nuget-harvest
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    set -e; \
    mkdir -p /harvest; \
    find /root/.nuget/packages -name '*.nupkg' -type f -exec cp -n {} /harvest/ \; ; \
    echo "собрано .nupkg: $(ls -1 /harvest | wc -l)"

# Собственно хранилище. Корень образа = то, что стадии выше монтируют как /assets.
# Тем же таргетом делается и выгрузка на диск (`--output type=local,dest=…`): формат один.
FROM scratch AS assets-image
COPY --from=blob-harvest        /harvest/ /
COPY --from=apt-harvest-builder /harvest/ /apt/builder/
COPY --from=apt-harvest-runner  /harvest/ /apt/runner/
COPY --from=nuget-harvest       /harvest/ /nuget/

# Runner — OS/arch of the published image (amd64 vs arm64)
FROM debian:13-slim AS runner

ARG TARGETARCH
ARG ASSETS_STRICT

LABEL org.opencontainers.image.description="Lampac NextGen - Media aggregator" \
    org.opencontainers.image.licenses="MIT" \
    org.opencontainers.image.source="https://github.com/lampac-nextgen/lampac" \
    org.opencontainers.image.vendor="Lampac NextGen"

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH="${PATH}:/usr/share/dotnet" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    CHROMIUM_PATH=/usr/bin/google-chrome-stable \
    CHROMIUM_FLAGS="--no-sandbox --disable-setuid-sandbox --disable-dev-shm-usage"

WORKDIR /lampac
EXPOSE 9118

# Runtime dependencies + Google Chrome (amd64 / arm64).
# Тот же приём, что в builder: .deb и списки из /assets/apt/runner, `--no-download`,
# сеть только как запасной путь. Chrome — отдельным файлом, у его URL версии в имени нет
# («current»), поэтому хранилище фиксирует ровно ту сборку, которую однажды скачали.
#
# Две грабли apt, обе стоили по итерации сборки (разбор — Media-server\claude\06 §CG):
# 🔴 `apt-get install --no-download <путь/к.deb>` не работает В ПРИНЦИПЕ: «E: Internal
#    Error, Pathname to install is not absolute». Поэтому список пакетов и Chrome ставятся
#    РАЗНЫМИ командами: пакеты — apt'ом, Chrome — dpkg'ом, а его собственные зависимости
#    (fonts-liberation, wget, xdg-utils — в замыкании нашего списка их нет) доставляет
#    `-f install`, тоже офлайн.
# 🔴 apt в режиме `--no-download` считает содержимое /var/cache/apt/archives «только что
#    скачанным» и по завершении ВЫЧИЩАЕТ каталог целиком (замер: 385 файлов → 0), причём
#    APT::Keep-Downloaded-Packages это не отменяет. Отсюда `seed` перед КАЖДОЙ командой.
# ⚠️ В сетевой ветке Chrome берётся ПОСЛЕ установки пакетов: в debian:13-slim нет curl,
#    и попытка скачать его первой падала с exit 127 «curl: not found».
COPY docker/apt-runner.pkgs /apt.pkgs
RUN --mount=type=bind,from=assets,target=/assets \
    set -e; \
    PKGS="$(grep -vE '^[[:space:]]*(#|$)' /apt.pkgs | tr '\n' ' ')"; \
    case "$TARGETARCH" in \
    arm64) CHROME_DEB="google-chrome-stable_current_arm64.deb" ;; \
    amd64) CHROME_DEB="google-chrome-stable_current_amd64.deb" ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    offline=0; \
    if [ -d /assets/apt/runner/archives ] && [ -d /assets/apt/runner/lists ] && [ -f "/assets/chrome/$CHROME_DEB" ]; then \
    cp -a /assets/apt/runner/lists/. /var/lib/apt/lists/ 2>/dev/null || true; \
    mkdir -p /var/cache/apt/archives; \
    seed() { cp -a /assets/apt/runner/archives/. /var/cache/apt/archives/ 2>/dev/null || true; }; \
    cp "/assets/chrome/$CHROME_DEB" /tmp/chrome.deb; \
    if seed && apt-get install -y --no-install-recommends --no-download $PKGS \
    && seed \
    && { dpkg -i /tmp/chrome.deb || apt-get install -y --no-install-recommends --no-download -f; } \
    && dpkg -s google-chrome-stable >/dev/null 2>&1; then \
    offline=1; echo "[assets] apt runner: установлено офлайн"; \
    else \
    echo "[assets] apt runner: офлайн не сложился, идём в сеть"; \
    dpkg --configure -a || true; \
    fi; \
    fi; \
    if [ "$offline" = "0" ]; then \
    if [ "$ASSETS_STRICT" = "1" ]; then \
    echo "🔴 хранилище подключено, но пакеты финального образа из него не встали." >&2; \
    echo "   пересобрать хранилище:  .\\scripts\\build-assets.ps1" >&2; \
    echo "   собрать из сети:        .\\scripts\\build-image.ps1 -AllowNetworkFallback" >&2; \
    exit 1; \
    fi; \
    echo "[network] apt runner: apt-get update + install"; \
    apt-get update; \
    apt-get install -y --no-install-recommends $PKGS; \
    if [ -f "/assets/chrome/$CHROME_DEB" ]; then \
    echo "[assets] Chrome из хранилища: $CHROME_DEB"; \
    cp "/assets/chrome/$CHROME_DEB" /tmp/chrome.deb; \
    else \
    echo "[network] Chrome качается: $CHROME_DEB"; \
    curl -fSL --no-progress-meter -o /tmp/chrome.deb "https://dl.google.com/linux/direct/${CHROME_DEB}"; \
    fi; \
    apt-get install -y --no-install-recommends /tmp/chrome.deb; \
    fi; \
    rm -f /tmp/chrome.deb /apt.pkgs; \
    ln -sf /usr/bin/google-chrome-stable /usr/bin/chromium; \
    apt-get clean; \
    rm -rf /var/lib/apt/lists/* /var/cache/apt/archives/*.deb /var/cache/apt/archives/partial; \
    rm -rf /usr/share/doc /usr/share/man /usr/share/info /usr/share/common-licenses

# Промежуточные сертификаты, которые сайты трекеров забывают досылать в цепочке.
# Проблема: torrent.by отдаёт ТОЛЬКО лист без промежуточного YE2 → OpenSSL внутри
# контейнера обрывает рукопожатие («unable to get local issuer certificate»), хотя из
# Windows на хосте сайт открывается: schannel умеет дотягивать недостающее звено по AIA,
# а curl/OpenSSL — нет. Локальная копия промежуточного закрывает дыру.
# ⚠️ Если трекер снова отвалится с unknown CA — значит он перевыпустился под другим
# промежуточным. Лечится за минуту: взять URL из AIA сертификата и положить сюда файл
#   openssl s_client -connect <host>:443 -servername <host> </dev/null | \
#     openssl x509 -noout -text | grep -A1 "Authority Information Access"
#   curl -s <URI> | openssl x509 -inform DER -out certs/<name>.pem
COPY certs/*.pem /usr/local/share/ca-certificates/
RUN for f in /usr/local/share/ca-certificates/*.pem; do mv "$f" "${f%.pem}.crt"; done \
    && update-ca-certificates

# Create non-root user before COPY to use --chown
# 🔴 chown на сам /lampac обязателен. Раньше владельцем его делал единственный
#    `COPY --chown=… /out /`: каталог lampac приезжал ИЗ источника, и --chown ложился на
#    него самого. После разреза на слои назначение уже существует (его создал WORKDIR от
#    root), COPY кладёт внутрь содержимое и владельца каталога НЕ меняет. Сервер на старте
#    делает mkdir /lampac/logs и падал с «Access to the path '/lampac/logs' is denied» —
#    поймано деплоем, потому что sync-lampac-fork.ps1 ждёт healthy, а не «контейнер создан».
RUN groupadd -r -g 1000 lampac \
    && useradd -r -u 1000 -g lampac -d /lampac lampac \
    && chown lampac:lampac /lampac

# ── Слои приложения: от самого холодного к самому горячему ───────────────────
# 🔴 Раньше здесь был ОДИН `COPY --from=builder /out /` на 800 МБ. Слой пересоздаётся
#    целиком от любой правки .cs, а экспорт образа — это упаковка и распаковка всех его
#    байт: 29 с упаковки + 7 с распаковки на каждую сборку, больше половины её времени.
#    Разрезано по «температуре»; порядок обязателен — слой инвалидируется, если изменился
#    любой из родительских, поэтому стабильное обязано лежать НИЖЕ горячего.
#
#      /usr/share/dotnet   106 МБ  меняется только при бампе DOTNET_VERSION
#      data/ffmpeg,ffprobe 281 МБ  статические бинари, меняются осознанным -Refresh
#      data/*               65 МБ  json/mmdb апстрима
#      runtimes/            30 МБ  сборки NuGet (их переносит туда LayoutManagedReferences…)
#      остальное          ~160 МБ  module/, wwwroot/, Core.dll — вот это и есть наш код
#
#    Итог: правка .cs переупаковывает ~160 МБ вместо 800.
# ⚠️ COPY каталога в существующий каталог СЛИВАЕТ содержимое, а не заменяет его —
#    поэтому data/ собирается из двух источников и ffmpeg переживает следующий COPY.
COPY --chown=lampac:lampac --from=builder /out/usr/share/dotnet /usr/share/dotnet
COPY --chown=lampac:lampac --from=builder /ffmpeg/ /lampac/data/
COPY --chown=lampac:lampac --from=builder /stage/data /lampac/data
COPY --chown=lampac:lampac --from=builder /stage/runtimes /lampac/runtimes
COPY --chown=lampac:lampac --from=builder /out/lampac /lampac

# Health check — Kestrel реально отвечает. Раньше был `pgrep -x dotnet`: живой процесс ≠ живой
# сервер (зависший Kestrel healthcheck считал здоровым). start-period больше: старт включает
# Roslyn-компиляцию модулей.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:9118/lampainit.js || exit 1

USER lampac

ENTRYPOINT ["/usr/share/dotnet/dotnet", "Core.dll"]
