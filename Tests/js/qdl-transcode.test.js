'use strict';
// Тесты фичи «Транскодировать в MP4»: canTranscode (кому предлагать конверсию)
// и состав пунктов quickMenu (транскод — только завершённым раздачам; локальным
// файлам не предлагаем ни транскод, ни слежение за сериями).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const { qdl } = H.loadQdl();

// ─────────────────────────────── canTranscode ───────────────────────────────

test('canTranscode: завершённая раздача → true', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, state: 'stalledUP' }), true);
});

test('canTranscode: недокачанная раздача → false', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 0.5, state: 'downloading' }), false);
  assert.strictEqual(qdl.canTranscode({ hash: 'x', state: 'downloading' }), false);   // progress отсутствует
});

test('canTranscode: уже локальный MP4 → false (по флагу и по state)', () => {
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, local: true }), false);
  assert.strictEqual(qdl.canTranscode({ hash: 'x', progress: 1, state: 'local' }), false);
});

test('canTranscode: null/undefined → false без исключений', () => {
  assert.strictEqual(qdl.canTranscode(null), false);
  assert.strictEqual(qdl.canTranscode(undefined), false);
});

// ─────────────────────────────── quickMenu: состав пунктов ───────────────────────────────

function menuItemsFor(item) {
  let captured = null;
  const lampa = H.makeLampa({ Select: { show: (opts) => { captured = opts; } } });
  const { qdl: q } = H.loadQdl({ lampa });
  q.quickMenu(item);
  assert.ok(captured, 'Select.show должен быть вызван');
  return captured.items.map((i) => i.title);
}

test('quickMenu: завершённой раздаче предлагаются транскод и слежение', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mkv', progress: 1, state: 'stalledUP' });
  assert.ok(titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1), 'должен быть пункт транскода');
  assert.ok(titles.some((t) => t.indexOf('Следить за новыми сериями') !== -1), 'должен быть пункт слежения');
  assert.ok(titles.some((t) => t.indexOf('Удалить') !== -1));
});

test('quickMenu: недокачанной раздаче транскод не предлагается', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mkv', progress: 0.4, state: 'downloading' });
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1));
});

test('quickMenu: локальному MP4 — ни транскода, ни слежения, но смотреть/удалить можно', () => {
  const titles = menuItemsFor({ hash: 'x', name: 'Movie.mp4', progress: 1, state: 'local', local: true });
  assert.ok(!titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1), 'транскод уже сделан');
  assert.ok(!titles.some((t) => t.indexOf('серия') !== -1 || t.indexOf('сериями') !== -1), 'слежение бессмысленно без торрента');
  assert.ok(titles.some((t) => t.indexOf('Смотреть') !== -1));
  assert.ok(titles.some((t) => t.indexOf('Удалить') !== -1));
});
