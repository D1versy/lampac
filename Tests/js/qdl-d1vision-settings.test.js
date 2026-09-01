'use strict';
// qdl 2.89: раздел «D1Vision» в настройках Lampa — глобальный фильтр каталога по году.
//
// 🔴 Настройка ОБЩАЯ на весь сервер: меняешь на одном устройстве — применяется всем. Поэтому
// раздел гейтится правом «действия» (manage) из /admin/d1v, а сервер отдаёт 403 на запись всем
// остальным. Сокрытие раздела защитой не считается — оно только чтобы не рисовать отказную кнопку.
//
// ⚠️ Разметку раздел строит РУКАМИ, а не через Lampa.SettingsApi.addParam: типа 'toggle' у
// addParam не существует (валидны select/trigger/input/title/static/button), и поле с ним молча
// не отрисовывается.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const FILTER = { ver: 1, enabled: false, movieYear: 2020, tvYear: 2010 };

/** Lampa с записью вызовов SettingsApi/Template + подставным ответом /qdl/catalog-filter. */
function settingsLampa(filter, over, video) {
  const calls = [];
  const lampa = H.makeLampa(Object.assign({
    SettingsApi: {
      addComponent: (d) => calls.push(['addComponent', d]),
      addParam: (d) => calls.push(['addParam', d]),
    },
    Template: { add: (n) => calls.push(['Template.add', n]), get: () => H.makeEl() },
    Settings: { listener: { _subs: [], follow(t, fn) { if (t === 'open') this._subs.push(fn); } } },
    Params: { listener: { _sent: [], send(t) { this._sent.push(t); } } },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok, err) => {
        if (url.indexOf('/qdl/live/video') !== -1) { if (video !== undefined) ok(video); return; }
        if (url.indexOf('/qdl/catalog-filter') !== -1) { if (filter) ok(filter); else if (err) err(); }
      };
    },
  }, over || {}));
  return { lampa, calls };
}

/** Узел-приёмник html + сбор обработчиков по data-action (их вешает paint). */
function makeList() {
  const handlers = {};
  const html = [];
  const node = {
    html: (h) => { if (h !== undefined) html.push(h); return node; },
    on: () => node,
    find: (sel) => {
      const m = /\[data-action="([^"]+)"\]/.exec(sel);
      const key = m ? m[1] : sel;
      return {
        on: (ev, fn) => { handlers[key] = fn; },
        html: () => {},
      };
    },
  };
  return { node, html, handlers, body: { find: () => node } };
}

// ─────────────────────────── регистрация раздела ───────────────────────────

test('d1vision: без права раздел не регистрируется', () => {
  const { lampa, calls } = settingsLampa(FILTER);
  const { qdl } = H.loadQdl({ lampa, perms: {} });
  qdl.registerD1VisionSettings();
  assert.strictEqual(calls.length, 0, 'ни addComponent, ни Template.add не должны вызываться');
});

test('d1vision: с правом раздел регистрируется, Template.add строго ПОСЛЕ addComponent', () => {
  const { lampa, calls } = settingsLampa(FILTER);
  const { qdl } = H.loadQdl({ lampa });
  qdl.registerD1VisionSettings();

  const iAdd = calls.findIndex((c) => c[0] === 'addComponent');
  const iTpl = calls.findIndex((c) => c[0] === 'Template.add' && c[1] === 'settings_qdl_d1vision');
  assert.ok(iAdd >= 0, 'addComponent должен быть вызван');
  assert.ok(iTpl >= 0, 'свой шаблон должен быть добавлен');
  assert.ok(iTpl > iAdd, 'иначе addComponent перетрёт шаблон пустым div');
  assert.strictEqual(calls[iAdd][1].component, 'qdl_d1vision');
  assert.strictEqual(calls[iAdd][1].name, 'D1Vision');
});

test('d1vision: повторный вызов не дублирует регистрацию', () => {
  const { lampa, calls } = settingsLampa(FILTER);
  const { qdl, sandbox } = H.loadQdl({ lampa });
  qdl.registerD1VisionSettings();
  qdl.registerD1VisionSettings();
  assert.strictEqual(calls.filter((c) => c[0] === 'addComponent').length, 1);
  assert.strictEqual(sandbox.window.qdl_d1v_settings, true);
});

test('d1vision: без SettingsApi (старый бандл) не падает и не помечает себя зарегистрированным', () => {
  const lampa = H.makeLampa({ SettingsApi: undefined });
  const { qdl, sandbox } = H.loadQdl({ lampa });
  assert.doesNotThrow(() => qdl.registerD1VisionSettings());
  assert.ok(!sandbox.window.qdl_d1v_settings);
});

// ─────────────────────────────── отрисовка ───────────────────────────────

