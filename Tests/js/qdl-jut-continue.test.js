'use strict';
// «Продолжить» на онлайн-экранах jut.su (qdl 2.42).
//
// До 2.42 карточка тайтла таймлайн не читала вовсе: зелёная «Смотреть» всегда запускала первую
// серию, а экран серий считал «текущую» СВОЕЙ эвристикой («первая на паузе сверху»), отличной
// от «Загрузок». Плюс ни один из двух экранов не выставлял ведро серверных таймкодов — записи
// онлайн-просмотров уезжали в ведро последней открытой TMDB-карточки (в боевой базе так и
// лежало: 1275779_movie), и прочитать их оттуда было некому.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const SLUG = 'liar-game';
const ITEMS = [
  { kind: 'episode', season: 1, ep: 1, key: 's1e1', tok: 'v1.t1.s1' },
  { kind: 'episode', season: 1, ep: 2, key: 's1e2', tok: 'v1.t2.s2' },
  { kind: 'episode', season: 1, ep: 3, key: 's1e3', tok: 'v1.t3.s3' },
  { kind: 'film', season: 1, ep: 1, key: 'film1', tok: 'v1.tf.sf' },
];

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

// percents: { 's1e1': 100, ... } — прогресс по ключу серии
function rig(percents, extra) {
  const calls = { pushes: [], played: [], playlists: [], buckets: [] };
  const { w, doc, qdl, lampa } = H.loadQdlDom({});
  w.Lampa.Scroll = jsdomScroll(w);
  w.Lampa.Activity.push = (a) => calls.pushes.push(a);
  w.Lampa.Player.play = (x) => calls.played.push(x);
  w.Lampa.Player.playlist = (p) => calls.playlists.push(p);
  w.Lampa.Listener.send = (name, e) => {
    if (name === 'lampac' && e && e.type === 'timecode_pullFromServer') calls.buckets.push(w.qdl_timecode_card);
  };
  w.Lampa.Reguest = function () {
    this.timeout = () => {}; this.clear = () => {};
    this.silent = (url, ok) => {
      const u = String(url);
      if (u.indexOf('/qdl/jut/title') !== -1) ok(Object.assign({ ok: true, title: 'Игра лжецов', items: ITEMS }, extra || {}));
      else if (u.indexOf('/qdl/jut/watch/list') !== -1) ok({ ok: true, items: [] });
      else if (u.indexOf('/qdl/jut/resolve') !== -1) ok({ ok: true, url: '/qdl/jut/stream?t=v1.cur.sig' });
      else ok({});
    };
  };
  Object.keys(percents || {}).forEach((k) => {
    lampa.Timeline._store['hqdltl:jut:' + SLUG + ':' + k] = { percent: percents[k], time: 0, duration: 0, handler() {} };
  });
  return { w, doc, qdl, lampa, calls };
}

function makeTitle(env) {
  const comp = new env.qdl.ComponentJutTitle({ jut_slug: SLUG });
  comp.activity = { loader() {}, toggle() {} };
  env.w.$('body').append(comp.create());
  return comp;
}

function btnTexts(doc) {
  return [...doc.querySelectorAll('.qdl-jut-page .selector, .scroll__body .selector')]
    .map((b) => b.textContent.trim()).filter(Boolean);
}

test('карточка тайтла: есть «Продолжить» на следующей серии, «Смотреть» остаётся «с начала»', () => {
  const env = rig({ s1e1: 100, s1e2: 100 });
  makeTitle(env);
  const texts = btnTexts(env.doc);
  assert.ok(texts.some((t) => t.indexOf('Продолжить · 3 серия') === 0), 'подпись ведёт на 3-ю: ' + texts.join(' | '));
  assert.ok(texts.indexOf('Смотреть') !== -1, 'зелёная «Смотреть» на месте');

  // «Смотреть» по-прежнему играет ПЕРВУЮ серию — это отдельное действие, а не «продолжить»
  const watch = [...env.doc.querySelectorAll('.selector')].filter((b) => b.textContent.trim() === 'Смотреть')[0];
  env.w.$(watch).trigger('hover:enter');
  assert.strictEqual(env.calls.played.length, 1);
  assert.ok(String(env.calls.played[0].title).indexOf('1 серия') !== -1, 'первая серия: ' + env.calls.played[0].title);
});

test('карточка тайтла: без прогресса «Продолжить» не показывается', () => {
  const env = rig({});
  makeTitle(env);
  assert.ok(btnTexts(env.doc).every((t) => t.indexOf('Продолжить') !== 0), 'продолжать нечего');
});

test('«Продолжить» ведёт на экран серий с автоплеем, а не сразу в плеер', () => {
  const env = rig({ s1e1: 100 });
  makeTitle(env);
  const b = env.doc.querySelector('.qdl-jut-continue');
  assert.ok(b, 'кнопка есть');
  env.w.$(b).trigger('hover:enter');
  const push = env.calls.pushes[env.calls.pushes.length - 1];
  assert.strictEqual(push.component, 'jut_episodes');
  assert.strictEqual(push.jut_slug, SLUG);
  assert.strictEqual(push.jut_autoplay, true);
  assert.strictEqual(env.calls.played.length, 0, 'плеер не запускается с карточки напрямую');
});

