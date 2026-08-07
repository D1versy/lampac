'use strict';
// Изоляция трафика клиента (qdl 2.19, фаза 1B) + клиентские части уведомлений.
//
// Клиент D1Vision обязан ходить ТОЛЬКО на наш сервер. Тут проверяем четыре вещи:
//  И1.1/И1.3 — lampainit-invc гасит WebSocket на cub.rip (socket_use/socket_methods) и services;
//  И1.2      — скринсейвер выключен ДО старта Lampa (иначе простой = запросы на GitHub/чужой CDN);
//  И1.6      — DMCA-список (/blocked) запрашивается по локальному URL, а не по внешнему tmdb.<cub>;
//  М1.4/М1.5 — уведомление kind=START и мгновенный пуш опроса по DOM-событию 'lwsEvent'.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// ═══════════════════════ И1.1 / И1.3: lampa_settings ═══════════════════════

test('И1.1: lampainit гасит socket_use и socket_methods (WebSocket на cub.rip)', () => {
  const settings = { disable_features: {} };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.strictEqual(sandbox.window.lampa_settings.socket_use, false);
  assert.strictEqual(sandbox.window.lampa_settings.socket_methods, false);
});

test('И1.3: lampainit гасит services (сторонний JS /plugin/* в нашем origin)', () => {
  const settings = { disable_features: {} };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.strictEqual(sandbox.window.lampa_settings.services, false);
});

test('И1.1/И1.3: новые флаги не вытеснили прежние (mirrors/geo/subscribe)', () => {
  const settings = { disable_features: {} };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.strictEqual(sandbox.window.lampa_settings.mirrors, false, 'регрессия 2.15');
  assert.strictEqual(sandbox.window.lampa_settings.geo, false, 'регрессия 2.15');
  assert.strictEqual(sandbox.window.lampa_settings.disable_features.subscribe, true);
});

test('И1.1: значения, уже стоявшие в lampa_settings, перезаписываются в false', () => {
  // бандл наливает дефолты через Arrays.extend БЕЗ replase (пишет только в undefined) —
  // но если кто-то (оболочка, чужой плагин) выставил true раньше нас, мы обязаны его пересилить
  const settings = { disable_features: {}, socket_use: true, socket_methods: true, services: true };
  const { sandbox } = H.loadLampaInit({ windowExtra: { lampa_settings: settings } });
  assert.strictEqual(sandbox.window.lampa_settings.socket_use, false);
  assert.strictEqual(sandbox.window.lampa_settings.socket_methods, false);
  assert.strictEqual(sandbox.window.lampa_settings.services, false);
});

// ═══════════════════════ И1.2: скринсейвер ═══════════════════════

test('И1.2: скринсейвер выключен до старта Lampa (screensaver=false)', () => {
  const { localStorage } = H.loadLampaInit();
  // Storage.get отдельно разбирает 'true'/'false' в булево → гейт
  // `if (!Storage.field('screensaver')) return;` в Screensaver.resetTimer сработает
  assert.strictEqual(localStorage.getItem('screensaver'), 'false');
});

test('И1.2: screensaver_type уведён с дефолтного aerial (GitHub + чужой CDN по http:)', () => {
  const { localStorage } = H.loadLampaInit();
  assert.strictEqual(localStorage.getItem('screensaver_type'), 'cub');
});

test('И1.2: форс переживает перезапуск — включённый руками тумблер снова гаснет', () => {
  const ls = H.makeStorage();
  ls.setItem('screensaver', 'true');       // пользователь включил в настройках
  ls.setItem('screensaver_type', 'aerial');
  H.loadLampaInit({ localStorage: ls });
  assert.strictEqual(ls.getItem('screensaver'), 'false');
  assert.strictEqual(ls.getItem('screensaver_type'), 'cub');
});

test('И1.2: идемпотентно — повторная загрузка не меняет уже проставленные значения', () => {
  const ls = H.makeStorage();
  H.loadLampaInit({ localStorage: ls });
  const first = [ls.getItem('screensaver'), ls.getItem('screensaver_type')];
  H.loadLampaInit({ localStorage: ls });
  assert.deepStrictEqual([ls.getItem('screensaver'), ls.getItem('screensaver_type')], first);
});

test('И1.2: без window.lampa_settings форс скринсейвера всё равно проходит (отдельный try/catch)', () => {
  const { localStorage } = H.loadLampaInit();   // lampa_settings нет → блок настроек падает и глотается
  assert.strictEqual(localStorage.getItem('screensaver'), 'false');
  assert.strictEqual(localStorage.getItem('proxy_tmdb'), 'true', 'соседний форс не задет');
});

