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
    Activity: { push() {}, replace() {}, active: () => ({}), all: () => [], backward() {}, own: () => true },
    // qdl 2.61: наши экраны пишут «Историю просмотров» штатным Lampa.Favorite.add
    Favorite: { _added: [], add(where, card, limit) { L.Favorite._added.push({ where, card, limit }); } },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Template: { get: () => makeEl() },
    Scroll: function () { this.render = () => makeEl(); this.body = () => make$(); this.append = () => {}; this.minus = () => {}; this.update = () => {}; this.destroy = () => {}; },   // body() — jQuery-заглушка: компоненты зовут scroll.body().append(...)
    Lang: { add() {} },
    // qdl 2.83: догоняющая установка плагинов (lampainit_invc.catchUpPlugins) — реестр
    // расширений и загрузчик. Хранит список в памяти, как настоящий Lampa.Plugins.
    Plugins: {
      _list: [],
      _saves: 0,
      get() { return L.Plugins._list; },
      add(p) { L.Plugins._list.push(p); },
      save() { L.Plugins._saves++; },
    },
    Utils: { putScriptAsync() {}, _scripts: [], putScript(urls) { L.Utils._scripts.push(urls); }, hash: (s) => 'h' + String(s) },
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
  // qdl 2.89: транскод/удаление/хелс-чеки/раздел «D1Vision» гейтятся ПРАВОМ «действия»
  // (qdlManage() → qdlAllowed('manage')). Куки qdl_unlock=1 больше нет — она была вторым
  // ключом с 2.39 и убрана целиком по решению владельца.
  // Дефолт тест-окружения — «право есть», чтобы прежние тесты quickMenu видели все пункты;
  // состояние «без права» задаётся явно через opts.perms: {}.
  const doc = opts.document || makeDocument();
  const sandbox = {
    window: win,
    Lampa: opts.lampa || makeLampa(),
    document: doc,
    navigator: opts.navigator || { userAgent: '' },
    $: opts.$ || make$(),
    fetch: opts.fetch || (() => Promise.resolve({ json: () => Promise.resolve({}) })),
    console,
    setTimeout: opts.setTimeout || setTimeout,
    clearTimeout: opts.clearTimeout || clearTimeout,
    setInterval: opts.setInterval || setInterval,     // инъецируемы: poll-тесты дёргают тик руками
    clearInterval: opts.clearInterval || clearInterval,
  };
  // Подмена интринсиков песочницы (Date и прочее): setTimeout инъецируется выше, но часы живут
  // в глобалах контекста, и дотянуться до них снаружи иначе нельзя.
  Object.assign(sandbox, opts.sandboxExtra || {});
  sandbox.window.Lampa = sandbox.Lampa;
  vm.createContext(sandbox);
  vm.runInContext(src, sandbox, { filename: 'qdl.js' });
  if (!win.__qdl) throw new Error('qdl.js did not export __qdl');
  win.__qdl.setPerms(opts.perms !== undefined ? opts.perms : { live: true, rec: true, manage: true });
  return { qdl: win.__qdl, sandbox };
}

/**
 * Load lampainit-invc.js. Top-level side effects run on load (premium seed).
 * opts: { localStorage, lampa, document, navigator, windowExtra }
 * returns { mod: lampainit_invc, sandbox, localStorage, lampa }
 */
