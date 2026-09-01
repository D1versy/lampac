'use strict';
// D1versy Live 2.96: экран эфира — квадрат живых камер, блок «не в эфире» и одна кнопка сверху.
//
// Что здесь защищается:
//  1. деление на секции: эфирные — в квадрат, остальные отдельным блоком СНИЗУ (возврат
//     поведения до 2.95 по требованию владельца);
//  2. квадрат влезает в экран — ширину колонки считает fitQuad(), а не CSS;
//  3. «вверх» с ОБЕИХ верхних плиток уводит на Detection (кнопка одна и стоит по центру,
//     геометрический Navigator туда попадает в лучшем случае с одной);
//  4. тумблер эфира ГЛОБАЛЬНЫЙ: значение приезжает с сервера в card.live.video, а не из
//     Lampa.Storage — «включил у себя, применилось всем»;
//  5. играют только плитки в кадре и не больше LIVE_MAX_PLAYERS;
//  6. pause()/stop()/destroy() сносят ВСЕ плееры (Lampa на forward-навигации зовёт только
//     pause() — без этого каждый вход в раздел копил бы декодеры, грабля §AL2).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const LIVE = [
  { id: 3, name: 'Garage 2', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/3/index.m3u8' },
  { id: 5, name: 'Garage 1', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/5/index.m3u8' },
  { id: 1, name: 'balkon', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/1/index.m3u8' },
  { id: 4, name: 'Front door', live: true, running: true, upload: false, path: '/qdl/live/watch/hls/4/index.m3u8' },
];
// Боевой состав: два mac-рекордера, которые почти всегда не пушат.
const OFF = [
  { id: 6, name: 'Vlad-MacBook-Recorder', live: false, running: false, upload: true, path: null },
  { id: 7, name: 'Vlad-MacBook-Recorder #2', live: false, running: false, upload: true, path: null },
];
const CAMS = LIVE.concat(OFF);

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
    this.observe = (el) => cb([{ target: el, isIntersecting: state.visible ? state.visible(el) : true }]);
    this.unobserve = () => {};
    this.disconnect = () => { state.disconnected = true; };
  };
}

/**
 * jsdom не реализует play/pause/load у <video> и сыплет «Not implemented» в virtual console —
 * шум чужого движка. Заглушки заодно дают ЧЕСТНОЕ состояние paused, без которого проверка
 * «соседние камеры не декодируют» ничего бы не значила.
 */
function stubMedia(w) {
  const proto = w.HTMLMediaElement.prototype;
  Object.defineProperty(proto, 'paused', { configurable: true, get() { return this.__paused !== false; } });
  // currentTime/readyState в jsdom не реализованы вовсе — а сторож эфира смотрит ИМЕННО на них:
  // «висит» это не отсутствие <video>, а картинка, которая не движется. Управляем ими из теста.
  Object.defineProperty(proto, 'currentTime', {
    configurable: true, get() { return this.__t || 0; }, set(v) { this.__t = v; },
  });
  Object.defineProperty(proto, 'readyState', { configurable: true, get() { return this.__rs || 0; } });
  proto.play = function () { this.__paused = false; return Promise.resolve(); };
  proto.pause = function () { this.__paused = true; };
  proto.load = function () {};
}

/** Живой эфир: у каждого играющего <video> есть декодированный кадр и время идёт вперёд. */
function flow(m, sec) {
  videos(m).each((_, v) => { if (!v.paused) { v.__rs = 4; v.__t = (v.__t || 0) + (sec || 1); } });
}

/** В jsdom вся геометрия нулевая, а fitQuad считает по ней — подставляем измеримый экран. */
function stubGrid(m, opts) {
  opts = opts || {};
  const top = opts.top || 120;
  const width = opts.width || 1200;
  const g = m.root.find('.qdl-watch-grid')[0];
  Object.defineProperty(g, 'clientWidth', { configurable: true, value: width });
  g.getBoundingClientRect = () => ({ top, left: 0, right: width, bottom: top + 400, width, height: 400 });
  return g;
}

