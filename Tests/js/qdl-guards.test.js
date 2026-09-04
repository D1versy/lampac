'use strict';
// Тесты UX-гардов и очереди транскода (см. claude/06 §Z):
// confirmPartial (гейт недокачанного), подтверждение удаления в quickMenu, dropAudioPref,
// бейдж «⚠ HEVC» в поиске раздач, pollTranscode с состоянием queued (полл не должен умирать на очереди).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Свежая песочница с перехватами: Select.show (все вызовы), Noty.show, Reguest.silent (по паттернам),
// Activity.push/replace, Controller.toggle, инъекция таймеров.
function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], pushes: [], replaces: 0, toggles: [], ticks: [], cleared: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push: (a) => calls.pushes.push(a), replace: () => calls.replaces++, active: () => ({}) },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl, sandbox } = H.loadQdl({
    lampa,
    setInterval: (fn, ms) => { calls.ticks.push(fn); return calls.ticks.length; },
    clearInterval: (id) => calls.cleared.push(id),
  });
  return { qdl, lampa, calls, sandbox };
}

const last = (a) => a[a.length - 1];

// ─────────────────────────────── gatePartial (qdl 2.93) ───────────────────────────────
// Был confirmPartial с пунктом «Смотреть всё равно». Стал ЖЁСТКИЙ гейт: у диалога один пункт
// и пути в плеер из него нет. Причина — последовательная загрузка в qBittorrent не включается
// нигде, файл на диске дырявый, и «всё равно» почти всегда давало отказ плеера или мусор.

test('gatePartial: progress 1 / без progress / local → run сразу, без Select', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.gatePartial({ progress: 1 }, () => runs++);
  qdl.gatePartial({}, () => runs++);                          // нет данных → fail-open
  qdl.gatePartial({ progress: 0.2, local: true }, () => runs++);
  qdl.gatePartial({ progress: 0.2, state: 'local' }, () => runs++);
  assert.strictEqual(runs, 4);
  assert.strictEqual(calls.selects.length, 0);
});

// 🔴 Порог 0.999, а не 1: взвешенный прогресс готовой группы сезонов не даёт ровной единицы,
// и на строгом сравнении «дождитесь загрузки» вылезало бы на полностью скачанном сериале.
test('gatePartial: 0.9995 считается готовым и играет без диалога', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.gatePartial({ progress: 0.9995 }, () => runs++);
  assert.strictEqual(runs, 1);
  assert.strictEqual(calls.selects.length, 0);
});

test('gatePartial: progress 0.5 → «Дождитесь загрузки», один пункт, в плеер не пускает', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.gatePartial({ progress: 0.5 }, () => runs++);
  assert.strictEqual(runs, 0, 'играть нельзя');
  assert.strictEqual(calls.selects.length, 1);
  assert.ok(calls.selects[0].title.indexOf('Дождитесь загрузки') !== -1, 'формулировка владельца');
  assert.ok(calls.selects[0].title.indexOf('50%') !== -1, 'сколько уже скачано');
  assert.strictEqual(calls.selects[0].items.length, 1, 'запасного пути в плеер нет');
  calls.selects[0].onSelect(calls.selects[0].items[0]);
  assert.strictEqual(runs, 0, 'единственный пункт НЕ запускает воспроизведение');
  assert.deepStrictEqual(calls.toggles, ['content']);
  calls.selects[0].onBack();
  assert.strictEqual(calls.toggles.length, 2);
});

// Живой прогресс поллера ПОБЕЖДАЕТ снимок — ровно этим чинится «докачалось, а клиент всё
// равно спрашивает»: /qdl/list кешируется 30 с, а qdl_progress на активности не обновляется.
test('gatePartial: живое «докачано» перекрывает устаревший снимок → играем без диалога', () => {
  const { qdl, calls } = rig();
  qdl.pgReset();
  qdl.pgApply({ ok: true, stamp: 's1', active: 0, pending: 0, items: [] });   // хеша нет в items = готов
  let runs = 0;
  qdl.gatePartial({ hash: 'a1', progress: 0.4 }, () => runs++);
  assert.strictEqual(runs, 1);
  assert.strictEqual(calls.selects.length, 0);
  qdl.pgReset();
});

