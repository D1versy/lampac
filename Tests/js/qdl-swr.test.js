'use strict';
// Свежесть рядов каталога (qdl 2.63): кеш остаётся ради мгновенной отрисовки, но состав и
// ПОРЯДОК диктует сервер. Патч бандла (/*qdl-cut:swr*/) при попадании в живой кеш отдаёт снимок
// как раньше и параллельно дотягивает свежий ответ → 'request_revalidate'. Здесь проверяется
// вся политика: реестр рядов, сопоставление, диф, перестройка на месте, защита от прыжков,
// троттлинг и выключатель.
//
// 🔥 Ключевой инвариант: перестраиваем ТОЛЬКО содержимое существующего ряда. Ни Api.main, ни
// Main.build звать нельзя (массив загрузчиков расходуемый, build не идемпотентен) — иначе дубли.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

/** Мок ряда в объёме, который трогает swrRebuild (Line$5 из бандла). */
function makeLine(url, ids, extra) {
  const destroyed = [];
  const appended = [];
  const line = {
    data: {
      qdl_req: url,
      url: '?sort=now_playing',        // в самом ряду лежит МЕТОД, не полный адрес — отсюда метка
      results: ids.map((id) => ({ id })),
      total_pages: 1,
    },
    params: { items: { view: 7 } },
    items: ids.map((id) => ({ id, destroy() { destroyed.push(id); } })),
    active: 3,
    last: {},
    more: {},
    html: { parentNode: {}, contains: () => false, querySelector: () => null },
    scroll: {
      cleared: 0,
      reseted: 0,
      clear() { this.cleared++; },
      reset() { this.reseted++; },
      render: () => ({ tag: 'scroll' }),
    },
    emit(ev, el) { if (ev === 'createAndAppend') appended.push(el); },
    _destroyed: destroyed,
    _appended: appended,
  };
  return Object.assign(line, extra || {});
}

/**
 * Окружение с НАСТОЯЩИМ pub/sub Listener и теми частями Lampa, которые нужны SWR.
 * ⚠️ Общий makeLampa не трогаем: добавление Controller.own в него перевернуло бы гейты
 * компонентных тестов (паттерн локального override — как в qdl-mirror.test.js).
 */
function env(over) {
  const subs = {};
  const lampa = H.makeLampa(Object.assign({
    Listener: {
      follow(type, fn) { (subs[type] = subs[type] || []).push(fn); },
      send(type, data) { (subs[type] || []).forEach((fn) => fn(data)); },
      remove() {},
    },
    Controller: {
      own: () => false,
      collectionSet() {},
      listener: { follow() {} },
    },
    Arrays: { destroy(items) { (items || []).forEach((i) => i && i.destroy && i.destroy()); } },
    Layer: { visible() {} },
  }, over || {}));
  const r = H.loadQdl({ lampa });
  r.lampa = lampa;
  r.subs = subs;
  r.qdl.swrReset();
  return r;
}

const URL1 = 'https://tmdb.cub/?sort=now_playing&page=1&email=';

// ─────────────────────────── контракт с патчем бандла ───────────────────────────

test('initSwr публикует window.qdl_swr — без него патч бандла не догоняет ничего', () => {
  const r = env();
  r.qdl.initSwr();
  assert.strictEqual(typeof r.sandbox.window.qdl_swr, 'function');
});

// ─────────────────────────── реестр рядов ───────────────────────────

test('реестр: ряд с меткой попадает, без метки (персоны) — нет, destroy убирает', () => {
  const r = env();
  const withMark = makeLine(URL1, [1, 2, 3]);
  const noMark = makeLine(null, [4, 5]);
  noMark.data.qdl_req = null;

  r.qdl.swrOnLine({ type: 'create', line: withMark });
  r.qdl.swrOnLine({ type: 'create', line: noMark });
  assert.strictEqual(r.qdl.swrState().lines.length, 1, 'персонский ряд ревалидировать нечем');

  r.qdl.swrOnLine({ type: 'create', line: withMark });   // повтор не должен дублировать
  assert.strictEqual(r.qdl.swrState().lines.length, 1);

  r.qdl.swrOnLine({ type: 'destroy', line: withMark });
  assert.strictEqual(r.qdl.swrState().lines.length, 0);
});

test('реестр игнорирует deprecated-форму события (нет line.emit)', () => {
  const r = env();
  r.qdl.swrOnLine({ type: 'create', line: { data: { qdl_req: URL1 } } });
  assert.strictEqual(r.qdl.swrState().lines.length, 0);
});

// ─────────────────────────── диф ───────────────────────────

test('диф: тот же состав и порядок → перестройки НЕТ', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2, 3]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 1 }, { id: 2 }, { id: 3 }] } });

  assert.strictEqual(line._appended.length, 0);
  assert.strictEqual(line._destroyed.length, 0);
  assert.strictEqual(line.scroll.cleared, 0);
});

