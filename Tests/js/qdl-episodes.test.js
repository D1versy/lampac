'use strict';
// Экран серий (ComponentEpisodes, qdl_episodes) — замена селектбокса:
// рендер строк с отметками/метой в настоящем DOM (jsdom + реальный jQuery),
// озвучка (преф молча / вопрос один раз при первом плее / смена кнопкой),
// автоплей от «Продолжить» (одноразовый), обновление отметок без перестройки DOM.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const HASH = 'h'.repeat(40);
const FILES = [
  { index: 0, name: 'Ep01.mkv', size: 1073741824 },
  { index: 1, name: 'Ep02.mkv', size: 1073741824 },
  { index: 4, name: 'Ep03.mkv', size: 536870912, source: 'donor', hash: 'dddd' },
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

// Scroll-мок на настоящем jsdom-DOM: компонент рендерит строки в реальные узлы
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
  const calls = { plays: [], playlists: [], selects: [] };
  const lampa = H.makeLampa(Object.assign(reqMock(opts.files || FILES, opts.audio), {
    Player: { play: (x) => calls.plays.push(x), playlist: (p) => calls.playlists.push(p) },
    Select: { show: (o) => calls.selects.push(o) },
    Platform: { tv: () => true },   // прямой /qdl/stream (кроме внешних дорожек → HLS)
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

// ─────────────────────────────── рендер ───────────────────────────────

test('рендер: заголовок, счётчик, строки с отметками/метой, подсветка текущей', () => {
  const m = mount({
    before: (lampa) => {
      lampa.Timeline.view(lampa.Utils.hash(HASH + ':Ep01')).percent = 100;
      lampa.Timeline.view(lampa.Utils.hash(HASH + ':Ep02')).percent = 40;
    },
  });
  const rows = rowsOf(m);
  assert.strictEqual(rows.length, 3, 'три строки серий');
  assert.strictEqual(m.root.find('.qdl-ep-title').text(), 'Сериал');
  assert.ok(m.root.text().indexOf('3 серии') !== -1, 'счётчик серий в шапке');

  const marks = rows.find('.qdl-ep-mark').map((_, e) => m.r.$(e).text()).get();
  assert.strictEqual(marks[0], '✓ ', '1-я досмотрена');
  assert.strictEqual(marks[1], '► 40% · ', '2-я на паузе');
  assert.strictEqual(marks[2], '', '3-я не начата');

  assert.ok(m.r.$(rows[1]).hasClass('qdl-ep--cur'), 'текущая (продолжаемая) подсвечена');
  assert.ok(!m.r.$(rows[0]).hasClass('qdl-ep--cur') && !m.r.$(rows[2]).hasClass('qdl-ep--cur'));

  assert.ok(m.r.$(rows[0]).text().indexOf('1 ГБ') !== -1, 'размер файла в мете');
  assert.ok(m.r.$(rows[2]).find('.qdl-ep-name').text().indexOf('· врем.') !== -1, 'донор помечен в имени');
  assert.ok(m.r.$(rows[2]).text().indexOf('временная — заменится основной') !== -1, 'донор расшифрован в мете');
});

test('hover:enter на строке играет соответствующий элемент плейлиста (донор — со своего hash)', () => {
  const m = mount();
  const rows = rowsOf(m);
  m.r.$(rows[2]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1);
  assert.strictEqual(m.calls.plays[0], m.calls.playlists[0][2], 'играется САМ элемент плейлиста');
  assert.ok(m.calls.plays[0].url.indexOf('hash=dddd') !== -1, 'донорская серия — с донорского hash');
  assert.strictEqual(m.calls.selects.length, 0, 'одна дорожка/нет дорожек → без вопроса об озвучке');
});

// ─────────────────────────────── озвучка ───────────────────────────────

const AUDIO = [{ id: 'e1', label: 'Русский' }, { id: 'd5', label: 'LostFilm' }];

test('озвучка без префа: кнопка «выбрать», вопрос ОДИН раз при первом плее, преф сохраняется', () => {
  const m = mount({ audio: AUDIO });
  const btn = m.root.find('.qdl-btn-focus');
  assert.strictEqual(btn.length, 1, 'кнопка озвучки есть при >1 дорожке');
  assert.strictEqual(btn.text(), 'Озвучка: выбрать');

  m.r.$(rowsOf(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 0, 'плей ждёт выбора озвучки');
  assert.strictEqual(m.calls.selects.length, 1, 'показан селект озвучки');
  assert.strictEqual(m.calls.selects[0].title, 'Озвучка');

  m.calls.selects[0].onSelect(m.calls.selects[0].items[1]);   // LostFilm (d5)
  assert.strictEqual(m.calls.plays.length, 1, 'после выбора — сразу плей');
  assert.ok(m.calls.plays[0].url.indexOf('_d5') !== -1, 'внешняя дорожка → HLS с audio-id');
  assert.strictEqual(m.r.lampa.Storage.get('qdl_audio2')[HASH], 'd5', 'преф сохранён на сериал');
  assert.strictEqual(btn.text(), 'Озвучка: LostFilm', 'кнопка показывает выбранную');

  m.r.$(rowsOf(m)[1]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 2, 'второй плей — без переспроса');
  assert.strictEqual(m.calls.selects.length, 1);
});

test('озвучка с префом: применяется молча, кнопка показывает её, плей без вопроса', () => {
  const m = mount({
    audio: AUDIO,
    before: (lampa) => { lampa.Storage.set('qdl_audio2', { [HASH]: 'd5' }); },
  });
  assert.strictEqual(m.root.find('.qdl-btn-focus').text(), 'Озвучка: LostFilm');
  m.r.$(rowsOf(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.calls.plays.length, 1, 'сразу плей');
  assert.strictEqual(m.calls.selects.length, 0, 'без вопроса');
});

test('смена озвучки кнопкой: преф обновлён, следующий плей — с новой дорожкой', () => {
  const m = mount({
    audio: AUDIO,
    before: (lampa) => { lampa.Storage.set('qdl_audio2', { [HASH]: 'd5' }); },
  });
  m.root.find('.qdl-btn-focus').trigger('hover:enter');
  assert.strictEqual(m.calls.selects.length, 1, 'селект смены озвучки');
  assert.ok(m.calls.selects[0].items[0].title.indexOf('✓') === 0, 'запомненная — первой с галочкой');

  const rus = m.calls.selects[0].items.filter((i) => i.id === 'e1')[0];
  m.calls.selects[0].onSelect(rus);
  assert.strictEqual(m.r.lampa.Storage.get('qdl_audio2')[HASH], 'e1', 'преф перезаписан');
  assert.strictEqual(m.root.find('.qdl-btn-focus').text(), 'Озвучка: Русский');
  assert.strictEqual(m.calls.plays.length, 0, 'смена озвучки сама по себе не играет');
});

// ─────────────────────────────── автоплей от «Продолжить» ───────────────────────────────

test('autoplay: играет продолжаемую серию и гаснет (возврат из плеера не перезапускает)', () => {
  const m = mount({
    obj: { qdl_autoplay: true },
    before: (lampa) => { lampa.Timeline.view(lampa.Utils.hash(HASH + ':Ep02')).percent = 40; },
  });
  assert.strictEqual(m.calls.plays.length, 1, 'плей стартовал сам');
  assert.strictEqual(m.calls.plays[0], m.calls.playlists[0][1], 'играется продолжаемая (2-я)');
  assert.strictEqual(m.obj.qdl_autoplay, false, 'флаг одноразовый');
});

// ─────────────────────────────── обновление отметок ───────────────────────────────

test('refreshMarks: отметки и подсветка обновляются НА МЕСТЕ (DOM не перестраивается)', () => {
  const m = mount({
    before: (lampa) => { lampa.Timeline.view(lampa.Utils.hash(HASH + ':Ep01')).percent = 40; },
  });
  const rows = rowsOf(m);
  const firstRowNode = rows[0];
  assert.strictEqual(m.r.$(rows[0]).find('.qdl-ep-mark').text(), '► 40% · ');

  // «досмотрели» 1-ю и «начали» 2-ю — как после возврата из плеера
  m.r.lampa.Timeline.view(m.r.lampa.Utils.hash(HASH + ':Ep01')).percent = 100;
  m.r.lampa.Timeline.view(m.r.lampa.Utils.hash(HASH + ':Ep02')).percent = 15;
  m.inst.refreshMarks();

  const rows2 = rowsOf(m);
  assert.strictEqual(rows2[0], firstRowNode, 'узлы те же — фокус пульта не теряется');
  assert.strictEqual(m.r.$(rows2[0]).find('.qdl-ep-mark').text(), '✓ ');
  assert.strictEqual(m.r.$(rows2[1]).find('.qdl-ep-mark').text(), '► 15% · ');
  assert.ok(m.r.$(rows2[1]).hasClass('qdl-ep--cur'), 'подсветка переехала на 2-ю');
  assert.ok(!m.r.$(rows2[0]).hasClass('qdl-ep--cur'));
});
