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

test('lampainit: appload calls Lampa.Utils.putScriptAsync for qdl.js, music.js and xsmart', () => {
  const scripts = [];
  const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
  const { mod } = H.loadLampaInit({ lampa });
  mod.appload();
  // 2.13: qdl.js + music.js (модуль Music upstream, включён флипом манифеста);
  // 2.68: третьим — плагин раздела XSMART, его отдаёт отдельный контейнер xsmart-proxy.
  assert.strictEqual(scripts.length, 3);
  // NB: array is created inside the vm sandbox (different realm's Array),
  // so deepStrictEqual on the array object rejects it — compare contents instead.
  assert.strictEqual(scripts[0].length, 1);
  // URL carries the {version} cache-buster token (the server replaces it with a per-start stamp).
  assert.strictEqual(scripts[0][0], '{localhost}/qdl.js?v={version}');
  assert.strictEqual(scripts[1][0], '{localhost}/music.js?v={version}');
  assert.ok(/\/xsmart\/xsmart\.js\?v=\{version\}$/.test(scripts[2][0]), scripts[2][0]);
});

// 🔴 Адрес контейнера XSMART вычисляется ИЗ хоста запроса, и обе ветки обязаны быть верными:
// дома клиент идёт на отдельный порт 9140, а снаружи /xsmart/* разводит Caddy на том же
// origin. Ошибка в любой из веток = раздел не грузится ровно на половине клиентов, причём
// на второй половине всё выглядит исправным.
test('lampainit 2.68: в LAN плагин XSMART грузится с порта 9140', () => {
  const scripts = [];
  const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
  const { mod } = H.loadLampaInit({ lampa, host: 'http://192.168.87.24:9118' });
  mod.appload();
  assert.strictEqual(scripts[2][0], 'http://192.168.87.24:9140/xsmart/xsmart.js?v={version}');
});

test('lampainit 2.68: снаружи плагин XSMART грузится с того же origin (через Caddy)', () => {
  const scripts = [];
  const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
  const { mod } = H.loadLampaInit({ lampa, host: 'https://tv.d1versy.com:9443' });
  mod.appload();
  assert.strictEqual(scripts[2][0], 'https://tv.d1versy.com:9443/xsmart/xsmart.js?v={version}');
});

// 🔴 Развилка решается по АДРЕСУ, а не по схеме: иначе внешний вход держался бы на том,
// что lampac видит запрос как https (X-Forwarded-Proto → KnownProxies → UseForwardedHeaders).
// Порвись цепочка — внешние клиенты молча ушли бы на http://…:9140 (мёртвый адрес плюс
// mixed content), и дома при этом всё было бы зелёным.
test('lampainit 2.69: неизвестный хост остаётся тем же origin (fail-safe, без :9140)', () => {
  const scripts = [];
  const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
  const { mod } = H.loadLampaInit({ lampa, host: 'http://tv.d1versy.com:9443' });
  mod.appload();
  assert.strictEqual(scripts[2][0], 'http://tv.d1versy.com:9443/xsmart/xsmart.js?v={version}');
});

