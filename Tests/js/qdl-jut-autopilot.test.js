'use strict';
// «Автопилот» jut.su на клиенте: тумблер в хедере (двойная стрелочка), пропуск опенинга
// и автозапуск следующей серии.
//
// 🔥 Главный тест здесь — порядок в jutPlay: плейлист обязан лежать НА ОБЪЕКТЕ в момент
// вызова Player.play. Нативные плееры (все наши оболочки идут по android-ветке) сериализуют
// data синхронно внутри play, а Lampa.Player.playlist() отрабатывает уже после — проверять
// нужно именно состояние объекта при вызове, а не факт «playlist() был вызван».

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function headHtml() {
  return (
    '<div class="head"><div class="head__body">' +
      '<div class="head__title">jut.su</div>' +
      '<div class="head__actions"><div class="head__action selector open--settings">s</div></div>' +
    '</div></div>'
  );
}

// ───────────────────────── тумблер в хедере ─────────────────────────

test('тумблер вставляется ровно один раз и сразу за названием', () => {
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: headHtml() });
  qdl.ensureJutAutopilot();
  qdl.ensureJutAutopilot();   // идемпотентность: вотчер хедера зовёт его многократно

  const btns = doc.querySelectorAll('.qdl-jut-skip');
  assert.strictEqual(btns.length, 1);
  assert.strictEqual(btns[0].previousElementSibling.className, 'head__title');
});

test('тумблер достижим пультом (data-controller=head)', () => {
  // Поздняя иконка в хедере без этого не фокусируется — грабля колокольчика.
  const { qdl, $, doc } = H.loadQdlDom({ bodyHtml: headHtml() });
  qdl.ensureJutAutopilot();
  assert.strictEqual($(doc.querySelector('.qdl-jut-skip')).data('controller'), 'head');
});

test('два .head__title не клонируют кнопку', () => {
  const html = headHtml().replace('</div></div>', '</div><div class="head__title">2</div></div>');
  const { doc, qdl } = H.loadQdlDom({ bodyHtml: html });
  qdl.ensureJutAutopilot();
  assert.strictEqual(doc.querySelectorAll('.qdl-jut-skip').length, 1);
});

test('клик переключает состояние и красит кнопку', () => {
  const { doc, qdl, lampa, $ } = H.loadQdlDom({ bodyHtml: headHtml() });
  qdl.ensureJutAutopilot();
  const btn = doc.querySelector('.qdl-jut-skip');

  assert.strictEqual(qdl.jutAutopilot(), false, 'по умолчанию выключен');
  assert.ok(!btn.classList.contains('qdl-jut-skip--on'));

  $(btn).trigger('hover:enter');
  assert.strictEqual(lampa.Storage.get('qdl_jut_autopilot', false), true);
  assert.ok(btn.classList.contains('qdl-jut-skip--on'));

  $(btn).trigger('hover:enter');
  assert.strictEqual(lampa.Storage.get('qdl_jut_autopilot', false), false);
  assert.ok(!btn.classList.contains('qdl-jut-skip--on'));
});

test('кнопка видна только на страницах jut.su', () => {
  const lampa = H.makeLampa();
  let component = 'jut_title';
  lampa.Activity.active = () => ({ component });

  const { doc, qdl } = H.loadQdlDom({ bodyHtml: headHtml(), lampa });
  qdl.ensureJutAutopilot();
  const btn = doc.querySelector('.qdl-jut-skip');
  assert.notStrictEqual(btn.style.display, 'none');

  for (const c of ['jut_catalog', 'jut_episodes', 'jut_search']) {
    component = c;
    qdl.jutAutopilotVisibility();
    assert.notStrictEqual(btn.style.display, 'none', c + ' — страница jut.su');
  }

  component = 'qdl_downloads';
  qdl.jutAutopilotVisibility();
  assert.strictEqual(btn.style.display, 'none', 'вне jut.su кнопки быть не должно');
});

test('состояние с другого устройства перекрашивает кнопку', () => {
  // Ключ синкается через sync_invc.import_keys — прилетевшее значение обязано быть видно.
  const lampa = H.makeLampa();
  const subs = {};
  lampa.Storage.listener = { follow(t, fn) { (subs[t] = subs[t] || []).push(fn); } };

  const { doc, qdl } = H.loadQdlDom({ bodyHtml: headHtml(), lampa });
  qdl.ensureJutAutopilot();
  qdl.initJutAutopilot();

  lampa.Storage.set('qdl_jut_autopilot', true);
  (subs.change || []).forEach((fn) => fn({ name: 'qdl_jut_autopilot' }));

  assert.ok(doc.querySelector('.qdl-jut-skip').classList.contains('qdl-jut-skip--on'));
});

// ───────────────────────── запуск серии ─────────────────────────

const ITEMS = [
  { kind: 'episode', season: 1, ep: 1, key: 's1e1', tok: 'TOK1' },
  { kind: 'episode', season: 1, ep: 2, key: 's1e2', tok: 'TOK2' },
  { kind: 'episode', season: 1, ep: 3, key: 's1e3', tok: 'TOK3' },
];

const RESOLVE = {
  ok: true,
  url: '/qdl/jut/stream?t=TOK1',
  intro_start: 80,
  intro_end: 170,
  segments: { skip: [{ start: 80, end: 170, name: 'intro' }] },
};

