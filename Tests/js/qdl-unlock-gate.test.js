'use strict';
// qdl 2.39: скрытый функционал (транскод / удаление с файлами / коллекции / экран «Хелс-чеки»)
// открывается кукой qdl_unlock=1. Без неё пункты не рисуются НИКОМУ — ни приложениям,
// ни браузеру: платформенных веток тут сознательно нет.
//
// 🔴 2.67: кука перестала быть ЕДИНСТВЕННЫМ ключом — те же действия открывает право
// «Управление», выданное устройству в /admin/d1v (qdlManage = кука ИЛИ право). Этот файл
// сторожит именно ветку куки: она осталась мастер-ключом владельца и страховкой от
// самозапирания, если потеряется access.json. Ветка права — в qdl-manage-gate.test.js.
// Дефолт харнеса — «разблокировано» (иначе старые тесты quickMenu не увидели бы пунктов),
// поэтому режим «без куки» задаётся явно через opts.cookie: ''.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// ─────────────────────────────── qdlUnlocked ───────────────────────────────

test('qdlUnlocked: без куки → false', () => {
  const { qdl } = H.loadQdl({ cookie: '' });
  assert.strictEqual(qdl.qdlUnlocked(), false);
});

test('qdlUnlocked: с кукой qdl_unlock=1 → true', () => {
  const { qdl } = H.loadQdl({ cookie: 'qdl_unlock=1' });
  assert.strictEqual(qdl.qdlUnlocked(), true);
});

test('qdlUnlocked: кука в середине списка (границы ^|; в регексе)', () => {
  const { qdl } = H.loadQdl({ cookie: 'a=1; qdl_unlock=1; b=2' });
  assert.strictEqual(qdl.qdlUnlocked(), true);
});

test('qdlUnlocked: похожие имена/значения не открывают доступ', () => {
  assert.strictEqual(H.loadQdl({ cookie: 'xqdl_unlock=1' }).qdl.qdlUnlocked(), false);
  assert.strictEqual(H.loadQdl({ cookie: 'qdl_unlock=0' }).qdl.qdlUnlocked(), false);
  assert.strictEqual(H.loadQdl({ cookie: 'qdl_unlock=' }).qdl.qdlUnlocked(), false);
});

test('qdlUnlocked: document без cookie не роняет плагин', () => {
  const doc = H.makeDocument();
  delete doc.cookie;
  const { qdl } = H.loadQdl({ document: doc, cookie: undefined });
  // харнесс подставит дефолт только если поля нет — тут оно удалено, значит вернётся 'qdl_unlock=1'
  assert.strictEqual(typeof qdl.qdlUnlocked(), 'boolean');
});

// ─────────────────────────────── quickMenu ───────────────────────────────

function menuTitles(cookie) {
  let captured = null;
  const lampa = H.makeLampa({ Select: { show: (opts) => { captured = opts; } } });
  const { qdl } = H.loadQdl({ lampa, cookie });
  qdl.quickMenu({ hash: 'x', name: 'Movie.mkv', progress: 1, state: 'stalledUP' });
  assert.ok(captured, 'Select.show должен быть вызван');
  return captured.items.map((i) => i.title);
}

test('quickMenu без куки: ни транскода, ни удаления, но обычные пункты на месте', () => {
  const titles = menuTitles('');
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать') !== -1), 'транскод скрыт');
  assert.ok(!titles.some((t) => t.indexOf('Удалить') !== -1), 'удаление скрыто');
  assert.ok(titles.some((t) => t.indexOf('Смотреть') !== -1), 'смотреть остаётся');
  assert.ok(titles.some((t) => t.indexOf('Озвучка') !== -1), 'озвучка остаётся');
  // 🔴 2.67: коллекции ушли под право «Управление» (решение владельца) — их мутации сервер
  // гейтит так же, как удаление, поэтому и в меню без ключа их быть не должно: пункт, который
  // гарантированно ответит 403, читается как поломка. Проверка «с правом» — в qdl-manage-gate.
  assert.ok(!titles.some((t) => t.indexOf('коллекци') !== -1), 'коллекции тоже под «Управлением»');
});

test('quickMenu с кукой: транскод и удаление доступны', () => {
  const titles = menuTitles('qdl_unlock=1');
  assert.ok(titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Удалить') !== -1));
});

test('quickMenu без куки: недокачанной раздаче тоже ничего лишнего', () => {
  let captured = null;
  const lampa = H.makeLampa({ Select: { show: (opts) => { captured = opts; } } });
  const { qdl } = H.loadQdl({ lampa, cookie: '' });
  qdl.quickMenu({ hash: 'x', name: 'Movie.mkv', progress: 0.4, state: 'downloading' });
  const titles = captured.items.map((i) => i.title);
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать') !== -1));
  assert.ok(!titles.some((t) => t.indexOf('Удалить') !== -1));
});

// ────────────────── подсказка про транскод в диалоге выбора раздачи ──────────────────

function hevcNoties(cookie) {
  const noties = [];
  let select = null;
  const lampa = H.makeLampa({
    Noty: { show: (t) => noties.push(String(t)) },
    Select: { show: (opts) => { select = opts; } },
    Reguest: function () {
      this.timeout = () => {};
      this.silent = (url, ok) => { ok([{ title: 'Movie 2160p', codec: 'hevc', magnet: 'magnet:?xt=1' }]); };
    },
  });
  const { qdl } = H.loadQdl({ lampa, cookie });
  qdl.chooseAndDownload({ title: 'Movie', media_type: 'movie' });
  assert.ok(select, 'список раздач должен показаться');
  select.onSelect(select.items[0]);
  return noties;
}

test('HEVC-подсказка про транскод показывается только с кукой', () => {
  assert.ok(hevcNoties('qdl_unlock=1').some((t) => t.indexOf('Транскодировать в MP4') !== -1),
    'с кукой подсказка уместна — пункт транскода есть');
  assert.ok(!hevcNoties('').some((t) => t.indexOf('Транскодировать в MP4') !== -1),
    'без куки подсказка ложная — пункта транскода нет');
});
