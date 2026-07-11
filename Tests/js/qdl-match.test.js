'use strict';
// Тесты строгого матчинга «карточка ↔ загрузка» (findDownload + normTitle/isSerialName/isSeasonTail).
// Регрессия бага «Дюна ↔ Дюна: Пророчество»: карточка ФИЛЬМА показывала зелёную «Смотреть (загружено)»
// и играла СЕРИАЛ — старый матчер падал в двунаправленный префиксный name-fallback без проверки
// media_type и цеплял раздачи, чья мета уже указывает на другую карточку (см. claude/06 §X).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl();

// Фикстура — реальные имена/меты из живого /qdl/list на момент бага.
function liveList() {
  return [
    { hash: 'a1', name: 'Dune.Prophecy.S01.1080p.AMZN.WEB-DL.H.264-EniaHD', meta: { id: 90228, media_type: 'tv', title: 'Дюна: Пророчество', original_title: 'Dune: Prophecy' } },
    { hash: 'b2', name: 'Dune. Part Two (2024) WEB-DLRip-AVC.mkv', meta: { id: 693134, media_type: 'movie', title: 'Дюна: Часть вторая', original_title: 'Dune: Part Two' } },
    { hash: 'c3', name: 'След Чикатило', meta: { id: 316567, media_type: 'tv', title: 'След Чикатило' } },
    { hash: 'd4', name: 'rutor.info', meta: { id: 111111, media_type: 'movie', title: 'Гренландия 2: Миграция' } },
  ];
}

const DUNE_MOVIE = { id: 438631, media_type: 'movie', title: 'Дюна', original_title: 'Dune' };
const DUNE_TV = { id: 90228, media_type: 'tv', title: 'Дюна: Пророчество', original_title: 'Dune: Prophecy' };

// ─────────────────────────────── normTitle ───────────────────────────────

test('normTitle: пунктуация → пробел, lowercase', () => {
  assert.strictEqual(qdl.normTitle('Dune: Part Two'), 'dune part two');
});

test('normTitle: ё-фолдинг с обеих сторон сравнения', () => {
  assert.strictEqual(qdl.normTitle('Ёлки 2'), 'елки 2');
  assert.strictEqual(qdl.normTitle('ЕЛКИ 2'), 'елки 2');
});

test('normTitle: схлопывание разделителей и trim', () => {
  assert.strictEqual(qdl.normTitle('  The.Movie--Name_ '), 'the movie name');
});

test('normTitle: null/undefined/пустая строка → пустая строка', () => {
  assert.strictEqual(qdl.normTitle(null), '');
  assert.strictEqual(qdl.normTitle(undefined), '');
  assert.strictEqual(qdl.normTitle(''), '');
});

test('normTitle: диакритика выпадает (осознанный false negative прохода B)', () => {
  // BUG?: без String.normalize (ES6, старые TV-webview) 'é' не фолдится в 'e' — матч по имени
  // для таких названий не сработает; проход A по id это покрывает.
  assert.strictEqual(qdl.normTitle('Léon'), 'l on');
});

// ─────────────────────────────── isSerialName ───────────────────────────────

test('isSerialName: S01 в scene-имени', () => {
  assert.strictEqual(qdl.isSerialName('Dune.Prophecy.S01.1080p.AMZN.WEB-DL.H.264-EniaHD'), true);
});

test('isSerialName: Season внутри скобок (до cleanName — по сырому имени)', () => {
  assert.strictEqual(qdl.isSerialName('House of the Dragon (Season 3) WEB-DL 1080p'), true);
});

test('isSerialName: кириллический «сезон» (⚠️ \\b с кириллицей не работает — токены)', () => {
  assert.strictEqual(qdl.isSerialName('Название 3 сезон'), true);
  assert.strictEqual(qdl.isSerialName('Сезон 1'), true);
  assert.strictEqual(qdl.isSerialName('Сериал.Сезоны.1-3'), true);
});

test('isSerialName: слитный токен S01E05', () => {
  assert.strictEqual(qdl.isSerialName('S01E05.mkv'), true);
});

test('isSerialName: фильмы — false (включая Se7en)', () => {
  assert.strictEqual(qdl.isSerialName('Dune. Part Two (2024) WEB-DLRip-AVC.mkv'), false);
  assert.strictEqual(qdl.isSerialName('Scary MoVie.mkv'), false);
  assert.strictEqual(qdl.isSerialName('Se7en.1995'), false);
  assert.strictEqual(qdl.isSerialName('Дюна 2021'), false);
});

// ─────────────────────────────── isSeasonTail ───────────────────────────────

test('isSeasonTail: s01 / season 3 / 3 сезон / сезон 1 — true', () => {
  assert.strictEqual(qdl.isSeasonTail('s01'), true);
  assert.strictEqual(qdl.isSeasonTail('s01e05'), true);
  assert.strictEqual(qdl.isSeasonTail('season 3'), true);
  assert.strictEqual(qdl.isSeasonTail('3 сезон'), true);
  assert.strictEqual(qdl.isSeasonTail('сезон 1'), true);
});

