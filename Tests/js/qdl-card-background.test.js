'use strict';
// Фон под фокусом карточки (qdl 2.72) — jut.su, XSMART и «Загрузки».
//
// Родная Lampa на своих экранах красит полноэкранный фон так:
//   Card.create() → hover:focus → onFocus → Background.change(Utils.cardImgBackground(data))
// Наши разделы карточки собирают сами и Background.change не звали вовсе — фон оставался тем,
// что оставил предыдущий экран.
//
// 🔴 Две вещи здесь ломаются молча и видны только на живом клиенте:
//   1. Подписка только на hover:focus. Десктопным клиентам lampainit-invc.js форсит
//      navigation_type='mouse', а в этом режиме Lampa на mouseenter шлёт ТОЛЬКО hover:hover —
//      на Windows/Mac фон не менялся бы вообще, то есть ровно там, откуда пришла жалоба.
//   2. Постер, посчитанный ЗАРАНЕЕ. В «Загрузках» enrich() дописывает t.posterUrl уже после
//      сборки карточки: сохранённая ссылка навсегда осталась бы заглушкой PX1.
//
// Компоненты здесь не поднимаются (Scroll/$ заглушены) — проверяем по исходнику, как в
// qdl-jut-lazy.test.js.
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

/** Срез исходника между двумя объявлениями верхнего уровня. */
function slice(from, to) {
  const src = H.qdlSource();
  const i = src.indexOf(from);
  const j = src.indexOf(to, i + 1);
  assert.ok(i > 0, from + ' не найдено');
  assert.ok(j > i, to + ' не найдено после ' + from);
  return src.slice(i, j);
}

const AREAS = [
  { name: 'каталог jut.su', src: () => slice('function ComponentJutCatalog', 'function ComponentJutTitle'), url: 'bgFocus(psrc)' },
  { name: 'поиск jut.su', src: () => slice('function ComponentJutSearch', 'function healthRow'), url: 'bgFocus(psrc)' },
  { name: '«Загрузки» и коллекции', src: () => slice('function ComponentDownloads', 'function relTime'), url: 'bgFocus(posterUrl(' },
];

test('bgFocus существует и гасит заглушку PX1', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function bgFocus(');
  assert.ok(i > 0, 'хелпер bgFocus не найден');
  const fn = src.slice(i, src.indexOf('\n    }', i));

  assert.ok(/url\s*===\s*PX1/.test(fn),
    'PX1 — прозрачный пиксель-заглушка «постера нет»; отдав её в Background.change, ' +
    'мы стёрли бы фон в пустоту вместо того, чтобы оставить прежний');
  assert.ok(fn.includes('Lampa.Background.change('), 'bgFocus обязан звать Lampa.Background.change');
  assert.ok(/try\s*\{[^}]*Lampa\.Background\.change/.test(fn),
    'вызов обязан быть в try/catch: косметика не имеет права ронять экран');
});

for (const area of AREAS) {
  test('фон красится на всех карточках: ' + area.name, () => {
    assert.ok(area.src().includes(area.url),
      'в разделе «' + area.name + '» нет вызова ' + area.url + ' — фон останется от предыдущего экрана');
  });

  test('подписка идёт и на hover:hover (мышь десктопа): ' + area.name, () => {
    const src = area.src();
    const re = /el\.on\('hover:focus hover:hover', function \(\) \{ bgFocus\(/g;
    assert.ok(re.test(src),
      'только hover:focus недостаточно: на Windows/Mac navigation_type=mouse, ' +
      'и Lampa на mouseenter шлёт ТОЛЬКО hover:hover — фон бы не менялся вовсе');
  });

  test('hover:touch к фону НЕ привязан: ' + area.name, () => {
    const src = area.src();
    const bad = src.split('\n').filter((l) => l.includes('bgFocus(') && l.includes('hover:touch'));
    assert.deepStrictEqual(bad, [],
      'на таче trigger_mouseenter не привязывается вовсе и родная Lampa фон на тапах тоже ' +
      'не красит, а touchstart во время пальцевого скролла красил бы случайные карточки');
  });
}

test('«Загрузки»: постер считается В МОМЕНТ фокуса, а не заранее', () => {
  const src = AREAS[2].src();
  assert.ok(src.includes("el.on('hover:focus hover:hover', function () { bgFocus(posterUrl(t)); });"),
    'posterUrl(t) обязан считаться внутри обработчика: enrich() дописывает t.posterUrl ' +
    'уже ПОСЛЕ сборки карточки, и вычисленная заранее ссылка навсегда осталась бы PX1');
  assert.ok(src.includes("el.on('hover:focus hover:hover', function () { bgFocus(posterUrl(c.cover)); });"),
    'у папки-коллекции фон берётся с постера фильма-обложки');
});

test('каталог jut.su красится постером, а не backdrop-ом', () => {
  // /qdl/jut/backdrop для НЕ открытого тайтла лезет на jut.su за страницей тайтла и качает
  // кадр 2560×1440 — прогулка фокусом по каталогу стала бы десятками походов на источник.
  const src = AREAS[0].src();
  const bg = src.split('\n').filter((l) => l.includes('bgFocus('));
  assert.ok(bg.length > 0, 'вызова bgFocus в каталоге нет');
  assert.ok(!bg.some((l) => l.includes('jutBackdropUrl')),
    'backdrop в каталоге запрещён: он тянется с самого jut.su на каждый незнакомый тайтл');
});

test('экраны тайтла свой фон не потеряли', () => {
  const src = H.qdlSource();
  assert.ok(src.includes('Lampa.Background.change(jutBackdropUrl(slug))'),
    'на экране тайтла jut.su фон — настоящий backdrop 2560×1440 с самой страницы тайтла');
});