function mount(opts) {
  opts = opts || {};
  const calls = { plays: [], pushes: [], notys: [], starts: [], focus: [] };
  const cams = opts.cams || CAMS;

  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        const u = String(url);
        if (u.indexOf('/qdl/live/watch/start') !== -1) {
          calls.starts.push(u);
          if (opts.start) ok(opts.start); else if (err) err();
          return;
        }
        if (u.indexOf('/qdl/live/watch/thumb') !== -1) return;
        if (u.indexOf('/qdl/live/watch') !== -1) { ok(opts.reply || { cameras: cams }); return; }
      };
    },
    Player: { play: (x) => calls.plays.push(x), playlist: () => {}, opened: () => !!opts.playerOpened },
    Activity: { push: (x) => calls.pushes.push(x), backward: () => { calls.backward = true; }, active: () => ({}) },
    Noty: { show: (t) => calls.notys.push(t) },
    Controller: {
      add(name, o) { calls.ctrl = o; },
      toggle(name) { calls.toggled = name; },
      collectionSet() {},
      collectionFocus(el) { calls.focus.push(el); },
    },
  });

  const r = H.loadQdlDom({ lampa, perms: opts.perms });
  r.lampa.Scroll = domScroll(r.w);
  stubMedia(r.w);
  // Navigator живёт в бандле Lampa, в jsdom его нет: без заглушки обычная ветка «вверх»
  // падает с TypeError, и тест про прыжок на Detection проверял бы только половину случая.
  r.w.Navigator = { canmove: () => false, move: () => {}, focused: (el) => { calls.marked = el; } };
  const io = {};
  if (opts.noIO !== true) fakeIO(r.w, Object.assign(io, opts.io || {}));
  // Глобальная настройка эфира приезжает с сервера ключом live.video в /qdl/features.
  if (opts.card !== null) r.qdl.setCard(opts.card || { live: { video: true } });
  // Сторож фулл вью считает секундами — тесту столько ждать нельзя, ускоряем его пороги.
  if (opts.guard) r.qdl.setLiveGuard(opts.guard);
  if (opts.before) opts.before(r);

  const inst = new r.qdl.ComponentLiveWatch({});
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  inst.start();

  return { r, inst, calls, io, root: inst.render(), body: r.$(r.w.document.body) };
}

// Лента компонента живёт отдельным деревом (Scroll-мок его никуда не вешает), а развёрнутая
// в фулл вью плитка уезжает в body — ищем в обоих местах.
const pick = (m, sel) => m.root.find(sel).add(m.body.find(sel));
const tiles = (m) => pick(m, '.qdl-watch-tile');
const quad = (m) => pick(m, '.qdl-watch-grid .qdl-watch-tile');
const offTiles = (m) => pick(m, '.qdl-watch-off .qdl-watch-tile');
const videos = (m) => pick(m, '.qdl-watch-tile video');
const full = (m) => pick(m, '.qdl-watch-tile--full');
const btns = (m) => m.root.find('.qdl-btn-focus');

// ─────────────────────────── состав и раскладка ───────────────────────────

test('эфирные камеры — в квадрат, неактивные — отдельным блоком снизу', () => {
  const m = mount();
  assert.strictEqual(quad(m).length, 4, 'четыре живых в квадрате');
  assert.strictEqual(offTiles(m).length, 2, 'два mac-рекордера в блоке «не в эфире»');
  assert.strictEqual(tiles(m).length, 6, 'и никто не потерялся');
  assert.strictEqual(m.root.find('.qdl-watch-offtitle').length, 1, 'у нижнего блока есть подпись');
  m.inst.destroy();
});

test('нижнего блока нет, когда все камеры в эфире', () => {
  const m = mount({ cams: LIVE });
  assert.strictEqual(quad(m).length, 4);
  assert.strictEqual(m.root.find('.qdl-watch-offtitle').length, 0);
  m.inst.destroy();
});

