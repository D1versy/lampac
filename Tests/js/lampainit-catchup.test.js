'use strict';
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// ─────────────────────────────────────────────────────────────────────────────
// Догоняющая установка плагинов (qdl 2.83).
//
// 🔥 Что чинилось. upstream ставит плагины ТОЛЬКО при ПЕРВОЙ инициализации устройства: блок с
// plugins_add стоит в lampainit.js НИЖЕ гарда `if (Storage.get('lampac_initiale','false')) return`.
// Значит устройство, заведённое раньше, не получит НИ ОДНОГО нового плагина никогда — что бы
// сервер ни объявлял. Из-за этого мак, Android TV и два айфона остались без timecode.js/
// bookmark.js и не написали на сервер ни строки истории, а «общая история» на них не работала
// в принципе (разбор — Media-server claude/06 §CP).
//
// Здесь под тестом ровно тот сценарий: устройство УЖЕ инициализировано, плагина нет.
// ─────────────────────────────────────────────────────────────────────────────

const SYNC = [
  { url: 'http://h/sync.js', status: 1, name: 'Синхронизация', author: 'lampac' },
  { url: 'http://h/timecode.js', status: 1, name: 'Синхронизация тайм-кодов', author: 'lampac' },
  { url: 'http://h/bookmark.js', status: 1, name: 'Синхронизация закладок', author: 'lampac' },
];

/** Устройство «как мак»: заведено давно, часть плагинов уже стоит. */
function device(installed, initiale) {
  const lampa = H.makeLampa();
  lampa.Plugins._list = (installed || []).slice();
  const { mod } = H.loadLampaInit({ lampa, host: 'http://h', initiale: initiale || SYNC });
  return { mod, lampa };
}

const urls = (lampa) => lampa.Plugins.get().map((p) => p.url);
// catchUpPlugins возвращает массив из ДРУГОГО realm (vm-песочница), и deepStrictEqual
// сравнивает прототипы — без переноса в свой realm падают даже одинаковые пустые массивы.
const arr = (x) => Array.from(x || []);

test('устройство без плагинов синка получает их', () => {
  const { mod, lampa } = device([]);
  const pushed = mod.catchUpPlugins();

  assert.deepStrictEqual(arr(pushed), ['http://h/sync.js', 'http://h/timecode.js', 'http://h/bookmark.js']);
  assert.deepStrictEqual(urls(lampa), arr(pushed));
  // недостающее обязано быть не только записано, но и ЗАГРУЖЕНО — иначе оно заработает
  // лишь со следующего запуска приложения
  assert.deepStrictEqual(lampa.Utils._scripts.map(arr), [arr(pushed)]);
  assert.ok(lampa.Plugins._saves >= 1, 'список расширений не сохранён — пропадёт при перезапуске');
});

test('повторный вызов не плодит дублей и ничего не грузит (идемпотентность)', () => {
  const { mod, lampa } = device([]);
  mod.catchUpPlugins();
  const after = urls(lampa).slice();
  const loads = lampa.Utils._scripts.length;

  assert.deepStrictEqual(arr(mod.catchUpPlugins()), []);
  assert.deepStrictEqual(urls(lampa), after);
  assert.strictEqual(lampa.Utils._scripts.length, loads);
});

test('доставляется ТОЛЬКО недостающее', () => {
  const { mod, lampa } = device([SYNC[0]]);           // sync.js уже стоит
  assert.deepStrictEqual(arr(mod.catchUpPlugins()), ['http://h/timecode.js', 'http://h/bookmark.js']);
  assert.strictEqual(urls(lampa).length, 3);
});

test('чужие плагины устройства не трогаются', () => {
  const mine = { url: 'http://h/my-own.js', status: 1, name: 'своё', author: 'user' };
  const { mod, lampa } = device([mine]);
  mod.catchUpPlugins();
  assert.ok(urls(lampa).indexOf('http://h/my-own.js') >= 0, 'чужой плагин пропал');
  assert.strictEqual(urls(lampa).length, 4);
});

test('пустой серверный список — ничего не ставим', () => {
  const { mod, lampa } = device([], []);
  assert.deepStrictEqual(arr(mod.catchUpPlugins()), []);
  assert.deepStrictEqual(urls(lampa), []);
});

test('битые записи списка пропускаются, остальные ставятся', () => {
  const { mod, lampa } = device([], [null, {}, { url: '' }, SYNC[1]]);
  assert.deepStrictEqual(arr(mod.catchUpPlugins()), ['http://h/timecode.js']);
  assert.strictEqual(urls(lampa).length, 1);
});

test('appready доставляет плагины сам', () => {
  // 🔴 Вызов обязан стоять в appready: он выполняется на КАЖДОМ старте, в отличие от блока
  // upstream, запертого гардом первой инициализации.
  const { mod, lampa } = device([]);
  mod.appready();
  assert.deepStrictEqual(urls(lampa), SYNC.map((p) => p.url));
});

test('гард первой инициализации не трогается', () => {
  // Догоняющая установка НЕ должна сбрасывать lampac_initiale: иначе upstream заново
  // переписал бы пользовательские настройки (source, качество, poster_size…).
  const { mod, lampa } = device([]);
  lampa.Storage.set('lampac_initiale', 'true');
  mod.catchUpPlugins();
  assert.strictEqual(lampa.Storage.get('lampac_initiale', 'false'), 'true');
});

test('сломанный Lampa.Plugins не роняет старт приложения', () => {
  const lampa = H.makeLampa();
  lampa.Plugins = undefined;
  const { mod } = H.loadLampaInit({ lampa, host: 'http://h', initiale: SYNC });
  assert.deepStrictEqual(arr(mod.catchUpPlugins()), []);
  assert.doesNotThrow(() => mod.appready());
});
