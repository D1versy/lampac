'use strict';
// Экран поиска jut.su: поле сверху + топ-50 недавнего (просмотренное вперёд, добор из выдач).
// Проверяем вход на экран, выбор клавиатуры по устройству, возврат фокуса после клавиатуры
// (штатный back у Input.edit уводит его в settings_component) и сборку ленты из /qdl/jut/recent.
//
// Тесты идут в реальном DOM: разметка экрана живёт внутри scroll.body(), и заглушка-Scroll
// прятала бы ровно то, что нужно проверить.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HEAD = '<div class="head"><div class="head__body"><div class="head__title">jut.su</div>' +
             '<div class="head__actions"></div></div></div>';

/**
 * jsdom + реальный jQuery + Scroll/Template, работающие с настоящим DOM.
 * opts: { items, fail, keyboard: 'lampa'|'integrate', mobile, platform }
 */
function boot(opts) {
  opts = opts || {};
  const calls = { urls: [], pushed: [], toggles: [], editCb: null, editParams: null };
  const lampa = H.makeLampa();

  lampa.Storage.field = (k) => (k === 'keyboard_type' ? (opts.keyboard || 'lampa') : undefined);
  lampa.Platform = { screen: () => !!opts.mobile, is: () => !!opts.mobile };
  lampa.Activity.push = (o) => calls.pushed.push(o);
  lampa.Controller.toggle = (n) => calls.toggles.push(n);
  lampa.Controller.collectionSet = () => {};
  lampa.Controller.collectionFocus = () => {};
  lampa.Controller.collectionAppend = () => {};
  lampa.Input = { edit(p, cb) { calls.editParams = p; calls.editCb = cb; } };
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok, err) => {
      calls.urls.push(url);
      if (opts.fail) err(); else ok({ ok: true, items: opts.items || [] });
    };
  };

  const ctx = H.loadQdlDom({
    bodyHtml: HEAD,
    lampa,
    windowExtra: opts.platform ? { d1vision_platform: opts.platform } : undefined,
  });
  const { w } = ctx;
  if (opts.platform) w.d1vision_platform = opts.platform;

  // Scroll, который реально кладёт содержимое в документ
  w.Lampa.Scroll = function () {
    const $box = w.$('<div class="scroll"></div>');
    w.document.body.appendChild($box[0]);
    this.render = () => $box;
    this.body = () => $box;
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => { $box.remove(); };
  };
  // Карточка — как в бандле: jQuery-узел с .card__img/.card__view
  w.Lampa.Template.get = (name, data) => w.$(
    '<div class="card selector"><div class="card__view"><img class="card__img" /></div>' +
    '<div class="card__title">' + ((data && data.title) || '') + '</div></div>');
  w.Lampa.Layer = { visible() {}, update() {} };

  return Object.assign(ctx, { calls, lampa: w.Lampa });
}

/** Создаёт компонент и МОНТИРУЕТ его разметку в документ (create() отдаёт открепленный узел). */
function makeScreen(opts) {
  const ctx = boot(opts);
  const comp = new ctx.qdl.ComponentJutSearch({});
  comp.activity = { loader() {}, toggle() { ctx.calls.toggles.push('activity'); } };

  const create = comp.create.bind(comp);
  comp.create = function () {
    const rendered = create();
    ctx.w.document.body.appendChild(rendered[0]);
    return rendered;
  };
  return Object.assign(ctx, { comp });
}

// ───────────────────────── вход на экран ─────────────────────────

test('плитка «Поиск» в каталоге ведёт на свой экран, а не сразу в клавиатуру', () => {
  // Раньше клавиатура открывалась поверх каталога, возвращаться было некуда,
  // и история выдачи нигде не жила.
  const ctx = boot({});
  const cat = new ctx.qdl.ComponentJutCatalog({});
  cat.activity = { loader() {}, toggle() {} };
  ctx.w.document.body.appendChild(cat.create()[0]);
  cat.appendSearchTile();

  const tile = ctx.doc.querySelector('.qdl-jut-search');
  assert.ok(tile, 'плитка поиска обязана быть в гриде');

  ctx.$(tile).trigger('hover:enter');
  assert.strictEqual(ctx.calls.pushed.length, 1);
  assert.strictEqual(ctx.calls.pushed[0].component, 'jut_search');
  assert.strictEqual(ctx.calls.editCb, null, 'клавиатура поверх каталога больше не открывается');
});

// ───────────────────────── лента недавнего ─────────────────────────

test('экран тянет топ-50 недавнего', () => {
  const { comp, calls } = makeScreen({ items: [] });
  comp.create();
  assert.ok(calls.urls.some((u) => u.includes('/qdl/jut/recent?limit=50')));
});

test('порядок сервера сохраняется: просмотренное впереди искомого', () => {
  // Приоритет считает сервер (он один знает про все устройства) — клиент не пересортировывает.
  const items = [
    { slug: 'watched-one', title: 'Смотрел', src: 'watch', episodes: 12 },
    { slug: 'searched-one', title: 'Искал', src: 'search', ongoing: true },
  ];
  const { comp, doc } = makeScreen({ items });
  comp.create();

  const titles = [...doc.querySelectorAll('.category-full .card__title')].map((e) => e.textContent);
  assert.deepStrictEqual(titles, ['Смотрел', 'Искал']);
});