test('старый надкус первой серии не перетягивает «Продолжить» на себя (баг владельца, онлайн)', () => {
  const env = rig({ s1e1: 12, s1e2: 100 });
  makeTitle(env);
  const texts = btnTexts(env.doc);
  assert.ok(texts.some((t) => t.indexOf('Продолжить · 3 серия') === 0), 'после досмотренной 2-й: ' + texts.join(' | '));
});

test('экран тайтла ставит ведро таймкодов qdl_jut:<slug> и снимает своё при уходе', () => {
  const env = rig({ s1e1: 40 });
  const comp = makeTitle(env);
  comp.start();
  assert.strictEqual(env.w.qdl_timecode_card, 'qdl_jut:' + SLUG);
  assert.ok(env.calls.buckets.indexOf('qdl_jut:' + SLUG) !== -1, 'pull с сервера запрошен уже с нашим ведром');
  comp.destroy();
  assert.strictEqual(env.w.qdl_timecode_card, undefined, 'своё ведро снято');
});

test('экран серий: подсветка «текущей» совпадает с выбором «Продолжить», ведро то же', () => {
  const env = rig({ s1e1: 12, s1e2: 100 });
  const comp = new env.qdl.ComponentJutEpisodes({ jut_slug: SLUG, jut_data: { ok: true, title: 'Игра лжецов', items: ITEMS } });
  comp.activity = { loader() {}, toggle() {} };
  env.w.$('body').append(comp.create());
  comp.start();

  assert.strictEqual(env.w.qdl_timecode_card, 'qdl_jut:' + SLUG);
  const cur = [...env.doc.querySelectorAll('.qdl-ep--cur')];
  assert.strictEqual(cur.length, 1, 'ровно одна подсвеченная строка');
  assert.ok(cur[0].textContent.indexOf('3 серия') !== -1, 'та же серия, что в кнопке: ' + cur[0].textContent);
  // ✓ у досмотренной, ►% у надкусанной
  const rows = [...env.doc.querySelectorAll('.qdl-ep')].map((r) => r.textContent.trim());
  assert.ok(rows[0].indexOf('► 12%') === 0, 'надкус показан процентом: ' + rows[0]);
  assert.ok(rows[1].indexOf('✓') === 0, 'досмотренная с галочкой: ' + rows[1]);
});

test('автоплей экрана серий играет продолжаемую серию (и только один раз)', () => {
  const env = rig({ s1e1: 100 });
  const object = { jut_slug: SLUG, jut_data: { ok: true, title: 'Игра лжецов', items: ITEMS }, jut_autoplay: true };
  const comp = new env.qdl.ComponentJutEpisodes(object);
  comp.activity = { loader() {}, toggle() {} };
  env.w.$('body').append(comp.create());
  comp.start();
  assert.strictEqual(env.calls.played.length, 1, 'сыграли');
  assert.ok(String(env.calls.played[0].title).indexOf('2 серия') !== -1, 'вторая серия: ' + env.calls.played[0].title);
  comp.start();   // возврат из плеера не должен перезапускать
  assert.strictEqual(env.calls.played.length, 1, 'автоплей одноразовый');
});

test('плейлист автоперехода: у соседних серий свой токен, битых ссылок нет', () => {
  const env = rig({});
  const comp = new env.qdl.ComponentJutEpisodes({ jut_slug: SLUG, jut_data: { ok: true, title: 'Игра лжецов', items: ITEMS } });
  comp.activity = { loader() {}, toggle() {} };
  env.w.$('body').append(comp.create());
  env.w.$(env.doc.querySelectorAll('.qdl-ep')[0]).trigger('hover:enter');

  const pl = env.calls.playlists[env.calls.playlists.length - 1];
  assert.strictEqual(pl.length, 3, 'только серии этого сезона, фильм отдельно');
  assert.ok(pl[0].url.indexOf('t=v1.cur.sig') !== -1, 'текущая — по свежему resolve');
  assert.ok(pl[1].url.indexOf('t=v1.t2.s2') !== -1, 'следующая — по своему токену: ' + pl[1].url);
  pl.forEach((x) => assert.ok(!/t=$/.test(x.url), 'пустых токенов в плейлисте нет: ' + x.url));
});

test('серия без токена (ответ старого сервера) в плейлист не попадает', () => {
  const env = rig({});
  const items = ITEMS.map((x) => (x.key === 's1e2' ? { kind: x.kind, season: x.season, ep: x.ep, key: x.key } : x));
  const comp = new env.qdl.ComponentJutEpisodes({ jut_slug: SLUG, jut_data: { ok: true, title: 'Игра лжецов', items: items } });
  comp.activity = { loader() {}, toggle() {} };
  env.w.$('body').append(comp.create());
  env.w.$(env.doc.querySelectorAll('.qdl-ep')[0]).trigger('hover:enter');

  const pl = env.calls.playlists[env.calls.playlists.length - 1];
  assert.deepStrictEqual(pl.map((x) => x.title.indexOf('1 серия') !== -1 ? 1 : x.title.indexOf('3 серия') !== -1 ? 3 : 0),
    [1, 3], 'вторая (без токена) выброшена, а не оставлена битой');
});
