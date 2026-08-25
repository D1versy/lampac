'use strict';
// qdl 2.67: право «Управление» (manage) — ВТОРОЙ ключ к тому же скрытому функционалу, что и кука
// qdl_unlock=1, но выдаваемый конкретному устройству в админке /admin/d1v. Кука живёт только в
// браузере владельца (он сеет её скриптом в консоли), приложения иметь её не могут — поэтому до
// 2.67 транскод, удаление с файлами, коллекции и «Хелс-чеки» были доступны исключительно из веба.
//
// 🔴 Единая точка гейта в UI — qdlManage() = qdlUnlocked() || qdlAllowed('manage'). Самый дешёвый
// способ сломать фичу при следующей правке — оставить где-то прямой qdlUnlocked(); это ловит
// инвариант по исходнику в конце файла.
//
// 🔴 Как и с Live/Rec, здесь только ОТРИСОВКА: настоящий замок стоит на сервере (мутации отвечают
// 403 с причиной). Подделка прав в localStorage даёт максимум видимый пункт, который откажет.
//
// Ветка куки (мастер-ключ владельца) сторожится отдельно — qdl-unlock-gate.test.js.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const tick = () => new Promise((r) => setImmediate(r));

// ─────────────────────────────── qdlManage ───────────────────────────────

test('qdlManage: кука открывает управление и без прав с сервера', () => {
  const { qdl } = H.loadQdl({ cookie: 'qdl_unlock=1' });
  qdl.setPerms(null);
  assert.strictEqual(qdl.qdlManage(), true);
});

test('qdlManage: право manage открывает управление БЕЗ куки — ради этого фича и делалась', () => {
  const { qdl } = H.loadQdl({ cookie: '' });
  qdl.setPerms({ manage: true });
  assert.strictEqual(qdl.qdlManage(), true);
  assert.strictEqual(qdl.qdlUnlocked(), false, 'кука тут ни при чём — у приложения её нет и быть не может');
});

test('qdlManage: ни куки, ни права → false (и явный manage:false тоже false)', () => {
  const { qdl } = H.loadQdl({ cookie: '' });
  qdl.setPerms(null);
  assert.strictEqual(qdl.qdlManage(), false);
  qdl.setPerms({ manage: false });
  assert.strictEqual(qdl.qdlManage(), false);
});

test('qdlManage: соседние права управление НЕ открывают', () => {
  const { qdl } = H.loadQdl({ cookie: '' });
  qdl.setPerms({ live: true, rec: true });
  assert.strictEqual(qdl.qdlManage(), false, 'эфир и записи — не управление, наборы прав независимы');
  assert.strictEqual(qdl.qdlAllowed('live'), true, 'а сами по себе они выданы — набор не сломан');
});

test('право приезжает обычным /qdl/features и переживает перезапуск через кеш Lampa.Storage', () => {
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        if (url.indexOf('/qdl/features') !== -1) ok({ uid: 'dueq3shm', features: { manage: true } });
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa, cookie: '' });
  qdl.loadFeatures();
  assert.strictEqual(qdl.qdlManage(), true);
  assert.deepStrictEqual(lampa.Storage.get('qdl_features', null), { manage: true },
    'без кеша следующий старт нарисовал бы урезанное меню до ответа сервера');
});

// ─────────────────────────────── quickMenu ───────────────────────────────

const TORRENT = { hash: 'a1', name: 'Movie.mkv', progress: 1, state: 'stalledUP' };
const COL = { id: 'c'.repeat(33), title: 'Дюна', cover: 'a1', hashes: ['a1'] };

/** Пункты quickMenu ВСЕГДА без куки — здесь проверяется именно ветка права. */
function menuTitles(opts) {
  opts = opts || {};
  let captured = null;
  const lampa = H.makeLampa({ Select: { show: (o) => { captured = o; } } });
  const { qdl } = H.loadQdl({ lampa, cookie: '' });
  qdl.setPerms(opts.perms || null);
  qdl.quickMenu(opts.item || TORRENT, opts.ctx);
  assert.ok(captured, 'Select.show должен быть вызван');
  return captured.items.map((i) => i.title);
}

