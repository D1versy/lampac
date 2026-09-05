'use strict';
// Долгое нажатие на карточке КАТАЛОГА → наше меню (qdl 2.108).
//
// Жалоба владельца: на приложениях всплывает гайд «Удерживайте кнопку (ОК) для вызова меню»,
// а само удержание открывает штатное меню закладок Lampa. Просьба: гайд убрать, удержание —
// на НАШЕ меню (следить за сериями / ждать сезон), штатное убрать совсем.
//
// Механика: бандл (AppPatch card-menu / card-menu-legacy) перед сборкой штатного меню шлёт
// событие Lampa.Listener 'qdl_card' {type:'menu', data, params, enabled, handled}; onCardMenu
// берёт его на себя (handled=true) и открывает quickMenu «Загрузок» у скачанного тайтла или
// меню «Скачать» у нескачанного.
//
// 🔒 Что здесь заперто:
//   • скачанный сериал → quickMenu с пунктами «следить» и «ждать сезон»; нескачанный → «Скачать»;
//   • тип карточки выводится как у самой Lampa (name → tv), а НЕ по slimCard: у карточки из ряда
//     после конструктора есть и title, и release_date; фильм с тем же TMDB id — не совпадение;
//   • объект карточки НЕ мутируется (это тот же объект, что лежит в клиентском кеше Request);
//   • «Клубничка» (params.card_collection) и персоны — handled=false, штатное меню остаётся;
//   • уже открытый селектбокс (второй hover:long от правой кнопки) — проглатывается;
//   • зазор: пока шёл /qdl/list, активность сменилась — меню не показывается;
//   • управление возвращается контроллеру из события (items_line), и по «назад», и по выбору
//     (onBeforeClose — штатный Select после выбора контроллер сам не восстанавливает);
//   • с каталога удаление НЕ делает Activity.replace() (экран не наш), из «Загрузок» — делает;
//   • регистрация подписки в start() на месте.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HA = 'a'.repeat(40);
const TV_DL = { hash: HA, name: 'Телохранители / Сезон: 1', progress: 1, state: 'queuedUP', meta: { id: 229564, media_type: 'tv', title: 'Телохранители' } };
const MOVIE_SAME_ID = { hash: 'b'.repeat(40), name: 'Movie.mkv', progress: 1, meta: { id: 229564, media_type: 'movie', title: 'Фильм с тем же id' } };

// карточка сериала из ряда TMDB: после конструктора карточки Lampa у неё есть и title, и release_date
const tvCard = () => ({ id: 229564, name: 'Телохранители', title: 'Телохранители', original_name: 'Bodyguard', first_air_date: '2018-08-26', release_date: '2018-08-26', source: 'tmdb' });
const movieCard = () => ({ id: 27205, title: 'Начало', original_title: 'Inception', release_date: '2010-07-15', source: 'tmdb' });
const person = () => ({ id: 6193, name: 'Leonardo DiCaprio', title: 'Leonardo DiCaprio', gender: 2, profile_path: '/x.jpg', known_for_department: 'Acting' });

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], toggles: [], pushes: [], replaces: 0 };
  let active = { component: 'main' };
  const doc = H.makeDocument();
  doc.body.classList = { contains: (c) => !!opts.selectOpen && c === 'selectbox--open' };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push: (o) => calls.pushes.push(o), replace() { calls.replaces++; }, active: () => active },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url), { ok, err });
        if (h === 'ERR') { if (err) err(); return; }
        if (h === 'HOLD') return;   // ответ придёт позже — тест дёрнет ok сам
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa, document: doc });
  return { qdl, calls, setActive: (a) => { active = a; } };
}

const withList = (list) => (url) => {
  if (url.indexOf('/qdl/list') >= 0) return list;
  if (url.indexOf('/qdl/delete') >= 0) return {};
  return undefined;
};
const last = (a) => a[a.length - 1];
const acts = (sel) => (sel.items || []).map((i) => i.act);
const ev = (data, over) => Object.assign({ type: 'menu', data, enabled: 'items_line', handled: false }, over || {});

