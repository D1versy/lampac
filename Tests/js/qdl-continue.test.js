'use strict';
// Тесты «Продолжить просмотр»: стабильность ключа эпизода (mkv→mp4), выбор серии
// chooseContinue, отметки/автофокус в пикере серий, timeline в плеере/плейлисте,
// короткое имя серии для кнопки (epShort).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl();

// ─────────────────────────────── ключ эпизода ───────────────────────────────

test('epTimelineHash: стабилен при смене расширения (транскод mkv→mp4)', () => {
  const h = 'a'.repeat(40);
  assert.strictEqual(
    qdl.epTimelineHash(h, 'Season 1/Ep.S01E02.1080p.mkv'),
    qdl.epTimelineHash(h, 'Ep.S01E02.1080p.mp4'));   // и без папки — baseName
});

test('epTimelineHash: разные серии → разные ключи, разные раздачи → разные ключи', () => {
  const h = 'a'.repeat(40);
  assert.notStrictEqual(qdl.epTimelineHash(h, 'Ep01.mkv'), qdl.epTimelineHash(h, 'Ep02.mkv'));
  assert.notStrictEqual(qdl.epTimelineHash(h, 'Ep01.mkv'), qdl.epTimelineHash('b'.repeat(40), 'Ep01.mkv'));
});

test('stripExt: срезает только видеорасширение', () => {
  assert.strictEqual(qdl.stripExt('Ep.01.mkv'), 'Ep.01');
  assert.strictEqual(qdl.stripExt('Ep.01.mp4'), 'Ep.01');
  assert.strictEqual(qdl.stripExt('Ep.01.srt'), 'Ep.01.srt');   // не видео — не трогаем
});

// ─────────────────────────────── chooseContinue ───────────────────────────────

function vidsOf(percents) {
  return percents.map((p, i) => ({ index: i, name: 'Ep' + (i + 1) + '.mkv', _p: p }));
}
const viewOf = (f) => ({ percent: f._p });

test('chooseContinue: нет прогресса нигде → null (кнопка не показывается)', () => {
  assert.strictEqual(qdl.chooseContinue(vidsOf([0, 0, 0]), viewOf), null);
});

test('chooseContinue: серия на паузе → продолжаем её', () => {
  const v = vidsOf([100, 42, 0]);
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[1]);
});

test('chooseContinue: несколько на паузе → ПОСЛЕДНЯЯ из них', () => {
  const v = vidsOf([50, 0, 60, 0]);
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[2]);
});

test('chooseContinue: после досмотренной → следующая непросмотренная', () => {
  const v = vidsOf([95, 100, 0, 0]);
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[2]);
});

test('chooseContinue: досмотрено с «дыркой» → серия после ПОСЛЕДНЕЙ досмотренной', () => {
  const v = vidsOf([100, 0, 100, 0]);   // пропустил 2-ю, досмотрел 3-ю → продолжаем с 4-й
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[3]);
});

test('chooseContinue: всё досмотрено → null', () => {
  assert.strictEqual(qdl.chooseContinue(vidsOf([100, 95, 91]), viewOf), null);
});

test('chooseContinue: <5% не считается прогрессом (случайный тык)', () => {
  assert.strictEqual(qdl.chooseContinue(vidsOf([3, 0, 0]), viewOf), null);
});

// ─────────────────────────────── epShort ───────────────────────────────

test('epShort: SxxExx, «серия N», голый номер, длинный фолбэк', () => {
  assert.strictEqual(qdl.epShort('Dune.Prophecy.S01E03.Sisterhood.mkv'), 'S1 · Серия 3');
  assert.strictEqual(qdl.epShort('01 серия_След Чикатило.mkv'), 'Серия 1');
  assert.strictEqual(qdl.epShort('Gnosia - 07 [1080p AVC].mkv'), 'Серия 7');
  const long = qdl.epShort('Very.Long.File.Name.Without.Any.Episode.Number.At.All.mkv');
  assert.ok(long.length <= 25, 'длинное имя усечено: ' + long);
});

// ─────────────────────────────── пикер серий: отметки, selected, timeline ───────────────────────────────

function fakeReqFiles(files) {
  // /qdl/episodes (новый объединённый) и /qdl/files (легаси) → список серий;
  // /qdl/audio → пусто (без пикера озвучки); прочее → пусто
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        ok(u.indexOf('/qdl/files') !== -1 || u.indexOf('/qdl/episodes') !== -1 ? files : []);
      };
    },
  };
}

