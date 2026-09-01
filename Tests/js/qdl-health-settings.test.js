'use strict';
// qdl 2.39: экран «Хелс-чеки» в настройках Lampa. Регистрируется только по праву «действия»
// (без него раздела нет ни у кого — плитку раздела иначе не спрятать), ровно один раз,
// и обязательно СВОИМ шаблоном ПОСЛЕ addComponent: тот сам кладёт пустой settings_qdl_health
// и перетёр бы нашу вёрстку при обратном порядке.
// ⚠️ qdl 2.89: гейтом была кука qdl_unlock=1, теперь только право — см. qdl-manage-gate.test.js.

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

test('health: без права раздел не регистрируется', () => {
  const { lampa, calls } = settingsLampa();
  const { qdl } = H.loadQdl({ lampa, perms: {} });
  qdl.registerHealthSettings();
  assert.strictEqual(calls.length, 0, 'ни addComponent, ни Template.add не должны вызываться');
});

test('health: с правом раздел регистрируется, Template.add строго ПОСЛЕ addComponent', () => {
  const { lampa, calls } = settingsLampa();
  const { qdl } = H.loadQdl({ lampa });
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
  const { qdl, sandbox } = H.loadQdl({ lampa });
  qdl.registerHealthSettings();
  qdl.registerHealthSettings();
  assert.strictEqual(calls.filter((c) => c[0] === 'addComponent').length, 1);
  assert.strictEqual(sandbox.window.qdl_health_settings, true);
});

test('health: без SettingsApi (старый бандл) не падает и не помечает себя зарегистрированным', () => {
  const lampa = H.makeLampa({ SettingsApi: undefined });
  const { qdl, sandbox } = H.loadQdl({ lampa });
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
  assert.match(qdl.healthRow({ id: 'x', name: 'X', status: 'warn', ms: 0 }), /⚠️/);
});

test('healthRow: неизвестный статус трактуется как сбой, а не как зелень', () => {
  const { qdl } = H.loadQdl();
  // страховка на будущее: лучше лишний раз позвать смотреть, чем показать ✅ у сломанного
  assert.match(qdl.healthRow({ id: 'x', name: 'X', status: 'degraded', ms: 0 }), /❌/);
  assert.match(qdl.healthRow({ id: 'x', name: 'X', ms: 0 }), /❌/);
});

test('healthSummary: считает по четырём статусам, неизвестный — в сбои', () => {
  const { qdl } = H.loadQdl();
  const n = qdl.healthSummary([
    { status: 'ok' }, { status: 'ok' }, { status: 'warn' },
    { status: 'fail' }, { status: 'off' }, { status: 'wat' },
  ]);
  // по полям, а не deepStrictEqual: объект приходит из realm'а jsdom и не reference-equal прототипу
  assert.strictEqual(n.ok, 2);
  assert.strictEqual(n.warn, 1);
  assert.strictEqual(n.fail, 2, 'неизвестный статус обязан считаться сбоем');
  assert.strictEqual(n.off, 1);
});

test('healthSummaryRow: без проблем говорит «всё работает»', () => {
  const { qdl } = H.loadQdl();
  assert.match(qdl.healthSummaryRow({ ok: 15, warn: 0, fail: 0, off: 3 }), /всё работает/);
  assert.match(qdl.healthSummaryRow({ ok: 15, warn: 1, fail: 2, off: 0 }), /❌ 2 не работают/);
  assert.match(qdl.healthSummaryRow({ ok: 15, warn: 0, fail: 1, off: 0 }), /❌ 1 не работает/);
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

/** Экран с заданным списком сервисов: возвращает итоговый HTML и запрошенные URL. */
function renderWith(services) {
  const html = [];
  const urls = [];
  const node = { html: (h) => { if (h !== undefined) html.push(h); }, on: () => node, find: () => node };
  const body = { find: () => node };
  const lampa = H.makeLampa({
    Params: { listener: { _sent: [], send(t) { this._sent.push(t); } } },
    Reguest: function () {
      this.timeout = () => {};
      this.silent = (url, ok) => { urls.push(url); ok({ at: '2026-08-14T00:00:00Z', services: services }); };
    },
  });
  const { qdl } = H.loadQdl({ lampa });
  qdl.renderHealth(body);
  return { html: html[html.length - 1], urls, qdl, body };
}

test('renderHealth: сводка сверху, «Проблемы» первой группой, сбойная строка не исчезает со своего места', () => {
  const { html } = renderWith([
    { id: 'qbit', name: 'qBittorrent', group: 'Инфраструктура', status: 'ok', ms: 5 },
    { id: 'shikimori', name: 'Shikimori', group: 'jut.su', status: 'fail', detail: 'http 403' },
    { id: 'jut-host', name: 'jut.su', group: 'jut.su', status: 'warn', detail: 'прокси-фолбэк' },
  ]);

  const iSummary = html.indexOf('❌ 1 не работает');
  const iBad = html.indexOf('Проблемы');
  const iFirstGroup = html.indexOf('Инфраструктура');
  assert.ok(iSummary >= 0, 'строка-итог обязана быть');
  assert.ok(iBad >= 0 && iBad < iFirstGroup, '«Проблемы» идут перед обычными группами');
  assert.ok(iSummary < iBad, 'итог — выше «Проблем»');

  // сбойные строки — КОПИИ: сервис остаётся и в своей группе, иначе список «съезжает»
  assert.strictEqual(html.split('Shikimori').length - 1, 2);
  assert.strictEqual(html.split('qBittorrent').length - 1, 1, 'здоровые в «Проблемы» не попадают');
});

test('renderHealth: без проблем группы «Проблемы» нет', () => {
  const { html } = renderWith([
    { id: 'qbit', name: 'qBittorrent', group: 'Инфраструктура', status: 'ok', ms: 5 },
    { id: 'ipcam', name: 'IPCamLive', group: 'Инфраструктура', status: 'off', detail: 'не настроено' },
  ]);
  assert.ok(html.indexOf('Проблемы') === -1);
  assert.match(html, /всё работает/);
});

test('renderHealth: quiet-строки красятся, но сводку не засоряют', () => {
  // один вставший планировщик иначе даёт десяток одинаковых ⚠️ и топит настоящую причину
  const { html } = renderWith([
    { id: 'searchmon', name: 'Мониторинг поиска', group: 'Поиск раздач', status: 'warn', detail: 'прогон 10 ч назад' },
    { id: 'tracker:rutor', name: 'Трекер rutor', group: 'Поиск раздач', status: 'warn', detail: 'данные устарели', quiet: true },
  ]);

  const bad = html.slice(html.indexOf('Проблемы'), html.indexOf('Поиск раздач'));
  assert.ok(bad.indexOf('Мониторинг поиска') >= 0, 'причина в сводке нужна');
  assert.ok(bad.indexOf('Трекер rutor') === -1, 'производная строка в сводку не тащится');
  assert.match(html, /⚠️ Трекер rutor/);   // но на своём месте она жёлтая
});

test('renderHealth: первый заход без ?fresh, кнопка «Обновить» — с ним', () => {
  const { urls, qdl, body } = renderWith([{ id: 'qbit', name: 'qBittorrent', group: 'Инфраструктура', status: 'ok' }]);
  assert.match(urls[0], /\/qdl\/health$/, 'открытие экрана не должно обходить кеш сервера');

  qdl.renderHealth(body, true);
  assert.match(urls[urls.length - 1], /\/qdl\/health\?fresh=1$/);
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
