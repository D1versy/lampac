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
  // форма URL — та, что реально отдаёт Lampa.TMDB.image при proxy_tmdb=true (наш прокси, не TMDB):
  // сервер её распарсит (TmdbPosterPath) и скачает картинку через свой /tmdb/img
  assert.match(decodeURIComponent(calls.fetches[0].body), /poster_url=\S*\/tmdb\/img\/t\/p\/w500\/sgvZ\.jpg/, 'url постера из tmdbImg');
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

// ── контракт JS↔C#: форму poster_url должен уметь распарсить сервер ──
// Регрессия 2.15 жила ровно в этом шве: клиент сменил форму URL (proxy_tmdb=true → наш прокси
// вместо image.tmdb.org), сервер её не понимал и молча не качал постер, тестов на шов не было.
// Регулярка ниже — копия _tmdbImgRx из Modules/QbitDownload/Controller.cs (TmdbPosterPath).
const SERVER_RX = /(?:\/tmdb\/img\/|image\.tmdb\.org\/)(t\/p\/[^?#]+)/;

function posterUrlOf(body) {
  const m = /(?:^|&)poster_url=([^&]*)/.exec(body);
  return m ? decodeURIComponent(m[1]) : null;
}

for (const proxy of [true, false]) {
  test(`контракт: poster_url при proxy_tmdb=${proxy} парсится серверным TmdbPosterPath`, async () => {
    const lampa = H.makeLampa();
    lampa.Storage.set('proxy_tmdb', proxy);
    const calls = { fetches: [] };
    const { qdl } = H.loadQdl({
      lampa,
      fetch: (url, init) => {
        calls.fetches.push({ url: String(url), body: (init && init.body) || '' });
        return Promise.resolve({ json: () => Promise.resolve({ success: true, has_poster: true }) });
      },
    });

    qdl.saveMeta(HASH, { id: 1, title: 'X', poster_path: '/sgvZ.jpg' });
    qdl.healPoster({ hash: HASH, has_poster: false, meta: { poster_path: '/sgvZ.jpg' } }, imgMock());
    await tick(); await tick();

    assert.strictEqual(calls.fetches.length, 2);
    for (const f of calls.fetches) {
      const purl = posterUrlOf(f.body);
      const m = SERVER_RX.exec(purl);
      assert.ok(m, 'сервер не распознает poster_url: ' + purl);
      assert.strictEqual(m[1], 't/p/w500/sgvZ.jpg');
    }
  });
}

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
