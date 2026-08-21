'use strict';
// qdl 2.54: права на скрытые разделы (D1versy Live / D1versy Rec) приходят с сервера
// по айди устройства — GET /qdl/features. Клиент только рисует то, что разрешено.
//
// 🔴 Главное, что здесь фиксируется: кеш прав в Lampa.Storage — это УСКОРЕНИЕ ОТРИСОВКИ, а не
// право. Настоящий замок стоит на сервере (13 роутов qdl/live/* отдают 404), и подделка
// localStorage даёт максимум пустой экран.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

/** Загрузка qdl.js с перехватом ответа сервера на /qdl/features. */
function withServer(reply) {
  const seen = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        seen.push(url);
        if (url.indexOf('/qdl/features') === -1) return;
        if (reply === null) { if (err) err(); return; }
        ok(reply);
      };
    },
  });
  const ctx = H.loadQdl({ lampa });
  return Object.assign(ctx, { seen, lampa });   // loadQdl отдаёт {qdl,sandbox} — Lampa докладываем сами
}

test('loadFeatures: ответ сервера кладётся в память и в кеш Lampa.Storage', () => {
  const { qdl, lampa } = withServer({ uid: 'dueq3shm', platform: 'mac', client: '1.0.9', features: { live: true, rec: false } });

  let done = false;
  qdl.loadFeatures(() => { done = true; });

  assert.ok(done, 'колбэк обязан вызваться — по нему перестраивается меню');
  assert.strictEqual(qdl.qdlAllowed('live'), true);
  assert.strictEqual(qdl.qdlAllowed('rec'), false);
  assert.deepStrictEqual(lampa.Storage.get('qdl_features', null), { live: true, rec: false });
});

test('loadFeatures: uid дописывается в запрос — иначе сервер не знает, чьи права отдавать', () => {
  const { qdl, seen, lampa } = withServer({ features: { live: true, rec: true } });
  lampa.Storage.set('lampac_unic_id', 'dueq3shm');
  qdl.loadFeatures();

  const call = seen.filter((u) => u.indexOf('/qdl/features') >= 0)[0];
  assert.ok(call, 'запрос к /qdl/features должен уйти');
  assert.ok(/[?&]uid=dueq3shm(&|$)/.test(call), 'в URL обязан быть uid: ' + call);
});

test('сервер не ответил — прежние права не теряются (сеть моргнула, не отзыв)', () => {
  const { qdl } = withServer(null);
  qdl.setPerms({ live: true, rec: true });

  let done = false;
  qdl.loadFeatures(() => { done = true; });

  assert.ok(done, 'колбэк зовётся и на ошибке — иначе меню осталось бы неперестроенным навсегда');
  assert.strictEqual(qdl.qdlAllowed('live'), true, 'сетевой сбой не должен гасить разделы');
});

test('ответ без features игнорируется целиком (битый/чужой JSON)', () => {
  const { qdl, lampa } = withServer({ error: 'oops' });
  qdl.setPerms({ live: true, rec: true });
  qdl.loadFeatures();

  assert.strictEqual(qdl.qdlAllowed('live'), true);
  assert.strictEqual(lampa.Storage.get('qdl_features', null), null, 'мусор в кеш не попадает');
});

test('qdlAllowed без ответа сервера читает кеш, а неизвестная фича всегда запрещена', () => {
  const { qdl, lampa } = withServer({ features: {} });
  qdl.setPerms(null);
  lampa.Storage.set('qdl_features', { live: true });

  assert.strictEqual(qdl.qdlAllowed('live'), true);
  assert.strictEqual(qdl.qdlAllowed('rec'), false, 'чего нет в наборе — того нет');
  assert.strictEqual(qdl.qdlAllowed('какая-то-будущая-вкладка'), false);
});

test('свежий ответ сервера перебивает кеш (право отозвали, пока клиент был открыт)', () => {
  const { qdl, lampa } = withServer({ features: { live: false, rec: false } });
  lampa.Storage.set('qdl_features', { live: true, rec: true });
  qdl.loadFeatures();

  assert.strictEqual(qdl.qdlAllowed('live'), false);
  assert.deepStrictEqual(lampa.Storage.get('qdl_features', null), { live: false, rec: false },
    'кеш обязан обновиться, иначе следующий старт снова нарисует отозванный пункт');
});

test('экраны Live/Rec без права уходят назад, а не показывают пустоту', () => {
  const backs = [];
  const notes = [];
  const lampa = H.makeLampa({
    Activity: { push() {}, replace() {}, backward() { backs.push(1); } },
    Noty: { show(t) { notes.push(String(t)); } },
  });
  const { qdl } = H.loadQdl({ lampa });
  qdl.setPerms({ live: false, rec: false });

  for (const name of ['ComponentLiveWatch', 'ComponentLive', 'ComponentLiveCamera']) {
    const inst = new qdl[name]({ qdl_camera: {}, qdl_date: '' });
    inst.activity = { loader() {}, toggle() {} };
    inst.create();
  }

  assert.strictEqual(notes.length, 3, 'каждый экран объясняет отказ');
  assert.ok(notes.every((t) => /недоступен/i.test(t)), notes.join(' | '));
});

test('withUid покрывает и превью, и эфир, и нативный плеер записей', () => {
  // Эти URL уходят мимо req(): <img src>, <video src> и плеер VLC/ExoPlayer. Без uid в самой
  // строке гейт прав отдал бы им 404 на каждый кадр и каждый сегмент.
  const src = H.qdlSource();
  const musts = [
    "withUid(API + '/qdl/live/watch/thumb?camera=",
    "withUid(API + '/qdl/live/thumb?id=",
    "withUid(API + '/qdl/live/watch/hls/",
    "withUid(API + '/qdl/live/stream?id=",
    "withUid(API + info.path)",
    "withUid(API + '/qdl/live/watch')",
    "withUid(API + '/qdl/live/days?back='",
    "withUid(API + '/qdl/live/cameras'",
    "withUid(API + '/qdl/live/recordings?camera=",
    "withUid(API + '/qdl/live/feed?offset='",
  ];
  for (const m of musts)
    assert.ok(src.includes(m), 'uid обязан дописываться: ' + m);

  assert.ok(!/network\.silent\(API \+ '\/qdl\/live/.test(src),
    'ни один вызов к qdl/live не должен идти мимо withUid');
  assert.ok(!/attr\('src', API \+ '\/qdl\/live/.test(src),
    'превью тоже ходят с uid — иначе плитки станут битыми картинками');
});
