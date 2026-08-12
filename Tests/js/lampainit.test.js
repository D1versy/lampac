'use strict';
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// ─────────────────────────── top-level seed (lines 122-130) ───────────────────────────

test('lampainit: top-level seed writes premium account_user into localStorage', () => {
  const { localStorage } = H.loadLampaInit();
  const raw = localStorage.getItem('account_user');
  assert.ok(raw, 'account_user should be set');
  const u = JSON.parse(raw);
  assert.strictEqual(u.id, 1);
  assert.strictEqual(typeof u.premium, 'number');
  // premium is a future timestamp (~10 years out)
  assert.ok(u.premium > Date.now());
});

test('lampainit: top-level seed removes developer_nopremium', () => {
  const ls = H.makeStorage();
  ls.setItem('developer_nopremium', 'true'); // pre-existing truthy value that would disable premium
  const { localStorage } = H.loadLampaInit({ localStorage: ls });
  assert.strictEqual(localStorage.getItem('developer_nopremium'), null);
});

test('lampainit: top-level seed removes developer_nopremium even when already absent (no throw)', () => {
  const { localStorage } = H.loadLampaInit();
  assert.strictEqual(localStorage.getItem('developer_nopremium'), null);
});

test('lampainit: top-level seed premium is roughly 3650 days ahead', () => {
  const before = Date.now();
  const { localStorage } = H.loadLampaInit();
  const after = Date.now();
  const u = JSON.parse(localStorage.getItem('account_user'));
  const tenYears = 3650 * 86400000;
  // premium == Date.now()+3650*86400000 captured at module-eval time, between before/after
  assert.ok(u.premium >= before + tenYears);
  assert.ok(u.premium <= after + tenYears);
});

test('lampainit: module exports lampainit_invc with the three lifecycle fns', () => {
  const { mod } = H.loadLampaInit();
  assert.strictEqual(typeof mod, 'object');
  assert.strictEqual(typeof mod.appload, 'function');
  assert.strictEqual(typeof mod.appready, 'function');
  assert.strictEqual(typeof mod.first_initiale, 'function');
});

// ─────────────────────────── appready() (lines 87-112) ───────────────────────────

test('lampainit: appready sets spoof account_user via Lampa.Storage.set', () => {
  const { mod, lampa } = H.loadLampaInit();
  mod.appready();
  const u = lampa.Storage.get('account_user');
  assert.ok(u);
  assert.strictEqual(u.id, 1);
  assert.ok(u.premium > Date.now());
  // it went through Storage.set (logged in _calls)
  const setCall = lampa.Storage._calls.find(c => c[0] === 'account_user');
  assert.ok(setCall, 'account_user should have been set via Storage.set');
  assert.strictEqual(setCall[1].id, 1);
});

test('lampainit: appready wires the empty-guard change listener', () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod } = H.loadLampaInit({ lampa });
  mod.appready();
  const changeFollow = followed.find(f => f[0] === 'change');
  assert.ok(changeFollow, 'should follow the "change" event');
  assert.strictEqual(typeof changeFollow[1], 'function');
});

test('lampainit: appready guard reinstalls when account_user emptied (empty string)', async () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  // clear the set log then simulate an "emptied" change event
  L.Storage._calls.length = 0;
  L.Storage.set('account_user', ''); // simulate wipe
  L.Storage._calls.length = 0;
  handler({ name: 'account_user', value: '' });
  // reinstall is scheduled via setTimeout(…, 0)
  await new Promise(r => setTimeout(r, 5));
  const reinstalled = L.Storage._calls.find(c => c[0] === 'account_user');
  assert.ok(reinstalled, 'guard should reinstall spoof user on empty value');
  assert.strictEqual(reinstalled[1].id, 1);
});

test('lampainit: appready guard reinstalls when value is object without id', async () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  L.Storage._calls.length = 0;
  handler({ name: 'account_user', value: {} }); // object, no .id → empty
  await new Promise(r => setTimeout(r, 5));
  const reinstalled = L.Storage._calls.find(c => c[0] === 'account_user');
  assert.ok(reinstalled, 'object without id counts as empty → reinstall');
});

test('lampainit: appready guard reinstalls when value is null', async () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  L.Storage._calls.length = 0;
  handler({ name: 'account_user', value: null });
  await new Promise(r => setTimeout(r, 5));
  assert.ok(L.Storage._calls.find(c => c[0] === 'account_user'));
});

