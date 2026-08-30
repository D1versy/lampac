'use strict';
// Сторож наших правок внутри АПСТРИМНОГО модуля Modules/Music (qdl 2.86).
//
// Зачем именно сторож, а не «просто помнить». Папка `Modules/Music` при синке берётся с апстрима
// ЦЕЛИКОМ (`git checkout upstream/main -- Modules/Music`, медиасервер claude/06 §CR) — так решили,
// когда шесть апстримных коммитов подряд спотыкались об одну нашу правку. Значит любая правка
// внутри папки теряется МОЛЧА. Раньше их было две (manifest.json + Controllers/ApiController.cs),
// после разбора Playwright-прогона стало семь, и держать это в голове больше нельзя.
//
// Этот файл читается ногой `fork-js`, которую sync-скрипт гоняет МЕЖДУ ребейзом и сборкой:
// правка исчезла → гейт красный → возвращаем по маркеру, а не обнаруживаем через месяц на клиенте.
// Схема повторяет Tests/js/rch-cors.test.js: (1) маркер на месте, (2) апстримной формы больше нет,
// (3) файл остаётся валидным.
//
// ⚠️ Тесты ТЕКСТОВЫЕ и это осознанно: файлы `Modules/Music` в тест-проект не линкуются —
// они держат usings в GlobalUsings.cs, а его нельзя тащить в чужую сборку (global using поедет
// на всё), при том что у Tests/QbitDownload.Tests стоит `ImplicitUsings: disable`.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const H = require('./harness');

const MUSIC = path.join(H.REPO, 'Modules', 'Music');
const read = (...parts) => fs.readFileSync(path.join(MUSIC, ...parts), 'utf8').replace(/\r\n/g, '\n');

const plugin = read('plugin.js');
const imgProxy = read('Services', 'Images', 'MusicImageProxyService.cs');
const cache = read('Services', 'Cache', 'MusicMetadataCacheService.cs');
const mbrainz = read('Providers', 'Metadata', 'MusicBrainz', 'MusicBrainzMetadataProvider.cs');
const playback = read('Controllers', 'PlaybackController.cs');
const apiController = read('Controllers', 'ApiController.cs');
const manifest = read('manifest.json');

// ── правки, которые были у нас ДО разбора: их сторожа не было вовсе ──────────

test('music: модуль включён нашим флипом манифеста', () => {
  assert.match(manifest, /"enable"\s*:\s*true/,
    'у апстрима модуль выключен манифестом, Startup такие пропускает и /music.js отдаёт 404');
});

test('music: наша схема кеширования music.js на месте (не апстримный ETag)', () => {
  assert.ok(apiController.includes('[Staticache(10080, always: true, immutable: true, queryKeys = ["v"])]'),
    'вернулась апстримная схема с ETag/no-cache — это условный запрос на КАЖДЫЙ старт каждого клиента (qdl 2.16: music.js был 77% повторного трафика)');
  assert.ok(!apiController.includes('If-None-Match'),
    'апстримный разбор If-None-Match вернулся в MusicJS');
});

test('music: список Replace покрывает все плейсхолдеры plugin.js', () => {
  // Апстрим дописывает в plugin.js новые {плейсхолдеры} под свой ApiController. Пропущенный
  // уедет к клиенту литералом и уронит весь раздел синтаксической ошибкой — при зелёной сборке.
  const inPlugin = new Set((plugin.match(/'\{[a-z_]+\}'/g) || []).map((s) => s.slice(1, -1)));
  const inController = new Set((apiController.match(/\{[a-z_]+\}/g) || []));
  const missing = [...inPlugin].filter((ph) => !inController.has(ph));
  assert.deepStrictEqual(missing, [], 'в MusicJS нет .Replace для плейсхолдеров: ' + missing.join(', '));
});

// ── F1: битая картинка перезапрашивалась бесконечно (77 req/s) ───────────────

test('F1: все три обработчика картинок схлопнуты', () => {
  for (const marker of ['d1v:img-noloop-card', 'd1v:img-noloop-artist', 'd1v:img-noloop-album'])
    assert.ok(plugin.includes(marker), 'потерян маркер ' + marker + ' (ребейз?)');
});

