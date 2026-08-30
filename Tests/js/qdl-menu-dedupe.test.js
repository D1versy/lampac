'use strict';
// Real-DOM tests (jsdom + real jQuery) for ensureMenu(): наши пункты левого меню
// (Загрузки → Уведомления → jut.su → D1versy Live → D1versy Rec) вставляются по одному
// разу, дубликаты от прошлых версий/двойных вставок самоисцеляются (dedupe), порядок держится.
//
// qdl 2.70: якорь — «Лента» (data-action="feed"), а не «Персоны»; порядок и место задал владелец.
// qdl 2.73: чужой пункт «xSmart» (его строит плагин из контейнера xsmart-proxy) переехал под
// «Главную» — ВЫШЕ нашего якоря. Слот-проходник на него убран: держать пункт, стоящий вне нашей
// цепочки, значит воевать с его хозяином за позицию. Здесь это закреплено тестом.

//
// qdl 2.54: D1versy Live/Rec гейтятся правами устройства (сервер, /qdl/features). Здесь же —
// главный инвариант новой цепочки якорей: дырка в середине (право не выдано) НЕ должна уносить
// пункты ниже. До 2.54 Rec цеплялся за Live, а jut.su за Rec — и спрятанный Live уносил все три.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Пункты, которые строит САМ qdl.js (xsmart-menu сюда не входит — он чужой, см. слот).
const ALL = ['qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu', 'qdl-watch-menu', 'qdl-live-menu'];

/** Готовый пункт XSMART — ровно такой, каким его вставляет плагин из контейнера xsmart-proxy. */
const XSMART_LI = '<li class="menu__item selector xsmart-menu"><div class="menu__text">XSMART</div></li>';

function menuHtml(extraItems) {
  return (
    '<div class="menu"><div class="menu__case"><ul class="menu__list">' +
      '<li class="menu__item selector" data-action="main">Главная</li>' +
      '<li class="menu__item selector" data-action="feed">Лента</li>' +
      '<li class="menu__item selector" data-action="myperson">Персоны</li>' +
      (extraItems || '') +
      '<li class="menu__item selector" data-action="settings">Настройки</li>' +
    '</ul></div></div>'
  );
}

/** Загрузить плагин в DOM с заданными правами (по умолчанию — оба раздела разрешены). */
function load(perms, extraItems) {
  const ctx = H.loadQdlDom({ bodyHtml: menuHtml(extraItems) });
  ctx.qdl.setPerms(perms === undefined ? { live: true, rec: true } : perms);
  return ctx;
}

function ourOrder(doc) {
  return Array.from(doc.querySelectorAll('.menu__item'))
    .map((e) => e.getAttribute('data-action') || e.className.match(/(?:qdl|xsmart)-[a-z-]+/)?.[0])
    .filter(Boolean);
}

/** Наши пункты подряд, начиная с «Ленты». */
function chain(doc, count) {
  const order = ourOrder(doc);
  const i = order.indexOf('feed');
  return order.slice(i, i + count + 1);
}

test('ensureMenu builds all five items once, in order after «Лента»', () => {
  const { doc, qdl } = load();
  qdl.ensureMenu();

  for (const cls of ALL)
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);

  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});

test('ensureMenu is idempotent across re-renders', () => {
  const { doc, qdl } = load();
  qdl.ensureMenu();
  qdl.ensureMenu();
  qdl.ensureMenu();
  for (const cls of ALL)
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);
});

test('ensureMenu self-heals pre-existing duplicates down to one of each', () => {
  // дубликаты в фикстуре — как после бага с вставкой в jQuery-набор (клон в каждый элемент)
  const dup =
    '<li class="menu__item selector qdl-menu"><div class="menu__text">Загрузки</div></li>' +
    '<li class="menu__item selector qdl-menu"><div class="menu__text">Загрузки</div></li>' +
    '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления<span class="qdl-noti-badge">2</span></div></li>' +
    '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления<span class="qdl-noti-badge">2</span></div></li>';
  const { doc, qdl } = load(undefined, dup);
  qdl.ensureMenu();

  for (const cls of ALL)
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-badge').length, 1);

  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});

