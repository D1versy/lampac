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

  // 🔴 телефон определяется платформой и ориентацией, а НЕ порогом ширины: вьюпорт приложения
  // на айфоне шире 600 px, и медиазапрос по ширине телефон не ловит (медиасервер claude/06 §DB)
  const idx = plugin.indexOf('function auroraIsPhone');
  assert.ok(idx > 0, 'пропала auroraIsPhone');
  const body = plugin.slice(idx, idx + 600);
  assert.ok(body.includes("window.d1vision_platform === 'ios'"), 'телефон больше не определяется платформой');
  assert.ok(body.includes('window.innerHeight > window.innerWidth'), 'пропала проверка ориентации');
  assert.ok(!/max-width:\s*\d+px[^}]*lm-au__/.test(plugin), 'раскладка Aurora завязана на порог ширины — та самая грабля §DB');

  // 🔴 макет нарисован под 1440x810; жёсткие пиксели оттуда разъедутся на 1280x720 и 1920x1080
  const cssIdx = plugin.indexOf('d1v:music-aurora — макет');
  assert.ok(cssIdx > 0, 'не найден блок стилей Aurora');
  const css = plugin.slice(cssIdx, plugin.indexOf('</style>', cssIdx));
  const hardPx = (css.match(/:\s*\d{2,}px/g) || []).filter((m) => !/1px/.test(m));
  assert.deepStrictEqual(hardPx, [], 'в раскладке появились жёсткие пиксели макета: ' + hardPx.join(', '));

  // 🔴 клиент ходит ТОЛЬКО в наш сервер: шрифт макета с Google Fonts подключать нельзя
  assert.ok(!/fonts\.(googleapis|gstatic)\.com/.test(plugin), 'подключён внешний шрифт — клиент пойдёт на чужой хост');

  // очередь строится фокусируемыми строками, иначе пультом по ней не походить
  assert.ok(plugin.includes('lm-au__row selector'), 'строки очереди не фокусируются');
  assert.match(plugin, /row\.on\('click hover:enter'/, 'строки очереди не слушают hover:enter');

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

  // выход с экрана и вход в лист очереди — тоже пультом
  assert.match(plugin, /lm-ios-full-player__tool selector" data-action="collapse"/,
    'крестик снова не фокусируется — экран опять станет ловушкой');
  assert.match(plugin, /lm-ios-full-player__sheet-row selector/,
    'строки очереди не фокусируются — кнопка «плейлист» ведёт в тупик');

  // 🔴 пульт жмёт hover:enter, а не click: без второй подписки кнопки молчат на OK
  assert.match(plugin, /on\('click hover:enter', '\[data-action\]'/, 'кнопки плеера не слушают hover:enter');
  assert.match(plugin, /row\.on\('click hover:enter'/, 'строки листа не слушают hover:enter');

  // контроллер обязан строить коллекцию, а не быть заглушкой
  const ctrlIdx = plugin.indexOf("Lampa.Controller.add('lampac_music_full_player'");
  assert.ok(ctrlIdx > 0, 'контроллер полноэкранного плеера не найден');
  const ctrl = plugin.slice(ctrlIdx, ctrlIdx + 1200);
  assert.ok(!/toggle: function \(\) \{\}/.test(ctrl), 'вернулся пустой toggle — фокус-коллекция не строится');
  assert.ok(ctrl.includes('Lampa.Controller.collectionSet'), 'коллекция не собирается');
  assert.ok(ctrl.includes('sheetOpen'), 'фокус не уезжает в открытый лист очереди');
});

// ── общее ───────────────────────────────────────────────────────────────────

test('music: plugin.js остаётся синтаксически валидным', () => {
  // Плейсхолдеры {localhost} и соседи сервер подставляет при отдаче; в исходнике они лежат
  // внутри строковых литералов, поэтому парсер их переваривает как есть.
  assert.doesNotThrow(() => new vm.Script(plugin, { filename: 'plugin.js' }));
});
