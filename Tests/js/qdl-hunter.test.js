'use strict';
// Тесты охоты за сериями по всем раздачам (клиентская часть):
// fetchEpisodes (объединённый плейлист + фолбэк), mergedVideoFiles (порядок сервера),
// srcHash (стрим от hash донора), стабильный tl-таймлайн (epTimelineKey/pickTimeline + легаси-миграция),
// пометка «врем.» у серий доноров, SWITCH-уведомление (подтверждение переключения раздачи).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], pushes: [], toggles: [], plays: [], playlists: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push: (a) => calls.pushes.push(a), replace: () => {}, active: () => ({}) },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
    Player: { play: (i) => calls.plays.push(i), playlist: (p) => calls.playlists.push(p) },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h === '__ERR__') { if (err) err(); return; }
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl, sandbox } = H.loadQdl({ lampa });
  return { qdl, lampa, calls, sandbox };
}

const last = (a) => a[a.length - 1];

// ─────────────────────────────── fetchEpisodes ───────────────────────────────

test('fetchEpisodes: /qdl/episodes отвечает массивом → отдаём его, /qdl/files не дёргаем', () => {
  const files = [{ index: 0, name: 'a.mkv', epkey: 's1e1' }];
  const r = rig({ respond: (u) => (u.indexOf('/qdl/episodes') !== -1 ? files : undefined) });
  let got = null;
  r.qdl.fetchEpisodes('h1', (f) => { got = f; });
  assert.deepStrictEqual(got, files);
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/files') !== -1).length, 0);
});

test('fetchEpisodes: ошибка /qdl/episodes → фолбэк на /qdl/files (старый сервер)', () => {
  const filesOld = [{ index: 0, name: 'a.mkv' }];
  const r = rig({
    respond: (u) => {
      if (u.indexOf('/qdl/episodes') !== -1) return '__ERR__';
      if (u.indexOf('/qdl/files') !== -1) return filesOld;
      return undefined;
    },
  });
  let got = null;
  r.qdl.fetchEpisodes('h1', (f) => { got = f; });
  assert.deepStrictEqual(got, filesOld);
});

test('fetchEpisodes: не-массив от /qdl/episodes → фолбэк на /qdl/files', () => {
  const filesOld = [{ index: 0, name: 'a.mkv' }];
  const r = rig({
    respond: (u) => {
      if (u.indexOf('/qdl/episodes') !== -1) return { error: 'internal error' };
      if (u.indexOf('/qdl/files') !== -1) return filesOld;
      return undefined;
    },
  });
  let got = null;
  r.qdl.fetchEpisodes('h1', (f) => { got = f; });
  assert.deepStrictEqual(got, filesOld);
});

// ─────────────────────────────── mergedVideoFiles / srcHash ───────────────────────────────

test('mergedVideoFiles: при наличии epkey порядок СЕРВЕРА сохраняется (не пересортировываем)', () => {
  const { qdl } = rig();
  const files = [
    { index: 3, name: 'Zeta S01E01.mkv', epkey: 's1e1' },
    { index: 0, name: 'Alpha S01E02.mkv', epkey: 's1e2' },   // по имени встал бы первым — нельзя
    { index: 1, name: 'note.txt' },
  ];
  const vids = qdl.mergedVideoFiles(files);
  assert.deepStrictEqual(vids.map((f) => f.index), [3, 0], 'порядок сервера + отфильтрован txt');
});

test('mergedVideoFiles: без epkey — старое поведение videoFiles (natural sort)', () => {
  const { qdl } = rig();
  const files = [{ index: 1, name: 'b 10.mkv' }, { index: 0, name: 'b 2.mkv' }];
  assert.deepStrictEqual(qdl.mergedVideoFiles(files).map((f) => f.index), [0, 1]);
});

test('srcHash: у файла донора свой hash, у основной — hash карточки', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.srcHash({ hash: 'dddd', source: 'donor' }, 'mmmm'), 'dddd');
  assert.strictEqual(qdl.srcHash({ name: 'a.mkv' }, 'mmmm'), 'mmmm');
  assert.strictEqual(qdl.srcHash(null, 'mmmm'), 'mmmm');
});

// ─────────────────────────────── buildPlaylist: URL и пометка донора ───────────────────────────────