test('d1vision: рисует три строки значениями с сервера', () => {
  const { lampa } = settingsLampa({ ver: 1, enabled: true, movieYear: 2021, tvYear: 2012 });
  const { qdl } = H.loadQdl({ lampa });
  const L = makeList();

  qdl.renderD1Vision(L.body);

  const last = L.html[L.html.length - 1];
  assert.match(last, /Фильтр по году выпуска/);
  assert.match(last, /Включён/);
  assert.match(last, /2021/);
  assert.match(last, /2012/);
  assert.match(last, /применяется ко всем устройствам|общая для всех устройств/,
    'пользователь обязан видеть, что настройка не личная');
});

test('d1vision: выключенный фильтр показан как «Выключен», а не пустым значением', () => {
  const { lampa } = settingsLampa(FILTER);
  const { qdl } = H.loadQdl({ lampa });
  const L = makeList();
  qdl.renderD1Vision(L.body);
  assert.match(L.html[L.html.length - 1], /Выключен/);
});

test('d1vision: сервер недоступен — честная ошибка, а не пустой экран', () => {
  const { lampa } = settingsLampa(null);   // silent зовёт err()
  const { qdl } = H.loadQdl({ lampa });
  const L = makeList();
  qdl.renderD1Vision(L.body);
  assert.match(L.html[L.html.length - 1], /❌/);
});

test('d1vRow: значения экранируются (сервер отдаёт произвольный текст)', () => {
  const { qdl } = H.loadQdl();
  const html = qdl.d1vRow('enabled', '<script>', '<b>', '<i>');
  assert.ok(html.indexOf('<script>') === -1);
  assert.ok(html.indexOf('<b>') === -1);
  assert.ok(html.indexOf('<i>') === -1);
});

// ─────────────────────────────── запись ───────────────────────────────

