'use strict';
// Тесты зеркала прогресса устройства (file_view ↔ нативное KV AndroidJS):
// парс/валидация, инварианты слияния mirrorMerge, кап mirrorPrune, инициализация
// (анти-клоббер с Timeline.read), дебаунс onTimelineUpdate, деградации (нет AndroidJS,
// kill-switch, set() бросает после dump()), ключ rawPlay после унификации на pickTimeline.
// Канон фичи — медиасервер claude/06 §AM.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl();   // общий sandbox для чистых функций

function road(percent, time, duration, extra) {
  return Object.assign({ percent: percent, time: time, duration: duration }, extra || {});
}

// объекты из vm-песочницы имеют чужой Object.prototype → deepStrictEqual с литералами
// теста падает на сверке прототипов; JSON-раундтрип приводит к реалму теста
function plain(x) { return JSON.parse(JSON.stringify(x)); }

/** Мок нативного KV (контракт AndroidJS.get/set: get → null если нет — как Android). */
function mockKV(initial) {
  const store = Object.assign({}, initial || {});
  const calls = { get: [], set: [] };
  return {
    _store: store, _calls: calls,
    get(k) { calls.get.push(k); return Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null; },
    set(k, v) { calls.set.push([k, v]); store[k] = v; },
  };
}

/** Инъецируемые таймеры для дебаунс-теста. */
function fakeTimers() {
  const timers = [];
  return {
    timers,
    setTimeout(fn, ms) { timers.push({ fn, ms, cleared: false, ran: false }); return timers.length; },
    clearTimeout(id) { if (timers[id - 1]) timers[id - 1].cleared = true; },
    runPending() { timers.forEach((t) => { if (!t.cleared && !t.ran) { t.ran = true; t.fn(); } }); },
  };
}

// ─────────────────────────────── mirrorParse / mirrorValidRoad ───────────────────────────────

test('mirrorParse: null/пусто/битый JSON/не-объект → {}, валидный объект → как есть', () => {
  assert.deepStrictEqual(plain(qdl.mirrorParse(null)), {});   // Android get() без ключа
  assert.deepStrictEqual(plain(qdl.mirrorParse('')), {});     // Apple get() без ключа
  assert.deepStrictEqual(plain(qdl.mirrorParse('не json')), {});
  assert.deepStrictEqual(plain(qdl.mirrorParse('[1,2]')), {});
  assert.deepStrictEqual(plain(qdl.mirrorParse('42')), {});
  assert.deepStrictEqual(plain(qdl.mirrorParse('{"h":{"percent":5}}')), { h: { percent: 5 } });
});

test('mirrorValidRoad: числа ≥0 — ок; легаси-число, строки, отрицательные, null — нет', () => {
  assert.ok(qdl.mirrorValidRoad(road(0, 0, 0)));
  assert.ok(qdl.mirrorValidRoad(road(97, 5000.5, 5200, { profile: 1 })));
  assert.ok(!qdl.mirrorValidRoad(42));                        // легаси «просто percent» не зеркалим
  assert.ok(!qdl.mirrorValidRoad(null));
  assert.ok(!qdl.mirrorValidRoad({ percent: '50', time: 1, duration: 1 }));
  assert.ok(!qdl.mirrorValidRoad({ percent: 50, time: -1, duration: 1 }));
  assert.ok(!qdl.mirrorValidRoad({ percent: 50, time: 1 }));  // нет duration
});

// ─────────────────────────────── mirrorMerge: инварианты ───────────────────────────────

test('merge: первый запуск — посев локального сентинел-меткой t=1 (не now: старьё не выдаёт себя за свежее)', () => {
  const r = qdl.mirrorMerge({ h1: road(40, 100, 250) }, {}, {}, 7000);
  assert.strictEqual(r.changedMirror, true);
  assert.strictEqual(r.changedLocal, false);
  assert.deepStrictEqual(plain(r.mirror.h1), road(40, 100, 250, { t: 1 }));
  assert.strictEqual(r.meta.h1, 1);
});

