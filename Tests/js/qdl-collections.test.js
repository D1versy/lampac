'use strict';
// Тесты коллекций в «Загрузках»: группировка грида (groupDownloads), автоимя (commonPrefixTitle),
// пикер «Добавить в коллекцию» (buildCollectionPicker), пункты quickMenu с/без ctx.collection,
// POST-мутации (remove/create/update) и меню коллекции.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Песочница с перехватами Select/Noty/Reguest/Activity + fetch (все POST-мутации идут через fetch)
function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], pushes: [], replaces: 0, backwards: 0, toggles: [], fetches: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push: (a) => calls.pushes.push(a), replace: () => calls.replaces++, backward: () => calls.backwards++, active: () => ({}) },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
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
  const { qdl, sandbox } = H.loadQdl({
    lampa,
    fetch: (url, init) => {
      calls.fetches.push({ url: String(url), body: (init && init.body) || '' });
      const j = (opts.fetchRespond || (() => ({ success: true })))(String(url));
      return Promise.resolve({ json: () => Promise.resolve(j) });
    },
  });
  return { qdl, lampa, calls, sandbox };
}

const tick = () => new Promise((r) => setImmediate(r));
const last = (a) => a[a.length - 1];

const DUNE1 = { hash: 'a'.repeat(40), name: 'Dune.2021.mkv', meta: { title: 'Дюна', year: 2021 } };
const DUNE2 = { hash: 'b'.repeat(40), name: 'Dune.Part.Two.mkv', meta: { title: 'Дюна: Часть вторая', year: 2024 } };
const RIDDICK = { hash: 'c'.repeat(40), name: 'Riddick.mkv', meta: { title: 'Риддик', year: 2013 } };
const col = (id, title, cover, hashes) => ({ id: 'c' + String(id).repeat(32), title, cover, hashes });

// ─────────────────────────────── groupDownloads ───────────────────────────────

test('groupDownloads: члены коллекции уходят из singles, коллекции в порядке файла', () => {
  const { qdl } = rig();
  const g = qdl.groupDownloads([DUNE1, RIDDICK, DUNE2], [col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash])]);
  assert.strictEqual(g.cols.length, 1);
  assert.deepStrictEqual(g.cols[0].items.map((t) => t.hash), [DUNE1.hash, DUNE2.hash]);
  assert.strictEqual(g.cols[0].cover.hash, DUNE1.hash);
  assert.deepStrictEqual(g.singles.map((t) => t.hash), [RIDDICK.hash]);
});

test('groupDownloads: мёртвые хэши отбрасываются, пустая коллекция не рендерится', () => {
  const { qdl } = rig();
  const g = qdl.groupDownloads([RIDDICK], [
    col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash]),        // все мертвы → не рендерится
    col('2', 'Риддик', 'x'.repeat(40), [RIDDICK.hash, 'x'.repeat(40)]),  // cover мёртв → фолбек
  ]);
  assert.strictEqual(g.cols.length, 1);
  assert.strictEqual(g.cols[0].col.title, 'Риддик');
  assert.strictEqual(g.cols[0].cover.hash, RIDDICK.hash, 'cover-фолбек на первый живой');
  assert.deepStrictEqual(g.singles, []);
});

test('groupDownloads: пустые входы не падают', () => {
  const { qdl } = rig();
  const g = qdl.groupDownloads(null, null);   // объекты из vm-реалма — сравниваем структурно
  assert.strictEqual(g.cols.length, 0);
  assert.strictEqual(g.singles.length, 0);
  assert.strictEqual(qdl.groupDownloads([DUNE1], null).singles.length, 1);
});

// ─────────────────────────────── commonPrefixTitle ───────────────────────────────

test('commonPrefixTitle: общий префикс, пунктуация и регистр', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.commonPrefixTitle('Дюна', 'Дюна: Часть вторая'), 'Дюна');
  assert.strictEqual(qdl.commonPrefixTitle('Дюна: Часть вторая', 'Дюна: Часть первая'), 'Дюна: Часть');
  assert.strictEqual(qdl.commonPrefixTitle('РИДДИК', 'риддик 2'), 'РИДДИК');
  assert.strictEqual(qdl.commonPrefixTitle('Елки', 'Ёлки 2'), 'Елки', 'ё → е');
});

