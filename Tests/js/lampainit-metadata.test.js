'use strict';
// qdl 2.45: выключение CUB AI-метаданных на клиенте.
//
// Замер: /cub/*/api/ai/metadata/<id>/<method> отвечает 500 «Метаданные не найдены» на 6 из 6
// тайтлов (премиум-фича чужого аккаунта), 60–212 мс, изредка 4 с. Запрос уходил на КАЖДОЕ
// открытие карточки при русской локали и не кешировался ни на сервере (StaticacheWriter режет
// TTL не-200 до минуты), ни на клиенте (у metadataGet нет params.cache).
//
// Гейт в бандле: `if (window.lampa_settings.disable_features.metadata) return oncomplite({})`,
// то есть результат ровно тот же, что даёт error-путь — только мгновенно и без сети.
// ⚠️ Ключа metadata НЕТ в дефолтах lampainit.js (там перечислены dmca/ads/reactions/discuss/ai/…),
// поэтому он был undefined → falsy → запрос уходил всегда.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function load(seed) {
  return H.loadLampaInit({
    windowExtra: { lampa_settings: { disable_features: seed || {} } },
  });
}

test('lampainit-invc: disable_features.metadata выставляется в true', () => {
  const { sandbox } = load();
  assert.strictEqual(sandbox.window.lampa_settings.disable_features.metadata, true);
});

test('lampainit-invc: соседние выключатели не задеты', () => {
  // subscribe наш (тоже true), а reactions обязан остаться как был — реакции работают и кешируются
  const { sandbox } = load({ reactions: false });
  const df = sandbox.window.lampa_settings.disable_features;
  assert.strictEqual(df.subscribe, true);
  assert.strictEqual(df.reactions, false, 'реакции выключать не собирались');
});

test('lampainit-invc: отсутствие lampa_settings не роняет загрузку', () => {
  // блок обёрнут в try/catch — порядок инициализации на разных клиентах не гарантирован
  const { mod } = H.loadLampaInit();
  assert.strictEqual(typeof mod.appload, 'function');
});

test('lampainit-invc: версия форка забампана (маркер актуальности кода у клиента)', () => {
  const { sandbox } = load();
  const v = String(sandbox.window.qdl_fork_version || '');
  assert.ok(/^\d+\.\d+$/.test(v), 'версия должна быть вида X.Y, получено: ' + v);

  const [major, minor] = v.split('.').map(Number);
  assert.ok(major > 2 || (major === 2 && minor >= 45),
    'правило владельца: при каждом фиксе клиентских плагинов бампать минор; ожидалось ≥ 2.45, получено ' + v);
});