test('isSeasonTail: произвольный хвост — false', () => {
  assert.strictEqual(qdl.isSeasonTail('2'), false);          // «Дюна 2» — не сезонный хвост
  assert.strictEqual(qdl.isSeasonTail('пророчество'), false);
  assert.strictEqual(qdl.isSeasonTail(''), false);
});

// ─────────────────────────────── findDownload, проход A (id + media_type) ───────────────────────────────

test('РЕГРЕССИЯ БАГА: карточка movie «Дюна» НЕ матчит скачанный сериал «Дюна: Пророчество»', () => {
  assert.strictEqual(qdl.findDownload(liveList(), DUNE_MOVIE), null);
});

test('tv-карточка «Дюна: Пророчество» матчит свою раздачу по id+типу', () => {
  const list = liveList();
  assert.strictEqual(qdl.findDownload(list, DUNE_TV), list[0]);
});

test('movie-карточка «Дюна: Часть вторая» матчит свою раздачу по id+типу', () => {
  const list = liveList();
  assert.strictEqual(qdl.findDownload(list, { id: 693134, media_type: 'movie', title: 'Дюна: Часть вторая' }), list[1]);
});

test('кросс-тип: movie-карточка с id сериала (90228) → null (id-пространства movie/tv раздельные, §R)', () => {
  assert.strictEqual(qdl.findDownload(liveList(), { id: 90228, media_type: 'movie', title: 'Другой фильм' }), null);
});

test('легаси-мета без media_type матчится по одному id (толерантность)', () => {
  const list = [{ hash: 'x', name: 'whatever', meta: { id: 5 } }];
  assert.strictEqual(qdl.findDownload(list, { id: 5, media_type: 'movie', title: 'X' }), list[0]);
  assert.strictEqual(qdl.findDownload(list, { id: 5, media_type: 'tv', name: 'X' }), list[0]);
});

test('id сравнивается с коэрцией типов (number ↔ string)', () => {
  const list = [{ hash: 'x', name: 'n', meta: { id: 42, media_type: 'movie' } }];
  assert.strictEqual(qdl.findDownload(list, { id: '42', media_type: 'movie' }), list[0]);
});

test('пустые id не дают ложного матча undefined === undefined', () => {
  const list = [{ hash: 'x', name: 'Что-то', meta: {} }];
  assert.strictEqual(qdl.findDownload(list, { title: 'Другое' }), null);
});

test('мусорное имя раздачи не мешает матчу по id (rutor.info)', () => {
  const list = liveList();
  assert.strictEqual(qdl.findDownload(list, { id: 111111, media_type: 'movie', title: 'Гренландия 2: Миграция' }), list[3]);
});

// ─────────────────────────────── findDownload, проход B (back-link по точному имени) ───────────────────────────────

test('back-link §N сохранён: безметочная раздача с точным названием матчится к movie-карточке', () => {
  const list = [{ hash: 'x9', name: 'Дюна 2021 WEB-DL' }];
  assert.strictEqual(qdl.findDownload(list, DUNE_MOVIE), list[0]);
});

test('сериал-гард: безметочная сериальная раздача НЕ цепляется к movie-карточке', () => {
  const list = [{ hash: 'x9', name: 'Dune.Prophecy.S01.1080p.AMZN.WEB-DL.H.264-EniaHD' }];
  assert.strictEqual(qdl.findDownload(list, DUNE_MOVIE), null);
});

test('tv-карточка + безметочная «Название.S01…» → матч (сезонный хвост)', () => {
  const list = [{ hash: 'x9', name: 'Название.S01.1080p.WEB-DL' }];
  assert.strictEqual(qdl.findDownload(list, { id: 7, media_type: 'tv', title: 'Название' }), list[0]);
});

test('tv-карточка + безметочная «Название 3 сезон» → матч (кириллический маркер)', () => {
  const list = [{ hash: 'x9', name: 'Название 3 сезон' }];
  assert.strictEqual(qdl.findDownload(list, { id: 7, media_type: 'tv', title: 'Название' }), list[0]);
});

test('раздача с метой ДРУГОЙ карточки никогда не матчится по имени', () => {
  // изолированный кейс дефекта: раньше «Дюна» цепляла «Dune Prophecy S01» префиксом, игнорируя мету
  const list = [liveList()[0]];
  assert.strictEqual(qdl.findDownload(list, DUNE_MOVIE), null);
});

test('префиксный матч мёртв: «Дюна» ↔ «Дюна 2» не отождествляются в обе стороны', () => {
  assert.strictEqual(qdl.findDownload([{ hash: 'x', name: 'Дюна 2' }], DUNE_MOVIE), null);
  assert.strictEqual(qdl.findDownload([{ hash: 'x', name: 'Дюна' }], { id: 1, media_type: 'movie', title: 'Дюна 2' }), null);
});