test('lampainit 2.69: домашние адреса всех приватных диапазонов уходят на :9140', () => {
  for (const host of ['http://192.168.87.24:9118', 'http://10.0.0.5:9118',
    'http://172.16.4.2:9118', 'http://localhost:9118', 'http://127.0.0.1:9118']) {
    const scripts = [];
    const lampa = H.makeLampa({ Utils: { putScriptAsync: (a) => scripts.push(a) } });
    const { mod } = H.loadLampaInit({ lampa, host });
    mod.appload();
    const expected = host.replace(/:\d+$/, '') + ':9140/xsmart/xsmart.js?v={version}';
    assert.strictEqual(scripts[2][0], expected, host);
  }
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
//
// 🔴 2.67: правила замка переехали из общего #qdl-hide-extras в ОТДЕЛЬНЫЙ узел #qdl-hide-settings.
// Причина ровно одна: из склеенной строки правило потом не вынуть, а вынимать его теперь надо —
// когда с сервера приезжает право «Управление», узел целиком снимает qdl.js (applySettingsLock).
// Поэтому здесь проверяем ДВА разных факта: узел замка (есть/нет) и неприкосновенность общего блока.

/** Прогон appload с заданной кукой → {doc, lock, extras}. */
function appload(cookie) {
  const doc = H.makeDocument();
  if (cookie !== undefined) doc.cookie = cookie;
  const { mod } = H.loadLampaInit({ document: doc });
  mod.appload();
  return {
    doc,
    lock: doc.getElementById('qdl-hide-settings'),
    extras: doc.getElementById('qdl-hide-extras').textContent,
  };
}

test('lampainit: без ключа появляется отдельный узел #qdl-hide-settings с обоими правилами', () => {
  const { doc, lock, extras } = appload();
  assert.ok(lock, 'узел замка обязан создаться СИНХРОННО — иначе шестерёнка моргнёт до приезда прав');
  assert.strictEqual(lock.tagName, 'STYLE');
  assert.ok(doc.head._children.some((c) => c.id === 'qdl-hide-settings'), 'и попасть в head');
  assert.match(lock.textContent, /\.head__action\.open--settings\{display:none!important\}/);
  assert.match(lock.textContent, /\.menu__item\[data-action="settings"\]/);
  assert.match(lock.textContent, /\.menu__item\[data-action="console"\]/);
  // 🔴 Плитка «Хелс-чеки» — тем же замком: Lampa.SettingsApi снять компонент не умеет,
  // поэтому при отзыве права раздел остаётся зарегистрированным и без этого правила доживал бы
  // до перезапуска (регрессию поймал боевой permsgate на фазе «отозвали»).
  assert.match(lock.textContent, /\[data-component="qdl_health"\]\{display:none!important\}/,
    'замок обязан прятать и плитку раздела «Хелс-чеки»');
  // 🔴 и ни одного из этих правил не должно остаться в общем блоке: снять их оттуда нечем
  assert.ok(extras.indexOf('open--settings') === -1, 'замок не подмешивается в #qdl-hide-extras');
  assert.ok(extras.indexOf('[data-action="settings"]') === -1, 'замок не подмешивается в #qdl-hide-extras');
  assert.ok(extras.indexOf('[data-action="console"]') === -1, 'замок не подмешивается в #qdl-hide-extras');
});

test('lampainit: замок закрывает и нижнюю панель телефона (qdl 2.84)', () => {
  // 🔴 Дыра до 2.84: при Platform.screen('mobile') Lampa рисует СВОЮ нижнюю панель, её кнопка
  // зовёт Controller.toggle('settings') напрямую — мимо шестерёнки и мимо пункта левого меню.
  // То есть с телефона настройки Lampa открывались в обход замка.
  const { lock } = appload();
  assert.match(lock.textContent, /\.navigation-bar__item\[data-action="settings"\]\{display:none!important\}/,
    'без этого правила замок обходится с телефона');
});

test('lampainit: общий блок прячет мёртвую «Трансляцию» и убранные пункты меню (qdl 2.84)', () => {
  // Фолбэк к AppPatch: при смене tree бандла якорь тихо не находится, и без CSS иконка/пункты
  // вернулись бы у всех клиентов.
  const { extras } = appload();
  assert.ok(extras.indexOf('.head__action.open--broadcast{display:none!important}') !== -1,
    'иконка «Трансляции» — фолбэк к патчу broadcast');
  assert.ok(extras.indexOf('[data-action="mytorrents"]') !== -1, 'фолбэк к патчу menu-items');
  assert.ok(extras.indexOf('[data-action="myperson"]') !== -1, 'фолбэк к флагу disable_features.persons');
});

test('lampainit: с кукой qdl_unlock=1 узла замка нет вовсе, а общий блок от куки не зависит', () => {
  const withKey = appload('a=1; qdl_unlock=1; b=2');
  assert.strictEqual(withKey.lock, null, 'с ключом замка не должно быть в принципе');
  assert.ok(!withKey.doc.head._children.some((c) => c.id === 'qdl-hide-settings'));

  // 🔴 главный инвариант переезда: содержимое #qdl-hide-extras теперь ОДИНАКОВО с ключом и без.
  // Пока правила замка жили строкой внутри него, эти две строки отличались — и снять замок,
  // не тронув скрытия CUB, было невозможно.
  assert.strictEqual(withKey.extras, appload('').extras, 'общий блок обязан быть побайтово тем же');

  // прочие скрытия целы — переезд не должен был унести соседей
  for (const rule of [
    '.feed-head__info',
    '.head__action.open--profile',
    '[data-component="account"]',
    '.menu__item[data-action="about"]',
    '.account-modal-split__text',
    '.selectbox-item:has(.selectbox-item__lock)',
    '.extensions__cub',
    '.button--subscribe',
    '.head__action.notice--icon',
    '.menu__item[data-action="relise"]',
  ]) assert.ok(withKey.extras.indexOf(rule) !== -1, 'потерялось скрытие: ' + rule);
});

test('lampainit: похожая кука замок не открывает', () => {
  for (const cookie of ['xqdl_unlock=1', 'qdl_unlock=0', 'qdl_unlock=', 'xqdl_unlock=1; qdl_unlock=0']) {
    const { lock } = appload(cookie);
    assert.ok(lock, 'кука «' + cookie + '» не должна открывать настройки');
    assert.match(lock.textContent, /open--settings/);
  }
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

// ─────────────────────── 2.52: первый запуск сразу на русском ───────────────────────
// Гейт бандла: if (localStorage.getItem('language') || !lampa_settings.lang_use) — грузим
// приложение, иначе показываем выбор языка и НЕ стартуем вовсе.

test('lampainit 2.52: на чистом клиенте сеет русский язык парой ключей', () => {
  const { localStorage } = H.loadLampaInit();
  // голая строка, а не JSON: Storage.set оборачивает только объекты и массивы
  assert.strictEqual(localStorage.getItem('language'), 'ru');
  assert.strictEqual(localStorage.getItem('tmdb_lang'), 'ru');
});

test('lampainit 2.52: выбранный руками язык не перетирается', () => {
  // ⚠️ Посев условный (как screensaver, а не как proxy_tmdb): иначе смена языка
  // в настройках не пережила бы перезагрузку страницы.
  const ls = H.makeStorage();
  ls.setItem('language', 'en');
  ls.setItem('tmdb_lang', 'en');
  const { localStorage } = H.loadLampaInit({ localStorage: ls });
  assert.strictEqual(localStorage.getItem('language'), 'en');
  assert.strictEqual(localStorage.getItem('tmdb_lang'), 'en');
});

test('lampainit 2.52: пункт смены языка не отключается (lang_use не трогаем)', () => {
  // lang_use=false убрал бы и сам выбор из «Настройки → Интерфейс» — владелец просил
  // убрать ЭКРАН при первой установке, а не отнять возможность сменить язык.
  const settings = { disable_features: {} };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.notStrictEqual(sandbox.window.lampa_settings.lang_use, false);
});

// ─────────────────────────── first_initiale() ───────────────────────────

test('lampainit: first_initiale exists and does not throw', () => {
  const { mod } = H.loadLampaInit();
  assert.strictEqual(typeof mod.first_initiale, 'function');
  assert.doesNotThrow(() => mod.first_initiale());
});
