'use strict';
// Экран qdl_card (ComponentCard) — карточка загрузки БЕЗ TMDB id: аниме с jut.su (сервер пишет
// meta.id = 0) и безымянные раздачи. До qdl 2.33 экран был мёртвым (зарегистрирован, но никем не
// открывался), поэтому поведенческих тестов на него не существовало вообще — при этом именно он
// теперь отвечает на жалобу владельца «из „Загрузок“ запускается плеер вместо карточки».
//
// Проверяем то, что реально ломается: появление синей «Продолжить · S1 · Серия N», её регистрацию
// в коллекции навигатора (без collectionAppend кнопка недостижима пультом — 06 §BE), куда она
// ведёт, и что у фильма/одной серии её нет.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HASH = 'b'.repeat(40);
const EPISODES = [
  { index: 0, name: 'Show.S01E01.mkv', epkey: 's1e1', tl: 'jut:show:s1e1' },
  { index: 1, name: 'Show.S01E02.mkv', epkey: 's1e2', tl: 'jut:show:s1e2' },
  { index: 2, name: 'Show.S01E03.mkv', epkey: 's1e3', tl: 'jut:show:s1e3' },
];
const ITEM = {
  hash: HASH, name: 'guimi-zhi-zhu', progress: 1, local: true, state: 'local',
  jut: { slug: 'guimi-zhi-zhu' },
  meta: { id: 0, media_type: 'tv', title: 'Повелитель тайн', year: 2025, overview: 'Описание' },
};