test('lampainit: appready guard does NOT reinstall for non-empty user (avoids recursion)', async () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  L.Storage._calls.length = 0;
  handler({ name: 'account_user', value: { id: 1, premium: Date.now() + 1000 } });
  await new Promise(r => setTimeout(r, 5));
  assert.strictEqual(L.Storage._calls.length, 0, 'valid user should not trigger reinstall');
});

test('lampainit: appready guard ignores changes for other keys', async () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  L.Storage._calls.length = 0;
  handler({ name: 'some_other_key', value: '' });
  await new Promise(r => setTimeout(r, 5));
  assert.strictEqual(L.Storage._calls.length, 0, 'other keys ignored');
});

test('lampainit: appready guard tolerates undefined event', () => {
  const followed = [];
  const lampa = H.makeLampa({
    Storage: { listener: { follow: (evt, fn) => followed.push([evt, fn]) } },
  });
  const { mod } = H.loadLampaInit({ lampa });
  mod.appready();
  const handler = followed.find(f => f[0] === 'change')[1];
  assert.doesNotThrow(() => handler(undefined));
});

test('lampainit: appready swallows Storage.listener.follow throwing', () => {
  const lampa = H.makeLampa({
    Storage: { listener: { follow: () => { throw new Error('boom'); } } },
  });
  const { mod, lampa: L } = H.loadLampaInit({ lampa });
  assert.doesNotThrow(() => mod.appready());
  // the set still happened before the throwing follow
  assert.ok(L.Storage.get('account_user'));
});

// ─────────────────────────── appload() (lines 10-83) ───────────────────────────

test('lampainit: appload sets torrserver_url and torrserver_auth in Storage', () => {
  // с 1.7 torrserver_url = location.origin + '/ts' (прокси lampac, работает и извне через домен)
  const { mod, lampa } = H.loadLampaInit();
  mod.appload();
  assert.strictEqual(lampa.Storage.get('torrserver_url'), 'http://192.168.87.24:9118/ts');
  assert.strictEqual(lampa.Storage.get('torrserver_auth'), 'false'); // string 'false', not boolean
});

test('lampainit: appload torrserver_url следует за origin (извне — домен)', () => {
  const { mod, lampa } = H.loadLampaInit({ location: { origin: 'https://tv.d1versy.com:9443', hostname: 'tv.d1versy.com' } });
  mod.appload();
  assert.strictEqual(lampa.Storage.get('torrserver_url'), 'https://tv.d1versy.com:9443/ts');
});

test('lampainit: appload calls Lampa.Utils.putScriptAsync for qdl.js and music.js', () => {
  const scripts = [];
  const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
  const { mod } = H.loadLampaInit({ lampa });
  mod.appload();
  // 2.13: два вызова — наш qdl.js и music.js (модуль Music upstream, включён флипом манифеста)
  assert.strictEqual(scripts.length, 2);
  // NB: array is created inside the vm sandbox (different realm's Array),
  // so deepStrictEqual on the array object rejects it — compare contents instead.
  assert.strictEqual(scripts[0].length, 1);
  // URL carries the {version} cache-buster token (the server replaces it with a per-start stamp).
  assert.strictEqual(scripts[0][0], '{localhost}/qdl.js?v={version}');
  assert.strictEqual(scripts[1][0], '{localhost}/music.js?v={version}');
});

test('lampainit: appload calls Lampa.Lang.add with the neutral strings', () => {
  const added = [];
  const lampa = H.makeLampa({ Lang: { add: (obj) => added.push(obj) } });
  const { mod } = H.loadLampaInit({ lampa });
  mod.appload();
  assert.strictEqual(added.length, 1);
  const dict = added[0];
  assert.ok(dict.change_source_on_cub);
  assert.strictEqual(dict.change_source_on_cub.ru, 'Сменить источник');
  assert.ok(dict.extensions_from_cub);
  assert.ok(dict.plugins_load_from);
});

test('lampainit: appload survives Lampa.Lang.add throwing (try/catch)', () => {
  const lampa = H.makeLampa({ Lang: { add: () => { throw new Error('nope'); } } });
  const { mod } = H.loadLampaInit({ lampa });
  assert.doesNotThrow(() => mod.appload());
});

