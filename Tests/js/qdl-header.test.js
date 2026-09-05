'use strict';
// Real-DOM tests (jsdom + real jQuery) for the header notification icon added to qdl.js:
// our own bell in .head__actions with an unread count badge, opening the qdl_notifications center,
// and updateNotiBadge keeping the header + left-menu badges in sync. Requires `npm install` (jsdom).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// A header actions bar (settings only — the native notice bell is cut server-side by AppPatch)
// plus a left-menu notifications item with its badge.
function pageHtml() {
  return (
    '<div class="head"><div class="head__body"><div class="head__actions">' +
      '<div class="head__action selector open--settings">s</div>' +
    '</div></div></div>' +
    '<div class="menu"><ul>' +
      '<li class="menu__item selector qdl-noti-menu"><div class="menu__text">Уведомления' +
        '<span class="qdl-noti-badge" style="display:none">0</span></div></li>' +
    '</ul></div>'
  );
}

test('ensureHeaderNoti injects exactly one bell into .head__actions', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();

  assert.strictEqual(doc.querySelectorAll('.head__actions .qdl-noti-head').length, 1);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti coexists with a surviving native bell (tree changed, anchor missed)', () => {
  // если после смены tree якорь AppPatch не сработал и штатный колокольчик уцелел —
  // наш всё равно вставляется ровно один (прятать штатный будет CSS-фолбэк lampainit-invc)
  const html = pageHtml().replace(
    'open--settings">s</div>',
    'open--settings">s</div><div class="head__action selector notice--icon">n</div>');
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti with TWO .head__actions containers adds exactly one bell (no clone)', () => {
  // раньше append шёл в jQuery-НАБОР: при двух контейнерах узел клонировался в каждый
  const html =
    '<div class="head">' +
      '<div class="head__body"><div class="head__actions"><div class="head__action selector open--settings">s</div></div></div>' +
      '<div class="head__body"><div class="head__actions"><div class="head__action selector open--search">f</div></div></div>' +
    '</div>';
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti self-heals pre-existing duplicate bells down to one', () => {
  // дубликаты от прошлых версий/двойных вставок: гард их не только видит, но и сносит лишние
  const html =
    '<div class="head"><div class="head__actions">' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
      '<div class="head__action selector open--qdl-noti qdl-noti-head">b<span class="qdl-noti-head-badge">2</span></div>' +
    '</div></div>';
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head-badge').length, 1);
});

test('ensureHeaderNoti is idempotent (no duplicate icons)', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();
  qdl.ensureHeaderNoti();
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 1);
});

test('ensureHeaderNoti does nothing when there is no header (no crash)', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: '<div class="menu"></div>' });
  qdl.ensureHeaderNoti();
  assert.strictEqual(doc.querySelectorAll('.qdl-noti-head').length, 0);
});

test('header bell opens the qdl_notifications component on hover:enter', () => {
  const pushes = [];
  const lampa = H.makeLampa({ Activity: { push: (a) => pushes.push(a) } });
  const { w, qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  qdl.ensureHeaderNoti();

  w.$('.qdl-noti-head').trigger('hover:enter');
  assert.strictEqual(pushes.length, 1);
  assert.strictEqual(pushes[0].component, 'qdl_notifications');
  assert.strictEqual(pushes[0].title, 'Уведомления');
});

test('updateNotiBadge syncs header + menu badges', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  qdl.ensureHeaderNoti();

  qdl.updateNotiBadge(5);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '5');
  assert.notStrictEqual(doc.querySelector('.qdl-noti-head-badge').style.display, 'none');
  assert.ok(doc.querySelector('.qdl-noti-head').className.includes('active'));   // dot fallback
  assert.strictEqual(doc.querySelector('.qdl-noti-menu .qdl-noti-badge').textContent, '5');

  qdl.updateNotiBadge(0);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').style.display, 'none');
  assert.ok(!doc.querySelector('.qdl-noti-head').className.includes('active'));

  qdl.updateNotiBadge(120);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '99+');
});

