'use strict';
// Общий экран поиска Lampa — d1v_search (qdl 2.121), «как в XSMART»: поле сверху, чипы недавних
// запросов, «Недавнее» из истории просмотров, выдача TMDB (search/multi) сеткой НА ТОМ ЖЕ экране.
// Заменяет штатный оверлей Lampa (клавиатура слева, ряды по источникам) — тот остаётся за чипом
// «Штатный поиск». Тесты в реальном DOM (jsdom + настоящий jQuery), как qdl-jut-search.test.js.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

/**
 * opts: { keyboard, mobile, platform, replica, results, pages: {n: [...]}, totalPages, fail,
 *         history (Favorite history), searchHistory (Storage search_history) }
 */
function boot(opts) {
  opts = opts || {};
  const calls = { get: [], pushed: [], toggles: [], editCb: null, editParams: null, urls: [], orig: [], noty: [] };
  const lampa = H.makeLampa();
  lampa.Storage.field = (k) => (k === 'keyboard_type' ? (opts.keyboard || 'lampa') : undefined);
  lampa.Platform = { screen: () => !!opts.mobile, is: () => !!opts.mobile };
  lampa.Activity.push = (o) => calls.pushed.push(o);
  lampa.Activity.active = () => ({ component: 'd1v_search' });
  lampa.Controller.add = () => {};
  lampa.Controller.toggle = (n) => calls.toggles.push(n);
  lampa.Controller.enabled = () => ({ name: 'content' });
  lampa.Controller.collectionSet = () => {};
  lampa.Controller.collectionFocus = () => {};
  lampa.Controller.collectionAppend = () => {};
  lampa.Input = { edit(p, cb) { calls.editParams = p; calls.editCb = cb; } };
  lampa.Noty = { show: (t) => calls.noty.push(t) };
  lampa.Search = { open(p) { calls.orig.push(p); } };
  lampa.Api = {
    img: (p, s) => '/api-img/' + s + p,
    sources: {
      tmdb: {
        img: (p, s) => '/tmdb/' + s + p,
        get(method, params, ok, err) {
          calls.get.push({ method, params });
          if (opts.fail) return err();
          const pages = opts.pages || {};
          const r = pages[params.page] || opts.results || [];
          ok({ results: r, total_pages: opts.totalPages || 1, page: params.page });
        },
      },
    },
  };
  lampa.Favorite.get = (p) => (p && p.type === 'history' ? (opts.history || []) : []);
  lampa.Utils.cardImgBackground = (c) => (c.backdrop_path ? '/bg' + c.backdrop_path : '');
  // /qdl/list (меню долгого нажатия) только фиксируем — дальше ушёл бы Select с чужими стабами
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => { calls.urls.push(url); if (!url.includes('/qdl/list')) ok({ ok: true, items: [] }); };
  };
  if (opts.searchHistory) lampa.Storage.set('search_history', opts.searchHistory);

  const ctx = H.loadQdlDom({ bodyHtml: '', lampa });
  const { w } = ctx;
  if (opts.platform) w.d1vision_platform = opts.platform;
  if (opts.replica) w.qdl_replica = true;
  w.Lampa.Scroll = function () {
    const $box = w.$('<div class="scroll"></div>');
    w.document.body.appendChild($box[0]);
    this.render = () => $box;
    this.body = () => $box;
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => { $box.remove(); };
  };
  // Карточка — как в бандле: .card__view/.card__img/.card__title/.card__age
  w.Lampa.Template.get = (name, data) => w.$(
    '<div class="card selector"><div class="card__view"><img class="card__img" /></div>' +
    '<div class="card__title">' + ((data && data.title) || '') + '</div>' +
    '<div class="card__age">' + ((data && data.release_year) || '') + '</div></div>');
  w.Lampa.Layer = { visible() {}, update() {} };
  return Object.assign(ctx, { calls });
}

