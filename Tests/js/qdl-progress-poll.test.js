'use strict';
// Поллер живого прогресса загрузок (qdl 2.93).
//
// Требование владельца дословно: «проценты загрузки на клиенте не обновляются, нужно чтобы клиент
// видел прогресс загрузки и проценты обновлялись» + «только если идут активные загрузки на данный
// момент». Второе — не пожелание, а инвариант: когда качать нечего, таймера не должно быть ВОВСЕ.
//
// Три вещи, на которых тут легко ошибиться и которые поэтому под тестом:
//   1. pgGet имеет ТРИ исхода (нет данных / качается / готово). Спутать «нет данных» с «готово» —
//      это молча играющий недокачанный фильм.
//   2. Интервал заводит pgApply, а не pgSubscribe: подписка делает один тик и ждёт ответа.
//      Иначе экран у мёртвого сервера держал бы таймер вечно.
//   3. ok:false — это «не знаю». Ни вердикта, ни таймера он менять не должен.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Песочница с инъецированными таймерами: тики дёргаем руками, реальных интервалов нет.
function rig(opts) {
  opts = opts || {};
  const calls = { reqs: [], ticks: [], cleared: [], fired: 0 };
  const lampa = H.makeLampa({
    Player: { opened: () => !!opts.playerOpen },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.reqs.push(String(url));
        const r = (opts.respond || (() => undefined))(String(url));
        if (r === 'ERR') { if (err) err(); return; }
        if (r !== undefined) ok(r);
      };
    },
  });
  const { qdl } = H.loadQdl({
    lampa,
    setInterval: (fn, ms) => { calls.ticks.push({ fn, ms }); return calls.ticks.length; },
    clearInterval: (id) => calls.cleared.push(id),
  });
  qdl.pgReset();
  qdl.setProgressConf({ poll: 5, idle: 30, budget: 10, block: true });
  return { qdl, calls };
}

const live = (over) => Object.assign({ ok: true, stamp: 's', active: 0, pending: 0, items: [] }, over || {});
const timers = (c) => c.ticks.length - c.cleared.length;   // сколько интервалов сейчас «живо»

// ─────────────────────────── жизненный цикл таймера ───────────────────────────

test('первая подписка делает НЕМЕДЛЕННЫЙ тик, но таймер ещё не заводит', () => {
  const { qdl, calls } = rig({ respond: () => undefined });   // ответа нет
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/progress') !== -1).length, 1, 'тик сразу');
  assert.strictEqual(calls.ticks.length, 0, 'интервал заводит ответ, а не подписка');
});

test('active > 0 → интервал poll; второй подписчик его не удваивает', () => {
  const { qdl, calls } = rig({ respond: () => live({ active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }) });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(calls.ticks.length, 1);
  assert.strictEqual(calls.ticks[0].ms, 5000);

  qdl.pgSubscribe('a', () => {});
  assert.strictEqual(timers(calls), 1, 'таймер один на весь плагин');
});

test('active=0, pending>0 → медленный пульс 30 с', () => {
  const { qdl, calls } = rig({ respond: () => live({ pending: 1, items: [{ h: 'a', p: 0.1, s: 'stalledDL' }] }) });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(calls.ticks[calls.ticks.length - 1].ms, 30000);
});

// 🔴 Это и есть требование №4 в виде теста.
test('ничего не качается → таймера НЕТ ВОВСЕ', () => {
  const { qdl, calls } = rig({ respond: () => live() });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(timers(calls), 0, 'опрос выключен, пока качать нечего');
});

test('бюджет медленного пульса исчерпан → замолкаем совсем', () => {
  const { qdl, calls } = rig({ respond: () => live({ pending: 1, items: [{ h: 'a', p: 0.1, s: 'stalledDL' }] }) });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(timers(calls), 1);

  qdl.pgSetIdleSince(Date.now() - 11 * 60000);   // 11 минут в пульсе при бюджете 10
  qdl.pgApply(live({ stamp: 's2', pending: 1, items: [{ h: 'a', p: 0.1, s: 'stalledDL' }] }));
  assert.strictEqual(timers(calls), 0, 'вечно стоящая раздача не держит опрос всю сессию');
});

test('отписка последнего подписчика гасит таймер', () => {
  const { qdl, calls } = rig({ respond: () => live({ active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }) });
  const t1 = qdl.pgSubscribe(null, () => {});
  const t2 = qdl.pgSubscribe(null, () => {});
  assert.strictEqual(timers(calls), 1);
  qdl.pgUnsubscribe(t1);
  assert.strictEqual(timers(calls), 1, 'подписчик ещё есть');
  qdl.pgUnsubscribe(t2);
  assert.strictEqual(timers(calls), 0);
});

test('отписка НЕ выбрасывает последнее известное состояние', () => {
  const { qdl } = rig({ respond: () => live({ active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }) });
  const t = qdl.pgSubscribe(null, () => {});
  qdl.pgUnsubscribe(t);
  // возврат на экран обязан рисоваться мгновенно, а не «пусто → через секунду цифра»
  assert.strictEqual(qdl.pgGet('a').p, 0.4);
});

test('плеер открыт → тик в сеть не ходит', () => {
  const { qdl, calls } = rig({ playerOpen: true, respond: () => live({ active: 1 }) });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(calls.reqs.filter((u) => u.indexOf('/qdl/progress') !== -1).length, 0);
});

test('per-file запрашивается только для подписчика с hash', () => {
  const { qdl, calls } = rig({ respond: () => live() });
  qdl.pgSubscribe(null, () => {});
  assert.ok(calls.reqs[0].indexOf('?hash=') === -1 && calls.reqs[0].indexOf('&hash=') === -1);
  qdl.pgSubscribe('abc', () => {});
  assert.ok(calls.reqs[1].indexOf('hash=abc') !== -1);
});

