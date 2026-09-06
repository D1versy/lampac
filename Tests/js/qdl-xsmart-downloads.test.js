'use strict';
// «Следить за новыми сериями» для карточек XSMART в разделе «Загрузки».
//
// Регресс-ловушка, ровно повторяющая жалобу владельца по jut.su (qdl 2.28): пункт
// не показывался ВООБЩЕ. Гейт торрентной ветки `if (!t.local && t.state !== 'local')`
// верен для торрентов, но xsmart-карточка — как и jut-овская — ВСЕГДА local: она живёт
// локальным маркером, торрента у неё нет по определению. Итог: слежение было полностью
// реализовано на сервере (XsmartWatch.cs, с qdl 2.71) и на карточке тайтла в разделе
// XSMART, а из «Загрузок» — недостижимо.
//
// Контуры разные и путать их нельзя: торрент следит по infohash через /qdl/watch,
// jut.su — по slug через /qdl/jut/watch, XSMART — по cat+id через /qdl/xsmart/watch.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], pushes: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Activity: { push: (o) => calls.pushes.push(o), replace() {}, active: () => ({}), all: () => [], backward() {}, own: () => true },
    // раздел XSMART рисует ЧУЖОЙ плагин из контейнера xsmart-proxy: по умолчанию он НЕ загружен
    Component: { add() {}, get: (n) => (opts.xsmartLoaded && n === 'xsmart_title' ? function () {} : undefined) },
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

// Как их отдаёт /qdl/list (XsmartDecorateListItem кладёт xsmart:{cat,id,ref,watch} и watched).
// ⚠️ ФАБРИКИ, а не константы: обработчик мутирует карточку, общий объект протёк бы между тестами.
const XS = (over) => Object.assign({
  hash: 'c'.repeat(40), name: 'Дюна: Пророчество', progress: 1, local: true, state: 'local',
  watched: false, meta: { media_type: 'tv', title: 'Дюна: Пророчество' },
  xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'off' },
}, over);
const XS_FILM = (over) => XS(Object.assign({ meta: { media_type: 'movie', title: 'Дюна' } }, over));
const JUT = () => ({ hash: 'd'.repeat(40), name: 'Игра лжецов', progress: 1, local: true,
                     state: 'local', watched: false, jut: { slug: 'liar-game' } });
const TORRENT = () => ({ hash: 'a'.repeat(40), name: 'Silo.S02.mkv', progress: 1 });

const titles = (calls) => last(calls.selects).items.map((i) => i.title);
const acts = (calls) => last(calls.selects).items.map((i) => i.act);
const pick = (calls) => last(calls.selects).items.filter((i) => i.act === 'xswatch')[0];

// ─────────────────────── наличие пункта ───────────────────────

test('xsmart-карточка в «Загрузках»: пункт «Следить» ЕСТЬ (та же ловушка, что у jut в 2.28)', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS());
  assert.ok(titles(calls).some((t) => t.indexOf('Следить') !== -1 && t.indexOf('качать') !== -1),
    'пункт слежения не показан для карточки XSMART — ровно тот баг, что чинили у jut.su');
  assert.ok(acts(calls).includes('xswatch'), 'должен быть xsmart-контур (act=xswatch), не торрентный и не jut');
});

test('xsmart-карточка под слежением: пункт переключается в «Не следить»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'grab' } }));
  assert.ok(titles(calls).some((t) => t.indexOf('Не следить за новыми сериями') !== -1));
});

test('режим notify показывает честную подпись, а не «Следить»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'notify' } }));
  assert.ok(pick(calls).title.indexOf('только уведомления') !== -1, 'подпись обязана показывать текущий режим');
});

// ─────────────────────── гейт «только сериал» ───────────────────────

test('xsmart-ФИЛЬМ: пункта нет — сервер на него отвечает «Следить можно только за сериалом»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS_FILM());
  assert.ok(!acts(calls).includes('xswatch'));
});

test('🔥 фильм с УЖЕ активной подпиской: пункт есть, иначе её нечем снять', () => {
  // Кривая/старая мета не должна запирать существующую подписку
  const { qdl, calls } = rig();
  qdl.quickMenu(XS_FILM({ watched: true, xsmart: { cat: 14, id: '9', ref: '14-9', watch: 'grab' } }));
  assert.ok(acts(calls).includes('xswatch'));
  assert.ok(pick(calls).title.indexOf('Не следить') !== -1);
});