function makeScreen(opts, object) {
  const ctx = boot(opts);
  const comp = new ctx.qdl.ComponentD1VSearch(object || {});
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
const cards = (doc) => [...doc.querySelectorAll('.category-full .card')];
const chips = (doc) => [...doc.querySelectorAll('.d1v-search-chip')].map((e) => e.textContent);
const search = (ctx, q) => { ctx.$(ctx.doc.querySelector('.d1v-search-field')).trigger('hover:enter'); ctx.calls.editCb(q); };

const MOVIE = { id: 603, media_type: 'movie', title: 'Матрица', release_date: '1999-03-31', poster_path: '/m.jpg', backdrop_path: '/mb.jpg' };
const TV = { id: 1399, media_type: 'tv', name: 'Игра престолов', first_air_date: '2011-04-17', poster_path: '/g.jpg' };
const PERSON = { id: 287, media_type: 'person', name: 'Брэд Питт', profile_path: '/p.jpg', gender: 2, known_for_department: 'Acting' };

test('ТВ: поле показывает запрос, клавиатура как в XSMART, Back с тем же значением не ищет повторно', () => {
  const { comp, doc, calls, $ } = makeScreen({ keyboard: 'lampa', results: [MOVIE] });
  comp.create();
  const text = doc.querySelector('.d1v-search-text');
  assert.ok(text && text.classList.contains('d1v-search-hint'), 'пустое поле — подсказка');
  assert.ok(!doc.querySelector('.d1v-search-field input'), 'на ТВ настоящий input не нужен');

  search({ doc, calls, $ }, 'матрица');
  assert.strictEqual(calls.editParams.keyboard, 'lampa', 'поле над клавиатурой не прячется, что бы ни стояло в keyboard_type');
  assert.strictEqual(calls.editParams.nosave, true, 'без полосы «Добавить это значение / 127.0.0.1:8090»');
  assert.strictEqual(calls.editParams.free, true);
  assert.ok(calls.editParams.layout && calls.editParams.layout.sim, 'объектная раскладка со слоем sim');
  assert.ok(calls.toggles.includes('content'), 'после клавиатуры фокус возвращён экрану');
  assert.strictEqual(text.textContent, 'матрица', '🔴 фикс жалобы: в поле стоит запрос');
  assert.ok(!text.classList.contains('d1v-search-hint'));
  assert.deepStrictEqual(titles(doc), ['Матрица']);

  const n = calls.get.length;
  search({ doc, calls, $ }, 'матрица');
  assert.strictEqual(calls.editParams.value, 'матрица', 'клавиатура открывается с текущим запросом');
  assert.strictEqual(calls.get.length, n, 'Back с прежним значением поиск не перезапускает');
  search({ doc, calls, $ }, 'м');
  assert.strictEqual(calls.get.length, n, 'короче minLen — не ищем');
  assert.ok(calls.noty.some((t) => /от 2 символов/.test(t)));
});

test('выдача: search/multi тем же путём, что у Lampa; source=tmdb; чужие типы и DMCA выброшены; персоны остаются', () => {
  const results = [MOVIE, { id: 10, media_type: 'collection', name: 'Сборник' }, TV, { id: 999, media_type: 'movie', title: 'Запрещённое', release_date: '2020-01-01' }, PERSON];
  const ctx = makeScreen({ keyboard: 'lampa', results });
  ctx.qdl.setDmcaList([{ id: 999, cat: 'movie' }]);
  ctx.comp.create();
  search(ctx, 'матрица');

  // объекты из vm-песочницы — другой realm, deepStrictEqual на них падает: сравниваем по JSON
  assert.strictEqual(JSON.stringify(ctx.calls.get[0]), JSON.stringify({ method: 'search/multi', params: { query: encodeURIComponent('матрица'), page: 1 } }));
  assert.deepStrictEqual(titles(ctx.doc), ['Матрица', 'Игра престолов', 'Брэд Питт']);

  const [movie, tv, person] = cards(ctx.doc);
  assert.ok(tv.classList.contains('card--tv') && /TV/.test(tv.querySelector('.card__type').textContent), 'сериал помечен');
  assert.ok(!movie.classList.contains('card--tv'));
  assert.ok(!person.querySelector('.card__age'), 'у персоны нет года — пустой блок убран');
  assert.strictEqual(tv.querySelector('.card__age').textContent, '2011');

  ctx.$(tv).trigger('visible');
  assert.strictEqual(tv.querySelector('.card__img').getAttribute('src'), '/tmdb/w300/g.jpg', 'постер через TMDB-источник (наш прокси)');

  ctx.$(tv).trigger('hover:enter');
  let o = ctx.calls.pushed.pop();
  assert.strictEqual(o.component, 'full');
  assert.strictEqual(o.method, 'tv');
  assert.strictEqual(o.id, 1399);
  assert.strictEqual(o.source, 'tmdb');
  assert.strictEqual(o.card.source, 'tmdb', 'source проставлен — его читают onCardMenu и полная карточка');

  ctx.$(movie).trigger('hover:enter');
  assert.strictEqual(ctx.calls.pushed.pop().method, 'movie');

  ctx.$(person).trigger('hover:enter');
  o = ctx.calls.pushed.pop();
  assert.strictEqual(o.component, 'actor');
  assert.strictEqual(o.id, 287);
});

test('страница 2 догружается по фокусу на хвосте выдачи; total_pages — граница', () => {
  const page1 = [], page2 = [];
  for (let i = 0; i < 14; i++) page1.push({ id: 100 + i, media_type: 'movie', title: 'A' + i });
  for (let i = 0; i < 3; i++) page2.push({ id: 200 + i, media_type: 'movie', title: 'B' + i });
  page2.push(page1[0]);   // TMDB иногда повторяет — дедуп по типу+id
  const ctx = makeScreen({ keyboard: 'lampa', pages: { 1: page1, 2: page2 }, totalPages: 2 });
  ctx.comp.create();
  search(ctx, 'фильм');
  assert.strictEqual(titles(ctx.doc).length, 14);
  const toggles = ctx.calls.toggles.filter((t) => t === 'activity').length;

  const list = cards(ctx.doc);
  ctx.$(list[list.length - 5]).trigger('hover:focus');
  assert.strictEqual(ctx.calls.get.length, 2);
  assert.strictEqual(ctx.calls.get[1].params.page, 2);
  assert.strictEqual(titles(ctx.doc).length, 17, 'три новых, дубль отброшен');
  assert.strictEqual(ctx.calls.toggles.filter((t) => t === 'activity').length, toggles, 'догрузка без activity.toggle');

  const again = cards(ctx.doc);
  ctx.$(again[again.length - 1]).trigger('hover:focus');
  assert.strictEqual(ctx.calls.get.length, 2, 'за total_pages не ходим');
});

test('пустая выдача и ошибка источника дают текст и возвращают фокус экрану', () => {
  const empty = makeScreen({ keyboard: 'lampa', results: [] });
  empty.comp.create();
  search(empty, 'нетакого');
  assert.ok(/Ничего не найдено/.test(empty.doc.querySelector('.category-full > div').textContent));
  assert.ok(empty.calls.toggles.includes('activity'));

  const fail = makeScreen({ keyboard: 'lampa', fail: true });
  fail.comp.create();
  search(fail, 'дюна');
  assert.ok(/Поиск недоступен/.test(fail.doc.querySelector('.category-full > div').textContent));
  assert.ok(fail.calls.toggles.includes('activity'));
});

test('мобила: настоящий input, Enter ищет, клавиши не утекают в движок, после ответа фокус снят', () => {
  const { comp, doc, w, calls } = makeScreen({ keyboard: 'integrate', mobile: true, results: [MOVIE] });
  comp.create();
  const input = doc.querySelector('.d1v-search-field input');
  assert.ok(input);
  assert.strictEqual(input.getAttribute('placeholder'), 'Название фильма или сериала');
  let bubbled = 0;
  w.document.addEventListener('keydown', () => bubbled++);
  input.focus();
  input.value = 'матрица';
  input.dispatchEvent(new w.KeyboardEvent('keydown', { keyCode: 13, which: 13, bubbles: true }));
  assert.strictEqual(bubbled, 0);
  assert.strictEqual(calls.get.length, 1);
  assert.deepStrictEqual(titles(doc), ['Матрица']);
  assert.notStrictEqual(doc.activeElement, input, 'после ответа input без DOM-фокуса — иначе экран глух к стрелкам');
});

test('Apple TV: экранная клавиатура принудительно, даже при keyboard_type=integrate', () => {
  const { comp, doc } = makeScreen({ keyboard: 'integrate', platform: 'tvos' });
  comp.create();
  assert.ok(!doc.querySelector('.d1v-search-field input'));
  assert.ok(doc.querySelector('.d1v-search-text'));
});

test('«Недавнее» — история просмотров Lampa: постер из img, открытие через full с card (jut-запись уводит перехват)', () => {
  const history = [
    { id: 'jut:naruto', source: 'jutsu', title: 'Наруто', img: '/qdl/jut/poster?slug=naruto' },
    { id: 603, source: 'tmdb', title: 'Матрица', poster_path: '/m.jpg', release_date: '1999-03-31' },
  ];
  const ctx = makeScreen({ keyboard: 'lampa', history });
  ctx.comp.create();
  assert.strictEqual(ctx.calls.get.length, 0, 'без запроса в TMDB не ходим');
  assert.deepStrictEqual(titles(ctx.doc), ['Наруто', 'Матрица']);
  assert.ok(/Недавнее/.test(ctx.doc.querySelector('.d1v-search-title').textContent));

  const [jut, movie] = cards(ctx.doc);
  ctx.$(jut).trigger('visible');
  assert.strictEqual(jut.querySelector('.card__img').getAttribute('src'), '/qdl/jut/poster?slug=naruto');
  ctx.$(jut).trigger('hover:enter');
  const o = ctx.calls.pushed.pop();
  assert.strictEqual(o.component, 'full');
  assert.strictEqual(o.card.source, 'jutsu', 'перехват initHistoryRouting уводит такую карточку в jut_title');
  ctx.$(movie).trigger('hover:enter');
  assert.strictEqual(ctx.calls.pushed.pop().method, 'movie');
});

test('пустая история — заглушка во всю ширину', () => {
  const ctx = makeScreen({ keyboard: 'lampa', history: [] });
  ctx.comp.create();
  const stub = ctx.doc.querySelector('.category-full > div');
  assert.ok(/Пока пусто/.test(stub.textContent));
  assert.ok(/width:\s*100%/.test(stub.getAttribute('style') || ''));
});

test('чипы: последние запросы свежими первыми, «Штатный поиск» последним; remember пишет в конец без дублей', () => {
  const ctx = makeScreen({ keyboard: 'lampa', results: [MOVIE], searchHistory: ['дюна', 'матрица', 'игра'] });
  ctx.qdl.initSearchRoute();
  ctx.comp.create();
  assert.deepStrictEqual(chips(ctx.doc), ['игра', 'матрица', 'дюна', 'Штатный поиск']);

  search(ctx, 'матрица');
  // 🔴 порядок хранилища — как у штатного History.add: свежие в КОНЕЦ (показ — reverse)
  assert.strictEqual(ctx.w.Lampa.Storage.get('search_history').join(','), 'дюна,игра,матрица');
  assert.deepStrictEqual(chips(ctx.doc), ['матрица', 'игра', 'дюна', 'Штатный поиск']);

  const chipEls = [...ctx.doc.querySelectorAll('.d1v-search-chip')];
  ctx.$(chipEls[2]).trigger('hover:enter');   // «дюна»
  assert.strictEqual(ctx.calls.get[ctx.calls.get.length - 1].params.query, encodeURIComponent('дюна'));
  assert.strictEqual(ctx.doc.querySelector('.d1v-search-text').textContent, 'дюна');

  // после submit ряд чипов перестроен — берём свежий узел
  const alt = [...ctx.doc.querySelectorAll('.d1v-search-chip')].pop();
  assert.strictEqual(alt.textContent, 'Штатный поиск');
  ctx.$(alt).trigger('hover:enter');   // → штатный оверлей с текущим запросом
  assert.strictEqual(JSON.stringify(ctx.calls.orig), JSON.stringify([{ input: 'дюна' }]));
});

test('чипов не больше десяти, без истории — только «Штатный поиск»', () => {
  const many = [];
  for (let i = 0; i < 15; i++) many.push('q' + i);
  const ctx = makeScreen({ keyboard: 'lampa', searchHistory: many });
  ctx.comp.create();
  const c = chips(ctx.doc);
  assert.strictEqual(c.length, 11);
  assert.strictEqual(c[0], 'q14');
  assert.strictEqual(c[9], 'q5');

  const none = makeScreen({ keyboard: 'lampa' });
  none.comp.create();
  assert.deepStrictEqual(chips(none.doc), ['Штатный поиск']);
});

test('долгое нажатие на карточке — наше меню (идёт за /qdl/list); на реплике меню нет', () => {
  const home = makeScreen({ keyboard: 'lampa', results: [MOVIE, PERSON] });
  home.comp.create();
  search(home, 'матрица');
  const [movie, person] = cards(home.doc);
  home.$(movie).trigger('hover:long');
  assert.ok(home.calls.urls.some((u) => u.includes('/qdl/list')), 'меню карточки каталога (qdl 2.108) поднято');
  home.calls.urls.length = 0;
  home.$(person).trigger('hover:long');
  assert.ok(!home.calls.urls.some((u) => u.includes('/qdl/list')), 'у персоны меню нет (cardIsTitle)');

  const replica = makeScreen({ keyboard: 'lampa', results: [MOVIE], replica: true });
  replica.comp.create();
  search(replica, 'матрица');
  replica.$(cards(replica.doc)[0]).trigger('hover:long');
  assert.ok(!replica.calls.urls.some((u) => u.includes('/qdl/list')), 'на tv2 «Скачать» упёрлось бы в 403 — меню не вешаем');
});

test('autokb: клавиатура открывается один раз после первого toggle, флаг снят с объекта синхронно', async () => {
  const object = { autokb: true };
  const { comp, calls } = makeScreen({ keyboard: 'lampa' }, object);
  comp.create();
  assert.strictEqual(object.autokb, false, 'объект уезжает в Storage activity и адрес — флаг не должен пережить старт');
  assert.strictEqual(calls.editCb, null, 'до toggle клавиатуру не открываем');
  await new Promise((r) => setTimeout(r, 10));
  assert.ok(calls.editCb);
  assert.ok(calls.toggles.indexOf('activity') >= 0);
});

test('openInput: на ТВ — клавиатура, на телефоне — фокус в input (кнопка шапки на открытом экране)', () => {
  const tv = makeScreen({ keyboard: 'lampa' });
  tv.comp.create();
  tv.comp.openInput();
  assert.ok(tv.calls.editCb);

  const phone = makeScreen({ keyboard: 'integrate', mobile: true });
  phone.comp.create();
  phone.comp.openInput();
  assert.strictEqual(phone.doc.activeElement, phone.doc.querySelector('.d1v-search-field input'));
  assert.strictEqual(phone.calls.editCb, null);
});

test('kbLayout: копия штатной default без http://, слой sim есть, {MIC} только при голосовом мосте, объект свежий', () => {
  const ctx = boot({});
  const a = ctx.qdl.kbLayout();
  const rows = (l) => Object.keys(l).reduce((acc, k) => acc.concat(l[k]), []);
  assert.deepStrictEqual(Object.keys(a).sort(), ['default', 'en', 'he', 'sim', 'uk']);
  assert.ok(!rows(a).some((r) => /http:\/\//.test(r)), 'клавиша http:// поиску не нужна');
  assert.ok(!rows(a).some((r) => /\{MIC\}/.test(r)), 'без моста Keyboard звал бы Android.voiceStart() у оболочки без него');
  assert.ok(a['default'].some((r) => /\{ENTER\}/.test(r)) && a['default'].some((r) => /\{SHIFT\}/.test(r)), 'раскладка та же, что видит зритель в XSMART');
  assert.notStrictEqual(ctx.qdl.kbLayout(), a, 'Keyboard дописывает -shift-слои в объект — копия каждый раз');

  ctx.w.AndroidJS = { voiceStart() {} };
  const b = ctx.qdl.kbLayout();
  assert.ok(b['default'].some((r) => /\{MIC\}$/.test(r)) && b.en.some((r) => /\{MIC\}$/.test(r)), 'на Android TV микрофон на месте http://');
});