test('dedupe removes extras and returns a single-element set', () => {
  const { w, qdl } = H.loadQdlDom({ bodyHtml: '<div class="foo">1</div><div class="foo">2</div><div class="foo">3</div>' });
  const n = qdl.dedupe('.foo');
  assert.strictEqual(n.length, 1);
  assert.strictEqual(w.$('.foo').length, 1);
  assert.strictEqual(n.text(), '1');   // остаётся ПЕРВЫЙ экземпляр
});

// ───────── qdl 2.54: права на скрытые разделы ─────────

test('без прав: ни Live, ни Rec — но Загрузки/Уведомления/jut.su на месте', () => {
  const { doc, qdl } = load({ live: false, rec: false });
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 0, 'D1versy Live не должен появиться');
  assert.strictEqual(doc.querySelectorAll('.qdl-live-menu').length, 0, 'D1versy Rec не должен появиться');
  assert.deepStrictEqual(chain(doc, 3), ['feed', 'qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu']);
});

test('только live: Rec нет, пункты ниже не уехали (главный баг старой цепочки якорей)', () => {
  const { doc, qdl } = load({ live: true, rec: false });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 4), ['feed', 'qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu', 'qdl-watch-menu']);
});

test('только rec: Rec встаёт на место Live, порядок остальных цел', () => {
  const { doc, qdl } = load({ live: false, rec: true });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 4), ['feed', 'qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu', 'qdl-live-menu']);
});

test('право отозвали — пункт снимается на следующем проходе', () => {
  const { doc, qdl } = load({ live: true, rec: true });
  qdl.ensureMenu();
  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 1);

  qdl.setPerms({ live: false, rec: true });
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 0, 'снятое право убирает пункт');
  assert.deepStrictEqual(chain(doc, 4), ['feed', 'qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu', 'qdl-live-menu']);
});

test('право выдали на живом клиенте — пункт встаёт в своё место, а не в конец', () => {
  const { doc, qdl } = load({ live: false, rec: false });
  qdl.ensureMenu();

  qdl.setPerms({ live: true, rec: true });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});

test('права не пришли (сервер молчит) — читаем кеш Lampa.Storage, но только его', () => {
  const { doc, qdl, lampa } = H.loadQdlDom({ bodyHtml: menuHtml() });
  qdl.setPerms(null);                                   // ответа сервера ещё не было
  lampa.Storage.set('qdl_features', { live: true, rec: false });
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 1, 'кеш даёт мгновенную отрисовку');
  assert.strictEqual(doc.querySelectorAll('.qdl-live-menu').length, 0, 'чего нет в кеше — не рисуем');
});

// ───────── qdl 2.70: якорь «Лента» и слот-проходник XSMART ─────────

/** Меню с произвольным набором штатных пунктов — для проверки фолбэков якоря. */
function menuHtmlCustom(stdItems) {
  return (
    '<div class="menu"><div class="menu__case"><ul class="menu__list">' +
      stdItems +
      '<li class="menu__item selector" data-action="settings">Настройки</li>' +
    '</ul></div></div>'
  );
}

/** Меню, где чужой пункт xSmart уже стоит на своём месте — сразу под «Главной» (qdl 2.73). */
function loadWithXsmartOnTop() {
  const ctx = H.loadQdlDom({ bodyHtml: menuHtmlCustom(
    '<li class="menu__item selector" data-action="main">Главная</li>' +
    XSMART_LI +
    '<li class="menu__item selector" data-action="feed">Лента</li>' +
    '<li class="menu__item selector" data-action="myperson">Персоны</li>',
  ) });
  ctx.qdl.setPerms({ live: true, rec: true });
  return ctx;
}

