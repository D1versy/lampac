'use strict';
// Платформенный блок D1Vision в lampainit-invc.js (ранний, до загрузки Lampa):
// детект UA-токена d1vision_<platform>/<ver> → d1vision_platform + форс андроидных
// ключей плеера для оболочек с нативным мостом (mac/ios/android/windows). См. claude/08-clients.md.
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const UA_BASE = 'Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
const FORCED = [['platform', 'android'], ['player', 'android'], ['player_torrent', 'android'], ['player_iptv', 'android'], ['internal_torrclient', 'true']];

function load(ua, ls) {
  return H.loadLampaInit({ navigator: { userAgent: ua }, localStorage: ls || H.makeStorage() });
}

function assertForced(localStorage) {
  for (const [k, v] of FORCED)
    assert.strictEqual(localStorage.getItem(k), v, `ключ ${k} должен быть форснут в ${v}`);
}

function assertNotForced(localStorage) {
  for (const [k] of FORCED)
    assert.strictEqual(localStorage.getItem(k), null, `ключ ${k} НЕ должен форситься`);
}

// ─────────────────────────── детект по UA-токену ───────────────────────────

test('platform: токен d1vision_mac → platform mac + андроидные форс-ключи', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client d1vision_mac/1.0-500`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'mac');
  assert.strictEqual(localStorage.getItem('d1vision_platform'), 'mac');
  assertForced(localStorage);
});

test('platform: токен d1vision_ios → platform ios + форс-ключи', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client d1vision_ios/1.0-500`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'ios');
  assertForced(localStorage);
});

test('platform: токен d1vision_android → platform android + форс-ключи', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client d1vision_android/1.4.2-522`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'android');
  assertForced(localStorage);
});

test('platform: токен d1vision_windows → platform windows + форс-ключи', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client d1vision_windows/1.0.0-500`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'windows');
  assert.strictEqual(localStorage.getItem('d1vision_platform'), 'windows');
  assertForced(localStorage);
});

test('platform: токен d1vision_tizen → platform tizen, ключи плеера НЕ форсятся', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} d1vision_tizen/1.0`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'tizen');
  assert.strictEqual(localStorage.getItem('d1vision_platform'), 'tizen');
  assertNotForced(localStorage);
});

test('platform: версия клиента из токена попадает в window.d1vision_client_version', () => {
  const { sandbox } = load(`${UA_BASE} lampa_client d1vision_mac/1.0-500`);
  assert.strictEqual(sandbox.window.d1vision_client_version, '1.0-500');
});

test('platform: токен матчится регистронезависимо', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client D1Vision_MAC/1.0-500`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'mac');
  assertForced(localStorage);
});

// ─────────────────────────── обратная совместимость ───────────────────────────

test('platform: старый бинарь (lampa_client без токена) → android + форс-ключи', () => {
  const { sandbox, localStorage } = load(`${UA_BASE} lampa_client`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'android');
  assertForced(localStorage);
});

test('platform: идемпотентность — ключи, уже форснутые старым бинарём, не меняются', () => {
  const ls = H.makeStorage();
  for (const [k, v] of FORCED) ls.setItem(k, v);   // клиентский форс-скрипт старого бинаря
  const { localStorage } = load(`${UA_BASE} lampa_client`, ls);
  assertForced(localStorage);
});

test('platform: обычный браузер (без токена и lampa_client) → web, ничего не форсится', () => {
  const { sandbox, localStorage } = load(`Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15`);
  assert.strictEqual(sandbox.window.d1vision_platform, 'web');
  assert.strictEqual(localStorage.getItem('d1vision_platform'), 'web');
  assertNotForced(localStorage);
});

// ─────────────────────────── взаимодействие с Tizen-loader ───────────────────────────

test('platform: посеянный loader-ом d1vision_platform=tizen уважается при UA без токена', () => {
  const ls = H.makeStorage();
  ls.setItem('d1vision_platform', 'tizen');        // сеет loader.js виджета ДО lampainit
  const { sandbox, localStorage } = load(`Mozilla/5.0 (SMART-TV; Linux; Tizen 5.5) AppleWebKit/537.36`, ls);
  assert.strictEqual(sandbox.window.d1vision_platform, 'tizen');
  assertNotForced(localStorage);
});

test('platform: UA-токен ПЕРЕБИВАЕТ ранее сохранённый d1vision_platform', () => {
  const ls = H.makeStorage();
  ls.setItem('d1vision_platform', 'web');          // остаток прошлой сессии
  const { sandbox } = load(`${UA_BASE} lampa_client d1vision_mac/1.0-500`, ls);
  assert.strictEqual(sandbox.window.d1vision_platform, 'mac');
});

test('platform: залипший d1vision_platform=mac НЕ форсит android в вебе (самоисцеление в web)', () => {
  // web-браузер без UA-токена и без lampa_client, но с чужим залипшим значением из localStorage:
  // из localStorage доверяем только 'tizen' → mac/ios/android игнорируются → web, ключи не форсятся.
  const ls = H.makeStorage();
  ls.setItem('d1vision_platform', 'mac');
  const { sandbox, localStorage } = load(`Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/120`, ls);
  assert.strictEqual(sandbox.window.d1vision_platform, 'web');
  assert.strictEqual(localStorage.getItem('d1vision_platform'), 'web');   // перезаписалось
  assertNotForced(localStorage);
});

// ─────────────────────────── бренд ───────────────────────────

test('brand: window.d1vision_brand по умолчанию D1Vision', () => {
  const { sandbox } = load(UA_BASE);
  assert.strictEqual(sandbox.window.d1vision_brand, 'D1Vision');
});

test('brand: appload ставит document.title = бренд (fetch в песочнице нет — не бросает)', () => {
  const doc = H.makeDocument();
  const { mod } = H.loadLampaInit({ document: doc, navigator: { userAgent: UA_BASE } });
  assert.doesNotThrow(() => mod.appload());        // fetch отсутствует → try/catch глотает
  assert.strictEqual(doc.title, 'D1Vision');
});
