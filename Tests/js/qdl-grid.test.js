'use strict';
// Компонентные тесты грида «Загрузки» (ComponentDownloads) на реальном DOM (jsdom + jQuery):
// рендер карточек и коллекций, под-грид коллекции, меню по long-press, самолечение постеров,
// авто-перерисовка устаревшего грида (colStamp). Регрессия: карточки не должны «пропадать»,
// а воспроизведение (enter → полная карточка, пункты «Смотреть») — ломаться.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HA = 'a'.repeat(40), HB = 'b'.repeat(40), HC = 'c'.repeat(40), HD = 'd'.repeat(40);
const COL_ID = 'c' + '1'.repeat(32);

const F1 = { hash: HA, name: 'Dune.mkv', progress: 1, has_poster: true, meta: { id: 1, media_type: 'movie', title: 'Дюна', year: 2021, poster_path: '/d1.jpg' } };
const F2 = { hash: HB, name: 'Dune2.mkv', progress: 1, has_poster: true, meta: { id: 2, media_type: 'movie', title: 'Дюна: Часть вторая', year: 2024, poster_path: '/d2.jpg' } };
const F3 = { hash: HC, name: 'Riddick.mkv', progress: 1, has_poster: true, meta: { id: 3, media_type: 'movie', title: 'Риддик', year: 2013, poster_path: '/r.jpg' } };
const F4 = { hash: HD, name: 'Broken.mkv', progress: 1, has_poster: false, meta: { id: 4, media_type: 'movie', title: 'Битый постер', year: 2000, poster_path: '/b.jpg' } };
const COL = { id: COL_ID, title: 'Дюна', cover: HA, hashes: [HA, HB] };

// Поднять компонент с мокнутыми Lampa/сетью. object — параметры активности (collection_id для под-грида).
function mount(object, data) {
  data = data || {};
  const calls = { selects: [], noty: [], pushes: [], replaces: 0, backwards: 0, reqs: [], fetches: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push: (a) => calls.pushes.push(a), replace: () => calls.replaces++, backward: () => calls.backwards++, active: () => ({}) },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        calls.reqs.push(String(url));
        if (String(url).indexOf('/qdl/collections') !== -1) return ok(data.collections || []);
        if (String(url).indexOf('/qdl/list') !== -1) return ok(data.list || []);
        ok([]);
      };
    },
  });
  const { w, $, qdl } = H.loadQdlDom({
    lampa,
    fetch: (url, init) => {
      calls.fetches.push({ url: String(url), body: (init && init.body) || '' });
      return Promise.resolve({ json: () => Promise.resolve({ success: true }) });
    },
  });
  // Template/Scroll должны отдавать НАСТОЯЩИЕ jQuery-элементы этого окна
  lampa.Template.get = function (name, d) {
    d = d || {};
    return $('<div class="card selector"><div class="card__view"><img class="card__img"></div><div class="card__title"></div></div>')
      .find('.card__title').text(d.title || '').end();
  };
  lampa.Scroll = function () {
    const el = $('<div class="scroll"><div class="scroll__body"></div></div>');
    this.render = () => el; this.body = () => el.find('.scroll__body');
    this.minus = () => {}; this.update = () => {}; this.destroy = () => {};
  };

  const comp = new qdl.ComponentDownloads(object || {});
  comp.activity = { loader() {}, toggle() {} };
  comp.create();   // мок-сеть отвечает синхронно → build уже выполнен
  return { comp, html: comp.render(), calls, qdl, $ };
}

// Array.from — массив из jsdom-реалма имеет чужой прототип, deepStrictEqual на нём падает
const cardTitles = (html) => Array.from(html.find('.card').map(function () { return this.querySelector('.card__title').textContent; }).get());

// ─────────────────────────── главный грид ───────────────────────────

test('грид: коллекция первой (стопка + бейдж), члены скрыты, одиночки на месте', () => {
  const { html } = mount({}, { list: [F1, F2, F3, F4], collections: [COL] });
  const cards = html.find('.card');

  assert.strictEqual(cards.length, 3, 'коллекция + 2 одиночки');
  assert.ok(cards.eq(0).hasClass('qdl-col-card'), 'коллекция первая');
  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Риддик', 'Битый постер']);
  assert.ok(cards.eq(0).text().indexOf('📁 2') !== -1, 'бейдж с количеством');
  assert.ok(cards.eq(0).find('.card__img').attr('src').indexOf('/qdl/poster?hash=' + HA) !== -1, 'обложка = постер первого фильма');
});

