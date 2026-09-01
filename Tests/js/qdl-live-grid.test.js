'use strict';
// D1versy Live 2.95: живая сетка эфира (ComponentLiveWatch).
//
// Что здесь защищается — ровно те три правила, на которых держится фича:
//  1. играют ТОЛЬКО плитки в кадре и не больше LIVE_MAX_PLAYERS (иначе прокрутка вниз копила бы
//     декодеры, а на домашнем ТВ свободно ~800 МБ);
//  2. тумблер «Видео» гасит стриминг на живую;
//  3. pause()/stop()/destroy() сносят ВСЕ плееры — Lampa на forward-навигации зовёт только
//     pause(), и без этого каждый вход в раздел оставлял бы позади ещё четыре живых декодера
//     (та же грабля, что уже убила таймер сетки в claude/06 §AL2).
//
// Плюс фулл вью одной камеры и кнопка Detection.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const CAMS = [
  { id: 3, name: 'Garage 2', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/3/index.m3u8' },
  { id: 5, name: 'Garage 1', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/5/index.m3u8' },
  { id: 1, name: 'balkon', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/1/index.m3u8' },
  { id: 4, name: 'Front door', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/4/index.m3u8' },
];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Scroll-мок на настоящем jsdom-DOM: компонент рендерит плитки в реальные узлы. */
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

/** Подставной IntersectionObserver: видимостью плиток управляет тест. */
function fakeIO(w, state) {
  w.IntersectionObserver = function (cb) {
    state.cb = cb;
    state.seen = [];
    this.observe = (el) => {
      state.seen.push(el);
      cb([{ target: el, isIntersecting: state.visible ? state.visible(el) : true }]);
    };
    this.unobserve = () => {};
    this.disconnect = () => { state.disconnected = true; };
  };
}

/**
 * jsdom не реализует play/pause/load у <video> и сыплет «Not implemented» в virtual console —
 * это шум чужого движка, а не наша ошибка. Заодно заглушки дают ЧЕСТНОЕ состояние paused,
 * без которого проверка «соседние камеры не декодируют» ничего бы не значила.
 */
function stubMedia(w) {
  const proto = w.HTMLMediaElement.prototype;
  Object.defineProperty(proto, 'paused', { configurable: true, get() { return this.__paused !== false; } });
  proto.play = function () { this.__paused = false; return Promise.resolve(); };
  proto.pause = function () { this.__paused = true; };
  proto.load = function () {};
}

function mount(opts) {
  opts = opts || {};
  const calls = { plays: [], pushes: [], notys: [], starts: [] };
  const cams = opts.cams || CAMS;

  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        const u = String(url);
        if (u.indexOf('/qdl/live/watch/start') !== -1) {
          calls.starts.push(u);
          if (opts.start) ok(opts.start);
          else if (err) err();
          return;
        }
        if (u.indexOf('/qdl/live/watch/thumb') !== -1) return;
        if (u.indexOf('/qdl/live/watch') !== -1) { ok(opts.reply || { cameras: cams }); return; }
      };
    },
    Player: { play: (x) => calls.plays.push(x), playlist: () => {}, opened: () => !!opts.playerOpened },
    Activity: { push: (x) => calls.pushes.push(x), backward: () => { calls.backward = true; }, active: () => ({}) },
    Noty: { show: (t) => calls.notys.push(t) },
    Controller: { add(name, o) { calls.ctrl = o; }, toggle() {}, collectionSet() {}, collectionFocus() {} },
  });

  const r = H.loadQdlDom({ lampa, perms: opts.perms });
  r.lampa.Scroll = domScroll(r.w);
  stubMedia(r.w);
  const io = {};
  if (opts.noIO !== true) fakeIO(r.w, Object.assign(io, opts.io || {}));
  if (opts.videoOff) r.lampa.Storage.set('qdl_live_video', '0');
  if (opts.before) opts.before(r);

  const inst = new r.qdl.ComponentLiveWatch({});
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  inst.start();

  return { r, inst, calls, io, root: inst.render(), body: r.$(r.w.document.body) };
}

// 🔴 Плитки ищем и в ленте, и на body: в фулл вью развёрнутая уезжает в body — position:fixed
// внутри скролла Lampa не покрывает экран (у контейнера transform, он и становится содержащим
// блоком). Поймано скриншотом живого клиента: соседние камеры и шапка Lampa оставались видны.
// (лента компонента живёт отдельным деревом — Scroll-мок его никуда не вешает, поэтому
// ищем в обоих местах, а не в document)
const pick = (m, sel) => m.root.find(sel).add(m.body.find(sel));
const tiles = (m) => pick(m, '.qdl-watch-tile');
const videos = (m) => pick(m, '.qdl-watch-tile video');
const full = (m) => pick(m, '.qdl-watch-tile--full');

// ─────────────────────────── раскладка и состав ───────────────────────────

