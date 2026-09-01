'use strict';
// Гейт недокачанных серий на экране qdl_episodes (qdl 2.93).
//
// Требование владельца: «если это сериал, то только серии которые загружены можно смотреть»,
// и решение по виду — «показывать заблокированными», а не прятать (иначе сериал выглядит короче,
// чем он есть, и непонятно, качается ли ещё что-то).
//
// 🔴 Самая дорогая мина здесь — СМЕЩЕНИЕ ИНДЕКСОВ. `i` в comp.play(i) это индекс в comp.vids,
// а плейлист отфильтрован; без карты qdlMap «серия 3» играла бы четвёртую. На плоском
// playlist[i] соответствующий тест обязан краснеть.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HASH = 'h'.repeat(40);

// Ep01 готова, Ep02 качается, Ep03 готова — «дыра» посередине и ловит смещение индексов
const FILES = [
  { index: 0, name: 'Ep01.mkv', size: 1073741824, progress: 1 },
  { index: 1, name: 'Ep02.mkv', size: 1073741824, progress: 0.62 },
  { index: 2, name: 'Ep03.mkv', size: 1073741824, progress: 1 },
];

function reqMock(files, calls) {
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        calls.reqs.push(u);
        if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
        else if (u.indexOf('/qdl/progress') !== -1) { /* тик поллера — ответ даём вручную */ }
        else ok([]);
      };
    },
  };
}

