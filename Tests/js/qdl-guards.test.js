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

// ─────────────────────────────── confirmPartial ───────────────────────────────

test('confirmPartial: progress 1 / без progress / local → run сразу, без Select', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.confirmPartial({ progress: 1 }, () => runs++);
  qdl.confirmPartial({}, () => runs++);
  qdl.confirmPartial({ progress: 0.2, local: true }, () => runs++);
  qdl.confirmPartial({ progress: 0.2, state: 'local' }, () => runs++);
  assert.strictEqual(runs, 4);
  assert.strictEqual(calls.selects.length, 0);
});

test('confirmPartial: progress 0.5 → Select с «50%», ok → run', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.confirmPartial({ progress: 0.5 }, () => runs++);
  assert.strictEqual(runs, 0, 'до подтверждения не играем');
  assert.strictEqual(calls.selects.length, 1);
  assert.ok(calls.selects[0].title.indexOf('50%') !== -1);
  calls.selects[0].onSelect(calls.selects[0].items[0]);   // «Смотреть всё равно»
  assert.strictEqual(runs, 1);
});

test('confirmPartial: «Отмена» → run не вызван + возврат фокуса', () => {
  const { qdl, calls } = rig();
  let runs = 0;
  qdl.confirmPartial({ progress: 0.3 }, () => runs++);
  calls.selects[0].onSelect(calls.selects[0].items[1]);   // «Отмена»
  assert.strictEqual(runs, 0);
  assert.deepStrictEqual(calls.toggles, ['content']);
  calls.selects[0].onBack();
  assert.strictEqual(calls.toggles.length, 2);
});

// ─────────────────────────────── watch / openDownload ───────────────────────────────

test('watch: недокачанный item → гейт; скачанный → сразу список серий (/qdl/episodes)', () => {
  const { qdl, calls } = rig();
  qdl.watch({ hash: 'a1', progress: 0.4, name: 'X' });
  assert.strictEqual(calls.selects.length, 1, 'гейт показан');
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/episodes') !== -1).length, 0);

  qdl.watch({ hash: 'b2', progress: 1, name: 'Y' });
  assert.strictEqual(calls.selects.length, 1, 'второго Select нет');
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/episodes?hash=b2') !== -1).length, 1);
});

test('openDownload: с метой → Activity.push содержит qdl_progress; без меты и progress<1 → гейт', () => {
  const { qdl, calls } = rig();
  qdl.openDownload({ hash: 'a1', progress: 0.7, meta: { id: 5, media_type: 'movie' } });
  assert.strictEqual(calls.pushes.length, 1);
  assert.strictEqual(calls.pushes[0].qdl_hash, 'a1');
  assert.strictEqual(calls.pushes[0].qdl_progress, 0.7);

  qdl.openDownload({ hash: 'c3', progress: 0.2, name: 'NoMeta' });
  assert.strictEqual(calls.pushes.length, 1, 'карточка не открывается без меты');
  assert.strictEqual(calls.selects.length, 1, 'гейт для прямого воспроизведения');
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