function loadLampaInit(opts) {
  opts = opts || {};
  // opts.host — подставить {localhost}, как это делает сервер (ApiController.LamInit).
  // Нужен там, где проверяется адрес, вычисленный ИЗ хоста запроса (загрузка плагина XSMART).
  let src = fs.readFileSync(LAMPAINIT, 'utf8');
  if (opts.host) src = src.split('{localhost}').join(opts.host);
  // opts.initiale — список плагинов, который сервер подставляет в {initiale} (ApiController.LamInit).
  // Без подстановки `var want = {initiale}` — это объектный литерал со свободной ссылкой, и
  // догоняющая установка молча уходит в catch. Умолчание — пустой список.
  src = src.split('{initiale}').join(JSON.stringify(opts.initiale || []));
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
const QDL_EXPORT = "window.__qdl = { focusBack: focusBack, markLast: markLast, onScreen: onScreen, esc: esc, names: names, slimCard: slimCard, cleanName: cleanName, videoFiles: videoFiles, baseName: baseName, isBrowser: isBrowser, isMobile: isMobile, mobileHls: mobileHls, streamUrl: streamUrl, posterUrl: posterUrl, notiPoster: notiPoster, PX1: PX1, dayLabel: dayLabel, dayTime: dayTime, notiBucket: notiBucket, notiIcon: notiIcon, ComponentNotifications: ComponentNotifications, badge: badge, chip: chip, getAudioPref: getAudioPref, setAudioPref: setAudioPref, updateNotiBadge: updateNotiBadge, ensureHeaderNoti: ensureHeaderNoti, buildHeaderNoti: buildHeaderNoti, ensureMenu: ensureMenu, dedupe: dedupe, normTitle: normTitle, isSerialName: isSerialName, isSeasonTail: isSeasonTail, findDownload: findDownload, addButton: addButton, canTranscode: canTranscode, quickMenu: quickMenu, onCardMenu: onCardMenu, catalogMenu: catalogMenu, cardIsTitle: cardIsTitle, cardType: cardType, cardParts: cardParts, partLabel: partLabel, partItem: partItem, withPart: withPart, watchToggle: watchToggle, deleteHashes: deleteHashes, epHeadSeason: epHeadSeason, gatePartial: gatePartial, livePartial: livePartial, livePct: livePct, epReady: epReady, epProgress: epProgress, epWaitNotice: epWaitNotice, DONE: DONE, pgSubscribe: pgSubscribe, pgUnsubscribe: pgUnsubscribe, pgGet: pgGet, pgFile: pgFile, pgApply: pgApply, pgKick: pgKick, pgStopAll: pgStopAll, pgReset: pgReset, pgBlockEnabled: pgBlockEnabled, setProgressConf: setProgressConf, pgState: function () { return { timer: _pgTimer, interval: _pgInterval, state: _pgState, conf: _pgConf, subs: _pgSubs, idleSince: _pgIdleSince }; }, pgSetIdleSince: function (t) { _pgIdleSince = t; }, watch: watch, openDownload: openDownload, chooseAndDownload: chooseAndDownload, pollTranscode: pollTranscode, dropAudioPref: dropAudioPref, rewriteCubUrl: rewriteCubUrl, rewriteWatchUrl: rewriteWatchUrl, isDmca: isDmca, whenDmca: whenDmca, setDmcaList: setDmcaList, noteCubDomain: noteCubDomain, itemActivity: itemActivity, groupDownloads: groupDownloads, gridOrder: gridOrder, commonPrefixTitle: commonPrefixTitle, buildCollectionPicker: buildCollectionPicker, itemTitle: itemTitle, addToCollection: addToCollection, collectionMenu: collectionMenu, renameCollection: renameCollection, chooseCover: chooseCover, healPoster: healPoster, saveMeta: saveMeta, releaseYear: releaseYear, matchKey: matchKey, cardMatches: cardMatches, ComponentDownloads: ComponentDownloads, touchCollections: touchCollections, stripExt: stripExt, epTimelineHash: epTimelineHash, epView: epView, epShort: epShort, chooseContinue: chooseContinue, sortEpisodes: sortEpisodes, pickContinue: pickContinue, epKindRank: epKindRank, epSeason: epSeason, firstUnwatched: firstUnwatched, jutBucket: jutBucket, buildPlaylist: buildPlaylist, chooseEpisode: chooseEpisode, watchByHash: watchByHash, ComponentEpisodes: ComponentEpisodes, epMark: epMark, epMeta: epMeta, epNumber: epNumber, fixSelectHeight: fixSelectHeight, initSelectFix: initSelectFix, liveSize: liveSize, ComponentCard: ComponentCard, ComponentNotifications: ComponentNotifications, ComponentLive: ComponentLive, ComponentLiveWatch: ComponentLiveWatch, ComponentLiveDetect: ComponentLiveDetect, ComponentRecFeed: ComponentRecFeed, liveVideoOn: liveVideoOn, liveVideoGlobal: liveVideoGlobal, liveVideoSet: liveVideoSet, ensureLiveDetectBtn: ensureLiveDetectBtn, liveDetectVisibility: liveDetectVisibility, liveHlsReady: liveHlsReady, liveMakePlayer: liveMakePlayer, liveDayName: liveDayName, LIVE_MAX_PLAYERS: LIVE_MAX_PLAYERS, LIVE_GUARD: LIVE_GUARD, setLiveGuard: function (o) { for (var k in o) LIVE_GUARD[k] = o[k]; }, ComponentLiveCamera: ComponentLiveCamera, startTranscode: startTranscode, addContinueButton: addContinueButton, mergedVideoFiles: mergedVideoFiles, srcHash: srcHash, fetchEpisodes: fetchEpisodes, epTimelineKey: epTimelineKey, pickTimeline: pickTimeline, shortDate: shortDate, torrentSubtitle: torrentSubtitle, openNotification: openNotification, pollNotifications: pollNotifications, mirrorParse: mirrorParse, mirrorValidRoad: mirrorValidRoad, mirrorRoad: mirrorRoad, mirrorMerge: mirrorMerge, mirrorPrune: mirrorPrune, mirrorHasKV: mirrorHasKV, mirrorStart: mirrorStart, mirrorRead: mirrorRead, mirrorWrite: mirrorWrite, initTimelineMirror: initTimelineMirror, onTimelineUpdate: onTimelineUpdate, tlBucket: tlBucket, setTlBucket: setTlBucket, clearTlBucket: clearTlBucket, initTimecodeBridge: initTimecodeBridge, rawPlay: rawPlay, warmup: warmup, prewarmForCard: prewarmForCard, warmupNext: warmupNext, dropEpCache: dropEpCache, audioLang: audioLang, audioLangs: audioLangs, getLangPref: getLangPref, setLangPref: setLangPref, filterByLang: filterByLang, langLabel: langLabel, ComponentJutCatalog: ComponentJutCatalog, ComponentJutTitle: ComponentJutTitle, ComponentJutEpisodes: ComponentJutEpisodes, ComponentJutSearch: ComponentJutSearch, jutUseNativeInput: jutUseNativeInput, qdlUid: qdlUid, withUid: withUid, req: req, liveWatchPlay: liveWatchPlay, liveWatchPlayIOS: liveWatchPlayIOS, livePlayDay: livePlayDay, liveWarmDay: liveWarmDay, jutAutopilot: jutAutopilot, jutAutopilotPaint: jutAutopilotPaint, jutAutopilotVisibility: jutAutopilotVisibility, ensureJutAutopilot: ensureJutAutopilot, initJutAutopilot: initJutAutopilot, initJutSegmentsPrefetch: initJutSegmentsPrefetch, jutPlay: jutPlay, jutTokOf: jutTokOf, buildJutMenuItem: buildJutMenuItem, jutEpTitle: jutEpTitle, jutPosterUrl: jutPosterUrl, jutErrText: jutErrText, jutMode: jutMode, jutWatchSet: jutWatchSet, jutWatchMenuCard: jutWatchMenuCard, isXsmart: isXsmart, xsMode: xsMode, xsCanWatch: xsCanWatch, xsWatchSet: xsWatchSet, seasonWaitFrom: seasonWaitFrom, canSeasonWait: canSeasonWait, seasonWaitToggle: seasonWaitToggle, openXsmartTitle: openXsmartTitle, notiBucket: notiBucket, openJutTitle: openJutTitle, JUT_MODE_LABEL: JUT_MODE_LABEL, orderButtons: orderButtons, ensureOnlineButton: ensureOnlineButton, buttonRank: buttonRank, qdlManage: qdlManage, d1vRow: d1vRow, renderD1Vision: renderD1Vision, registerD1VisionSettings: registerD1VisionSettings, applySettingsLock: applySettingsLock, healthRow: healthRow, renderHealth: renderHealth, registerHealthSettings: registerHealthSettings, healthSummary: healthSummary, healthSummaryRow: healthSummaryRow, noteHistory: noteHistory, historyCard: historyCard, jutHistoryCard: jutHistoryCard, jutSlugFromCardId: jutSlugFromCardId, initHistoryRouting: initHistoryRouting, activityCard: activityCard, qdlAllowed: qdlAllowed, loadFeatures: loadFeatures, denySection: denySection, MENU_ORDER: MENU_ORDER, swrGate: swrGate, swrKey: swrKey, swrOnLine: swrOnLine, swrOnRevalidate: swrOnRevalidate, swrChanged: swrChanged, swrIds: swrIds, swrBusyLine: swrBusyLine, swrBusyScreen: swrBusyScreen, swrCardStyle: swrCardStyle, swrRebuild: swrRebuild, swrFlush: swrFlush, initSwr: initSwr, swrState: function () { return { lines: swrLines, pending: swrPending, last: swrLast, budget: swrBudget }; }, swrReset: function () { swrLines.length = 0; swrPending = {}; swrLast = {}; swrBudget.length = 0; }, gridOff: gridOff, gridKey: gridKey, gridMark: gridMark, gridBuild: gridBuild, gridNext: gridNext, gridFilled: gridFilled, gridAlive: gridAlive, gridPumpLater: gridPumpLater, gridPump: gridPump, initGridDedup: initGridDedup, gridConf: function () { return { max: GRID_PUMP_MAX, ms: GRID_PUMP_MS, cap: GRID_SEEN_CAP }; }, setPerms: function (p) { qdlPerms = p; }, setCard: function (c) { qdlCard = c; } };";
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
  // Таймеры инъецируемы и здесь (как в loadQdl): у поллера прогресса живой setInterval держал бы
  // процесс node --test открытым, а тик всё равно надо дёргать руками.
  if (opts.setInterval) w.setInterval = opts.setInterval;
  if (opts.clearInterval) w.clearInterval = opts.clearInterval;
  w.appready = false;
  w.eval(qdlSource());                     // auto-start stripped; internal helpers exported to window.__qdl
  if (!w.__qdl) throw new Error('qdl.js did not export __qdl (jsdom)');
  w.__qdl.setPerms(opts.perms !== undefined ? opts.perms : { live: true, rec: true, manage: true });   // дефолт «право есть» (см. loadQdl)
  return { dom, w, doc: w.document, qdl: w.__qdl, lampa: w.Lampa, $: w.$ };
}

module.exports = { loadQdl, loadQdlDom, loadLampaInit, makeLampa, makeDocument, makeStorage, makeEl, make$, deepAssign, qdlSource, REPO };
