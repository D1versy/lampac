'use strict';
// Тесты ускорений 2.10: мемоизация fetchEpisodes (кеш + коалесценция + инвалидация)
// и прогрев кеша сервера (warmup / prewarmForCard / warmupNext).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Reguest-мок со счётчиком сетевых вызовов; ok зовётся синхронно (как кеш-хит Lampa)
function countingReq(files) {
  const calls = [];
  return {
    calls,
    lampaOver: {
      Reguest: function () {
        this.timeout = () => {};
        this.clear = () => {};
        this.silent = (url, ok, err) => {
          calls.push(String(url));
          const u = String(url);
          if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
          else ok([]);
        };
      },
    },
  };
}

// fetch-мок для warmup (fire-and-forget)
function capturingFetch() {
  const urls = [];
  const f = (url) => { urls.push(String(url)); return Promise.resolve({ json: () => Promise.resolve({}) }); };
  f.urls = urls;
  return f;
}

const FILES = [
  { index: 0, name: 'Ep01.mkv', size: 1 },
  { index: 1, name: 'Ep02.mkv', size: 1 },
  { index: 2, name: 'Ep03.mkv', size: 1 },
];

// ─────────────────────────────── мемоизация fetchEpisodes ───────────────────────────────

test('fetchEpisodes: два последовательных вызова = ОДИН сетевой запрос, оба cb получили файлы', () => {
  const net = countingReq(FILES);
  const { qdl: q } = H.loadQdl({ lampa: H.makeLampa(net.lampaOver) });
  const h = 'a'.repeat(40);
  let got1 = null, got2 = null;
  q.fetchEpisodes(h, (f) => { got1 = f; });
  q.fetchEpisodes(h, (f) => { got2 = f; });
  assert.strictEqual(net.calls.filter((u) => u.indexOf('/qdl/episodes') !== -1).length, 1, 'сеть дёрнута один раз');
  assert.deepStrictEqual(got1, FILES);
  assert.deepStrictEqual(got2, FILES);
});

test('fetchEpisodes: разные hash кешируются раздельно', () => {
  const net = countingReq(FILES);
  const { qdl: q } = H.loadQdl({ lampa: H.makeLampa(net.lampaOver) });
  q.fetchEpisodes('a'.repeat(40), () => {});
  q.fetchEpisodes('b'.repeat(40), () => {});
  assert.strictEqual(net.calls.filter((u) => u.indexOf('/qdl/episodes') !== -1).length, 2);
});

test('fetchEpisodes: параллельные запросы одного hash коалесцируются (оба cb, одна сеть)', () => {
  // ok захватываем и зовём вручную — второй вызов должен сесть в _epPending
  let pendingOk = null;
  const calls = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => { calls.push(String(url)); pendingOk = ok; };
    },
  });
  const { qdl: q } = H.loadQdl({ lampa });
  const h = 'c'.repeat(40);
  let got1 = null, got2 = null;
  q.fetchEpisodes(h, (f) => { got1 = f; });
  q.fetchEpisodes(h, (f) => { got2 = f; });   // сеть ещё не ответила → подписчик
  assert.strictEqual(calls.length, 1, 'второй вызов не пошёл в сеть');
  pendingOk(FILES);
  assert.deepStrictEqual(got1, FILES);
  assert.deepStrictEqual(got2, FILES, 'подписчик тоже получил результат');
});

test('fetchEpisodes: dropEpCache(hash) → следующий вызов снова идёт в сеть', () => {
  const net = countingReq(FILES);
  const { qdl: q } = H.loadQdl({ lampa: H.makeLampa(net.lampaOver) });
  const h = 'd'.repeat(40);
  q.fetchEpisodes(h, () => {});
  q.dropEpCache(h);
  q.fetchEpisodes(h, () => {});
  assert.strictEqual(net.calls.filter((u) => u.indexOf('/qdl/episodes') !== -1).length, 2);
});

test('fetchEpisodes: ошибка НЕ кешируется — следующий вызов ретраит сеть', () => {
  let fail = true;
  const calls = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.push(String(url));
        if (fail) err(); else ok(FILES);
      };
    },
  });
  const { qdl: q } = H.loadQdl({ lampa });
  const h = 'e'.repeat(40);
  let errCalled = false, got = null;
  q.fetchEpisodes(h, () => {}, () => { errCalled = true; });
  assert.ok(errCalled, 'err пробросился (episodes и files упали)');
  fail = false;
  q.fetchEpisodes(h, (f) => { got = f; });
  assert.deepStrictEqual(got, FILES, 'после ошибки повторный вызов сходил в сеть');
});

// ─────────────────────────────── warmup / prewarmForCard / warmupNext ───────────────────────────────

test('warmup: дёргает /qdl/warmup с hash и index; без index → -1', () => {
  const fetch = capturingFetch();
  const { qdl: q } = H.loadQdl({ fetch });
  const h = 'f'.repeat(40);
  q.warmup(h, 2);
  q.warmup(h);
  assert.strictEqual(fetch.urls.length, 2);
  assert.ok(fetch.urls[0].indexOf('/qdl/warmup?hash=' + h + '&index=2') !== -1, fetch.urls[0]);
  assert.ok(fetch.urls[1].indexOf('&index=-1') !== -1, fetch.urls[1]);
});

