'use strict';

// Канарейка гейта: при D1V_SELFCHECK=1 падает намеренно.
// Доказывает, что сьюта способна покраснеть — молча «зелёный» прогон,
// который на самом деле не запустился, хуже отсутствия тестов.

const test = require('node:test');
const assert = require('node:assert');

test('сьюта умеет краснеть (канарейка -SelfCheck)', () => {
  assert.notStrictEqual(process.env.D1V_SELFCHECK, '1',
    'канарейка -SelfCheck: сьюта форка (JS) умеют краснеть');
});
