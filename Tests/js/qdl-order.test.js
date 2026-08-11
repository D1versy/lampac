'use strict';
// Порядок кнопок полной карточки (qdl 2.30): [Продолжить][Смотреть][Скачать] … родные … [priority][Онлайн].
// Вставки идут вразнобой (complite синхронно, /qdl/list и /qdl/episodes асинхронно), а onGroupButtons
// Lampa на каждом входе в контроллер может prepend'ить клон .button--priority — порядок держит
// идемпотентная orderButtons + MutationObserver. Плюс ребрендинг .button--play → «Онлайн» (коробка).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// родной ряд новой карточки: play + book + reaction + options (как в шаблоне full_start_new)
const PAGE =
  '<div class="full-start-new__buttons">' +
    '<div class="full-start__button selector button--play"><svg viewBox="0 0 28 29"><use xlink:href="#sprite-play"></use></svg><span>Смотреть</span></div>' +
    '<div class="full-start__button selector button--book"><span>Закладки</span></div>' +
    '<div class="full-start__button selector button--reaction"><span>Реакции</span></div>' +
    '<div class="full-start__button selector button--options"></div>' +
  '</div>';

function lampaFor(method, fixture) {
  return H.makeLampa({
    Activity: { active: () => ({ method, source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok) => { if (fixture && String(url).indexOf('/qdl/list') !== -1) ok(fixture); };
    },
  });
}

function fireAddButton(w, movie) {
  w.fetch = () => Promise.resolve({ json: () => Promise.resolve({}) });
  w.__qdl.addButton({
    type: 'complite',
    object: { activity: { render: () => w.$('body') } },
    data: { movie },
  });
}

const KNOWN = ['qdl-continue-btn', 'qdl-watch-btn', 'qdl-download', 'button--priority', 'button--play',
  'button--book', 'button--reaction', 'button--options', 'view--online'];
function key(b) {
  for (const c of KNOWN) if (b.classList.contains(c)) return c;
  return b.className;
}
const row = (doc) => [...doc.querySelectorAll('.full-start-new__buttons .full-start__button')].map(key);

function buildRow(w, classes) {
  const cont = w.$('<div class="full-start-new__buttons"></div>');
  for (const c of classes) cont.append('<div class="full-start__button ' + c + '"></div>');
  w.$('body').empty().append(cont);
  return cont;
}

// ─────────────────────────────── интеграция addButton ───────────────────────────────

test('UI: без загрузок — «Скачать» первая, «Онлайн» (коробка) последняя, без дублей на повторе', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', null) });
  fireAddButton(w, { id: 693134, title: 'Дюна 2' });

  assert.deepStrictEqual(row(doc),
    ['qdl-download', 'button--book', 'button--reaction', 'button--options', 'button--play']);

  const play = doc.querySelector('.button--play');
  assert.ok(play.classList.contains('qdl-online-btn'), 'guard-класс ребрендинга');
  assert.strictEqual(play.querySelector('span').textContent, 'Онлайн');
  assert.strictEqual(play.querySelector('use'), null, 'спрайт play заменён');
  assert.ok(play.querySelector('svg path'), 'иконка-коробка на месте');

  fireAddButton(w, { id: 693134, title: 'Дюна 2' });   // повторный complite (возврат в карточку)
  assert.strictEqual(doc.querySelectorAll('.button--play svg').length, 1, 'иконка не дублируется');
  assert.strictEqual(doc.querySelectorAll('.qdl-download').length, 1, 'кнопка не дублируется');
});

test('UI: есть загрузка — [Смотреть][Скачать] в голове ряда, подпись без «(загружено)»', () => {
  const fixture = [{ hash: 'x9', name: 'Дюна 2021 WEB-DL' }];
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', fixture) });
  fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  assert.deepStrictEqual(row(doc),
    ['qdl-watch-btn', 'qdl-download', 'button--book', 'button--reaction', 'button--options', 'button--play']);
  assert.strictEqual(doc.querySelector('.qdl-watch-btn span').textContent, 'Смотреть');
});

test('CSS: подписи наших кнопок видны всегда (перебивают :not(.focus) span{display:none})', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', null) });
  fireAddButton(w, { id: 5, title: 'Х' });
  const css = doc.getElementById('qdl-css').textContent;
  assert.ok(css.includes('.full-start-new__buttons .qdl-download span'), 'селектор подписи «Скачать»');
  assert.ok(css.includes('.full-start-new__buttons .qdl-continue-btn span'), 'селектор подписи «Продолжить»');
  assert.ok(css.includes('display:inline !important'), 'важность против правила Lampa');
  assert.ok(css.includes('text-overflow:ellipsis'), 'кап ширины подписи (epShort до 24 символов)');
});