test('🔴 чужой пункт xSmart стоит ВЫШЕ нашего якоря — мы его не трогаем', () => {
  // С 2.73 «xSmart» живёт сразу под «Главной», то есть до «Ленты», с которой начинается наша
  // цепочка. Раньше мы держали его слотом внутри цепочки; сохранись слот — цикл «держим строго
  // после якоря» утянул бы пункт вниз, чужой плагин вернул бы наверх, и меню замигало бы.
  const { doc, qdl } = loadWithXsmartOnTop();
  qdl.ensureMenu();

  assert.deepStrictEqual(ourOrder(doc).slice(0, 2), ['main', 'xsmart-menu'],
    'чужой пункт обязан остаться сразу под «Главной»');
  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL), 'наша цепочка идёт своим чередом');
});

test('слот XSMART: сам пункт мы НЕ создаём — его хозяин чужой плагин', () => {
  const { doc, qdl } = load();
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 0, 'qdl.js не строит чужой пункт');
  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});

test('🔴 повторные проходы не двигают чужой пункт (нет пинг-понга с xsmart.js)', () => {
  const { doc, qdl } = loadWithXsmartOnTop();
  qdl.ensureMenu();
  const after1 = ourOrder(doc);
  qdl.ensureMenu();
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 1);
  assert.deepStrictEqual(ourOrder(doc), after1, 'позиция обязана быть неподвижной точкой');
});


test('переезд с 2.69: пункты стояли под «Персонами» в старом порядке — перестраиваются под «Ленту»', () => {
  const stale =
    '<li class="menu__item selector qdl-menu"><div class="menu__text">Загрузки</div></li>' +
    '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления</div></li>' +
    '<li class="menu__item selector qdl-watch-menu"><div class="menu__text">D1versy Live</div></li>' +
    '<li class="menu__item selector qdl-live-menu"><div class="menu__text">D1versy Rec</div></li>' +
    '<li class="menu__item selector qdl-jut-menu"><div class="menu__text">jut.su</div></li>' +
    XSMART_LI;
  const { doc, qdl } = load(undefined, stale);
  qdl.ensureMenu();

  for (const cls of ALL) assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);
  // Чужой пункт из старой раскладки мы не переставляем — его вернёт наверх сам xsmart.js.
  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 1);
  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});


test('якорь: «Ленту» спрятали настройкой Lampa — встаём под «Главную»', () => {
  const ctx = H.loadQdlDom({ bodyHtml: menuHtmlCustom(
    '<li class="menu__item selector" data-action="main">Главная</li>' +
    '<li class="menu__item selector" data-action="myperson">Персоны</li>') });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  const order = ourOrder(ctx.doc);
  assert.deepStrictEqual(order.slice(order.indexOf('main'), order.indexOf('main') + 6), ['main'].concat(ALL));
});

test('якорь: нет ни «Ленты», ни «Главной» — последний фолбэк «Фильмы» (qdl 2.84)', () => {
  // Был «myperson», но с 2.84 пункт «Персоны» скрыт штатным флагом disable_features.persons —
  // фолбэк на него означал бы «якоря нет вовсе». «Фильмы» остаются при любых настройках.
  const ctx = H.loadQdlDom({ bodyHtml: menuHtmlCustom(
    '<li class="menu__item selector" data-action="movie">Фильмы</li>') });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  const order = ourOrder(ctx.doc);
  assert.deepStrictEqual(order.slice(0, 6), ['movie'].concat(ALL));
});

test('якорь: скрытые «Персоны» больше не считаются якорем (qdl 2.84)', () => {
  // Меню без «Ленты»/«Главной»/«Фильмов» — вставлять некуда, ждём следующего тика.
  const ctx = H.loadQdlDom({ bodyHtml: menuHtmlCustom(
    '<li class="menu__item selector" data-action="myperson">Персоны</li>') });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  assert.strictEqual(ctx.doc.querySelectorAll('.qdl-menu').length, 0,
    'мёртвый якорь не должен превращаться в «вставим куда попало»');
});

test('якорь: меню ещё не отрисовано — не вставляем ничего и не падаем', () => {
  const ctx = H.loadQdlDom({ bodyHtml: '<div class="menu"><div class="menu__case"><ul class="menu__list"></ul></div></div>' });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  assert.strictEqual(ctx.doc.querySelectorAll('.menu__item').length, 0, 'ждём следующего тика, а не рисуем в пустоту');
});