test('merge: чистое устройство — принятие из зеркала, поле t в file_view не попадает', () => {
  const r = qdl.mirrorMerge({}, { h1: road(40, 100, 250, { t: 5000 }) }, {}, 9000);
  assert.strictEqual(r.changedLocal, true);
  assert.deepStrictEqual(plain(r.local.h1), road(40, 100, 250));
  assert.ok(!('t' in r.local.h1));
  assert.strictEqual(r.meta.h1, 5000);
});

test('merge: конфликт, зеркало новее меты → принять зеркальное', () => {
  const r = qdl.mirrorMerge(
    { h1: road(10, 60, 600) },
    { h1: road(80, 480, 600, { t: 200 }) },
    { h1: 100 }, 9000);
  assert.strictEqual(r.changedLocal, true);
  assert.strictEqual(r.local.h1.percent, 80);
  assert.strictEqual(r.meta.h1, 200);
});

test('merge: зеркало отстало от меты → ремонт (перезалить локальное с t=meta)', () => {
  const r = qdl.mirrorMerge(
    { h1: road(80, 480, 600) },
    { h1: road(10, 60, 600, { t: 200 }) },
    { h1: 300 }, 9000);
  assert.strictEqual(r.changedLocal, false);
  assert.strictEqual(r.local.h1.percent, 80);                 // локальное нетронуто
  assert.deepStrictEqual(plain(r.mirror.h1), road(80, 480, 600, { t: 300 }));
  assert.strictEqual(r.changedMirror, true);
});

test('merge: метки равны → no-op (нет холостых записей при каждом старте)', () => {
  const r = qdl.mirrorMerge(
    { h1: road(80, 480, 600) },
    { h1: road(80, 480, 600, { t: 300 }) },
    { h1: 300 }, 9000);
  assert.strictEqual(r.changedLocal, false);
  assert.strictEqual(r.changedMirror, false);
});

test('merge: мусор в зеркале (нет чисел / нет t) пропущен, в file_view не попал', () => {
  const r = qdl.mirrorMerge({}, {
    bad1: { t: 100 },                                         // нет road-полей
    bad2: road(50, 10, 100),                                  // нет t
    ok: road(30, 30, 100, { t: 50 }),
  }, {}, 9000);
  assert.deepStrictEqual(Object.keys(r.local), ['ok']);
});

test('merge: percent≥96 и незнакомые поля road переносятся без потерь', () => {
  const r = qdl.mirrorMerge({}, { h1: road(97, 590, 600, { t: 10, profile: 3, foo: 'x' }) }, {}, 9000);
  assert.deepStrictEqual(plain(r.local.h1), road(97, 590, 600, { profile: 3, foo: 'x' }));
});

test('merge: гигиена меты — ключи, которых нет ни в local, ни в mirror, удалены', () => {
  const r = qdl.mirrorMerge({}, {}, { dead: 50 }, 9000);
  assert.ok(!('dead' in r.meta));
});

test('merge: входы не мутируются', () => {
  const local = { h1: road(10, 60, 600) };
  const mirror = { h2: road(20, 90, 600, { t: 5 }) };
  const meta = {};
  qdl.mirrorMerge(local, mirror, meta, 9000);
  assert.deepStrictEqual(local, { h1: road(10, 60, 600) });
  assert.deepStrictEqual(mirror, { h2: road(20, 90, 600, { t: 5 }) });
  assert.deepStrictEqual(meta, {});
});

// ────────────────── инварианты по находкам ревью (клоббер/удаление/часы/churn) ──────────────────

test('merge: КЛОББЕР-ЗАЩИТА — сид другого origin не затирает локальный прогресс при пустой мете', () => {
  // рассинхрон до 2.9: у этого origin 80% (свежее), другой origin посеял свои старые 30% первым
  const r = qdl.mirrorMerge(
    { h1: road(80, 480, 600) },
    { h1: road(30, 180, 600, { t: 1 }) },
    {}, 9000);
  assert.strictEqual(r.changedLocal, false);
  assert.strictEqual(r.local.h1.percent, 80);      // локальное живо
  assert.strictEqual(r.mirror.h1.percent, 30);     // зеркало не трогаем — рассудит реальный просмотр
  assert.ok(!('h1' in r.meta), 'мета не ставится при no-op');
});

