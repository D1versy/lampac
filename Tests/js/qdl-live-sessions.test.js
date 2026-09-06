'use strict';
// qdl 2.113: склеенные сессии upload-камер (Mac-рекордер) в D1versy Rec.
//
// Регистратор через ~5 мин после конца сессии склеивает её чанки в ОДИН mp4 под id первого чанка
// (compression_preset=merged), остальные id исчезают (404). Сервер для такого дня отдаёт
// mode:'sessions' — одна строка = один файл, играть напрямую /qdl/live/stream?id= (moov в начале,
// перемотка мгновенная), а сшитый дневной HLS не собирать и ремукс не будить.
//
// Что держат тесты:
//  • livePlayDay на ответ mode:'sessions' сразу играет mp4-сессии, а не HLS-плейлист дня;
//  • liveWarmDay для sessions-камеры регистратор не трогает;
//  • экран камер: вход в sessions-камеру открывает СПИСОК сессий, а не плеер;
//  • экран камеры в sessions: нет кнопки «весь день одной записью», строка = сессия,
//    постер и плеер — по id строки (единственный уцелевший id);
//  • RTSP-камеры (mode:'day') ведут себя как раньше.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const CAM = { id: 6, name: 'Vlad-MacBook-Recorder', mode: 'sessions', upload: true };
const RTSP = { id: 3, name: 'Garage 2', mode: 'day', upload: false };

// Боевой день: камера 6 за 2026-09-06 — одна склеенная запись id=96 (ids 97, 99, 101, 103 удалены).
const SESSION = { id: 96, start: '13:27', end: '14:39', seconds: 4319, size: 2229571692, trigger: 'continuous', preset: 'merged', merged: true };
const SESSIONS_DAY = {
  date: '2026-09-06', label: 'Сегодня', camera: { id: 6, name: 'Vlad-MacBook-Recorder', upload: true },
  mode: 'sessions', path: '/qdl/live/day/6/2026-09-06/stream.m3u8',
  ready: 1, total: 1, complete: true, seconds: 4319, items: [SESSION],
};

/** Плагин с подменённым сервером: ответы по подстроке URL. */
function boot(replies, opts) {
  opts = opts || {};
  const played = [];
  const asked = [];
  const pushes = [];
  const timers = [];
  let now = 1000000;

  const lampa = H.makeLampa({
    Player: { play: (item) => played.push(item), playlist: () => {} },
    Timeline: { view: () => null, update: () => {} },
    Noty: { show: () => {} },
    Activity: { push: (x) => pushes.push(x), backward: () => {}, active: () => ({}) },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        asked.push(url);
        for (const k of Object.keys(replies)) {
          if (url.indexOf(k) !== -1) { ok(replies[k]); return; }
        }
        if (err) err();
      };
    },
  });

  const ctx = H.loadQdl({
    lampa,
    setTimeout: (fn, ms) => timers.push({ fn, at: now + (ms || 0) }),
    clearTimeout: (id) => { if (timers[id - 1]) timers[id - 1].dead = true; },
  });

  function tick(ms) {
    const until = now + ms;
    for (;;) {
      const due = timers.filter((t) => !t.dead && !t.done && t.at <= until).sort((a, b) => a.at - b.at)[0];
      if (!due) break;
      now = Math.max(now, due.at);
      due.done = true;
      due.fn();
    }
    now = until;
  }

  return Object.assign(ctx, { played, asked, pushes, tick, lampa });
}

test('liveSpan: сессия через полночь подписывается относительно показанного дня', () => {
  const { qdl } = boot({});
  assert.strictEqual(qdl.liveSpan({ start: '13:27', end: '14:39' }), '13:27 – 14:39');
  assert.strictEqual(qdl.liveSpan({ start: '23:00', end: '04:00', nextDay: true }), '23:00 – 04:00 (+1 день)');
  assert.strictEqual(qdl.liveSpan({ start: '23:00', end: '04:00', prevDay: true }), 'вчера 23:00 – 04:00');
});

test('liveSessions: режим читается только из mode', () => {
  const { qdl } = boot({});
  assert.strictEqual(qdl.liveSessions({ mode: 'sessions' }), true);
  assert.strictEqual(qdl.liveSessions({ mode: 'day', upload: true }), false, 'upload с неслитыми чанками — прежний день');
  assert.strictEqual(qdl.liveSessions(null), false);
});