test('gatePartial: живое «ещё качается» перекрывает снимок progress:1', () => {
  const { qdl, calls } = rig();
  qdl.pgReset();
  qdl.pgApply({ ok: true, stamp: 's1', active: 1, pending: 0, items: [{ h: 'a1', p: 0.4, s: 'downloading' }] });
  let runs = 0;
  qdl.gatePartial({ hash: 'a1', progress: 1 }, () => runs++);
  assert.strictEqual(runs, 0);
  assert.strictEqual(calls.selects.length, 1);
  qdl.pgReset();
});

// ok:false — это «не знаю», а не «всё скачано»: лёгший qBit и реплика не должны ни запирать, ни врать
test('gatePartial: ok:false не меняет вердикт — остаёмся на снимке', () => {
  const { qdl, calls } = rig();
  qdl.pgReset();
  qdl.pgApply({ ok: false, poll: 5, active: 0, pending: 0, items: [] });
  let runs = 0;
  qdl.gatePartial({ hash: 'a1', progress: 0.4 }, () => runs++);
  assert.strictEqual(runs, 0, 'снимок говорит «качается» — верим ему');
  assert.strictEqual(calls.selects.length, 1);
  qdl.pgReset();
});

// Киллсвитч partialPlayBlock:false — страховка на случай, если гейт где-то ошибётся
test('gatePartial: block:false с сервера снимает блокировку целиком', () => {
  const { qdl, calls } = rig();
  qdl.pgReset();
  qdl.setProgressConf({ block: false });
  let runs = 0;
  qdl.gatePartial({ progress: 0.5 }, () => runs++);
  assert.strictEqual(runs, 1);
  assert.strictEqual(calls.selects.length, 0);
  qdl.setProgressConf({ block: true });
  qdl.pgReset();
});

// ─────────────────────────────── watch / openDownload ───────────────────────────────

// 🔴 С 2.93 гейт живёт ВНУТРИ watchByHash, после fetchEpisodes: до неё ещё неизвестно, фильм это
// или сериал. Поэтому /qdl/episodes спрашивается ВСЕГДА, а диалог решается по прогрессу самого
// файла. Раньше гейт стоял до развилки — и сериал, у которого нужная серия давно скачана,
// ругался карточным (взвешенным по размеру) прогрессом.
test('watch: /qdl/episodes спрашивается всегда; гейт — по прогрессу ФАЙЛА', () => {
  const { qdl, calls } = rig({
    respond: (u) => (u.indexOf('/qdl/episodes?hash=a1') !== -1 ? [{ index: 0, name: 'a.mkv', progress: 0.4 }]
      : u.indexOf('/qdl/episodes?hash=b2') !== -1 ? [{ index: 0, name: 'b.mkv', progress: 1 }]
        : undefined),
  });
  qdl.watch({ hash: 'a1', progress: 0.4, name: 'X' });
  assert.strictEqual(calls.selects.length, 1, 'файл недокачан → гейт');
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/episodes?hash=a1') !== -1).length, 1);

  qdl.watch({ hash: 'b2', progress: 1, name: 'Y' });
  assert.strictEqual(calls.selects.length, 1, 'второго Select нет');
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/episodes?hash=b2') !== -1).length, 1);
});

// Снятие card-level гейта для сериала — обратная сторона того же переноса
test('watch: сериал с карточным 0.92 уходит на экран серий БЕЗ диалога', () => {
  const { qdl, calls } = rig({
    respond: (u) => (u.indexOf('/qdl/episodes') !== -1
      ? [{ index: 0, name: 'S01E01.mkv', progress: 1 }, { index: 1, name: 'S01E02.mkv', progress: 0.3 }]
      : undefined),
  });
  qdl.watch({ hash: 'a1', progress: 0.92, name: 'Сериал' });
  assert.strictEqual(calls.selects.length, 0, 'карточного гейта у сериала больше нет');
  assert.strictEqual(calls.pushes.filter((a) => a.component === 'qdl_episodes').length, 1);
});