test('quickMenu с правом и без куки: транскод, удаление и коллекции на месте (главный сценарий приложений)', () => {
  const titles = menuTitles({ perms: { manage: true } });
  assert.ok(titles.some((t) => t.indexOf('Транскодировать в MP4') !== -1), 'транскод');
  assert.ok(titles.some((t) => t.indexOf('Удалить (с файлами)') !== -1), 'удаление');
  assert.ok(titles.some((t) => t.indexOf('Добавить в коллекцию') !== -1), 'коллекции');
});

test('quickMenu без права и без куки: три пункта управления скрыты, а СОСЕДИ по меню целы', () => {
  // регресс-страж: гейт обязан унести ровно три пункта. Соседи (карточка, оффлайн-просмотр,
  // озвучка, слежение за сериями) — обычный функционал, он положен всем.
  const titles = menuTitles({ perms: { live: true, rec: true } });
  for (const gated of ['Транскодировать', 'Удалить', 'коллекци'])
    assert.ok(!titles.some((t) => t.indexOf(gated) !== -1), 'должно быть под замком: ' + gated + ' | ' + titles.join(' / '));
  for (const kept of ['Открыть карточку', 'Смотреть (оффлайн)', 'Озвучка', 'Следить за новыми сериями'])
    assert.ok(titles.some((t) => t.indexOf(kept) !== -1), 'гейт унёс соседа по меню: ' + kept + ' | ' + titles.join(' / '));
});

test('quickMenu в под-гриде коллекции: без права нет «Убрать из коллекции», с правом есть', () => {
  const off = menuTitles({ ctx: { collection: COL } });
  assert.ok(!off.some((t) => t.indexOf('Убрать из коллекции') !== -1), 'мутация коллекции — тоже управление');
  assert.ok(!off.some((t) => t.indexOf('Добавить в коллекцию') !== -1), 'и подмены на «Добавить» быть не должно');

  const on = menuTitles({ ctx: { collection: COL }, perms: { manage: true } });
  assert.ok(on.some((t) => t.indexOf('Убрать из коллекции') !== -1));
  assert.ok(!on.some((t) => t.indexOf('Добавить в коллекцию') !== -1), 'в под-гриде «Добавить» не место и с правом');
});

test('HEVC-подсказка про транскод следует за правом, а не только за кукой', () => {
  // подсказка честна ровно тогда, когда пункт транскода реально есть в меню
  const noties = [];
  let select = null;
  const lampa = H.makeLampa({
    Noty: { show: (t) => noties.push(String(t)) },
    Select: { show: (o) => { select = o; } },
    Reguest: function () {
      this.timeout = () => {};
      this.silent = (url, ok) => { ok([{ title: 'Movie 2160p', codec: 'hevc', magnet: 'magnet:?xt=1' }]); };
    },
  });
  const { qdl } = H.loadQdl({ lampa, cookie: '' });
  qdl.setPerms({ manage: true });
  qdl.chooseAndDownload({ title: 'Movie', media_type: 'movie' });
  assert.ok(select, 'список раздач должен показаться');
  select.onSelect(select.items[0]);
  assert.ok(noties.some((t) => t.indexOf('Транскодировать в MP4') !== -1), noties.join(' | '));
});

test('«Хелс-чеки» регистрируются по праву — без него раздела в настройках нет', () => {
  const calls = [];
  const mk = () => H.makeLampa({
    SettingsApi: { addComponent: (d) => calls.push(d.component), addParam: () => {} },
    Template: { add: () => {}, get: () => H.makeEl() },
    Settings: { listener: { follow() {} } },
  });
  const off = H.loadQdl({ lampa: mk(), cookie: '' });
  off.qdl.setPerms({ live: true });
  off.qdl.registerHealthSettings();
  assert.deepStrictEqual(calls, [], 'без права раздел не регистрируется');

  const on = H.loadQdl({ lampa: mk(), cookie: '' });
  on.qdl.setPerms({ manage: true });
  on.qdl.registerHealthSettings();
  assert.deepStrictEqual(calls, ['qdl_health'], 'с правом — регистрируется без всякой куки');
});

// ─────────────────────────── applySettingsLock (DOM) ───────────────────────────
// Здесь нужен НАСТОЯЩИЙ DOM (jsdom): узел снимается через node.parentNode.removeChild,
// а у makeDocument-заглушки parentNode нет — снятие молча провалилось бы в try/catch.

