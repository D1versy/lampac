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

// 🔥 Жалоба владельца (14.08.2026): «посмотрел серию с jut.su, отметилась, а „Продолжить“
// ведёт на первую». Причина — старый надкус 1-й серии (открыл на пару минут месяц назад)
// выигрывал у свежего досмотра, потому что «последняя на паузе» искалась по ВСЕМУ списку.
// Правило: серия на паузе ЛЕВЕЕ последней досмотренной — это брошенный хвост, а не «продолжить».
test('chooseContinue: старый надкус ДО последней досмотренной не перебивает её (баг 14.08.2026)', () => {
  const v = vidsOf([12, 0, 0, 0, 100, 0, 0]);
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[5], 'ждём серию после досмотренной, а не первую');
});

test('chooseContinue: пауза ПОСЛЕ последней досмотренной по-прежнему выигрывает', () => {
  const v = vidsOf([12, 0, 100, 40, 0]);
  assert.strictEqual(qdl.chooseContinue(v, viewOf), v[3]);
});

// Порядок массива не должен решать: локальная (jut) ветка /qdl/episodes отдавала файлы
// в лексикографическом порядке по пути — s1e100 между s1e10 и s1e11, film/ova в начале.
test('chooseContinue: порядок массива не важен — считаем по номерам серий', () => {
  const mk = (epkey, p) => ({ name: 'show.' + epkey + '.mp4', epkey: epkey, _p: p });
  const shuffled = [mk('s1e10', 0), mk('s1e2', 100), mk('s1e1', 100), mk('s1e11', 0), mk('s1e3', 0)];
  const cur = qdl.chooseContinue(shuffled, viewOf);
  assert.strictEqual(cur.epkey, 's1e3', 'после s1e2 идёт s1e3, хотя в массиве он последний');
});

test('chooseContinue: экстры (film/ova) не предлагаются как «следующая серия»', () => {
  const mk = (epkey, p) => ({ name: 'show.' + epkey + '.mp4', epkey: epkey, _p: p });
  const v = [mk('film1', 0), mk('s1e1', 100), mk('s1e2', 100)];
  assert.strictEqual(qdl.chooseContinue(v, viewOf), null, 'сезон досмотрен — фильм сам себя не предлагает');
});

test('chooseContinue: начатая экстра всё-таки продолжается', () => {
  const mk = (epkey, p) => ({ name: 'show.' + epkey + '.mp4', epkey: epkey, _p: p });
  const v = [mk('ova1', 45), mk('s1e1', 100), mk('s1e2', 100)];
  assert.strictEqual(qdl.chooseContinue(v, viewOf).epkey, 'ova1');
});

test('chooseContinue: следующий сезон, а не следующий индекс', () => {
  const mk = (s, e, p) => ({ name: 'show.s' + s + 'e' + e + '.mp4', epkey: 's' + s + 'e' + e, season: s, _p: p });
  const v = [mk(2, 1, 0), mk(1, 1, 100), mk(1, 2, 100)];
  assert.strictEqual(qdl.chooseContinue(v, viewOf).epkey, 's2e1');
});

test('sortEpisodes: не мутирует вход и возвращает ТЕ ЖЕ объекты (indexOf/=== в вызывающих)', () => {
  const v = vidsOf([0, 0, 0]);
  const copy = v.slice();
  const sorted = qdl.sortEpisodes(v);
  assert.deepStrictEqual(v, copy, 'исходный массив на месте');
  assert.notStrictEqual(sorted, v, 'вернулась копия');
  sorted.forEach((f) => assert.ok(v.indexOf(f) !== -1, 'элементы — те же ссылки'));
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
  // 2.62: играется ОТДЕЛЬНЫЙ объект (несёт playlist для нативов, цикла нет),
  // но timeline — общий инстанс с элементом плейлиста: прогресс пишется в одно место
  assert.strictEqual(played.url, playlist[2].url, 'играется 3-й элемент (тот же url)');
  assert.notStrictEqual(played, playlist[2], 'отдельным объектом');
  assert.strictEqual(played.playlist, playlist, 'плейлист лежит на объекте — для нативов');
  assert.ok(played.timeline, 'у элемента есть timeline');
  assert.strictEqual(played.timeline, playlist[2].timeline, 'timeline — общий инстанс');
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