test('livePlayDay: ответ mode=sessions → сразу играем mp4-сессии напрямую, а не HLS дня', () => {
  const { qdl, played, tick } = boot({ '/qdl/live/day': SESSIONS_DAY });

  qdl.livePlayDay(CAM, '2026-09-06', 'Сегодня');
  tick(10);

  assert.strictEqual(played.length, 1);
  assert.ok(played[0].url.indexOf('/qdl/live/stream?id=96') > 0, played[0].url);
  assert.strictEqual(played[0].url.indexOf('stream.m3u8'), -1, 'path дневного HLS не используется');
  assert.ok(played[0].title.indexOf('13:27 – 14:39') === 0, played[0].title);
});

test('livePlayDay: sessions без items — сообщение, плеер не открывается', () => {
  const { qdl, played, tick } = boot({ '/qdl/live/day': Object.assign({}, SESSIONS_DAY, { items: [] }) });

  qdl.livePlayDay(CAM, '2026-09-06', 'Сегодня');
  tick(30000);

  assert.strictEqual(played.length, 0);
});

test('livePlayDay: RTSP-камера (mode=day) играет плейлист дня как раньше', () => {
  const day = { date: '2026-09-06', mode: 'day', camera: { id: 3, name: 'Garage 2' }, path: '/qdl/live/day/3/2026-09-06/stream.m3u8', ready: 8, total: 8, complete: true, seconds: 7552 };
  const { qdl, played, tick } = boot({ '/qdl/live/day': day });

  qdl.livePlayDay(RTSP, '2026-09-06', 'Сегодня');
  tick(10);

  assert.strictEqual(played.length, 1);
  assert.ok(played[0].url.indexOf('/qdl/live/day/3/2026-09-06/stream.m3u8') > 0, played[0].url);
});

test('liveWarmDay: sessions-камеру не будим — ремукс многочасового mp4 на регистраторе никому не нужен', () => {
  const { qdl, asked, tick } = boot({ '/qdl/live/day': SESSIONS_DAY });

  qdl.liveWarmDay(CAM, '2026-09-06');
  tick(5000);
  assert.strictEqual(asked.filter((u) => u.indexOf('/qdl/live/day') !== -1).length, 0);

  qdl.liveWarmDay(RTSP, '2026-09-06');
  tick(5000);
  assert.strictEqual(asked.filter((u) => u.indexOf('/qdl/live/day') !== -1).length, 1, 'RTSP-камера будится как раньше');
});

// ── экраны на настоящем DOM ──

function domScroll(w) {
  return function () {
    const root = w.document.createElement('div');
    const bodyEl = w.document.createElement('div');
    root.appendChild(bodyEl);
    this.render = () => w.$(root);
    this.body = () => w.$(bodyEl);
    this.append = (x) => { w.$(bodyEl).append(x); };
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

function mount(name, object, replies) {
  const calls = { plays: [], pushes: [], urls: [] };
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        const u = String(url);
        calls.urls.push(u);
        for (const k of Object.keys(replies)) {
          if (u.indexOf(k) !== -1) { ok(replies[k]); return; }
        }
        if (err) err();
      };
    },
    Player: { play: (x) => calls.plays.push(x), playlist: () => {} },
    Activity: { push: (x) => calls.pushes.push(x), backward: () => {}, active: () => ({}) },
    Select: { show: () => {}, listener: { follow() {}, send() {} } },
    Noty: { show: () => {} },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {}, own: () => true, collectionAppend() {} },
    Layer: { visible() {}, update() {} },
  });

  const r = H.loadQdlDom({ lampa });
  r.lampa.Scroll = domScroll(r.w);

  const inst = new r.qdl[name](object);
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  return { r, inst, calls, root: inst.render() };
}

const rows = (m) => m.root.find('.qdl-row-focus');

const CAMERAS_DAY = {
  date: '2026-09-06', label: 'Сегодня', today: '2026-09-06', total: 6,
  cameras: [
    { id: 6, name: 'Vlad-MacBook-Recorder', count: 1, first: '13:27', last: '14:39', seconds: 4319, thumb: 96, upload: true, mode: 'sessions' },
    { id: 3, name: 'Garage 2', count: 8, first: '00:00', last: '23:59', seconds: 86000, thumb: 700, upload: false, mode: 'day' },
  ],
};