test('CSS: ряд ПЕРЕНОСИТСЯ, а не скроллится — обрезанный хвост = фокус на невидимой кнопке', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', null) });
  fireAddButton(w, { id: 5, title: 'Х' });
  const css = doc.getElementById('qdl-css').textContent;
  assert.ok(/\.full-start-new__buttons\{flex-wrap:wrap/.test(css.replace(/\.full-start__buttons,/, '')),
    'перенос строк вместо горизонтального скролла');
  assert.ok(!css.includes('overflow-x:auto'),
    'скролл-контейнер ряду не нужен: его никто не прокручивает (не Lampa.Scroll, нет scrollIntoView)');
});

// ─────────────────────────── карточка из «Загрузок» (.qdl-only) ───────────────────────────
// Главный сценарий редизайна: [Продолжить · S1 · Серия 2][Смотреть]. До 2.30 эта ветка
// и создание qdl-continue-btn не исполнялись ни одним тестом (можно было сломать молча).

const HASH = 'a'.repeat(40);
const EPISODES = [
  { index: 0, name: 'Show.S01E01.mkv', epkey: 's1e1', tl: 'show:s1e1' },
  { index: 1, name: 'Show.S01E02.mkv', epkey: 's1e2', tl: 'show:s1e2' },
  { index: 2, name: 'Show.S01E03.mkv', epkey: 's1e3', tl: 'show:s1e3' },
];

// percents — прогресс по сериям [e1, e2, …]; пустой массив = не смотрели ничего
function lampaDownloads(percents) {
  const L = H.makeLampa({
    Activity: { active: () => ({ qdl_hash: HASH, qdl_progress: 1, method: 'tv', source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(EPISODES);
      };
    },
  });
  // ключ таймлайна — Utils.hash('qdltl:'+tl), в харнессе hash = 'h'+строка
  EPISODES.forEach((f, i) => {
    if (percents[i] === undefined) return;
    L.Timeline._store['hqdltl:' + f.tl] = { percent: percents[i], time: 0, duration: 0, handler() {} };
  });
  return L;
}

test('UI: карточка из «Загрузок» → .qdl-only и [Продолжить · S1 · Серия 2][Смотреть] в голове', () => {
  // 1-я досмотрена, 2-я на паузе → продолжаем вторую
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaDownloads([100, 42]) });
  fireAddButton(w, { id: 90228, name: 'Show' });

  assert.ok(doc.body.classList.contains('qdl-only'), 'режим одной кнопки включён');
  assert.deepStrictEqual(row(doc).slice(0, 2), ['qdl-continue-btn', 'qdl-watch-btn'],
    'синяя «Продолжить» пришла async ПОСЛЕ зелёной, но встала первой');
  assert.strictEqual(doc.querySelector('.qdl-continue-btn span').textContent, 'Продолжить · S1 · Серия 2');
  assert.strictEqual(doc.querySelector('.qdl-watch-btn span').textContent, 'Смотреть');
  assert.strictEqual(doc.querySelectorAll('.qdl-download').length, 0, '«Скачать» в этом режиме не нужна');
  // иконки РАЗНЫЕ — исходная жалоба владельца была именно про одинаковые
  const svg = (sel) => doc.querySelector(sel + ' svg').innerHTML;
  assert.notStrictEqual(svg('.qdl-continue-btn'), svg('.qdl-watch-btn'), 'у кнопок разные иконки');
  assert.ok(svg('.qdl-watch-btn').includes('circle'), 'у «Смотреть» play в круге');
});

test('UI: нечего продолжать (ничего не смотрели) → синей кнопки нет, зелёная одна', () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaDownloads([]) });
  fireAddButton(w, { id: 90228, name: 'Show' });
  assert.strictEqual(doc.querySelectorAll('.qdl-continue-btn').length, 0);
  assert.strictEqual(doc.querySelectorAll('.qdl-watch-btn').length, 1);
});

