'use strict';
// Каталог jut.su: бесконечная лента не должна терять место при догрузке (qdl 2.38).
//
// Жалоба владельца: на телефоне быстро проскроллил вниз, следующая страница не успела
// прийти — и после её прихода экран улетал в САМОЕ НАЧАЛО списка вместо продолжения.
//
// Механика: comp.activity.toggle() звался на КАЖДУЮ страницу → Activity.start →
// Controller.toggle('content') → наш toggle → collectionFocus(last || false). На таче
// hover:focus не приходит вообще, поэтому last оставался пустым, фокус вставал на первую
// карточку, а её hover:focus утаскивал scroll.update наверх.
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

test('догрузка страницы НЕ дёргает activity.toggle (иначе фокус уезжает в начало)', () => {
  const fn = catalogSrc();
  assert.ok(fn.includes('if (p === 1) comp.activity.toggle();'),
    'toggle допустим только на первой странице — так же поступает upstream InteractionCategory.next');
  assert.ok(!/\n\s*comp\.activity\.toggle\(\);/.test(fn.replace('if (p === 1) comp.activity.toggle();', '')
      .replace(/this\.empty[\s\S]*?\n\s*\};/, '')),
    'безусловного comp.activity.toggle() в load() быть не должно');
});

test('тач помечает last (без этого collectionFocus всегда падает на первую карточку)', () => {
  const fn = catalogSrc();
  // qdl 2.80: локальный markLast уехал в общий хелпер — теперь так пишут ВСЕ наши экраны
  assert.strictEqual(
    (fn.match(/\.on\('hover:touch hover:hover', function \(\) \{ last = markLast\(el\); \}\);/g) || []).length, 2,
    'карточки И плитка поиска обязаны писать last по тачу и мыши — образец app.min.js');
  // именно focused, а не focus: focus триггерит hover:focus и двигает скролл
  const mk = H.qdlSource().match(/function markLast\(el\)[\s\S]{0,240}/)[0];
  assert.ok(mk.includes('Navigator.focused(el[0])'));
  assert.ok(mk.includes('return el[0];'), 'хелпер отдаёт элемент вызывающей стороне — им и пишется last');
  assert.ok(!mk.includes('Navigator.focus('), 'Navigator.focus утащит скролл — нужен focused');
});

test('карточки уходят только в СВОЮ коллекцию фокуса', () => {
  const fn = catalogSrc();
  assert.ok(/Lampa\.Controller\.own\(comp\)[\s\S]{0,60}collectionAppend/.test(fn),
    'ответ мог прийти, когда пользователь ушёл в меню — тогда карточки попадут в чужую коллекцию');
  assert.ok(fn.includes('link: comp,'),
    'без link контроллера Controller.own(comp) всегда ложь и collectionAppend не сработает');
});

test('повторный слаг не рисуется дважды (лента сдвигается новинками)', () => {
  const fn = catalogSrc();
  assert.ok(fn.includes('seenSlugs'), 'нужен дедуп по слагу');
  const i = fn.indexOf('this.append = function');
  const app = fn.slice(i, i + 500);
  assert.ok(app.includes('if (seenSlugs[c.slug]) return;'),
    'дедуп обязан стоять В НАЧАЛЕ append, до создания карточки');
});

test('пагинация и стартовый фокус на месте', () => {
  const fn = catalogSrc();
  // регресс-страховка: чиня скролл, легко потерять саму догрузку
  assert.ok(fn.includes('scroll.onEnd'), 'бесконечная лента');
  assert.ok(fn.includes('PREFETCH_AHEAD'), 'префетч за два ряда — на пульте иначе ждёшь на дне');
  assert.ok(fn.includes('focusBack(scroll, last)'),
    'возврат из карточки обязан восстанавливать позицию (qdl 2.80 — общий хелпер)');
  assert.ok(fn.includes('scroll.minus()'), 'без minus() на ТВ у .scroll нет высоты');
});
