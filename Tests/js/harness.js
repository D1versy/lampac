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
    // Зеркало реального Modules/Proxy/TmdbProxy/plugin.js ПОСЛЕ подстановки {localhost} на отдаче.
    // Плоский 'IMG:'+p прятал ровно ту форму URL, из-за которой сломались постеры «Загрузок»
    // (qdl 2.15 форсит proxy_tmdb=true → клиент шлёт в poster_url НАШ адрес, а не image.tmdb.org).
    // proxy_tmdb по умолчанию true — как у живого клиента после lampainit-invc.js.
    TMDB: {
      image: (p) => (L.Storage.get('proxy_tmdb', true)
        ? 'http://192.168.87.24:9118/tmdb/img/' + p + '?account_email=&uid=test'
        : 'http://image.tmdb.org/' + p),
      key: () => 'TMDBKEY',
      api: (p) => 'API:' + p,
    },
    Platform: {},                 // no tv()/is() by default
    Reguest: function () { this.timeout = () => {}; this.silent = () => {}; this.clear = () => {}; },
    Listener: { follow() {} },
    Component: { add() {} },
    Select: {
      show() {},
      render: () => null,
      // как в новом бандле Lampa: Select.listener с fullshow — на него подписан initSelectFix
      listener: {
        _subs: {},
        follow(t, fn) { (this._subs[t] = this._subs[t] || []).push(fn); },
        send(t, e) { (this._subs[t] || []).slice().forEach((fn) => fn(e)); },
      },
    },
    Layer: { _calls: [], update(w) { this._calls.push(w); } },
    Player: { play() {}, playlist() {} },
    Noty: { show() {} },
    Activity: { push() {}, replace() {}, active: () => ({}), backward() {}, own: () => true },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Template: { get: () => makeEl() },
    Scroll: function () { this.render = () => makeEl(); this.body = () => make$(); this.append = () => {}; this.minus = () => {}; this.update = () => {}; this.destroy = () => {}; },   // body() — jQuery-заглушка: компоненты зовут scroll.body().append(...)
    Lang: { add() {} },
    Utils: { putScriptAsync() {}, hash: (s) => 'h' + String(s) },
    // Timeline-стаб: view() выдаёт стабильный объект на hash (как настоящий Lampa.Timeline)
    Timeline: {
      _store: {},
      view(h) {
        const s = L.Timeline._store;
        if (!s[h]) s[h] = { hash: h, percent: 0, time: 0, duration: 0, handler() {} };
        return s[h];
      },
    },
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
  const src = qdlSource();

  const win = Object.assign({ appready: false }, opts.windowExtra || {});
  const sandbox = {
    window: win,
    Lampa: opts.lampa || makeLampa(),
    document: opts.document || makeDocument(),
    navigator: opts.navigator || { userAgent: '' },
    $: opts.$ || make$(),
    fetch: opts.fetch || (() => Promise.resolve({ json: () => Promise.resolve({}) })),
    console,
    setTimeout: opts.setTimeout || setTimeout,
    clearTimeout: opts.clearTimeout || clearTimeout,
    setInterval: opts.setInterval || setInterval,     // инъецируемы: poll-тесты дёргают тик руками
    clearInterval: opts.clearInterval || clearInterval,
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
    // appload с 1.7 читает location.origin (torrserver_url = origin + '/ts') — без стаба падал весь appload-блок
    location: opts.location || { origin: 'http://192.168.87.24:9118', hostname: '192.168.87.24' },
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    console, setTimeout, clearTimeout, setInterval, clearInterval,
  };
  sandbox.window.Lampa = lampa;
  sandbox.window.localStorage = localStorage;
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: 'lampainit-invc.js' });
  return { mod: sandbox.lampainit_invc, sandbox, localStorage, lampa };
}

