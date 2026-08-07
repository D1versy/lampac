'use strict';
// Real-DOM tests (jsdom + real jQuery) for the header notification icon added to qdl.js:
// our own bell in .head__actions with an unread count badge, opening the qdl_notifications center,
// and updateNotiBadge keeping the header + left-menu badges in sync. Requires `npm install` (jsdom).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// A header actions bar (settings only — the native notice bell is cut server-side by AppPatch)
// plus a left-menu notifications item with its badge.
function pageHtml() {
  return (
    '<div class="head"><div class="head__body"><div class="head__actions">' +
      '<div class="head__action selector open--settings">s</div>' +
    '</div></div></div>' +
    '<div class="menu"><ul>' +
      '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления' +
        '<span class="qdl-noti-badge" style="display:none">0</span></div></li>' +
    '</ul></div>'
  );
}

test('ensureHeaderNoti injects exactly one bell into .head__actions', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();

  assert.strictEqual(doc.querySelectorAll('.head__actions .qdl-noti-head').length, 1);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti coexists with a surviving native bell (tree changed, anchor missed)', () => {
  // если после смены tree якорь AppPatch не сработал и штатный колокольчик уцелел —
  // наш всё равно вставляется ровно один (прятать штатный будет CSS-фолбэк lampainit-invc)
  const html = pageHtml().replace(
    'open--settings">s</div>',
    'open--settings">s</div><div class="head__action selector notice--icon">n</div>');
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti with TWO .head__actions containers adds exactly one bell (no clone)', () => {
  // раньше append шёл в jQuery-НАБОР: при двух контейнерах узел клонировался в каждый
  const html =
    '<div class="head">' +
      '<div class="head__body"><div class="head__actions"><div class="head__action selector open--settings">s</div></div></div>' +
      '<div class="head__body"><div class="head__actions"><div class="head__action selector open--search">f</div></div></div>' +
    '</div>';
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti self-heals pre-existing duplicate bells down to one', () => {
  // дубликаты от прошлых версий/двойных вставок: гард их не только видит, но и сносит лишние
  const html =
    '<div class="head"><div class="head__actions">' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
    '</div></div>';
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head-badge').length, 1);
});

test('ensureHeaderNoti is idempotent (no duplicate icons)', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();
  qdl.ensureHeaderNoti();
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti does nothing when there is no header (no crash)', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: '<div class="menu"></div>' });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 0);
});

test('header bell opens the qdl_notifications component on hover:enter', () => {
  const pushes = [];
  const lampa = H.makeLampa({ Activity: { push: (a) => pushes.push(a) } });
  const { w, qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  qdl.ensureHeaderNoti();

  w.$('.qdl-noti-head').trigger('hover:enter');
  assert.strictEqual(pushes.length, 1);
  assert.strictEqual(pushes[0].component, 'qdl_notifications');
  assert.strictEqual(pushes[0].title, 'Уведомления');
});

test('updateNotiBadge syncs header + menu badges', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();

  qdl.updateNotiBadge(5);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '5');
  assert.notStrictEqual(doc.querySelector('.qdl-noti-head-badge').style.display, 'none');
  assert.ok(doc.querySelector('.qdl-noti-head').className.includes('active'));   // dot fallback
  assert.strictEqual(doc.querySelector('.qdl-noti-menu .qdl-noti-badge').textContent, '5');

  qdl.updateNotiBadge(0);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').style.display, 'none');
  assert.ok(!doc.querySelector('.qdl-noti-head').className.includes('active'));

  qdl.updateNotiBadge(120);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '99+');
});

test('header badge updates even when the left menu is absent', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: '<div class="head"><div class="head__actions"></div></div>' });
  qdl.ensureHeaderNoti();
  qdl.updateNotiBadge(3);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '3');
});

// ── pollNotifications: гонка и агрегация тостов ──

// Lampa-мок, где Reguest.silent копит запросы, не отвечая — ответ дёргается вручную
function makePollLampa(noty) {
  const pending = [];
  const lampa = H.makeLampa({ Noty: { show: (m) => noty.push(m) } });
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, cb, err) => pending.push({ url, cb, err });
  };
  return { lampa, pending };
}

test('pollNotifications is single-flight: concurrent calls make one request', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });

  qdl.pollNotifications();
  qdl.pollNotifications();          // в полёте — должен быть проигнорирован
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 1);

  pending[0].cb({ items: [], unread: 0 });   // ответ пришёл → замок снят
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 2);
});

test('pollNotifications unlocks after a request error', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });

  qdl.pollNotifications();
  pending[0].err();                 // сеть упала → замок обязан сняться
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 2);
});

test('multiple SWITCH/INFO in one poll are aggregated into a single toast', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);   // не первый опрос — тосты разрешены

  qdl.pollNotifications();
  pending[0].cb({ unread: 3, items: [
    { id: 6, kind: 'SWITCH', title: 'Сериал А', label: 'S01E05' },
    { id: 7, kind: 'INFO',   title: 'Сериал Б', label: 'S02E01' },
    { id: 8, kind: 'SWITCH', title: 'Сериал В', label: 'S03E02' },
  ] });

  assert.strictEqual(noty.length, 1);
  assert.strictEqual(noty[0], '🔔 Новых уведомлений: 3');
  assert.strictEqual(lampa.Storage.get('qdl_noti_lastid'), 8);
});

test('single SWITCH keeps the detailed toast; downloaded episodes keep their own aggregate', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);

  qdl.pollNotifications();
  pending[0].cb({ unread: 3, items: [
    { id: 6, kind: 'SWITCH', title: 'Сериал А', label: 'S01E05' },
    { id: 7, kind: 'DL', title: 'Сериал Б', label: 'S02E01' },
    { id: 8, kind: 'DL', title: 'Сериал В', label: 'S03E02' },
  ] });

  assert.strictEqual(noty.length, 2);
  assert.ok(noty[0].indexOf('🔀 Сериал А — S01E05') === 0);
  assert.strictEqual(noty[1], '📺 Скачано новых серий: 2');
});
