'use strict';
// Склеенная карточка сезонов (qdl 2.78): сервер отдаёт сезоны одного сериала ОДНОЙ карточкой
// с полем parts. Просмотру про части знать не надо (плейлист уже общий), а управлению — надо:
// удалять, транскодировать, следить и запоминать озвучку умеет только конкретная раздача.
//
// 🔒 Что здесь заперто:
//   • обычная карточка (parts нет) ведёт себя ТОЧНО как раньше — ни одного лишнего Select;
//   • удаление склеенной карточки не сносит группу молча: сперва выбор, потом подтверждение;
//   • цепочка удаления последовательная и останавливается на первой ошибке;
//   • в гриде видно, что карточка составная;
//   • в списке серий сезоны разделены заголовками (иначе нумерация 1..16 дважды = «дубли»).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HA = 'a'.repeat(40), HB = 'b'.repeat(40);

const PARTS = [
  { hash: HA, name: 'Сериал / Сезон: 1', season: 1, size: 100, progress: 1, state: 'queuedUP', local: false, watched: false },
  { hash: HB, name: 'Сериал / Сезон: 2', season: 2, size: 200, progress: 1, state: 'queuedUP', local: false, watched: true },
];
const MERGED = {
  hash: HA, name: 'Сериал / Сезон: 1', progress: 1, state: 'queuedUP', size: 300,
  meta: { id: 7, media_type: 'tv', title: 'Сериал' }, seasons: [1, 2], parts: PARTS,
};
const PLAIN = { hash: HA, name: 'Movie.mkv', progress: 1, meta: { id: 9, media_type: 'movie', title: 'Фильм' } };

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [], replaces: 0, toggles: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push() {}, replace: () => calls.replaces++, active: () => ({}) },
    Controller: { add() {}, toggle: (n) => calls.toggles.push(n), collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h === 'ERR') { if (err) err(); return; }
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa });
  return { qdl, lampa, calls };
}

const last = (a) => a[a.length - 1];
const menuItem = (m, act) => m.items.filter((i) => i.act === act)[0];

// ─────────────────────────────── чистые хелперы ───────────────────────────────

test('cardParts: только карточка с ДВУМЯ и более частями считается склеенной', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.cardParts(PLAIN), null);
  assert.strictEqual(qdl.cardParts({ parts: [PARTS[0]] }), null, 'одна часть — обычная карточка');
  assert.strictEqual(qdl.cardParts(MERGED).length, 2);
});

test('partLabel: сезон, если сервер его разобрал; иначе укороченное имя раздачи + метка MP4', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.partLabel(PARTS[0]), 'Сезон 1');
  assert.strictEqual(qdl.partLabel({ season: 2, local: true }), 'Сезон 2 · MP4');
  assert.strictEqual(qdl.partLabel({ name: 'Что-то без сезона' }), 'Что-то без сезона');
  assert.ok(qdl.partLabel({ name: 'x'.repeat(80) }).length < 50, 'длинное имя режется');
});

test('withPart: обычная карточка выполняет действие СРАЗУ, склеенная — сперва спрашивает', () => {
  const { qdl, calls } = rig();
  const got = [];
  qdl.withPart(PLAIN, 'Заголовок', (it) => got.push(it));
  assert.strictEqual(calls.selects.length, 0, 'лишнего Select на обычной карточке нет');
  assert.strictEqual(got[0], PLAIN);

  qdl.withPart(MERGED, 'Какой сезон?', (it) => got.push(it));
  const pick = last(calls.selects);
  assert.strictEqual(pick.title, 'Какой сезон?');
  assert.deepStrictEqual(pick.items.map((i) => i.title), ['Сезон 1', 'Сезон 2  🔔', 'Отмена']);
  pick.onSelect(pick.items[1]);
  assert.strictEqual(got[1].hash, HB, 'действие ушло выбранной раздаче');
  assert.strictEqual(got[1].meta, MERGED.meta, 'мета и постер — общие карточки');
});

test('withPart: пункт «ко всем частям» отдаёт список раздач, «Отмена» возвращает фокус', () => {
  const { qdl, calls } = rig();
  let all = null, runs = 0;
  qdl.withPart(MERGED, 'Удалить', (it, a) => { runs++; all = a; }, '🗑 Всё');
  const pick = last(calls.selects);
  assert.strictEqual(pick.items[0].title, '🗑 Всё');
  pick.onSelect(pick.items[0]);
  assert.deepStrictEqual(all.map((p) => p.hash), [HA, HB]);

  qdl.withPart(MERGED, 'Удалить', () => runs++, '🗑 Всё');
  const pick2 = last(calls.selects);
  pick2.onSelect(pick2.items[pick2.items.length - 1]);   // «Отмена»
  assert.strictEqual(runs, 1, 'отмена ничего не выполняет');
  assert.ok(calls.toggles.indexOf('content') !== -1);
});