test('карточка без меты, но с подпиской, тоже управляема', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS({ meta: undefined, watched: true, xsmart: { cat: 14, id: '9', ref: '14-9', watch: 'grab' } }));
  assert.ok(acts(calls).includes('xswatch'));
});

// ─────────────────────── xsMode ───────────────────────

test('старый сервер без xsmart.watch: watched:true читается как «качаю», а не как уведомления', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.xsMode({ xsmart: { id: '1' }, watched: true }), 'grab');
  assert.strictEqual(qdl.xsMode({ xsmart: { id: '1' }, watched: false }), 'off');
  assert.strictEqual(qdl.xsMode({ xsmart: { id: '1', watch: 'notify' }, watched: true }), 'notify');
  assert.strictEqual(qdl.xsMode({ xsmart: { id: '1', watch: 'мусор' }, watched: true }), 'grab');
});

// ─────────────────────── куда ходит пункт ───────────────────────

test('подписка из «Загрузок» шлёт autoGrab=1 в /qdl/xsmart/watch и НЕ шлёт season', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true, mode: 'grab', message: 'Слежу за сезоном 1.' }) });
  const card = XS();
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));

  const url = last(calls.reqs);
  assert.ok(url.indexOf('/qdl/xsmart/watch?cat=14&id=77031') !== -1, 'ушло не туда: ' + url);
  assert.ok(url.indexOf('autoGrab=1') !== -1, 'из «Загрузок» обязан включаться режим «качаю»: ' + url);
  assert.ok(url.indexOf('season=') === -1, 'сезон выбирает сервер (последний вышедший), клиент его не навязывает');
  assert.strictEqual(card.xsmart.watch, 'grab');
  assert.strictEqual(card.watched, true);
  assert.ok(last(calls.noty).indexOf('Слежу за сезоном 1.') !== -1, 'серверное сообщение показываем дословно');
});

test('«Не следить» одним тапом дёргает /watch/remove и гасит флаг', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true, message: 'Слежение выключено' }) });
  const card = XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'grab' } });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));

  assert.ok(last(calls.reqs).indexOf('/qdl/xsmart/watch/remove?cat=14&id=77031') !== -1);
  assert.strictEqual(card.xsmart.watch, 'off');
  assert.strictEqual(card.watched, false);
  assert.strictEqual(calls.selects.length, 1, 'снятие подписки — один тап, без подменю режимов');
});

test('🔥 notify → «качаю» идёт через /watch/mode, а НЕ повторной подпиской (защита baseline)', () => {
  // /qdl/xsmart/watch сбрасывает baseline на текущее состояние источника: серия, вышедшая
  // между тиком и нажатием, ушла бы в baseline — в режиме «качаю» её уже никто не скачает.
  const { qdl, calls } = rig({ respond: (u) => (u.indexOf('/watch/mode') !== -1 ? { ok: true, mode: 'grab' } : undefined) });
  const card = XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'notify' } });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));

  const menu = last(calls.selects);
  // строкой, а не deepStrictEqual: массив приходит из vm-песочницы (чужой realm)
  assert.strictEqual(menu.items.map((i) => String(i.want)).join(','), 'grab,off,undefined');
  menu.onSelect(menu.items[0]);

  const url = last(calls.reqs);
  assert.ok(url.indexOf('/qdl/xsmart/watch/mode?cat=14&id=77031') !== -1, 'ушло не туда: ' + url);
  assert.ok(url.indexOf('autoGrab=1') !== -1);
  assert.strictEqual(card.xsmart.watch, 'grab');
});

test('notify → «Не следить» снимает подписку целиком', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true }) });
  const card = XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'notify' } });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));
  const menu = last(calls.selects);
  menu.onSelect(menu.items[1]);

  assert.ok(last(calls.reqs).indexOf('/qdl/xsmart/watch/remove?cat=14&id=77031') !== -1);
  assert.strictEqual(card.xsmart.watch, 'off');
  assert.strictEqual(card.watched, false);
});

