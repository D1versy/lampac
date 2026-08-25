'use strict';
// Real-DOM tests (jsdom + real jQuery) for ensureMenu(): наши пункты левого меню
// (Загрузки → Уведомления → XSMART → jut.su → D1versy Live → D1versy Rec) вставляются по одному
// разу, дубликаты от прошлых версий/двойных вставок самоисцеляются (dedupe), порядок держится.
//
// qdl 2.70: якорь — «Лента» (data-action="feed"), а не «Персоны»; порядок и место задал владелец.
// Плюс СЛОТ-ПРОХОДНИК 'xsmart-menu': пункт XSMART строит ЧУЖОЙ плагин (контейнер xsmart-proxy),
// мы его только держим на позиции. Без слота наш цикл вырывал бы jut.su на его место, чужой
// плагин вставлял бы пункт назад — и меню мигало бы бесконечно.
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

test('слот XSMART: чужой пункт переезжает между «Уведомления» и jut.su', () => {
  // плагин xsmart-proxy вставил свой пункт РАНЬШЕ нас и не туда — держать место обязаны мы
  const { doc, qdl } = load(undefined, XSMART_LI);
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 6), [
    'feed', 'qdl-menu', 'qdl-noti-menu', 'xsmart-menu', 'qdl-jut-menu', 'qdl-watch-menu', 'qdl-live-menu',
  ]);
});

test('слот XSMART: сам пункт мы НЕ создаём — его хозяин чужой плагин', () => {
  const { doc, qdl } = load();
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 0, 'qdl.js не строит чужой пункт');
  // и дырка на месте слота не разрывает цепочку — jut.su встаёт сразу за «Уведомлениями»
  assert.deepStrictEqual(chain(doc, 5), ['feed'].concat(ALL));
});

test('слот XSMART: повторные проходы не двигают пункт (нет пинг-понга с чужим плагином)', () => {
  const { doc, qdl } = load(undefined, XSMART_LI);
  qdl.ensureMenu();
  const after1 = chain(doc, 6);
  qdl.ensureMenu();
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 1);
  assert.deepStrictEqual(chain(doc, 6), after1);
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
  assert.strictEqual(doc.querySelectorAll('.xsmart-menu').length, 1);
  assert.deepStrictEqual(chain(doc, 6), [
    'feed', 'qdl-menu', 'qdl-noti-menu', 'xsmart-menu', 'qdl-jut-menu', 'qdl-watch-menu', 'qdl-live-menu',
  ]);
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

test('якорь: нет ни «Ленты», ни «Главной» — последний фолбэк «Персоны»', () => {
  const ctx = H.loadQdlDom({ bodyHtml: menuHtmlCustom(
    '<li class="menu__item selector" data-action="myperson">Персоны</li>') });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  const order = ourOrder(ctx.doc);
  assert.deepStrictEqual(order.slice(0, 6), ['myperson'].concat(ALL));
});

test('якорь: меню ещё не отрисовано — не вставляем ничего и не падаем', () => {
  const ctx = H.loadQdlDom({ bodyHtml: '<div class="menu"><div class="menu__case"><ul class="menu__list"></ul></div></div>' });
  ctx.qdl.setPerms({ live: true, rec: true });
  ctx.qdl.ensureMenu();

  assert.strictEqual(ctx.doc.querySelectorAll('.menu__item').length, 0, 'ждём следующего тика, а не рисуем в пустоту');
});
