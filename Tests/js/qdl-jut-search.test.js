'use strict';
// Экран поиска jut.su: поле сверху + топ-50 недавнего, выдача НА ТОМ ЖЕ экране сеткой
// (qdl 2.51; с 2.121 — на общей фабрике makeSearchScreen вместе с d1v_search).
// Проверяем вход на экран, выбор клавиатуры по устройству, возврат фокуса после клавиатуры
// (штатный back у Input.edit уводит его в settings_component), что поле ПОКАЗЫВАЕТ запрос
// (жалоба владельца «набранного текста не видно»: до 2.121 на ТВ стояла статичная подсказка,
// а выдача уезжала в jut_catalog без поля), постраничку с префетчем и сборку ленты из /qdl/jut/recent.
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
 * opts: { items, results, hasNext, stale, fail, keyboard: 'lampa'|'integrate', mobile, platform }
 *   items   — ответ /qdl/jut/recent;  results — ответ /qdl/jut/search (по умолчанию = items)
 */
function boot(opts) {
  opts = opts || {};
  const calls = { urls: [], pushed: [], toggles: [], editCb: null, editParams: null, noty: [] };
  const lampa = H.makeLampa();

  lampa.Storage.field = (k) => (k === 'keyboard_type' ? (opts.keyboard || 'lampa') : undefined);
  lampa.Platform = { screen: () => !!opts.mobile, is: () => !!opts.mobile };
  lampa.Activity.push = (o) => calls.pushed.push(o);
  lampa.Controller.toggle = (n) => calls.toggles.push(n);
  lampa.Controller.collectionSet = () => {};
  lampa.Controller.collectionFocus = () => {};
  lampa.Controller.collectionAppend = () => {};
  lampa.Input = { edit(p, cb) { calls.editParams = p; calls.editCb = cb; } };
  lampa.Noty = { show: (t) => calls.noty.push(t) };
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok, err) => {
      calls.urls.push(url);
      if (opts.fail) return err();
      if (url.includes('/qdl/jut/search')) ok({ ok: true, items: opts.results || opts.items || [], hasNext: !!opts.hasNext, stale: !!opts.stale });
      else ok({ ok: true, items: opts.items || [] });
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
function makeScreen(opts, object) {
  const ctx = boot(opts);
  const comp = new ctx.qdl.ComponentJutSearch(object || {});
  comp.activity = { loader() {}, toggle() { ctx.calls.toggles.push('activity'); } };

  const create = comp.create.bind(comp);
  comp.create = function () {
    const rendered = create();
    ctx.w.document.body.appendChild(rendered[0]);
    return rendered;
  };
  return Object.assign(ctx, { comp });
}

const titles = (doc) => [...doc.querySelectorAll('.category-full .card__title')].map((e) => e.textContent);
const searchUrl = (q, page) => '/qdl/jut/search?query=' + encodeURIComponent(q) + '&page=' + (page || 1);

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
  assert.deepStrictEqual(titles(doc), ['Смотрел', 'Искал']);
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

test('jutUseNativeInput: телефон и десктоп — системная клавиатура, ТВ и Apple TV — экранная', () => {
  assert.strictEqual(boot({ keyboard: 'lampa' }).qdl.jutUseNativeInput(), false,
    'на ТВ нужна экранная клавиатура Lampa');
  assert.strictEqual(boot({ keyboard: 'integrate', mobile: true }).qdl.jutUseNativeInput(), true);
  assert.strictEqual(boot({ keyboard: 'lampa', platform: 'windows' }).qdl.jutUseNativeInput(), true,
    'у десктопа физическая клавиатура');
  // 🔴 tvOS: приватный WebKit Apple TV системную клавиатуру по focus() не поднимет,
  // а пульт там уже переведён в клавиши Lampa — экранная, что бы ни стояло в keyboard_type.
  assert.strictEqual(boot({ keyboard: 'integrate', platform: 'tvos' }).qdl.jutUseNativeInput(), false);
});

test('ТВ: после клавиатуры фокус возвращается в content, выдача на том же экране, поле показывает запрос', () => {
  // 🔴 Штатный back у Lampa.Input.edit зовёт Controller.toggle('settings_component') —
  // без явного возврата экран остаётся глухим к пульту.
  const { comp, doc, $, calls } = makeScreen({ keyboard: 'lampa', results: [{ slug: 'naruto', title: 'Наруто' }] });
  comp.create();

  const field = doc.querySelector('.d1v-search-field');
  assert.ok(field, 'поле поиска обязано быть на экране');
  assert.ok(!doc.querySelector('.d1v-search-field input'), 'на ТВ настоящий input не нужен');
  const text = doc.querySelector('.d1v-search-text');
  assert.ok(text && text.classList.contains('d1v-search-hint') && /Название аниме/.test(text.textContent), 'пустое поле — подсказка');

  $(field).trigger('hover:enter');
  assert.ok(calls.editCb, 'enter на поле открывает клавиатуру Lampa');
  // Раскладка как в XSMART, без полосы «ссылок», поле над клавиатурой не прячется (keyboard:'lampa').
  assert.strictEqual(calls.editParams.keyboard, 'lampa');
  assert.strictEqual(calls.editParams.nosave, true);
  assert.strictEqual(calls.editParams.value, '');
  assert.ok(calls.editParams.layout && calls.editParams.layout.sim && calls.editParams.layout['default'], 'объектная раскладка со слоем sim');

  calls.editCb('наруто');
  assert.ok(calls.toggles.includes('content'), 'фокус возвращён на наш экран');
  assert.strictEqual(calls.pushed.length, 0, 'выдача рисуется на том же экране — каталог не толкаем');
  assert.ok(calls.urls.some((u) => u.includes(searchUrl('наруто'))), 'запрос ушёл в /qdl/jut/search');
  assert.deepStrictEqual(titles(doc), ['Наруто']);
  // 🔴 Фикс жалобы владельца: в поле стоит запрос, а не подсказка.
  assert.strictEqual(text.textContent, 'наруто');
  assert.ok(!text.classList.contains('d1v-search-hint'));

  // Колбэк Input.edit приходит и на Back с прежним значением — повторный поиск не запускаем.
  const n = calls.urls.length;
  $(field).trigger('hover:enter');
  assert.strictEqual(calls.editParams.value, 'наруто', 'клавиатура открывается с текущим запросом');
  calls.editCb('наруто');
  assert.strictEqual(calls.urls.length, n, 'тот же запрос не перезапускаем');
  $(field).trigger('hover:enter');
  calls.editCb('   ');
  assert.strictEqual(calls.urls.length, n, 'пустой запрос не ищем');
  assert.strictEqual(text.textContent, 'наруто', 'отмена клавиатуры не стирает поле');
});

test('мобила: настоящий input, Enter ищет, клавиши не утекают в движок, после ответа фокус снят с поля', () => {
  const { comp, doc, w, calls } = makeScreen({ keyboard: 'integrate', mobile: true, results: [{ slug: 'bleach', title: 'Блич' }] });
  comp.create();

  const input = doc.querySelector('.d1v-search-field input');
  assert.ok(input, 'на телефоне нужен настоящий input — он и поднимает системную клавиатуру');

  // Движок Lampa и desktop.js слушают keydown без preventDefault: без stopPropagation
  // набор текста дёргал бы навигацию и скролл.
  let bubbled = 0;
  w.document.addEventListener('keydown', () => bubbled++);

  input.focus();
  input.value = 'блич';
  input.dispatchEvent(new w.KeyboardEvent('keydown', { keyCode: 13, which: 13, bubbles: true }));

  assert.strictEqual(bubbled, 0, 'stopPropagation обязателен');
  assert.ok(calls.urls.some((u) => u.includes(searchUrl('блич'))));
  assert.strictEqual(calls.pushed.length, 0, 'выдача на том же экране');
  assert.deepStrictEqual(titles(doc), ['Блич']);
  assert.strictEqual(input.value, 'блич', 'запрос остаётся в поле');
  // 🔴 Тем же Enter, что запустил поиск, hover:enter вернул бы DOM-фокус в <input>, и экран
  // глох бы к стрелкам — после ответа фокус с поля снимается.
  assert.notStrictEqual(doc.activeElement, input, 'после ответа input обязан быть без DOM-фокуса');
});

// ───────────────────────── постраничка ─────────────────────────

test('выдача постраничная: префетч за 12 карточек до конца, дедуп по slug, страница 2 без activity.toggle', () => {
  const results = [];
  for (let i = 0; i < 14; i++) results.push({ slug: 's' + i, title: 'T' + i });
  const { comp, doc, $, calls } = makeScreen({ keyboard: 'lampa', results, hasNext: true });
  comp.create();
  $(doc.querySelector('.d1v-search-field')).trigger('hover:enter');
  calls.editCb('аниме');
  assert.strictEqual(titles(doc).length, 14);
  const togglesAfterPage1 = calls.toggles.filter((t) => t === 'activity').length;

  // фокус на 3-й с конца карточке → догрузка второй страницы
  const cards = doc.querySelectorAll('.category-full .card');
  $(cards[cards.length - 3]).trigger('hover:focus');
  assert.ok(calls.urls.some((u) => u.includes(searchUrl('аниме', 2))), 'страница 2 запрошена заранее');
  // сервер отдал те же slug'и — дублей в сетке нет
  assert.strictEqual(titles(doc).length, 14, 'дедуп по slug');
  assert.strictEqual(calls.toggles.filter((t) => t === 'activity').length, togglesAfterPage1,
    'на догрузке activity.toggle не зовём — он утаскивал фокус и скролл в начало ленты');
});

test('запрос в объекте активности ищет сразу; autokb открывает клавиатуру один раз и снимает флаг с объекта', async () => {
  const object = { autokb: true };
  const { comp, calls } = makeScreen({ keyboard: 'lampa', items: [] }, object);
  comp.create();
  // 🔴 флаг снят синхронно: объект активности уезжает в Storage 'activity' и в адрес —
  // иначе клавиатура всплывала бы при старте приложения и при Back из стека
  assert.strictEqual(object.autokb, false);
  await new Promise((r) => setTimeout(r, 10));
  assert.ok(calls.editCb, 'клавиатура открыта сама (кнопка поиска в шапке)');

  const withQuery = makeScreen({ keyboard: 'lampa', results: [{ slug: 'x', title: 'X' }] }, { query: 'блич' });
  withQuery.comp.create();
  assert.ok(withQuery.calls.urls.some((u) => u.includes(searchUrl('блич'))), 'готовый запрос ищется без клавиатуры');
  assert.strictEqual(withQuery.doc.querySelector('.d1v-search-text').textContent, 'блич');
  assert.strictEqual(withQuery.calls.editCb, null);
});