test('chooseEpisode: пушит экран серий (qdl_episodes), плеер получает элемент плейлиста с timeline', () => {
  const files = [
    { index: 0, name: 'Ep01.mkv', size: 1 },
    { index: 1, name: 'Ep02.mkv', size: 1 },
    { index: 2, name: 'Ep03.mkv', size: 1 },
  ];
  let pushed = null, played = null, playlist = null;
  const lampa = H.makeLampa(Object.assign(fakeReqFiles(files), {
    Activity: { push: (o) => { pushed = o; } },
    Player: { play: (x) => { played = x; }, playlist: (p) => { playlist = p; } },
    Platform: { tv: () => false },   // браузер → HLS
  }));
  // прогресс: 1-я досмотрена, 2-я на паузе 40%
  const { qdl: q } = H.loadQdl({ lampa });
  const h = 'c'.repeat(40);
  lampa.Timeline.view(lampa.Utils.hash(h + ':Ep01')).percent = 100;
  lampa.Timeline.view(lampa.Utils.hash(h + ':Ep02')).percent = 40;

  q.chooseEpisode(h, 'Сериал');
  assert.ok(pushed, 'экран серий открыт');
  assert.strictEqual(pushed.component, 'qdl_episodes');
  assert.strictEqual(pushed.qdl_hash, h);
  assert.strictEqual(pushed.qdl_autoplay, false, 'без autoplay по умолчанию');

  const inst = new q.ComponentEpisodes(pushed);
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  assert.strictEqual(inst.vids.length, 3, 'серии загружены');

  inst.play(2);
  assert.ok(played && playlist, 'играем и плейлист передан');
  assert.strictEqual(played, playlist[2], 'играется САМ элемент плейлиста (общий инстанс timeline)');
  assert.ok(played.timeline, 'у элемента есть timeline');
  assert.strictEqual(played.timeline, lampa.Timeline.view(lampa.Utils.hash(h + ':Ep03')), 'timeline привязан к серии');
});

test('экран серий: отметки epMark и мета-строка epMeta', () => {
  const { qdl: q } = H.loadQdl();
  assert.strictEqual(q.epMark(100), '✓ ', 'досмотрена');
  assert.strictEqual(q.epMark(40), '► 40% · ', 'на паузе с процентом');
  assert.strictEqual(q.epMark(3), '', '<5% — случайный тык, без отметки');
  assert.strictEqual(q.epMeta({ size: 1073741824 }), '1 ГБ');
  assert.ok(q.epMeta({ source: 'donor' }).indexOf('временная') === 0, 'донор помечен');
  assert.strictEqual(q.epMeta({}), '', 'без данных — пусто');
});

test('watchByHash: одиночный файл играет с timeline по имени файла', () => {
  const files = [{ index: 0, name: 'Movie.2024.mkv', size: 1 }];
  let played = null;
  const lampa = H.makeLampa(Object.assign(fakeReqFiles(files), {
    Player: { play: (x) => { played = x; }, playlist: () => {} },
    Platform: { tv: () => true },   // ТВ → прямой stream
  }));
  const { qdl: q } = H.loadQdl({ lampa });
  const h = 'd'.repeat(40);
  q.watchByHash(h, 'Фильм');
  assert.ok(played, 'плеер запущен');
  assert.strictEqual(played.timeline, lampa.Timeline.view(lampa.Utils.hash(h + ':Movie.2024')), 'ключ — по базе имени файла');
});

// ─────────────────────────────── buildPlaylist ───────────────────────────────

test('buildPlaylist: у каждого элемента свой timeline и правильный URL', () => {
  const lampa = H.makeLampa({ Platform: { tv: () => false } });
  const { qdl: q } = H.loadQdl({ lampa });
  const h = 'e'.repeat(40);
  const vids = [{ index: 3, name: 'A.mkv' }, { index: 5, name: 'B.mkv' }];
  const pl = q.buildPlaylist(h, vids, null);
  assert.strictEqual(pl.length, 2);
  assert.ok(pl[0].url.indexOf('/qdl/hls/' + h + '_3/') !== -1, 'HLS-ключ по торрент-индексу');
  assert.ok(pl[0].timeline && pl[1].timeline && pl[0].timeline !== pl[1].timeline, 'timeline индивидуальны');
});