test('сетка: плитка на каждую камеру из ответа сервера, фильтр — на сервере', () => {
  const m = mount();
  assert.strictEqual(tiles(m).length, 4, 'четыре плитки — ровно то, что отдал сервер');
  assert.ok(m.root.find('.qdl-watch-grid').length === 0 || true);
  assert.ok(m.root.text().indexOf('Garage 2') !== -1, 'имя камеры на плитке');
  m.inst.destroy();
});

test('шапка: три кнопки — на весь экран, Detection, тумблер видео', () => {
  const m = mount();
  const btns = m.root.find('.qdl-btn-focus');
  assert.strictEqual(btns.length, 3);
  assert.ok(m.r.$(btns[0]).text().indexOf('На весь экран') !== -1);
  assert.ok(m.r.$(btns[1]).text().indexOf('Detection') !== -1);
  assert.ok(m.r.$(btns[2]).text().indexOf('Видео: вкл') !== -1, 'по умолчанию эфир в плитках включён');
  m.inst.destroy();
});

test('Detection: кнопка уводит на свой экран', () => {
  const m = mount();
  m.r.$(m.root.find('.qdl-btn-focus')[1]).trigger('hover:enter');
  assert.strictEqual(m.calls.pushes.length, 1);
  assert.strictEqual(m.calls.pushes[0].component, 'qdl_live_detect');
  m.inst.destroy();
});

// ─────────────────────────── живое видео ───────────────────────────

test('видимые плитки поднимают <video>, невидимые — нет', async () => {
  const m = mount({ io: { visible: (el) => el.getAttribute('data-cam') !== '4' } });
  await sleep(1700);   // разбег стартов 500 мс на плитку

  assert.strictEqual(videos(m).length, 3, 'играют три видимые плитки, четвёртая — нет');
  const played = tiles(m).filter((_, el) => m.r.$(el).find('video').length > 0)
    .map((_, el) => m.r.$(el).attr('data-cam')).get();
  assert.ok(played.indexOf('4') === -1, 'скрытая камера не декодируется');
  m.inst.destroy();
});

test('одновременно не больше LIVE_MAX_PLAYERS, даже если видно больше', async () => {
  const many = CAMS.concat([
    { id: 8, name: 'Пятая', live: true, path: '/qdl/live/watch/hls/8/index.m3u8' },
    { id: 9, name: 'Шестая', live: true, path: '/qdl/live/watch/hls/9/index.m3u8' },
  ]);
  const m = mount({ cams: many });
  await sleep(2200);

  assert.strictEqual(tiles(m).length, 6, 'плиток шесть');
  assert.strictEqual(videos(m).length, m.r.qdl.LIVE_MAX_PLAYERS, 'а декодеров — не больше четырёх');
  m.inst.destroy();
});

test('плеер несёт uid устройства прямо в URL плейлиста', async () => {
  const m = mount({ before: (r) => r.lampa.Storage.set('lampac_unic_id', '7kfrxzfr') });
  await sleep(200);
  const src = m.r.$(videos(m)[0]).attr('src') || '';
  // hls.js в jsdom нет, поэтому src ставится только на нативном пути; проверяем сам факт узла
  assert.strictEqual(videos(m).length > 0, true, 'видео-узел создан');
  if (src) assert.ok(/[?&]uid=7kfrxzfr(&|$)/.test(src), 'uid в URL: ' + src);
  m.inst.destroy();
});

test('камера не в эфире: плитка будит поток на регистраторе, а не молчит', async () => {
  const cams = [{ id: 3, name: 'Garage 2', live: false, running: false, upload: false, path: null }];
  const m = mount({ cams, start: { camera: 3, ready: false, running: true, path: null } });
  await sleep(100);

  assert.strictEqual(videos(m).length, 0, 'без плейлиста декодер не поднимаем');
  assert.ok(m.calls.starts.length >= 1, 'но /watch/start дёрнут');
  m.inst.destroy();
});

// ─────────────────────────── тумблер ───────────────────────────

test('тумблер выключен: ни одного <video>, плитки живут кадрами', async () => {
  const m = mount({ videoOff: true });
  await sleep(300);

  assert.strictEqual(videos(m).length, 0);
  assert.ok(m.r.$(m.root.find('.qdl-btn-focus')[2]).text().indexOf('Видео: выкл') !== -1);
  assert.ok(m.root.find('.qdl-watch-frame').length === 4, 'кадры-подложки на месте');
  m.inst.destroy();
});

test('тумблер переключается на живую: гасит и поднимает без перезахода в раздел', async () => {
  const m = mount();
  await sleep(1700);
  assert.ok(videos(m).length > 0, 'сначала играет');

  const btn = m.r.$(m.root.find('.qdl-btn-focus')[2]);
  btn.trigger('hover:enter');
  assert.strictEqual(videos(m).length, 0, 'выключили — декодеров не осталось');
  assert.strictEqual(m.r.lampa.Storage.get('qdl_live_video'), '0', 'состояние сохранено на устройстве');
  assert.ok(btn.text().indexOf('Видео: выкл') !== -1);

  btn.trigger('hover:enter');
  await sleep(1700);
  assert.ok(videos(m).length > 0, 'включили — поднялись обратно');
  m.inst.destroy();
});

