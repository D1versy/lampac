'use strict';
// Real-DOM tests (jsdom + real jQuery) for the header notification icon added to qdl.js:
// our own bell in .head__actions with an unread count badge, opening the qdl_notifications center,
// and updateNotiBadge keeping the header + left-menu badges in sync. Requires `npm install` (jsdom).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// A header actions bar (settings + native notice) plus a left-menu notifications item with its badge.
function pageHtml() {
  return (
    '<div class="head"><div class="head__body"><div class="head__actions">' +
      '<div class="head__action selector open--settings">s</div>' +
      '<div class="head__action selector open--notice notice--icon">n</div>' +
    '</div></div></div>' +
    '<div class="menu"><ul>' +
      '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления' +
        '<span class="qdl-noti-badge" style="display:none">0</span></div></li>' +
    '</ul></div>'
  );
}

test('ensureHeaderNoti injects our bell into .head__actions, before the native notice', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();

  assert.strictEqual(doc.querySelectorAll('.head__actions .qdl-noti-head').length, 1);
  const cls = Array.from(doc.querySelectorAll('.head__actions > *')).map((e) => e.className);
  const iOur = cls.findIndex((c) => c.includes('qdl-noti-head'));
  const iNative = cls.findIndex((c) => c.includes('open--notice'));
  assert.ok(iOur >= 0 && iNative >= 0 && iOur < iNative, 'our bell should sit before the native notice');
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
