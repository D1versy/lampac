'use strict';
// qdl 2.39: экран «Хелс-чеки» в настройках Lampa. Регистрируется только при куке qdl_unlock=1
// (без неё раздела нет ни у кого — плитку раздела иначе не спрятать), ровно один раз,
// и обязательно СВОИМ шаблоном ПОСЛЕ addComponent: тот сам кладёт пустой settings_qdl_health
// и перетёр бы нашу вёрстку при обратном порядке.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

/** Lampa с записью вызовов SettingsApi/Template/Settings.listener. */
function settingsLampa(over) {
  const calls = [];
  const lampa = H.makeLampa(Object.assign({
    SettingsApi: {
      addComponent: (d) => calls.push(['addComponent', d]),
      addParam: (d) => calls.push(['addParam', d]),
    },
    Template: { add: (n) => calls.push(['Template.add', n]), get: () => H.makeEl() },
    Settings: { listener: { _subs: [], follow(t, fn) { if (t === 'open') this._subs.push(fn); } } },
    Params: { listener: { _sent: [], send(t) { this._sent.push(t); } } },
  }, over || {}));
  return { lampa, calls };
}

test('health: без куки раздел не регистрируется', () => {
  const { lampa, calls } = settingsLampa();
  const { qdl } = H.loadQdl({ lampa, cookie: '' });
  qdl.registerHealthSettings();
  assert.strictEqual(calls.length, 0, 'ни addComponent, ни Template.add не должны вызываться');
});

test('health: с кукой раздел регистрируется, Template.add строго ПОСЛЕ addComponent', () => {
  const { lampa, calls } = settingsLampa();
  const { qdl } = H.loadQdl({ lampa, cookie: 'qdl_unlock=1' });
  qdl.registerHealthSettings();

  const iAdd = calls.findIndex((c) => c[0] === 'addComponent');
  const iTpl = calls.findIndex((c) => c[0] === 'Template.add' && c[1] === 'settings_qdl_health');
  assert.ok(iAdd >= 0, 'addComponent должен быть вызван');
  assert.ok(iTpl >= 0, 'свой шаблон должен быть добавлен');
  assert.ok(iTpl > iAdd, 'иначе addComponent перетрёт шаблон пустым div');
  assert.strictEqual(calls[iAdd][1].component, 'qdl_health');
});

test('health: повторный вызов не дублирует регистрацию', () => {
  const { lampa, calls } = settingsLampa();
  const { qdl, sandbox } = H.loadQdl({ lampa, cookie: 'qdl_unlock=1' });
  qdl.registerHealthSettings();
  qdl.registerHealthSettings();
  assert.strictEqual(calls.filter((c) => c[0] === 'addComponent').length, 1);
  assert.strictEqual(sandbox.window.qdl_health_settings, true);
});

test('health: без SettingsApi (старый бандл) не падает и не помечает себя зарегистрированным', () => {
  const lampa = H.makeLampa({ SettingsApi: undefined });
  const { qdl, sandbox } = H.loadQdl({ lampa, cookie: 'qdl_unlock=1' });
  assert.doesNotThrow(() => qdl.registerHealthSettings());
  assert.ok(!sandbox.window.qdl_health_settings);
});

// ─────────────────────────────── строки отчёта ───────────────────────────────

test('healthRow: маркер по статусу, мс и detail', () => {
  const { qdl } = H.loadQdl();
  const ok = qdl.healthRow({ id: 'qbit', name: 'qBittorrent', status: 'ok', ms: 12, detail: 'v5.0' });
  assert.match(ok, /✅/);
  assert.match(ok, /qBittorrent/);
  assert.match(ok, /12 мс/);
  assert.match(ok, /v5\.0/);

  assert.match(qdl.healthRow({ id: 'x', name: 'X', status: 'fail', ms: 0 }), /❌/);
  assert.match(qdl.healthRow({ id: 'x', name: 'X', status: 'off', ms: 0 }), /⏸/);
});

test('healthRow: имя и detail экранируются (сервер отдаёт произвольный текст)', () => {
  const { qdl } = H.loadQdl();
  const html = qdl.healthRow({ id: 'x', name: '<script>', status: 'fail', ms: 0, detail: '<b>' });
  assert.ok(html.indexOf('<script>') === -1, 'имя должно быть экранировано');
  assert.ok(html.indexOf('<b>') === -1, 'detail должен быть экранирован');
});

test('renderHealth: группирует сервисы и просит пересчитать скролл', () => {
  const html = [];
  const node = { html: (h) => { if (h !== undefined) html.push(h); }, on: () => node, find: () => node };
  const body = { find: () => node };
  const lampa = H.makeLampa({
    Params: { listener: { _sent: [], send(t) { this._sent.push(t); } } },
    Reguest: function () {
      this.timeout = () => {};
      this.silent = (url, ok) => {
        assert.match(url, /\/qdl\/health$/);
        ok({
          at: '2026-08-12T00:00:00Z',
          services: [
            { id: 'qbit', name: 'qBittorrent', group: 'Инфраструктура', status: 'ok', ms: 5 },
            { id: 'tmdb-api', name: 'TMDB API', group: 'Метаданные', status: 'fail', ms: 2500, detail: 'таймаут' },
          ],
        });
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa });
  qdl.renderHealth(body);

  const last = html[html.length - 1];
  assert.match(last, /Инфраструктура/);
  assert.match(last, /Метаданные/);
  assert.match(last, /✅ qBittorrent/);
  assert.match(last, /❌ TMDB API/);
  assert.match(last, /Обновить/);
  assert.ok(lampa.Params.listener._sent.includes('update_scroll'), 'после вставки нужен пересчёт скролла');
});

test('renderHealth: недоступный /qdl/health показывает ошибку, а не пустой экран', () => {
  const html = [];
  const node = { html: (h) => { if (h !== undefined) html.push(h); }, on: () => node, find: () => node };
  const body = { find: () => node };
  const lampa = H.makeLampa({
    Params: { listener: { send() {} } },
    Reguest: function () { this.timeout = () => {}; this.silent = (url, ok, err) => err(); },
  });
  const { qdl } = H.loadQdl({ lampa });
  qdl.renderHealth(body);
  assert.match(html[html.length - 1], /недоступен/);
});