test('диф: сменился ПОРЯДОК при том же составе → перестройка есть', () => {
  // Ровно то, о чём просил владелец: «поменялся порядок по популярности — клиент перестраивает»
  const r = env();
  const line = makeLine(URL1, [1, 2, 3]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 3 }, { id: 2 }, { id: 1 }] } });

  assert.strictEqual(line._appended.length, 3);
  assert.deepStrictEqual(line.data.results.map((c) => c.id), [3, 2, 1]);
});

test('диф: появился новый фильм → перестройка', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2, 3]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }, { id: 1 }, { id: 2 }, { id: 3 }] } });

  assert.strictEqual(line.data.results[0].id, 9);
  assert.strictEqual(line._appended.length, 4);
});

// ─────────────────────────── перестройка на месте ───────────────────────────

test('перестройка: старые карточки уничтожены ДО очистки, состояние ряда сброшено', () => {
  // 🔴 Arrays.destroy обязателен и обязан идти первым: карточные onDestroy снимают подписки и
  // обнуляют обработчики картинок — иначе утечка на каждой перестройке.
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 7 }, { id: 8 }], total_pages: 4 } });

  assert.deepStrictEqual(line._destroyed, [1, 2]);
  assert.strictEqual(line.items.length, 0);
  assert.strictEqual(line.scroll.cleared, 1);
  assert.strictEqual(line.scroll.reseted, 1);
  assert.strictEqual(line.active, 0);
  assert.strictEqual(line.last, null);
  assert.strictEqual(line.more, null, 'без сброса кнопка «ещё» больше не появится');
  assert.strictEqual(line.data.total_pages, 4);
});

test('перестройка добирает не больше видимых карточек (params.items.view)', () => {
  const r = env();
  const line = makeLine(URL1, [1]);
  line.params.items.view = 2;
  r.qdl.swrOnLine({ type: 'create', line });
  const many = Array.from({ length: 20 }, (_, i) => ({ id: 100 + i }));
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: many } });

  assert.strictEqual(line._appended.length, 2, 'остальное доберёт штатный скролл');
  assert.strictEqual(line.data.results.length, 20);
});

test('пустой свежий ответ ряд НЕ гасит', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [] } });

  assert.strictEqual(line.scroll.cleared, 0);
  assert.deepStrictEqual(line.data.results.map((c) => c.id), [1, 2]);
});

test('перестройка переносит стиль карточек (широкие в UHD-ряду и «Трейлерах»)', () => {
  // Стиль ряду проставляет загрузчик бандла, свежий ответ сервера про него не знает —
  // без переноса ряд из широких карточек молча стал бы обычным.
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  line.data.results.forEach((c) => { c.params = { style: { name: 'wide' } }; });
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 5 }, { id: 6 }] } });

  assert.strictEqual(line.data.results[0].params.style.name, 'wide');
  assert.strictEqual(line.data.results[1].params.style.name, 'wide');
});

// ─────────────────────────── защита от прыжков под зрителем ───────────────────────────

test('зритель внутри ряда (владеет управлением) → перестройки нет, ответ ждёт выхода', () => {
  const r = env({ Controller: { own: () => true, collectionSet() {}, listener: { follow() {} } } });
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }] } });

  assert.strictEqual(line.scroll.cleared, 0, 'карточка не должна уехать из-под пальца');
  assert.ok(r.qdl.swrState().pending[URL1], 'ответ сохранён до выхода из ряда');
});

test('после выхода из ряда отложенная перестройка дожимается', () => {
  let inside = true;
  const r = env({ Controller: { own: () => inside, collectionSet() {}, listener: { follow() {} } } });
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }] } });
  assert.strictEqual(line.scroll.cleared, 0);

  inside = false;
  r.qdl.swrOnLine({ type: 'toggle', line });   // ушёл из ряда

  assert.strictEqual(line.scroll.cleared, 1);
  assert.strictEqual(r.qdl.swrState().pending[URL1], undefined);
});

test('верхний ряд при входе на экран обновляется, хотя фокус по умолчанию в нём', () => {
  // 🔴 Без этого исключения фича была невидима: при открытии главной фокус стоит на первой
  // карточке верхнего ряда, и самый заметный ряд обновлялся только после ухода из него.
  const r = env({ Controller: { own: () => true, collectionSet() {}, listener: { follow() {} } } });
  const line = makeLine(URL1, [1, 2, 3]);
  line.active = 0;                       // зритель ещё не листал ряд
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }, { id: 1 }, { id: 2 }] } });

  assert.strictEqual(line.scroll.cleared, 1, 'ряд перестроен');
  assert.strictEqual(line.data.results[0].id, 9);
});

test('но стоит зрителю сдвинуться внутри ряда — перестройка снова откладывается', () => {
  const r = env({ Controller: { own: () => true, collectionSet() {}, listener: { follow() {} } } });
  const line = makeLine(URL1, [1, 2, 3]);
  line.active = 2;                       // листает ряд — трогать нельзя
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }] } });

  assert.strictEqual(line.scroll.cleared, 0);
  assert.ok(r.qdl.swrState().pending[URL1]);
});