test('commonPrefixTitle: нет общих слов → название первого; пустые входы не падают', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.commonPrefixTitle('Дюна', 'Риддик'), 'Дюна');
  assert.strictEqual(qdl.commonPrefixTitle('', 'Риддик'), 'Риддик');
  assert.strictEqual(qdl.commonPrefixTitle('', ''), 'Коллекция');
});

// ─────────────────────────────── buildCollectionPicker ───────────────────────────────

test('buildCollectionPicker: коллекции сверху со счётчиком, ниже одиночки без текущего', () => {
  const { qdl } = rig();
  const c1 = col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash]);
  const items = qdl.buildCollectionPicker(RIDDICK, [c1], [DUNE1, DUNE2, RIDDICK, { hash: 'd'.repeat(40), name: 'Alien.mkv' }]);

  assert.strictEqual(items.length, 2);
  assert.ok(items[0].title.indexOf('📁 Дюна') === 0, 'коллекция первая');
  assert.ok(items[0].subtitle.indexOf('2') !== -1, 'счётчик фильмов');
  assert.strictEqual(items[0].col, c1);
  assert.ok(items[1].title.indexOf('Alien.mkv') !== -1, 'одиночка без меты — по имени файла');
  assert.ok(!items.some((i) => i.item && i.item.hash === RIDDICK.hash), 'сам фильм исключён');
  assert.ok(!items.some((i) => i.item && (i.item.hash === DUNE1.hash || i.item.hash === DUNE2.hash)), 'члены коллекций исключены');
});

test('buildCollectionPicker: пусто, когда нет ни коллекций, ни других фильмов', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.buildCollectionPicker(RIDDICK, [], [RIDDICK]).length, 0);
});

// ─────────────────────────────── quickMenu: пункты ───────────────────────────────

test('quickMenu без ctx → «Добавить в коллекцию», с ctx.collection → «Убрать из коллекции»', () => {
  const { qdl, calls } = rig();
  qdl.quickMenu({ hash: 'a1', name: 'X', progress: 1 });
  let titles = last(calls.selects).items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Добавить в коллекцию') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Убрать из коллекции') !== -1));

  qdl.quickMenu({ hash: 'a1', name: 'X', progress: 1 }, { collection: col('1', 'Дюна', 'a1', ['a1']) });
  titles = last(calls.selects).items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Убрать из коллекции') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Добавить в коллекцию') !== -1));
});

test('quickMenu «Убрать»: POST remove; deleted → Noty + backward, иначе → replace', async () => {
  let deleted = false;
  const { qdl, calls } = rig({ fetchRespond: () => ({ success: true, deleted }) });
  const ctx = { collection: col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash]) };

  qdl.quickMenu(DUNE1, ctx);
  let menu = last(calls.selects);
  menu.onSelect(menu.items.filter((i) => i.title.indexOf('Убрать') !== -1)[0]);
  await tick(); await tick();
  assert.strictEqual(calls.fetches.length, 1);
  assert.ok(calls.fetches[0].url.indexOf('/qdl/collections/remove') !== -1);
  assert.ok(calls.fetches[0].body.indexOf('hash=' + DUNE1.hash) !== -1);
  assert.strictEqual(calls.replaces, 1, 'не последний → replace');
  assert.strictEqual(calls.backwards, 0);

  deleted = true;
  qdl.quickMenu(DUNE1, ctx);
  menu = last(calls.selects);
  menu.onSelect(menu.items.filter((i) => i.title.indexOf('Убрать') !== -1)[0]);
  await tick(); await tick();
  assert.strictEqual(calls.backwards, 1, 'последний → выходим из под-грида');
  assert.ok(calls.noty.some((m) => m.indexOf('удалена') !== -1));
});

// ─────────────────────────────── addToCollection ───────────────────────────────