test('экран камер: вход в sessions-камеру открывает список сессий, а не плеер', () => {
  const m = mount('ComponentLive', {}, { '/qdl/live/cameras': CAMERAS_DAY });
  const list = rows(m);
  assert.strictEqual(list.length, 2);

  m.r.$(list[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 0, 'плеер не открыт');
  assert.strictEqual(m.calls.pushes.length, 1);
  assert.strictEqual(m.calls.pushes[0].component, 'qdl_live_camera');
  assert.strictEqual(m.calls.pushes[0].qdl_camera.mode, 'sessions', 'режим едет в экран камеры вместе с камерой');
  assert.strictEqual(m.calls.pushes[0].qdl_date, '2026-09-06');

  assert.ok(m.r.$(list[0]).text().indexOf('1 сессия') !== -1, 'подпись строки — сессии: ' + m.r.$(list[0]).text());
  assert.ok(m.r.$(list[0]).find('img').attr('src').indexOf('/qdl/live/thumb?id=96') !== -1, 'постер — уцелевший id');
  m.inst.destroy();
});

test('экран камер: RTSP-камера по входу идёт в готовку дня, как раньше', () => {
  const m = mount('ComponentLive', {}, { '/qdl/live/cameras': CAMERAS_DAY, '/qdl/live/day': { date: '2026-09-06', mode: 'day', path: '/qdl/live/day/3/2026-09-06/stream.m3u8', ready: 8, total: 8, complete: true, seconds: 86000 } });
  m.r.$(rows(m)[1]).trigger('hover:enter');

  assert.strictEqual(m.calls.pushes.length, 0);
  assert.ok(m.calls.urls.some((u) => u.indexOf('/qdl/live/day?camera=3') !== -1), 'запрошен день камеры 3');
  m.inst.destroy();
});

const RECORDINGS_SESSIONS = {
  date: '2026-09-06', label: 'Сегодня', camera: { id: 6, name: 'Vlad-MacBook-Recorder', upload: true },
  mode: 'sessions',
  items: [SESSION, { id: 80, start: 'вчера 23:00', end: '01:30', seconds: 9000, size: 1, trigger: 'continuous', preset: 'merged', merged: true, prevDay: true }],
};

test('экран камеры (sessions): нет кнопки «весь день одной записью», строка = файл, играет mp4 по id строки', () => {
  const m = mount('ComponentLiveCamera', { qdl_camera: CAM, qdl_date: '2026-09-06' }, { '/qdl/live/recordings': RECORDINGS_SESSIONS });
  const text = m.root.text();

  assert.strictEqual(text.indexOf('Весь день одной записью'), -1, 'дневного HLS в sessions нет');
  assert.strictEqual(text.indexOf('Фрагменты подряд'), -1);
  assert.ok(text.indexOf('Все сессии подряд') !== -1, 'кнопка «все сессии подряд»');
  assert.ok(text.indexOf('2 сессии') !== -1, 'заголовок считает сессии: ' + text);

  const list = rows(m);
  assert.strictEqual(list.length, 2, 'одна строка = одна сессия');
  assert.ok(m.r.$(list[0]).find('img').attr('src').indexOf('/qdl/live/thumb?id=96') !== -1);
  assert.ok(m.r.$(list[0]).text().indexOf('13:27 – 14:39') !== -1);

  m.r.$(list[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1);
  assert.ok(m.calls.plays[0].url.indexOf('/qdl/live/stream?id=96') !== -1, m.calls.plays[0].url);
  assert.ok(!m.calls.urls.some((u) => u.indexOf('/qdl/live/day') !== -1), 'сервер за днём не спрашивали');
  m.inst.destroy();
});

test('экран камеры (sessions): «все сессии подряд» стартует с первой, ни одного запроса за HLS', () => {
  const m = mount('ComponentLiveCamera', { qdl_camera: CAM, qdl_date: '2026-09-06' }, { '/qdl/live/recordings': RECORDINGS_SESSIONS });
  m.root.find('.qdl-btn-green').trigger('hover:enter');

  assert.strictEqual(m.calls.plays.length, 1);
  assert.ok(m.calls.plays[0].url.indexOf('/qdl/live/stream?id=96') !== -1);
  assert.ok(!m.calls.urls.some((u) => u.indexOf('/qdl/live/day') !== -1));
  m.inst.destroy();
});

test('экран камеры (day): прежние две кнопки — регресса у RTSP нет', () => {
  const day = {
    date: '2026-09-06', label: 'Сегодня', camera: { id: 3, name: 'Garage 2', upload: false }, mode: 'day',
    items: [{ id: 700, start: '10:00', end: '10:16', seconds: 1000, size: 1, trigger: 'continuous', preset: 'original', merged: false }],
  };
  const m = mount('ComponentLiveCamera', { qdl_camera: RTSP, qdl_date: '2026-09-06' }, { '/qdl/live/recordings': day });
  const text = m.root.text();

  assert.ok(text.indexOf('Весь день одной записью') !== -1);
  assert.ok(text.indexOf('Фрагменты подряд') !== -1);
  assert.ok(text.indexOf('1 запись') !== -1, text);
  m.inst.destroy();
});