test('видеорасширение срезается до сравнения (однофайловые раздачи)', () => {
  const list = [{ hash: 'x9', name: 'Scary MoVie.mkv' }];
  assert.strictEqual(qdl.findDownload(list, { id: 8, media_type: 'movie', title: 'Очень страшное кино', original_title: 'Scary Movie' }), list[0]);
});

test('пунктуация нормализуется: «Дюна.Часть.вторая.2024» ↔ «Дюна: Часть вторая» (старый код ложно-отрицал)', () => {
  const list = [{ hash: 'x9', name: 'Дюна.Часть.вторая.2024.WEB-DL' }];
  assert.strictEqual(qdl.findDownload(list, { id: 693134, media_type: 'movie', title: 'Дюна: Часть вторая', original_title: 'Dune: Part Two' }), list[0]);
});

// ─────────────────────────────── findDownload, границы ───────────────────────────────

test('границы: null/пустой список, карточка без данных, пустые имена — null без исключений', () => {
  assert.strictEqual(qdl.findDownload(null, DUNE_MOVIE), null);
  assert.strictEqual(qdl.findDownload([], DUNE_MOVIE), null);
  assert.strictEqual(qdl.findDownload(liveList(), {}), null);
  assert.strictEqual(qdl.findDownload(liveList(), null), null);
  assert.strictEqual(qdl.findDownload([{ hash: 'x', name: '' }, { hash: 'y', name: null }, null], DUNE_MOVIE), null);
});

test('безметочный «rutor.info» не матчится к «Гренландия 2» по имени (имена не совпадают)', () => {
  assert.strictEqual(qdl.findDownload([{ hash: 'x', name: 'rutor.info' }], { id: 1, media_type: 'movie', title: 'Гренландия 2: Миграция' }), null);
});

test('из двух одинаковых безметочных кандидатов возвращается первый по порядку списка', () => {
  const list = [{ hash: 'first', name: 'Дюна 2021' }, { hash: 'second', name: 'Дюна 2021' }];
  assert.strictEqual(qdl.findDownload(list, DUNE_MOVIE), list[0]);
});

// ─────────────────────────────── интеграция addButton (jsdom + реальный jQuery) ───────────────────────────────

// Мок Lampa: Activity.active() отдаёт method/source открытой карточки (как в проде, §R),
// Reguest.silent отвечает фикстурой ТОЛЬКО на /qdl/list (прочие запросы молчат).
function lampaFor(method, fixture) {
  return H.makeLampa({
    Activity: { active: () => ({ method, source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => { if (String(url).indexOf('/qdl/list') !== -1) ok(fixture); };
    },
  });
}

function fireAddButton(w, movie) {
  const fetches = [];
  w.fetch = (url, opts) => { fetches.push({ url: String(url), opts }); return Promise.resolve({ json: () => Promise.resolve({}) }); };
  w.__qdl.addButton({
    type: 'complite',
    object: { activity: { render: () => w.$('body') } },
    data: { movie },
  });
  return fetches;
}

const PAGE = '<div class="full-start__buttons"></div>';

test('UI-регрессия: movie «Дюна» — зелёной кнопки НЕТ, «Скачать» есть, чужая мета не отравлена', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', liveList()) });
  // media_type намеренно не задан — addButton обязан дозаполнить его из Activity.active().method (§R)
  const fetches = fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-btn').length, 0, 'зелёная кнопка не должна появиться');
  assert.strictEqual(doc.querySelectorAll('.qdl-download').length, 1, 'обычная «Скачать» должна быть');
  assert.strictEqual(fetches.filter((f) => f.url.indexOf('/qdl/save') !== -1).length, 0, '/qdl/save не должен дёргаться');
});

test('UI: tv «Дюна: Пророчество» — ровно одна зелёная кнопка, мета не пересохраняется', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('tv', liveList()) });
  const fetches = fireAddButton(w, { id: 90228, title: 'Дюна: Пророчество', original_title: 'Dune: Prophecy' });

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-btn').length, 1);
  assert.strictEqual(fetches.filter((f) => f.url.indexOf('/qdl/save') !== -1).length, 0, 'у раздачи уже есть мета');
});

test('UI: back-link — безметочная точная раздача получает мету карточки через /qdl/save', () => {
  const fixture = [{ hash: 'x9', name: 'Дюна 2021 WEB-DL' }];
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', fixture) });
  const fetches = fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  assert.strictEqual(doc.querySelectorAll('.qdl-watch-btn').length, 1);
  const saves = fetches.filter((f) => f.url.indexOf('/qdl/save') !== -1);
  assert.strictEqual(saves.length, 1, 'back-link должен сохранить мету');
  assert.ok(/(^|&)hash=x9(&|$)/.test(saves[0].opts.body), 'мета сохраняется для правильного hash');
});