test('openDownload: с TMDB-метой → полная карточка с qdl_hash/qdl_progress', () => {
  const { qdl, calls } = rig();
  qdl.openDownload({ hash: 'a1', progress: 0.7, meta: { id: 5, media_type: 'movie' } });
  assert.strictEqual(calls.pushes.length, 1);
  assert.strictEqual(calls.pushes[0].component, 'full');
  assert.strictEqual(calls.pushes[0].qdl_hash, 'a1');
  assert.strictEqual(calls.pushes[0].qdl_progress, 0.7);
});

// 🔥 Жалоба владельца (2.33): вход из «Загрузок» в скачанный тайтл jut.su сразу запускал плеер
// с позиции, оставшейся от онлайн-просмотра, вместо экрана карточки. Сервер помечает jut-маркеры
// meta.id = 0 («TMDB id у аниме с jut.su нет»), а проверка была на truthy — 0 уводил в ветку
// «просто играем». Экран qdl_card был написан и зарегистрирован, но его никто не открывал.
test('openDownload: jut-загрузка (meta.id = 0) → экран карточки qdl_card, а НЕ плеер', () => {
  const { qdl, calls } = rig();
  qdl.openDownload({
    hash: 'j1', progress: 1, local: true, name: 'guimi-zhi-zhu',
    jut: { slug: 'guimi-zhi-zhu' },
    meta: { id: 0, media_type: 'tv', title: 'Повелитель тайн' },
  });
  assert.strictEqual(calls.pushes.length, 1, 'экран обязан открыться');
  assert.strictEqual(calls.pushes[0].component, 'qdl_card');
  assert.strictEqual(calls.pushes[0].title, 'Повелитель тайн');
  assert.strictEqual(calls.pushes[0].qdl.hash, 'j1');
  assert.strictEqual(calls.selects.length, 0, 'ни гейта, ни выбора серии — просто карточка');
});

test('openDownload: безымянная раздача без меты → тот же экран qdl_card (гейт не показываем)', () => {
  const { qdl, calls } = rig();
  qdl.openDownload({ hash: 'c3', progress: 0.2, name: 'NoMeta' });
  assert.strictEqual(calls.pushes.length, 1);
  assert.strictEqual(calls.pushes[0].component, 'qdl_card');
  assert.strictEqual(calls.pushes[0].title, 'NoMeta');
  assert.strictEqual(calls.selects.length, 0, 'гейт недокачанного покажется уже по «Смотреть»');
});

// ─────────────────────────────── quickMenu: подтверждение удаления ───────────────────────────────

function openDelConfirm(rigObj, item) {
  rigObj.qdl.quickMenu(item);
  const menu = rigObj.calls.selects[0];
  const del = menu.items.filter((i) => i.act === 'del')[0];
  assert.ok(del, 'пункт удаления есть');
  menu.onSelect(del);
  return last(rigObj.calls.selects);
}

test('quickMenu del: двухступенчатое подтверждение, «Удалить» → запрос + чистка qdl_audio2 + Noty', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/delete') !== -1 ? { success: true } : undefined) });
  r.lampa.Storage.set('qdl_audio2', { x9: 'e2', other: 'e0' });

  const confirm = openDelConfirm(r, { hash: 'x9', name: 'Movie.mkv', progress: 1, meta: { title: 'Фильм' } });
  assert.notStrictEqual(confirm, r.calls.selects[0], 'открылся ВТОРОЙ Select');
  assert.ok(confirm.title.indexOf('Фильм') !== -1 && confirm.title.indexOf('с файлами?') !== -1);

  confirm.onSelect(confirm.items[0]);   // «Удалить»
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/delete?hash=x9') !== -1 && u.indexOf('deleteFiles=true') !== -1).length, 1);
  assert.ok(r.calls.noty.some((m) => m === 'Удалено'));
  assert.strictEqual(r.calls.replaces, 1);
  const audio = r.lampa.Storage.get('qdl_audio2', {});
  assert.strictEqual(audio.x9, undefined, 'своя озвучка вычищена');
  assert.strictEqual(audio.other, 'e0', 'чужая цела');
});

