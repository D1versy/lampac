'use strict';
// Каталог jut.su: постеры грузятся лениво, по мере показа карточки (qdl 2.41).
//
// Жалоба владельца: карточки на jut.su «медленнее», чем на главной. Замеры показали, что
// каталожный JSON тут ни при чём (снапшот-индекс отдаёт страницу за 3-4 мс на любой глубине,
// а карточек на странице даже БОЛЬШЕ, чем у CUB — 30 против 20). Дело было в постерах:
//   страница jut.su  30 карточек = 6.1 МБ     страница CUB  20 карточек = 0.72 МБ
// и все 30 заказывались РАЗОМ, при лимите браузера в 6 соединений на origin.
//
// Механика лечения: своего lazy писать не пришлось — шаблон 'card' уже несёт класс
// layer--visible, Lampa.Scroll в scrollEnded() зовёт Layer.visible(html) в ветке else от
// onScroll, а Layer шлёт на элемент настоящее DOM-событие 'visible' (Utils.trigger →
// createEvent + dispatchEvent). Событие уже летело нашим карточкам — на него никто не
// подписывался. Порог Layer — ±2 экрана, то есть картинка приезжает заранее.
//
// Канон: E:\Media-server\claude\jut\05-client.md
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function catalogSrc() {
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentJutCatalog');
  const j = src.indexOf('function ComponentJutTitle');
  assert.ok(i > 0 && j > i, 'ComponentJutCatalog не найден');
  return src.slice(i, j);
}

function appendSrc() {
  const fn = catalogSrc();
  const i = fn.indexOf('this.append = function');
  assert.ok(i > 0, 'this.append не найден');
  const j = fn.indexOf('this.render = function', i);
  return fn.slice(i, j > i ? j : i + 3000);
}

test('постер вешается на событие visible, а не присваивается при append', () => {
  const app = appendSrc();
  assert.ok(app.includes("el.on('visible', loadPoster);"),
    'карточка обязана подписаться на visible — иначе ленивой загрузки нет');
  assert.ok(!/img\.attr\('src',\s*jutPosterUrl/.test(app),
    'жадное присвоение src вернуло бы 6.1 МБ и 30 параллельных запросов на страницу');
});

test('после каждой пачки карточек грид просят пересчитать видимость', () => {
  const fn = catalogSrc();
  assert.ok(/Lampa\.Layer\.visible\(scroll\.render\(true\)\)/.test(fn),
    'без явного вызова ПЕРВАЯ страница не получит ни одного visible: Scroll.startScroll ' +
    'при неизменившейся позиции зовёт scrollEnded() только если !isFilled(), ' +
    'а 30 карточек экран заведомо заполняют');
});

test('видимость пересчитывается на КАЖДОЙ странице, а toggle — только на первой', () => {
  const fn = catalogSrc();

  // Правило qdl 2.38 нетронуто: toggle по-прежнему под гардом первой страницы.
  assert.ok(fn.includes('if (p === 1) comp.activity.toggle();'),
    'правило qdl 2.38 обязано остаться нетронутым');

  // А вот Layer.visible гардом p === 1 накрывать НЕЛЬЗЯ: догруженная пачка тогда
  // осталась бы с заглушками до первой прокрутки.
  const i = fn.indexOf('Lampa.Layer.visible(scroll.render(true))');
  assert.ok(i > 0, 'вызов Layer.visible не найден');
  const line = fn.slice(fn.lastIndexOf('\n', i) + 1, i + 60);
  assert.ok(!/p\s*===\s*1/.test(line),
    'Layer.visible обязан зваться на любой странице, иначе догрузка остаётся с img_load.svg');
});

test('scroll.onScroll не назначается — иначе Layer.visible отключится молча', () => {
  const fn = catalogSrc();
  assert.ok(!/scroll\.onScroll\s*=/.test(fn),
    'Lampa.Scroll зовёт Layer.visible ТОЛЬКО в ветке else от onScroll: ' +
    'назначив onScroll, мы убьём ленивую загрузку и не заметим этого');
});

// ⚠️ Искать hover:focus надо ВНУТРИ this.append: первым в компоненте идёт обработчик
// плитки «Поиск» (appendSearchTile), и проверка молча уехала бы не на ту карточку.
function cardFocusHandler() {
  const app = appendSrc();
  const i = app.indexOf("el.on('hover:focus'");
  assert.ok(i > 0, 'hover:focus у карточки не найден');
  return app.slice(i, i + 500);
}

test('сфокусированная карточка получает постер принудительно', () => {
  assert.ok(cardFocusHandler().includes('loadPoster()'),
    'страховка обязательна: пульт (зажатый ArrowDown) может обогнать Layer, ' +
    'и карточка осталась бы с заглушкой img_load.svg');
});

test('hover:focus сохранил всё прежнее поведение (last / scroll.update / prefetch)', () => {
  const h = cardFocusHandler();
  assert.ok(h.includes('last = el[0]'), 'last обязан писаться (правило скролла 2.38)');
  assert.ok(h.includes('scroll.update(el, true)'), 'scroll.update потерян');
  assert.ok(h.includes('comp.prefetch(el)'), 'префетч следующей страницы потерян (qdl 2.29)');
});

test('обработчик ошибки картинки остался', () => {
  const app = appendSrc();
  assert.ok(/img\.on\('error'/.test(app) && app.includes('img_broken.svg'),
    'битый постер обязан подменяться заглушкой');
});
