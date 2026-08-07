'use strict';
// Real-DOM tests (jsdom + real jQuery) for ensureMenu(): наши пункты левого меню
// (Загрузки → Уведомления → D1versy Live → D1versy Rec) вставляются по одному разу,
// дубликаты от прошлых версий/двойных вставок самоисцеляются (dedupe), порядок держится.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

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

function ourOrder(doc) {
  return Array.from(doc.querySelectorAll('.menu__item'))
    .map((e) => e.getAttribute('data-action') || e.className.match(/qdl-[a-z-]+/)?.[0])
    .filter(Boolean);
}

test('ensureMenu builds all four items once, in order after «Персоны»', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: menuHtml() });
  qdl.ensureMenu();

  for (const cls of ['qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu'])
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);

  const order = ourOrder(doc);
  const i = order.indexOf('myperson');
  assert.deepStrictEqual(order.slice(i, i + 5), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu']);
});

test('ensureMenu is idempotent across re-renders', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: menuHtml() });
  qdl.ensureMenu();
  qdl.ensureMenu();
  qdl.ensureMenu();
  for (const cls of ['qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu'])
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);
});

test('ensureMenu self-heals pre-existing duplicates down to one of each', () => {
  // дубликаты в фикстуре — как после бага с вставкой в jQuery-набор (клон в каждый элемент)
  const dup =
    '<li class="menu__item selector qdl-menu"><div class="menu__text">Загрузки</div></li>' +
    '<li class="menu__item selector qdl-menu"><div class="menu__text">Загрузки</div></li>' +
    '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления<span class="qdl-noti-badge">2</span></div></li>' +
    '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления<span class="qdl-noti-badge">2</span></div></li>';
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: menuHtml(dup) });
  qdl.ensureMenu();

  for (const cls of ['qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu'])
    assert.strictEqual(doc.querySelectorAll('.' + cls).length, 1, cls);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-badge').length, 1);

  const order = ourOrder(doc);
  const i = order.indexOf('myperson');
  assert.deepStrictEqual(order.slice(i, i + 5), ['myperson', 'qdl-menu', 'qdl-noti-menu', 'qdl-watch-menu', 'qdl-live-menu']);
});

test('dedupe removes extras and returns a single-element set', () => {
  const { w, qdl } = H.loadQdlDom({ bodyHtml: '<div class="foo">1</div><div class="foo">2</div><div class="foo">3</div>' });
  const n = qdl.dedupe('.foo');
  assert.strictEqual(n.length, 1);
  assert.strictEqual(w.$('.foo').length, 1);
  assert.strictEqual(n.text(), '1');   // остаётся ПЕРВЫЙ экземпляр
});