test('в «Загрузках» нет пункта понижения до «только уведомления» (паритет с jut.su)', () => {
  // Решение владельца: «Загрузки» = качаем или не следим; понижение живёт на карточке тайтла
  const { qdl, calls } = rig({ respond: () => ({ ok: true }) });
  qdl.quickMenu(XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'grab' } }));
  last(calls.selects).onSelect(pick(calls));
  assert.ok(!calls.reqs.some((u) => u.indexOf('autoGrab=0') !== -1), 'понижение до notify из «Загрузок» не делается');
});

test('подменю режимов имеет onBack — иначе на ТВ теряется фокус', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true }) });
  qdl.quickMenu(XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'notify' } }));
  last(calls.selects).onSelect(pick(calls));
  assert.strictEqual(typeof last(calls.selects).onBack, 'function');
});

test('отказ /watch/mode добирается полной подпиской (подписки уже нет / старый сервер)', () => {
  const seen = [];
  const { qdl, calls } = rig({
    respond: (u) => {
      seen.push(u);
      if (u.indexOf('/watch/mode') !== -1) return { ok: false, code: 'NOT_WATCHED' };
      return { ok: true, mode: 'grab', message: 'ок' };
    },
  });
  const card = XS({ watched: true, xsmart: { cat: 14, id: '77031', ref: '14-77031', watch: 'notify' } });
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));
  const menu = last(calls.selects);
  menu.onSelect(menu.items[0]);

  assert.ok(seen.some((u) => u.indexOf('/watch/mode') !== -1));
  assert.ok(seen.some((u) => u.indexOf('/qdl/xsmart/watch?cat=14&id=77031&autoGrab=1') !== -1),
    'после отказа /watch/mode обязан быть добор полной подпиской: ' + seen.join(' | '));
  assert.strictEqual(card.xsmart.watch, 'grab');
  assert.ok(!calls.noty.some((m) => m.indexOf('NOT_WATCHED') !== -1), 'внутренний код ошибки пользователю не показываем');
});

test('ошибка сервера не выставляет флаг слежения', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: false, message: 'XSMART недоступен' }) });
  const card = XS();
  qdl.quickMenu(card);
  last(calls.selects).onSelect(pick(calls));
  assert.strictEqual(card.watched, false);
  assert.strictEqual(card.xsmart.watch, 'off');
});

test('cat и id экранируются', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true }) });
  qdl.quickMenu(XS({ xsmart: { cat: 14, id: 'a b&c', ref: '14-a b&c', watch: 'off' } }));
  last(calls.selects).onSelect(pick(calls));
  assert.ok(last(calls.reqs).indexOf('id=a%20b%26c') !== -1, 'сырой & расщепил бы query: ' + last(calls.reqs));
});

// ─────────────────────── изоляция контуров ───────────────────────

test('xsmart-карточка не получает ни одного торрентного и ни одного jut-URL', () => {
  const { qdl, calls } = rig({ respond: () => ({ ok: true }) });
  qdl.quickMenu(XS());
  last(calls.selects).onSelect(pick(calls));
  assert.ok(!calls.reqs.some((u) => u.indexOf('/qdl/jut/') !== -1), 'пояс изоляции в UI пробит: ' + calls.reqs.join(' | '));
  assert.ok(!calls.reqs.some((u) => /\/qdl\/watch(\?|\/)/.test(u)), 'торрентный контур не при делах: ' + calls.reqs.join(' | '));
});

test('jut-карточка остаётся на СВОЁМ контуре — xsmart-ветка её не перехватывает', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(JUT());
  assert.ok(acts(calls).includes('jutwatch'));
  assert.ok(!acts(calls).includes('xswatch'));
});

test('торрент остаётся на СВОЁМ контуре', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(TORRENT());
  assert.ok(acts(calls).includes('watch'));
  assert.ok(!acts(calls).includes('xswatch'));
});

// ─────────────────────── тап по уведомлению ───────────────────────