function domScroll(w) {
  return function () {
    const root = w.document.createElement('div');
    const bodyEl = w.document.createElement('div');
    root.appendChild(bodyEl);
    this.render = () => w.$(root);
    this.body = () => w.$(bodyEl);
    this.append = (x) => { w.$(bodyEl).append(x); };
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

function mount(opts) {
  opts = opts || {};
  const calls = { plays: [], playlists: [], selects: [], noty: [], reqs: [], ticks: [], cleared: [] };
  const lampa = H.makeLampa(Object.assign(reqMock(opts.files || FILES, calls), {
    Player: { play: (x) => calls.plays.push(x), playlist: (p) => calls.playlists.push(p), opened: () => false },
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Platform: { tv: () => true },
  }));
  const r = H.loadQdlDom({
    lampa,
    setInterval: (fn, ms) => { calls.ticks.push({ fn, ms }); return calls.ticks.length; },
    clearInterval: (id) => calls.cleared.push(id),
  });
  r.lampa.Scroll = domScroll(r.w);
  r.qdl.pgReset();
  if (opts.before) opts.before(r.qdl, r);
  const inst = new r.qdl.ComponentEpisodes(Object.assign({ qdl_hash: HASH, qdl_name: 'Сериал' }, opts.obj || {}));
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  return { r, inst, calls, root: inst.render() };
}

const rows = (m) => m.root.find('.qdl-row-focus');

// ─────────────────────────── как выглядит запертая строка ───────────────────────────

test('недокачанная строка приглушена и несёт процент; докачанная — чистая', () => {
  const m = mount();
  const rs = rows(m);
  assert.strictEqual(rs.length, 3, 'строки НЕ прячем — решение владельца');

  assert.strictEqual(m.r.$(rs[0]).hasClass('qdl-ep--wait'), false);
  assert.strictEqual(m.r.$(rs[0]).find('.qdl-ep-dl').text(), '', 'готовая серия без отметки загрузки');

  assert.strictEqual(m.r.$(rs[1]).hasClass('qdl-ep--wait'), true);
  assert.ok(m.r.$(rs[1]).find('.qdl-ep-dl').text().indexOf('62%') !== -1);
  assert.ok(m.r.$(rs[1]).find('.qdl-ep-meta').text().indexOf('качается') !== -1, 'и в подстрочнике тоже');
});

// 🔴 .qdl-ep-mark — это ПРОСМОТР (epMark), .qdl-ep-dl — ЗАГРУЗКА. Два разных процента.
test('отметка загрузки не занимает узел отметки просмотра', () => {
  const m = mount({
    before: (qdl, r) => { r.lampa.Timeline.view(r.lampa.Utils.hash(HASH + ':Ep02')).percent = 40; },
  });
  const row = m.r.$(rows(m)[1]);
  assert.ok(row.find('.qdl-ep-dl').text().indexOf('62%') !== -1, 'загрузка');
  assert.ok(row.find('.qdl-ep-mark').text().indexOf('40%') !== -1, 'просмотр');
});

test('нет данных о прогрессе вовсе → строка играбельна (fail-open)', () => {
  const m = mount({ files: [{ index: 0, name: 'Ep01.mkv', size: 1 }, { index: 1, name: 'Ep02.mkv', size: 1 }] });
  assert.strictEqual(m.r.$(rows(m)[0]).hasClass('qdl-ep--wait'), false);
  m.inst.play(0);
  assert.strictEqual(m.calls.plays.length, 1);
});

// ─────────────────────────── блокировка воспроизведения ───────────────────────────

test('нажатие на качающуюся серию → тост, плеер НЕ открывается', () => {
  const m = mount();
  m.inst.play(1);
  assert.strictEqual(m.calls.plays.length, 0, 'в плеер не пускаем');
  assert.strictEqual(m.calls.noty.length, 1);
  assert.ok(m.calls.noty[0].indexOf('Дождитесь загрузки') !== -1);
  assert.ok(m.calls.noty[0].indexOf('62%') !== -1, 'говорим, сколько уже скачано');
});

test('гейт стоит ДО истории: запертая серия в «Историю просмотров» не попадает', () => {
  let added = 0;
  const m = mount({ before: (qdl, r) => { r.lampa.Favorite.add = () => { added++; }; } });
  m.inst.play(1);
  assert.strictEqual(added, 0);
});

test('клик по строке идёт через тот же гейт (дыра hover:enter закрыта)', () => {
  const m = mount();
  m.r.$(rows(m)[1]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 0);
  m.r.$(rows(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1);
});

// ─────────────────────────── 🔴 мина смещения индексов ───────────────────────────

test('плейлист только из готовых, а индекс ищется по карте — не сдвигается', () => {
  const m = mount();
  m.inst.play(2);   // Ep03 — третья в vids, но ВТОРАЯ в отфильтрованном плейлисте
  assert.strictEqual(m.calls.plays.length, 1);

  const played = m.calls.plays[0];
  assert.strictEqual(played.playlist.length, 2, 'качающаяся серия в плейлист не попала');
  assert.ok(played.title.indexOf('Ep03') !== -1, 'играем именно ту серию, на которую нажали');
  assert.ok(played.url.indexOf('index=2') !== -1);
  // и авто-переход внутри плеера тоже ходит только по готовым
  played.playlist.forEach((it) => assert.ok(it.title.indexOf('Ep02') === -1));
});

test('все серии качаются → играть нечего, тост и молчание', () => {
  const m = mount({
    files: [{ index: 0, name: 'Ep01.mkv', size: 1, progress: 0.1 }, { index: 1, name: 'Ep02.mkv', size: 1, progress: 0.2 }],
  });
  m.inst.play(0);
  assert.strictEqual(m.calls.plays.length, 0);
  assert.strictEqual(m.calls.noty.length, 1);
});

test('автоплей с «Продолжить» не садится на качающуюся серию', () => {
  const m = mount({
    obj: { qdl_autoplay: true },
    // Ep01 досмотрена → продолжать надо с Ep02, но она качается ⇒ уходим на Ep03
    before: (qdl, r) => { r.lampa.Timeline.view(r.lampa.Utils.hash(HASH + ':Ep01')).percent = 95; },
  });
  assert.strictEqual(m.calls.plays.length, 1);
  assert.ok(m.calls.plays[0].title.indexOf('Ep03') !== -1);
});

test('автоплей при всех недокачанных → тост, плеер молчит', () => {
  const m = mount({
    obj: { qdl_autoplay: true },
    files: [{ index: 0, name: 'Ep01.mkv', size: 1, progress: 0.1 }],
  });
  assert.strictEqual(m.calls.plays.length, 0);
  assert.ok(m.calls.noty.some((t) => t.indexOf('качаются') !== -1));
});

test('подсветка «текущей» не садится на запертую строку', () => {
  const m = mount({
    before: (qdl, r) => { r.lampa.Timeline.view(r.lampa.Utils.hash(HASH + ':Ep02')).percent = 40; },
  });
  // Ep02 надкушена, но качается — «текущей» должна считаться доступная серия, а не она
  assert.strictEqual(m.r.$(rows(m)[1]).hasClass('qdl-ep--cur'), false);
});

// ─────────────────────────── живой прогресс ───────────────────────────

test('живой per-file прогресс перекрывает снимок /qdl/episodes', () => {
  const m = mount();
  // сервер говорит: Ep02 докачалась (её нет в files с недобором), Ep01 наоборот отвалилась
  m.r.qdl.pgApply({
    ok: true, stamp: 'x', active: 1, pending: 0,
    items: [{ h: HASH, p: 0.9, s: 'downloading' }],
    files: { [HASH]: [[0, 0.5], [1, 1], [2, 1]] },
  });
  m.inst.refreshDownload();

  assert.strictEqual(m.r.$(rows(m)[1]).hasClass('qdl-ep--wait'), false, 'докачалась — разблокировали');
  assert.strictEqual(m.r.$(rows(m)[0]).hasClass('qdl-ep--wait'), true, 'а эта, наоборот, ещё нет');

  m.inst.play(1);
  assert.strictEqual(m.calls.plays.length, 1, 'теперь играется');
});

test('живое обновление НЕ пересобирает строки — фокус пульта остаётся', () => {
  const m = mount();
  const before = rows(m)[1];
  m.r.qdl.pgApply({
    ok: true, stamp: 'x', active: 1, pending: 0,
    items: [{ h: HASH, p: 0.9, s: 'downloading' }],
    files: { [HASH]: [[0, 1], [1, 0.8], [2, 1]] },
  });
  m.inst.refreshDownload();
  assert.strictEqual(rows(m)[1], before, 'тот же DOM-узел');
  assert.ok(m.r.$(rows(m)[1]).find('.qdl-ep-dl').text().indexOf('80%') !== -1, 'а текст обновился');
});

test('серия донора читается по СВОЕМУ хешу, а не по хешу карточки', () => {
  const DONOR = 'd'.repeat(40);
  const m = mount({
    files: [
      { index: 0, name: 'Ep01.mkv', size: 1, progress: 1 },
      { index: 5, name: 'Ep02.mkv', size: 1, progress: 0, source: 'donor', hash: DONOR },
    ],
  });
  m.r.qdl.pgApply({
    ok: true, stamp: 'x', active: 1, pending: 0,
    items: [{ h: DONOR, p: 0.5, s: 'downloading' }],
    files: { [DONOR]: [[5, 1]] },   // у ДОНОРА серия готова
  });
  m.inst.refreshDownload();
  assert.strictEqual(m.r.$(rows(m)[1]).hasClass('qdl-ep--wait'), false);
  m.inst.play(1);
  assert.strictEqual(m.calls.plays.length, 1);
});

// ─────────────────────────── подписка и мина «нет destroy вперёд» ───────────────────────────

test('start подписывает, pause отписывает: сложенные копии экрана не множат опрос', () => {
  const m = mount();
  m.inst.start();
  const subs1 = Object.keys(m.r.qdl.pgState().subs).length;
  assert.strictEqual(subs1, 1);

  m.inst.pause();
  assert.strictEqual(Object.keys(m.r.qdl.pgState().subs).length, 0, 'уход вперёд снимает подписку');

  m.inst.start();
  assert.strictEqual(Object.keys(m.r.qdl.pgState().subs).length, 1, 'возврат — снова одна, не две');

  m.inst.destroy();
  assert.strictEqual(Object.keys(m.r.qdl.pgState().subs).length, 0, 'destroy идемпотентен');
});