test('merge: реальный апдейт (t>сентинела) при пустой мете принимается поверх локального', () => {
  const r = qdl.mirrorMerge(
    { h1: road(30, 180, 600) },
    { h1: road(80, 480, 600, { t: 5000 }) },
    {}, 9000);
  assert.strictEqual(r.local.h1.percent, 80);
  assert.strictEqual(r.meta.h1, 5000);
});

test('merge: удаление уважается — локально стёрто при mt<=меты → запись уходит и из зеркала', () => {
  const r = qdl.mirrorMerge(
    {},
    { h1: road(50, 300, 600, { t: 200 }) },
    { h1: 200 }, 9000);
  assert.ok(!('h1' in r.mirror));
  assert.ok(!('h1' in r.local));
  assert.ok(!('h1' in r.meta));
  assert.strictEqual(r.changedMirror, true);
});

test('merge: кламп будущей метки — увод часов вперёд не даёт вечного преимущества', () => {
  const r = qdl.mirrorMerge(
    {},
    { h1: road(10, 60, 600, { t: 9000 + 999999999 }) },
    {}, 9000);
  assert.ok(r.mirror.h1.t <= 9000 + 300000, 'метка в зеркале переписана клампнутой (де-пойзон)');
  assert.ok(r.meta.h1 <= 9000 + 300000, 'мета не улетает в будущее');
  assert.strictEqual(r.local.h1.percent, 10);      // сама запись принята (known=0, локального нет)
});

test('merge: бюджет посева — при полном зеркале лишние локальные не сеются (нет вечного churn)', () => {
  const mirror = { m1: road(1, 1, 10, { t: 100 }), m2: road(2, 2, 10, { t: 200 }) };
  const local = { m1: road(1, 1, 10), m2: road(2, 2, 10), l1: road(3, 3, 10), l2: road(4, 4, 10) };
  const r = qdl.mirrorMerge(local, mirror, { m1: 100, m2: 200 }, 9000, 2);
  assert.ok(!('l1' in r.mirror) && !('l2' in r.mirror), 'сверх капа не сеем');
  assert.strictEqual(r.changedMirror, false, 'повторный старт не перезаписывает KV впустую');
  // при появлении места — сеется ровно до капа
  const r2 = qdl.mirrorMerge(local, mirror, { m1: 100, m2: 200 }, 9000, 3);
  assert.ok('l1' in r2.mirror);
  assert.ok(!('l2' in r2.mirror));
});

test('merge: ремонт после вайпа «Мигрировать» — пересев t=1 не перебивает, origin с метой восстанавливает своё', () => {
  // origin A вайпнул KV (Android dumpStorage → clear) и пересеял свой road сентинелом;
  // у origin B есть реальная мета и свой local → зеркало восстанавливается данными B
  const r = qdl.mirrorMerge(
    { h1: road(80, 480, 600) },
    { h1: road(30, 180, 600, { t: 1 }) },
    { h1: 7000 }, 9000);
  assert.strictEqual(r.local.h1.percent, 80);
  assert.deepStrictEqual(plain(r.mirror.h1), road(80, 480, 600, { t: 7000 }));
  assert.strictEqual(r.meta.h1, 7000);
});

// ─────────────────────────────── mirrorPrune ───────────────────────────────

test('mirrorPrune: остаются cap самых свежих по t; запись без t вылетает первой', () => {
  const m = {
    old: road(1, 1, 10, { t: 1 }),
    mid: road(2, 2, 10, { t: 2 }),
    fresh: road(3, 3, 10, { t: 3 }),
    noT: road(4, 4, 10),
  };
  const out = qdl.mirrorPrune(m, 2);
  assert.deepStrictEqual(Object.keys(out).sort(), ['fresh', 'mid']);
  assert.strictEqual(qdl.mirrorPrune(m, 10), m);              // под капом — тот же объект
});

// ─────────────────────────────── initTimelineMirror (интеграция) ───────────────────────────────
// qdl 2.18: дефолт режима — 'auto' (зеркало уступает серверному таймкод-синку).
// Интеграционные тесты самого зеркала форсят 'on' — прежнее синхронное поведение.