test('quickMenu del: «Отмена» → запроса нет, фокус возвращён', () => {
  const r = rig();
  const confirm = openDelConfirm(r, { hash: 'x9', name: 'Movie.mkv', progress: 1 });
  confirm.onSelect(confirm.items[1]);   // «Отмена»
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/delete') !== -1).length, 0);
  assert.ok(r.calls.toggles.indexOf('content') !== -1);
});

// ─────────────────────────────── dropAudioPref ───────────────────────────────

test('dropAudioPref: удаляет только свой hash; пустая карта не роняет', () => {
  const { qdl, lampa } = rig();
  lampa.Storage.set('qdl_audio2', { a: 'e1', b: 'e2' });
  qdl.dropAudioPref('a');
  assert.deepStrictEqual(Object.keys(lampa.Storage.get('qdl_audio2', {})), ['b']);
  qdl.dropAudioPref('missing');   // ничего не делает
  qdl.dropAudioPref(undefined);
  assert.deepStrictEqual(Object.keys(lampa.Storage.get('qdl_audio2', {})), ['b']);
});

// ─────────────────────────────── chooseAndDownload: бейдж кодека ───────────────────────────────

function searchRig(list) {
  return rig({
    respond: (u) => {
      if (u.indexOf('/qdl/search') !== -1) return list;
      if (u.indexOf('/qdl/add') !== -1) return { success: true, hash: 'h1' };
      return undefined;
    },
  });
}