test('buildPlaylist: серия донора играет с донорского hash и помечена «врем.»', () => {
  const { qdl, lampa } = rig();
  lampa.Platform.tv = () => true;   // ТВ → прямой /qdl/stream (проще проверять URL)
  const vids = [
    { index: 0, name: 'Show S02E01.mkv', source: 'main', tl: 't1:s2e1' },
    { index: 4, name: 'Other S02E02.mkv', source: 'donor', hash: 'dddd', tl: 't1:s2e2' },
  ];
  const pl = qdl.buildPlaylist('mmmm', vids, null);
  assert.ok(pl[0].url.indexOf('hash=mmmm') !== -1);
  assert.ok(pl[1].url.indexOf('hash=dddd') !== -1, 'донор — со своего hash');
  assert.ok(pl[1].title.indexOf('врем.') !== -1, 'пометка временной серии');
  assert.strictEqual(pl[0].title.indexOf('врем.'), -1);
});

test('buildPlaylist: audio-id применяется ТОЛЬКО к своей раздаче; донор получает дефолтную дорожку', () => {
  const { qdl, lampa } = rig();
  lampa.Platform.tv = () => false;   // браузер → HLS (audio уходит в ключ)
  const vids = [
    { index: 0, name: 'Show S02E01.mkv', source: 'main', tl: 't1:s2e1' },              // hash основной
    { index: 4, name: 'Other S02E02.mkv', source: 'donor', hash: 'dddd', tl: 't1:s2e2' },
  ];
  // озвучка 'e1' выбрана для ОСНОВНОЙ раздачи (audioHash='mmmm')
  const pl = qdl.buildPlaylist('mmmm', vids, 'e1', 'mmmm');
  assert.ok(pl[0].url.indexOf('_e1') !== -1, 'основная — с выбранной дорожкой e1');
  assert.ok(pl[1].url.indexOf('_e1') === -1, 'донор НЕ получает чужую дорожку e1 (иначе не тот язык/отказ HLS)');
  assert.ok(pl[1].url.indexOf('hash') !== -1 || pl[1].url.indexOf('/qdl/hls/dddd') !== -1);
});

// ─────────────────────────────── стабильный tl-таймлайн ───────────────────────────────

test('epTimelineKey: одинаковый tl → один ключ при разных hash и именах (донор→основная, re-grab)', () => {
  const { qdl } = rig();
  const kDonor = qdl.epTimelineKey({ tl: 't125988:s2e9', name: 'Other S02E09.mkv', hash: 'dddd' }, 'mmmm');
  const kMain = qdl.epTimelineKey({ tl: 't125988:s2e9', name: 'Show S02E09.mkv' }, 'mmmm2');
  assert.strictEqual(kDonor, kMain);
  const kLegacy = qdl.epTimelineKey({ name: 'Show S02E09.mkv' }, 'mmmm');
  assert.strictEqual(kLegacy, qdl.epTimelineHash('mmmm', 'Show S02E09.mkv'), 'без tl — легаси-ключ');
});

test('pickTimeline: legacy-фолбэк при старом прогрессе; иначе новый ключ', () => {
  const { qdl, lampa } = rig();
  const f = { tl: 't1:s1e5', name: 'Show S01E05.mkv' };
  // нет никакого прогресса → новый ключ (percent 0)
  const nv = qdl.pickTimeline('mmmm', f);
  assert.strictEqual(nv.hash, qdl.epTimelineKey(f, 'mmmm'));

  // старый прогресс под легаси-ключом → возвращается ОН (миграция без потери)
  const legacyKey = qdl.epTimelineHash('mmmm', f.name);
  lampa.Timeline._store[legacyKey] = { hash: legacyKey, percent: 42, time: 100, duration: 200 };
  const picked = qdl.pickTimeline('mmmm', f);
  assert.strictEqual(picked.percent, 42);

  // новый ключ уже с прогрессом → он и побеждает
  const newKey = qdl.epTimelineKey(f, 'mmmm');
  lampa.Timeline._store[newKey] = { hash: newKey, percent: 77, time: 10, duration: 20 };
  assert.strictEqual(qdl.pickTimeline('mmmm', f).percent, 77);
});

// ─────────────────────────────── chooseEpisode: серии доноров в общем списке ───────────────────────────────