test('грид: единая сортировка по дате загрузки — свежая одиночка выше коллекции', () => {
  // Риддик скачан позже всех серий «Дюны» → он первый, коллекция второй
  const { html } = mount({}, {
    list: [{ ...F1, added: 100 }, { ...F2, added: 200 }, { ...F3, added: 300 }],
    collections: [COL],
  });
  assert.deepStrictEqual(cardTitles(html), ['Риддик', 'Дюна']);
  assert.ok(!html.find('.card').eq(0).hasClass('qdl-col-card'), 'одиночка первой');
});

test('грид: свежескачанная серия поднимает коллекцию наверх', () => {
  // дата коллекции = max(added) её элементов: серия F2 новее Риддика → коллекция первая
  const { html } = mount({}, {
    list: [{ ...F1, added: 100 }, { ...F2, added: 400 }, { ...F3, added: 300 }],
    collections: [COL],
  });
  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Риддик']);
  assert.ok(html.find('.card').eq(0).hasClass('qdl-col-card'), 'коллекция первая');
});

test('gridOrder: элементы без added уходят вниз, при всех-нулях прежний порядок (коллекции → одиночки)', () => {
  const { qdl } = mount({}, {});
  // без added — как раньше: коллекции первыми, одиночки следом
  const g0 = qdl.groupDownloads([F1, F2, F3, F4], [COL]);
  const o0 = qdl.gridOrder(g0);
  assert.ok(o0[0].col, 'коллекция первой при равных датах');
  assert.deepStrictEqual(Array.from(o0.slice(1), (e) => e.item.hash), [HC, HD]);
  // элемент без added (0) — ниже датированных
  const g1 = qdl.groupDownloads([{ ...F3, added: 50 }, F4], []);
  const o1 = qdl.gridOrder(g1);
  assert.strictEqual(o1[0].item.hash, HC);
  assert.strictEqual(o1[1].item.hash, HD, 'без added → вниз');
});

// ─────────────────────── актуальность (activity, qdl 2.28) ───────────────────────

test('грид: activity поднимает сериал с новой серией выше свежих одиночек', () => {
  // Дюна добавлена давно (added 100), но охота докачала серию (activity 500) → выше Риддика (added 300)
  const { html } = mount({}, {
    list: [{ ...F1, added: 100, activity: 500 }, { ...F3, added: 300 }],
    collections: [],
  });
  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Риддик']);
});

test('грид: коллекция всплывает по max activity её элементов', () => {
  // обе части «Дюны» старые по added, но у F2 свежая активность → коллекция первая
  const { html } = mount({}, {
    list: [{ ...F1, added: 100 }, { ...F2, added: 150, activity: 600 }, { ...F3, added: 300 }],
    collections: [COL],
  });
  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Риддик']);
  assert.ok(html.find('.card').eq(0).hasClass('qdl-col-card'), 'коллекция первая');
});

test('грид: без activity — фолбэк на added (старый сервер)', () => {
  const { html } = mount({}, {
    list: [{ ...F1, added: 100 }, { ...F3, added: 300 }],
    collections: [],
  });
  assert.deepStrictEqual(cardTitles(html), ['Риддик', 'Дюна']);
});

test('itemActivity: мусор и отрицательное → фолбэк added, NaN не протекает', () => {
  const { qdl } = mount({}, {});
  assert.strictEqual(qdl.itemActivity({ activity: 'garbage', added: 42 }), 42);
  assert.strictEqual(qdl.itemActivity({ activity: -5, added: 42 }), 42);
  assert.strictEqual(qdl.itemActivity({ activity: 100, added: 42 }), 100);
  assert.strictEqual(qdl.itemActivity({ added: 42 }), 42);
  assert.strictEqual(qdl.itemActivity({}), 0);
  assert.strictEqual(qdl.itemActivity(null), 0);
});

test('грид: activity == added (финализированный транскод) порядок не меняет', () => {
  // страховка клиентской стороны §AG: сервер отдаёт activity=added у маркеров без событий
  const { html } = mount({}, {
    list: [{ ...F1, added: 100, activity: 100 }, { ...F3, added: 300, activity: 300 }],
    collections: [],
  });
  assert.deepStrictEqual(cardTitles(html), ['Риддик', 'Дюна']);
});

