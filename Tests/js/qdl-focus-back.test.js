'use strict';
// Возврат фокуса после долгого нажатия (qdl 2.80, claude/06 §CO).
//
// Жалоба владельца: в «Загрузках» открыл меню долгим нажатием, нажал back (или кликнул в
// пустую область) — и фокус спрыгнул в САМОЕ НАЧАЛО списка вместо той карточки.
//
// Механика: карточки писали last только по hover:focus — событию ПУЛЬТА. Палец шлёт
// hover:touch (hover:focus не приходит вовсе), мышь десктопа в navigation_type=mouse —
// только hover:hover. last оставался пуст → onBack селектбокса → Controller.toggle('content')
// → collectionFocus(false) → штатный Lampa берёт ПЕРВЫЙ .selector → hover:focus → scroll.update
// → лента в начало.
//
// Проверяем оба конца: last пишется по пальцу и мыши, и возврат НЕ двигает ленту.
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HA = 'a'.repeat(40), HB = 'b'.repeat(40), HC = 'c'.repeat(40);
const COL_ID = 'c' + '1'.repeat(32);

const F1 = { hash: HA, name: 'Dune.mkv', progress: 1, added: 300, meta: { id: 1, media_type: 'movie', title: 'Дюна', year: 2021 } };
const F2 = { hash: HB, name: 'Riddick.mkv', progress: 1, added: 200, meta: { id: 2, media_type: 'movie', title: 'Риддик', year: 2013 } };
const F3 = { hash: HC, name: 'Alien.mkv', progress: 1, added: 100, meta: { id: 3, media_type: 'movie', title: 'Чужой', year: 1979 } };
const COL = { id: COL_ID, title: 'Сборник', cover: HA, hashes: [HA, HB] };

// Поднять «Загрузки» на реальном DOM. Ловим контроллер (его toggle — точка возврата),
// цель collectionFocus и вызовы scroll.update.
function mount(data) {
  data = data || {};
  let w;   // заполнится ниже; мок collectionFocus читает его в момент вызова
  const calls = { selects: [], focused: [], updates: [], navFocused: [], navFocus: 0, controllers: {} };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Controller: {
      add: (name, ctrl) => { calls.controllers[name] = ctrl; },
      toggle() {},
      collectionSet() {},
      // 🔥 Мок ОБЯЗАН триггерить hover:focus, как живая Lampa (Navigator.focus →
      // Controller.focus → Utils.trigger). Без этого обработчик карточки не зовёт
      // scroll.update вовсе, и проверка «лента не двинулась» становится пустой —
      // зелёной и на сломанном коде (проверено диверсией).
      collectionFocus: (t) => {
        calls.focused.push(t);
        if (t && t.nodeType) w.$(t).trigger('hover:focus');
      },
    },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        if (String(url).indexOf('/qdl/collections') !== -1) return ok(data.collections || []);
        if (String(url).indexOf('/qdl/list') !== -1) return ok(data.list || []);
        ok([]);
      };
    },
  });
  const dom = H.loadQdlDom({ lampa });
  w = dom.w;
  const $ = dom.$, qdl = dom.qdl;

  lampa.Template.get = (name, d) =>
    $('<div class="card selector"><div class="card__view"><img class="card__img"></div><div class="card__title"></div></div>')
      .find('.card__title').text((d || {}).title || '').end();

  const scroll = {};
  lampa.Scroll = function () {
    const el = $('<div class="scroll"><div class="scroll__body"></div></div>');
    this.render = () => el;
    this.body = () => el.find('.scroll__body');
    this.minus = () => {};
    this.update = (e) => calls.updates.push(e && e[0] ? e[0] : e);
    this.destroy = () => {};
    scroll.inst = this;
  };

  // Navigator — глобал бандла, в jsdom его нет (window.Navigator это DOM-интерфейс).
  // Ссылка резолвится в момент вызова, поэтому подменять можно уже после загрузки плагина.
  w.Navigator = {
    focused: (e) => calls.navFocused.push(e),
    focus: () => { calls.navFocus++; },
    canmove: () => false,
    move: () => {},
  };

  const comp = new qdl.ComponentDownloads({});
  comp.activity = { loader() {}, toggle() {} };
  comp.create();
  comp.start();
  return { comp, html: comp.render(), calls, qdl, $, w, scroll };
}

// «Карточка на экране»: jsdom считает геометрию нулевой, а focusBack смотрит именно на неё.
function seeOnScreen(node, top) {
  node.getBoundingClientRect = () => ({ top: top, bottom: top + 100, left: 0, right: 100, width: 100, height: 100 });
}
function seeOffScreen(node) {
  node.getBoundingClientRect = () => ({ top: -900, bottom: -800, left: 0, right: 100, width: 100, height: 100 });
}

// ───────────────────── last пишется не только пультом ─────────────────────

for (const ev of ['hover:hover', 'hover:touch']) {
  test(`«Загрузки»: ${ev} по карточке пишет last — возврат приходит на НЕЁ, а не на первую`, () => {
    const { html, calls } = mount({ list: [F1, F2, F3] });
    const cards = html.find('.card');
    assert.strictEqual(cards.length, 3);

    const target = cards.eq(2)[0];      // третья: если last пуст, фокус сядет на первую
    seeOnScreen(target, 200);
    cards.eq(2).trigger(ev);

    calls.focused.length = 0;
    calls.controllers.content.toggle();

    assert.strictEqual(calls.focused.length, 1);
    assert.strictEqual(calls.focused[0], target,
      'на мыши и пальце hover:focus не приходит вовсе — без записи last фокус уходит на первый .selector');
  });
}

