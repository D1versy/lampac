'use strict';
// Идентификатор устройства в запросах к нашему серверу: на нём держится раздельная
// история jut.su (каждому клиенту — своя выдача экрана поиска).
//
// Два инварианта, которые легко сломать и трудно заметить:
//  • uid дописывается ТОЛЬКО к нашим URL — тот же req() ходит и в /cub/-прокси, который
//    склеивает upstream-адрес вместе с нашей строкой запроса дословно;
//  • URL текущей серии в item.url и в элементе плейлиста обязан совпадать ПОСИМВОЛЬНО —
//    Android ищет текущий элемент сравнением строк, расхождение молча заиграет первую серию.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// В плагине адрес сервера — плейсхолдер, который подставляется при отдаче файла;
// в тестах он остаётся как есть, и гейт «наш/чужой» сравнивает именно с ним.
const API = '{localhost}';

function boot(uid, opts) {
  opts = opts || {};
  const lampa = H.makeLampa();
  if (uid !== undefined && uid !== null) lampa.Storage.set('lampac_unic_id', uid);
  return H.loadQdl(Object.assign({ lampa }, opts));
}

test('uid берётся из канонического ключа устройства', () => {
  const { qdl } = boot('da11111');
  assert.strictEqual(qdl.qdlUid(), 'da11111');
});

test('числовой uid не превращается в мусор', () => {
  // Storage.get отдаёт чисто цифровую строку ЧИСЛОМ (ветка /^\d+$/) — без String()
  // конкатенация дала бы неверный параметр.
  const { qdl } = boot(12345678);
  assert.strictEqual(qdl.qdlUid(), '12345678');
});

test('без Lampa.Storage uid берётся из нативного KV', () => {
  const lampa = H.makeLampa();
  const { qdl } = H.loadQdl({
    lampa,
    windowExtra: { AndroidJS: { get: (k) => (k === 'qdl_device_uid' ? 'dnative1' : '') } },
  });
  assert.strictEqual(qdl.qdlUid(), 'dnative1');
});

test('нет uid — параметр не дописывается вовсе', () => {
  const { qdl } = boot('');
  assert.strictEqual(qdl.withUid(API + '/qdl/jut/recent?limit=50'), API + '/qdl/jut/recent?limit=50');
});

test('uid дописывается к нашим адресам, с учётом уже имеющейся строки запроса', () => {
  const { qdl } = boot('da11111');
  assert.strictEqual(qdl.withUid(API + '/qdl/jut/recent?limit=50'),
                     API + '/qdl/jut/recent?limit=50&uid=da11111');
  assert.strictEqual(qdl.withUid(API + '/qdl/health'), API + '/qdl/health?uid=da11111');
});

test('🔴 к чужим адресам uid не дописывается', () => {
  // /cub/-прокси склеивает upstream-URL с нашей строкой запроса дословно — параметр уехал бы наружу.
  const { qdl } = boot('da11111');
  const foreign = 'http://cub.rip/api/checker?x=1';
  assert.strictEqual(qdl.withUid(foreign), foreign);
});

test('повторный вызов не дублирует параметр', () => {
  const { qdl } = boot('da11111');
  const once = qdl.withUid(API + '/qdl/jut/recent');
  assert.strictEqual(qdl.withUid(once), once);
});

test('req() помечает запрос сам — вызывающим ничего не нужно помнить', () => {
  const lampa = H.makeLampa();
  lampa.Storage.set('lampac_unic_id', 'da11111');
  const seen = [];
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => { seen.push(url); ok({ ok: true }); };
  };

  const { qdl } = H.loadQdl({ lampa });
  qdl.req(API + '/qdl/jut/recent?limit=50', () => {}, () => {});

  assert.strictEqual(seen.length, 1);
  assert.ok(seen[0].includes('uid=da11111'), 'uid обязан уехать вместе с запросом');
});

// ─────────────────── URL потока: его открывает нативный плеер ───────────────────

const ITEMS = [
  { kind: 'episode', season: 1, ep: 1, key: 's1e1', tok: 'TOK1' },
  { kind: 'episode', season: 1, ep: 2, key: 's1e2', tok: 'TOK2' },
];

