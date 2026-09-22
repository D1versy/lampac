'use strict';
// Кнопка поиска в шапке — контекстная (qdl 2.121): в разделе XSMART → поиск XSMART, в jut.su →
// поиск jut.su, везде остальное → наш экран d1v_search; киллсвитч searchScreen с сервера
// возвращает штатный оверлей Lampa целиком.
//
// Почему перевеска DOM-обработчика: Head.addElement привязал hover:enter кнопки прямой ссылкой
// на замыкание open$3 бандла — обёртка Lampa.Search.open клик по кнопке НЕ перехватит. Обёртка
// нужна для программных входов: нижняя панель телефона (Lampa.Search.open()) и голосовой запрос
// Android-клиента (Lampa.Search.open({input:q})). Канон — E:\Media-server\claude\19-search.md.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function head() {
  return '<div class="head"><div class="head__body"><div class="head__actions">' +
    '<div class="head__action selector open--search">s</div></div></div></div>';
}

function boot(opts) {
  opts = opts || {};
  const calls = { pushed: [], toggles: [], orig: [], editCb: null, urls: [] };
  const lampa = H.makeLampa();
  let active = opts.component || 'main';
  lampa.Activity.active = () => ({ component: active });
  lampa.Activity.push = (o) => calls.pushed.push(o);
  lampa.Controller.add = () => {};
  lampa.Controller.toggle = (n) => calls.toggles.push(n);
  lampa.Controller.collectionSet = () => {};
  lampa.Controller.collectionFocus = () => {};
  lampa.Controller.collectionAppend = () => {};
  lampa.Storage.field = (k) => (k === 'keyboard_type' ? 'lampa' : undefined);
  lampa.Platform = { screen: () => false, is: () => false };
  lampa.Input = { edit(p, cb) { calls.editCb = cb; } };
  lampa.Search = { open(p) { calls.orig.push(p); } };   // штатный оверлей (open$3 бандла)
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => { calls.urls.push(url); ok({ ok: true, items: [] }); };
  };

  const ctx = H.loadQdlDom({ bodyHtml: head(), lampa });
  const { w } = ctx;
  w.Lampa.Scroll = function () {
    const $box = w.$('<div class="scroll"></div>');
    w.document.body.appendChild($box[0]);
    this.render = () => $box;
    this.body = () => $box;
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => { $box.remove(); };
  };
  w.Lampa.Layer = { visible() {}, update() {} };
  w.Lampa.Template.get = (name, data) => w.$(
    '<div class="card selector"><div class="card__view"><img class="card__img" /></div>' +
    '<div class="card__title">' + ((data && data.title) || '') + '</div></div>');
  return Object.assign(ctx, { calls, setActive: (c) => { active = c; } });
}

const btn = (ctx) => ctx.doc.querySelector('.head__actions .open--search');

test('обёртка Lampa.Search.open ставится один раз, оригинал сохраняется', () => {
  const ctx = boot();
  const orig = ctx.w.Lampa.Search.open;
  ctx.qdl.initSearchRoute();
  const wrapped = ctx.w.Lampa.Search.open;
  assert.notStrictEqual(wrapped, orig, 'Lampa.Search.open обёрнут');
  ctx.qdl.initSearchRoute();
  assert.strictEqual(ctx.w.Lampa.Search.open, wrapped, 'повторный init (qdl.js приехал дважды) обёртку не оборачивает');
  assert.strictEqual(ctx.qdl.origSearchOpen(), orig, 'оригинал сохранён для фолбэка и чипа «Штатный поиск»');
});

test('перевеска кнопки шапки идемпотентна и снимает штатный обработчик', () => {
  const ctx = boot();
  let stock = 0;
  ctx.$(btn(ctx)).on('hover:enter', () => stock++);   // как Head.addElement: прямая ссылка на open$3

  ctx.qdl.initSearchRoute();
  ctx.qdl.ensureSearchRoute();
  ctx.qdl.ensureSearchRoute();
  const handlers = ctx.$._data(btn(ctx), 'events')['hover:enter'];
  assert.strictEqual(handlers.length, 1, 'ровно один обработчик после трёх вызовов');

  ctx.$(btn(ctx)).trigger('hover:enter');
  assert.strictEqual(stock, 0, 'штатный обработчик снят — иначе открылись бы ОБА поиска');
  assert.strictEqual(ctx.calls.pushed.length, 1);
  const o = ctx.calls.pushed[0];
  assert.strictEqual(o.component, 'd1v_search');
  assert.strictEqual(o.autokb, true, 'с кнопки шапки клавиатура открывается сразу');
  assert.strictEqual(o.query, '');
  // без title/url/page шапка активности осталась бы без заголовка (start$4 → Head.title)
  assert.strictEqual(o.title, 'Поиск');
  assert.strictEqual(o.url, '');
  assert.strictEqual(o.page, 1);
});