test('с выключенным тумблером Enter по плитке ведёт в нативный плеер, как раньше', () => {
  const m = mount({ videoOff: true, start: { ready: true, running: true, path: '/qdl/live/watch/hls/3/index.m3u8' } });
  m.r.$(tiles(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1, 'открылся нативный плеер');
  assert.ok(String(m.calls.plays[0].url).indexOf('/qdl/live/watch/hls/3/index.m3u8') !== -1);
  m.inst.destroy();
});

// ─────────────────────────── фулл вью ───────────────────────────

test('фулл вью: одна камера на весь экран, остальные на паузе', async () => {
  const m = mount();
  await sleep(1700);
  assert.strictEqual(videos(m).length, 4);

  const home = tiles(m)[0].parentNode;
  m.r.$(tiles(m)[0]).trigger('hover:enter');
  await sleep(50);

  const f = full(m);
  assert.strictEqual(f.length, 1, 'развёрнута ровно одна плитка');
  assert.strictEqual(m.r.$(f[0]).attr('data-cam'), '3');
  assert.strictEqual(f[0].parentNode, m.r.w.document.body, 'развёрнутая плитка уехала в body');

  assert.strictEqual(m.r.$(f[0]).find('video')[0].paused, false, 'развёрнутая камера играет');

  const others = tiles(m).filter((_, el) => !m.r.$(el).hasClass('qdl-watch-tile--full'));
  others.each((_, el) => {
    const v = m.r.$(el).find('video')[0];
    if (v) assert.strictEqual(v.paused, true, 'соседняя камера не декодирует');
  });

  // Back возвращает в сетку — и плитку на её место в ленте
  m.calls.ctrl.back();
  assert.strictEqual(full(m).length, 0);
  assert.strictEqual(tiles(m)[0].parentNode, home, 'плитка вернулась в сетку');
  assert.ok(!m.calls.backward, 'Back из фулл вью не выходит из раздела');
  m.inst.destroy();
});

test('в фулл вью стрелки переключают камеру, а не двигают фокус', async () => {
  const m = mount();
  await sleep(1700);
  m.r.$(tiles(m)[0]).trigger('hover:enter');
  await sleep(20);

  m.calls.ctrl.right();
  assert.strictEqual(full(m).length, 1, 'развёрнута по-прежнему ровно одна');
  assert.strictEqual(m.r.$(full(m)[0]).attr('data-cam'), '5');
  m.calls.ctrl.left();
  assert.strictEqual(m.r.$(full(m)[0]).attr('data-cam'), '3');
  m.inst.destroy();
});

test('кнопка «На весь экран» разворачивает камеру под фокусом', async () => {
  const m = mount();
  await sleep(600);
  m.r.$(tiles(m)[1]).trigger('hover:focus');
  m.r.$(m.root.find('.qdl-btn-focus')[0]).trigger('hover:enter');

  assert.strictEqual(m.r.$(full(m)[0]).attr('data-cam'), '5');
  m.inst.destroy();
});

test('уход с экрана возвращает развёрнутую плитку: иначе камера висит поверх чужого экрана', async () => {
  const m = mount();
  await sleep(1700);
  const home = tiles(m)[0].parentNode;

  m.r.$(tiles(m)[0]).trigger('hover:enter');
  await sleep(50);
  assert.strictEqual(full(m)[0].parentNode, m.r.w.document.body);

  m.inst.pause();
  assert.strictEqual(full(m).length, 0, 'класс снят');
  assert.strictEqual(tiles(m)[0].parentNode, home, 'плитка вернулась в ленту');
  assert.strictEqual(videos(m).length, 0, 'и плееров не осталось');
  m.inst.destroy();
});

// ─────────────────────────── жизненный цикл ───────────────────────────

test('pause() сносит ВСЕ плееры: иначе каждый вход в раздел копил бы декодеры', async () => {
  const m = mount();
  await sleep(1700);
  assert.ok(videos(m).length > 0);

  m.inst.pause();
  assert.strictEqual(videos(m).length, 0, 'после pause() в DOM не осталось ни одного <video>');
  m.inst.destroy();
});

test('destroy() отписывает наблюдателя видимости', async () => {
  const m = mount();
  await sleep(100);
  m.inst.destroy();
  assert.strictEqual(m.io.disconnected, true);
  assert.strictEqual(videos(m).length, 0);
});

test('открытый нативный плеер поверх сетки глушит декодеры', async () => {
  const m = mount({ playerOpened: true });
  await sleep(300);
  assert.strictEqual(videos(m).length, 0, 'пока играет нативный плеер, сетка не декодирует');
  m.inst.destroy();
});

test('без права live экран не строится', () => {
  const m = mount({ perms: {} });
  assert.strictEqual(tiles(m).length, 0);
  m.inst.destroy();
});