test('уведомление XSMART в режиме notify открывает карточку тайтла, а не плеер по мёртвому URL', () => {
  // Карточки в «Загрузках» ещё нет (ничего не качали) — /qdl/list её не отдаст
  const { qdl, calls } = rig({ xsmartLoaded: true, respond: (u) => (u.indexOf('/qdl/list') !== -1 ? [] : undefined) });
  qdl.openNotification({ kind: 'NEW', hash: 'e'.repeat(40), title: 'Дюна: Пророчество', xsmart: '14-77031' });

  const p = last(calls.pushes);
  assert.ok(p, 'ничего не открылось');
  assert.strictEqual(p.component, 'xsmart_title');
  assert.strictEqual(p.xsmart_cat, 14);
  assert.strictEqual(p.xsmart_id, '77031');
  assert.ok(!calls.reqs.some((u) => u.indexOf('/qdl/stream') !== -1), 'плеер по мёртвому URL — ровно то, что чиним');
});

test('раздел XSMART у клиента не загружен → прежний фолбэк, а не пустой экран nocomponent', () => {
  const { qdl, calls } = rig({ respond: (u) => (u.indexOf('/qdl/list') !== -1 ? [] : undefined) });
  qdl.openNotification({ kind: 'NEW', hash: 'e'.repeat(40), title: 'Дюна', xsmart: '14-77031' });
  assert.ok(!calls.pushes.some((p) => p.component === 'xsmart_title'),
    'без компонента в реестре push увёл бы в nocomponent — пустой экран');
});

test('скачанное открывается карточкой «Загрузок» как раньше — ref не перехватывает', () => {
  const HASH = 'e'.repeat(40);
  const { qdl, calls } = rig({ xsmartLoaded: true,
    respond: (u) => (u.indexOf('/qdl/list') !== -1 ? [{ hash: HASH, name: 'Дюна', local: true, state: 'local' }] : undefined) });
  qdl.openNotification({ kind: 'NEW', hash: HASH, title: 'Дюна', xsmart: '14-77031' });
  assert.ok(!calls.pushes.some((p) => p.component === 'xsmart_title'));
});

test('openXsmartTitle: кривой ref не открывает ничего', () => {
  const { qdl, calls } = rig({ xsmartLoaded: true });
  assert.strictEqual(qdl.openXsmartTitle('', 'x'), false);
  assert.strictEqual(qdl.openXsmartTitle('мусор', 'x'), false);
  assert.strictEqual(qdl.openXsmartTitle(null, 'x'), false);
  assert.strictEqual(calls.pushes.length, 0);
});

test('openXsmartTitle: id с дефисом не режется по первому дефису', () => {
  const { qdl, calls } = rig({ xsmartLoaded: true });
  assert.strictEqual(qdl.openXsmartTitle('14-77031-2', 'x'), true);
  assert.strictEqual(last(calls.pushes).xsmart_id, '77031-2');
});

test('jut-уведомление по-прежнему уходит на свой экран (slug имеет приоритет)', () => {
  const { qdl, calls } = rig({ xsmartLoaded: true, respond: (u) => (u.indexOf('/qdl/list') !== -1 ? [] : undefined) });
  qdl.openNotification({ kind: 'NEW', hash: 'f'.repeat(40), title: 'Игра лжецов', slug: 'liar-game' });
  assert.strictEqual(last(calls.pushes).component, 'jut_title');
});

// ─────────────────────── карточка «в полёте» (qdl 2.114) ───────────────────────
// До первого готового файла сервер отдаёт xsmart-карточку с local:false. Торрентная ветка
// «Следить» гейтится по !local — без гейта по контуру фильм XSMART получал бы /qdl/watch.

test('in-flight xsmart-фильм без маркера не получает торрентный «Следить»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS_FILM({ local: false, state: 'downloading', progress: 0.3 }));
  assert.ok(!acts(calls).includes('watch'), 'торрентный контур не при делах: ' + acts(calls).join(','));
  assert.ok(!acts(calls).includes('xswatch'), 'у фильма слежения нет');
});

test('in-flight xsmart-сериал без маркера получает СВОЙ «Следить», а не торрентный', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS({ local: false, state: 'queued', progress: 0 }));
  assert.ok(acts(calls).includes('xswatch'));
  assert.ok(!acts(calls).includes('watch'));
});

test('у карточки «в полёте» пункт удаления подписан как отмена закачки', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu(XS_FILM({ local: false, state: 'downloading', progress: 0.3 }));
  const del = titles(calls).filter((t) => /Удалить|Отменить/.test(t))[0];
  if (del) assert.ok(/Отменить закачку/.test(del), 'подпись: ' + del);   // пункт есть только с правом «действия»
});
