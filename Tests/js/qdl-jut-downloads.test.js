'use strict';
// «Следить за новыми сериями» для карточек jut.su в разделе «Загрузки».
//
// Регресс-ловушка (жалоба владельца, qdl 2.28): пункт не показывался ВООБЩЕ.
// Причина — гейт торрентной ветки `if (!t.local && t.state !== 'local')`: он верен
// для торрентов (им нужен живой торрент и links/<hash>.json), но jut-карточка
// ВСЕГДА local — она живёт локальным маркером, торрента у неё нет по определению.
// Итог: слежение было полностью реализовано на сервере и на вкладке jut.su,
// но из «Загрузок» — единственного места, куда попадает скачанное, — недостижимо.
//
// Контуры разные и путать их нельзя: торрент следит по infohash через /qdl/watch,
// jut.su — по slug через /qdl/jut/watch (см. три пояса изоляции, claude/06 §BC.7).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa });
  return { qdl, calls };
}

const last = (a) => a[a.length - 1];
const HASH = 'd'.repeat(40);

// Как их отдаёт /qdl/list. ⚠️ ФАБРИКИ, а не константы: обработчик меню мутирует
// карточку (t.watched = true), и общий объект протёк бы между тестами — следующий
// стартовал бы «уже под слежением» и уходил в ветку отписки.
const JUT = (over) => Object.assign({ hash: HASH, name: 'Игра лжецов', progress: 1,
  local: true, state: 'local', watched: false, jut: { slug: 'liar-game' } }, over);
const TORRENT = () => ({ hash: 'a'.repeat(40), name: 'Silo.S02.mkv', progress: 1 });   // не local
const LOCAL_MP4 = () => ({ hash: 'b'.repeat(40), name: 'Dune.mp4', progress: 1,       // транскод, не jut
                           local: true, state: 'local' });

const titles = (calls) => last(calls.selects).items.map((i) => i.title);
const acts = (calls) => last(calls.selects).items.map((i) => i.act);

// ─────────────────────── наличие пункта ───────────────────────

test('jut-карточка в «Загрузках»: пункт «Следить» ЕСТЬ (регресс 2.28)', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(JUT());
  assert.ok(titles(calls).some((t) => t.indexOf('Следить за новыми сериями') !== -1),
    'пункт слежения не показан для карточки jut.su — ровно тот баг, что чинили в 2.28');
  assert.ok(acts(calls).includes('jutwatch'), 'должен быть jut-контур (act=jutwatch), не торрентный');
});

test('jut-карточка под слежением: пункт переключается в «Не следить»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(JUT({ watched: true }));
  assert.ok(titles(calls).some((t) => t.indexOf('Не следить за новыми сериями') !== -1));
});

test('торрент остаётся на СВОЁМ контуре (act=watch), jut-ветка его не перехватывает', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(TORRENT());
  assert.ok(acts(calls).includes('watch'));
  assert.ok(!acts(calls).includes('jutwatch'));
});

test('локальный MP4 без jut (транскод торрента) — слежения нет ни в каком виде', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(LOCAL_MP4());
  assert.ok(!acts(calls).includes('watch'));
  assert.ok(!acts(calls).includes('jutwatch'));
});

// ─────────────────────── куда ходит кнопка ───────────────────────

test('«Следить» дёргает /qdl/jut/watch со slug, а НЕ /qdl/watch с hash', () => {
  const { qdl, calls } = rig({ respond: (u) => (u.indexOf('/qdl/jut/watch') !== -1
    ? { ok: true, season: 1, baseline: 18, message: 'Слежу за сезоном 1.' } : undefined) });
  qdl.quickMenu(JUT());
  const item = last(calls.selects).items.filter((i) => i.act === 'jutwatch')[0];
  last(calls.selects).onSelect(item);

  const url = last(calls.reqs);
  assert.ok(url.indexOf('/qdl/jut/watch?slug=liar-game') !== -1, 'ушло не туда: ' + url);
  assert.ok(url.indexOf(HASH) === -1, 'infohash в jut-контуре не участвует');
  // серверное сообщение важно показать дословно: оно объясняет, что уже вышедшие серии не качаются
  assert.ok(last(calls.noty).indexOf('Слежу за сезоном 1.') !== -1);
});

test('«Не следить» дёргает /qdl/jut/watch/remove и гасит флаг', () => {
  const { qdl, calls } = rig({ respond: (u) => (u.indexOf('/remove') !== -1 ? { ok: true } : undefined) });
  const card = JUT({ watched: true });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(last(calls.selects).items.filter((i) => i.act === 'jutwatch')[0]);

  assert.ok(last(calls.reqs).indexOf('/qdl/jut/watch/remove?slug=liar-game') !== -1);
  assert.strictEqual(card.watched, false);
});

test('слаг экранируется (в каталоге бывают дефисы и цифры, но URL строим честно)', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true, season: 2 }) });
  const card = JUT({ jut: { slug: 'yarikomizuki-no-gamer' } });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(last(calls.selects).items.filter((i) => i.act === 'jutwatch')[0]);
  assert.ok(last(calls.reqs).indexOf('slug=yarikomizuki-no-gamer') !== -1);
  assert.strictEqual(card.watched, true);
});

test('ошибка сервера не выставляет флаг слежения', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: false, error: 'SITE_DOWN' }) });
  const card = JUT();
  qdl.quickMenu(card);
  last(calls.selects).onSelect(last(calls.selects).items.filter((i) => i.act === 'jutwatch')[0]);
  assert.strictEqual(card.watched, false, 'при ошибке карточка не должна выглядеть отслеживаемой');
  assert.ok(last(calls.noty).indexOf('SITE_DOWN') !== -1);
});
