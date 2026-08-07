'use strict';
// Синглтон-гард qdl.js: двойное подключение плагина (нативная оболочка + инжект,
// горячая переподгрузка) не должно создавать второй экземпляр — observers/interval/Listener
// вешаются один раз, повторная загрузка — no-op.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

test('second load of qdl.js into the same window is a no-op', () => {
  const { w } = H.loadQdlDom({ bodyHtml: '<div class="head"><div class="head__actions"></div></div>' });
  assert.strictEqual(w.qdl_plugin_loaded, 1);
  assert.ok(w.__qdl);

  w.__qdl._marker = 'first-instance';
  w.eval(H.qdlSource());   // вторая загрузка того же источника в то же окно

  assert.strictEqual(w.__qdl._marker, 'first-instance', 'second load must not re-create the module');
  assert.strictEqual(w.qdl_plugin_loaded, 1);
});