/** Гоняет jutPlay с подменённым fetch и снимает СОСТОЯНИЕ data в момент Player.play. */
function runPlay(autopilot) {
  const lampa = H.makeLampa();
  lampa.Storage.set('qdl_jut_autopilot', autopilot);

  const seen = { atPlay: null, playlistArg: null };
  lampa.Player.play = (data) => {
    // снимок именно на момент вызова: дальше объект ещё будет дополняться
    seen.atPlay = JSON.parse(JSON.stringify(data));
  };
  lampa.Player.playlist = (list) => { seen.playlistArg = list; };
  lampa.Player.listener = { follow() {} };

  // req() в qdl.js ходит через Lampa.Reguest — отдаём готовый resolve
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => { if (String(url).includes('/qdl/jut/resolve')) ok(RESOLVE); };
  };

  const { qdl } = H.loadQdlDom({ bodyHtml: headHtml(), lampa });
  qdl.jutPlay('spy-family', ITEMS[0], 'Spy x Family', ITEMS);
  return seen;
}

test('автопилот ВКЛ: плейлист лежит на объекте уже в момент Player.play', () => {
  // 🔥 Первопричина, а не следствие: нативы читают data синхронно внутри play.
  const seen = runPlay(true);

  assert.ok(seen.atPlay, 'Player.play обязан быть вызван');
  assert.ok(Array.isArray(seen.atPlay.playlist), 'нативам плейлист доезжает только так');
  assert.strictEqual(seen.atPlay.playlist.length, 3);
  assert.ok(seen.playlistArg, 'веб-плеер получает список отдельно, как раньше');
});

test('автопилот ВКЛ: опенинг едет сегментом, без duration_ms', () => {
  const seen = runPlay(true);

  assert.deepStrictEqual(seen.atPlay.segments, { skip: [{ start: 80, end: 170, name: 'intro' }] });
  assert.strictEqual(seen.atPlay.segments.duration_ms, undefined,
    'с duration_ms бандл подгоняет метки под свою эвристику');
  assert.deepStrictEqual(seen.atPlay.playlist[0].segments, seen.atPlay.segments,
    'текущая серия в плейлисте несёт ту же разметку');
});

test('автопилот ВКЛ: у соседних серий есть адрес их разметки', () => {
  const seen = runPlay(true);
  const next = seen.atPlay.playlist[1];

  assert.ok(next.segmentsUrl.includes('/qdl/jut/segments?t=TOK2'));
  assert.strictEqual(next.qdl_jut_tok, 'TOK2');
  assert.strictEqual(next.segments, undefined, 'разметка соседей приезжает префетчем/нативом');
});

test('автопилот ВЫКЛ: нативам плейлист не уходит, автонекст глушится флагом', () => {
  const seen = runPlay(false);

  assert.strictEqual(seen.atPlay.playlist, undefined, 'это и есть гейт для нативных плееров');
  assert.strictEqual(seen.atPlay.segments, undefined, 'опенинг не пропускаем');
  assert.strictEqual(seen.atPlay.qdl_no_autonext, true);
  assert.ok(seen.playlistArg.every((x) => x.qdl_no_autonext === true),
    'флаг нужен каждому элементу: при select он станет текущим');
  assert.ok(seen.playlistArg.every((x) => x.segmentsUrl === undefined));
  assert.strictEqual(seen.playlistArg.length, 3, 'ручное переключение серий остаётся');
});

test('серия без разметки опенинга просто играет без скипа', () => {
  const lampa = H.makeLampa();
  lampa.Storage.set('qdl_jut_autopilot', true);
  let played = null;
  lampa.Player.play = (d) => { played = d; };
  lampa.Player.playlist = () => {};
  lampa.Player.listener = { follow() {} };
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    // ответ сервера без intro — штатная ситуация (размечены не все серии)
    this.silent = (url, ok) => {
      if (String(url).includes('/qdl/jut/resolve'))
        ok({ ok: true, url: '/qdl/jut/stream?t=TOK1', segments: { skip: [] } });
    };
  };

  const { qdl } = H.loadQdlDom({ bodyHtml: headHtml(), lampa });
  qdl.jutPlay('spy-family', ITEMS[0], 'Spy x Family', ITEMS);

  assert.ok(played, 'плеер обязан стартовать и без разметки');
  assert.deepStrictEqual(played.segments, { skip: [] });
});

test('старый сервер без поля segments не ломает запуск', () => {
  const lampa = H.makeLampa();
  lampa.Storage.set('qdl_jut_autopilot', true);
  let played = null;
  lampa.Player.play = (d) => { played = d; };
  lampa.Player.playlist = () => {};
  lampa.Player.listener = { follow() {} };
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, ok) => {
      if (String(url).includes('/qdl/jut/resolve')) ok({ ok: true, url: '/qdl/jut/stream?t=TOK1' });
    };
  };

  const { qdl } = H.loadQdlDom({ bodyHtml: headHtml(), lampa });
  qdl.jutPlay('spy-family', ITEMS[0], 'Spy x Family', ITEMS);

  assert.ok(played);
  assert.strictEqual(played.segments, undefined);
  assert.ok(Array.isArray(played.playlist), 'автопереход при этом работает');
});