// ─────────────────────────────── чистые хелперы ───────────────────────────────

test('cardType: как сама Lampa при открытии карточки — name → tv, иначе movie; явный media_type сильнее', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.cardType(tvCard()), 'tv', 'у карточки из ряда есть и title, и release_date — решает name');
  assert.strictEqual(qdl.cardType(movieCard()), 'movie');
  assert.strictEqual(qdl.cardType({ id: 1, name: 'x', media_type: 'movie' }), 'movie');
  assert.strictEqual(qdl.cardType({ id: 1, title: 'x', media_type: 'tv' }), 'tv');
  assert.strictEqual(qdl.cardType(null), 'movie');
});

test('cardIsTitle: TMDB-тайтл — да; без id, клубничка (card_collection) и персона — нет', () => {
  const { qdl } = rig();
  assert.ok(qdl.cardIsTitle(tvCard()));
  assert.ok(qdl.cardIsTitle(movieCard()));
  assert.ok(!qdl.cardIsTitle({ name: 'Video', title: 'Video', video: 'x' }), 'у клубнички нет id');
  assert.ok(!qdl.cardIsTitle({ id: 5, name: 'Video', title: 'Video' }, { card_collection: true }), 'клубничка помечена params.card_collection');
  assert.ok(!qdl.cardIsTitle(person()), 'персона: gender/profile_path');
  assert.ok(!qdl.cardIsTitle({ id: 7 }), 'без названия');
  assert.ok(!qdl.cardIsTitle(null));
});

// ─────────────────────────────── onCardMenu ───────────────────────────────

test('чужой тип события — не наше, handled остаётся false и ни одного Select', () => {
  const { qdl, calls } = rig({ respond: withList([TV_DL]) });
  const e = ev(tvCard(), { type: 'focus' });
  qdl.onCardMenu(e);
  assert.strictEqual(e.handled, false);
  assert.strictEqual(calls.selects.length, 0);
  assert.strictEqual(calls.reqs.length, 0);
});

test('скачанный сериал → quickMenu «Загрузок»: есть «следить» и «ждать сезон», handled=true', () => {
  const { qdl, calls } = rig({ respond: withList([TV_DL]) });
  const e = ev(tvCard());
  qdl.onCardMenu(e);
  assert.strictEqual(e.handled, true);
  assert.strictEqual(calls.selects.length, 1);
  const m = last(calls.selects);
  assert.strictEqual(m.title, 'Телохранители');
  assert.ok(acts(m).includes('watch'), 'пункт «Следить за новыми сериями»');
  assert.ok(acts(m).includes('seasonwait'), 'пункт «Ждать следующий сезон»');
  assert.ok(!acts(m).includes('download'), 'у скачанного нет «Скачать»');
});

test('фильм с тем же TMDB id — НЕ совпадение (тип из name), карточка не мутируется', () => {
  const { qdl, calls } = rig({ respond: withList([MOVIE_SAME_ID]) });
  const card = tvCard();
  const e = ev(card);
  qdl.onCardMenu(e);
  assert.strictEqual(e.handled, true);
  const m = last(calls.selects);
  assert.strictEqual(acts(m).join(','), 'download,page', 'нескачанный → меню «Скачать»');
  assert.ok(!('media_type' in card), 'объект карточки — тот же, что в кеше Request: media_type не дописываем');
  assert.deepStrictEqual(Object.keys(card).sort(), Object.keys(tvCard()).sort(), 'ни одного нового поля');
});