test('пустая история даёт понятную заглушку во всю ширину', () => {
  // ⚠️ width:100% обязателен: .cols--N > * иначе сожмёт текст до ширины одной карточки.
  const { comp, doc, calls } = makeScreen({ items: [] });
  comp.create();

  const stub = doc.querySelector('.category-full > div');
  assert.ok(stub && /Пока пусто/.test(stub.textContent));
  assert.ok(/width:\s*100%/.test(stub.getAttribute('style') || ''));
  assert.ok(calls.toggles.includes('activity'), 'без activity.toggle экран остаётся глухим');
});

test('недоступный сервер не оставляет экран без фокуса', () => {
  const { comp, doc, calls } = makeScreen({ fail: true });
  comp.create();

  assert.ok(calls.toggles.includes('activity'));
  assert.ok(/Не удалось/.test(doc.querySelector('.category-full > div').textContent));
});

test('карточка ведёт на тайтл jut.su', () => {
  const { comp, doc, $, calls } = makeScreen({ items: [{ slug: 'spy-family', title: 'Spy x Family' }] });
  comp.create();

  $(doc.querySelector('.category-full .card')).trigger('hover:enter');
  assert.strictEqual(calls.pushed[0].component, 'jut_title');
  assert.strictEqual(calls.pushed[0].jut_slug, 'spy-family');
});

test('постеры ленивые: src ставится по событию visible, а не сразу', () => {
  const { comp, doc, $ } = makeScreen({ items: [{ slug: 'spy-family', title: 'Spy x Family' }] });
  comp.create();

  const img = doc.querySelector('.category-full .card__img');
  assert.ok(!img.getAttribute('src'), 'до показа постер не заказываем');

  $(doc.querySelector('.category-full .card')).trigger('visible');
  assert.ok(/\/qdl\/jut\/poster\?slug=spy-family/.test(img.getAttribute('src')));
});

// ───────────────────────── клавиатура по устройству ─────────────────────────

test('jutUseNativeInput: телефон и десктоп — системная клавиатура, ТВ — своя', () => {
  assert.strictEqual(boot({ keyboard: 'lampa' }).qdl.jutUseNativeInput(), false,
    'на ТВ нужна экранная клавиатура Lampa');
  assert.strictEqual(boot({ keyboard: 'integrate', mobile: true }).qdl.jutUseNativeInput(), true);
  assert.strictEqual(boot({ keyboard: 'lampa', platform: 'windows' }).qdl.jutUseNativeInput(), true,
    'у десктопа физическая клавиатура');
});

test('ТВ: после клавиатуры фокус возвращается в content, запрос уходит в каталог', () => {
  // 🔴 Штатный back у Lampa.Input.edit зовёт Controller.toggle('settings_component') —
  // без явного возврата экран остаётся глухим к пульту.
  const { comp, doc, $, calls } = makeScreen({ keyboard: 'lampa' });
  comp.create();

  const field = doc.querySelector('.qdl-jut-search-field');
  assert.ok(field, 'поле поиска обязано быть на экране');
  assert.ok(!doc.querySelector('.qdl-jut-search-field input'), 'на ТВ настоящий input не нужен');

  $(field).trigger('hover:enter');
  assert.ok(calls.editCb, 'enter на поле открывает клавиатуру Lampa');

  calls.editCb('наруто');
  assert.ok(calls.toggles.includes('content'), 'фокус возвращён на наш экран');
  assert.strictEqual(calls.pushed.length, 1);
  assert.strictEqual(calls.pushed[0].component, 'jut_catalog');
  assert.strictEqual(calls.pushed[0].jut_query, 'наруто');

  calls.editCb('   ');
  assert.strictEqual(calls.pushed.length, 1, 'пустой запрос активность не толкает');
});

test('мобила: настоящий input, Enter ищет, клавиши не утекают в движок', () => {
  const { comp, doc, w, calls } = makeScreen({ keyboard: 'integrate', mobile: true });
  comp.create();

  const input = doc.querySelector('.qdl-jut-search-field input');
  assert.ok(input, 'на телефоне нужен настоящий input — он и поднимает системную клавиатуру');

  // Движок Lampa и desktop.js слушают keydown без preventDefault: без stopPropagation
  // набор текста дёргал бы навигацию и скролл.
  let bubbled = 0;
  w.document.addEventListener('keydown', () => bubbled++);

  input.value = 'блич';
  input.dispatchEvent(new w.KeyboardEvent('keydown', { keyCode: 13, which: 13, bubbles: true }));

  assert.strictEqual(bubbled, 0, 'stopPropagation обязателен');
  assert.strictEqual(calls.pushed.length, 1);
  assert.strictEqual(calls.pushed[0].jut_query, 'блич');
});