test('lampainit: appload injects <style id="qdl-hide-extras"> into document.head', () => {
  const doc = H.makeDocument();
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  // style registered via createElement proxy → byId map
  const st = doc.getElementById('qdl-hide-extras');
  assert.ok(st, 'style element should be registered by id');
  assert.strictEqual(st.tagName, 'STYLE');
  // and appended to head
  const inHead = doc.head._children.find(c => c.id === 'qdl-hide-extras');
  assert.ok(inHead, 'style should be appended to document.head');
  // sanity: css text contains the CUB-hiding rules
  assert.match(st.textContent, /qdl-hide-extras|display:none/);
  assert.match(st.textContent, /feed-head__info/);
});

// qdl 2.39: вход в настройки Lampa скрыт у ВСЕХ клиентов, открывает кука qdl_unlock=1
// (владелец сетит её скриптом в консоли браузера). Платформенных веток тут нет.
test('lampainit: без куки qdl_unlock прячет шестерёнку и пункт меню «Настройки»', () => {
  const doc = H.makeDocument();
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  const css = doc.getElementById('qdl-hide-extras').textContent;
  assert.match(css, /\.head__action\.open--settings\{display:none!important\}/);
  assert.match(css, /\.menu__item\[data-action="settings"\]\{display:none!important\}/);
  assert.match(css, /feed-head__info/, 'остальные правила на месте');
});

test('lampainit: с кукой qdl_unlock=1 настройки видны, прочие скрытия не трогаются', () => {
  const doc = H.makeDocument();
  doc.cookie = 'a=1; qdl_unlock=1; b=2';
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  const css = doc.getElementById('qdl-hide-extras').textContent;
  assert.ok(css.indexOf('open--settings') === -1, 'шестерёнка остаётся видимой');
  assert.ok(css.indexOf('[data-action="settings"]') === -1, 'пункт меню остаётся видимым');
  assert.match(css, /feed-head__info/);
  assert.match(css, /\.head__action\.open--profile/, 'безусловные скрытия CUB не зависят от куки');
});

test('lampainit: похожая кука не разблокирует настройки', () => {
  const doc = H.makeDocument();
  doc.cookie = 'xqdl_unlock=1; qdl_unlock=0';
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.match(doc.getElementById('qdl-hide-extras').textContent, /open--settings/);
});

test('lampainit: appload does not re-inject style when one already exists', () => {
  const doc = H.makeDocument();
  // pre-seed an existing style with that id
  const existing = H.makeEl({ tagName: 'STYLE', id: 'qdl-hide-extras' });
  doc._byId['qdl-hide-extras'] = existing;
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  // head should NOT have received a new style child
  const appended = doc.head._children.filter(c => c.id === 'qdl-hide-extras');
  assert.strictEqual(appended.length, 0, 'no new style appended when id already present');
});