test('нескачанный фильм → «Скачать» уходит в /qdl/search с типом фильма, «Открыть карточку» → Activity.push', () => {
  const { qdl, calls } = rig({ respond: withList([]) });
  const card = movieCard();
  qdl.onCardMenu(ev(card));
  const m = last(calls.selects);
  assert.strictEqual(acts(m).join(','), 'download,page');

  m.onSelect(m.items[0]);
  const search = calls.reqs.filter((u) => u.indexOf('/qdl/search') >= 0)[0];
  assert.ok(search, 'поиск раздач запущен');
  assert.ok(search.indexOf('is_serial=1') >= 0, 'фильм → is_serial=1');
  assert.ok(search.indexOf('tmdb_id=27205') >= 0, 'TMDB id едет в поиск');

  m.onSelect(m.items[1]);
  const p = last(calls.pushes);
  assert.strictEqual(p.component, 'full');
  assert.strictEqual(p.method, 'movie');
  assert.strictEqual(p.id, 27205);
  assert.strictEqual(p.card, card, 'в карточку уходит тот же объект, как у самой Lampa');
});

test('нескачанный сериал: «Скачать» ищет как сериал, «Открыть карточку» — method tv', () => {
  const { qdl, calls } = rig({ respond: withList([]) });
  qdl.onCardMenu(ev(tvCard()));
  const m = last(calls.selects);
  m.onSelect(m.items[0]);
  const search = calls.reqs.filter((u) => u.indexOf('/qdl/search') >= 0)[0];
  assert.ok(search.indexOf('is_serial=2') >= 0, 'сериал → is_serial=2');
  m.onSelect(m.items[1]);
  assert.strictEqual(last(calls.pushes).method, 'tv');
});

test('/qdl/list не ответил → меню «Скачать», а не тишина', () => {
  const { qdl, calls } = rig({ respond: (url) => (url.indexOf('/qdl/list') >= 0 ? 'ERR' : undefined) });
  const e = ev(tvCard());
  qdl.onCardMenu(e);
  assert.strictEqual(e.handled, true);
  assert.strictEqual(acts(last(calls.selects)).join(','), 'download,page');
});

test('клубничка и персона — не берём: handled=false, штатное меню Lampa остаётся', () => {
  const { qdl, calls } = rig({ respond: withList([TV_DL]) });
  const sisi = ev({ name: 'Video', title: 'Video', video: 'x', picture: 'y' }, { params: { card_collection: true } });
  qdl.onCardMenu(sisi);
  assert.strictEqual(sisi.handled, false);
  const actor = ev(person());
  qdl.onCardMenu(actor);
  assert.strictEqual(actor.handled, false);
  assert.strictEqual(calls.selects.length, 0);
  assert.strictEqual(calls.reqs.length, 0, 'без единого запроса');
});

test('селектбокс уже открыт (второе hover:long от правой кнопки) — проглатываем без меню', () => {
  const a = rig({ respond: withList([TV_DL]) });
  const e1 = ev(tvCard(), { enabled: 'select' });
  a.qdl.onCardMenu(e1);
  assert.strictEqual(e1.handled, true, 'штатное меню тоже не строится');
  assert.strictEqual(a.calls.selects.length, 0);
  assert.strictEqual(a.calls.reqs.length, 0);

  const b = rig({ respond: withList([TV_DL]), selectOpen: true });
  const e2 = ev(tvCard());
  b.qdl.onCardMenu(e2);
  assert.strictEqual(e2.handled, true);
  assert.strictEqual(b.calls.selects.length, 0);
});

test('зазор: пока шёл /qdl/list, активность сменилась (Enter открыл карточку) — меню не показываем', () => {
  let held = null;
  const { qdl, calls, setActive } = rig({ respond: (url, h) => { if (url.indexOf('/qdl/list') >= 0) { held = h; return 'HOLD'; } return undefined; } });
  const e = ev(tvCard());
  qdl.onCardMenu(e);
  assert.strictEqual(e.handled, true);
  assert.ok(held, 'запрос ушёл');
  setActive({ component: 'full' });
  held.ok([TV_DL]);
  assert.strictEqual(calls.selects.length, 0, 'ответ пришёл на чужой экран — молчим');
});

