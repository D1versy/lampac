'use strict';
// Тесты фичи «Транскодировать в MP4»: canTranscode (кому предлагать конверсию)
// и состав пунктов quickMenu (транскод — только завершённым раздачам; локальным
// файлам не предлагаем ни транскод, ни слежение за сериями).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl();

// ─────────────────────────────── canTranscode ───────────────────────────────

test('canTranscode: завершённая раздача → true', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, state: 'stalledUP' }), true);
});

test('canTranscode: недокачанная раздача → false', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 0.5, state: 'downloading' }), false);
  assert.strictEqual(qdl.canTranscode({ hash: 'x', state: 'downloading' }), false);   // progress отсутствует
});

test('canTranscode: уже локальный MP4 → false (по флагу и по state)', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, local: true }), false);
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, state: 'local' }), false);
});

test('canTranscode: null/undefined → false без исключений', () => {
  assert.strictEqual(qdl.canTranscode(null), false);
  assert.strictEqual(qdl.canTranscode(undefined), false);
});

// ─────────────────────────────── quickMenu: состав пунктов ───────────────────────────────

function menuItemsFor(item) {
  let captured = null;
  const lampa = H.makeLampa({ Select: { show: (opts) => { captured = opts; } } });
  const { qdl: q } = H.loadQdl({ lampa });
  q.quickMenu(item);
  assert.ok(captured, 'Select.show должен быть вызван');
  return captured.items.map((i) => i.title);
}

test('quickMenu: завершённой раздаче предлагаются транскод и слежение', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mkv', progress: 1, state: 'stalledUP' });
  assert.ok(titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1), 'должен быть пункт транскода');
  assert.ok(titles.some((t) => t.indexOf('Следить за новыми сериями') !== -1), 'должен быть пункт слежения');
  assert.ok(titles.some((t) => t.indexOf('Удалить') !== -1));
});

test('quickMenu: недокачанной раздаче транскод не предлагается', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mkv', progress: 0.4, state: 'downloading' });
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1));
});

test('quickMenu: локальному MP4 — ни транскода, ни слежения, но смотреть/удалить можно', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mp4', progress: 1, state: 'local', local: true });
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1), 'транскод уже сделан');
  assert.ok(!titles.some((t) => t.indexOf('серия') !== -1 || t.indexOf('сериями') !== -1), 'слежение бессмысленно без торрента');
  assert.ok(titles.some((t) => t.indexOf('Смотреть') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Удалить') !== -1));
});

// ─────────────────────────────── startTranscode: оверлей/финализация ───────────────────────────────

function reqStub(routes, calls) {
  // routes: url-фрагмент → ответ; calls: сюда пишутся все запрошенные url
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        calls.push(String(url));
        for (const k of Object.keys(routes)) if (String(url).indexOf(k) !== -1) { ok(routes[k]); return; }
        ok({});
      };
    },
  };
}

test('startTranscode: сериал БЕЗ слежения → сразу finalize-запрос без диалога', () => {
  const calls = [];
  let select = null;
  const lampa = H.makeLampa(Object.assign(
    reqStub({ '/qdl/transcode?': { success: true, queued: 1, files: 3 } }, calls),
    { Select: { show: (o) => { select = o; } } }));
  const { qdl: q } = H.loadQdl({ lampa });
  q.startTranscode({ hash: 'x'.repeat(40), name: 'S', progress: 1, watched: false });
  assert.strictEqual(select, null, 'диалог не показывается');
  assert.ok(calls.some((u) => u.indexOf('/qdl/transcode?hash=') !== -1 && u.indexOf('mode=') === -1), 'mode не передаётся (дефолт сервера)');
});

test('startTranscode: сериал ПОД слежением → диалог, выбор оверлея шлёт mode=overlay', () => {
  const calls = [];
  let select = null;
  const files = [{ index: 0, name: 'E1.mkv' }, { index: 1, name: 'E2.mkv' }];
  const lampa = H.makeLampa(Object.assign(
    reqStub({ '/qdl/files': files, '/qdl/transcode?': { success: true, queued: 1, files: 2 } }, calls),
    { Select: { show: (o) => { select = o; } } }));
  const { qdl: q } = H.loadQdl({ lampa });
  q.startTranscode({ hash: 'y'.repeat(40), name: 'S', progress: 1, watched: true });
  assert.ok(select, 'диалог выбора режима показан');
  const overlay = select.items.filter((i) => i.mode === 'overlay')[0];
  const finalize = select.items.filter((i) => i.mode === 'finalize')[0];
  assert.ok(overlay && finalize, 'есть оба режима');
  select.onSelect(overlay);
  assert.ok(calls.some((u) => u.indexOf('mode=overlay') !== -1), 'ушёл mode=overlay');
});

test('startTranscode: фильм под слежением (1 файл) → диалога нет', () => {
  const calls = [];
  let select = null;
  const lampa = H.makeLampa(Object.assign(
    reqStub({ '/qdl/files': [{ index: 0, name: 'M.mkv' }], '/qdl/transcode?': { success: true, queued: 1 } }, calls),
    { Select: { show: (o) => { select = o; } } }));
  const { qdl: q } = H.loadQdl({ lampa });
  q.startTranscode({ hash: 'z'.repeat(40), name: 'M', progress: 1, watched: true });
  assert.strictEqual(select, null);
  assert.ok(calls.some((u) => u.indexOf('/qdl/transcode?') !== -1));
});

// ─────────────────────────────── pollTranscode: тосты «серия i/N» ───────────────────────────────

test('pollTranscode: сериальный статус → тост с «серия i/N»', () => {
  const toasts = [];
  let tick = null;
  const lampa = H.makeLampa(Object.assign(
    reqStub({ '/qdl/transcode/status': { state: 'running', progress: 0.42, fileDone: 2, filesTotal: 8 } }, []),
    { Noty: { show: (m) => toasts.push(m) } }));
  const { qdl: q } = H.loadQdl({
    lampa,
    setInterval: (fn) => { tick = fn; return 1; },
    clearInterval: () => {},
  });
  q.pollTranscode('h'.repeat(40), 'Сериал');
  tick();
  assert.ok(toasts.some((m) => m.indexOf('серия 3/8') !== -1 && m.indexOf('40%') !== -1),
    'тост «серия 3/8 — 40%», получено: ' + JSON.stringify(toasts));
});