test('«Загрузки»: карточка-папка коллекции помечается так же', () => {
  const { html, calls } = mount({ list: [F1, F2, F3], collections: [COL] });
  const col = html.find('.qdl-col-card');
  assert.strictEqual(col.length, 1);

  seeOnScreen(col[0], 50);
  col.trigger('hover:hover');

  calls.focused.length = 0;
  calls.controllers.content.toggle();
  assert.strictEqual(calls.focused[0], col[0]);
});

test('пишем last через Navigator.focused, а НЕ focus (focus сам утащил бы скролл)', () => {
  const { html, calls } = mount({ list: [F1, F2] });
  const card = html.find('.card').eq(1);
  card.trigger('hover:touch');

  assert.deepStrictEqual(calls.navFocused, [card[0]], 'focused только помечает элемент активным');
  assert.strictEqual(calls.navFocus, 0, 'Navigator.focus триггерит hover:focus и двигает ленту');
  assert.deepStrictEqual(calls.updates, [], 'пометка мышью/пальцем скролл не трогает');
});

// ───────────────────── полный сценарий жалобы ─────────────────────

test('долгое нажатие → back из меню: фокус на исходной карточке, лента не шелохнулась', () => {
  const { html, calls } = mount({ list: [F1, F2, F3] });
  const cards = html.find('.card');
  const target = cards.eq(2)[0];
  seeOnScreen(target, 300);

  cards.eq(2).trigger('hover:hover');   // мышь десктопа подвела курсор
  cards.eq(2).trigger('hover:long');    // удержание → меню карточки

  const menu = calls.selects[calls.selects.length - 1];
  assert.ok(menu && typeof menu.onBack === 'function', 'у меню обязан быть onBack');

  calls.focused.length = 0;
  calls.updates.length = 0;
  menu.onBack();                        // back или клик в пустую область — путь один
  calls.controllers.content.toggle();   // Controller.toggle('content') замокан, зовём сами

  assert.strictEqual(calls.focused[0], target, 'вернулись на ту же карточку');
  assert.deepStrictEqual(calls.updates, [], 'scroll.update при возврате не зовётся — лента стоит');
});

// ───────────────────── focusBack: сам хелпер ─────────────────────

test('focusBack: видимый элемент — фокус без scroll.update, и update возвращается на место', () => {
  const { html, calls, scroll, w } = mount({ list: [F1, F2] });
  const card = html.find('.card').eq(1)[0];
  seeOnScreen(card, 120);

  const before = scroll.inst.update;
  calls.focused.length = 0;
  calls.updates.length = 0;

  w.__qdl.focusBack(scroll.inst, card);

  assert.deepStrictEqual(calls.focused, [card]);
  assert.deepStrictEqual(calls.updates, [], 'центрирование при возврате — тот самый лишний рывок');
  assert.strictEqual(scroll.inst.update, before, 'подмена обязана быть снята: дальше скролл нужен рабочим');
});

test('focusBack: элемент ВНЕ экрана всё-таки центрируем (иначе фокус там, куда не видно)', () => {
  const { html, calls, scroll, w } = mount({ list: [F1, F2] });
  const card = html.find('.card').eq(1)[0];
  seeOffScreen(card);

  const before = scroll.inst.update;
  calls.updates.length = 0;
  w.__qdl.focusBack(scroll.inst, card);

  assert.strictEqual(scroll.inst.update, before, 'глушилка не ставилась вовсе');
  assert.deepStrictEqual(calls.updates, [card], 'уехавшую карточку обязаны подвезти в кадр');
});

test('focusBack: пустой last — прежнее поведение, фокус на первый .selector', () => {
  const { calls, scroll, w } = mount({ list: [F1] });
  calls.focused.length = 0;
  w.__qdl.focusBack(scroll.inst, null);
  assert.strictEqual(calls.focused[0], false, 'false → штатный collectionFocus берёт первый видимый');
});

test('focusBack: бросок изнутри collectionFocus не оставляет scroll.update заглушённым', () => {
  const { html, scroll, w } = mount({ list: [F1, F2] });
  const card = html.find('.card').eq(1)[0];
  seeOnScreen(card, 10);

  const upd = scroll.inst.update;
  w.Lampa.Controller.collectionFocus = () => { throw new Error('boom'); };
  assert.throws(() => w.__qdl.focusBack(scroll.inst, card), /boom/);
  assert.strictEqual(scroll.inst.update, upd, 'finally обязан вернуть update даже на исключении');
});

// ───────────────────── сторож от регресса новых экранов ─────────────────────

test('каждый экран с focusBack умеет писать last по пальцу и мыши', () => {
  const src = H.qdlSource();
  // Границы компонентов: от «function ComponentX(» до следующего такого объявления.
  const names = src.match(/function Component[A-Za-z]+\(/g).map((m) => m.slice(9, -1));
  const bad = [];
  for (let i = 0; i < names.length; i++) {
    const from = src.indexOf('function ' + names[i] + '(');
    const to = i + 1 < names.length ? src.indexOf('function ' + names[i + 1] + '(') : src.length;
    const body = src.slice(from, to);
    if (body.includes('focusBack(scroll, last)') && !body.includes('markLast(')) bad.push(names[i]);
  }
  assert.deepStrictEqual(bad, [],
    'экран восстанавливает фокус по last, но нигде его не пишет по hover:touch/hover:hover — ' +
    'на мыши и пальце он будет прыгать в начало');
});

test('markLast не вешается на строку с bgFocus (фон на таче не красим)', () => {
  const src = H.qdlSource();
  const bad = src.split('\n').filter((l) => l.includes('bgFocus(') && l.includes('hover:touch'));
  assert.deepStrictEqual(bad, [], 'touchstart во время пальцевого скролла красил бы случайные карточки');
});