test('версия форка бампнута минимум до 2.19 (маркер актуальности кода у клиента)', () => {
  const { sandbox } = H.loadLampaInit();
  const v = String(sandbox.window.qdl_fork_version || '');
  const m = v.match(/^(\d+)\.(\d+)$/);
  assert.ok(m, 'версия должна иметь вид <major>.<minor>, получено: ' + v);
  // сравнение по номеру, а не по литералу: иначе тест приходится править на каждый бамп
  // (правило владельца — бампать минор при КАЖДОМ фиксе клиентских плагинов)
  const num = Number(m[1]) * 100 + Number(m[2]);
  assert.ok(num >= 219, 'изоляция клиента приехала в 2.19, версия не должна откатываться: ' + v);
});

// ═══════════════════════ И1.6: DMCA-список без похода наружу ═══════════════════════

// Reguest-мок, копящий URL запросов
function netRig(respond) {
  const reqs = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, cb, err) => {
        reqs.push(String(url));
        const r = respond ? respond(String(url)) : undefined;
        if (r === 'ERR') { if (err) err(); }
        else if (r !== undefined && cb) cb(r);
      };
    },
  });
  return { lampa, reqs };
}

test('И1.6: /blocked запрашивается локально, без внешнего tmdb.<cub>', () => {
  const { lampa, reqs } = netRig((u) => (u.indexOf('/blocked') !== -1 ? [] : undefined));
  const { qdl } = H.loadQdl({ lampa });
  qdl.whenDmca(() => {});
  assert.deepStrictEqual(reqs, ['{localhost}/cub/tmdb.cub.rip/blocked']);
  reqs.forEach((u) => assert.ok(!/^https?:\/\//.test(u), 'ни один запрос не абсолютный внешний: ' + u));
});

test('И1.6: запомненное зеркало CUB попадает в путь, но адрес остаётся локальным', () => {
  const { lampa, reqs } = netRig((u) => (u.indexOf('/blocked') !== -1 ? [] : undefined));
  const { qdl } = H.loadQdl({ lampa, windowExtra: { qdl_cub_domain: 'mirror-kurwa.men' } });
  qdl.whenDmca(() => {});
  assert.deepStrictEqual(reqs, ['{localhost}/cub/tmdb.mirror-kurwa.men/blocked']);
});

test('И1.6: локальный /blocked не попадает под наш же XHR-переписыватель (петли нет)', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(qdl.rewriteCubUrl('{localhost}/cub/tmdb.cub.rip/blocked'), null);
  assert.strictEqual(qdl.rewriteWatchUrl('{localhost}/cub/tmdb.cub.rip/blocked'), null);
});

test('И1.6: в исходнике qdl.js не осталось кода, строящего внешний tmdb.<cub>-URL', () => {
  // фолбэк req() уходит голым fetch(url) мимо request_before/cubproxy.js — любая внешняя
  // строка тут означала бы утечку даже при исправном основном пути
  const code = H.qdlSource().split('\n').filter((l) => l.trim().indexOf('//') !== 0).join('\n');
  assert.strictEqual(/['"]https?:\/\/(?:tmdb|cub)\./.test(code), false,
    'найдена литеральная внешняя ссылка на cub/tmdb вне комментариев');
});

// ═══════════════════════ М1.4: уведомление kind=START ═══════════════════════

// Lampa-мок, где Reguest.silent копит запросы, не отвечая — ответ дёргается вручную
function pollRig(noty) {
  const pending = [];
  const lampa = H.makeLampa({ Noty: { show: (m) => noty.push(m) } });
  lampa.Reguest = function () {
    this.timeout = () => {};
    this.clear = () => {};
    this.silent = (url, cb, err) => pending.push({ url, cb, err });
  };
  return { lampa, pending };
}

test('М1.4: одиночный START — свой тост, без «скачана»', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { qdl } = H.loadQdl({ lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);      // не первый опрос → тосты разрешены

  qdl.pollNotifications();
  pending[0].cb({ unread: 1, items: [{ id: 6, kind: 'START', title: 'Сериал А', label: 'Сезон 1 · серия 3' }] });

  assert.strictEqual(noty.length, 1);
  assert.ok(noty[0].indexOf('Сериал А') !== -1 && noty[0].indexOf('серия 3') !== -1, noty[0]);
  assert.ok(noty[0].indexOf('скачана') === -1, 'START — это НЕ готовая серия');
  assert.ok(noty[0].indexOf('⏬') === 0, 'своя иконка: ' + noty[0]);
});

test('М1.4: пачка START агрегируется в один тост', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { qdl } = H.loadQdl({ lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);

  qdl.pollNotifications();
  pending[0].cb({ unread: 3, items: [
    { id: 6, kind: 'START', title: 'А', label: 'E1' },
    { id: 7, kind: 'START', title: 'Б', label: 'E2' },
    { id: 8, kind: 'START', title: 'В', label: 'E3' },
  ] });

  assert.strictEqual(noty.length, 1);
  assert.strictEqual(noty[0], '⏬ Начата загрузка серий: 3');
  assert.strictEqual(lampa.Storage.get('qdl_noti_lastid'), 8);
});

test('М1.4: START не смешивается ни со «скачано», ни со SWITCH/INFO', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { qdl } = H.loadQdl({ lampa });
  lampa.Storage.set('qdl_noti_lastid', 5);

  qdl.pollNotifications();
  pending[0].cb({ unread: 5, items: [
    { id: 6, kind: 'SWITCH', title: 'А', label: 'полнее' },
    { id: 7, kind: 'INFO',   title: 'Б', label: 'что-то' },
    { id: 8, kind: 'START',  title: 'В', label: 'E1' },
    { id: 9, kind: 'START',  title: 'Г', label: 'E2' },
    { id: 10,                title: 'Д', label: 'E3' },      // обычная «серия скачана»
  ] });

  assert.strictEqual(noty.length, 3, 'три независимые корзины: special / START / скачанные');
  assert.strictEqual(noty[0], '🔔 Новых уведомлений: 2');
  assert.strictEqual(noty[1], '⏬ Начата загрузка серий: 2');
  assert.ok(noty[2].indexOf('скачана') !== -1, noty[2]);
});

test('М1.4: на самом первом опросе (lastid=0) START тоже не спамит историей', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { qdl } = H.loadQdl({ lampa });

  qdl.pollNotifications();
  pending[0].cb({ unread: 2, items: [{ id: 6, kind: 'START', title: 'А', label: 'E1' }] });
  assert.strictEqual(noty.length, 0);
  assert.strictEqual(lampa.Storage.get('qdl_noti_lastid'), 6, 'точка отсчёта запомнена');
});

