'use strict';
// Язык аудиодорожки (qdl 2.24): автовыбор русской + кнопка «Язык» на экране серий.
//
// Главное, что здесь проверяется, — фича НЕ МОЖЕТ спрятать контент и не меняет поведение
// там, где языка не видно. Отсюда два инварианта:
//   1) нет lang2 у дорожек → экран и меню ровно такие же, как до фичи;
//   2) нет дорожек выбранного языка → показываем ВСЕ, а не пустой список.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HASH = 'h'.repeat(40);
const FILES = [
  { index: 0, name: 'Ep01.mkv', size: 1073741824 },
  { index: 1, name: 'Ep02.mkv', size: 1073741824 },
];

function reqMock(files, audio) {
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/episodes') !== -1 || u.indexOf('/qdl/files') !== -1) ok(files);
        else if (u.indexOf('/qdl/audio') !== -1) ok(audio || []);
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
    this.minus = () => {};
    this.update = () => {};
    this.append = (el) => w.$(bodyEl).append(el);
    this.destroy = () => {};
  };
}

function mount(opts) {
  opts = opts || {};
  const calls = { plays: [], playlists: [], selects: [] };
  const lampa = H.makeLampa(Object.assign(reqMock(opts.files || FILES, opts.audio), {
    Player: { play: (x) => calls.plays.push(x), playlist: (p) => calls.playlists.push(p) },
    Select: { show: (o) => calls.selects.push(o) },
    Platform: { tv: () => true },
  }));
  const r = H.loadQdlDom({ lampa });
  r.lampa.Scroll = domScroll(r.w);
  if (opts.before) opts.before(r.lampa, r);
  const inst = new r.qdl.ComponentEpisodes({ qdl_hash: HASH, qdl_name: 'Сериал' });
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  return { r, inst, calls, root: inst.render(), q: r.qdl };
}

const RU_EN = [
  { id: 'e1', label: 'Русский (ориг.)', lang2: 'ru', langName: 'Русский' },
  { id: 'e2', label: 'English (ориг.)', lang2: 'en', langName: 'Английский' },
];
const TWO_RU = [
  { id: 'e1', label: 'Дубляж (ориг.)', lang2: 'ru', langName: 'Русский' },
  { id: 'd5', label: 'LostFilm', lang2: 'ru', langName: 'Русский' },
  { id: 'e2', label: 'English (ориг.)', lang2: 'en', langName: 'Английский' },
];
const NO_LANG = [{ id: 'e1', label: 'Русский' }, { id: 'd5', label: 'LostFilm' }];

// ── чистые функции ──

test('audioLang: нет lang2 → null (мок без поля не должен ронять код)', () => {
  const m = mount();
  assert.strictEqual(m.q.audioLang({ id: 'e1' }), null);
  assert.strictEqual(m.q.audioLang(null), null);
  assert.strictEqual(m.q.audioLang({ lang2: 'ru' }), 'ru');
});

test('filterByLang НИКОГДА не отдаёт пусто: нет нужного языка → все дорожки', () => {
  const m = mount();
  assert.strictEqual(m.q.filterByLang(RU_EN, 'ru').length, 1);
  assert.strictEqual(m.q.filterByLang(RU_EN, 'ja').length, 2, 'японской нет → показываем все');
  assert.strictEqual(m.q.filterByLang(RU_EN, 'all').length, 2);
  assert.strictEqual(m.q.filterByLang(NO_LANG, 'ru').length, 2, 'языков нет вовсе → все');
});

test('audioLangs: только реально представленные языки, без дублей', () => {
  const m = mount();
  // .join: массив приходит из jsdom-песочницы (свой Array.prototype), deepStrictEqual сверяет прототипы
  assert.strictEqual(m.q.audioLangs(TWO_RU).map((l) => l.code).join(','), 'ru,en');
  assert.strictEqual(m.q.audioLangs(NO_LANG).length, 0);
});

test('langLabel честно сообщает, когда выбранного языка нет', () => {
  const m = mount();
  assert.strictEqual(m.q.langLabel(RU_EN, 'ru'), 'Русский');
  assert.strictEqual(m.q.langLabel(RU_EN, 'all'), 'Все языки');
  assert.ok(m.q.langLabel(RU_EN, 'ja').indexOf('показаны все') !== -1);
});

// ── экран серий ──

test('обратная совместимость: без lang2 кнопки языка нет и поведение прежнее', () => {
  const m = mount({ audio: NO_LANG });
  assert.strictEqual(m.root.find('.qdl-lang-btn').length, 0, 'кнопки языка нет');
  assert.strictEqual(m.root.find('.qdl-audio-btn').length, 1, 'кнопка озвучки одна, как раньше');
  assert.strictEqual(m.root.find('.qdl-audio-btn').text(), 'Озвучка: выбрать');
});

test('ru+en по одной: русская выбирается МОЛЧА, вопроса об озвучке нет', () => {
  const m = mount({ audio: RU_EN });
  m.root.find('.qdl-row-focus').eq(0).trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 0, 'ни одного меню');
  assert.strictEqual(m.calls.plays.length, 1, 'сразу играет');
  assert.strictEqual(m.inst.audio, 'e1', 'выбрана русская дорожка');
});

test('две русские: молча не выбираем (это выбор зрителя), спрашиваем ТОЛЬКО про русские', () => {
  const m = mount({ audio: TWO_RU });
  m.root.find('.qdl-row-focus').eq(0).trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 1, 'меню показано');
  const ids = m.calls.selects[0].items.map((i) => i.id);
  assert.deepStrictEqual(ids, ['e1', 'd5'], 'английская дорожка в меню не предлагается');
});

test('кнопка «Язык» есть только когда языков больше одного', () => {
  assert.strictEqual(mount({ audio: RU_EN }).root.find('.qdl-lang-btn').length, 1);
  const onlyRu = [{ id: 'e1', label: 'Дубляж', lang2: 'ru', langName: 'Русский' },
                  { id: 'd5', label: 'LostFilm', lang2: 'ru', langName: 'Русский' }];
  assert.strictEqual(mount({ audio: onlyRu }).root.find('.qdl-lang-btn').length, 0, 'один язык — кнопка не нужна');
});

test('смена языка: преф сохраняется, подписи обеих кнопок обновляются, строки не перестроены', () => {
  const m = mount({ audio: TWO_RU });
  const rowsBefore = m.root.find('.qdl-row-focus').length;

  m.root.find('.qdl-lang-btn').trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 1);
  const en = m.calls.selects[0].items.filter((i) => i.code === 'en')[0];
  assert.ok(en, 'английский в списке языков');
  m.calls.selects[0].onSelect(en);

  assert.strictEqual(m.r.lampa.Storage.get('qdl_audio_lang'), 'en');
  assert.strictEqual(m.root.find('.qdl-lang-btn').text(), 'Язык: Английский');
  assert.strictEqual(m.root.find('.qdl-row-focus').length, rowsBefore, 'DOM строк не трогали');

  // теперь английская единственная в своём языке → играет молча
  m.calls.selects.length = 0;
  m.root.find('.qdl-row-focus').eq(0).trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 0);
  assert.strictEqual(m.inst.audio, 'e2', 'выбрана английская дорожка');
});

test('сохранённый выбор озвучки бьёт автовыбор по языку', () => {
  const m = mount({
    audio: RU_EN,
    before: (lampa) => { lampa.Storage.set('qdl_audio2', { [HASH]: 'e2' }); },
  });
  m.root.find('.qdl-row-focus').eq(0).trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 0, 'без вопроса');
  assert.strictEqual(m.inst.audio, 'e2', 'играется руками выбранная e2, а не русская');
});
