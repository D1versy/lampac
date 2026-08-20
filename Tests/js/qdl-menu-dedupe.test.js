'use strict';
// Real-DOM tests (jsdom + real jQuery) for ensureMenu(): наши пункты левого меню
// (Загрузки → Уведомления → D1versy Live → D1versy Rec → jut.su) вставляются по одному разу,
// дубликаты от прошлых версий/двойных вставок самоисцеляются (dedupe), порядок держится.
//
// qdl 2.54: D1versy Live/Rec гейтятся правами устройства (сервер, /qdl/features). Здесь же —
// главный инвариант новой цепочки якорей: дырка в середине (право не выдано) НЕ должна уносить
// пункты ниже. До 2.54 Rec цеплялся за Live, а jut.su за Rec — и спрятанный Live уносил все три.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const ALL = ['qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu', 'qdl-jut-menu'];

function menuHtml(extraItems) {
  return (
    '<div class="menu"><div class="menu__case"><ul class="menu__list">' +
      '<li class="menu__item selector" data-action="main">Главная</li>' +
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
    .map((e) => e.getAttribute('data-action') || e.className.match(/qdl-[a-z-]+/)?.[0])
    .filter(Boolean);
}

/** Наши пункты подряд, начиная с «Персоны». */
function chain(doc, count) {
  const order = ourOrder(doc);
  const i = order.indexOf('myperson');
  return order.slice(i, i + count + 1);
}

test('ensureMenu builds all five items once, in order after «Персоны»', () => {
  const { doc, qdl } = load();
  qdl.ensureMenu();

  for (const cls of ALL)
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);

  assert.deepStrictEqual(chain(doc, 5), ['myperson'].concat(ALL));
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

  assert.deepStrictEqual(chain(doc, 5), ['myperson'].concat(ALL));
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
  assert.deepStrictEqual(chain(doc, 3), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-jut-menu']);
});

test('только live: Rec нет, jut.su не уехал (главный баг старой цепочки якорей)', () => {
  const { doc, qdl } = load({ live: true, rec: false });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 4), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-jut-menu']);
});

test('только rec: Rec встаёт на место Live, порядок остальных цел', () => {
  const { doc, qdl } = load({ live: false, rec: true });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 4), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-live-menu', 'qdl-jut-menu']);
});

test('право отозвали — пункт снимается на следующем проходе', () => {
  const { doc, qdl } = load({ live: true, rec: true });
  qdl.ensureMenu();
  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 1);

  qdl.setPerms({ live: false, rec: true });
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 0, 'снятое право убирает пункт');
  assert.deepStrictEqual(chain(doc, 4), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-live-menu', 'qdl-jut-menu']);
});

test('право выдали на живом клиенте — пункт встаёт в своё место, а не в конец', () => {
  const { doc, qdl } = load({ live: false, rec: false });
  qdl.ensureMenu();

  qdl.setPerms({ live: true, rec: true });
  qdl.ensureMenu();

  assert.deepStrictEqual(chain(doc, 5), ['myperson'].concat(ALL));
});

test('права не пришли (сервер молчит) — читаем кеш Lampa.Storage, но только его', () => {
  const { doc, qdl, lampa } = H.loadQdlDom({ bodyHtml: menuHtml() });
  qdl.setPerms(null);                                   // ответа сервера ещё не было
  lampa.Storage.set('qdl_features', { live: true, rec: false });
  qdl.ensureMenu();

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-menu').length, 1, 'кеш даёт мгновенную отрисовку');
  assert.strictEqual(doc.querySelectorAll('.qdl-live-menu').length, 0, 'чего нет в кеше — не рисуем');
});
