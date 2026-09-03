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
const ytMusic = read('Providers', 'Discovery', 'YouTubeMusic', 'YouTubeMusicSearchSupport.cs');
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

test('index: 🔴 у входа нет серверного Staticache — в теле адреса всех плагинов', () => {
  // Кеш дисковый и переживает рестарт; вход с вшитым LamInitVersion() до пяти минут после деплоя
  // раздавал клиентам старый bootstrap, и правка плагина «не доезжала» (медиасервер claude/06 §DE).
  // ⚠️ Это ApiController модуля LampaWeb (вход и lampainit.js), а не Music — тот выше как apiController.
  const lampaWebApi = fs.readFileSync(path.join(H.REPO, 'Modules', 'LampaWeb', 'Controllers', 'ApiController.cs'), 'utf8').replace(/\r\n/g, '\n');
  assert.ok(lampaWebApi.includes('d1v:index-no-staticache'), 'потерян маркер d1v:index-no-staticache');
  const idx = lampaWebApi.indexOf('[Route("/")]');
  assert.ok(idx > 0, 'не найден маршрут входа');
  const before = lampaWebApi.slice(Math.max(0, idx - 400), idx);
  assert.ok(!/\[Staticache\([^\]]*\)\]\s*$/.test(before.trimEnd()), 'на вход вернулся Staticache — bootstrap снова будет залипать после деплоя');
  const body = lampaWebApi.slice(idx, idx + 400);
  assert.ok(body.includes('SetHeadersNoCache();'), 'вход без no-cache заголовков — WebView закеширует его сам');
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

// ── §DE: трек доигрывал и музыка замолкала; продолжения не было вовсе ────────

test('DE1: артист ютубовской заливки берётся из заголовка, а не из канала', () => {
  assert.ok(ytMusic.includes('d1v:yt-artist-from-title'), 'потерян маркер d1v:yt-artist-from-title');
  assert.ok(ytMusic.includes('static void ResolveArtistAndTitle'), 'пропал общий разбор артиста и заголовка');
  // Апстримная форма: titleArtist подставлялся ТОЛЬКО когда канал пуст, а пустым он не бывает.
  // Радио строится ИЗ АРТИСТОВ сидов, поэтому на таких треках оно возвращало ноль (замер:
  // сид как есть — available=false за 1.4 с, он же с настоящим артистом — 13 треков за 2.4 с).
  assert.ok(!/if \(string\.IsNullOrWhiteSpace\(artist\)\)\s*\n\s*artist = titleArtist;/.test(ytMusic),
    'вернулась апстримная форма: артистом снова станет название канала, а радио — пустым');
  assert.strictEqual((ytMusic.match(/ResolveArtistAndTitle\(rawTitle,/g) || []).length, 3,
    'не все три маппера (поиск / плейлист / загрузки канала) ходят через общий разбор');
});

test('DE2: очередь ведётся по настоящему ended', () => {
  assert.ok(plugin.includes('d1v:embedded-ended-next'), 'потерян маркер d1v:embedded-ended-next');
  assert.ok(plugin.includes('function advanceEmbeddedQueueOnce'), 'пропал общий вход перехода');

  const idx = plugin.indexOf("if (event && (event.type === 'ended' || event.type === 'error'))");
  assert.ok(idx > 0, 'не найдена ветка ended/error панельного обработчика');

  const body = plugin.slice(idx, idx + 2200);
  assert.ok(body.includes("advanceEmbeddedQueueOnce('ended')"),
    'вернулся ранний return: трек доиграет до конца и музыка замолчит — ровно симптом владельца');
  assert.ok(!/'error'[\s\S]{0,40}advanceEmbeddedQueueOnce/.test(body),
    'переход повешен и на error — там своя ветка со счётчиком подряд идущих мертвецов');
});

test('DE2: 🔴 у перехода один хозяин — автопереход ядра для музыки заглушён', () => {
  // Иначе их двое. Ядро на 'ended' зовёт PlayerPlaylist.next(), тот взводит
  // wait_for_loading_url и уходит в резолв url-функции на 2-16 с; наш switchToken делает резолв
  // устаревшим, resolvePlaybackUrl выходит без call(), ядро НИКОГДА не снимает флаг — и дальше
  // игнорируется любой next(), включая кнопку ► на панели, до перезапуска плеера.
  assert.ok(plugin.includes('d1v:music-owns-next'), 'потерян маркер d1v:music-owns-next');

  // флаг обязан быть на ОБОИХ объектах: buildPlayback (соседи очереди) и buildResolvedPlayback
  // (первый трек — ровно тот, на котором владелец и вставал)
  assert.strictEqual((plugin.match(/qdl_no_autonext: true/g) || []).length, 2,
    'qdl_no_autonext стоит не на обоих объектах воспроизведения');
  for (const fn of ['function buildPlayback', 'function buildResolvedPlayback']) {
    const idx = plugin.indexOf(fn);
    assert.ok(idx > 0, 'не найдена ' + fn);
    assert.ok(plugin.slice(idx, idx + 2600).includes('qdl_no_autonext: true'),
      'в ' + fn + ' пропал флаг — вернётся гонка с автопереходом ядра');
  }
});

test('DE2: двойной шаг закрыт отметкой времени, а не сравнением ключа', () => {
  // effective-end перематывает медиа в конец и ставит паузу — браузер вслед может отдать ещё и
  // ended. Сравнивать musicPlayerPanelSyntheticEndKey тут нельзя: после сдвига очереди в ключ
  // входят уже другой trackId и другой индекс, то есть защита не сработала бы никогда.
  assert.ok(plugin.includes('musicPlayerPanelAdvancedAt'), 'пропала отметка «очередь уже сдвинули»');

  const idx = plugin.indexOf('function advanceEmbeddedQueueOnce');
  assert.match(plugin.slice(idx, idx + 1200),
    /musicPlayerPanelAdvancedAt && now - musicPlayerPanelAdvancedAt < \d+/,
    'пропало окно защиты от двойного шага — трек будет пролистываться через один');
});

test('DE3: автопродолжение включено по умолчанию', () => {
  assert.ok(plugin.includes('d1v:radio-autoplay-default'), 'потерян маркер d1v:radio-autoplay-default');
  assert.match(plugin, /radio_autoplay_enabled, true\) === true/,
    'вернулся апстримный дефолт false: за 7 часов боевой работы не случилось ни одного /music/radio');
});

test('DE3: конец очереди не заканчивается тишиной', () => {
  assert.ok(plugin.includes('d1v:endless-fallback'), 'потерян маркер d1v:endless-fallback');
  assert.ok(plugin.includes('function continueEndlessPlayback'), 'пропало последнее средство');
  assert.ok(plugin.includes('function playQueueIndexAnyEngine'), 'пропал общий вход «сыграть по индексу»');

  // 🔴 Оба движка обязаны звать его на конце очереди: собственный <audio> — дефолт в оболочках,
  // встроенный видеоплеер остаётся у тех, кто выбрал его руками.
  assert.ok(plugin.includes("continueEndlessPlayback('standalone-ended')"),
    'собственный <audio> снова останавливается на конце очереди');
  assert.match(plugin, /playEmbeddedQueueOffset\(1\) && !continueEndlessPlayback\(origin\)/,
    'встроенный плеер снова останавливается на конце очереди');

  // Долив приезжает асинхронно и сам ничего не запускает: без этого музыка молчала бы ровно
  // в тот момент, когда продолжение уже пришло.
  assert.ok(plugin.includes('awaitingResume'), 'пропало ожидание долива радио');
  assert.ok(plugin.includes("playQueueIndexAnyEngine(firstAdded, 'radio-resume')"),
    'долитые радио треки не запускаются после того, как очередь кончилась');
});

test('DE4: дефолт музыки остаётся встроенным плеером', () => {
  assert.ok(plugin.includes('d1v:music-default-inner'), 'потерян маркер d1v:music-default-inner');

  const idx = plugin.indexOf('function defaultMusicPlayerId');
  const body = plugin.slice(idx, idx + 400);
  assert.ok(!body.includes("return 'ios'"),
    'дефолт музыки сменён — это решение принимает владелец, а не ребейз');
});

test('DE7: 🔴 раскладка «Aurora»: две формы, телефон по платформе, без чужих шрифтов', () => {
  assert.ok(plugin.includes('d1v:music-aurora'), 'потерян маркер d1v:music-aurora');

  // две раскладки одной разметкой
  for (const cls of ['lm-ios-full-player--aurora', 'lm-ios-full-player--wide', 'lm-ios-full-player--phone'])
    assert.ok(plugin.includes(cls), 'пропал класс раскладки ' + cls);
  for (const el of ['lm-au__body', 'lm-au__main', 'lm-au__queue-list', 'lm-au__row', 'lm-au__sub', 'lm-au__eq'])
    assert.ok(plugin.includes(el), 'пропал узел раскладки ' + el);

  // 🔴 телефон определяется платформой и короткой стороной, а НЕ порогом ширины: вьюпорт
  // приложения на айфоне шире 600 px, и медиазапрос по ширине телефон не ловит (медиасервер
  // claude/06 §DB); ориентация тоже не годится — телефон в ландшафте получал ТВ-раскладку (DF6)
  const idx = plugin.indexOf('function auroraIsPhone');
  assert.ok(idx > 0, 'пропала auroraIsPhone');
  const body = plugin.slice(idx, idx + 600);
  assert.ok(body.includes("window.d1vision_platform === 'ios'"), 'телефон больше не определяется платформой');
  assert.ok(body.includes('Math.min(window.innerWidth, window.innerHeight) <= 520'), 'пропала проверка по короткой стороне');
  assert.ok(!/max-width:\s*\d+px[^}]*lm-au__/.test(plugin), 'раскладка Aurora завязана на порог ширины — та самая грабля §DB');

  // 🔴 макет нарисован под 1440x810; жёсткие пиксели оттуда разъедутся на 1280x720 и 1920x1080.
  // Блок полов HIG (d1v:music-phone-targets, qdl 2.104) — осознанное исключение: там пиксели
  // стоят НИЖНЕЙ границей внутри max(em, px), и это единственная защита от масштаба Lampa на
  // телефоне; их держит DF3. Срез обрезаем по его маркеру.
  const cssIdx = plugin.indexOf('d1v:music-aurora — макет');
  assert.ok(cssIdx > 0, 'не найден блок стилей Aurora');
  const targetsIdx = plugin.indexOf('d1v:music-phone-targets', cssIdx);
  assert.ok(targetsIdx > cssIdx, 'блок полов HIG должен идти ПОСЛЕ раскладки Aurora (иначе каскад)');
  // 🔴 Комментарии вырезаем ДО поиска: иначе кейс ловит собственную прозу вида «замер: 228px».
  // Ровно на этом уже спотыкался DE4 — сторож обязан смотреть на правила, а не на пояснения.
  const css = plugin.slice(cssIdx, targetsIdx).replace(/\/\*[\s\S]*?\*\//g, '');
  const hardPx = (css.match(/:\s*\d{2,}px/g) || []).filter((m) => !/1px/.test(m));
  assert.deepStrictEqual(hardPx, [], 'в раскладке появились жёсткие пиксели макета: ' + hardPx.join(', '));

  // 🔴 клиент ходит ТОЛЬКО в наш сервер: шрифт макета с Google Fonts подключать нельзя
  assert.ok(!/fonts\.(googleapis|gstatic)\.com/.test(plugin), 'подключён внешний шрифт — клиент пойдёт на чужой хост');

  // очередь строится фокусируемыми строками, иначе пультом по ней не походить
  assert.ok(plugin.includes('lm-au__row selector'), 'строки очереди не фокусируются');
  assert.match(plugin, /row\.on\('hover:enter'/, 'строки очереди не слушают hover:enter');

  // список не должен пересобираться на каждом тике — это рвало бы фокус посреди навигации
  assert.ok(plugin.includes('data-au-key'), 'пропал ключ пересборки очереди');
});

test('DE6: 🔴 плеер музыки назначает сервер, а не устройство', () => {
  // Выбор жил в localStorage каждого клиента, поэтому починка плеера означала «зайди в настройки
  // на каждом устройстве». Владелец: «сделай так, чтобы это всё настраивалось на сервере».
  assert.ok(plugin.includes('d1v:music-player-server'), 'потерян маркер d1v:music-player-server');

  const idx = plugin.indexOf('function getMusicPlayerId');
  assert.ok(idx > 0, 'не найдена getMusicPlayerId');
  const body = plugin.slice(idx, idx + 1200);

  assert.ok(body.includes('window.d1vision_music_player'), 'серверное значение больше не читается');

  // 🔴 Порядок решает всё: серверное значение обязано проверяться ДО локального Storage,
  // иначе «настраивается на сервере» превращается в «настраивается на клиенте».
  const forcedAt = body.indexOf('window.d1vision_music_player');
  const storageAt = body.indexOf('Lampa.Storage.get(MUSIC.storage.player');
  assert.ok(storageAt > forcedAt,
    'локальный выбор снова читается раньше серверного — приоритет перевёрнут');

  // незнакомое значение не должно уводить раздел в несуществующий режим
  assert.match(body, /forced && Object\.prototype\.hasOwnProperty\.call\(values, forced\)/,
    'пропала проверка, что назначенный сервером плеер вообще существует на этой платформе');
});

test('DE5: 🔴 полноэкранный плеер музыки достижим пультом', () => {
  // Владелец: «оверлей поверх плейлиста, который к тому же не закрывается». Причина была не в
  // логике закрытия, а в том, что в фокус-коллекцию не попадал НИ ОДИН элемент: у кнопок не было
  // класса selector, а контроллер регистрировался с пустым `toggle: function () {}`. Экран
  // открывался и становился ловушкой.
  assert.ok(plugin.includes('d1v:music-four-buttons'), 'потерян маркер d1v:music-four-buttons');

  // ровно четыре кнопки в основном ряду, и каждая фокусируемая
  const actionsIdx = plugin.indexOf("'<div class=\"lm-ios-full-player__actions\">'");
  assert.ok(actionsIdx > 0, 'не найден ряд кнопок плеера');
  const actions = plugin.slice(actionsIdx, actionsIdx + 1400);
  // Состав и ПОРЯДОК по макету «Aurora Player»: на телефоне это один ряд слева направо, а на
  // широком экране перемешивание и повтор уводит во второй ряд распорка lm-au__break — то есть
  // порядок в DOM обязан оставаться мобильным.
  const buttons = actions.match(/data-action="(prev|playpause|next|queue|shuffle|repeat)"/g) || [];
  assert.deepStrictEqual(buttons, [
    'data-action="shuffle"', 'data-action="prev"', 'data-action="playpause"',
    'data-action="next"', 'data-action="repeat"',
  ], 'состав или порядок кнопок разошёлся с макетом');
  assert.strictEqual((actions.match(/lm-ios-full-player__btn[^"]*selector/g) || []).length, 5,
    'кнопка без selector = кнопка, до которой не дойти пультом');
  assert.ok(actions.includes('lm-au__break'), 'пропала распорка — на ТВ шаффл и повтор не уйдут во второй ряд');

  // выход с экрана и вход в лист очереди — тоже пультом (с qdl 2.101 крестик = «остановить»)
  assert.match(plugin, /lm-ios-full-player__tool selector" data-action="stop"/,
    'крестик снова не фокусируется — экран опять станет ловушкой');
  assert.match(plugin, /lm-ios-full-player__sheet-row selector/,
    'строки очереди не фокусируются — кнопка «плейлист» ведёт в тупик');

  // 🔴 пульт жмёт hover:enter, а не click: без второй подписки кнопки молчат на OK.
  // 🔴 И подписка обязана стоять ПРЯМО на кнопках: Lampa шлёт hover:enter через Utils.trigger с
  // bubbles=false, делегированный обработчик на корне его не видит (владелец: «предыдущий,
  // пауза, следующий, перемешать, повтор не работают»).
  assert.ok(plugin.includes('d1v:music-enter-direct'), 'потерян маркер d1v:music-enter-direct');
  // (с 2.104 подписка ТОЛЬКО на hover:enter — см. DF1; здесь важно, что она прямая)
  assert.match(plugin, /player\.find\('\[data-action\]'\)\.on\('hover:enter'/, 'кнопки плеера не слушают hover:enter на себе');
  // (корень полноэкранного плеера — `player`; у мёртвой мини-панели `bar` делегированная подписка
  // есть и не мешает: она display:none !important с d1v:music-no-minibar)
  assert.ok(!/player\.on\('(click )?hover:enter( click)?', '\[data-action\]'/.test(plugin), 'вернулась делегированная подписка — OK по кнопкам снова молчит');
  assert.match(plugin, /row\.on\('hover:enter'/, 'строки листа не слушают hover:enter');

  // контроллер обязан строить коллекцию, а не быть заглушкой
  const ctrlIdx = plugin.indexOf("Lampa.Controller.add('lampac_music_full_player'");
  assert.ok(ctrlIdx > 0, 'контроллер полноэкранного плеера не найден');
  const ctrl = plugin.slice(ctrlIdx, ctrlIdx + 2600);
  assert.ok(!/toggle: function \(\) \{\}/.test(ctrl), 'вернулся пустой toggle — фокус-коллекция не строится');
  assert.ok(ctrl.includes('Lampa.Controller.collectionSet'), 'коллекция не собирается');
  assert.ok(ctrl.includes('sheetOpen'), 'фокус не уезжает в открытый лист очереди');
});

test('DE10: 🔴 стрелки пульта ходят через глобальный Navigator, а не Lampa.Navigator', () => {
  // Владелец: «на пульте не работает совсем навигация, ничего не происходит в плеере». Причина:
  // контроллер звал Lampa.Navigator.move — а такого объекта в бандле Lampa НЕТ. Навигатор это
  // глобальный `Navigator` из vender/navigator/navigator.js (так его зовут и апстримный раздел,
  // и qdl.js). Каждая стрелка падала TypeError при живых кнопках и рабочем OK.
  assert.ok(plugin.includes('d1v:music-dpad-zones'), 'потерян маркер d1v:music-dpad-zones');
  // 🔴 Ищем ВЫЗОВ (с точкой после имени), а не слово: иначе сторож ловит собственную прозу —
  // на этом уже спотыкались DE4 и DE7.
  assert.ok(!/Lampa\.Navigator\./.test(plugin),
    'вернулся вызов Lampa.Navigator — в бандле его нет, стрелки пульта снова умрут');

  const ctrlIdx = plugin.indexOf("Lampa.Controller.add('lampac_music_full_player'");
  const ctrl = plugin.slice(ctrlIdx, ctrlIdx + 3400);
  for (const dir of ['left', 'right', 'up', 'down'])
    assert.match(ctrl, new RegExp(dir + ": function \\(\\) \\{ auroraDpad\\(player, '" + dir + "'\\); \\}"),
      'стрелка ' + dir + ' больше не идёт через auroraDpad');

  // сперва обычный шаг навигатора, и только без кандидата — явный переход между зонами:
  // straightOnly не найдёт очередь справа сверху с транспорта внизу слева при короткой очереди
  const dpadIdx = plugin.indexOf('function auroraDpad(');
  assert.ok(dpadIdx > 0, 'пропала auroraDpad');
  const dpad = plugin.slice(dpadIdx, dpadIdx + 900);
  assert.match(dpad, /Navigator\.canmove\(direction\)/, 'auroraDpad не спрашивает навигатор');
  assert.match(dpad, /Navigator\.move\(direction\)/, 'auroraDpad не двигает фокус навигатором');
  assert.ok(dpad.includes('auroraDpadJump'), 'пропал переход между зонами');

  const jumpIdx = plugin.indexOf('function auroraDpadJump(');
  const jump = plugin.slice(jumpIdx, jumpIdx + 1600);
  for (const zone of ["'transport'", "'queue'", "'head'"])
    assert.ok(jump.includes('zone === ' + zone), 'в переходах пропала зона ' + zone);
  assert.ok(jump.includes("lm-ios-full-player--phone"), 'переходы не различают телефонную раскладку');

  // длинная очередь: фокус подтягивает строку в видимую часть, текущий трек — в центр
  // (с 2.104 — через scrollWithinPlayer, не scrollIntoView: тот прокручивал корень оверлея, DF2)
  assert.match(plugin, /row\.on\('hover:focus'[\s\S]{0,160}scrollWithinPlayer\(this, 'nearest'\)/, 'строки очереди не прокручиваются под фокус');
  assert.ok(plugin.includes('data-au-current'), 'текущий трек не подтягивается в центр очереди');
});

test('DE11: 🔴 пока плеер открыт, контроллер фокуса принадлежит ему', () => {
  // Трасса с телевизора: через 19 мс после открытия раздел под плеером дёргает Line.toggle
  // (items_line), через 12 с renderHome → activity.toggle() → content → items_line. Стрелки
  // уходят к невидимым карточкам, Back — в раздел. Владелец: «навигация уебищная и через пульт
  // кнопка назад не работает».
  assert.ok(plugin.includes('d1v:music-player-owns-focus'), 'потерян маркер d1v:music-player-owns-focus');

  const idx = plugin.indexOf('function bindStandaloneIosFullPlayerFocusGuard(');
  assert.ok(idx > 0, 'пропал сторож контроллера плеера');
  const body = plugin.slice(idx, idx + 1500);
  assert.match(body, /Lampa\.Controller\.listener\.follow\('toggle'/, 'сторож не слушает переключения контроллера');
  assert.ok(body.includes("Lampa.Controller.toggle('lampac_music_full_player')"), 'сторож не возвращает контроллер плеера');
  assert.ok(body.includes('MUSIC_IOS_FULL_PLAYER_TRANSIENT[name]'), 'временные слои над плеером отбираются — select/loading сломаются');
  assert.ok(body.includes('if (!MUSIC_IOS_FULL_PLAYER_OPEN'), 'сторож работает и при закрытом плеере — раздел не получит фокус назад');

  // сторож обязан включаться при открытии плеера
  const openIdx = plugin.indexOf('function openStandaloneIosFullPlayer(');
  assert.ok(plugin.slice(openIdx, openIdx + 4000).includes('bindStandaloneIosFullPlayerFocusGuard()'), 'сторож не включается при открытии');

  // список временных слоёв: без своего имени сторож зациклится, без select/modal/loading — сломает их
  const tIdx = plugin.indexOf('var MUSIC_IOS_FULL_PLAYER_TRANSIENT');
  assert.ok(tIdx > 0, 'пропал список временных слоёв');
  const transient = plugin.slice(tIdx, tIdx + 400);
  for (const n of ['select', 'modal', 'loading', 'lampac_music_full_player'])
    assert.ok(new RegExp('\\b' + n + ': 1').test(transient), 'в списке временных слоёв нет ' + n);

  // при возврате фокус встаёт на последний элемент плеера, а не всегда на паузу
  assert.ok(plugin.includes("player.on('hover:focus', '.selector'"), 'последний фокус в плеере не запоминается');
});

test('DE12: страница плейлиста и альбома открывается с фокусом на «Слушать»', () => {
  // Владелец: «начальный фокус на "Слушать" и на просто канвасе, нужно переводить — сделай, чтобы
  // просто нажал "Слушать" и всё запустилось». Фокус вставал на шапку, у которой нет действия на OK.
  assert.ok(plugin.includes('d1v:music-play-first-focus'), 'потерян маркер d1v:music-play-first-focus');
  assert.ok(plugin.includes('last = rawTracks.length ? playBtn[0] : header[0];'), 'плейлист снова открывается с фокусом на шапке');
  assert.ok(plugin.includes('last = tracks.length ? playAlbumBtn[0] : header[0];'), 'альбом снова открывается с фокусом на шапке');
  // OK по шапке тоже запускает — мёртвой остановки на пути к первому треку нет
  assert.strictEqual((plugin.match(/header\.on\('hover:enter', function \(\) \{\s*if \((rawTracks|tracks)\.length\)\s*playTrack\(/g) || []).length, 2,
    'OK по шапке плейлиста/альбома снова ничего не делает');
});

test('DE8: выход из плеера — только остановка; кнопки «свернуть» нет', () => {
  // Владелец: «сверху есть кнопка свернуть плеер — убери её. Или мы слушаем что-то, или
  // закрываем плеер, сворачивать нельзя». Мини-панели нет, свернуть = музыка без экрана.
  assert.ok(plugin.includes('d1v:music-exit-stops'), 'потерян маркер d1v:music-exit-stops');
  assert.ok(!plugin.includes('data-action="collapse"'), 'вернулась кнопка «свернуть»');
  assert.ok(!plugin.includes('IOS_PLAYER_DOWN_ICON'), 'вернулась стрелка вниз');

  const exitIdx = plugin.indexOf('function exitStandaloneIosFullPlayer(');
  assert.ok(exitIdx > 0, 'пропала exitStandaloneIosFullPlayer');
  const exit = plugin.slice(exitIdx, exitIdx + 200);
  assert.ok(exit.includes('closeStandaloneIosFullPlayer()') && exit.includes('stopStandaloneIosAudioPlayback()'),
    'выход больше не закрывает И не останавливает');

  // все три выхода ведут туда же: Back контроллера, перехват клавиш Back, свайп вниз, крестик
  const ctrlIdx = plugin.indexOf("Lampa.Controller.add('lampac_music_full_player'");
  const ctrl = plugin.slice(ctrlIdx, ctrlIdx + 3400);
  assert.match(ctrl, /back: function \(\) \{[\s\S]{0,300}exitStandaloneIosFullPlayer\(\);/, 'Back контроллера снова сворачивает');
  const keyIdx = plugin.indexOf('function handleStandaloneIosFullPlayerBack(');
  assert.ok(plugin.slice(keyIdx, keyIdx + 700).includes('exitStandaloneIosFullPlayer()'), 'клавиша Back снова сворачивает');
  const gestureIdx = plugin.indexOf('function closeStandaloneIosFullPlayerWithGesture(');
  assert.ok(plugin.slice(gestureIdx, gestureIdx + 600).includes('exitStandaloneIosFullPlayer()'), 'свайп вниз снова сворачивает');
  assert.match(plugin, /if \(action === 'stop'\) \{\s*exitStandaloneIosFullPlayer\(\);/, 'крестик не останавливает');

  // переиспользованная очередь тоже открывает плеер — иначе музыка играет «в никуда»
  const selIdx = plugin.indexOf('function selectFromStandaloneIosQueue(');
  assert.ok(plugin.slice(selIdx, selIdx + 500).includes('openStandaloneIosFullPlayer()'),
    'старт из переиспользованной очереди не открывает плеер');
});

test('DE9: строки очереди несут обложку трека', () => {
  // Владелец: «в плейлисте список треков без картинок, только сокращения СВ, СА».
  assert.ok(plugin.includes('d1v:music-queue-art'), 'потерян маркер d1v:music-queue-art');
  const idx = plugin.indexOf('function renderAuroraQueue(');
  assert.ok(idx > 0, 'пропала renderAuroraQueue');
  const body = plugin.slice(idx, idx + 3000);
  assert.match(body, /art\.attr\('src', trackImage\(track\) \|\| IMG_BG\)/, 'обложка строки не берётся из trackImage');
  assert.ok(body.includes("lm-au__row-tile--fallback"), 'нет подложки на случай битой картинки');
  // стили: картинка на всю плитку, при ошибке прячется
  assert.match(plugin, /\.lm-au__row-tile img \{[^}]*object-fit: cover/, 'обложка не растянута на плитку');
  assert.match(plugin, /\.lm-au__row-tile--fallback img \{ display: none; \}/, 'битая обложка не прячется');
});

// ── qdl 2.104: Playwright-аудит телефона и ТВ (медиасервер claude/06 §DF) ────────────

test('DF1: 🔴 одно нажатие — одно действие: прямые подписки без click', () => {
  // Владелец: «кнопка паузы не работает на айфоне». Lampa вешает click на КАЖДЫЙ .selector
  // (Controller.observe, app.min.js:44192) и через 20 мс шлёт hover:enter — подписка на оба
  // давала два действия: лог тапа «click → pause() → hover:enter → play()». Ломалось и мышью.
  assert.ok(plugin.includes('d1v:music-enter-once'), 'потерян маркер d1v:music-enter-once');
  // ни одной ПРЯМОЙ подписки на пару событий (делегированные — с селектором вторым аргументом —
  // не в счёт: hover:enter не всплывает, там срабатывает только click, один раз)
  const direct = plugin.match(/\.on\('(click hover:enter|hover:enter click)'(?!, ')/g) || [];
  assert.deepStrictEqual(direct, [], 'вернулась прямая подписка на click+hover:enter: ' + direct.join(' | '));
  // пять мест обязаны слушать hover:enter напрямую
  assert.match(plugin, /player\.find\('\[data-action\]'\)\.on\('hover:enter'/, 'кнопки транспорта');
  assert.strictEqual((plugin.match(/row\.on\('hover:enter', function \(event\)/g) || []).length, 2, 'строки листа и очереди');
  assert.match(plugin, /button\.on\('hover:enter', function \(event\)/, 'кнопка «Текст» в панели видеоплеера');
  assert.match(plugin, /earlier\.add\(later\)\.on\('hover:enter', function \(event\)/, 'кнопки смещения текста');
});

test('DF2: 🔴 корень оверлея не прокручивается: нет scrollIntoView внутри плеера', () => {
  // На телефоне шапка с крестиком уезжала на y=−36: scrollIntoView строки очереди прокручивал
  // не список, а корень .lm-ios-full-player (56 px переполнения от __backdrop{inset:-2em}).
  assert.ok(plugin.includes('d1v:music-no-root-scroll'), 'потерян маркер d1v:music-no-root-scroll');
  assert.ok(plugin.includes('function scrollWithinPlayer('), 'пропал scrollWithinPlayer');
  // от начала полноэкранного плеера до кода визуала ВИДЕОплеера (это другой оверлей, у него свой
  // scrollIntoView) не должно остаться ни одного вызова scrollIntoView
  const from = plugin.indexOf('// ===== IOS FULL PLAYER =====');
  const to = plugin.indexOf('function updateMusicPlayerVisualLyricsHighlight(');
  const helperAt = plugin.indexOf('function scrollWithinPlayer(');
  assert.ok(from > 0 && to > from && helperAt > from && helperAt < to, 'не найдены границы кода плеера');
  const calls = plugin.slice(from, to).match(/\.scrollIntoView\(/g) || [];
  assert.deepStrictEqual(calls, [], 'внутри плеера снова зовут scrollIntoView — шапка уедет за экран');
  // helper двигает только один контейнер и не трогает корень
  const helper = plugin.slice(helperAt, helperAt + 2200);
  assert.ok(helper.includes("closest('.lm-ios-full-player')") && helper.includes('scroller === root'), 'helper не останавливается на корне оверлея');
  assert.ok(helper.includes('scroller.scrollTop = target'), 'helper не двигает scrollTop контейнера');
  // CSS-страховка: clip запрещает и программную прокрутку
  assert.match(plugin, /\.lm-ios-full-player \{\\\n[\s\S]{0,700}overflow: hidden;\\\n\s*overflow: clip;\\\n/, 'у корня плеера нет overflow: clip после hidden');
});

test('DF3: цели нажатия и типографика телефона с полами в пикселях', () => {
  // Замер iPhone 14 Pro: пауза 37.6, пред/след 30.7, крестик 26.3, строка очереди 38.4 —
  // при минимуме 44 по HIG. Полы под классом --phone, а не в @media: класс ставится по платформе.
  assert.ok(plugin.includes('d1v:music-phone-targets'), 'потерян маркер d1v:music-phone-targets');
  // ищем ПЕРВОЕ правило после начала БЛОКА полов (маркер с этим же именем стоит и выше, у кнопок
  // поиска): те же селекторы повторяются ниже в ландшафтном блоке и выше в базовой раскладке
  const base = plugin.indexOf('d1v:music-phone-targets — полы');
  assert.ok(base > 0, 'не найден блок полов HIG');
  for (const rule of [
    ['.lm-ios-full-player--phone .lm-ios-full-player__btn {', 'max(2.9em, 44px)'],
    ['.lm-ios-full-player--phone .lm-ios-full-player__btn--primary {', 'max(3.55em, 56px)'],
    ['.lm-ios-full-player--phone .lm-ios-full-player__tool {', 'max(2.48em, 44px)'],
    ['.lm-ios-full-player--phone .lm-au__row {', 'min-height: 44px'],
    ['.lm-ios-full-player--phone .lm-au__row {', 'grid-template-columns: max(2.6em, 40px)'],   // колонка растёт с плиткой
    ['.lm-ios-full-player--phone .lm-au__main {', 'flex: 0 0 auto'],                          // стопка не сжимается
    ['.lm-ios-full-player--phone .lm-au__queue {', 'flex: 0 1 auto'],                         // ужимается очередь
    ['.lm-ios-full-player--phone .lm-ios-full-player__title {', 'max(1.38em, 18px)'],
    ['.lm-ios-full-player--phone .lm-au__row-title {', 'max(0.88em, 14px)'],
  ]) {
    const idx = plugin.indexOf(rule[0], base);
    assert.ok(idx > 0, 'нет правила ' + rule[0]);
    assert.ok(plugin.slice(idx, idx + 220).includes(rule[1]), rule[0] + ' без пола ' + rule[1]);
  }
  // кнопки главной раздела (поиск/фильтр/закладки) — 37.8 px на телефоне
  assert.match(plugin, /\.lm-search-btn--icon \{\\\n[\s\S]{0,200}width: max\(3\.1em, 44px\)/, 'кнопки поиска без пола 44 px');
});

test('DF4: широкая раскладка без капа героя — название не клампится при пустом месте', () => {
  // ТВ 1920: герой 776 px из 1241, по бокам 240+226 px пусто, название на три строки резалось «…».
  assert.ok(plugin.includes('d1v:music-wide-hero'), 'потерян маркер d1v:music-wide-hero');
  assert.match(plugin, /\.lm-ios-full-player--wide \.lm-ios-full-player__hero \{ width: 100%; max-width: none; margin: 0; align-self: stretch; \}/,
    'у --wide __hero нет сброса max-width/margin — базовые 34em и auto снова центрируют героя');
});

test('DF5: подпись «Повтор» переживает обновление кнопки', () => {
  assert.ok(plugin.includes('d1v:music-repeat-label'), 'потерян маркер d1v:music-repeat-label');
  assert.match(plugin, /\.html\(\(repeatMode === 'one' \? IOS_PLAYER_REPEAT_ONE_ICON : IOS_PLAYER_REPEAT_ICON\) \+ '<span>Повтор<\/span>'\)/,
    '.html() у повтора снова стирает подпись из шаблона');
});

test('DF6: телефон узнаётся по короткой стороне, а не по ориентации', () => {
  // Телефон и ТВ на Android шлют один токен d1vision_android; «портрет = телефон» давал телефону
  // в ландшафте ТВ-раскладку (очередь столбиком 286 px на экране 915×412).
  assert.ok(plugin.includes('d1v:music-phone-by-short-side'), 'потерян маркер d1v:music-phone-by-short-side');
  const idx = plugin.indexOf('function auroraIsPhone(');
  const body = plugin.slice(idx, idx + 400);
  assert.ok(body.includes("window.d1vision_platform === 'ios'"), 'айфон больше не телефон по платформе');
  assert.ok(body.includes('Math.min(window.innerWidth, window.innerHeight) <= 520'), 'нет порога по короткой стороне');
  assert.ok(!body.includes('innerHeight > window.innerWidth'), 'вернулась проверка по ориентации');
  // телефон в ландшафте: вертикальная стопка в 412 px не помещается — обязана быть ландшафтная
  // раскладка под --phone (по ориентации, не по ширине в px)
  const land = plugin.indexOf('@media screen and (orientation: landscape)');
  assert.ok(land > 0, 'нет ландшафтной раскладки телефона');
  const landCss = plugin.slice(land, land + 2200);
  assert.ok(landCss.includes('.lm-ios-full-player--phone .lm-au__body { flex-direction: row;'), 'в ландшафте тело не в ряд');
  assert.ok(landCss.includes('.lm-ios-full-player--phone .lm-au__queue {') && landCss.includes('max-height: none'), 'в ландшафте очередь не колонкой');
});

// ── общее ───────────────────────────────────────────────────────────────────

test('music: plugin.js остаётся синтаксически валидным', () => {
  // Плейсхолдеры {localhost} и соседи сервер подставляет при отдаче; в исходнике они лежат
  // внутри строковых литералов, поэтому парсер их переваривает как есть.
  assert.doesNotThrow(() => new vm.Script(plugin, { filename: 'plugin.js' }));
});