test('UI: DMCA + скачано → [Смотреть][Скачать], «Продолжить» больше не прячется CSS', () => {
  const fixture = [{ hash: 'x9', name: 'Дюна 2021 WEB-DL', meta: { id: 438631, media_type: 'movie', title: 'Дюна' } }];
  const lampa = H.makeLampa({
    Activity: { active: () => ({ method: 'movie', source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok) => { if (String(url).indexOf('/qdl/list') !== -1) ok(fixture); };
    },
  });
  const { w, doc, qdl } = H.loadQdlDom({ bodyHtml: PAGE, lampa });
  qdl.setDmcaList([{ id: 438631, cat: 'movie', kpid: 0 }]);
  fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  assert.ok(doc.body.classList.contains('qdl-dmca'));
  assert.deepStrictEqual(row(doc).slice(0, 2), ['qdl-watch-btn', 'qdl-download']);
  const css = doc.getElementById('qdl-css').textContent;
  assert.ok(css.includes(':not(.qdl-download):not(.qdl-watch-btn):not(.qdl-continue-btn)'),
    'на заблокированном скачанном сериале «Продолжить» обязана оставаться видимой');
});

test('fixFocus: авто-фокус на приоритетном клоне переезжает на первую кнопку ряда', () => {
  const focused = [];
  const lampa = H.makeLampa({ Controller: { collectionFocus: (t) => focused.push(t) } });
  const { w, qdl } = H.loadQdlDom({ lampa });
  const cont = buildRow(w, ['button--priority focus', 'qdl-download', 'button--book', 'button--play']);
  qdl.orderButtons(cont);

  assert.deepStrictEqual([...cont[0].children].map(key),
    ['qdl-download', 'button--book', 'button--priority', 'button--play']);
  assert.strictEqual(focused.length, 1, 'фокус спасён ровно один раз');
  assert.ok(focused[0].classList.contains('qdl-download'), 'фокус на первой кнопке, а не у правого края');
});

test('fixFocus: ручной фокус на обычной кнопке при реордере не трогаем', () => {
  const focused = [];
  const lampa = H.makeLampa({ Controller: { collectionFocus: (t) => focused.push(t) } });
  const { w, qdl } = H.loadQdlDom({ lampa });
  const cont = buildRow(w, ['button--play', 'qdl-download focus', 'button--book']);
  qdl.orderButtons(cont);
  assert.deepStrictEqual(focused, [], 'пользователь сам выбрал кнопку — фокус его');
});

// ───────── async-кнопки: коллекция навигатора и фокус на главном действии ─────────
// 🔥 Коллекция SpatialNavigator статична: Navigator.focus(el) возвращает false для элемента,
// которого в ней нет, move() его не видит. Наши кнопки приезжают ПОСЛЕ collectionSet — без
// collectionAppend пультом до «Смотреть» было не дойти. А фокус Lampa ставит ещё раньше, когда
// в ряду одна «Скачать» (жалоба владельца с ТВ: «фокус на Скачать, а должен быть на первой»).

function rigAsyncList() {
  const focused = [], appended = [];
  let listCb = null;
  const lampa = H.makeLampa({
    Activity: { active: () => ({ method: 'movie', source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok) => { if (String(url).indexOf('/qdl/list') !== -1) listCb = ok; };   // держим колбэк
    },
    Controller: { collectionFocus: (t) => focused.push(t), collectionAppend: (b) => appended.push(b) },
  });
  return { lampa, focused, appended, reply: () => listCb([{ hash: 'x9', name: 'Дюна 2021 WEB-DL',
    meta: { id: 438631, media_type: 'movie', title: 'Дюна' } }]) };
}

test('async «Смотреть»: регистрируется в коллекции навигатора и перетягивает фокус со «Скачать»', () => {
  const rig = rigAsyncList();
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: rig.lampa });
  fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  // Lampa фокусирует первый .selector на activity.toggle() — тогда в ряду только «Скачать»
  doc.querySelector('.qdl-download').classList.add('focus');
  rig.reply();

  assert.strictEqual(rig.appended.length, 1, 'кнопка добавлена в коллекцию навигатора');
  assert.ok(rig.appended[0][0].classList.contains('qdl-watch-btn'), 'именно приехавшая «Смотреть»');
  assert.strictEqual(rig.focused.length, 1, 'фокус переведён один раз');
  assert.ok(rig.focused[0].classList.contains('qdl-watch-btn'), 'фокус на «Смотреть», а не на «Скачать»');
});