test('init: принятие из зеркала — Storage.set(file_view) СТРОГО до Timeline.read, подписка на update', () => {
  const kv = mockKV({ qdl_file_view: JSON.stringify({ hm: road(40, 100, 2000, { t: 5000 }) }) });
  let readAfterSet = false;
  const follows = [];
  const lampa = H.makeLampa({
    Timeline: {
      read() { readAfterSet = lampa.Storage._calls.some((c) => c[0] === 'file_view'); },
      listener: { follow(name, fn) { follows.push([name, typeof fn]); } },
    },
  });
  lampa._storage.set('qdl_tl_mirror', 'on');
  const { qdl: q } = H.loadQdl({ lampa, windowExtra: { AndroidJS: kv } });
  q.initTimelineMirror();

  const fv = lampa.Storage._calls.filter((c) => c[0] === 'file_view');
  assert.strictEqual(fv.length, 1);
  assert.deepStrictEqual(plain(fv[0][1].hm), road(40, 100, 2000));   // без t
  assert.strictEqual(readAfterSet, true, 'Timeline.read() должен идти после Storage.set (анти-клоббер)');
  assert.deepStrictEqual(follows, [['update', 'function']]);
  const meta = lampa.Storage._calls.filter((c) => c[0] === 'qdl_tl_meta').pop();
  assert.strictEqual(meta[1].hm, 5000);
});

test('init без Timeline.read: file_view НЕ трогаем, мету не двигаем, но посев в KV уехал', () => {
  const kv = mockKV();
  const lampa = H.makeLampa();                                // дефолтный Timeline: без read/listener
  lampa._storage.set('file_view', { h1: road(30, 60, 600) });
  lampa._storage.set('qdl_tl_mirror', 'on');
  const { qdl: q } = H.loadQdl({ lampa, windowExtra: { AndroidJS: kv } });
  q.initTimelineMirror();

  assert.ok(!lampa.Storage._calls.some((c) => c[0] === 'file_view'), 'без read file_view не пишем');
  const meta = lampa.Storage._calls.filter((c) => c[0] === 'qdl_tl_meta').pop();
  assert.deepStrictEqual(plain(meta[1]), {});                 // мета осталась исходной
  const written = JSON.parse(kv._store.qdl_file_view);
  assert.strictEqual(written.h1.percent, 30);                 // посев состоялся
  assert.strictEqual(typeof written.h1.t, 'number');
});

test('onTimelineUpdate: дебаунс — три апдейта → ОДНА запись KV со всеми хэшами и метками', () => {
  const kv = mockKV();
  const ft = fakeTimers();
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow() {} } } });
  lampa._storage.set('qdl_tl_mirror', 'on');
  const { qdl: q } = H.loadQdl({
    lampa, windowExtra: { AndroidJS: kv },
    setTimeout: ft.setTimeout, clearTimeout: ft.clearTimeout,
  });
  q.initTimelineMirror();
  const before = kv._calls.set.length;

  q.onTimelineUpdate({ data: { hash: 'hA', road: road(10, 60, 600) } });
  q.onTimelineUpdate({ data: { hash: 'hB', road: road(20, 120, 600) } });
  q.onTimelineUpdate({ data: { hash: 'hA', road: road(15, 90, 600) } });
  assert.strictEqual(kv._calls.set.length, before, 'до срабатывания таймера записи нет');
  ft.runPending();

  assert.strictEqual(kv._calls.set.length, before + 1);
  const written = JSON.parse(kv._store.qdl_file_view);
  assert.strictEqual(written.hA.percent, 15);                 // последний апдейт победил
  assert.strictEqual(written.hB.percent, 20);
  assert.ok(typeof written.hA.t === 'number' && typeof written.hB.t === 'number');
  const meta = lampa.Storage._calls.filter((c) => c[0] === 'qdl_tl_meta').pop();
  assert.ok(meta[1].hA && meta[1].hB, 'мета проставлена до записи зеркала');
});