// ─────────────────────────────── удаление ───────────────────────────────

test('deleteHashes: строго последовательно, чистит озвучку, первая ошибка обрывает цепочку', () => {
  const bad = { respond: (u) => (u.indexOf(HB) !== -1 ? 'ERR' : { success: true }) };
  const r = rig(bad);
  r.lampa.Storage.set('qdl_audio2', { [HA]: 'e1', [HB]: 'e2', other: 'e0' });

  let ok = null;
  r.qdl.deleteHashes([HA, HB], (v) => { ok = v; });
  assert.strictEqual(ok, false, 'сбой второй раздачи — не «готово»');
  const dels = r.calls.reqs.filter((u) => u.indexOf('/qdl/delete') !== -1);
  assert.strictEqual(dels.length, 2);
  assert.ok(dels[0].indexOf(HA) !== -1 && dels[1].indexOf(HB) !== -1, 'порядок сохранён');
  const audio = r.lampa.Storage.get('qdl_audio2', {});
  assert.strictEqual(audio[HA], undefined, 'у удалённой озвучка вычищена');
  assert.strictEqual(audio[HB], 'e2', 'у неудалённой осталась');
  assert.strictEqual(audio.other, 'e0');
});

test('quickMenu del: склеенная карточка — выбор сезона, затем подтверждение, затем ОДИН запрос', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/delete') !== -1 ? { success: true } : undefined) });
  r.qdl.quickMenu(MERGED);
  last(r.calls.selects).onSelect(menuItem(r.calls.selects[0], 'del'));

  const pick = last(r.calls.selects);
  assert.ok(pick.title.indexOf('Сериал') !== -1);
  pick.onSelect(pick.items[2]);                     // «Сезон 2» (0 — «весь сериал»)

  const confirm = last(r.calls.selects);
  assert.ok(confirm.title.indexOf('сезон 2') !== -1 && confirm.title.indexOf('с файлами?') !== -1, confirm.title);
  confirm.onSelect(confirm.items[0]);

  const dels = r.calls.reqs.filter((u) => u.indexOf('/qdl/delete') !== -1);
  assert.strictEqual(dels.length, 1);
  assert.ok(dels[0].indexOf(HB) !== -1 && dels[0].indexOf('deleteFiles=true') !== -1);
  assert.ok(r.calls.noty.indexOf('Удалено') !== -1);
  assert.strictEqual(r.calls.replaces, 1);
});

test('quickMenu del: «Весь сериал» удаляет ВСЕ раздачи группы', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/delete') !== -1 ? { success: true } : undefined) });
  r.qdl.quickMenu(MERGED);
  last(r.calls.selects).onSelect(menuItem(r.calls.selects[0], 'del'));
  const pick = last(r.calls.selects);
  pick.onSelect(pick.items[0]);                     // «🗑 Весь сериал»

  const confirm = last(r.calls.selects);
  assert.ok(confirm.title.indexOf('целиком (раздач: 2)') !== -1, confirm.title);
  confirm.onSelect(confirm.items[0]);

  const dels = r.calls.reqs.filter((u) => u.indexOf('/qdl/delete') !== -1);
  assert.strictEqual(dels.length, 2);
  assert.ok(dels[0].indexOf(HA) !== -1 && dels[1].indexOf(HB) !== -1);
});

// ─────────────────────────────── слежение и озвучка ───────────────────────────────

test('quickMenu watch: подписка уходит ВЫБРАННОЙ раздаче (новые серии — в последнем сезоне)', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/watch') !== -1 ? { success: true } : undefined) });
  r.qdl.quickMenu(MERGED);
  last(r.calls.selects).onSelect(menuItem(r.calls.selects[0], 'watch'));

  const pick = last(r.calls.selects);
  pick.onSelect(pick.items[0]);   // «Сезон 1» — слежения нет → включаем
  assert.ok(r.calls.reqs.some((u) => u.indexOf('/qdl/watch?hash=' + HA) !== -1));
  assert.ok(r.calls.noty.some((m) => m.indexOf('Слежу') !== -1));
});

test('watchToggle: у подписанной раздачи снимает слежение, у чужой не трогает', () => {
  const r = rig({ respond: () => ({ success: true }) });
  const item = { hash: HB, watched: true };
  r.qdl.watchToggle(item);
  assert.ok(r.calls.reqs.some((u) => u.indexOf('/qdl/watch/remove?hash=' + HB) !== -1));
  assert.strictEqual(item.watched, false);
});