test('chooseEpisode: объединённый список с донором — суффикс «врем.», плей уходит с донорского hash', () => {
  const episodes = [
    { index: 0, name: 'Show S02E01.mkv', source: 'main', epkey: 's2e1', tl: 't1:s2e1', progress: 1 },
    { index: 4, name: 'Other S02E02.mkv', source: 'donor', hash: 'dddd', epkey: 's2e2', tl: 't1:s2e2', progress: 1 },
  ];
  const r = rig({
    respond: (u) => {
      if (u.indexOf('/qdl/episodes') !== -1) return episodes;
      if (u.indexOf('/qdl/audio') !== -1) return [];
      return undefined;
    },
  });
  r.lampa.Platform.tv = () => true;
  r.qdl.chooseEpisode('mmmm', 'Сериал');
  const menu = last(r.calls.selects);
  assert.ok(menu, 'меню серий показано');
  assert.strictEqual(menu.items.length, 2);
  assert.ok(menu.items[1].title.indexOf('врем.') !== -1);
  assert.strictEqual(menu.items[0].title.indexOf('врем.'), -1);

  menu.onSelect(menu.items[1]);
  assert.strictEqual(r.calls.plays.length, 1);
  assert.ok(r.calls.plays[0].url.indexOf('hash=dddd') !== -1, 'серия донора — с его hash');
});

// ─────────────────────────────── SWITCH-уведомление ───────────────────────────────

test('openNotification SWITCH: подтверждение → /qdl/watch/switch?accept=1; отказ → accept=0', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/watch/switch') !== -1 ? { success: true, switched: true } : undefined) });
  const n = { kind: 'SWITCH', hash: 'mmmm', title: 'Сериал', label: 'Найдена более полная раздача — серии 12 из 12 · 47 сид.' };

  r.qdl.openNotification(n);
  const confirm = last(r.calls.selects);
  assert.ok(confirm.title.indexOf('Сериал') !== -1);
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/list') !== -1).length, 0, 'карточка не открывается — это подтверждение');

  confirm.onSelect(confirm.items[0]);   // «Переключить»
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/watch/switch?hash=mmmm&accept=1') !== -1).length, 1);
  assert.ok(r.calls.noty.some((m) => m.indexOf('Переключено') !== -1));

  r.qdl.openNotification(n);
  const confirm2 = last(r.calls.selects);
  confirm2.onSelect(confirm2.items[1]);   // «Оставить как есть»
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('accept=0') !== -1).length, 1);
});

test('openNotification без kind: старое поведение — открытие карточки по /qdl/list', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/list') !== -1 ? [{ hash: 'aaaa', meta: { id: 1, media_type: 'tv' } }] : undefined) });
  r.qdl.openNotification({ hash: 'aaaa', title: 'Сериал' });
  assert.strictEqual(r.calls.pushes.length, 1, 'полная карточка открыта');
});

// ─────────────────────────────── pollNotifications: SWITCH/INFO без «скачана» ───────────────────────────────

test('pollNotifications: SWITCH-уведомление НЕ получает суффикс «скачана», обычная серия — получает', () => {
  const items = [
    { id: 10, kind: 'SWITCH', hash: 'mmmm', title: 'Сериал', label: 'Найдена более полная раздача — 12 из 12' },
    { id: 11, hash: 'mmmm', title: 'Сериал', label: 'Сезон 2 · серия 9' },
  ];
  const r = rig({ respond: (u) => (u.indexOf('/qdl/notifications') !== -1 ? { items: items, unread: 2 } : undefined) });
  r.lampa.Storage.set('qdl_noti_lastid', 5);   // lastId>0 → тосты активны
  r.qdl.pollNotifications();

  const sw = r.calls.noty.filter(function (m) { return m.indexOf('Найдена более полная') !== -1; });
  assert.strictEqual(sw.length, 1, 'SWITCH показан своим тостом');
  assert.ok(sw[0].indexOf('скачана') === -1, 'без «скачана»');
  assert.ok(sw[0].indexOf('🔀') !== -1, 'иконка переключения');
  assert.ok(r.calls.noty.some(function (m) { return m.indexOf('серия 9') !== -1 && m.indexOf('скачана') !== -1; }), 'обычная серия — «скачана»');
});

// ─────────────────────────────── shortDate ───────────────────────────────

test('shortDate: дд.мм.гг; битые даты → пусто', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.shortDate('2026-07-15T10:00:00Z'), '15.07.26');
  assert.strictEqual(qdl.shortDate('not a date'), '');
  assert.strictEqual(qdl.shortDate('1970-01-01T00:00:00Z'), '');
});