// Lampa.Scroll харнесса отдаёт болванки вне DOM — для экрана нужен скролл на настоящем jQuery,
// иначе кнопки не окажутся в документе и проверять будет нечего.
function jsdomScroll(w) {
  return function () {
    const render = w.$('<div class="scroll"><div class="scroll__body"></div></div>');
    this.render = () => render;
    this.body = () => render.find('.scroll__body');
    this.append = (el) => render.find('.scroll__body').append(el);
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

// files — что отдаёт /qdl/episodes; percents — прогресс просмотра по сериям
function rig(files, percents) {
  const calls = { pushes: [], appended: [] };
  const { w, doc, qdl, lampa } = H.loadQdlDom({});
  w.Lampa.Scroll = jsdomScroll(w);   // qdl.js берёт Lampa.Scroll в момент создания компонента
  w.Lampa.Activity.push = (a) => calls.pushes.push(a);
  w.Lampa.Controller.collectionAppend = (b) => calls.appended.push(b);
  w.Lampa.Reguest = function () {
    this.timeout = () => {}; this.clear = () => {};
    this.silent = (url, ok) => {
      const u = String(url);
      if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
    };
  };
  (percents || []).forEach((p, i) => {
    if (p === undefined || !files[i]) return;
    lampa.Timeline._store['hqdltl:' + files[i].tl] = { percent: p, time: 0, duration: 0, handler() {} };
  });
  w.fetch = () => Promise.resolve({ json: () => Promise.resolve({}) });

  const comp = new qdl.ComponentCard({ qdl: ITEM });
  comp.activity = { loader() {}, toggle() {} };
  w.$('body').append(comp.create());
  return { w, doc, calls, comp, lampa };
}

// прогресс просмотра серии — как после возврата из плеера
function watched(lampa, tl, pct) {
  lampa.Timeline._store['hqdltl:' + tl] = { percent: pct, time: 0, duration: 0, handler() {} };
}

test('сериал с прогрессом → [Продолжить · S1 · Серия 2][Смотреть], обе кнопки в ряду', () => {
  const { doc } = rig(EPISODES, [100, 42]);   // первая досмотрена, вторая на паузе
  const btns = [...doc.querySelectorAll('.qdl-card-btns .selector')]
    .map((b) => (b.className.match(/qdl-[a-z-]+/g) || []).join('+') + ':' + b.textContent.trim());
  assert.deepStrictEqual(btns, ['qdl-continue:Продолжить · S1 · Серия 2', 'qdl-watch:Смотреть'],
    'синяя «Продолжить» первая, зелёная «Смотреть» — за ней');
});

test('«Продолжить» регистрируется в коллекции навигатора (иначе пультом не дойти)', () => {
  const { doc, calls } = rig(EPISODES, [100, 42]);
  assert.strictEqual(calls.appended.length, 1);
  assert.ok(calls.appended[0][0].classList.contains('qdl-continue'));
  assert.ok(doc.querySelector('.qdl-continue svg'), 'у кнопки своя иконка (resume), а не текст');
});

test('«Продолжить» ведёт на экран серий с автоплеем, а не в плеер напрямую', () => {
  const { w, doc, calls } = rig(EPISODES, [100, 42]);
  w.$(doc.querySelector('.qdl-continue')).trigger('hover:enter');   // ровно то, что делает Enter с пульта
  const push = calls.pushes[calls.pushes.length - 1];
  assert.strictEqual(push.component, 'qdl_episodes');
  assert.strictEqual(push.qdl_hash, HASH);
  assert.strictEqual(push.qdl_autoplay, true);
  assert.ok(String(push.title).indexOf('Повелитель тайн') !== -1, 'заголовок из меты, а не имя папки');
});

test('ничего не смотрели → «Продолжить» нет, остаётся одна «Смотреть»', () => {
  const { doc } = rig(EPISODES, []);
  assert.strictEqual(doc.querySelectorAll('.qdl-continue').length, 0);
  assert.strictEqual(doc.querySelectorAll('.qdl-watch').length, 1);
});

test('фильм (один файл) → «Продолжить» не появляется даже с прогрессом', () => {
  const { doc } = rig([EPISODES[0]], [42]);
  assert.strictEqual(doc.querySelectorAll('.qdl-continue').length, 0, 'продолжать нечего — хватит «Смотреть»');
});

// 🔥 Жалоба владельца 14.08.2026: посмотрел серию — подпись всё равно ведёт на первую.
// Экран карточки создаётся ОДИН раз, а возврат из плеера/списка серий его только показывает,
// поэтому кнопка обязана пересчитываться в start().
test('возврат на карточку после просмотра → подпись переезжает на следующую серию', () => {
  const { doc, comp, lampa, calls } = rig(EPISODES, [100, 42]);
  const before = doc.querySelector('.qdl-continue');
  assert.strictEqual(before.textContent.trim(), 'Продолжить · S1 · Серия 2');

  watched(lampa, 'jut:show:s1e2', 100);   // досмотрели вторую и вышли
  comp.start();

  const after = doc.querySelector('.qdl-continue');
  assert.strictEqual(after.textContent.trim(), 'Продолжить · S1 · Серия 3', 'подпись свежая');
  assert.strictEqual(after, before, 'узел тот же — фокус пульта на месте');
  assert.strictEqual(calls.appended.length, 1, 'в коллекцию навигатора кнопка добавлена один раз');
  assert.strictEqual(doc.querySelectorAll('.qdl-continue').length, 1, 'дубля кнопки нет');
});

test('старый надкус первой серии не перетягивает подпись на себя', () => {
  const { doc } = rig(EPISODES, [12, 100]);   // 1-ю когда-то потыкали, 2-ю досмотрели
  assert.strictEqual(doc.querySelector('.qdl-continue').textContent.trim(), 'Продолжить · S1 · Серия 3');
});

test('досмотрели всё → «Продолжить» уходит с карточки при возврате', () => {
  const { doc, comp, lampa } = rig(EPISODES, [100, 42]);
  assert.ok(doc.querySelector('.qdl-continue'), 'кнопка была');

  watched(lampa, 'jut:show:s1e2', 100);
  watched(lampa, 'jut:show:s1e3', 100);
  comp.start();

  assert.strictEqual(doc.querySelectorAll('.qdl-continue').length, 0, 'продолжать больше нечего');
  assert.strictEqual(doc.querySelectorAll('.qdl-watch').length, 1, '«Смотреть» осталась');
});

test('кнопка появляется при возврате, даже если при первом открытии её не было', () => {
  const { doc, comp, lampa, calls } = rig(EPISODES, []);
  assert.strictEqual(doc.querySelectorAll('.qdl-continue').length, 0);

  watched(lampa, 'jut:show:s1e1', 100);
  comp.start();

  assert.strictEqual(doc.querySelector('.qdl-continue').textContent.trim(), 'Продолжить · S1 · Серия 2');
  assert.strictEqual(calls.appended.length, 1, 'новая кнопка зарегистрирована в навигаторе');
});

test('destroy помечает экран мёртвым — async-колбэки не трогают чужой DOM', () => {
  const { comp } = rig(EPISODES, [100, 42]);
  assert.ok(!comp.destroyed);
  comp.destroy();
  assert.strictEqual(comp.destroyed, true, 'гарды self.destroyed в create() наконец работают');
});

// Жалоба владельца (2.34): «кнопка на аниме „Смотреть“ теперь под текстом описания». Пока экран
// был мёртвым, порядок блоков никто не видел; как только он открылся (2.33), до главного действия
// на ТВ приходилось доскроливать простыню синопсиса.
test('кнопки стоят ВЫШЕ описания — до главного действия не надо доскроливать', () => {
  const { doc } = rig(EPISODES, [100, 42]);
  const bar = doc.querySelector('.qdl-card-btns');
  const descr = doc.querySelector('.qdl-card-descr');
  assert.ok(bar && descr, 'и кнопки, и описание на экране есть');
  // DOCUMENT_POSITION_FOLLOWING (4) = descr идёт ПОСЛЕ кнопок в документе
  assert.ok(bar.compareDocumentPosition(descr) & 4, 'описание обязано идти после кнопок, а не до них');
});

test('экран рисует мету загрузки: название, прогресс, постер', () => {
  const { doc } = rig(EPISODES, [100, 42]);
  assert.ok(doc.body.textContent.indexOf('Повелитель тайн') !== -1, 'название из меты');
  assert.ok(doc.body.textContent.indexOf('загружено') !== -1, 'бейдж прогресса загрузки');
  assert.ok(doc.querySelector('.qdl-poster'), 'постер (для jut — апгрейженный, из /qdl/poster)');
});
