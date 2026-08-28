'use strict';
// Маркер «жду следующий сезон» (qdl 2.79): подписка на СЕРИАЛ, а не на раздачу.
//
// Жалоба владельца по карточке 229564 («Телохранители»): оба сезона скачаны и завершены,
// пункт «Следить за новыми сериями» ничего не даёт — он про новые серии УЖЕ скачанной раздачи.
//
// 🔒 Что здесь заперто:
//   • пункт виден у сериала ВСЕГДА — в том числе у полностью транскодированного (регресс §CK:
//     гейт `!t.local` дважды прятал подписку у карточек, которые всегда local — jut в 2.28,
//     XSMART в 2.76);
//   • у фильма, jut.su и XSMART пункта нет — у последних свой контур ожидания сезона;
//   • переключение НЕ спрашивает сезон (withPart перечисляет скачанное, а ждём мы не скачанное);
//   • адреса ручек и подписи состояний;
//   • обычная карточка не получает ни одного лишнего Select.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HA = 'a'.repeat(40);

const TV = { hash: HA, name: 'Телохранители / Сезон: 1', progress: 1, state: 'queuedUP', meta: { id: 229564, media_type: 'tv', title: 'Телохранители' } };
const MOVIE = { hash: HA, name: 'Movie.mkv', progress: 1, meta: { id: 9, media_type: 'movie', title: 'Фильм' } };
const TV_LOCAL = Object.assign({}, TV, { local: true, state: 'local' });
const TV_JUT = Object.assign({}, TV, { local: true, jut: { slug: 'naruto', watch: 'off' } });
const TV_XS = Object.assign({}, TV, { local: true, xsmart: { cat: 'serial', id: '5', ref: 'serial-5', watch: 'off' } });

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], toggles: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push() {}, replace() {}, active: () => ({}) },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h === 'ERR') { if (err) err(); return; }
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa });
  return { qdl, calls };
}

const last = (a) => a[a.length - 1];
const menuItem = (m, act) => m.items.filter((i) => i.act === act)[0];

// ─────────────────────────────── чистые хелперы ───────────────────────────────

test('seasonWaitFrom: маркер приезжает полем карточки, без отдельного запроса', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.seasonWaitFrom(TV), 0);
  assert.strictEqual(qdl.seasonWaitFrom({ seasonWait: { from: 3 } }), 3);
  assert.strictEqual(qdl.seasonWaitFrom({ seasonWait: {} }), 0);
  assert.strictEqual(qdl.seasonWaitFrom(null), 0);
});

test('canSeasonWait: гейт по МЕТЕ, а не по local — транскодированный сериал ждать сезон может', () => {
  const { qdl } = rig();
  assert.ok(qdl.canSeasonWait(TV));
  assert.ok(qdl.canSeasonWait(TV_LOCAL), 'регресс §CK: гейт !t.local прятал бы пункт у MP4-карточки');
  assert.ok(!qdl.canSeasonWait(MOVIE), 'у фильма сезонов не бывает');
  assert.ok(!qdl.canSeasonWait(TV_JUT), 'у jut.su свой контур ожидания сезона');
  assert.ok(!qdl.canSeasonWait(TV_XS), 'у XSMART тоже свой');
  assert.ok(!qdl.canSeasonWait({ hash: HA }), 'без меты TMDB id неоткуда взять');
});

// ─────────────────────────────── пункт меню ───────────────────────────────────

test('quickMenu: у сериала пункт есть и меняет подпись по состоянию маркера', () => {
  const { qdl, calls } = rig();

  qdl.quickMenu(TV);
  assert.strictEqual(menuItem(last(calls.selects), 'seasonwait').title, '⏳ Ждать следующий сезон');

  qdl.quickMenu(Object.assign({}, TV, { seasonWait: { from: 3 } }));
  assert.strictEqual(menuItem(last(calls.selects), 'seasonwait').title, '⏳ Жду 3 сезон — отменить');
});

test('quickMenu: пункт стоит рядом со слежением, а не вместо него', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(TV);
  const items = last(calls.selects).items;
  assert.ok(menuItem(last(calls.selects), 'watch'), 'слежение за новыми сериями осталось');
  const iw = items.findIndex((i) => i.act === 'watch');
  const is = items.findIndex((i) => i.act === 'seasonwait');
  assert.ok(is === iw + 1, 'ожидание сезона идёт следом за слежением');
});

test('quickMenu: у фильма, jut.su и XSMART пункта нет', () => {
  const { qdl, calls } = rig();
  for (const t of [MOVIE, TV_JUT, TV_XS]) {
    qdl.quickMenu(t);
    assert.strictEqual(menuItem(last(calls.selects), 'seasonwait'), undefined);
  }
});

// ─────────────────────────────── переключение ─────────────────────────────────

test('включение: один запрос, без вопроса «какой сезон»', () => {
  const { qdl, calls } = rig({ respond: (u) => (u.indexOf('/qdl/season/watch?') !== -1 ? { success: true, id: 229564, from: 3 } : undefined) });
  const t = Object.assign({}, TV);

  qdl.seasonWaitToggle(t);

  assert.deepStrictEqual(calls.reqs.map((u) => u.replace(/^.*\/qdl/, '/qdl')), ['/qdl/season/watch?hash=' + HA]);
  assert.strictEqual(t.seasonWait.from, 3);
  assert.match(last(calls.noty), /Жду 3 сезон/);
  assert.strictEqual(calls.selects.length, 0, 'маркер на сериале — выбирать раздачу незачем');
});

test('выключение: своя ручка remove и своя подпись', () => {
  const { qdl, calls } = rig({ respond: () => ({ success: true }) });
  const t = Object.assign({}, TV, { seasonWait: { from: 3 } });

  qdl.seasonWaitToggle(t);

  assert.ok(last(calls.reqs).indexOf('/qdl/season/watch/remove?hash=' + HA) !== -1);
  assert.ok(!qdl.seasonWaitFrom(t));
  assert.match(last(calls.noty), /Ожидание сезона снято/);
});

test('карточка без данных TMDB: сервер отказывает — говорим об этом, маркер не рисуем', () => {
  const { qdl, calls } = rig({ respond: () => ({ success: false, error: 'no tmdb tv' }) });
  const t = Object.assign({}, TV);

  qdl.seasonWaitToggle(t);

  assert.match(last(calls.noty), /нет данных TMDB/);
  assert.strictEqual(qdl.seasonWaitFrom(t), 0);
});

test('сеть отвалилась: состояние не меняется', () => {
  const { qdl, calls } = rig({ respond: () => 'ERR' });
  const t = Object.assign({}, TV);

  qdl.seasonWaitToggle(t);

  assert.match(last(calls.noty), /Не удалось включить ожидание/);
  assert.strictEqual(qdl.seasonWaitFrom(t), 0);
});

test('пункт меню доводит действие до сервера', () => {
  const { qdl, calls } = rig({ respond: () => ({ success: true, from: 3 }) });
  qdl.quickMenu(TV);
  last(calls.selects).onSelect({ act: 'seasonwait' });

  assert.ok(last(calls.reqs).indexOf('/qdl/season/watch?hash=' + HA) !== -1);
  assert.strictEqual(calls.selects.length, 1, 'ни одного лишнего Select');
});