test('async «Смотреть»: если пользователь уже нажал кнопку пульта — фокус не забираем', () => {
  const rig = rigAsyncList();
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: rig.lampa });
  fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });

  doc.querySelector('.qdl-download').classList.add('focus');
  w.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'ArrowRight' }));   // пользователь пошёл по ряду
  rig.reply();

  assert.strictEqual(rig.appended.length, 1, 'в коллекцию добавляем всегда — иначе кнопка недостижима');
  assert.deepStrictEqual(rig.focused, [], 'фокус остаётся там, куда его увёл пользователь');
});

test('navAppend: карточку успели закрыть — коллекцию активного экрана не пачкаем', () => {
  const rig = rigAsyncList();
  const { w, doc } = H.loadQdlDom({ bodyHtml: '<div class="activity--active"></div>' + PAGE, lampa: rig.lampa });
  fireAddButton(w, { id: 438631, title: 'Дюна', original_title: 'Dune' });
  rig.reply();   // ряд кнопок лежит ВНЕ активной активности — значит это уже другая карточка
  assert.deepStrictEqual(rig.appended, [], 'чужая кнопка в коллекции ловила бы фокус на другом экране');
});

// ─────────────────────────────── orderButtons: юниты ───────────────────────────────

test('orderButtons: любой порядок прихода async-вставок сводится к эталону', () => {
  const { w, qdl } = H.loadQdlDom({});
  const ETALON = ['qdl-continue-btn', 'qdl-watch-btn', 'qdl-download', 'button--book', 'button--options', 'button--play'];
  // родные (book/options) во всех перестановках в исходном отн. порядке —
  // гонка async двигает только НАШИ кнопки, стабильность родных проверяется отдельно
  const worst = [
    ['button--play', 'qdl-download', 'qdl-watch-btn', 'qdl-continue-btn', 'button--book', 'button--options'],
    ['button--book', 'button--play', 'qdl-continue-btn', 'button--options', 'qdl-download', 'qdl-watch-btn'],
    ['qdl-download', 'qdl-continue-btn', 'button--book', 'button--options', 'qdl-watch-btn', 'button--play'],
  ];
  for (const perm of worst) {
    const cont = buildRow(w, perm);
    qdl.orderButtons(cont);
    assert.deepStrictEqual([...cont[0].children].map(key), ETALON, perm.join(','));
  }
});

test('orderButtons: родные кнопки не переставляются между собой (стабильность)', () => {
  const { w, qdl } = H.loadQdlDom({});
  const cont = buildRow(w, ['button--options', 'button--reaction', 'button--book', 'qdl-download', 'button--play']);
  qdl.orderButtons(cont);
  assert.deepStrictEqual([...cont[0].children].map(key),
    ['qdl-download', 'button--options', 'button--reaction', 'button--book', 'button--play']);
});

test('orderButtons: идемпотентна — на верном порядке ни одной DOM-мутации', () => {
  const { w, qdl } = H.loadQdlDom({});
  const cont = buildRow(w, ['qdl-watch-btn', 'qdl-download', 'button--book', 'button--play']);
  qdl.orderButtons(cont);
  const mo = new w.MutationObserver(() => {});
  mo.observe(cont[0], { childList: true });
  qdl.orderButtons(cont);
  assert.strictEqual(mo.takeRecords().length, 0, 'иначе MutationObserver зациклился бы');
});

test('orderButtons: клон .button--priority от Lampa уезжает в конец, перед «Онлайн»', () => {
  const { w, qdl } = H.loadQdlDom({});
  const cont = buildRow(w, ['qdl-watch-btn', 'qdl-download', 'button--book', 'button--play']);
  qdl.orderButtons(cont);
  cont.prepend('<div class="full-start__button button--priority"></div>');   // так делает onPriorityButton
  qdl.orderButtons(cont);   // в проде это сделает MutationObserver
  assert.deepStrictEqual([...cont[0].children].map(key),
    ['qdl-watch-btn', 'qdl-download', 'button--book', 'button--priority', 'button--play']);
});

test('MutationObserver: prepend приоритетного клона Lampa чинится сам, без явного вызова', async () => {
  const { w, doc } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie', null) });
  fireAddButton(w, { id: 5, title: 'Х' });
  w.$('.full-start-new__buttons').prepend('<div class="full-start__button button--priority"></div>');
  await new Promise((r) => setTimeout(r, 0));   // observer-колбэк — микротаск, дать ему сработать
  const seq = row(doc);
  assert.strictEqual(seq[0], 'qdl-download', 'клон не удержался первым');
  assert.deepStrictEqual(seq.slice(-2), ['button--priority', 'button--play']);
});