test('lampainit: appload stripBrand rewrites " - CUB" head title to bare name', () => {
  const cubTitle = H.makeEl({ tagName: 'DIV', textContent: 'Movie - CUB' });
  const doc = H.makeDocument({ '.head__title': [cubTitle] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(cubTitle.textContent, 'Movie');
});

test('lampainit: appload stripBrand leaves a real " - Title" name unchanged', () => {
  const cubTitle = H.makeEl({ tagName: 'DIV', textContent: 'Movie - CUB' });
  const realTitle = H.makeEl({ tagName: 'DIV', textContent: 'Some - Title' });
  const doc = H.makeDocument({ '.head__title': [cubTitle, realTitle] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(cubTitle.textContent, 'Movie');
  assert.strictEqual(realTitle.textContent, 'Some - Title'); // untouched
});

test('lampainit: appload stripBrand strips case-insensitive " - cub" and trailing space', () => {
  const t1 = H.makeEl({ tagName: 'DIV', textContent: 'Film - cub' });
  const t2 = H.makeEl({ tagName: 'DIV', textContent: 'Film - CUB ' }); // trailing space
  const doc = H.makeDocument({ '.head__title': [t1, t2] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(t1.textContent, 'Film');
  assert.strictEqual(t2.textContent, 'Film');
});

test('lampainit: appload stripBrand handles en-dash / em-dash separators', () => {
  const enDash = H.makeEl({ tagName: 'DIV', textContent: 'Show – CUB' }); // – U+2013
  const emDash = H.makeEl({ tagName: 'DIV', textContent: 'Show — CUB' }); // — U+2014
  const doc = H.makeDocument({ '.head__title': [enDash, emDash] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(enDash.textContent, 'Show');
  assert.strictEqual(emDash.textContent, 'Show');
});

test('lampainit: appload stripBrand only strips CUB at the END', () => {
  // "CUB - Movie" — CUB is a prefix, not the " - CUB" suffix → unchanged
  const t = H.makeEl({ tagName: 'DIV', textContent: 'CUB - Movie' });
  const doc = H.makeDocument({ '.head__title': [t] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(t.textContent, 'CUB - Movie');
});

test('lampainit: appload hideLocked hides .selectbox-item ancestors of locks', () => {
  const item = H.makeEl({ tagName: 'DIV' });
  const lock = H.makeEl({ tagName: 'SPAN' });
  lock.closest = (sel) => (sel === '.selectbox-item' ? item : null);
  const doc = H.makeDocument({ '.selectbox-item__lock': [lock] });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(item.style.display, 'none');
});

test('lampainit: appload hides a navigation tab whose text is exactly "CUB"', () => {
  const cubTab = H.makeEl({ tagName: 'DIV', textContent: '  CUB  ' }); // trimmed → 'CUB'
  const otherTab = H.makeEl({ tagName: 'DIV', textContent: 'Home' });
  const navBar = H.makeEl({ tagName: 'DIV' });
  // hideCubTabs first gates on a .modal/.navigation-tabs container being present (perf), so the
  // mock must model the real DOM where the buttons live inside a .navigation-tabs container.
  const doc = H.makeDocument({
    '.navigation-tabs': [navBar],
    '.navigation-tabs__button': [cubTab, otherTab],
  });
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  assert.strictEqual(cubTab.style.display, 'none');
  assert.notStrictEqual(otherTab.style.display, 'none'); // other tab untouched
});

test('lampainit: appload wires a MutationObserver on document.body', () => {
  const observed = [];
  const lampa = H.makeLampa();
  const doc = H.makeDocument();
  const localStorage = H.makeStorage();
  // custom sandbox not exposed; instead assert appload does not throw with default MutationObserver
  const { mod } = H.loadLampaInit({ lampa, document: doc, localStorage });
  assert.doesNotThrow(() => mod.appload());
  // if MutationObserver wiring threw, the outer try/catch swallows it; smoke: style still injected
  assert.ok(doc.getElementById('qdl-hide-extras'));
});

test('lampainit: appload is idempotent across two calls (no throw, single style)', () => {
  const doc = H.makeDocument();
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  mod.appload();
  const styles = doc.head._children.filter(c => c.id === 'qdl-hide-extras');
  assert.strictEqual(styles.length, 1, 'second appload should not append a duplicate style');
});

// ─────────────────────────── top-level: локализация внешних источников (qdl 2.15) ───────────────────────────

test('lampainit 2.15: top-level форсит proxy_tmdb=true даже поверх сохранённого false', () => {
  const ls = H.makeStorage();
  ls.setItem('proxy_tmdb', 'false'); // старый клиент: lampainit.js по GeoIP(LAN)=null записал false
  const { localStorage } = H.loadLampaInit({ localStorage: ls });
  assert.strictEqual(localStorage.getItem('proxy_tmdb'), 'true');
});

test('lampainit 2.15: top-level гасит lampa_settings.mirrors и .geo (пробы зеркал/гео CUB)', () => {
  const settings = { disable_features: {} };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.strictEqual(sandbox.window.lampa_settings.mirrors, false);
  assert.strictEqual(sandbox.window.lampa_settings.geo, false);
  // соседний флаг из более раннего блока тоже на месте — блоки не мешают друг другу
  assert.strictEqual(sandbox.window.lampa_settings.disable_features.subscribe, true);
});

test('lampainit 2.15: без window.lampa_settings не падает, а proxy_tmdb всё равно форсится', () => {
  // отдельные try/catch: падение блока настроек не должно отменить форс proxy_tmdb
  const { localStorage } = H.loadLampaInit();
  assert.strictEqual(localStorage.getItem('proxy_tmdb'), 'true');
});

// ─────────────────────────── first_initiale() ───────────────────────────

test('lampainit: first_initiale exists and does not throw', () => {
  const { mod } = H.loadLampaInit();
  assert.strictEqual(typeof mod.first_initiale, 'function');
  assert.doesNotThrow(() => mod.first_initiale());
});