test('F1: апстримных ретраящих обработчиков не осталось', () => {
  assert.ok(!plugin.includes("posterImg.attr('src', img).css('opacity', 1);"),
    'вернулся ретрай постера: битая обложка снова уйдёт в цикл ~77 запросов/с');
  assert.ok(!/on\('error', function \(\) \{\s*img\.attr\('src', item\.image \|\| IMG_BG\);/.test(plugin),
    'вернулся ретрай карточки');
});

test('F1: раскрытие картинки не потеряно ни в одной из двух форм', () => {
  // 🔴 Формы РАЗНЫЕ: карточка раскрывается классом loaded на обёртке, постер — инлайновым
  // opacity. Перепутать = невидимая картинка вместо битой, то есть регрессия хуже исходной.
  assert.match(plugin, /d1v:img-noloop-card[\s\S]{0,900}?img\.on\('load error', function \(\) \{\s*html\.addClass\('loaded'\);/,
    'у карточки пропал addClass(loaded) — .lm-card__img останется на opacity:0');
  for (const marker of ['d1v:img-noloop-artist', 'd1v:img-noloop-album']) {
    const idx = plugin.indexOf(marker);
    assert.ok(idx > 0, marker);
    assert.match(plugin.slice(idx, idx + 900), /posterImg\.on\('load error', function \(\) \{\s*posterImg\.css\('opacity', 1\);/,
      'у ' + marker + ' пропал css(opacity,1) — у .lm-full__poster-img правила .loaded нет');
  }
});

// ── F4: Enter на артисте MusicBrainz не открывал его нигде ──────────────────

test('F4: openArtist открывает каталог безусловно', () => {
  assert.ok(plugin.includes('d1v:artist-enter'), 'потерян маркер d1v:artist-enter');
  assert.match(plugin, /function openArtist\(artist\) \{\s*if \(!artist\) return;\s*openArtistCatalog\(artist\);\s*\}/,
    'вернулась апстримная форма openArtist с мёртвым гардом по activeQuery');
});

test('F4: мёртвый код не вернулся', () => {
  assert.ok(!plugin.includes('function getActiveMusicQuery('),
    'getActiveMusicQuery вернулась — значит вернулся и гард, который она обслуживала');
  assert.ok(!plugin.includes('function isDirectArtistEntity('),
    'isDirectArtistEntity вернулась — её единственный вызов был в openArtist');
});

test('F4: гарды openArtistCatalog на месте', () => {
  // Псевдо-артисты «часто встречается в фитах» и артисты без id обязаны уходить в поиск,
  // иначе Enter будет открывать пустые экраны.
  assert.match(plugin, /function openArtistCatalog\(artist\) \{\s*if \(!artist \|\| !artist\.id \|\| \/\^related:\/i\.test/,
    'из openArtistCatalog пропал гард на related:*/отсутствие id');
});

// ── F5: у трека в истории не было обложки ───────────────────────────────────

test('F5: обложка истории шлётся отдельным аппендером', () => {
  assert.ok(plugin.includes('d1v:hist-image'), 'потерян маркер d1v:hist-image (клиент)');
  assert.ok(playback.includes('d1v:hist-image'), 'потерян маркер d1v:hist-image (сервер)');
  assert.match(playback, /long\? played_ms = null, string image = null/,
    'у MarkHistory пропал параметр image');
});

test('F5: 🔴 image НЕ дописан в общий билдер параметров', () => {
  // buildTrackRequestParams строит ещё и /music/play, /music/stream (src плеера, m3u) и ключ
  // префетч-кеша. Проксированный URL картинки — сотни символов; там ему не место.
  const body = plugin.slice(plugin.indexOf('function buildTrackRequestParams('));
  assert.ok(!body.slice(0, body.indexOf('\n    }')).includes('image='),
    'image= уехал в buildTrackRequestParams — он попадёт в адрес плеера');
});

test('F5: аппендер применён ровно к трём вызовам markHistory', () => {
  const calls = (plugin.match(/appendTrackImageParam\(/g) || []).length;
  assert.strictEqual(calls, 4, 'ожидалось объявление + 3 вызова на markHistory, найдено вхождений: ' + calls);
  for (const line of plugin.split('\n')) {
    if (line.includes('appendTrackImageParam(') && !line.includes('function appendTrackImageParam'))
      assert.ok(line.includes('markHistory'), 'appendTrackImageParam зовётся не на markHistory: ' + line.trim());
  }
});

test('F5: заглушка artwork() в базу не уедет', () => {
  const idx = plugin.indexOf('function appendTrackImageParam(');
  const body = plugin.slice(idx, idx + 600);
  assert.ok(body.includes('selectSizedImage'), 'берём images напрямую, а не trackImage()');
  assert.ok(!body.includes('trackImage('), 'trackImage() возвращает data:-SVG — он уедет в SQLite');
});

// ── F2: клиент ходил на чужие CDN напрямую ──────────────────────────────────

test('F2a: protocol-relative нормализуется ДО проксирования', () => {
  assert.ok(imgProxy.includes('d1v:img-scheme'), 'потерян маркер d1v:img-scheme');
  const norm = imgProxy.indexOf('image.url = "https:" + image.url');
  const proxy = imgProxy.indexOf('controller.HostImgProxy(init, image.url)');
  assert.ok(norm > 0 && proxy > 0, 'не найдены нормализация или вызов HostImgProxy');
  assert.ok(norm < proxy,
    '🔴 нормализация переехала ПОСЛЕ HostImgProxy — ProxyImg требует href.StartsWith("http") и вернёт 404');
});

test('F2b: Apply(MusicAlbum) обходит треки', () => {
  assert.ok(imgProxy.includes('d1v:img-album-tracks'), 'потерян маркер d1v:img-album-tracks');
  const idx = imgProxy.indexOf('public static MusicAlbum Apply');
  const body = imgProxy.slice(idx, idx + 1600);
  assert.ok(body.includes('album.tracks'),
    'вернулась апстримная версия: обложки треков поедут клиенту сырыми (было 150 из 151)');
});

// ── F3: пустая дискография залипала на 7 суток ──────────────────────────────

test('F3: пустой артист и пустой альбом живут коротко', () => {
  assert.ok(cache.includes('d1v:empty-artist'), 'потерян маркер d1v:empty-artist');
  assert.match(cache, /payload is MusicArtist artist/, 'ветка MusicArtist в IsEmptyPayload пропала');
  assert.match(cache, /payload is MusicAlbum album/, 'ветка MusicAlbum в IsEmptyPayload пропала');
});

test('F3: 🔴 CacheKeyPrefix не бампнут', () => {
  // Он общий для preferred_source SoundCloud, картинок Discogs, DailyMix и каталога SC.
  // Бамп «чтобы сбросить кеш» снёс бы их все; отравленные записи снимаются точечно.
  assert.ok(cache.includes('CacheKeyPrefix = "music:metadata:v2"'),
    'CacheKeyPrefix изменён — это сброс кешей, которые к находке отношения не имеют');
});

test('F3: ретрай MusicBrainz на месте и уважает шлюз', () => {
  assert.ok(mbrainz.includes('d1v:mb-retry'), 'потерян маркер d1v:mb-retry');
  const idx = mbrainz.indexOf('static async Task<JsonObject> GetJsonAsync');
  const body = mbrainz.slice(idx, idx + 2200);
  assert.ok(body.includes('await RespectRateLimitAsync(cancellationToken);'),
    'ретрай обязан ходить через шлюз 1 req/s, а не мимо него');
  assert.ok(body.includes('RetryAfter'), 'пропало уважение Retry-After');
  assert.ok(mbrainz.includes('retryAfterCap'), 'пропал кап ожидания — бюджет ручки 20 с');
});

// ── общее ───────────────────────────────────────────────────────────────────

test('music: plugin.js остаётся синтаксически валидным', () => {
  // Плейсхолдеры {localhost} и соседи сервер подставляет при отдаче; в исходнике они лежат
  // внутри строковых литералов, поэтому парсер их переваривает как есть.
  assert.doesNotThrow(() => new vm.Script(plugin, { filename: 'plugin.js' }));
});