test('quickMenu audio: озвучка запоминается СЕЗОНУ (id дорожки специфичен для рипа)', () => {
  const r = rig({ respond: (u) => (u.indexOf('/qdl/audio') !== -1 ? [{ id: 'e1', label: 'Дубляж' }] : undefined) });
  r.qdl.quickMenu(MERGED);
  last(r.calls.selects).onSelect(menuItem(r.calls.selects[0], 'audio'));
  last(r.calls.selects).onSelect(last(r.calls.selects).items[1]);   // «Сезон 2»

  assert.ok(r.calls.reqs.some((u) => u.indexOf('/qdl/audio?hash=' + HB) !== -1), 'дорожки спрашиваем у выбранной');
  const opts = last(r.calls.selects);
  opts.onSelect(opts.items[0]);
  assert.strictEqual(r.lampa.Storage.get('qdl_audio2', {})[HB], 'e1');
  assert.strictEqual(r.lampa.Storage.get('qdl_audio2', {})[HA], undefined, 'соседнему сезону чужую дорожку не навязали');
});

test('buildPlaylist: файлу ЧУЖОЙ раздачи достаётся ЕЁ запомненная озвучка, а не выбранная', () => {
  const r = rig();
  // id внешней дорожки (d…) едет В КЛЮЧЕ HLS — на нём это и видно, на любой платформе
  r.lampa.Storage.set('qdl_audio2', { [HB]: 'd55' });
  const vids = [
    { index: 0, name: 'S01E01.mkv', hash: HA, tl: 't7:s1e1' },
    { index: 0, name: 'S02E01.mkv', hash: HB, tl: 't7:s2e1' },
  ];
  const pl = r.qdl.buildPlaylist(HA, vids, 'd11', HA);
  assert.ok(pl[0].url.indexOf(HA + '_0_d11/') !== -1, 'своей раздаче — выбранная дорожка: ' + pl[0].url);
  assert.ok(pl[1].url.indexOf(HB + '_0_d55/') !== -1, 'чужой — её собственная: ' + pl[1].url);
});

// ─────────────────────────────── экран серий ───────────────────────────────

function domScroll(w) {
  return function () {
    const root = w.document.createElement('div');
    const bodyEl = w.document.createElement('div');
    root.appendChild(bodyEl);
    this.render = () => w.$(root);
    this.body = () => w.$(bodyEl);
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

function mountEpisodes(files) {
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
        else ok([]);
      };
    },
    Select: { show() {} },
    Player: { play() {}, playlist() {} },
    Platform: { tv: () => true },
  });
  const r = H.loadQdlDom({ lampa });
  r.lampa.Scroll = domScroll(r.w);
  const inst = new r.qdl.ComponentEpisodes({ qdl_hash: HA, qdl_name: 'Сериал' });
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  return { r, root: inst.render() };
}

const ep = (season, n, hash) => ({
  index: n - 1, name: `Show.S0${season}.E0${n}.mkv`, size: 100, hash: hash,
  season: season, episode: n, epkey: 's' + season + 'e' + n, tl: 't7:s' + season + 'e' + n,
});

test('экран серий: два сезона — заголовки «Сезон 1»/«Сезон 2», строки на месте', () => {
  const { root } = mountEpisodes([ep(1, 1, HA), ep(1, 2, HA), ep(2, 1, HB), ep(2, 2, HB)]);
  assert.strictEqual(root.find('.qdl-row-focus').length, 4, 'ни одна серия не потерялась');
  const text = root.text();
  assert.ok(text.indexOf('Сезон 1') !== -1 && text.indexOf('Сезон 2') !== -1, 'разделители сезонов есть');
  assert.strictEqual(root.find('.qdl-ep-num').eq(2).text(), '1', 'нумерация второго сезона начинается заново');
});

test('экран серий: один сезон — заголовков нет (лишний шум на обычной карточке)', () => {
  const { root } = mountEpisodes([ep(1, 1, HA), ep(1, 2, HA)]);
  assert.strictEqual(root.find('.qdl-row-focus').length, 2);
  assert.strictEqual(root.text().indexOf('Сезон 1'), -1);
});

test('epHeadSeason: экстры не получают заголовок чужого сезона', () => {
  const { qdl } = rig();
  assert.strictEqual(qdl.epHeadSeason({ season: 2, epkey: 's2e1' }), 2);
  assert.strictEqual(qdl.epHeadSeason({ epkey: 'film1' }), 0, 'экстра → «Дополнительно»');
  assert.strictEqual(qdl.epHeadSeason({ name: 'trailer.mkv' }), -1, 'без ключа заголовка не даём вовсе');
});