test('🔴 d1vision: переключение шлёт POST с uid — без него сервер отказал бы 403 даже с грантом', async () => {
  const fetches = [];
  const { lampa } = settingsLampa(FILTER);
  lampa.Storage.set('lampac_unic_id', 'sajnp6ml');

  const { qdl } = H.loadQdl({
    lampa,
    fetch: (url, init) => {
      fetches.push({ url: String(url), body: (init && init.body) || '' });
      return Promise.resolve({ json: () => Promise.resolve({ success: true, filter: { ver: 1, enabled: true, movieYear: 2020, tvYear: 2010 } }) });
    },
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.enabled();
  await new Promise((r) => setImmediate(r));

  assert.strictEqual(fetches.length, 1);
  assert.match(fetches[0].url, /\/qdl\/catalog-filter/);
  assert.match(fetches[0].url, /uid=sajnp6ml/, 'RequestInfo.getuid читает ТОЛЬКО query');
  // тумблер инвертирует текущее значение, годы едут как есть
  assert.match(fetches[0].body, /enabled=true/);
  assert.match(fetches[0].body, /movieYear=2020/);
  assert.match(fetches[0].body, /tvYear=2010/);
});

test('🔴 d1vision: отказ сервера показан ПРИЧИНОЙ — отозванное право иначе читается как поломка', async () => {
  const notys = [];
  const { lampa } = settingsLampa(FILTER, { Noty: { show: (m) => notys.push(m) } });

  const { qdl } = H.loadQdl({
    lampa,
    fetch: () => Promise.resolve({ json: () => Promise.resolve({ error: 'нет права управления' }) }),
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.enabled();
  await new Promise((r) => setImmediate(r));

  assert.ok(notys.some((m) => /нет права управления/.test(m)),
    'причина отказа обязана доехать до пользователя: ' + JSON.stringify(notys));
});

test('d1vision: успешная запись перерисовывает раздел ответом сервера', async () => {
  const { lampa } = settingsLampa(FILTER);
  const { qdl } = H.loadQdl({
    lampa,
    fetch: () => Promise.resolve({
      json: () => Promise.resolve({ success: true, filter: { ver: 1, enabled: true, movieYear: 2020, tvYear: 2010 } }),
    }),
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  assert.match(L.html[L.html.length - 1], /Выключен/);

  L.handlers.enabled();
  await new Promise((r) => setImmediate(r));

  assert.match(L.html[L.html.length - 1], /Включён/, 'перерисовка обязана идти от ОТВЕТА сервера');
});

test('🔴 d1vision: мусор вместо года не уходит на сервер', async () => {
  const fetches = [];
  const notys = [];
  const edits = [];
  const { lampa } = settingsLampa(FILTER, {
    Noty: { show: (m) => notys.push(m) },
    Input: { edit: (o, cb) => { edits.push(o); cb('не год'); } },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
  });

  const { qdl } = H.loadQdl({
    lampa,
    fetch: (url, init) => { fetches.push(String(url)); return Promise.resolve({ json: () => Promise.resolve({ success: true }) }); },
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.movieYear();
  await new Promise((r) => setImmediate(r));

  assert.strictEqual(fetches.length, 0, 'сервер не должен был увидеть этот запрос');
  assert.ok(notys.some((m) => /год/i.test(m)), 'пользователю сказали, что не так');
});

test('d1vision: год из ввода уходит числом', async () => {
  const fetches = [];
  const { lampa } = settingsLampa(FILTER, {
    Input: { edit: (o, cb) => cb('2024') },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
  });

  const { qdl } = H.loadQdl({
    lampa,
    fetch: (url, init) => { fetches.push((init && init.body) || ''); return Promise.resolve({ json: () => Promise.resolve({ success: true }) }); },
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.tvYear();
  await new Promise((r) => setImmediate(r));

  assert.strictEqual(fetches.length, 1);
  assert.match(fetches[0], /tvYear=2024/);
  assert.match(fetches[0], /movieYear=2020/, 'соседнее поле обязано уехать неизменным');
});

// ───────────────────── эфир: глобальный тумблер (qdl 2.96) ─────────────────────
//
// Владелец: «настройки глобальны для всех — я у себя включил, и оно на все девайсы».
// Значение серверное (live-video.json), едет клиенту ключом live.video в /qdl/features
// и правится отсюда. Раньше это была кнопка на самом экране эфира и Lampa.Storage.

test('эфир: строка рисуется значением из карты прав', () => {
  const { lampa } = settingsLampa(FILTER);
  const { qdl } = H.loadQdl({ lampa });
  qdl.setCard({ live: { video: false } });

  const L = makeList();
  qdl.renderD1Vision(L.body);

  const html = L.html[L.html.length - 1];
  assert.match(html, /Видео в плитках камер/);
  assert.match(html, /Выключено/);
  assert.match(html, /общая для всех устройств/, 'пользователь обязан видеть, что настройка не личная');
  assert.match(html, /iPhone/, 'и что на айфоне это ограничение системы, а не поломка');
});

test('эфир: ответ сервера уточняет строку, даже если карта прав устарела', () => {
  // Другое устройство выключило эфир минуту назад — /qdl/features ещё не перечитан.
  const { lampa } = settingsLampa(FILTER, null, { video: false });
  const { qdl } = H.loadQdl({ lampa });
  qdl.setCard({ live: { video: true } });

  const L = makeList();
  qdl.renderD1Vision(L.body);

  assert.match(L.html[L.html.length - 1], /Выключено/, 'показываем правду сервера, а не кеш');
});

test('🔴 эфир: переключение шлёт POST на /qdl/live/video С uid — иначе гейт «действий» откажет', async () => {
  const fetches = [];
  const { lampa } = settingsLampa(FILTER, null, { video: true });
  lampa.Storage.set('lampac_unic_id', 'sajnp6ml');

  const { qdl } = H.loadQdl({
    lampa,
    fetch: (url, init) => {
      fetches.push({ url: String(url), body: (init && init.body) || '' });
      return Promise.resolve({ json: () => Promise.resolve({ success: true, video: false }) });
    },
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.liveVideo();
  await new Promise((r) => setImmediate(r));

  assert.strictEqual(fetches.length, 1);
  assert.match(fetches[0].url, /\/qdl\/live\/video/);
  assert.match(fetches[0].url, /uid=sajnp6ml/, 'RequestInfo.getuid читает ТОЛЬКО query');
  assert.match(fetches[0].body, /on=false/, 'тумблер инвертирует текущее значение');
});

test('эфир: успешная запись сразу отражается и в строке, и в карте прав', async () => {
  const { lampa } = settingsLampa(FILTER, null, { video: true });
  const { qdl } = H.loadQdl({
    lampa,
    fetch: () => Promise.resolve({ json: () => Promise.resolve({ success: true, video: false }) }),
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  assert.match(L.html[L.html.length - 1], /Включено/);

  L.handlers.liveVideo();
  await new Promise((r) => setImmediate(r));

  assert.match(L.html[L.html.length - 1], /Выключено/, 'строка перерисована');
  // 🔴 Зеркало в карте прав — то, по чему экран эфира гасит плееры БЕЗ похода на сервер.
  assert.strictEqual(qdl.liveVideoGlobal(), false);
});

test('эфир: отказ сервера показан причиной', async () => {
  const notys = [];
  const { lampa } = settingsLampa(FILTER, { Noty: { show: (m) => notys.push(m) } }, { video: true });

  const { qdl } = H.loadQdl({
    lampa,
    fetch: () => Promise.resolve({ json: () => Promise.resolve({ error: 'нет права управления' }) }),
  });

  const L = makeList();
  qdl.renderD1Vision(L.body);
  L.handlers.liveVideo();
  await new Promise((r) => setImmediate(r));

  assert.ok(notys.some((m) => /нет права управления/.test(m)), JSON.stringify(notys));
  assert.strictEqual(qdl.liveVideoGlobal(), true, 'отказ не должен менять значение у клиента');
});
