'use strict';
// Автопереход серий в «Загрузках» (qdl 2.62): плейлист обязан лежать НА играемом объекте
// уже в момент Lampa.Player.play — нативные плееры (мак/винда/андроид) сериализуют data
// синхронно внутри play, а Lampa.Player.playlist() до них не доезжает вовсе (no-op на
// android-ветке). Играемый объект при этом ОТДЕЛЬНЫЙ: item.playlist на элементе самого
// массива дал бы цикл, и JSON.stringify в нативной ветке бросил бы.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HASH = 'h'.repeat(40);
const FILES = [
  { index: 0, name: 'Ep01.mkv', size: 1073741824 },
  { index: 1, name: 'Ep02.mkv', size: 1073741824 },
  { index: 2, name: 'Ep03.mkv', size: 1073741824 },
];

function reqMock(files) {
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
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
  // stringifyOk снимается ВНУТРИ play: доказывает сериализуемость ровно в момент вызова
  const calls = { plays: [], playlists: [], stringifyOk: [] };
  const lampa = H.makeLampa(Object.assign(reqMock(opts.files || FILES), {
    Player: {
      play: (x) => {
        calls.plays.push(x);
        try { JSON.stringify(x); calls.stringifyOk.push(true); }
        catch (e) { calls.stringifyOk.push(false); }
      },
      playlist: (p) => calls.playlists.push(p),
    },
    Platform: { tv: () => true },
  }));
  const r = H.loadQdlDom({ lampa });
  r.lampa.Scroll = domScroll(r.w);
  if (opts.before) opts.before(r.lampa, r);
  const obj = Object.assign({ qdl_hash: HASH, qdl_name: 'Сериал' }, opts.obj || {});
  const inst = new r.qdl.ComponentEpisodes(obj);
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  return { r, inst, obj, calls, root: inst.render() };
}

const rowsOf = (m) => m.root.find('.qdl-row-focus');

test('плейлист лежит на играемом объекте уже в момент Player.play', () => {
  const m = mount();
  m.r.$(rowsOf(m)[1]).trigger('hover:enter');

  assert.strictEqual(m.calls.plays.length, 1);
  const played = m.calls.plays[0];
  assert.ok(Array.isArray(played.playlist), 'нативам плейлист доезжает только на объекте');
  assert.strictEqual(played.playlist.length, FILES.length, 'все серии раздачи');
  assert.ok(m.calls.playlists.length === 1, 'веб-плеер получает список отдельно, как раньше');
});

test('играемый объект отдельный, цикла нет, url — та же строка', () => {
  const m = mount();
  m.r.$(rowsOf(m)[1]).trigger('hover:enter');

  const played = m.calls.plays[0];
  assert.notStrictEqual(played, played.playlist[1],
    'item.playlist на элементе самого массива дал бы цикл');
  assert.strictEqual(m.calls.stringifyOk[0], true, 'JSON.stringify в момент play не бросает');
  assert.strictEqual(played.url, played.playlist[1].url,
    'нативы ищут текущий индекс ТОЧНЫМ сравнением строк url');
  assert.strictEqual(played.title, played.playlist[1].title);
});

test('у каждого элемента плейлиста есть timeline (отчёты по правильному хешу)', () => {
  const m = mount();
  m.r.$(rowsOf(m)[0]).trigger('hover:enter');

  const played = m.calls.plays[0];
  assert.ok(played.playlist.every((x) => x.timeline && typeof x.timeline === 'object'),
    'без per-item timeline натив не пометит промежуточные серии просмотренными');
  assert.strictEqual(played.timeline, played.playlist[0].timeline,
    'timeline играемого — общий ref со «своим» элементом: прогресс пишется в одно место');
});

test('автоплей от «Продолжить» тоже несёт плейлист', () => {
  const m = mount({
    obj: { qdl_autoplay: true },
    before: (lampa) => { lampa.Timeline.view(lampa.Utils.hash(HASH + ':Ep02')).percent = 40; },
  });

  assert.strictEqual(m.calls.plays.length, 1, 'плей стартовал сам');
  const played = m.calls.plays[0];
  assert.ok(Array.isArray(played.playlist), 'путь автоплея идёт через тот же comp.play');
  assert.strictEqual(played.url, played.playlist[1].url, 'играется продолжаемая (2-я)');
  assert.strictEqual(m.calls.stringifyOk[0], true);
});