test('AndroidJS.set бросает (Android после dump()) → не падаем; следующий старт дозаливает зеркало', () => {
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow() {} } } });
  lampa._storage.set('file_view', { h1: road(30, 60, 600) });
  lampa._storage.set('qdl_tl_mirror', 'on');
  const broken = { get: () => null, set: () => { throw new Error('already dumped'); } };
  const { qdl: q1 } = H.loadQdl({ lampa, windowExtra: { AndroidJS: broken } });
  assert.doesNotThrow(() => q1.initTimelineMirror());

  // новый запуск (новый sandbox = новое приложение), KV снова пишется — посев догоняет
  const kv = mockKV();
  const { qdl: q2 } = H.loadQdl({ lampa, windowExtra: { AndroidJS: kv } });
  q2.initTimelineMirror();
  const written = JSON.parse(kv._store.qdl_file_view);
  assert.strictEqual(written.h1.percent, 30);
});

test('guard: повторный initTimelineMirror → одна подписка follow', () => {
  const follows = [];
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow(n) { follows.push(n); } } } });
  lampa._storage.set('qdl_tl_mirror', 'on');
  const { qdl: q } = H.loadQdl({ lampa, windowExtra: { AndroidJS: mockKV() } });
  q.initTimelineMirror();
  q.initTimelineMirror();
  assert.strictEqual(follows.length, 1);
});

test('kill-switch qdl_tl_mirror=off → ни одного обращения к AndroidJS', () => {
  const kv = mockKV();
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow() {} } } });
  lampa._storage.set('qdl_tl_mirror', 'off');
  const { qdl: q } = H.loadQdl({ lampa, windowExtra: { AndroidJS: kv } });
  q.initTimelineMirror();
  assert.strictEqual(kv._calls.get.length + kv._calls.set.length, 0);
});

test('без AndroidJS (браузер): init тихо выходит, Storage не тронут', () => {
  const lampa = H.makeLampa();
  const { qdl: q } = H.loadQdl({ lampa });
  assert.doesNotThrow(() => q.initTimelineMirror());
  assert.strictEqual(lampa.Storage._calls.length, 0);
});

// ─────────────────────────────── режим 'auto' (qdl 2.18): сервер главный ───────────────────────────────

/** Инъецируемый setInterval: тик дёргается руками. */
function fakeInterval() {
  const timers = [];
  return {
    timers,
    setInterval(fn, ms) { timers.push({ fn, ms, cleared: false }); return timers.length; },
    clearInterval(id) { if (timers[id - 1]) timers[id - 1].cleared = true; },
    tick(n) { for (let i = 0; i < n; i++) timers.forEach((t) => { if (!t.cleared) t.fn(); }); },
  };
}

test("auto: серверный таймкод-плагин появился → зеркало НЕ стартует (KV не тронут, подписки нет)", () => {
  const kv = mockKV({ qdl_file_view: JSON.stringify({ h1: road(40, 100, 2000, { t: 5000 }) }) });
  const fi = fakeInterval();
  const follows = [];
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow(n) { follows.push(n); } } } });
  const win = { AndroidJS: kv };
  const { qdl: q, sandbox } = H.loadQdl({
    lampa, windowExtra: win,
    setInterval: fi.setInterval, clearInterval: fi.clearInterval,
  });
  q.initTimelineMirror();
  assert.strictEqual(follows.length, 0, 'до решения зеркало не подписывается');
  sandbox.window.lampac_timecode_plugin = true;   // sync.js догрузил timecode.js
  fi.tick(3);
  assert.strictEqual(follows.length, 0, 'сервер главный — зеркало так и не стартовало');
  assert.strictEqual(kv._calls.get.length + kv._calls.set.length, 0);
  assert.ok(fi.timers[0].cleared, 'поллинг остановлен');
});