test('в теле экрана кнопок нет вовсе — панель начинается сразу с камер', () => {
  const m = mount();
  assert.strictEqual(btns(m).length, 0, 'Detection уехал в шапку Lampa, «На весь экран» убрана');
  assert.strictEqual(m.root.find('.qdl-watch-head').length, 0);
  m.inst.destroy();
});

// ── Detection в шапке Lampa ───────────────────────────────────────────────
// Владелец: «detection в хедере». Иконка живёт рядом с названием раздела и видна только
// на экранах эфира/детекций — тот же приём, что у кнопки автопилота jut.su.

function headMount(opts) {
  opts = opts || {};
  const pushes = [];
  const lampa = H.makeLampa({
    Activity: { push: (x) => pushes.push(x), active: () => ({ component: opts.component || 'qdl_live_watch' }) },
  });
  const r = H.loadQdlDom({
    lampa,
    perms: opts.perms,
    bodyHtml: '<div class="head"><div class="head__title">D1versy Live</div><div class="head__actions"></div></div>',
  });
  r.qdl.ensureLiveDetectBtn();
  return { r, pushes, btn: r.$('.qdl-det-btn') };
}

test('шапка: иконка Detection встаёт в ряд значков', () => {
  const m = headMount();
  assert.strictEqual(m.btn.length, 1, 'иконка вставлена ровно одна');
  assert.strictEqual(m.btn.find('svg').length, 1, 'это svg-иконка, а не эмодзи');
  assert.ok(m.btn.hasClass('head__action'), 'оформлена как штатный значок шапки');
  // 🔴 Ряд значков, а не «за названием»: вставленная после .head__title кнопка ложится
  // ПОД «назад» в левом верхнем углу (замер живого клиента: box [29,0,55,55]).
  assert.strictEqual(m.btn.parent().attr('class'), 'head__actions', 'кнопка в ряду значков шапки');
  assert.notStrictEqual(m.btn.css('display'), 'none', 'на экране эфира видна');
});

test('шапка: повторный вызов не плодит копии', () => {
  const m = headMount();
  m.r.qdl.ensureLiveDetectBtn();
  m.r.qdl.ensureLiveDetectBtn();
  assert.strictEqual(m.r.$('.qdl-det-btn').length, 1);
});

test('шапка: иконка нажимается и открывает ленту детекций', () => {
  const m = headMount();
  m.btn.trigger('hover:enter');
  assert.strictEqual(m.pushes.length, 1);
  assert.strictEqual(m.pushes[0].component, 'qdl_live_detect');
});

test('шапка: на чужом экране иконка спрятана, а не висит везде', () => {
  const m = headMount({ component: 'qdl_downloads' });
  assert.strictEqual(m.btn.css('display'), 'none');
});

test('шапка: без права «эфир» иконки не видно', () => {
  const m = headMount({ perms: {} });
  assert.strictEqual(m.btn.css('display'), 'none');
});

test('панель занимает весь экран, а плитка остаётся 16:9 — кадр не обрезается', () => {
  const m = mount();
  const g = stubGrid(m, { top: 120, width: 1200 });
  m.inst.start();                       // start() пересчитывает раскладку

  const h = Number(/(\d+)px/.exec(g.style.gridAutoRows)[1]);
  const w = Number(/repeat\(2, (\d+)px\)/.exec(g.style.gridTemplateColumns)[1]);
  assert.ok(h > 0 && w > 0, 'раскладка посчитана: ' + g.style.gridTemplateColumns + ' / ' + g.style.gridAutoRows);
  assert.ok(m.r.$(g).hasClass('qdl-watch-grid--fit'), 'высоту ряда диктует fitQuad');

  // 🔴 Главный контракт: пропорция кадра сохранена. Обрезка — это ровно то, на что
  // владелец пожаловался («на IPCamLive они не обрезаются»).
  assert.ok(Math.abs(h - (w * 9 / 16)) <= 1, `плитка не 16:9: ${w}x${h}`);

  const used = 120 + h * 2;              // верх сетки + два ряда (зазор между ними — внутри)
  assert.ok(used <= m.r.w.innerHeight, 'панель не вылезает за экран: ' + used);
  assert.ok(w * 2 <= 1200, 'и не вылезает по ширине');
  m.inst.destroy();
});