/** qdl.js в jsdom, по умолчанию без куки. */
function domLock(opts) {
  opts = opts || {};
  const r = H.loadQdlDom({ cookie: opts.cookie || '' });
  r.qdl.setPerms(opts.perms || null);
  return r;
}

/** Ровно то, что синхронно ставит lampainit-invc.js ещё до загрузки qdl.js. */
function seedLock(doc) {
  const st = doc.createElement('style');
  st.id = 'qdl-hide-settings';
  st.textContent = '.head__action.open--settings{display:none!important}'
                 + '.menu__item[data-action="settings"],.menu__item[data-action="console"]{display:none!important}';
  doc.head.appendChild(st);
  return st;
}

test('applySettingsLock: без права ставит узел #qdl-hide-settings в head', () => {
  const { doc, qdl } = domLock({ perms: { live: true } });   // соседнее право замок не снимает
  qdl.applySettingsLock();
  const node = doc.getElementById('qdl-hide-settings');
  assert.ok(node, 'замок обязан стоять');
  assert.strictEqual(node.tagName, 'STYLE');
  assert.strictEqual(node.parentNode, doc.head, 'узел живёт в head — там же, где его ставит lampainit');
  assert.match(node.textContent, /\.head__action\.open--settings\{display:none!important\}/);
  assert.match(node.textContent, /\.menu__item\[data-action="settings"\]/);
  assert.match(node.textContent, /\.menu__item\[data-action="console"\]/);
  // 🔴 Плитка «Хелс-чеки» — тем же замком: Lampa.SettingsApi снять компонент не умеет,
  // поэтому при отзыве права раздел остаётся зарегистрированным и без этого правила доживал бы
  // до перезапуска (регрессию поймал боевой permsgate на фазе «отозвали»).
  assert.match(node.textContent, /\[data-component="qdl_health"\]\{display:none!important\}/,
    'замок обязан прятать и плитку раздела «Хелс-чеки»');
});

test('applySettingsLock: право снимает узел, который синхронно поставил lampainit-invc.js', () => {
  const { doc, qdl } = domLock({ perms: { manage: true } });
  seedLock(doc);
  qdl.applySettingsLock();
  assert.strictEqual(doc.getElementById('qdl-hide-settings'), null, 'право есть — замок снят целиком');
  assert.strictEqual(doc.head.querySelector('#qdl-hide-settings'), null, 'и из head тоже');
});

test('applySettingsLock: идемпотентен в обе стороны — узлов не плодит и снимает начисто', () => {
  const { doc, qdl } = domLock({});
  qdl.applySettingsLock();
  qdl.applySettingsLock();
  qdl.applySettingsLock();
  assert.strictEqual(doc.querySelectorAll('#qdl-hide-settings').length, 1,
    'ровно один узел: функцию зовёт и старт, и таймер раз в минуту');

  qdl.setPerms({ manage: true });
  qdl.applySettingsLock();
  qdl.applySettingsLock();
  assert.strictEqual(doc.querySelectorAll('#qdl-hide-settings').length, 0);
});

test('applySettingsLock: отзыв права на ЖИВОМ клиенте возвращает замок', () => {
  // права перечитываются раз в минуту — отзыв обязан доехать до открытого приложения сам,
  // иначе «убрал грант» работало бы только после перезапуска
  const { doc, qdl } = domLock({ perms: { manage: true } });
  qdl.applySettingsLock();
  assert.strictEqual(doc.getElementById('qdl-hide-settings'), null, 'сначала право есть — замка нет');

  qdl.setPerms({ manage: false });
  qdl.applySettingsLock();
  const node = doc.getElementById('qdl-hide-settings');
  assert.ok(node, 'право отозвали — замок обязан вернуться');
  assert.match(node.textContent, /open--settings/);
});

test('applySettingsLock: кука снимает замок и без права — страховка от самозапирания', () => {
  const { doc, qdl } = domLock({ cookie: 'qdl_unlock=1' });
  seedLock(doc);
  qdl.setPerms({ manage: false });
  qdl.applySettingsLock();
  assert.strictEqual(doc.getElementById('qdl-hide-settings'), null,
    'если потеряется access.json, войти в настройки владелец должен по куке');
});