test('маршрут по разделу: XSMART → xsmart_search, jut.su → jut_search, остальное → d1v_search', () => {
  const ctx = boot();
  ctx.qdl.initSearchRoute();
  const go = (c) => { ctx.setActive(c); ctx.qdl.routeSearch({}); return ctx.calls.pushed.pop(); };

  let o = go('xsmart_main');
  assert.strictEqual(o.component, 'xsmart_search');
  assert.strictEqual(o.xsmart_autokb, true);
  assert.strictEqual(o.xsmart_query, '');
  assert.strictEqual(o.title, 'XSMART — поиск');
  assert.strictEqual(go('xsmart_title').component, 'xsmart_search');
  assert.strictEqual(go('xsmart_episodes').component, 'xsmart_search');

  o = go('jut_catalog');
  assert.strictEqual(o.component, 'jut_search');
  assert.strictEqual(o.autokb, true);
  assert.strictEqual(go('jut_title').component, 'jut_search');
  assert.strictEqual(go('jut_episodes').component, 'jut_search');

  for (const c of ['main', 'full', 'category_full', 'qdl_downloads', 'lampac_music_home', 'sisi_lampac', 'online_main', '']) {
    assert.strictEqual(go(c).component, 'd1v_search', 'раздел ' + JSON.stringify(c));
  }
});

test('готовый запрос (голосовой ввод Android: Lampa.Search.open({input})) ищется сразу, без клавиатуры', () => {
  const ctx = boot();
  ctx.qdl.initSearchRoute();

  ctx.w.Lampa.Search.open({ input: '  матрица ' });
  let o = ctx.calls.pushed.pop();
  assert.strictEqual(o.component, 'd1v_search');
  assert.strictEqual(o.query, 'матрица');
  assert.strictEqual(o.autokb, false, 'запрос есть — клавиатура поверх выдачи не нужна');

  ctx.setActive('xsmart_catalog');
  ctx.w.Lampa.Search.open({ input: 'дюна' });
  o = ctx.calls.pushed.pop();
  assert.strictEqual(o.component, 'xsmart_search');
  assert.strictEqual(o.xsmart_query, 'дюна');
  assert.strictEqual(o.xsmart_autokb, false);
});

test('вызов без аргументов (панель телефона) и с jQuery-событием не падает', () => {
  const ctx = boot();
  ctx.qdl.initSearchRoute();
  ctx.w.Lampa.Search.open();
  ctx.qdl.routeSearch(ctx.$.Event('hover:enter'));
  assert.strictEqual(ctx.calls.pushed.length, 2);
  assert.strictEqual(ctx.calls.pushed[0].query, '');
  assert.strictEqual(ctx.calls.pushed[1].query, '', 'объект события не читается как запрос');
  assert.strictEqual(ctx.calls.pushed[1].autokb, true);
});

test('уже на экране поиска: запрос уходит в экран, без запроса — фокус и клавиатура, новых push нет', () => {
  const ctx = boot({ component: 'jut_search' });
  ctx.qdl.initSearchRoute();
  const comp = new ctx.qdl.ComponentJutSearch({});
  comp.activity = { loader() {}, toggle() {} };
  ctx.w.document.body.appendChild(comp.create()[0]);
  comp.start();   // экран объявляет себя активным
  assert.strictEqual(ctx.qdl.searchScreenActive(), comp);

  ctx.qdl.routeSearch({ input: 'наруто' });
  assert.strictEqual(ctx.calls.pushed.length, 0, 'второй экран поиска поверх первого не растим');
  assert.ok(ctx.calls.urls.some((u) => u.includes('/qdl/jut/search?query=' + encodeURIComponent('наруто'))), 'запрос ушёл в открытый экран');

  ctx.calls.toggles.length = 0;
  ctx.qdl.routeSearch({});
  assert.strictEqual(ctx.calls.pushed.length, 0);
  assert.ok(ctx.calls.toggles.includes('content'), 'фокус возвращён экрану');
  assert.ok(ctx.calls.editCb, 'без запроса открывается клавиатура');

  comp.destroy();
  assert.strictEqual(ctx.qdl.searchScreenActive(), null, 'destroy снимает регистрацию');
});

test('киллсвитч searchScreen:false возвращает штатный оверлей с теми же параметрами', () => {
  const ctx = boot();
  ctx.qdl.initSearchRoute();

  ctx.qdl.setSearchConf({ screen: false });
  assert.strictEqual(ctx.qdl.d1vSearchEnabled(), false);
  assert.deepStrictEqual(JSON.parse(JSON.stringify(ctx.w.Lampa.Storage.get('qdl_search_cfg'))), { screen: false },
    'кеш в Storage: первая же кнопка после старта не ждёт /qdl/features');

  ctx.$(btn(ctx)).trigger('hover:enter');
  ctx.w.Lampa.Search.open({ input: 'z' });
  assert.strictEqual(ctx.calls.pushed.length, 0, 'наши экраны не открываются');
  assert.strictEqual(ctx.calls.orig.length, 2, 'оба входа ушли в штатный оверлей');
  assert.deepStrictEqual(ctx.calls.orig[1], { input: 'z' }, 'параметры переданы как есть');

  ctx.qdl.setSearchConf({ screen: true });   // /qdl/features вернул флаг — без перезапуска
  ctx.$(btn(ctx)).trigger('hover:enter');
  assert.strictEqual(ctx.calls.pushed.length, 1);
  assert.strictEqual(ctx.calls.orig.length, 2);
});

test('setSearchConf терпит мусор: флаг не булев — остаётся прежним', () => {
  const ctx = boot();
  ctx.qdl.setSearchConf(null);
  ctx.qdl.setSearchConf({ screen: 'no' });
  ctx.qdl.setSearchConf({ other: 1 });
  assert.strictEqual(ctx.qdl.d1vSearchEnabled(), true);
});