test('пока поток не пошёл, у плитки виден кадр камеры, а не пустой прямоугольник', async () => {
  const m = mount({ cams: LIVE });
  await sleep(300);

  const v = videos(m)[0];
  assert.ok(v, 'видео-узел создан');
  assert.ok(/\/qdl\/live\/watch\/thumb\?camera=/.test(v.poster || ''), 'постер — снимок камеры: ' + v.poster);
  // подложка тем же кадром остаётся на месте, даже если постер не поддержан движком
  assert.strictEqual(m.root.find('.qdl-watch-frame').length, 4);
  m.inst.destroy();
});

test('на телефоне плитки идут друг за другом во всю ширину', () => {
  const m = mount();
  Object.defineProperty(m.r.w, 'innerWidth', { configurable: true, value: 420 });
  const g = stubGrid(m);
  m.inst.start();
  assert.strictEqual(g.style.gridAutoRows, '', 'высота ряда не навязана — её диктует 16/9 кадра');
  assert.ok(!m.r.$(g).hasClass('qdl-watch-grid--fit'));
  m.inst.destroy();
});

// ─────────────────────────── навигация ───────────────────────────

test('«вверх» из верхнего ряда уводит в шапку Lampa — там теперь Detection', () => {
  const m = mount();
  stubGrid(m);
  m.inst.start();

  m.calls.toggled = null;
  m.r.$(quad(m)[0]).trigger('hover:focus');
  m.calls.ctrl.up();                 // Navigator.canmove('up') в заглушке = false
  assert.strictEqual(m.calls.toggled, 'head', 'фокус ушёл в шапку');
  m.inst.destroy();
});

// ─────────────────────────── живое видео ───────────────────────────

test('видимые плитки поднимают <video>, невидимые — нет', async () => {
  const m = mount({ cams: LIVE, io: { visible: (el) => el.getAttribute('data-cam') !== '4' } });
  await sleep(1700);   // разбег стартов 500 мс на плитку

  assert.strictEqual(videos(m).length, 3, 'играют три видимые плитки, четвёртая — нет');
  m.inst.destroy();
});

test('одновременно не больше LIVE_MAX_PLAYERS, даже если видно больше', async () => {
  const many = LIVE.concat([
    { id: 8, name: 'Пятая', live: true, path: '/qdl/live/watch/hls/8/index.m3u8' },
    { id: 9, name: 'Шестая', live: true, path: '/qdl/live/watch/hls/9/index.m3u8' },
  ]);
  const m = mount({ cams: many });
  await sleep(2200);

  assert.strictEqual(quad(m).length, 6, 'плиток шесть');
  assert.strictEqual(videos(m).length, m.r.qdl.LIVE_MAX_PLAYERS, 'а декодеров — не больше четырёх');
  m.inst.destroy();
});

test('неактивная камера декодер не занимает, но поток на регистраторе будит', async () => {
  const m = mount();
  await sleep(1700);

  assert.strictEqual(offTiles(m).find('video').length, 0, 'в нижнем блоке видео нет');
  assert.ok(m.calls.starts.length >= 1, 'но /watch/start по ним ушёл');
  m.inst.destroy();
});

test('плеер несёт uid устройства прямо в URL плейлиста', async () => {
  const m = mount({ before: (r) => r.lampa.Storage.set('lampac_unic_id', '7kfrxzfr') });
  await sleep(200);
  const src = m.r.$(videos(m)[0]).attr('src') || '';
  assert.ok(videos(m).length > 0, 'видео-узел создан');
  if (src) assert.ok(/[?&]uid=7kfrxzfr(&|$)/.test(src), 'uid в URL: ' + src);
  m.inst.destroy();
});

// ─────────────────────────── глобальный тумблер ───────────────────────────