// ─────────────────────────────── uid в мутациях ───────────────────────────────

test('🔴 POST-мутация коллекции несёт uid — без него сервер отказал бы 403 даже устройству с грантом', async () => {
  const selects = [];
  const fetches = [];
  const lampa = H.makeLampa({
    Select: { show: (o) => selects.push(o) },
    Noty: { show() {} },
    Activity: { push() {}, replace() {}, backward() {}, active: () => ({}) },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
  });
  lampa.Storage.set('lampac_unic_id', 'dueq3shm');
  const { qdl } = H.loadQdl({
    lampa,
    cookie: '',
    fetch: (url, init) => {
      fetches.push({ url: String(url), body: (init && init.body) || '' });
      return Promise.resolve({ json: () => Promise.resolve({ success: true }) });
    },
  });
  qdl.setPerms({ manage: true });

  qdl.quickMenu(TORRENT, { collection: COL });
  const menu = selects[selects.length - 1];
  const item = menu.items.filter((i) => i.title.indexOf('Убрать') !== -1)[0];
  assert.ok(item, 'с правом пункт «Убрать из коллекции» обязан быть');
  menu.onSelect(item);
  await tick(); await tick();

  assert.strictEqual(fetches.length, 1, 'мутация должна уйти ровно одна');
  assert.ok(fetches[0].url.indexOf('/qdl/collections/remove') !== -1, fetches[0].url);
  assert.ok(/[?&]uid=dueq3shm(&|$)/.test(fetches[0].url),
    'uid обязан быть в QUERY: RequestInfo.getuid тело формы не читает — ' + fetches[0].url);
});

// ─────────────────────────── инварианты по исходнику ───────────────────────────

test('🔴 инвариант: post() дописывает uid ДО отправки', () => {
  // Самая дорогая ошибка этой фичи: гейт пропустил, а сервер ответил 403, потому что uid ушёл
  // только в теле формы. Коллекции сломались бы разом у всех, включая владельца.
  const src = H.qdlSource();
  const i = src.indexOf('function post(url, data, cb, err)');
  assert.ok(i > 0, 'post() должен найтись в исходнике — иначе инвариант проверяет пустоту');
  const body = src.slice(i, src.indexOf('function esc(', i));
  const uid = body.indexOf('url = withUid(url);');
  const send = body.indexOf('fetch(url,');
  assert.ok(uid !== -1, 'в post() обязана быть строка url = withUid(url)');
  assert.ok(send !== -1, 'и сама отправка');
  assert.ok(uid < send, 'uid дописывается ДО fetch, иначе строка бессмысленна');
});

test('🔴 инвариант: UI гейтится qdlManage(), а qdlUnlocked() зовётся только из неё', () => {
  const src = H.qdlSource();
  const bad = src.split('\n').filter((l) => l.indexOf('qdlUnlocked()') !== -1
    && l.indexOf('function qdlUnlocked') === -1
    && l.indexOf('function qdlManage') === -1);
  assert.deepStrictEqual(bad, [],
    'прямой вызов qdlUnlocked() мимо qdlManage() — гейт разъехался, устройство с грантом снова\n'
    + 'увидит урезанный UI:\n' + bad.join('\n'));

  // И сами точки гейта на месте: пропасть они могут только вместе с фичей.
  // ⚠️ Якоря нарочно РЕГЕКСЫ, а не точные строки: два из них уже ломались на безобидном
  // переформатировании (слияние двух соседних if, добавленная проверка node.parentNode),
  // и сторож краснел там, где поведение было целым.
  for (const anchor of [
    /if \(canTranscode\(t\) && qdlManage\(\)\)/,
    /if \(qdlManage\(\)\) \{[\s\S]{0,400}🗑 Удалить \(с файлами\)/,   // пункт внутри блока гейта
    /if \(qdlManage\(\)\) el\.on\('hover:long'/,
    /if \(window\.qdl_health_settings \|\| !qdlManage\(\)\) return;/,
    /if \(qdlManage\(\)\) \{ if \(node/,                                    // applySettingsLock
  ]) assert.ok(anchor.test(src), 'потерялась точка гейта: ' + anchor);
});