test('header badge updates even when the left menu is absent', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: '<div class="head"><div class="head__actions"></div></div>' });
  qdl.ensureHeaderNoti();
  qdl.updateNotiBadge(3);
  assert.strictEqual(doc.querySelector('.qdl-noti-head-badge').textContent, '3');
});

// ── pollNotifications: гонка и агрегация тостов ──

// Lampa-мок, где Reguest.silent копит запросы, не отвечая — ответ дёргается вручную
function makePollLampa(noty) {
  const pending = [];
  const lampa = H.makeLampa({ Noty: { show: (m) => noty.push(m) } });
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, cb, err) => pending.push({ url, cb, err });
  };
  return { lampa, pending };
}

test('pollNotifications is single-flight: concurrent calls make one request', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });

  qdl.pollNotifications();
  qdl.pollNotifications();          // в полёте — должен быть проигнорирован
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 1);

  pending[0].cb({ items: [], unread: 0 });   // ответ пришёл → замок снят
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 2);
});

test('pollNotifications unlocks after a request error', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });

  qdl.pollNotifications();
  pending[0].err();                 // сеть упала → замок обязан сняться
  qdl.pollNotifications();
  assert.strictEqual(pending.length, 2);
});

test('multiple SWITCH/INFO in one poll are aggregated into a single toast', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);   // не первый опрос — тосты разрешены

  qdl.pollNotifications();
  pending[0].cb({ unread: 3, items: [
    { id: 6, kind: 'SWITCH', title: 'Сериал А', label: 'S01E05' },
    { id: 7, kind: 'INFO',   title: 'Сериал Б', label: 'S02E01' },
    { id: 8, kind: 'SWITCH', title: 'Сериал В', label: 'S03E02' },
  ] });

  assert.strictEqual(noty.length, 1);
  assert.strictEqual(noty[0], '🔔 Новых уведомлений: 3');
  assert.strictEqual(lampa.Storage.get('qdl_noti_lastid'), 8);
});