test('поиск: «⚠ HEVC»/«⚠ AV1» только у плохих кодеков', () => {
  const r = searchRig([
    { title: 'A x265', codec: 'hevc', quality: 1080, size: '5 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
    { title: 'B av1', codec: 'av1', quality: 1080, size: '4 GB', tracker: 'tr', sid: 9, magnet: 'magnet:?xt=urn:btih:b' },
    { title: 'C x264', codec: 'h264', quality: 1080, size: '6 GB', tracker: 'tr', sid: 8, magnet: 'magnet:?xt=urn:btih:c' },
    { title: 'D none', codec: null, quality: 720, size: '2 GB', tracker: 'tr', sid: 7, magnet: 'magnet:?xt=urn:btih:d' },
  ]);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const items = last(r.calls.selects).items;
  assert.ok(items[0].subtitle.indexOf('⚠ HEVC') === 0);
  assert.ok(items[1].subtitle.indexOf('⚠ AV1') === 0);
  assert.strictEqual(items[2].subtitle.indexOf('⚠'), -1);
  assert.strictEqual(items[3].subtitle.indexOf('⚠'), -1);
});

test('поиск: выбор HEVC → предупреждение, но /qdl/add всё равно уходит; h264 → без предупреждения', () => {
  const r = searchRig([
    { title: 'A x265', codec: 'hevc', quality: 1080, size: '5 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
    { title: 'C x264', codec: 'h264', quality: 1080, size: '6 GB', tracker: 'tr', sid: 8, magnet: 'magnet:?xt=urn:btih:c' },
  ]);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const menu = last(r.calls.selects);

  menu.onSelect(menu.items[0]);   // HEVC
  assert.ok(r.calls.noty.some((m) => m.indexOf('HEVC') === 0 && m.indexOf('транскода') !== -1), 'предупреждение показано');
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/add') !== -1).length, 1, 'скачивание НЕ заблокировано');

  const notyBefore = r.calls.noty.length;
  menu.onSelect(menu.items[1]);   // h264
  assert.ok(!r.calls.noty.slice(notyBefore).some((m) => m.indexOf('HEVC') === 0), 'для h264 предупреждения нет');
});

// ─────────────────────────────── chooseAndDownload: умная выдача (⭐/🔔/серии/дата) ───────────────────────────────

test('поиск: ⭐ у rec + why в subtitle; 🔔 у watchable-сериала; «серии: N из M»; дата', () => {
  const r = searchRig([
    { title: 'A relevant', rec: true, why: 'точное имя · сезон 2 · серии 8 из 12 · 47 сидов', watchable: true,
      quality: 1080, size: '5 GB', tracker: 'rutracker', sid: 47, parselink: 'http://x/parsemagnet?id=1',
      ep: { have: 8, total: 12, ongoing: true }, date: '2026-07-15T10:00:00Z' },
    { title: 'B plain', quality: 1080, size: '4 GB', tracker: 'rutor', sid: 9, watchable: false,
      ep: { have: 12, total: 12, ongoing: false }, date: '2026-01-02T10:00:00Z', magnet: 'magnet:?xt=urn:btih:b' },
  ]);
  r.qdl.chooseAndDownload({ title: 'Сериал', media_type: 'tv', number_of_seasons: 2 });
  const items = last(r.calls.selects).items;

  assert.ok(items[0].title.indexOf('⭐ ') === 0, '⭐ только у rec');
  assert.ok(items[1].title.indexOf('⭐') === -1);
  assert.ok(items[0].subtitle.indexOf('точное имя') !== -1, 'why в subtitle у rec');
  assert.ok(items[0].subtitle.indexOf('🔔') !== -1, '🔔 у watchable-сериала');
  assert.ok(items[1].subtitle.indexOf('🔔') === -1);
  assert.ok(items[1].subtitle.indexOf('серии: 12 из 12') !== -1);
  assert.ok(items[1].subtitle.indexOf('02.01.26') !== -1, 'короткая дата раздачи');
});

test('поиск: url содержит season, /qdl/add увозит TMDB-контекст (title_original/year/is_serial/season)', () => {
  const r = searchRig([
    { title: 'A', quality: 1080, size: '5 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
  ]);
  r.qdl.chooseAndDownload({ title: 'Сериал', original_name: 'The Serial', first_air_date: '2024-01-01', media_type: 'tv', number_of_seasons: 2 });

  const searchUrl = r.calls.reqs.filter((u) => u.indexOf('/qdl/search') !== -1)[0];
  assert.ok(searchUrl.indexOf('&season=2') !== -1, 'season уходит в поиск');
  assert.ok(searchUrl.indexOf('&is_serial=2') !== -1);

  const menu = last(r.calls.selects);
  menu.onSelect(menu.items[0]);
  const addUrl = r.calls.reqs.filter((u) => u.indexOf('/qdl/add') !== -1)[0];
  assert.ok(addUrl.indexOf('title_original=The%20Serial') !== -1);
  assert.ok(addUrl.indexOf('&year=2024') !== -1);
  assert.ok(addUrl.indexOf('&is_serial=2') !== -1);
  assert.ok(addUrl.indexOf('&season=2') !== -1);
});

// TMDB id карточки — ключ к локальному DHT-индексу bitmagnet (Bitmagnet.cs): без него этот источник
// пуст, и именно так 04.09.2026 охота за сериями осталась слепой. Клиент шлёт id в /qdl/search
// (plugins/qdl.js, ~строка 3875) — сторожим, чтобы параметр не потерялся при следующей правке URL.
test('поиск: url несёт tmdb_id карточки (источник bitmagnet); без id параметра нет', () => {
  const r = searchRig([
    { title: 'A', quality: 1080, size: '5 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
  ]);
  r.qdl.chooseAndDownload({ id: 125988, title: 'Сериал', original_name: 'The Serial', first_air_date: '2024-01-01', media_type: 'tv', number_of_seasons: 2 });
  const withId = r.calls.reqs.filter((u) => u.indexOf('/qdl/search') !== -1)[0];
  assert.ok(withId.indexOf('&tmdb_id=125988') !== -1, 'tmdb_id уходит в поиск — иначе bitmagnet молчит');

  const r2 = searchRig([
    { title: 'A', quality: 1080, size: '5 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
  ]);
  r2.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const noId = r2.calls.reqs.filter((u) => u.indexOf('/qdl/search') !== -1)[0];
  assert.strictEqual(noId.indexOf('tmdb_id='), -1, 'без id в карточке параметр не подставляется');
});

test('поиск: старый ответ сервера БЕЗ новых полей рендерится как раньше (регрессия)', () => {
  const r = searchRig([
    { title: 'A x264', codec: 'h264', quality: 1080, size: '6 GB', tracker: 'tr', sid: 8, magnet: 'magnet:?xt=urn:btih:c' },
  ]);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const items = last(r.calls.selects).items;
  assert.strictEqual(items[0].title, 'A x264', 'без ⭐');
  assert.ok(items[0].subtitle.indexOf('1080p') !== -1 && items[0].subtitle.indexOf('сидов: 8') !== -1);
  assert.ok(items[0].subtitle.indexOf('🔔') === -1, 'для фильма 🔔 не показываем');
});

// ─────────────────────────────── pollTranscode: очередь ───────────────────────────────

test('pollTranscode: queued(2)→queued(1)→running→done — тост очереди один раз, полл живёт, clearInterval в конце', () => {
  const statuses = [
    { state: 'queued', position: 2 },
    { state: 'queued', position: 1 },
    { state: 'running', progress: 0.5 },
    { state: 'done' },
  ];
  let i = 0;
  const r = rig({ respond: (u) => (u.indexOf('/qdl/transcode/status') !== -1 ? statuses[Math.min(i++, statuses.length - 1)] : undefined) });
  r.qdl.pollTranscode('h1', 'Кино');
  assert.strictEqual(r.calls.ticks.length, 1, 'setInterval установлен');
  const tick = r.calls.ticks[0];

  tick();   // queued(2)
  tick();   // queued(1) — второй тост очереди не нужен
  assert.strictEqual(r.calls.noty.filter((m) => m.indexOf('в очереди') !== -1).length, 1);
  assert.strictEqual(r.calls.cleared.length, 0, 'полл НЕ умер на очереди');

  tick();   // running 50%
  assert.ok(r.calls.noty.some((m) => m.indexOf('50%') !== -1));

  tick();   // done
  assert.strictEqual(r.calls.cleared.length, 1, 'полл остановлен');
  assert.ok(last(r.calls.noty).indexOf('теперь MP4') !== -1);
});

test('pollTranscode: running→none → тост «прервано»; none сразу → тихая остановка', () => {
  const seq1 = [{ state: 'running', progress: 0.2 }, { state: 'none' }];
  let i = 0;
  const r1 = rig({ respond: (u) => (u.indexOf('/status') !== -1 ? seq1[Math.min(i++, 1)] : undefined) });
  r1.qdl.pollTranscode('h1', 'Кино');
  r1.calls.ticks[0]();
  r1.calls.ticks[0]();
  assert.ok(last(r1.calls.noty).indexOf('прервано') !== -1);
  assert.strictEqual(r1.calls.cleared.length, 1);

  const r2 = rig({ respond: (u) => (u.indexOf('/status') !== -1 ? { state: 'none' } : undefined) });
  r2.qdl.pollTranscode('h2', 'Кино');
  r2.calls.ticks[0]();
  assert.strictEqual(r2.calls.noty.length, 0, 'без sawAlive — молча');
  assert.strictEqual(r2.calls.cleared.length, 1);
});

test('pollTranscode: повторный вызов того же hash не создаёт второй интервал', () => {
  const r = rig({ respond: () => ({ state: 'queued', position: 1 }) });
  r.qdl.pollTranscode('h1', 'Кино');
  r.qdl.pollTranscode('h1', 'Кино');
  assert.strictEqual(r.calls.ticks.length, 1);
});
