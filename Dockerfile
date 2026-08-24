# Multi-platform Dockerfile for linux/amd64 and linux/arm64
# Build with: docker buildx build --platform linux/amd64,linux/arm64 -f Dockerfile .

# Global ARGs
ARG DOTNET_VERSION=10.0.11
ARG DOTNET_SDK_VERSION=10.0.400

# Builder image — platform set by buildx
FROM --platform=$BUILDPLATFORM debian:13-slim AS builder

ARG BUILDARCH
ARG TARGETARCH
ARG DOTNET_VERSION
ARG DOTNET_SDK_VERSION

RUN mkdir -p /out

# ⚠️ Порядок слоёв здесь — не стиль, а цена каждой пересборки.
# Раньше apt и весь блок загрузок стояли ПОД `COPY . .`, поэтому любая правка исходника
# инвалидировала их: сборка заново качала SDK, runtime и ffmpeg (~300-400 МБ) и оставляла
# ~3 ГБ кеша, который больше никогда не переиспользовался (за август так накопилось 255 ГБ).
# Теперь всё, что не зависит от кода, стоит ВЫШЕ COPY и переживает правки.

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    libicu76 \
    xz-utils \
    && rm -rf /var/lib/apt/lists/*

# RID нужен и здесь, и в publish, а переменные оболочки между RUN не живут — кладём в файл.
RUN case "$TARGETARCH" in \
    arm64) echo "linux-arm64" > /rid ;; \
    amd64) echo "linux-x64" > /rid ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac

# SDK — только для publish, в образ не едет (стадия builder выбрасывается целиком).
# Распаковывается в /sdk, а не в /out: прежний `rm -rf /out/usr/share/dotnet` был нужен
# ровно потому, что SDK и runtime делили один каталог.
RUN case "$BUILDARCH" in \
    arm64) \
    DOTNET_SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-arm64.tar.gz" \
    ;; \
    amd64) \
    DOTNET_SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
    ;; \
    *) echo "Unsupported BUILDARCH: $BUILDARCH" && exit 1 ;; \
    esac \
    && curl -fSL -o /tmp/dotnet-sdk.tar.gz "${DOTNET_SDK_URL}" \
    && mkdir -p /sdk \
    && tar -oxzf /tmp/dotnet-sdk.tar.gz -C /sdk \
    && rm /tmp/dotnet-sdk.tar.gz

# ASP.NET Core runtime — сразу на своё место в будущем образе.
RUN case "$TARGETARCH" in \
    arm64) \
    DOTNET_RUNTIME_URL="https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${DOTNET_VERSION}/aspnetcore-runtime-${DOTNET_VERSION}-linux-arm64.tar.gz" \
    ;; \
    amd64) \
    DOTNET_RUNTIME_URL="https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${DOTNET_VERSION}/aspnetcore-runtime-${DOTNET_VERSION}-linux-x64.tar.gz" \
    ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac \
    && mkdir -p /out/usr/share/dotnet \
    && curl -fSL -o /tmp/dotnet-runtime.tar.gz "${DOTNET_RUNTIME_URL}" \
    && tar -oxzf /tmp/dotnet-runtime.tar.gz -C /out/usr/share/dotnet \
    && rm /tmp/dotnet-runtime.tar.gz

# FFmpeg & FFprobe — качаются здесь, копируются в /out уже после publish: каталог
# /out/lampac/data создаёт именно publish.
RUN case "$TARGETARCH" in \
    arm64) \
    FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz" \
    ;; \
    amd64) \
    FFMPEG_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz" \
    ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac \
    && curl -fSL -o /tmp/ffmpeg.tar.xz "${FFMPEG_URL}" \
    && mkdir -p /ffmpeg \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /ffmpeg \
    --wildcards "*/bin/ffmpeg" "*/bin/ffprobe" \
    --strip-components=2 \
    && chmod +x /ffmpeg/ffmpeg /ffmpeg/ffprobe \
    && rm /tmp/ffmpeg.tar.xz

WORKDIR /build

# ── Слой восстановления зависимостей ─────────────────────────────────────────
# Раньше restore жил ВНУТРИ publish, то есть ниже `COPY . .`, и пакеты NuGet качались
# заново на каждую пересборку: правка одного .cs инвалидировала слой, а вместе с ним и
# всё восстановление. Теперь restore — отдельный слой, зависящий ТОЛЬКО от файлов
# проектов. Их правят редко, поэтому обычная правка исходника его не трогает вовсе.
#
# `--mount=type=cache` — второй рубеж, на случай когда файлы проектов всё-таки изменились:
# каталог пакетов лежит у нас, вне образа и вне слоёв, и переживает инвалидацию слоя.
# restore тогда отрабатывает заново, но по сети тянет только то, чего у нас ещё нет, —
# то есть действительно новую версию пакета.
# ⚠️ Кеш-маунты требуют BuildKit; он включён по умолчанию с Docker 23 (здесь 29.6).
#
# Публикуется только Core.csproj, а он ссылается ровно на Shared.csproj — поэтому и файлов
# проектов нужно два. Остальные ~120 модулей сервер компилирует сам, Roslyn'ом, на старте.
COPY Core/Core.csproj Core/
COPY Shared/Shared.csproj Shared/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /rid)" \
    && DOTNET_CLI_TELEMETRY_OPTOUT=1 /sdk/dotnet restore --runtime "$RID" \
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
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /rid)" \
    && DOTNET_CLI_TELEMETRY_OPTOUT=1 /sdk/dotnet publish --no-restore --configuration Release --runtime "$RID" --output /out/lampac -p:PlaywrightPlatform="$RID" Core/Core.csproj \
    && mkdir -p /out/lampac/data \
    && cp /ffmpeg/ffmpeg /ffmpeg/ffprobe /out/lampac/data/ \
    && touch /out/lampac/isdocker

# Runner — OS/arch of the published image (amd64 vs arm64)
FROM debian:13-slim AS runner

ARG TARGETARCH

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

# Runtime dependencies + Google Chrome (amd64 / arm64)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    fontconfig \
    gstreamer1.0-libav \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-plugins-base \
    gstreamer1.0-plugins-base-apps \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-ugly \
    gstreamer1.0-tools \
    imagemagick \
    libgstreamer-plugins-base1.0-0 \
    libgstreamer1.0-0 \
    libicu76 \
    libjpeg-dev \
    libnspr4 \
    libpng-dev \
    libwebp-dev \
    && case "$TARGETARCH" in \
    arm64) CHROME_URL="https://dl.google.com/linux/direct/google-chrome-stable_current_arm64.deb" ;; \
    amd64) CHROME_URL="https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb" ;; \
    *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac \
    && curl -fSL -o /tmp/chrome.deb "${CHROME_URL}" \
    && apt-get install -y --no-install-recommends /tmp/chrome.deb \
    && rm -f /tmp/chrome.deb \
    && ln -sf /usr/bin/google-chrome-stable /usr/bin/chromium \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && rm -rf \
    /usr/share/doc \
    /usr/share/man \
    /usr/share/info \
    /usr/share/common-licenses

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
RUN groupadd -r -g 1000 lampac \
    && useradd -r -u 1000 -g lampac -d /lampac lampac

# Copy application
COPY --chown=lampac:lampac --from=builder /out /

# Health check — Kestrel реально отвечает. Раньше был `pgrep -x dotnet`: живой процесс ≠ живой
# сервер (зависший Kestrel healthcheck считал здоровым). start-period больше: старт включает
# Roslyn-компиляцию модулей.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:9118/lampainit.js || exit 1

USER lampac

ENTRYPOINT ["/usr/share/dotnet/dotnet", "Core.dll"]