test('single SWITCH keeps the detailed toast; downloaded episodes keep their own aggregate', () => {
  const noty = [];
  const { lampa, pending } = makePollLampa(noty);
  const { qdl } = H.loadQdlDom({ bodyHtml: pageHtml(), lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);

  qdl.pollNotifications();
  // ⚠️ 2.35: у «серия скачана» kind ОТСУТСТВУЕТ (сервер пишет null) — выдуманного 'DL' в базе
  // не бывает, а корзина «скачана» теперь белый список, а не ветвь по остатку.
  pending[0].cb({ unread: 3, items: [
    { id: 6, kind: 'SWITCH', title: 'Сериал А', label: 'S01E05' },
    { id: 7, kind: null, title: 'Сериал Б', label: 'S02E01' },
    { id: 8, kind: null, title: 'Сериал В', label: 'S03E02' },
  ] });

  assert.strictEqual(noty.length, 2);
  assert.ok(noty[0].indexOf('🔀 Сериал А — S01E05') === 0);
  // qdl 2.111: «скачана» к тексту сервера больше не дописывается — он приходит готовым
  assert.strictEqual(noty[1], '📺 Новых серий: 2');
});

// ─────────────────── лента уведомлений: постеры строк (qdl 2.46) ───────────────────
// Прямая регрессия на жалобу владельца 15.08.2026: «в уведомлениях от JUT.su для серий,
// которые отслеживаются но не скачаны, нету карточек». Строка ленты брала картинку строго
// как `n.hash ? '/qdl/poster?hash='+n.hash : img_broken`, а hash у jut-уведомления ПСЕВДО
// (sha1("jutsu:"+slug)) — файла img/<hash>.jpg для НЕ скачанного тайтла не бывает никогда.
// Замер на живом сервере: 4 битых строки из 50.

// Lampa.Scroll харнесса отдаёт болванки вне DOM — ленте нужен скролл на настоящем jQuery,
// иначе строки не окажутся в документе и проверять будет нечего (та же оснастка, что в
// qdl-card-screen.test.js).
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

function feed(items) {
  const { w, doc, qdl } = H.loadQdlDom({ bodyHtml: pageHtml() });
  w.Lampa.Scroll = jsdomScroll(w);
  w.Lampa.Reguest = function () {
    this.timeout = () => {}; this.clear = () => {};
    this.silent = (url, ok) => { if (String(url).indexOf('/qdl/notifications') !== -1) ok({ items, unread: 0 }); };
  };
  const comp = new qdl.ComponentNotifications({});
  comp.activity = { loader() {}, toggle() {} };
  w.$('body').append(comp.create());
  return { w, doc, qdl };
}

const ROWS = [
  // отслеживается, НЕ скачано — сервер отдал ручку jut.su по слагу (сама жалоба)
  { id: 269, kind: 'NEW', title: 'Клеватесс', label: 'jut.su · сезон 2 · серия 6 — вышла',
    slug: 'clevatess', hash: 'f'.repeat(40), posterUrl: '/qdl/jut/poster?slug=clevatess&v=2', created: '2026-08-15T12:37:27Z' },
  // скачанная торрентная серия — прежний путь по своему хешу
  { id: 265, kind: null, title: 'Великий расхититель гробниц', label: 'Серия 6',
    slug: null, hash: '6'.repeat(40), posterUrl: '/qdl/poster?hash=' + '6'.repeat(40), created: '2026-08-15T10:00:00Z' },
  // «Поиск раздач» — постера нет и быть не может, это ШТАТНО
  { id: 200, kind: 'DIAG', title: 'Поиск раздач', label: 'Кинозал не отвечает',
    slug: null, hash: '', posterUrl: null, created: '2026-08-15T09:00:00Z' },
];

test('лента: у каждой строки свой источник постера, jut идёт по слагу', () => {
  const { doc, qdl } = feed(ROWS);
  const src = [...doc.querySelectorAll('.qdl-noti-row img')].map((i) => i.getAttribute('src'));
  assert.strictEqual(src.length, 3, 'все строки отрисованы — постер ничего не скрывает');
  assert.strictEqual(src[0], '{localhost}/qdl/jut/poster?slug=clevatess&v=2');
  assert.strictEqual(src[1], '{localhost}/qdl/poster?hash=' + '6'.repeat(40));
  assert.strictEqual(src[2], qdl.PX1);
});

test('лента: img_broken.svg в экране уведомлений больше не встречается', () => {
  const { doc } = feed(ROWS);
  assert.ok(!doc.body.innerHTML.includes('img_broken'),
    'битый значок читается как поломка приложения, а «постера нет» — штатный случай');
});

test('лента: не догрузившаяся картинка падает в плитку, а не в битый значок', () => {
  const { w, doc, qdl } = feed(ROWS);
  const img = doc.querySelector('.qdl-noti-row img');
  w.$(img).trigger('error');
  assert.strictEqual(img.getAttribute('src'), qdl.PX1);
});

test('лента: клиент 2.46 против сервера 2.45 (без posterUrl) всё равно находит постер jut', () => {
  // Страховка на время выкатки: образ пересобран, а закешированный клиент ещё старый — и наоборот.
  const { doc } = feed([{ id: 1, kind: 'NEW', title: 'Клеватесс', label: 'вышла',
                          slug: 'clevatess', hash: 'f'.repeat(40), created: '2026-08-15T12:00:00Z' }]);
  assert.strictEqual(doc.querySelector('.qdl-noti-row img').getAttribute('src'),
                     '{localhost}/qdl/jut/poster?slug=clevatess');
});

test('лента: в исходнике экрана уведомлений нет ни одной ссылки на img_broken', () => {
  // Структурная защита: DOM-тесты проверяют текущие фикстуры, а этот — сам код экрана.
  // Любая новая ветка рендера, вернувшая рваную заглушку, уронит тест сразу.
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentNotifications');
  const j = src.indexOf('function buildNotiMenuItem');
  assert.ok(i > 0 && j > i, 'границы компонента не найдены — тест устарел, поправь якоря');
  assert.ok(!src.slice(i, j).includes('img_broken'),
    '«постера нет» в ленте — штатный случай, рисуется PX1');
});
