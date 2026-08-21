'use strict';
// qdl 2.59: «весь день одной записью» — КОГДА мы отдаём сутки в плеер.
//
// 🔴 Раньше играли по первому же готовому куску (`info.ready > 0`), потому что недособранный день
// отдавался плейлистом EVENT и «лента росла сама». С 2.59 плейлист ВСЕГДА самозавершённый
// (VOD + ENDLIST) — иначе libVLC считает поток эфиром, отдаёт length=0, и у зрителя мёртвый
// ползунок, мёртвый драг-скраб и выброшенная позиция просмотра на Android. Плата за ENDLIST:
// то, что попало в плейлист, — это и есть вся лента, дорасти она уже не может.
//
// Отсюда инварианты этого файла:
//   • собранный день играем сразу;
//   • несобранный — ЖДЁМ (а не играем огрызок), но не бесконечно;
//   • сохранённая позиция обязана быть зажата в длину собранного, иначе резюм уводит за ENDLIST.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const CAM = { id: 6, name: 'Vlad-MacBook-Recorder' };

/**
 * Плагин с подменённым /qdl/live/day: replies — очередь ответов на последовательные опросы,
 * последний повторяется. Таймеры ручные, чтобы 20-секундное терпение не жгло реальное время.
 */
function boot(replies, opts) {
  opts = opts || {};
  const played = [];
  const asked = [];
  const timers = [];
  let now = opts.now || 1000000;
  let i = 0;

  const lampa = H.makeLampa({
    Player: { play: (item) => played.push(item), playlist: () => {} },
    Timeline: { view: () => opts.timeline || null, update: () => {} },
    Noty: { show: () => {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        if (url.indexOf('/qdl/live/day') === -1) return;
        asked.push(url);
        const r = replies[Math.min(i++, replies.length - 1)];
        if (r === 'fail') { if (err) err(); return; }
        ok(r);
      };
    },
  });

  // Часы под контролем: терпение livePlayDay — 20 с, жечь их реальным временем в тестах незачем.
  // Подменяем ТОЛЬКО now(), остальное отдаём настоящему Date — qdl.js пользуется и new Date().
  const RealDate = Date;
  function FakeDate(...a) { return a.length ? new RealDate(...a) : new RealDate(now); }
  FakeDate.now = () => now;
  FakeDate.parse = RealDate.parse;
  FakeDate.UTC = RealDate.UTC;
  FakeDate.prototype = RealDate.prototype;

  const ctx = H.loadQdl({
    lampa,
    setTimeout: (fn, ms) => timers.push({ fn, at: now + (ms || 0) }),
    clearTimeout: (id) => { if (timers[id - 1]) timers[id - 1].dead = true; },
    sandboxExtra: { Date: FakeDate },
  });

  /** Прокрутить виртуальные часы вперёд, выполняя всё, что должно было сработать. */
  function tick(ms) {
    const until = now + ms;
    for (;;) {
      const due = timers.filter((t) => !t.dead && !t.done && t.at <= until)
                        .sort((a, b) => a.at - b.at)[0];
      if (!due) break;
      now = Math.max(now, due.at);
      due.done = true;
      due.fn();
    }
    now = until;
  }

  return Object.assign(ctx, { played, asked, tick, lampa });
}

const READY = (n, total, complete, seconds) => ({
  date: '2026-08-21', label: 'Сегодня', camera: { id: 6, name: 'cam' },
  path: '/qdl/live/day/6/2026-08-21/stream.m3u8',
  ready: n, total: total, complete: complete, seconds: seconds,
});

test('день собран целиком — играем сразу', () => {
  const { qdl, played, tick } = boot([READY(8, 8, true, 7552)]);

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(10);

  assert.strictEqual(played.length, 1);
  assert.ok(played[0].url.indexOf('/qdl/live/day/6/2026-08-21/stream.m3u8') > 0, played[0].url);
});

test('день ещё домалывается — НЕ играем огрызок, ждём полную ленту', () => {
  // 🔴 Регрессия, которую этот тест держит: до 2.59 здесь сразу уходил в плеер плейлист из
  // одного куска (17 минут вместо двух часов), и с ENDLIST это была бы вся доступная лента.
  const { qdl, played, tick } = boot([READY(1, 8, false, 1000)]);

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(10000);

  assert.strictEqual(played.length, 0, 'играть недособранные сутки рано');
});