test('глобальное «выключено» с сервера гасит эфир в плитках', async () => {
  const m = mount({ card: { live: { video: false } } });
  await sleep(300);

  assert.strictEqual(videos(m).length, 0, 'ни одного декодера');
  assert.strictEqual(quad(m).length, 4, 'а плитки на месте — просто кадрами');
  assert.strictEqual(m.root.find('.qdl-watch-frame').length, 6);
  m.inst.destroy();
});

test('смена настройки на другом устройстве подхватывается без перезахода', async () => {
  const m = mount({ card: { live: { video: false } } });
  await sleep(300);
  assert.strictEqual(videos(m).length, 0);

  // так это и приезжает: следующий /qdl/features обновляет карту прав
  m.r.qdl.setCard({ live: { video: true } });
  m.inst.start();
  await sleep(1700);
  assert.ok(videos(m).length > 0, 'эфир поднялся сам');
  m.inst.destroy();
});

test('с выключенным эфиром Enter по плитке ведёт в нативный плеер, как раньше', () => {
  const m = mount({
    card: { live: { video: false } },
    start: { ready: true, running: true, path: '/qdl/live/watch/hls/3/index.m3u8' },
  });
  m.r.$(quad(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1, 'открылся нативный плеер');
  assert.ok(String(m.calls.plays[0].url).indexOf('/qdl/live/watch/hls/3/index.m3u8') !== -1);
  m.inst.destroy();
});

// ─────────────────────────── фулл вью ───────────────────────────

test('фулл вью: одна камера на весь экран, остальные на паузе', async () => {
  const m = mount({ cams: LIVE });
  await sleep(1700);
  assert.strictEqual(videos(m).length, 4);

  const home = quad(m)[0].parentNode;
  m.r.$(quad(m)[0]).trigger('hover:enter');
  await sleep(50);

  const f = full(m);
  assert.strictEqual(f.length, 1, 'развёрнута ровно одна плитка');
  assert.strictEqual(m.r.$(f[0]).attr('data-cam'), '3');
  // 🔴 position:fixed внутри скролла Lampa не покрывает экран (у контейнера transform),
  // поэтому плитка уезжает в body — поймано скриншотом живого клиента.
  assert.strictEqual(f[0].parentNode, m.r.w.document.body, 'развёрнутая плитка уехала в body');
  assert.strictEqual(m.r.$(f[0]).find('video')[0].paused, false, 'развёрнутая камера играет');
  // 🔴 Фокус обязан остаться на развёрнутой плитке: после переноса в body Lampa уводила его
  // на соседнюю, и OK в фулл вью открывал НЕ ТУ камеру, а Back возвращал не туда.
  assert.strictEqual(m.calls.marked, f[0], 'фокус переехал вместе с плиткой');

  tiles(m).filter((_, el) => !m.r.$(el).hasClass('qdl-watch-tile--full')).each((_, el) => {
    const v = m.r.$(el).find('video')[0];
    if (v) assert.strictEqual(v.paused, true, 'соседняя камера не декодирует');
  });

  m.calls.ctrl.back();
  assert.strictEqual(full(m).length, 0);
  assert.strictEqual(quad(m)[0].parentNode, home, 'плитка вернулась в сетку');
  assert.ok(!m.calls.backward, 'Back из фулл вью не выходит из раздела');
  m.inst.destroy();
});

test('🔴 на айфоне раскладка телефонная, даже если вьюпорт шире 600', () => {
  // Регресс: у приложения на iPhone вьюпорт оказался ШИРЕ 600 px — медиазапрос молчал,
  // офлайн-камеры вставали в два столбца, а панель уезжала за экран (свайп вбок).
  const m = mount();
  Object.defineProperty(m.r.w, 'innerWidth', { configurable: true, value: 1024 });
  Object.defineProperty(m.r.w.document.documentElement, 'clientWidth', { configurable: true, value: 900 });
  m.r.w.d1vision_platform = 'ios';

  const g = stubGrid(m, { top: 120, width: 900 });
  const off = m.root.find('.qdl-watch-off')[0];
  m.inst.start();

  assert.match(g.style.gridTemplateColumns, /^\d+px$/, 'одна колонка фиксированной ширины: ' + g.style.gridTemplateColumns);
  assert.strictEqual(g.style.gridAutoRows, '', 'высоту ряда на телефоне не навязываем — её даёт 16:9');
  assert.ok(!m.r.$(g).hasClass('qdl-watch-grid--fit'));
  assert.strictEqual(g.style.maxWidth, '900px', 'сетка не шире экрана — иначе появляется свайп вбок');
  assert.match(off.style.gridTemplateColumns, /^\d+px$/, 'офлайн-камеры тоже в одну колонку');
  m.inst.destroy();
});

test('🔴 развёрнутая камера остаётся НАД сеткой даже под фокусом', () => {
  // Регресс: у `.qdl-watch-tile.focus{z-index:1}` специфичность выше, чем у одиночного
  // `--full{z-index:900}`, и после переключения стрелкой сетка рисовалась ПОВЕРХ
  // полноэкранной камеры (поймано на телевизоре).
  const css = H.qdlSource();
  const rule = /\.qdl-watch-tile--full\.focus\{[^}]*z-index:900/.test(css);
  assert.ok(rule, 'в injectCss нет правила .qdl-watch-tile--full.focus{...z-index:900}');
  assert.ok(/\.qdl-watch-tile--full\.focus \.qdl-watch-ring\{border-color:transparent/.test(css),
    'рамка фокуса не должна обводить полноэкранную камеру');
});

test('в фулл вью стрелки переключают камеру, а не двигают фокус', async () => {
  const m = mount({ cams: LIVE });
  await sleep(1700);
  m.r.$(quad(m)[0]).trigger('hover:enter');
  await sleep(20);

  m.calls.ctrl.right();
  assert.strictEqual(full(m).length, 1, 'развёрнута по-прежнему ровно одна');
  assert.strictEqual(m.r.$(full(m)[0]).attr('data-cam'), '5');
  m.calls.ctrl.left();
  assert.strictEqual(m.r.$(full(m)[0]).attr('data-cam'), '3');
  m.inst.destroy();
});

test('уход с экрана возвращает развёрнутую плитку: иначе камера висит поверх чужого экрана', async () => {
  const m = mount({ cams: LIVE });
  await sleep(1700);
  const home = quad(m)[0].parentNode;

  m.r.$(quad(m)[0]).trigger('hover:enter');
  await sleep(50);
  assert.strictEqual(full(m)[0].parentNode, m.r.w.document.body);

  m.inst.pause();
  assert.strictEqual(full(m).length, 0, 'класс снят');
  assert.strictEqual(quad(m)[0].parentNode, home, 'плитка вернулась в ленту');
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

// ─────────────────────── сторож фулл вью (qdl 2.98) ───────────────────────
// Просьба владельца: «когда я открываю фулл вью, хочу чтобы была проверка, и если висит —
// перезапускался». Зависший поток НЕ даёт ошибок: hls.js молчит, <video> на месте, просто
// картинка стоит. Поэтому всё здесь меряется движением currentTime, а не наличием узла.

const FAST = { beat: 20, soft: 60, stale: 60, tick: 20, dead: 120, again: 150, young: 30, wake: 0 };

/** Открыть первую камеру на весь экран на живой сетке. */
async function openFull(m) {
  await sleep(1700);
  flow(m, 5);
  m.r.$(quad(m)[0]).trigger('hover:enter');
  await sleep(60);
  return full(m).find('video')[0];
}

test('фулл вью: замерший поток перезапускается сам', async () => {
  const m = mount({ cams: LIVE, guard: FAST });
  const before = await openFull(m);
  assert.ok(before, 'развёрнутая камера играет');

  // Картинку больше не двигаем — ровно то, что видит владелец: «висит».
  await sleep(400);

  const after = full(m).find('video')[0];
  assert.ok(after, 'плеер на месте');
  assert.notStrictEqual(after, before, 'плеер не пересобран — камера так и висит');
  assert.ok(m.calls.starts.some((u) => /camera=3(&|$)/.test(u)), 'поток на регистраторе тоже разбудили');
  m.inst.destroy();
});

test('фулл вью: живой поток никто не перезапускает', async () => {
  const m = mount({ cams: LIVE, guard: FAST });
  const before = await openFull(m);

  // 20 циклов сторожа при исправной картинке: ложный перезапуск здорового эфира — исход
  // хуже самого бага, зритель получал бы чёрный экран каждые несколько секунд.
  for (let i = 0; i < 20; i++) { flow(m, 1); await sleep(20); }

  assert.strictEqual(full(m).find('video')[0], before, 'плеер тот же самый');
  assert.strictEqual(m.calls.starts.length, 0, 'и регистратор не тревожили');
  m.inst.destroy();
});

test('🔴 в сетке зависшие плитки НЕ перезапускаются — это осознанная граница', async () => {
  // На слабом ТВ владельца из четырёх плиток поднимаются две («это не страшно, телевизор
  // слабый»). Автоперезапуск там стал бы вечным циклом «создать декодер — не дали — снести».
  const m = mount({ cams: LIVE, guard: FAST });
  await sleep(1700);
  const before = videos(m).toArray();
  assert.strictEqual(before.length, 4);

  await sleep(400);   // никто не двигает картинку — сетка «висит» целиком

  assert.deepStrictEqual(videos(m).toArray(), before, 'плееры сетки не пересобирали');
  assert.strictEqual(m.calls.starts.length, 0, 'и регистратор не будили');
  m.inst.destroy();
});

test('фулл вью на мёртвой плитке забирает декодеры соседей, на живой — только паузит', async () => {
  // Пауза декодер НЕ освобождает, его освобождает только destroy: если открытая камера не
  // показывает картинку, соседи и есть та стена, в которую она упирается.
  const dead = mount({ cams: LIVE, guard: FAST });
  await sleep(1700);
  assert.strictEqual(videos(dead).length, 4);
  dead.r.$(quad(dead)[0]).trigger('hover:enter');
  await sleep(60);
  assert.strictEqual(videos(dead).length, 1, 'соседи снесены, декодеры у полноэкранной');
  dead.inst.destroy();

  const live = mount({ cams: LIVE, guard: FAST });
  await openFull(live);
  assert.strictEqual(videos(live).length, 4, 'здоровой камере хватает своего — соседи целы');
  tiles(live).filter((_, el) => !live.r.$(el).hasClass('qdl-watch-tile--full')).each((_, el) => {
    const v = live.r.$(el).find('video')[0];
    if (v) assert.strictEqual(v.paused, true, 'но стоят на паузе');
  });
  live.inst.destroy();
});

test('выключенный глобально эфир сторож не воскрешает', async () => {
  // Тумблер могли выключить с другого устройства прямо во время просмотра: sync() гасит
  // плееры, и сторож обязан молчать — иначе он поднимал бы видео каждые десять секунд.
  const m = mount({ cams: LIVE, guard: FAST });
  await openFull(m);

  m.r.qdl.setCard({ live: { video: false } });
  m.inst.start();                       // так это и приезжает: следующий /qdl/features
  assert.strictEqual(videos(m).length, 0, 'эфир погашен');

  await sleep(400);                     // сторож успел бы дважды
  assert.strictEqual(videos(m).length, 0, 'и остался погашенным');
  m.inst.destroy();
});

test('сторож умирает вместе с экраном: после ухода перезапусков нет', async () => {
  const m = mount({ cams: LIVE, guard: FAST });
  await openFull(m);

  m.inst.pause();                       // ушли вперёд на другой экран
  assert.strictEqual(videos(m).length, 0, 'плееров не осталось');
  const starts = m.calls.starts.length;

  await sleep(400);                     // сторож, если бы выжил, успел бы дважды
  assert.strictEqual(videos(m).length, 0, 'фоновый сторож ничего не поднял');
  assert.strictEqual(m.calls.starts.length, starts, 'и регистратор из фона не будил');
  m.inst.destroy();
});