test('auto: серверный синк так и не появился → фолбэк в зеркало после таймаута поллинга', () => {
  const kv = mockKV({ qdl_file_view: JSON.stringify({ h1: road(40, 100, 2000, { t: 5000 }) }) });
  const fi = fakeInterval();
  const follows = [];
  const lampa = H.makeLampa({ Timeline: { read() {}, listener: { follow(n) { follows.push(n); } } } });
  const { qdl: q } = H.loadQdl({
    lampa, windowExtra: { AndroidJS: kv },
    setInterval: fi.setInterval, clearInterval: fi.clearInterval,
  });
  q.initTimelineMirror();
  fi.tick(30);   // 30 × 500 мс = 15 с
  assert.deepStrictEqual(follows, ['update'], 'после таймаута зеркало стартовало');
  const fv = lampa.Storage._calls.filter((c) => c[0] === 'file_view');
  assert.strictEqual(fv.length, 1, 'слияние применено (принятие из KV)');
  assert.ok(fi.timers[0].cleared, 'поллинг остановлен');
});

// ─────────────────────────────── бакет серверных таймкодов (qdl 2.18) ───────────────────────────────

test('tlBucket: seriesKey из f.tl (сегмент sNeM отрезан), без tl — infohash; set/clear защищён от чужого', () => {
  const { qdl: q, sandbox } = H.loadQdl();
  assert.strictEqual(q.tlBucket([{ tl: 'breaking-bad:s1e4' }], 'HASH1'), 'qdl_breaking-bad');
  assert.strictEqual(q.tlBucket([{ name: 'extra.mkv' }], 'HASH1'), 'qdl_HASH1');   // файлы без tl
  assert.strictEqual(q.tlBucket([], 'HASH1'), 'qdl_HASH1');
  q.setTlBucket('qdl_a');
  assert.strictEqual(sandbox.window.qdl_timecode_card, 'qdl_a');
  q.clearTlBucket('qdl_b');   // чужой бакет не снимаем (destroy отложен на 200 мс, экран уже сменился)
  assert.strictEqual(sandbox.window.qdl_timecode_card, 'qdl_a');
  q.clearTlBucket('qdl_a');
  assert.ok(!sandbox.window.qdl_timecode_card);
});

test('setTlBucket шлёт timecode_pullFromServer; initTimecodeBridge перерисовывает активный экран по timecode_updated', () => {
  const events = [];
  const subs = {};
  const lampa = H.makeLampa({
    Listener: {
      follow(name, fn) { (subs[name] = subs[name] || []).push(fn); },
      send(name, e) { events.push([name, e && e.type]); (subs[name] || []).forEach((fn) => fn(e)); },
    },
  });
  const { qdl: q } = H.loadQdl({ lampa });
  q.initTimecodeBridge();
  q.setTlBucket('qdl_x');
  assert.deepStrictEqual(events, [['lampac', 'timecode_pullFromServer']]);
  // 'timecode_updated' без активного экрана — не падает
  assert.doesNotThrow(() => lampa.Listener.send('lampac', { type: 'timecode_updated' }));
});

// ─────────────────────────────── rawPlay после унификации на pickTimeline ───────────────────────────────

test('rawPlay без объекта файла → бинарно прежний легаси-ключ (регресс совместимости)', () => {
  const played = [];
  const lampa = H.makeLampa({ Player: { play(i) { played.push(i); }, playlist() {} } });
  const { qdl: q } = H.loadQdl({ lampa });
  q.rawPlay('HASH', 0, 'Title', null, 'Movie.2020.mkv');
  assert.strictEqual(played[0].timeline.hash, q.epTimelineHash('HASH', 'Movie.2020.mkv'));
  // fileName нет → фолбэк на title, как раньше
  q.rawPlay('HASH', 0, 'Title', null);
  assert.strictEqual(played[1].timeline.hash, q.epTimelineHash('HASH', 'Title'));
});

test('rawPlay с файлом (есть tl) → стабильный tl-ключ, как у плейлиста', () => {
  const played = [];
  const lampa = H.makeLampa({ Player: { play(i) { played.push(i); }, playlist() {} } });
  const { qdl: q } = H.loadQdl({ lampa });
  q.rawPlay('HASH', 0, 'Ep1', null, 'Ep1.mkv', { name: 'Ep1.mkv', tl: 'show:s1e1' });
  assert.strictEqual(played[0].timeline.hash, lampa.Utils.hash('qdltl:show:s1e1'));
});
