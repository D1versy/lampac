'use strict';
// Самолечение постеров (healPoster): у загрузки есть мета, но постер на сервере не скачался
// (has_poster=false при живом poster_path) → грид дёргает POST /qdl/save только с poster_url
// (без card — мету не перезаписываем) и обновляет <img> на месте.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rig(fetchRespond) {
  const calls = { fetches: [] };
  const { qdl } = H.loadQdl({
    lampa: H.makeLampa(),
    fetch: (url, init) => {
      calls.fetches.push({ url: String(url), body: (init && init.body) || '' });
      return Promise.resolve({ json: () => Promise.resolve(fetchRespond ? fetchRespond() : { success: true, has_poster: true }) });
    },
  });
  return { qdl, calls };
}

function imgMock() {
  return { attrs: [], attr(k, v) { this.attrs.push([k, v]); } };
}

const tick = () => new Promise((r) => setImmediate(r));
const HASH = 'a'.repeat(40);

test('healPoster: битый постер при живой мете → POST /qdl/save с poster_url, img обновлён', async () => {
  const { qdl, calls } = rig();
  const t = { hash: HASH, has_poster: false, meta: { title: 'Чёрная дыра', poster_path: '/sgvZ.jpg' } };
  const img = imgMock();

  qdl.healPoster(t, img);
  await tick(); await tick();

  assert.strictEqual(calls.fetches.length, 1);
  assert.ok(calls.fetches[0].url.indexOf('/qdl/save') !== -1);
  assert.ok(calls.fetches[0].body.indexOf('hash=' + HASH) !== -1);
  assert.ok(decodeURIComponent(calls.fetches[0].body).indexOf('poster_url=IMG:t/p/w500/sgvZ.jpg') !== -1, 'url постера из tmdbImg');
  assert.ok(calls.fetches[0].body.indexOf('card=') === -1, 'мету не перезаписываем');
  assert.strictEqual(t.has_poster, true);
  assert.strictEqual(img.attrs.length, 1);
  assert.ok(img.attrs[0][1].indexOf('/qdl/poster?hash=' + HASH) !== -1, 'src обновлён с кэш-бастером');
});

test('healPoster: no-op когда постер уже есть / нет меты / нет poster_path', async () => {
  const { qdl, calls } = rig();
  const img = imgMock();
  qdl.healPoster({ hash: HASH, has_poster: true, meta: { poster_path: '/x.jpg' } }, img);
  qdl.healPoster({ hash: HASH, has_poster: false }, img);
  qdl.healPoster({ hash: HASH, has_poster: false, meta: { title: 'X' } }, img);
  qdl.healPoster(null, img);
  await tick();
  assert.strictEqual(calls.fetches.length, 0);
  assert.strictEqual(img.attrs.length, 0);
});

test('healPoster: сервер снова не скачал постер → флаг и img не трогаем (ретрай при следующем открытии)', async () => {
  const { qdl, calls } = rig(() => ({ success: true, has_poster: false }));
  const t = { hash: HASH, has_poster: false, meta: { poster_path: '/x.jpg' } };
  const img = imgMock();

  qdl.healPoster(t, img);
  await tick(); await tick();

  assert.strictEqual(calls.fetches.length, 1);
  assert.strictEqual(t.has_poster, false);
  assert.strictEqual(img.attrs.length, 0);
});
