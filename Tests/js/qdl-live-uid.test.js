'use strict';
// qdl 2.56: эфир камер в НАТИВНОМ плеере.
//
// 🔴 Инвариант, который уже один раз сломался и стоил «Не удалось воспроизвести» на Android TV,
// маке и Windows (айфон при этом играл — у него своя ветка liveWatchPlayIOS, где withUid стоял):
// ссылка, уходящая в нативный плеер, обязана нести uid устройства В САМОМ URL. VLC/ExoPlayer не
// несут ни cookie, ни заголовков, а с qdl 2.54 все роуты qdl/live/* гейтятся правами по uid и без
// него отдают пустой 404.
//
// Тесты держат обе стороны: поведение (что реально уходит в Lampa.Player.play) и статический
// запрет на «голый» API + '/qdl/live/...' в исходнике — новая точка входа не должна повторить
// ту же забывчивость.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const API = '{localhost}';

/** Плагин с перехваченным /qdl/live/watch/start и записью того, что ушло в плеер. */
function boot(reply) {
  const played = [];
  const lampa = H.makeLampa({
    Player: { play: (item) => played.push(item), playlist: () => {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        if (url.indexOf('/qdl/live/watch/start') === -1) return;
        ok(reply);
      };
    },
  });
  lampa.Storage.set('lampac_unic_id', '7kfrxzfr');   // тот самый Android TV из реестра прав
  const ctx = H.loadQdl({ lampa });
  return Object.assign(ctx, { played, lampa });
}

test('эфир: URL плейлиста уходит в нативный плеер С uid — иначе гейт прав отдаёт 404', () => {
  const { qdl, played } = boot({ ready: true, running: true, path: '/qdl/live/watch/hls/1/index.m3u8' });

  qdl.liveWatchPlay({ id: 1, name: 'balkon' });

  assert.strictEqual(played.length, 1, 'плеер должен получить ровно один элемент');
  assert.ok(/[?&]uid=7kfrxzfr(&|$)/.test(played[0].url), 'в URL обязан быть uid: ' + played[0].url);
  assert.ok(played[0].url.indexOf('/qdl/live/watch/hls/1/index.m3u8') > 0, 'путь эфира сохранён');
});

test('эфир: без uid параметр не дописывается — веб-клиент в локалке ходит как ходил', () => {
  const played = [];
  const lampa = H.makeLampa({
    Player: { play: (item) => played.push(item), playlist: () => {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        if (url.indexOf('/qdl/live/watch/start') === -1) return;
        ok({ ready: true, running: true, path: '/qdl/live/watch/hls/3/index.m3u8' });
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa });

  qdl.liveWatchPlay({ id: 3, name: 'Garage 2' });

  assert.strictEqual(played.length, 1);
  assert.strictEqual(played[0].url.indexOf('uid='), -1, 'пустой uid не должен давать uid=: ' + played[0].url);
});

test('эфир: камера не готова — в плеер не уходит ничего', () => {
  const { qdl, played } = boot({ ready: false, running: false, path: null });

  qdl.liveWatchPlay({ id: 6, name: 'Vlad-MacBook-Recorder' });

  assert.strictEqual(played.length, 0, 'офлайн-камера не должна открывать плеер');
});

test('статический запрет: ни одна ссылка на qdl/live/* не строится мимо withUid', () => {
  const src = H.qdlSource();

  // Ищем конкатенации вида API + '/qdl/live/...' и смотрим, есть ли открывающий withUid( слева.
  const bad = [];
  const rx = /API\s*\+\s*'\/qdl\/live\//g;
  let m;
  while ((m = rx.exec(src)) !== null) {
    const line = src.slice(src.lastIndexOf('\n', m.index) + 1, src.indexOf('\n', m.index));
    // req() и post() дописывают uid сами — обе первой строкой зовут withUid (post — с 2.67,
    // когда гейт «действий» начал читать uid ТОЛЬКО из query). Это законные пути;
    // всё остальное обязано быть в withUid().
    if (/withUid\s*\(/.test(line) || /\breq\s*\(/.test(line) || /\bpost\s*\(/.test(line)) continue;
    bad.push(line.trim());
  }

  assert.deepStrictEqual(bad, [], 'эти ссылки уйдут без uid и получат 404:\n' + bad.join('\n'));
});

test('статический запрет: путь из ответа сервера (st.path) тоже обёрнут', () => {
  const src = H.qdlSource();
  const bad = [];
  const rx = /API\s*\+\s*(?:st|r|info)\.(?:path|url)/g;
  let m;
  while ((m = rx.exec(src)) !== null) {
    const line = src.slice(src.lastIndexOf('\n', m.index) + 1, src.indexOf('\n', m.index));
    if (/withUid\s*\(/.test(line) || /\breq\s*\(/.test(line)) continue;
    bad.push(line.trim());
  }

  assert.deepStrictEqual(bad, [], 'серверный путь уйдёт в плеер без uid:\n' + bad.join('\n'));
});
