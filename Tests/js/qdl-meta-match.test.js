'use strict';
// Строгая сверка карточки TMDB с именем раздачи (qdl 2.43).
//
// Было: tmdbSearch брал ПЕРВЫЙ результат search/multi с постером. У безымянной загрузки
// «Holod.S01.2026.WEB-DL.1080p.ExKinoRay» это давало «Голод-33» (1991) — чужая карточка прилипала
// к загрузке навсегда (разбор 14.08.2026). Точную привязку btih → tmdb делает сервер (MetaHeal.cs),
// а здесь остаются только очевидные совпадения: лучше без карточки, чем с чужой.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl({ lampa: H.makeLampa() });

// кандидаты из живой выдачи TMDB
const GOLOD33 = { id: 204022, media_type: 'movie', title: 'Голод-33', original_title: 'Голод-33', release_date: '1991-01-01', poster_path: '/a.jpg' };
const HOLOD = { id: 318354, media_type: 'tv', name: 'Холод', original_name: 'Холод', first_air_date: '2026-07-16', poster_path: '/b.jpg' };
const DJANGO = { id: 68718, media_type: 'movie', title: 'Джанго освобождённый', original_title: 'Django Unchained', release_date: '2012-12-25', poster_path: '/c.jpg' };

function key(name) { return qdl.matchKey(qdl.cleanName(name)); }

test('releaseYear: год берётся из ПОЛНОГО имени раздачи (cleanName его срезает)', () => {
  assert.strictEqual(qdl.releaseYear('Holod.S01.2026.WEB-DL.1080p.ExKinoRay'), 2026);
  assert.strictEqual(qdl.releaseYear('Django.Unchained.2012.WEB-DL.KP.1080p-SOFCJ.mkv'), 2012);
  assert.strictEqual(qdl.releaseYear('Some.Release.1080p.mkv'), 0);
  assert.strictEqual(qdl.cleanName('Holod.S01.2026.WEB-DL.1080p.ExKinoRay'), 'Holod S01');
});

test('matchKey: хвост сезона/серии в ключ сверки не идёт', () => {
  assert.strictEqual(key('Holod.S01.2026.WEB-DL.1080p.ExKinoRay'), 'holod');
  assert.strictEqual(key('Silo (Season 3) WEB-DL 1080p'), 'silo');
  assert.strictEqual(key('Лаки (Lucky) Сезон 1'), 'лаки');
});

test('«Голод-33» больше не приклеивается к «Holod…»', () => {
  const k = key('Holod.S01.2026.WEB-DL.1080p.ExKinoRay');
  const year = qdl.releaseYear('Holod.S01.2026.WEB-DL.1080p.ExKinoRay');

  assert.strictEqual(qdl.cardMatches(k, year, GOLOD33), false, 'чужой фильм — не карточка этой раздачи');
  // и правильную кириллическую карточку по латинскому имени тоже не угадываем:
  // её донесёт сервер по btih → tmdb, а гадать нельзя
  assert.strictEqual(qdl.cardMatches(k, year, HOLOD), false);
});

test('очевидное совпадение по оригинальному названию проходит', () => {
  const name = 'Django.Unchained.2012.WEB-DL.KP.1080p-SOFCJ.mkv';
  assert.strictEqual(qdl.cardMatches(key(name), qdl.releaseYear(name), DJANGO), true);
});

test('совпало название, но год мимо → не берём', () => {
  const other = Object.assign({}, DJANGO, { release_date: '1966-12-23' });   // «Джанго» 1966
  assert.strictEqual(qdl.cardMatches('django unchained', 2012, other), false);
  // ±1 год — допуск (дата релиза TMDB и год в имени раздачи часто разъезжаются)
  assert.strictEqual(qdl.cardMatches('django unchained', 2013, DJANGO), true);
});

test('год в имени раздачи отсутствует → сверяем только название', () => {
  assert.strictEqual(qdl.cardMatches('django unchained', 0, DJANGO), true);
});

test('пустой ключ или пустой кандидат — всегда мимо', () => {
  assert.strictEqual(qdl.cardMatches('', 2012, DJANGO), false);
  assert.strictEqual(qdl.cardMatches('django unchained', 2012, null), false);
  assert.strictEqual(qdl.cardMatches('django unchained', 2012, {}), false);
});

test('название карточки как начало имени раздачи (и наоборот) — совпадение', () => {
  assert.strictEqual(qdl.cardMatches('лаки', 0, { name: 'Лаки', first_air_date: '2026-01-01' }), true);
  assert.strictEqual(qdl.cardMatches('пункт назначения кровные узы', 0,
    { title: 'Пункт назначения', release_date: '2025-05-14' }), true);
});