function play(uid) {
  const lampa = H.makeLampa();
  lampa.Storage.set('lampac_unic_id', uid);
  lampa.Storage.set('qdl_jut_autopilot', true);

  const seen = {};
  lampa.Player.play = (d) => { seen.data = JSON.parse(JSON.stringify(d)); };
  lampa.Player.playlist = () => {};
  lampa.Player.listener = { follow() {} };
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => {
      if (String(url).includes('/qdl/jut/resolve'))
        ok({ ok: true, url: '/qdl/jut/stream?t=TOK1', segments: { skip: [] } });
    };
  };

  const { qdl } = H.loadQdlDom({ bodyHtml: '<div class="head"></div>', lampa });
  qdl.jutPlay('spy-family', ITEMS[0], 'Spy x Family', ITEMS);
  return seen.data;
}

test('uid уезжает в URL потока — иначе просмотр не привязать к устройству', () => {
  // Поток открывает нативный плеер (VLC/ExoPlayer): ни заголовок, ни cookie туда не подложить,
  // а «смотрел» сервер пишет именно по факту байтов через /qdl/jut/stream.
  const data = play('da11111');
  assert.ok(data.url.includes('uid=da11111'));
  assert.ok(data.playlist[1].url.includes('uid=da11111'), 'соседние серии тоже');
});

test('🔴 URL текущей серии в item и в плейлисте совпадают посимвольно', () => {
  // Android ищет текущий элемент как playlist.indexOfFirst { it.url == videoUrl }.
  // Расхождение хоть на символ → индекс 0 → вместо выбранной серии играет первая в сезоне.
  const data = play('da11111');
  const hit = data.playlist.filter((p) => p.url === data.url);
  assert.strictEqual(hit.length, 1, 'ровно один элемент плейлиста равен item.url');
  assert.strictEqual(data.playlist[0].url, data.url, 'и это именно текущая серия');
});

test('без uid ссылки остаются прежними (старый клиент, браузер до генерации)', () => {
  const data = play('');
  assert.ok(!data.url.includes('uid='));
  assert.strictEqual(data.playlist[0].url, data.url);
});

// ── сеятель uid в lampainit-invc.js (до загрузки Lampa) ───────────────────
//
// qdl 2.61: uid обязан существовать ДО плагинов синка. У нативных оболочек канон живёт в KV
// (qdl_device_uid), а браузер и Tizen раньше ждали, пока uid заведёт первый дотянувшийся
// timecode.js — но bookmark.js грузится тем же putScriptAsync-массивом, порядок не гарантирован,
// а запрос без uid сервер отвергает МОЛЧА (пустой user_uid → success:false с кодом 200).

function seed(opts) {
  const localStorage = (opts && opts.localStorage) || H.makeStorage();
  H.loadLampaInit({ localStorage, windowExtra: (opts && opts.windowExtra) || {} });
  return localStorage.getItem('lampac_unic_id');
}

function kv(store) {
  return { get: (k) => (k in store ? store[k] : null), set: (k, v) => { store[k] = String(v); } };
}

test('браузер без моста получает uid сам', () => {
  const uid = seed();
  assert.ok(uid, 'uid должен быть заведён');
  // первый символ — буква: Lampa.Storage.get парсит чисто цифровую строку как number
  assert.match(uid, /^d[a-z0-9]{7}$/);
});

test('существующий uid браузера не перетирается', () => {
  const ls = H.makeStorage();
  ls.setItem('lampac_unic_id', 'dexisting');
  assert.strictEqual(seed({ localStorage: ls }), 'dexisting');
});

test('с мостом канон берётся из нативного KV, а не генерится заново', () => {
  const store = { qdl_device_uid: 'dnative1' };
  const ls = H.makeStorage();
  ls.setItem('lampac_unic_id', 'dorigin1');   // per-origin значение обязано уступить канону

  assert.strictEqual(seed({ localStorage: ls, windowExtra: { AndroidJS: kv(store) } }), 'dnative1');
});

test('пустой KV усыновляет uid этого origin', () => {
  const store = {};
  const ls = H.makeStorage();
  ls.setItem('lampac_unic_id', 'dorigin1');

  assert.strictEqual(seed({ localStorage: ls, windowExtra: { AndroidJS: kv(store) } }), 'dorigin1');
  assert.strictEqual(store.qdl_device_uid, 'dorigin1', 'и становится каноном для всех origin');
});

test('бросающий AndroidJS.set не мешает старту (поток «Мигрировать» на Android)', () => {
  const bridge = { get: () => null, set: () => { throw new Error('dumped'); } };
  assert.doesNotThrow(() => seed({ windowExtra: { AndroidJS: bridge } }));
});