test('управление возвращается контроллеру из события: и по «назад», и по выбору (onBeforeClose)', () => {
  const { qdl, calls } = rig({ respond: withList([]) });
  qdl.onCardMenu(ev(movieCard(), { enabled: 'items_line' }));
  const m = last(calls.selects);
  m.onBack();
  assert.deepStrictEqual(calls.toggles, ['items_line'], 'на главной/в поиске это items_line, а не content');
  assert.strictEqual(typeof m.onBeforeClose, 'function', 'штатный Select после выбора контроллер не восстанавливает');
  assert.strictEqual(m.onBeforeClose(), true, 'onBeforeClose обязан вернуть true — иначе Select не закроется');
  assert.deepStrictEqual(calls.toggles, ['items_line', 'items_line']);
  assert.ok(!calls.toggles.includes('content'));
});

test('без enabled в событии — content, как было', () => {
  const { qdl, calls } = rig({ respond: withList([]) });
  qdl.onCardMenu(ev(movieCard(), { enabled: undefined }));
  last(calls.selects).onBack();
  assert.deepStrictEqual(calls.toggles, ['content']);
});

test('quickMenu с каталога: выбор «ждать сезон» не оставляет ни одного toggle(content)', () => {
  const { qdl, calls } = rig({ respond: (url) => (url.indexOf('/qdl/list') >= 0 ? [TV_DL] : url.indexOf('/qdl/season/watch') >= 0 ? { success: true, from: 3 } : undefined) });
  qdl.onCardMenu(ev(tvCard(), { enabled: 'items_line' }));
  const m = last(calls.selects);
  assert.strictEqual(m.onBeforeClose(), true);
  m.onSelect(m.items.filter((i) => i.act === 'seasonwait')[0]);
  assert.ok(calls.reqs.some((u) => u.indexOf('/qdl/season/watch?hash=' + HA) >= 0), 'ожидание сезона включено');
  assert.ok(calls.toggles.length > 0 && calls.toggles.every((t) => t === 'items_line'), 'только items_line: ' + calls.toggles.join(','));
});

test('удаление с каталога: подтверждение и /qdl/delete, но БЕЗ Activity.replace() — экран не наш', () => {
  const { qdl, calls } = rig({ respond: withList([TV_DL]) });
  qdl.onCardMenu(ev(tvCard(), { enabled: 'items_line' }));
  const m = last(calls.selects);
  const del = m.items.filter((i) => i.act === 'del')[0];
  assert.ok(del, 'право «действия» есть по умолчанию теста → пункт удаления виден');
  m.onSelect(del);
  const confirm = last(calls.selects);
  assert.ok(/Удалить/.test(confirm.title));
  confirm.onSelect(confirm.items[0]);
  assert.ok(calls.reqs.some((u) => u.indexOf('/qdl/delete?hash=' + HA) >= 0));
  assert.strictEqual(calls.replaces, 0, 'с карточки каталога чужой экран не перерисовываем');
  // «Отмена» подтверждения — тоже к items_line
  confirm.onSelect(confirm.items[1]);
  assert.ok(calls.toggles.includes('items_line') && !calls.toggles.includes('content'));
});

test('то же удаление из «Загрузок» (без ctx) — Activity.replace() как раньше, контроллер content', () => {
  const { qdl, calls } = rig({ respond: withList([TV_DL]) });
  qdl.quickMenu(TV_DL);
  const m = last(calls.selects);
  m.onSelect(m.items.filter((i) => i.act === 'del')[0]);
  const confirm = last(calls.selects);
  confirm.onSelect(confirm.items[0]);
  assert.strictEqual(calls.replaces, 1);
  m.onBack();
  assert.strictEqual(last(calls.toggles), 'content');
});

test('start(): подписка на qdl_card зарегистрирована рядом с full', () => {
  const src = H.qdlSource();
  assert.ok(/Lampa\.Listener\.follow\('qdl_card', onCardMenu\)/.test(src), 'без подписки событие бандла никто не берёт — штатное меню вернётся');
  assert.ok(/Lampa\.Listener\.follow\('full', addButton\)/.test(src));
});
