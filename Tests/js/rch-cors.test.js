'use strict';
// Сторож правки upstream-файла Core/plugins/invc-rch_nws.js (qdl 2.39).
//
// Апстрим определял «умеет ли клиент CORS» запросом на https://github.com/ — в браузере проба
// ВСЕГДА падала (красная ошибка в консоли на каждом старте), а вердикт был известен заранее.
// Мы заменили её на мгновенный check() без сети. Файл инлайнится в online.js, sisi.js и
// /invc-ws.js, причём последний AppReplace не покрывает — поэтому правка живёт в исходнике,
// а этот тест ловит её тихий откат при ребейзе с upstream (sync-скрипт гоняет тесты между
// rebase и build).

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const H = require('./harness');

const FILE = path.join(H.REPO, 'Core', 'plugins', 'invc-rch_nws.js');
const src = fs.readFileSync(FILE, 'utf8').replace(/\r\n/g, '\n');

test('rch-cors: маркер патча на месте', () => {
  assert.ok(src.includes('/*qdl-cut:rch-cors*/'),
    'правка потерялась (ребейз?) — вернуть: if (true) check(Lampa.Platform.is(\'android\') || Lampa.Platform.is(\'tizen\'));/*qdl-cut:rch-cors*/');
});

test('rch-cors: голого upstream-якоря больше нет', () => {
  assert.ok(!src.includes("if (Lampa.Platform.is('android') || Lampa.Platform.is('tizen')) check(true);"),
    'исходная ветка вернулась — проба github.com снова стреляет');
});

test('rch-cors: форма if сохранена (иначе else повисает без if = SyntaxError)', () => {
  const line = src.split('\n').find((l) => l.includes('/*qdl-cut:rch-cors*/'));
  assert.ok(/^\s*if \(true\) check\(/.test(line), 'патч обязан начинаться с if (true) check(');
  assert.ok(/else \{/.test(src.split('/*qdl-cut:rch-cors*/')[1].slice(0, 40)),
    'мёртвая else-ветка остаётся целой — так патч переживает ребейз с минимальным диффом');
});

test('rch-cors: файл остаётся синтаксически валидным JS', () => {
  // vm.Script парсит без исполнения — ловит именно dangling else
  const vm = require('vm');
  assert.doesNotThrow(() => new vm.Script(src, { filename: 'invc-rch_nws.js' }));
});

test('rch-cors: вердикт совпадает со старым поведением на всех платформах', () => {
  const vm = require('vm');
  // вырезаем тело typeInvoke и прогоняем его с моками — проверяем, что тип не изменился
  for (const [platform, expect] of [['android', 'apk'], ['tizen', 'cors'], ['browser', 'web']]) {
    const sandbox = {
      window: { rch_nws: {} },
      location: { host: '192.168.87.24:9118' },
      Lampa: {
        Platform: { is: (p) => p === platform },
        Reguest: function () { this.silent = () => { throw new Error('сети быть не должно'); }; },
      },
      console,
    };
    sandbox.window.location = sandbox.location;
    vm.createContext(sandbox);
    vm.runInContext(src.replace(/\{localhost\}/g, 'http://192.168.87.24:9118'), sandbox, { filename: 'rch.js' });

    const key = Object.keys(sandbox.window.rch_nws)[0];
    let called = false;
    sandbox.window.rch_nws[key].typeInvoke('http://192.168.87.24:9118', () => { called = true; });
    assert.strictEqual(sandbox.window.rch_nws[key].type, expect, `${platform} → ${expect}`);
    assert.ok(called, 'колбэк должен вызваться синхронно, без сети');
  }
});