// ─────────────────────────── три исхода pgGet ───────────────────────────

test('pgGet: нет данных → null (fail-open), в items → прогресс, нет в items → готово', () => {
  const { qdl } = rig({ respond: () => undefined });
  assert.strictEqual(qdl.pgGet('a'), null, 'состояния ещё нет');

  qdl.pgApply(live({ active: 1, items: [{ h: 'a', p: 0.42, s: 'downloading' }] }));
  assert.strictEqual(qdl.pgGet('a').p, 0.42);
  assert.strictEqual(qdl.pgGet('b').p, 1, 'нет в items при ok:true = докачано');

  qdl.pgApply({ ok: false, active: 0, pending: 0, items: [] });
  assert.strictEqual(qdl.pgGet('a').p, 0.42, 'ok:false прежнее состояние не стирает');
});

test('pgFile: отдаёт прогресс по индексу, иначе null', () => {
  const { qdl } = rig({ respond: () => undefined });
  qdl.pgApply(live({ active: 1, items: [{ h: 'a', p: 0.5, s: 'downloading' }], files: { a: [[0, 1], [3, 0.2]] } }));
  assert.strictEqual(qdl.pgFile('a', 0), 1);
  assert.strictEqual(qdl.pgFile('a', 3), 0.2);
  assert.strictEqual(qdl.pgFile('a', 9), null, 'про этот файл сервер не рассказывал');
  assert.strictEqual(qdl.pgFile('b', 0), null, 'про эту раздачу тоже');
});

// ─────────────────────────── ok:false и ошибки ───────────────────────────

test('ok:false не трогает ни таймер, ни вердикт', () => {
  const { qdl, calls } = rig({ respond: () => live({ active: 1, items: [{ h: 'a', p: 0.3, s: 'downloading' }] }) });
  qdl.pgSubscribe(null, () => {});
  const before = timers(calls);
  qdl.pgApply({ ok: false, poll: 5, active: 0, pending: 0, items: [] });
  assert.strictEqual(timers(calls), before);
  assert.strictEqual(qdl.pgGet('a').p, 0.3);
});

test('серверный киллсвитч (poll:0) выключает поллер целиком', () => {
  const { qdl, calls } = rig({ respond: () => live({ active: 1 }) });
  qdl.pgSubscribe(null, () => {});
  qdl.pgApply({ ok: false, poll: 0, active: 0, pending: 0, items: [] });
  assert.strictEqual(timers(calls), 0);
  const n = calls.reqs.length;
  qdl.pgKick();
  assert.strictEqual(calls.reqs.length, n, 'после киллсвитча в сеть не ходим');
});

test('ошибка сети без известной работы → молчим до пробуждения', () => {
  const { qdl, calls } = rig({ respond: () => 'ERR' });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(timers(calls), 0, 'долбить мёртвый сервер впустую незачем');
});

test('ошибки при известной работе → бэкофф ×2 с потолком в минуту', () => {
  let mode = 'ok';
  const { qdl, calls } = rig({
    respond: () => (mode === 'ok' ? live({ active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }) : 'ERR'),
  });
  qdl.pgSubscribe(null, () => {});
  assert.strictEqual(calls.ticks[calls.ticks.length - 1].ms, 5000);

  mode = 'err';
  for (let i = 0; i < 3; i++) calls.ticks[calls.ticks.length - 1].fn();
  assert.strictEqual(calls.ticks[calls.ticks.length - 1].ms, 10000, 'после трёх неудач — вдвое реже');

  for (let i = 0; i < 12; i++) calls.ticks[calls.ticks.length - 1].fn();
  assert.ok(calls.ticks[calls.ticks.length - 1].ms <= 60000, 'потолок минута');
});

// ─────────────────────────── подписчики ───────────────────────────

test('неизменный stamp → подписчиков не дёргаем (DOM не рискует фокусом)', () => {
  let n = 0;
  const { qdl } = rig({ respond: () => undefined });
  qdl.pgSubscribe(null, () => n++);
  qdl.pgApply(live({ stamp: 'x', active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }));
  assert.strictEqual(n, 1);
  qdl.pgApply(live({ stamp: 'x', active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }));
  assert.strictEqual(n, 1, 'то же состояние — перерисовки нет');
  qdl.pgApply(live({ stamp: 'y', active: 1, items: [{ h: 'a', p: 0.5, s: 'downloading' }] }));
  assert.strictEqual(n, 2);
});

test('исчезновение хеша из items сбрасывает кеш серий ТОЛЬКО этой раздачи', () => {
  const { qdl, calls } = rig({
    respond: (u) => (u.indexOf('/qdl/episodes') !== -1 ? [{ index: 0, name: 'a.mkv', progress: 1 }] : undefined),
  });
  let epCalls = () => calls.reqs.filter((u) => u.indexOf('/qdl/episodes?hash=a') !== -1).length;

  qdl.fetchEpisodes('a', () => {});
  assert.strictEqual(epCalls(), 1);
  qdl.fetchEpisodes('a', () => {});
  assert.strictEqual(epCalls(), 1, 'мемоизация работает');

  qdl.pgApply(live({ stamp: '1', active: 1, items: [{ h: 'a', p: 0.4, s: 'downloading' }] }));
  qdl.pgApply(live({ stamp: '2' }));   // 'a' пропал = докачался
  qdl.fetchEpisodes('a', () => {});
  assert.strictEqual(epCalls(), 2, 'после докачки список серий перезапрошен');
});
