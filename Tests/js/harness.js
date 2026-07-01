'use strict';
// Harness for unit-testing the fork's Lampa browser plugins under Node (node:test).
//
// The plugins are browser IIFEs that reference globals (Lampa, window, document, navigator, $ …)
// and auto-execute. We load their SOURCE in a fresh `vm` context seeded with mock globals so we can
// exercise the pure logic. For qdl.js we strip the auto-start tail and export the internal helpers.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const REPO = path.resolve(__dirname, '..', '..');
const QDL = path.join(REPO, 'Modules', 'QbitDownload', 'plugins', 'qdl.js');
const LAMPAINIT = path.join(REPO, 'Modules', 'LampaWeb', 'plugins', 'lampainit-invc.js');

// ─────────────────────────── mock builders ───────────────────────────

/** In-memory localStorage-compatible store. */
function makeStorage() {
  const m = new Map();
  return {
    _map: m,
    getItem: (k) => (m.has(k) ? m.get(k) : null),
    setItem: (k, v) => m.set(k, String(v)),
    removeItem: (k) => m.delete(k),
    clear: () => m.clear(),
  };
}

/** Minimal DOM element. */
function makeEl(props) {
  const el = {
    tagName: 'DIV', id: '', className: '', textContent: '', innerHTML: '',
    style: {}, _children: [], _attrs: {}, _listeners: {},
    setAttribute(k, v) { this._attrs[k] = v; },
    getAttribute(k) { return k in this._attrs ? this._attrs[k] : null; },
    removeAttribute(k) { delete this._attrs[k]; },
    appendChild(c) { this._children.push(c); return c; },
    insertBefore(c) { this._children.push(c); return c; },
    addEventListener(t, fn) { (this._listeners[t] = this._listeners[t] || []).push(fn); },
    closest() { return null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
  };
  return Object.assign(el, props || {});
}

/** Minimal document; `find` maps a selector → array of elements returned by querySelectorAll.
 *  Comma-separated selectors are supported and unioned (in document-agnostic order, de-duped),
 *  so `find['.a']` is returned for a query like `.a, .b .c`. */
function selectorLookup(find, sel) {
  const out = [];
  const seen = new Set();
  for (let part of String(sel).split(',')) {
    const arr = find[part.trim()];
    if (arr) for (const el of arr) if (!seen.has(el)) { seen.add(el); out.push(el); }
  }
  return out;
}
function makeDocument(find) {
  find = find || {};
  const byId = {};
  const head = makeEl({ tagName: 'HEAD' });
  const body = makeEl({ tagName: 'BODY' });
  return {
    head, body,
    _byId: byId,
    getElementById(id) { return byId[id] || null; },
    createElement(tag) {
      const e = makeEl({ tagName: String(tag).toUpperCase() });
      return new Proxy(e, { set(t, p, v) { t[p] = v; if (p === 'id' && v) byId[v] = t; return true; } });
    },
    querySelector(sel) { const a = selectorLookup(find, sel); return a.length ? a[0] : null; },
    querySelectorAll(sel) { return selectorLookup(find, sel); },
  };
}

/** Default Lampa mock; pass overrides to replace any branch. */
function makeLampa(over) {
  const storage = new Map();
  const L = {
    _storage: storage,
    Storage: {
      _calls: [],
      get(k, def) { return storage.has(k) ? storage.get(k) : def; },
      set(k, v) { storage.set(k, v); L.Storage._calls.push([k, v]); },
      listener: { follow() {} },
    },
    TMDB: { image: (p) => 'IMG:' + p, key: () => 'TMDBKEY', api: (p) => 'API:' + p },
    Platform: {},                 // no tv()/is() by default
    Reguest: function () { this.timeout = () => {}; this.silent = () => {}; this.clear = () => {}; },
    Listener: { follow() {} },
    Component: { add() {} },
    Select: { show() {} },
    Player: { play() {}, playlist() {} },
    Noty: { show() {} },
    Activity: { push() {}, replace() {}, active: () => ({}), backward() {}, own: () => true },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Template: { get: () => makeEl() },
    Scroll: function () { this.render = () => makeEl(); this.body = () => makeEl(); this.append = () => {}; this.minus = () => {}; this.update = () => {}; this.destroy = () => {}; },
    Lang: { add() {} },
    Utils: { putScriptAsync() {} },
  };
  return deepAssign(L, over || {});
}

function deepAssign(base, over) {
  for (const k of Object.keys(over)) {
    if (over[k] && typeof over[k] === 'object' && !Array.isArray(over[k]) && typeof base[k] === 'object')
      deepAssign(base[k], over[k]);
    else base[k] = over[k];
  }
  return base;
}

/** jQuery-ish stub: `$(html)` → element-ish with chainable no-ops. */
function make$() {
  const chain = new Proxy(function () { return chain; }, {
    get(_t, p) {
      if (p === 'length') return 0;
      if (p === Symbol.toPrimitive) return () => '';
      return () => chain;
    },
    apply() { return chain; },
  });
  return chain;
}

// ─────────────────────────── loaders ───────────────────────────

/**
 * Load qdl.js and return its internal pure helpers.
 * opts: { navigator, lampa, document, $, fetch, windowExtra }
 */
function loadQdl(opts) {
  opts = opts || {};
  let src = fs.readFileSync(QDL, 'utf8');

  const TAIL = "if (window.appready) start();\n    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });";
  const EXPORT = "window.__qdl = { esc: esc, names: names, slimCard: slimCard, cleanName: cleanName, videoFiles: videoFiles, baseName: baseName, isBrowser: isBrowser, isMobile: isMobile, streamUrl: streamUrl, posterUrl: posterUrl, relTime: relTime, badge: badge, chip: chip, getAudioPref: getAudioPref, setAudioPref: setAudioPref };";
  if (!src.includes(TAIL)) throw new Error('qdl.js tail anchor not found — harness needs updating');
  src = src.replace(TAIL, EXPORT);

  const win = Object.assign({ appready: false }, opts.windowExtra || {});
  const sandbox = {
    window: win,
    Lampa: opts.lampa || makeLampa(),
    document: opts.document || makeDocument(),
    navigator: opts.navigator || { userAgent: '' },
    $: opts.$ || make$(),
    fetch: opts.fetch || (() => Promise.resolve({ json: () => Promise.resolve({}) })),
    console, setTimeout, clearTimeout, setInterval, clearInterval,
  };
  sandbox.window.Lampa = sandbox.Lampa;
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: 'qdl.js' });
  if (!win.__qdl) throw new Error('qdl.js did not export __qdl');
  return { qdl: win.__qdl, sandbox };
}

/**
 * Load lampainit-invc.js. Top-level side effects run on load (premium seed).
 * opts: { localStorage, lampa, document, navigator, windowExtra }
 * returns { mod: lampainit_invc, sandbox, localStorage, lampa }
 */
function loadLampaInit(opts) {
  opts = opts || {};
  const src = fs.readFileSync(LAMPAINIT, 'utf8');
  const localStorage = opts.localStorage || makeStorage();
  const lampa = opts.lampa || makeLampa();
  const sandbox = {
    window: Object.assign({}, opts.windowExtra || {}),
    Lampa: lampa,
    document: opts.document || makeDocument(),
    navigator: opts.navigator || { userAgent: '' },
    localStorage,
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    console, setTimeout, clearTimeout, setInterval, clearInterval,
  };
  sandbox.window.Lampa = lampa;
  sandbox.window.localStorage = localStorage;
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: 'lampainit-invc.js' });
  return { mod: sandbox.lampainit_invc, sandbox, localStorage, lampa };
}

module.exports = { loadQdl, loadLampaInit, makeLampa, makeDocument, makeStorage, makeEl, make$, deepAssign, REPO };
