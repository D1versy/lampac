'use strict';
// Real-DOM tests (jsdom) for the "CUB tab flashes in the Уведомления modal" fix in lampainit-invc.js.
// jsdom gives a real querySelectorAll + a real MutationObserver, so we can prove BOTH that the CUB
// tab is hidden and that it happens in the immediate observer pass (before the 300ms debounce) — i.e.
// no flash. Requires `npm install` in Tests/js (jsdom).

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const SRC = fs.readFileSync(
  path.resolve(__dirname, '..', '..', 'Modules', 'LampaWeb', 'plugins', 'lampainit-invc.js'), 'utf8');

const tick = () => new Promise((r) => setTimeout(r, 0));

// Boot a jsdom window with the plugin evaluated + a minimal Lampa mock.
function boot(bodyHtml) {
  const dom = new JSDOM('<!DOCTYPE html><html><head></head><body>' + (bodyHtml || '') + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/' });
  const w = dom.window;
  const storage = {};
  w.Lampa = {
    Utils: { putScriptAsync() {} },
    Storage: {
      get: (k, d) => (k in storage ? storage[k] : d),
      set: (k, v) => { storage[k] = v; },
      listener: { follow() {} },
    },
    Lang: { add() {} },
  };
  w.eval(SRC);
  return { dom, w, doc: w.document };
}

// A realistic notifications modal: title + a 3-tab bar (Все / CUB / TMDB).
function notificationsModalHtml() {
  return (
    '<div class="modal">' +
      '<div class="modal__content">' +
        '<div class="modal__title">Уведомления</div>' +
        '<div class="modal__tabs">' +
          '<div class="tabs__item selector" id="t-all">Все</div>' +
          '<div class="tabs__item selector" id="t-cub">CUB</div>' +
          '<div class="tabs__item selector" id="t-tmdb">TMDB</div>' +
        '</div>' +
      '</div>' +
    '</div>'
  );
}

test('CUB tab already present at appload is hidden; siblings stay visible', () => {
  const { w, doc } = boot(notificationsModalHtml());
  w.lampainit_invc.appload();   // calls hideCubTabs() synchronously once
  assert.strictEqual(doc.getElementById('t-cub').style.display, 'none');
  assert.strictEqual(doc.getElementById('t-all').style.display, '');
  assert.strictEqual(doc.getElementById('t-tmdb').style.display, '');
});

test('CUB tab inserted AFTER appload is hidden in the immediate observer pass, not the 300ms debounce', async () => {
  const { w, doc } = boot('');
  w.lampainit_invc.appload();   // installs the MutationObserver on body

  const wrap = doc.createElement('div');
  wrap.innerHTML = notificationsModalHtml();
  doc.body.appendChild(wrap);   // childList mutation → observer fires

  // A setTimeout(0) tick flushes the observer microtask but is far short of the 300ms hideLocked
  // debounce, so hiding here proves it runs in the IMMEDIATE pass. (The real no-flash guarantee also
  // relies on the browser draining that microtask before paint — a property jsdom does not model.)
  await tick();
  assert.strictEqual(doc.getElementById('t-cub').style.display, 'none');
  assert.strictEqual(doc.getElementById('t-all').style.display, '');
});

test('hides the whole tab even when the CUB text is a nested label', () => {
  const html =
    '<div class="modal"><div class="modal__title">Уведомления</div>' +
    '<div class="modal__tabs">' +
      '<div class="tabs__item selector" id="wrap"><span id="lbl">CUB</span></div>' +
    '</div></div>';
  const { w, doc } = boot(html);
  w.lampainit_invc.appload();
  // climbs from the <span> up to the single-tab wrapper whose text is still exactly "CUB"
  assert.strictEqual(doc.getElementById('wrap').style.display, 'none');
});

test('hides only the exact-"CUB" tab; look-alike, sibling, and the bar itself all stay', () => {
  // A real 3-tab bar with a genuine CUB tab: proves the exact-text match AND that hiding the CUB tab
  // never collapses the surrounding bar or the other tabs (the real over-hiding guard).
  const html =
    '<div class="modal"><div class="modal__title">Уведомления</div>' +
    '<div class="modal__tabs" id="bar">' +
      '<div class="tabs__item selector" id="all">Все</div>' +
      '<div class="tabs__item selector" id="cub">CUB</div>' +
      '<div class="tabs__item selector" id="cuboid">CUBOID</div>' +
    '</div></div>';
  const { w, doc } = boot(html);
  w.lampainit_invc.appload();
  assert.strictEqual(doc.getElementById('cub').style.display, 'none');   // exact match hidden
  assert.strictEqual(doc.getElementById('cuboid').style.display, '');    // substring look-alike untouched
  assert.strictEqual(doc.getElementById('all').style.display, '');       // sibling untouched
  assert.strictEqual(doc.getElementById('bar').style.display, '');       // whole tab bar NOT collapsed
});

test('navigation-tabs CUB button is hidden too', () => {
  const html =
    '<div class="navigation-tabs">' +
      '<div class="navigation-tabs__button" id="n-my">Мой Lampa</div>' +
      '<div class="navigation-tabs__button" id="n-cub">CUB</div>' +
    '</div>';
  const { w, doc } = boot(html);
  w.lampainit_invc.appload();
  assert.strictEqual(doc.getElementById('n-cub').style.display, 'none');
  assert.strictEqual(doc.getElementById('n-my').style.display, '');
});

test('appload still injects the hide-extras stylesheet and sets torrserver storage', () => {
  const { w, doc } = boot(notificationsModalHtml());
  w.lampainit_invc.appload();
  assert.ok(doc.getElementById('qdl-hide-extras'), 'style#qdl-hide-extras injected');
  assert.strictEqual(w.Lampa.Storage.get('torrserver_auth'), 'false');
});