// ═══════════════════════ М1.5: мгновенный пуш по 'lwsEvent' ═══════════════════════

test('М1.5: событие lwsEvent name=qdl_noti дёргает опрос уведомлений', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { doc } = H.loadQdlDom({ lampa });

  const ev = new doc.defaultView.CustomEvent('lwsEvent', { detail: { uid: 'x', name: 'qdl_noti', data: '' } });
  doc.dispatchEvent(ev);

  assert.strictEqual(pending.length, 1, 'опрос запущен без ожидания 90-секундного тика');
  assert.ok(pending[0].url.indexOf('/qdl/notifications') !== -1, pending[0].url);
});

test('М1.5: чужие имена событий синка игнорируются', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { doc } = H.loadQdlDom({ lampa });
  const W = doc.defaultView;

  doc.dispatchEvent(new W.CustomEvent('lwsEvent', { detail: { name: 'sync_storage', data: 'recomends_list' } }));
  doc.dispatchEvent(new W.CustomEvent('lwsEvent', { detail: { name: 'system', data: 'connected' } }));
  doc.dispatchEvent(new W.CustomEvent('lwsEvent', {}));                       // без detail — не падаем
  doc.dispatchEvent(new W.CustomEvent('lwsEvent', { detail: { data: 'x' } })); // без name — не падаем

  assert.strictEqual(pending.length, 0);
});

test('М1.5: пуш не ломает in-flight защиту — пачка событий даёт один запрос', () => {
  const noty = [];
  const { lampa, pending } = pollRig(noty);
  const { doc } = H.loadQdlDom({ lampa });
  const W = doc.defaultView;
  const push = () => doc.dispatchEvent(new W.CustomEvent('lwsEvent', { detail: { name: 'qdl_noti' } }));

  push(); push(); push();
  assert.strictEqual(pending.length, 1, 'notiPollBusy держит замок');

  pending[0].cb({ items: [], unread: 0 });   // ответ пришёл → замок снят
  push();
  assert.strictEqual(pending.length, 2);
});

test('М1.5: 90-секундный опрос оставлен фолбэком на случай разрыва сокета', () => {
  assert.ok(/setInterval\(pollNotifications,\s*90000\)/.test(H.qdlSource()),
    'резервный интервал опроса пропал — при мёртвом WS уведомления перестанут приходить вовсе');
});