test('prewarmForCard: сериал с прогрессом → греется серия «Продолжить»', () => {
  const net = countingReq(FILES);
  const fetch = capturingFetch();
  const lampa = H.makeLampa(net.lampaOver);
  const { qdl: q } = H.loadQdl({ lampa, fetch });
  const h = 'a'.repeat(40);
  lampa.Timeline.view(lampa.Utils.hash(h + ':Ep02')).percent = 40;   // на паузе → продолжаем её
  q.prewarmForCard(h);
  assert.strictEqual(fetch.urls.length, 1, 'один warmup');
  assert.ok(fetch.urls[0].indexOf('/qdl/warmup?hash=' + h + '&index=1') !== -1, 'греется Ep02 (index=1): ' + fetch.urls[0]);
});

test('prewarmForCard: без прогресса → греется первая серия; один файл → он сам', () => {
  const netSerial = countingReq(FILES);
  const fetch1 = capturingFetch();
  const { qdl: q1 } = H.loadQdl({ lampa: H.makeLampa(netSerial.lampaOver), fetch: fetch1 });
  q1.prewarmForCard('b'.repeat(40));
  assert.ok(fetch1.urls[0].indexOf('&index=0') !== -1, 'первая серия: ' + fetch1.urls[0]);

  const movie = [{ index: 7, name: 'Movie.mkv', size: 1 }];
  const netMovie = countingReq(movie);
  const fetch2 = capturingFetch();
  const { qdl: q2 } = H.loadQdl({ lampa: H.makeLampa(netMovie.lampaOver), fetch: fetch2 });
  q2.prewarmForCard('c'.repeat(40));
  assert.ok(fetch2.urls[0].indexOf('&index=7') !== -1, 'единственный файл: ' + fetch2.urls[0]);
});

test('prewarmForCard: серия-донор греется со СВОИМ hash (srcHash)', () => {
  const donor = 'd'.repeat(40);
  const files = [
    { index: 0, name: 'Ep01.mkv', size: 1 },
    { index: 4, name: 'Ep02.mkv', size: 1, hash: donor, source: 'donor' },
  ];
  const net = countingReq(files);
  const fetch = capturingFetch();
  const lampa = H.makeLampa(net.lampaOver);
  const { qdl: q } = H.loadQdl({ lampa, fetch });
  const h = 'e'.repeat(40);
  // Ep01 досмотрена → продолжаем донорскую Ep02
  lampa.Timeline.view(lampa.Utils.hash(h + ':Ep01')).percent = 100;
  q.prewarmForCard(h);
  assert.ok(fetch.urls[0].indexOf('hash=' + donor) !== -1 && fetch.urls[0].indexOf('&index=4') !== -1,
    'донорская серия по hash донора: ' + fetch.urls[0]);
});

test('warmupNext: греет следующую серию; на последней — молчит', () => {
  const fetch = capturingFetch();
  const { qdl: q } = H.loadQdl({ fetch });
  const h = 'a'.repeat(40);
  const vids = FILES;
  q.warmupNext(h, vids, vids[0]);
  assert.strictEqual(fetch.urls.length, 1);
  assert.ok(fetch.urls[0].indexOf('&index=1') !== -1, 'следующая после первой: ' + fetch.urls[0]);
  q.warmupNext(h, vids, vids[2]);   // последняя
  assert.strictEqual(fetch.urls.length, 1, 'после последней серии прогрева нет');
});

test('экран серий: после старта серии греется следующая по плейлисту', () => {
  const net = countingReq(FILES);
  const fetch = capturingFetch();
  const lampa = H.makeLampa(Object.assign(net.lampaOver, {
    Player: { play() {}, playlist() {} },
    Platform: { tv: () => true },
  }));
  const { qdl: q } = H.loadQdl({ lampa, fetch });
  const h = 'b'.repeat(40);
  const inst = new q.ComponentEpisodes({ qdl_hash: h, qdl_name: 'Сериал' });
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  inst.play(1);
  const wu = fetch.urls.filter((u) => u.indexOf('/qdl/warmup') !== -1);
  assert.strictEqual(wu.length, 1, 'один warmup при старте: ' + JSON.stringify(fetch.urls));
  assert.ok(wu[0].indexOf('&index=2') !== -1, 'греется серия после текущей: ' + wu[0]);
});

test('openDownload: прогрев уходит сразу, не дожидаясь полной карточки', () => {
  const net = countingReq(FILES);
  const fetch = capturingFetch();
  let pushed = null;
  const lampa = H.makeLampa(Object.assign(net.lampaOver, {
    Activity: { push: (o) => { pushed = o; }, replace() {}, active: () => ({}), backward() {}, own: () => true },
  }));
  const { qdl: q } = H.loadQdl({ lampa, fetch });
  const h = 'c'.repeat(40);
  q.openDownload({ hash: h, meta: { id: 42, media_type: 'tv' }, progress: 1 });
  assert.ok(pushed, 'полная карточка запушена');
  assert.strictEqual(fetch.urls.filter((u) => u.indexOf('/qdl/warmup') !== -1).length, 1, 'warmup улетел при открытии');
});