test('дождались готовности — играем полную ленту', () => {
  const { qdl, played, tick } = boot([
    READY(1, 8, false, 1000),
    READY(4, 8, false, 4012),
    READY(8, 8, true, 7552),
  ]);

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(10000);

  assert.strictEqual(played.length, 1);
});

test('терпение вышло (20 с) — играем то, что готово: лента короче, но перемотка работает', () => {
  const { qdl, played, tick } = boot([READY(3, 8, false, 3008)]);

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');

  tick(19000);
  assert.strictEqual(played.length, 0, 'до порога ещё ждём');

  tick(6000);
  assert.strictEqual(played.length, 1, 'после порога играем готовый префикс');
});

test('готово ноль и день закрыт — честная ошибка, а не пустой плеер', () => {
  const { qdl, played, tick } = boot([READY(0, 4, true, 0)]);

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(30000);

  assert.strictEqual(played.length, 0);
});

test('записей за день нет — плеер не открывается', () => {
  const { qdl, played, tick } = boot([{ date: '2026-08-14', total: 0, ready: 0, empty: true }]);

  qdl.livePlayDay(CAM, '2026-08-14', '14 августа');
  tick(30000);

  assert.strictEqual(played.length, 0);
});

test('сохранённая позиция зажимается в длину собранного — иначе резюм уводит за ENDLIST', () => {
  // Вчера день досмотрели до конца (7500 с), сегодня открыли его же, а собран пока только префикс
  // в 3008 с. Плейлист самозавершённый: seek на 7500 — это seek за конец потока.
  const timeline = { time: 7500, duration: 7552, percent: 99 };
  const { qdl, played, tick } = boot([READY(3, 8, true, 3008)], { timeline });

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(10);

  assert.strictEqual(played.length, 1);
  assert.ok(played[0].timeline.time <= 3008, 'позиция обязана быть внутри ленты: ' + played[0].timeline.time);
  assert.strictEqual(played[0].timeline.duration, 3008, 'длительность — по собранному');
});

test('позиция внутри ленты не трогается', () => {
  const timeline = { time: 1200, duration: 7552, percent: 16 };
  const { qdl, played, tick } = boot([READY(8, 8, true, 7552)], { timeline });

  qdl.livePlayDay(CAM, '2026-08-21', 'Сегодня');
  tick(10);

  assert.strictEqual(played[0].timeline.time, 1200);
  assert.strictEqual(played[0].timeline.duration, 7552);
});

test('прогрев: наводка будит ремукс — но с дебаунсом и один раз на камеру+день', () => {
  const { qdl, asked, tick } = boot([READY(8, 8, true, 7552)]);

  qdl.liveWarmDay(CAM, '2026-08-21');
  assert.strictEqual(asked.length, 0, 'мгновенно дёргать регистратор нельзя — фокус ещё едет');

  tick(800);
  assert.strictEqual(asked.length, 1, 'после паузы — один запрос');
  assert.ok(asked[0].indexOf('camera=6') > 0 && asked[0].indexOf('date=2026-08-21') > 0, asked[0]);

  qdl.liveWarmDay(CAM, '2026-08-21');
  tick(2000);
  assert.strictEqual(asked.length, 1, 'повторная наводка на ту же камеру не будит второй раз');
});

test('прогрев: пробег фокуса по списку не будит каждую камеру по пути', () => {
  // 🔴 Иначе пульт, пролетающий шесть камер, запускал бы шесть ремуксов и гигабайты кэша на
  // регистраторе — при том, что откроют в итоге одну.
  const { qdl, asked, tick } = boot([READY(8, 8, true, 7552)]);

  qdl.liveWarmDay({ id: 1 }, '2026-08-21');
  tick(200);
  qdl.liveWarmDay({ id: 3 }, '2026-08-21');
  tick(200);
  qdl.liveWarmDay({ id: 6 }, '2026-08-21');
  tick(800);

  assert.strictEqual(asked.length, 1, 'разбужена только та камера, на которой фокус остановился');
  assert.ok(asked[0].indexOf('camera=6') > 0, asked[0]);
});
