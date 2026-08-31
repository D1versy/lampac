'use strict';
// Сторож локализации залипших чужих адресов в localStorage (qdl 2.88, lampainit-invc.js).
//
// Что стережём. Тема и скринсейвер CUB хранятся АБСОЛЮТНЫМ URL, зашитым в момент установки:
// Theme.toggle → Storage.set('cub_theme', 'https://cub.red/extensions/196'). Устройство,
// выбравшее тему до вендоринга 2.17, подключает её <link>-ом с ЧУЖОГО хоста на каждом старте —
// причём нативным тегом, мимо Lampa.Reguest, так что ни request_before, ни cubproxy.js этого
// не видят, и ни одна серверная ручка тут не поможет. Лечится ровно одним местом — этим блоком.
//
// Почему тест по коду, а не «проверим на сервере». Блок срабатывает только у устройства, у
// которого чужое значение УЖЕ лежит; на чистом профиле headless-прогон его не затрагивает —
// то есть живая проверка тут зелёная всегда, и на сломанном коде тоже. Единственный способ
// поймать регрессию — прогнать сам блок на подставном localStorage, что тест и делает.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const H = require('./harness');

const FILE = path.join(H.REPO, 'Modules', 'LampaWeb', 'plugins', 'lampainit-invc.js');
const src = fs.readFileSync(FILE, 'utf8').replace(/\r\n/g, '\n');

// ── вырезаем сам блок и гоняем его в песочнице ───────────────────────────────
// Берём IIFE, начинающийся со строки с ORIGIN: он самодостаточен и ничего, кроме localStorage
// и location, не трогает.
function extractBlock() {
  const start = src.indexOf("var ORIGIN = location.protocol");
  assert.ok(start > 0, 'блок локализации пропал из lampainit-invc.js');

  // от начала IIFE до её конца
  const open = src.lastIndexOf('(function () {', start);
  const end = src.indexOf('})();', start);
  assert.ok(open > 0 && end > open, 'не нашёл границы IIFE блока локализации');

  return src.slice(open, end + 5);
}

function run(seed) {
  const store = Object.assign({}, seed);
  const localStorage = {
    getItem: (k) => (Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null),
    setItem: (k, v) => { store[k] = String(v); },
    removeItem: (k) => { delete store[k]; },
  };

  const ctx = vm.createContext({
    localStorage,
    location: { protocol: 'http:', host: '192.168.87.24:9118' },
    JSON,
    Object,
  });

  vm.runInContext(extractBlock(), ctx, { filename: 'lampainit-invc-block.js' });
  return store;
}

const OUR = 'http://192.168.87.24:9118';

test('тема с чужого хоста переезжает на наш, id сохраняется', () => {
  const s = run({ cub_theme: 'https://cub.red/extensions/196' });
  assert.strictEqual(s.cub_theme, OUR + '/cub/red/extensions/196');
});

test('токен аккаунта в хвосте не уезжает вместе с адресом', () => {
  const s = run({ cub_theme: 'https://cub.best/extensions/212?token=SECRET' });
  assert.strictEqual(s.cub_theme, OUR + '/cub/red/extensions/212');
});

test('скринсейвер лечится так же', () => {
  const s = run({ cub_screensaver: 'https://cub.red/extensions/183' });
  assert.strictEqual(s.cub_screensaver, OUR + '/cub/red/extensions/183');
});

test('наш адрес не трогаем (иначе тема слетала бы каждую загрузку)', () => {
  const url = OUR + '/cub/red/extensions/196';
  const s = run({ cub_theme: url });
  assert.strictEqual(s.cub_theme, url);
});

test('пустое значение остаётся пустым', () => {
  const s = run({ cub_theme: '' });
  assert.strictEqual(s.cub_theme, '');
});

test('чужой адрес без разбираемого id сбрасывается на дефолт, а не остаётся чужим', () => {
  const s = run({ cub_theme: 'https://evil.example/theme.css' });
  assert.strictEqual(s.cub_theme, '');
});

test('плагин с чужого хоста переезжает на наш /ext/<slug>.js', () => {
  const s = run({ plugins: JSON.stringify([{ url: 'https://lampaplugins.github.io/store/rating.js', status: 1 }]) });
  const list = JSON.parse(s.plugins);
  assert.strictEqual(list.length, 1);
  assert.strictEqual(list[0].url, OUR + '/ext/rating.js');
});

test('плагин, из которого имя файла не вынуть, выбрасывается целиком', () => {
  const s = run({ plugins: JSON.stringify([{ url: 'https://evil.example/serve?id=7' }]) });
  assert.deepStrictEqual(JSON.parse(s.plugins), []);
});

test('наши плагины в списке остаются нетронутыми', () => {
  const mine = [{ url: OUR + '/ext/bwa.ad-rc.js', status: 1 }];
  const s = run({ plugins: JSON.stringify(mine) });
  assert.deepStrictEqual(JSON.parse(s.plugins), mine);
});

test('кеш каталога с чужими ссылками выбрасывается', () => {
  const s = run({ account_extensions: JSON.stringify({ results: [{ link: 'https://cub.best/plugin/x.js' }] }) });
  assert.strictEqual(s.account_extensions, undefined);
});

test('кеш каталога с нашими ссылками остаётся', () => {
  const raw = JSON.stringify({ results: [{ link: OUR + '/ext/x.js' }] });
  const s = run({ account_extensions: raw });
  assert.strictEqual(s.account_extensions, raw);
});

test('блок не падает на мусоре в plugins', () => {
  assert.doesNotThrow(() => run({ plugins: 'не json' }));
  assert.doesNotThrow(() => run({ plugins: '{"не":"массив"}' }));
});

test('весь файл остаётся синтаксически валидным JS', () => {
  assert.doesNotThrow(() => new vm.Script(src, { filename: 'lampainit-invc.js' }));
});
