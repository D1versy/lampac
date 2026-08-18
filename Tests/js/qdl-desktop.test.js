'use strict';
// desktop.js (qdl 2.50) — десктоп-адаптации Windows/Mac: платформенный гейт,
// синглтон, перехват wheel в capture (штатный хендлер Lampa на .scroll глохнет),
// шаг горизонтальной ленты, форс navigation_type='mouse' и гейт putScriptAsync
// в lampainit-invc.js. Поведенческая правда (плавность) проверяется руками —
// здесь стражи от регрессий по контракту.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const H = require('./harness');

const DESKTOP = path.join(H.REPO, 'Modules', 'QbitDownload', 'plugins', 'desktop.js');
const LAMPAINIT = path.join(H.REPO, 'Modules', 'LampaWeb', 'plugins', 'lampainit-invc.js');
const UA_BASE = 'Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';

// jsdom с pretendToBeVisual: desktop.js зовёт requestAnimationFrame сразу при старте глайда
function loadDesktop(platform, opts) {
  opts = opts || {};
  const { JSDOM } = require('jsdom');
  const dom = new JSDOM('<!DOCTYPE html><html><head></head><body>' + (opts.bodyHtml || '') + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/', pretendToBeVisual: true });
  const w = dom.window;
  if (platform !== undefined) w.d1vision_platform = platform;
  w.Lampa = opts.lampa || H.makeLampa();
  w.eval(fs.readFileSync(DESKTOP, 'utf8').replace(/\r\n/g, '\n'));
  return { dom, w, doc: w.document };
}

// ─────────────────────────── платформенный гейт ───────────────────────────

test('desktop: windows → движок активен (d1v_desktop, CSS, класс body)', () => {
  const { w, doc } = loadDesktop('windows');
  assert.ok(w.d1v_desktop, 'window.d1v_desktop должен быть выставлен');
  assert.ok(doc.getElementById('d1v-desktop-css'), 'CSS d1v-desktop-css должен быть вставлен');
  assert.ok(doc.body.classList.contains('d1v-desktop'), 'body.d1v-desktop должен стоять');
});

test('desktop: mac → движок активен', () => {
  const { w, doc } = loadDesktop('mac');
  assert.ok(w.d1v_desktop);
  assert.ok(doc.getElementById('d1v-desktop-css'));
});

for (const plat of ['android', 'ios', 'tizen', 'web', undefined]) {
  test(`desktop: платформа ${plat === undefined ? '(нет)' : plat} → движок НЕ активируется`, () => {
    const { w, doc } = loadDesktop(plat);
    assert.strictEqual(w.d1v_desktop, undefined, 'd1v_desktop не должен ставиться');
    assert.strictEqual(doc.getElementById('d1v-desktop-css'), null, 'CSS не должен вставляться');
    assert.ok(!doc.body.classList.contains('d1v-desktop'));
  });
}

test('desktop: синглтон — повторная загрузка не дублирует CSS', () => {
  const { w, doc } = loadDesktop('windows');
  w.eval(fs.readFileSync(DESKTOP, 'utf8').replace(/\r\n/g, '\n'));   // второй putScriptAsync
  assert.strictEqual(doc.querySelectorAll('#d1v-desktop-css').length, 1);
});

// ─────────────────────────── перехват wheel ───────────────────────────

const SCROLL_HTML =
  '<div class="scroll"><div class="scroll__body"><div class="card selector">x</div></div></div>';

function stubScroll(w, el, over) {
  const body = el.querySelector('.scroll__body');
  let pos = 0;
  el.Scroll = Object.assign({
    _shifts: [],
    shift(px) { this._shifts.push(px); pos -= px; },
    position() { return pos; },
    body() { return body; },
    params() { return {}; },
    isEnd() { return false; },
  }, over || {});
  return el.Scroll;
}

function fireWheel(w, target, init) {
  const e = new w.WheelEvent('wheel', Object.assign({ bubbles: true, cancelable: true, deltaMode: 0 }, init));
  target.dispatchEvent(e);
  return e;
}

test('desktop: wheel над вертикальным .scroll → глайд стартует, штатный хендлер Lampa глохнет', () => {
  const { w, doc } = loadDesktop('windows', { bodyHtml: SCROLL_HTML });
  const scroll = doc.querySelector('.scroll');
  stubScroll(w, scroll);
  // как Lampa: свой wheel-хендлер в bubble на самом .scroll (29744)
  let stockCalls = 0;
  scroll.addEventListener('wheel', () => { stockCalls++; });
  const e = fireWheel(w, doc.querySelector('.card'), { deltaY: 120 });
  assert.ok(e.defaultPrevented, 'wheel должен быть preventDefault-нут');
  assert.strictEqual(stockCalls, 0, 'штатный хендлер на .scroll не должен сработать (stopPropagation в capture)');
  assert.ok(scroll.querySelector('.scroll__body').classList.contains('d1v-glide'),
    'на время глайда на .scroll__body должен стоять d1v-glide (transition:none)');
});

test('desktop: ctrlKey-wheel (пинч/зум) над .scroll глотается без глайда', () => {
  const { w, doc } = loadDesktop('windows', { bodyHtml: SCROLL_HTML });
  const scroll = doc.querySelector('.scroll');
  stubScroll(w, scroll);
  const e = fireWheel(w, doc.querySelector('.card'), { deltaY: 120, ctrlKey: true });
  assert.ok(e.defaultPrevented, 'зум-жест должен гаситься');
  assert.ok(!scroll.querySelector('.scroll__body').classList.contains('d1v-glide'), 'глайд не должен стартовать');
});

test('desktop: wheel вне скроллов и активити — не вмешиваемся', () => {
  const { w, doc } = loadDesktop('windows', { bodyHtml: '<div id="head">head</div>' });
  const e = fireWheel(w, doc.getElementById('head'), { deltaY: 120 });
  assert.ok(!e.defaultPrevented, 'без скролла событие не трогаем');
});

test('desktop: deltaX над горизонтальной лентой → шаг фокуса через onWheel (знак по дельте)', () => {
  const html = '<div class="scroll scroll--horizontal"><div class="scroll__body"><div class="card selector">x</div></div></div>';
  const { w, doc } = loadDesktop('windows', { bodyHtml: html });
  const line = doc.querySelector('.scroll--horizontal');
  const calls = [];
  stubScroll(w, line, { params: () => ({ horizontal: true, step: 300 }), onWheel: (s) => calls.push(s) });
  fireWheel(w, doc.querySelector('.card'), { deltaX: 120, deltaY: 3 });
  assert.deepStrictEqual(calls, [300], 'один шаг +300 (аккумулятор ≥60px, знак вправо)');
});

test('desktop: вертикальное колесо над горизонтальной лентой скроллит вертикального родителя', () => {
  const html =
    '<div class="scroll" id="page"><div class="scroll__body">' +
    '<div class="scroll scroll--horizontal" id="line"><div class="scroll__body"><div class="card selector">x</div></div></div>' +
    '</div></div>';
  const { w, doc } = loadDesktop('windows', { bodyHtml: html });
  const page = doc.getElementById('page');
  const line = doc.getElementById('line');
  stubScroll(w, page);
  const lineCalls = [];
  stubScroll(w, line, { params: () => ({ horizontal: true, step: 300 }), onWheel: (s) => lineCalls.push(s) });
  fireWheel(w, doc.querySelector('.card'), { deltaY: 120, deltaX: 0 });
  assert.deepStrictEqual(lineCalls, [], 'лента не листается вертикальной дельтой при живом родителе');
  assert.ok(page.querySelector('.scroll__body').classList.contains('d1v-glide'), 'глайд ушёл вертикальному родителю');
});

// ─────────────────────────── lampainit-invc.js: десктопные форсы ───────────────────────────

test('lampainit: navigation_type=mouse форсится для windows и mac', () => {
  for (const tok of ['d1vision_windows/1.0.2-502', 'd1vision_mac/1.0.5-507']) {
    const { localStorage } = H.loadLampaInit({ navigator: { userAgent: `${UA_BASE} lampa_client ${tok}` } });
    assert.strictEqual(localStorage.getItem('navigation_type'), 'mouse', tok);
  }
});

test('lampainit: navigation_type НЕ форсится для ios/android/tizen/web', () => {
  const uas = [
    `${UA_BASE} lampa_client d1vision_ios/1.0.5-508`,
    `${UA_BASE} lampa_client d1vision_android/1.4.2-568`,
    `${UA_BASE} d1vision_tizen/1.0`,
    'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/120',
  ];
  for (const ua of uas) {
    const { localStorage } = H.loadLampaInit({ navigator: { userAgent: ua } });
    assert.strictEqual(localStorage.getItem('navigation_type'), null, ua);
  }
});

test('lampainit: appload подключает desktop.js ТОЛЬКО для windows/mac', () => {
  for (const [tok, expected] of [
    ['d1vision_windows/1.0.2-502', true],
    ['d1vision_mac/1.0.5-507', true],
    ['d1vision_ios/1.0.5-508', false],
    ['d1vision_android/1.4.2-568', false],
  ]) {
    const scripts = [];
    const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(...a) } });
    const { mod } = H.loadLampaInit({ lampa, navigator: { userAgent: `${UA_BASE} lampa_client ${tok}` } });
    mod.appload();
    const got = scripts.some((s) => s.indexOf('/desktop.js') >= 0);
    assert.strictEqual(got, expected, `${tok}: desktop.js ${expected ? 'должен' : 'не должен'} подключаться`);
  }
});