test('addToCollection: выбор коллекции → POST add; выбор фильма → POST create с автоименем', async () => {
  const c1 = col('1', 'Дюна', DUNE1.hash, [DUNE1.hash]);
  const { qdl, calls } = rig({
    respond: (url) => {
      if (url.indexOf('/qdl/collections') !== -1) return [c1];
      if (url.indexOf('/qdl/list') !== -1) return [DUNE1, DUNE2, RIDDICK];
    },
    fetchRespond: () => ({ success: true, collection: { title: 'Дюна' } }),
  });

  qdl.addToCollection(DUNE2);
  let picker = last(calls.selects);
  assert.ok(picker.title.indexOf('Дюна: Часть вторая') !== -1);
  picker.onSelect(picker.items.filter((i) => i.col)[0]);          // в существующую «Дюна»
  await tick(); await tick();
  assert.ok(last(calls.fetches).url.indexOf('/qdl/collections/add') !== -1);
  assert.ok(last(calls.fetches).body.indexOf('id=' + encodeURIComponent(c1.id)) !== -1);
  assert.strictEqual(calls.replaces, 1);

  qdl.addToCollection(DUNE2);
  picker = last(calls.selects);
  picker.onSelect(picker.items.filter((i) => i.item && i.item.hash === RIDDICK.hash)[0]);   // новая из двух
  await tick(); await tick();
  const create = last(calls.fetches);
  assert.ok(create.url.indexOf('/qdl/collections/create') !== -1);
  assert.ok(create.body.indexOf('hashes=' + encodeURIComponent(DUNE2.hash + ',' + RIDDICK.hash)) !== -1);
  assert.ok(decodeURIComponent(create.body).indexOf('title=' + qdl.commonPrefixTitle('Дюна: Часть вторая', 'Риддик')) !== -1);
});

test('addToCollection: нечего предложить → Noty, Select не открывается', () => {
  const { qdl, calls } = rig({
    respond: (url) => {
      if (url.indexOf('/qdl/collections') !== -1) return [];
      if (url.indexOf('/qdl/list') !== -1) return [DUNE2];
    },
  });
  qdl.addToCollection(DUNE2);
  assert.strictEqual(calls.selects.length, 0);
  assert.strictEqual(calls.noty.length, 1);
});

// ─────────────────────────────── меню коллекции ───────────────────────────────

test('collectionMenu: состав пунктов; расформирование — только через confirm', async () => {
  const { qdl, calls } = rig({ fetchRespond: () => ({ success: true }) });
  const c1 = col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash]);

  qdl.collectionMenu(c1, [DUNE1, DUNE2]);
  const menu = last(calls.selects);
  const titles = menu.items.map((i) => i.title);
  assert.ok(titles.some((t) => t.indexOf('Переименовать') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Сменить обложку') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Расформировать') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Удалить') !== -1), 'удаления файлов в меню коллекции нет');

  menu.onSelect(menu.items.filter((i) => i.title.indexOf('Расформировать') !== -1)[0]);
  assert.strictEqual(calls.fetches.length, 0, 'без confirm мутации нет');
  const confirm = last(calls.selects);
  confirm.onSelect(confirm.items[0]);                              // «Расформировать»
  await tick(); await tick();
  assert.ok(last(calls.fetches).url.indexOf('/qdl/collections/dissolve') !== -1);
  assert.strictEqual(calls.replaces, 1);
});

test('chooseCover: ✓ у текущей обложки, выбор → POST update с cover', async () => {
  const { qdl, calls } = rig({ fetchRespond: () => ({ success: true }) });
  const c1 = col('1', 'Дюна', DUNE1.hash, [DUNE1.hash, DUNE2.hash]);

  qdl.chooseCover(c1, [DUNE1, DUNE2]);
  const sel = last(calls.selects);
  assert.ok(sel.items[0].title.indexOf('✓') === 0, 'текущая обложка помечена');
  assert.ok(sel.items[1].title.indexOf('✓') === -1);
  sel.onSelect(sel.items[1]);
  await tick(); await tick();
  assert.ok(last(calls.fetches).url.indexOf('/qdl/collections/update') !== -1);
  assert.ok(last(calls.fetches).body.indexOf('cover=' + DUNE2.hash) !== -1);
  assert.strictEqual(c1.cover, DUNE2.hash, 'локальный объект обновлён');
});

test('renameCollection без Lampa.Input → Select-фолбек: общий префикс + названия фильмов', async () => {
  const { qdl, calls } = rig({ fetchRespond: () => ({ success: true }) });
  const c1 = col('1', 'X', DUNE1.hash, [DUNE1.hash, DUNE2.hash]);

  qdl.renameCollection(c1, [DUNE1, DUNE2]);
  const sel = last(calls.selects);
  const titles = sel.items.map((i) => i.title);
  assert.strictEqual(titles[0], 'Дюна', 'первым — общий префикс');
  assert.ok(titles.indexOf('Дюна: Часть вторая') !== -1);
  sel.onSelect(sel.items[0]);
  await tick(); await tick();
  assert.ok(last(calls.fetches).body.indexOf('title=' + encodeURIComponent('Дюна')) !== -1);
  assert.strictEqual(c1.title, 'Дюна');
});
