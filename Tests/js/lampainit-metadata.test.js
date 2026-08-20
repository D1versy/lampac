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
//
// qdl 2.53: сюда же добавились blacklist (сетевой шаг последовательной очереди старта ради
// заведомо пустого ответа) и reactions (запрос на скрытый CSS-ом интерфейс, в Status(9) карточки).

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

test('lampainit-invc: blacklist и reactions выключены (qdl 2.53)', () => {
  // blacklist: loadBlackList — сетевой шаг ПОСЛЕДОВАТЕЛЬНОЙ очереди старта ради заведомо
  //   пустого ответа (у нас заглушка CubProxy отдаёт []).
  // reactions: интерфейс реакций и так скрыт CSS, а запрос с таймаутом 5 с уходил на каждое
  //   открытие карточки и входил в Status(9), которого ждёт экран карточки.
  // Оба ставим ПОВЕРХ пришедших значений — как раз затем и ставим.
  const { sandbox } = load({ blacklist: false, reactions: false });
  const df = sandbox.window.lampa_settings.disable_features;
  assert.strictEqual(df.blacklist, true);
  assert.strictEqual(df.reactions, true);
});

test('lampainit-invc: чужие выключатели не задеты', () => {
  // трогаем ровно четыре ключа (subscribe/metadata/blacklist/reactions) — всё остальное,
  // что налил бандл или lampainit.js, обязано дойти до Lampa как есть.
  const { sandbox } = load({ discuss: false, persons: false, trailers: false, lgbt: true });
  const df = sandbox.window.lampa_settings.disable_features;
  assert.strictEqual(df.subscribe, true);
  assert.strictEqual(df.metadata, true);
  assert.strictEqual(df.discuss, false, 'discuss выключать не собирались');
  assert.strictEqual(df.persons, false, 'persons выключать не собирались');
  assert.strictEqual(df.trailers, false, 'trailers выключать не собирались');
  assert.strictEqual(df.lgbt, true);
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