test('грид: пусто → сообщение-подсказка, карточек нет', () => {
  const { html } = mount({}, { list: [], collections: [] });
  assert.strictEqual(html.find('.card').length, 0);
  assert.ok(html.text().indexOf('пока пусто') !== -1);
});

test('карточки не пропадают: битые/чужие данные коллекций не валят рендер фильмов', () => {
  // регрессия «не видно карточек»: мусор в collections не должен скрыть сами фильмы
  const { html } = mount({}, {
    list: [F1, F3],
    collections: [null, {}, { id: 'cX', title: 'Мёртвая', cover: 'x'.repeat(40), hashes: ['x'.repeat(40)] }],
  });
  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Риддик'], 'все фильмы на месте, мёртвая коллекция не рендерится');
});

test('enter по коллекции → под-грид; long-press → меню коллекции без «Удалить»', () => {
  const { html, calls } = mount({}, { list: [F1, F2, F3], collections: [COL] });
  const colCard = html.find('.qdl-col-card');

  colCard.trigger('hover:enter');
  assert.strictEqual(calls.pushes.length, 1);
  assert.strictEqual(calls.pushes[0].component, 'qdl_downloads');
  assert.strictEqual(calls.pushes[0].collection_id, COL_ID);
  assert.strictEqual(calls.pushes[0].title, 'Дюна');

  colCard.trigger('hover:long');
  const menu = calls.selects[calls.selects.length - 1];
  const titles = menu.items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Переименовать') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Сменить обложку') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Расформировать') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Удалить') !== -1), 'файлы из меню коллекции не удалить');
});

test('воспроизведение из грида: enter по фильму открывает полную карточку с qdl_hash', () => {
  const { html, calls } = mount({}, { list: [F3], collections: [] });
  html.find('.card').eq(0).trigger('hover:enter');
  assert.strictEqual(calls.pushes.length, 1);
  assert.strictEqual(calls.pushes[0].qdl_hash, HC);
});

test('long-press по фильму: «Добавить в коллекцию» + все пункты воспроизведения на месте', () => {
  const { html, calls } = mount({}, { list: [F3], collections: [] });
  html.find('.card').eq(0).trigger('hover:long');
  const titles = calls.selects[0].items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Добавить в коллекцию') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Смотреть (оффлайн)') !== -1), 'воспроизведение не потеряно');
  assert.ok(titles.some((t) => t.indexOf('Открыть карточку') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Озвучка') !== -1));
});

// ─────────────────────────── под-грид коллекции ───────────────────────────

test('под-грид: только фильмы коллекции в порядке добавления; «Убрать» вместо «Добавить», плейбек цел', () => {
  const { html, calls } = mount({ collection_id: COL_ID }, { list: [F1, F2, F3], collections: [COL] });

  assert.deepStrictEqual(cardTitles(html), ['Дюна', 'Дюна: Часть вторая'], 'Риддик не в коллекции — его тут нет');
  assert.strictEqual(html.find('.qdl-col-card').length, 0, 'внутри коллекции карточек-папок нет');

  html.find('.card').eq(0).trigger('hover:long');
  const titles = calls.selects[0].items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Убрать из коллекции') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Добавить в коллекцию') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Смотреть (оффлайн)') !== -1), 'воспроизведение внутри коллекции не потеряно');
});

test('под-грид исчезнувшей коллекции не падает — сообщение вместо карточек', () => {
  const { html } = mount({ collection_id: 'c' + '9'.repeat(32) }, { list: [F1], collections: [] });
  assert.strictEqual(html.find('.card').length, 0);
  assert.ok(html.text().indexOf('Коллекции больше нет') !== -1);
});

// ─────────────────────────── самолечение постеров ───────────────────────────

test('healPoster в гриде: ретрай только для карточек с метой без постера', () => {
  const { calls } = mount({}, { list: [F3, F4], collections: [] });
  const saves = calls.fetches.filter((f) => f.url.indexOf('/qdl/save') !== -1);
  assert.strictEqual(saves.length, 1, 'ровно один ретрай — для битой карточки');
  assert.ok(saves[0].body.indexOf('hash=' + HD) !== -1);
  assert.ok(saves[0].body.indexOf('card=') === -1, 'мета не перезаписывается');
});