// ── shared qdl.js source transform (strip auto-start, export internal helpers) ──
const QDL_TAIL = "if (window.appready) start();\n    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });";
const QDL_EXPORT = "window.__qdl = { esc: esc, names: names, slimCard: slimCard, cleanName: cleanName, videoFiles: videoFiles, baseName: baseName, isBrowser: isBrowser, isMobile: isMobile, mobileHls: mobileHls, streamUrl: streamUrl, posterUrl: posterUrl, relTime: relTime, badge: badge, chip: chip, getAudioPref: getAudioPref, setAudioPref: setAudioPref, updateNotiBadge: updateNotiBadge, ensureHeaderNoti: ensureHeaderNoti, buildHeaderNoti: buildHeaderNoti, ensureMenu: ensureMenu, dedupe: dedupe, normTitle: normTitle, isSerialName: isSerialName, isSeasonTail: isSeasonTail, findDownload: findDownload, addButton: addButton, canTranscode: canTranscode, quickMenu: quickMenu, confirmPartial: confirmPartial, watch: watch, openDownload: openDownload, chooseAndDownload: chooseAndDownload, pollTranscode: pollTranscode, dropAudioPref: dropAudioPref, rewriteCubUrl: rewriteCubUrl, rewriteWatchUrl: rewriteWatchUrl, isDmca: isDmca, whenDmca: whenDmca, setDmcaList: setDmcaList, noteCubDomain: noteCubDomain, groupDownloads: groupDownloads, gridOrder: gridOrder, commonPrefixTitle: commonPrefixTitle, buildCollectionPicker: buildCollectionPicker, itemTitle: itemTitle, addToCollection: addToCollection, collectionMenu: collectionMenu, renameCollection: renameCollection, chooseCover: chooseCover, healPoster: healPoster, saveMeta: saveMeta, ComponentDownloads: ComponentDownloads, touchCollections: touchCollections, stripExt: stripExt, epTimelineHash: epTimelineHash, epView: epView, epShort: epShort, chooseContinue: chooseContinue, buildPlaylist: buildPlaylist, chooseEpisode: chooseEpisode, watchByHash: watchByHash, ComponentEpisodes: ComponentEpisodes, epMark: epMark, epMeta: epMeta, epNumber: epNumber, fixSelectHeight: fixSelectHeight, initSelectFix: initSelectFix, liveSize: liveSize, ComponentCard: ComponentCard, ComponentNotifications: ComponentNotifications, ComponentLive: ComponentLive, ComponentLiveWatch: ComponentLiveWatch, ComponentLiveCamera: ComponentLiveCamera, startTranscode: startTranscode, addContinueButton: addContinueButton, mergedVideoFiles: mergedVideoFiles, srcHash: srcHash, fetchEpisodes: fetchEpisodes, epTimelineKey: epTimelineKey, pickTimeline: pickTimeline, shortDate: shortDate, torrentSubtitle: torrentSubtitle, openNotification: openNotification, pollNotifications: pollNotifications, mirrorParse: mirrorParse, mirrorValidRoad: mirrorValidRoad, mirrorRoad: mirrorRoad, mirrorMerge: mirrorMerge, mirrorPrune: mirrorPrune, mirrorHasKV: mirrorHasKV, mirrorStart: mirrorStart, mirrorRead: mirrorRead, mirrorWrite: mirrorWrite, initTimelineMirror: initTimelineMirror, onTimelineUpdate: onTimelineUpdate, tlBucket: tlBucket, setTlBucket: setTlBucket, clearTlBucket: clearTlBucket, initTimecodeBridge: initTimecodeBridge, rawPlay: rawPlay, warmup: warmup, prewarmForCard: prewarmForCard, warmupNext: warmupNext, dropEpCache: dropEpCache, audioLang: audioLang, audioLangs: audioLangs, getLangPref: getLangPref, setLangPref: setLangPref, filterByLang: filterByLang, langLabel: langLabel, ComponentJutCatalog: ComponentJutCatalog, ComponentJutTitle: ComponentJutTitle, ComponentJutEpisodes: ComponentJutEpisodes, buildJutMenuItem: buildJutMenuItem, jutEpTitle: jutEpTitle, jutPosterUrl: jutPosterUrl, jutErrText: jutErrText };";
function qdlSource() {
  // CRLF→LF: после git checkout с autocrlf файл на диске может оказаться в CRLF — якорь с \n обязан находиться
  const src = fs.readFileSync(QDL, 'utf8').replace(/\r\n/g, '\n');
  if (!src.includes(QDL_TAIL)) throw new Error('qdl.js tail anchor not found — harness needs updating');
  return src.replace(QDL_TAIL, QDL_EXPORT);
}

/**
 * Load qdl.js into a real DOM (jsdom) with REAL jQuery (from the repo vendor) — for testing the
 * DOM/jQuery helpers (header notification icon, badge sync). opts: { bodyHtml, lampa }.
 * Returns { dom, w, doc, qdl, lampa, $ }.
 */
function loadQdlDom(opts) {
  opts = opts || {};
  const { JSDOM } = require('jsdom');
  const jquerySrc = fs.readFileSync(
    path.join(REPO, 'Modules', 'LampaWeb', 'widgets', 'lg', 'app', 'vender', 'jquery', 'jquery.js'), 'utf8');

  const dom = new JSDOM('<!DOCTYPE html><html><head></head><body>' + (opts.bodyHtml || '') + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/' });
  const w = dom.window;
  w.eval(jquerySrc);                       // real jQuery → window.$ / window.jQuery
  w.Lampa = opts.lampa || makeLampa();     // Reguest.silent is a no-op → pollNotifications does no network
  if (opts.fetch) w.fetch = opts.fetch;    // перехват POST-мутаций (post() в qdl.js ходит через fetch)
  w.appready = false;
  w.eval(qdlSource());                     // auto-start stripped; internal helpers exported to window.__qdl
  if (!w.__qdl) throw new Error('qdl.js did not export __qdl (jsdom)');
  return { dom, w, doc: w.document, qdl: w.__qdl, lampa: w.Lampa, $: w.$ };
}

module.exports = { loadQdl, loadQdlDom, loadLampaInit, makeLampa, makeDocument, makeStorage, makeEl, make$, deepAssign, qdlSource, REPO };
