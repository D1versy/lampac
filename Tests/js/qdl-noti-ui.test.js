'use strict';
// Экран уведомлений (qdl 2.111) — жалоба владельца: «все уведомления летят в кучу и зависит
// от длины названия фильма».
//
// Причина была не в вёрстке «примерно», а буквально: у строки не существовало НИ ОДНОГО
// css-класса — всё жило инлайновым стилем, у заголовка не было ни white-space, ни overflow,
// ни text-overflow, а сама строка была flex без фиксированной высоты. Название вроде
// «Изгнанный реинкарнированный тяжёлый рыцарь не имеет себе равных в знаниях игры»
// переносилось на 2-3 строки, и высота строки уезжала относительно соседей.
//
// Здесь закреплены три вещи:
//   1. геометрия строки задана классами и не зависит от длины текста;
//   2. лента разбита на дни, а время в строке — только часы:минуты;
//   3. служебные виды (START/SWITCH/INFO/NOSPACE/DIAG) до клиента не доходят, а если дошли бы
//      от старого сервера — не врут «скачана».
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

// ─────────────────────────── 1. геометрия строки ───────────────────────────

test('строка ленты собрана классами, без инлайновых стилей', () => {
  const src = slice('this.append = function (n) {', 'this.appendId = function () {');
  assert.ok(src.includes('qdl-noti-row'), 'нет класса строки');
  assert.ok(src.includes('qdl-noti-pos'), 'нет класса постера');
  assert.ok(src.includes('qdl-noti-ttl'), 'нет класса заголовка');
  assert.ok(src.includes('qdl-noti-sub'), 'нет класса текста');
  assert.ok(src.includes('qdl-noti-time'), 'нет класса времени');
  // 🔴 главное: ни одного style= обратно в разметку строки
  assert.ok(!/style="/.test(src), 'инлайновый стиль вернулся в строку ленты');
});

test('заголовок и текст обрезаются многоточием в ОДНУ строку', () => {
  const css = H.qdlSource();
  for (const cls of ['.qdl-noti-ttl', '.qdl-noti-sub']) {
    const i = css.indexOf("'" + cls + "{");
    assert.ok(i > 0, 'нет правила ' + cls);
    const rule = css.slice(i, css.indexOf("}'", i));
    assert.ok(rule.includes('white-space:nowrap'), cls + ': нет white-space:nowrap');
    assert.ok(rule.includes('overflow:hidden'), cls + ': нет overflow:hidden');
    assert.ok(rule.includes('text-overflow:ellipsis'), cls + ': нет text-overflow:ellipsis');
  }
});

test('высоту строки задаёт постер фиксированного размера', () => {
  const css = H.qdlSource();
  const i = css.indexOf("'.qdl-noti-pos{");
  assert.ok(i > 0, 'нет правила .qdl-noti-pos');
  const rule = css.slice(i, css.indexOf("}'", i));
  assert.ok(/width:[\d.]+em/.test(rule) && /height:[\d.]+em/.test(rule), 'размер постера не задан');
  assert.ok(rule.includes('flex:none'), 'постер сжимается — высота строки поедет');
});

test('строка занимает всю ширину — иначе .category-full ставит их в ряд', () => {
  // 🔴 САМА причина жалобы «летит в кучу и зависит от длины названия»: контейнер ленты
  // .category-full у Lampa — display:flex;flex-wrap:wrap (сделан под карточки). Строка без
  // базиса была flex-элементом ПО СОДЕРЖИМОМУ, и ширина равнялась длине названия: замер на
  // живом клиенте до фикса — 1000, 353, 542, 587, 554 px в одной ленте, по 2-3 строки в ряд.
  const css = H.qdlSource();
  for (const cls of ['.qdl-noti-row', '.qdl-noti-id']) {
    const i = css.indexOf("'" + cls + "{");
    assert.ok(i > 0, 'нет правила ' + cls);
    const rule = css.slice(i, css.indexOf("}'", i));
    assert.ok(/flex:1 1 100%/.test(rule), cls + ': нет flex:1 1 100% — встанет в ряд с соседями');
  }
});

test('колонка текста не распирает строку', () => {
  const css = H.qdlSource();
  const i = css.indexOf("'.qdl-noti-txt{");
  const rule = css.slice(i, css.indexOf("}'", i));
  // min-width:0 обязателен: без него flex-элемент не даёт себя сжать и ellipsis не работает
  assert.ok(rule.includes('min-width:0'), 'нет min-width:0 — многоточие не сработает');
});

// ─────────────────────────── 2. дни и время ───────────────────────────

test('лента идёт подряд, без разделителей дней', () => {
  // Решение владельца 05.09.2026: «Сегодня : Вчера — и даты не нужно показывать,
  // просто шли подряд уведомления». Лента и так отсортирована от свежего к старому.
  const src = slice('this.build = function (items) {', 'this.append = function (n) {');
  assert.ok(!src.includes('qdl-noti-day'), 'разделители дней вернулись');
  assert.ok(!H.qdlSource().includes('function dayLabel'), 'функция дня вернулась');
});

test('в строке — только часы:минуты, без даты', () => {
  const src = slice('this.append = function (n) {', 'this.appendId = function () {');
  assert.ok(src.includes('dayTime('), 'время строки считается не dayTime');
  assert.ok(!/getFullYear|getMonth/.test(src), 'в строку просочилась дата');
});

test('непрочитанное снимается на сборке экрана', () => {
  // build() сразу за append() метит ВСЮ ленту прочитанной — без снимка точка не появилась бы
  const src = slice('this.append = function (n) {', 'this.appendId = function () {');
  assert.ok(src.includes('n.read === false'), 'непрочитанное не отмечается');
  assert.ok(src.includes('unread'), 'нет класса unread');
});

// ─────────────────────────── 3. виды уведомлений ───────────────────────────

test('волна новых серий — своя корзина, а не «скачана»', () => {
  const { qdl } = H.loadQdl({});
  assert.strictEqual(qdl.notiBucket({ kind: 'WAVE' }), 'wave');
  assert.strictEqual(qdl.notiBucket({ kind: null }), 'done');
  assert.strictEqual(qdl.notiBucket({ kind: 'NEW' }), 'new');
  assert.strictEqual(qdl.notiBucket({ kind: 'SEASON' }), 'season');
  assert.strictEqual(qdl.notiBucket({ kind: 'TITLE' }), 'title');
});

test('у каждой корзины свой значок, неизвестный вид не притворяется серией', () => {
  const { qdl } = H.loadQdl({});
  assert.strictEqual(qdl.notiIcon({ kind: 'WAVE' }), '📺');
  assert.strictEqual(qdl.notiIcon({ kind: 'NEW' }), '🆕');
  assert.strictEqual(qdl.notiIcon({ kind: 'SEASON' }), '🗓');
  assert.strictEqual(qdl.notiIcon({ kind: 'TITLE' }), '📦');
  assert.strictEqual(qdl.notiIcon({ kind: 'ЧТО-ТО-НОВОЕ' }), '🔔');
});

test('к готовому тексту сервера не дописывается «скачана»', () => {
  // С qdl 2.111 сервер шлёт «Вышла новая серия 10» — приписка давала «…серия 10 скачана».
  const src = slice('function pollNotifications', 'function openNotification');
  assert.ok(!src.includes("+ ' скачана'"), 'приписка «скачана» вернулась');
});

test('служебные виды остались в белом списке как нейтральные', () => {
  // Старый сервер + новый клиент: строки ещё могут прийти и НЕ должны врать про скачивание
  const { qdl } = H.loadQdl({});
  for (const k of ['START', 'SWITCH', 'INFO', 'NOSPACE', 'DIAG']) {
    assert.notStrictEqual(qdl.notiBucket({ kind: k }), 'done', k + ' попал в «скачана»');
    assert.notStrictEqual(qdl.notiBucket({ kind: k }), 'wave', k + ' попал в «волну»');
  }
});