// ─────────────────────────── свежесть грида (colStamp) ───────────────────────────

test('устаревший грид: после мутации коллекций start() перерисовывает активность', () => {
  const { comp, calls, qdl } = mount({}, { list: [F1, F2], collections: [COL] });

  comp.start();
  assert.strictEqual(calls.replaces, 0, 'без мутаций перерисовки нет');

  qdl.touchCollections();   // любая мутация коллекций (add/remove/rename/…)
  comp.start();
  assert.strictEqual(calls.replaces, 1, 'стамп разошёлся → Activity.replace');
});

// ─────────────────────────── живые проценты (qdl 2.93) ───────────────────────────
// Владелец: «проценты загрузки на клиенте не обновляются». Раньше бейдж рисовался один раз
// в append() и замерзал на всё время жизни активности в стеке Lampa.

const HALF = { hash: HA, name: 'Half.mkv', progress: 0.4, has_poster: true, meta: { id: 9, media_type: 'movie', title: 'Половинка', year: 2020, poster_path: '/h.jpg' } };

test('бейдж прогресса обновляется по тику поллера, карточка остаётся ТЕМ ЖЕ узлом', () => {
  const { comp, html, qdl } = mount({}, { list: [HALF], collections: [] });
  qdl.pgReset();

  const badge = () => html.find('.qdl-dl-badge');
  assert.strictEqual(badge().text(), '40%');
  const node = html.find('.card')[0];

  qdl.pgApply({ ok: true, stamp: '1', active: 1, pending: 0, items: [{ h: HA, p: 0.77, s: 'downloading' }] });
  comp.refreshBadges();
  assert.strictEqual(badge().text(), '77%', 'процент поехал');
  assert.strictEqual(html.find('.card')[0], node, 'DOM не пересобран — фокус пульта жив');

  qdl.pgApply({ ok: true, stamp: '2', active: 0, pending: 0, items: [] });   // докачалось
  comp.refreshBadges();
  assert.strictEqual(badge().text(), '✓');
  qdl.pgReset();
});

test('грид подписывается в start и отписывается в pause (мина «нет destroy вперёд»)', () => {
  const { comp, qdl } = mount({}, { list: [HALF], collections: [] });
  qdl.pgReset();

  comp.start();
  assert.strictEqual(Object.keys(qdl.pgState().subs).length, 1);
  comp.start();
  assert.strictEqual(Object.keys(qdl.pgState().subs).length, 1, 'повторный start не удваивает');

  comp.pause();
  assert.strictEqual(Object.keys(qdl.pgState().subs).length, 0);
  comp.destroy();
  assert.strictEqual(Object.keys(qdl.pgState().subs).length, 0, 'destroy идемпотентен');
  qdl.pgReset();
});

// 🔴 Взвешенный по РАЗМЕРУ, как MergeSeriesGroup на сервере. Наивное среднее здесь уже стреляло:
// докачанный 20-гигабайтный сезон рядом с пустым одногигабайтным давал «50%» на готовом сериале.
test('livePct склеенной карточки взвешен по размеру и совпадает с сервером', () => {
  const { qdl } = mount({}, { list: [], collections: [] });
  qdl.pgReset();

  const merged = {
    hash: HA, progress: 0,
    parts: [
      { hash: HA, size: 20 * 1024 * 1024 * 1024, progress: 1 },
      { hash: HB, size: 1 * 1024 * 1024 * 1024, progress: 0 },
    ],
  };
  // то же соотношение 20:1, что у серверного SeriesMergeTests.Прогресс_склеенной_карточки_взвешен_по_размеру
  // (20000/21000): клиент и сервер обязаны давать одно число, иначе бейдж и гейт разойдутся
  assert.ok(Math.abs(qdl.livePct(merged) - 20 / 21) < 1e-9, 'вес по размеру, а не среднее');

  // живой прогресс частей побеждает снимок
  qdl.pgApply({ ok: true, stamp: '1', active: 1, pending: 0, items: [{ h: HB, p: 0.5, s: 'downloading' }] });
  assert.ok(Math.abs(qdl.livePct(merged) - (20 + 0.5) / 21) < 1e-9);
  qdl.pgReset();
});