test('сфокусированная карточка внутри ряда (ТВ) тоже считается «зритель внутри»', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  line.html.querySelector = (sel) => (sel === '.selector.focus' ? {} : null);
  assert.strictEqual(r.qdl.swrBusyLine(line), true);
});

test('открытый селектбокс останавливает любую перестройку', () => {
  const r = H.loadQdlDom({ bodyHtml: '<div></div>' });
  r.$('body').addClass('selectbox--open');
  assert.strictEqual(r.qdl.swrBusyScreen(), true);
});

// ─────────────────────────── троттлинг и выключатель ───────────────────────────

test('троттлинг: повторный догон того же адреса в пределах окна отклоняется', () => {
  const r = env();
  const params = { url: URL1, cache: { life: 60 * 24 * 2 } };
  assert.strictEqual(r.qdl.swrGate(params), true);
  assert.strictEqual(r.qdl.swrGate(params), false, 'не чаще раза в 10 минут на адрес');
});

test('троттлинг: короткий кеш, POST и наши собственные ручки не догоняются', () => {
  const r = env();
  assert.strictEqual(r.qdl.swrGate({ url: 'https://x/a', cache: { life: 5 } }), false, 'это не ряд каталога');
  assert.strictEqual(r.qdl.swrGate({ url: 'https://x/b', cache: { life: 999 }, post_data: {} }), false);
  assert.strictEqual(r.qdl.swrGate({ url: '{localhost}/qdl/list', cache: { life: 999 } }), false);
  assert.strictEqual(r.qdl.swrGate({ url: '{localhost}/d1vision/hosts.json', cache: { life: 999 } }), false);
  // 🔴 А вот ряды каталога идут через НАШ ЖЕ сервер — раньше фильтр «всё, что начинается с API»
  // отсекал их разом, и фича молча не работала ни на одном ряду.
  assert.strictEqual(r.qdl.swrGate({ url: '{localhost}/cub/tmdb./?sort=now_playing&page=1', cache: { life: 2880 } }), true);
  assert.strictEqual(r.qdl.swrGate({ url: '{localhost}/tmdb/api/3/movie/popular', cache: { life: 2880 } }), true);
  assert.strictEqual(r.qdl.swrGate({ cache: { life: 999 } }), false);
});

test('троттлинг: бюджет ограничивает пачку догонов', () => {
  const r = env();
  let allowed = 0;
  for (let i = 0; i < 30; i++) {
    if (r.qdl.swrGate({ url: 'https://tmdb.cub/row' + i, cache: { life: 999 } })) allowed++;
  }
  assert.ok(allowed <= 12, 'первый экран 6 рядов + дозагрузка, остальное отсекаем: ' + allowed);
  assert.ok(allowed >= 6, 'но первый экран догнать обязаны: ' + allowed);
});

test('выключатель qdl_swr_off гасит и догон, и перестройку', () => {
  const r = env();
  r.lampa.Storage.set('qdl_swr_off', true);
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }] } });

  assert.strictEqual(r.qdl.swrGate({ url: URL1, cache: { life: 999 } }), false);
  assert.strictEqual(line.scroll.cleared, 0);
});

// ─────────────────────────── устойчивость ───────────────────────────

test('мусорные события не роняют обработчики', () => {
  // 🔴 Рассылка в бандле обёрнута ОДНИМ общим try: исключение отсюда оборвало бы остальных
  // подписчиков события 'line'.
  const r = env();
  assert.doesNotThrow(() => {
    r.qdl.swrOnLine(null);
    r.qdl.swrOnLine({});
    r.qdl.swrOnLine({ type: 'create' });
    r.qdl.swrOnRevalidate(null);
    r.qdl.swrOnRevalidate({ url: URL1 });
    r.qdl.swrFlush();
  });
});

test('перестройка не входит сама в себя (её же события line игнорируются)', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  // добор карточек шлёт 'line' append — если бы обработчик реагировал, получили бы рекурсию
  line.emit = function (ev, el) {
    if (ev !== 'createAndAppend') return;
    this._appended.push(el);
    r.qdl.swrOnLine({ type: 'append', line });
  };
  r.qdl.swrOnLine({ type: 'create', line });
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }, { id: 8 }] } });

  assert.strictEqual(line.scroll.cleared, 1, 'ровно одна перестройка');
  assert.strictEqual(line._appended.length, 2);
});

test('ряд, выброшенный из DOM, выпадает из реестра и не перестраивается', () => {
  const r = env();
  const line = makeLine(URL1, [1, 2]);
  r.qdl.swrOnLine({ type: 'create', line });
  line.html.parentNode = null;
  r.qdl.swrOnRevalidate({ url: URL1, data: { results: [{ id: 9 }] } });

  assert.strictEqual(line.scroll.cleared, 0);
  assert.strictEqual(r.qdl.swrState().lines.length, 0);
});
