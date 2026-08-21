'use strict';
// Клиентская часть вкладки jut.su (qdl 2.26).
// Инварианты ТВ-интерфейса и почему они важны — E:\Media-server\claude\jut\05-client.md

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

test('пункт меню называется ровно «jut.su» (решение владельца, не «Аниме»)', () => {
  const { qdl } = H.loadQdl();
  const item = qdl.buildJutMenuItem();
  const html = String(item.__html || item.html || '');
  // make$ — заглушка, поэтому проверяем по исходнику: литерал обязан быть именно таким
  const src = H.qdlSource();
  assert.ok(src.includes('<div class="menu__text">jut.su</div>'),
    'текст пункта меню должен быть ровно «jut.su»');
  assert.ok(!src.includes('<div class="menu__text">Аниме</div>'),
    'пункт «Аниме» — остаток раннего черновика, его быть не должно');
});

test('пункт меню идёт последним в MENU_ORDER, сразу за «D1versy Rec» (.qdl-live-menu)', () => {
  const { qdl } = H.loadQdl();
  // Array.from — массив из vm-песочницы живёт в другом realm, и deepStrictEqual ловит чужой прототип
  const order = Array.from(qdl.MENU_ORDER, (x) => x.cls);
  assert.deepStrictEqual(order.slice(-2), ['qdl-live-menu', 'qdl-jut-menu'],
    'jut.su стоит сразу после «D1versy Rec» (имена классов обманчивы: .qdl-live-menu = Rec/записи)');
});

test('jut.su не зависит от прав на Live/Rec — иначе чужой гейт уносил бы вкладку', () => {
  // 🔴 Регресс qdl 2.54: до переписывания цепочки якорей jut.su цеплялся ЗА .qdl-live-menu,
  // и скрытый по правам «D1versy Rec» уносил вкладку аниме вместе с собой.
  const { qdl } = H.loadQdl();
  const jut = qdl.MENU_ORDER.filter((x) => x.cls === 'qdl-jut-menu')[0];
  assert.ok(jut, 'jut.su обязан быть в MENU_ORDER');
  assert.strictEqual(jut.show(), true, 'вкладка jut.su видна всегда, правами не гейтится');
});

test('ключ таймлайна совпадает с серверным tl (прогресс переживает скачивание)', () => {
  const src = H.qdlSource();
  assert.ok(src.includes("'qdltl:jut:' + slug + ':' + key"),
    'ключ обязан строиться как qdltl:jut:<slug>:<epkey> — тот же формат, что отдаёт /qdl/episodes');
});

test('постер и стрим идут ТОЛЬКО через наш сервер', () => {
  const { qdl } = H.loadQdl();
  const url = qdl.jutPosterUrl('spy-family');
  assert.ok(url.includes('/qdl/jut/poster?slug=spy-family'));
  assert.ok(!url.includes('jut.su'), 'клиент не должен ходить на jut.su напрямую');
  assert.ok(!url.includes('yandexwebcache'), 'клиент не должен видеть CDN');

  const src = H.qdlSource();
  assert.ok(!/url:\s*['"]https?:\/\/r\d+\.yandexwebcache/.test(src),
    'прямая ссылка на CDN в плеер попасть не может: hash вяжется с UA → 403');
});

test('плеер запускается только после resolve (сервер выбирает качество)', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function jutPlay');
  assert.ok(i > 0);
  // рамка — до следующей функции того же уровня, а не «i + N символов»:
  // счётчик символов ломался от любой добавленной строки комментария
  const fn = src.slice(i, src.indexOf('\n    function ', i + 10));
  assert.ok(fn.includes('/qdl/jut/resolve'), 'без resolve ссылки нет');
  assert.ok(fn.includes('Lampa.Player.play'), 'после resolve — запуск плеера');
  assert.ok(fn.includes('Lampa.Player.playlist'), 'плейлист сезона даёт автопереход');
  // Автопереход N→N+1 онлайн: /qdl/jut/stream резолвит ссылку ПО ТОКЕНУ, и пустой t=
  // у всех соседних элементов делал автопереход мёртвым (фикс 2.42)
  assert.ok(fn.includes('encodeURIComponent(x.tok)'), 'у соседних серий — свой токен');
  assert.ok(!/stream\?t='\s*\)/.test(fn), 'пустого t= в плейлисте быть не должно');
});

test('названия серий: фильмы и OVA не превращаются в «N серия»', () => {
  const { qdl } = H.loadQdl();
  assert.ok(qdl.jutEpTitle({ kind: 'episode', season: 1, ep: 7 }, '').includes('7 серия'));
  assert.ok(qdl.jutEpTitle({ kind: 'film', season: 1, ep: 3 }, '').includes('3 фильм'));
  assert.ok(qdl.jutEpTitle({ kind: 'ova', season: 1, ep: 2 }, '').includes('OVA 2'));
  // название серии, если сайт его дал
  assert.ok(qdl.jutEpTitle({ kind: 'episode', season: 1, ep: 1, name: 'Операция «Стрикс»' }, '')
    .includes('Операция'));
});

test('ошибки resolve показываются человеческим текстом, а не молчанием', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(
    qdl.jutErrText({ error: 'NOT_AUTHORIZED', message: 'Требуется вход на jut.su — обновите куки на сервере' }),
    'Требуется вход на jut.su — обновите куки на сервере');
  assert.ok(qdl.jutErrText(null).length > 0, 'даже на пустой ответ должен быть текст');
});

test('грид каталога раскладывается как штатный каталог Lampa', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentJutCatalog');
  const fn = src.slice(i, i + 900);
  // Без mapping--grid cols--6 карточки прижимаются влево и справа зияет пустота (§AV п.9)
  assert.ok(fn.includes('mapping--grid cols--6'));
});

test('пустое сообщение получает width:100% (правило .cols--N > *)', () => {
  const src = H.qdlSource();
  // Ищем ВНУТРИ ComponentJutCatalog: правило .cols--N > * — про его грид, а this.empty есть
  // и у других компонентов (лента записей Rec объявлена в файле раньше).
  const cat = src.indexOf('function ComponentJutCatalog');
  const i = src.indexOf('this.empty = function', cat);
  const fn = src.slice(i, i + 300);
  assert.ok(fn.includes('width:100%'),
    'без width:100% сообщение сожмётся до ширины одной карточки');
});

test('дозагруженные карточки регистрируются в коллекции фокуса', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentJutCatalog');
  const fn = src.slice(i, src.indexOf('function ComponentJutTitle'));
  assert.ok(fn.includes('collectionAppend'),
    'без collectionAppend стрелки пульта не дойдут до 2-й страницы: элементы в DOM есть, навигатор о них не знает');
  assert.ok(fn.includes('scroll.onEnd'), 'бесконечная лента вместо кнопок (46 страниц)');
});

test('поиск открывается экранной клавиатурой Lampa', () => {
  const src = H.qdlSource();
  assert.ok(src.includes('Lampa.Input.edit'),
    'ввод текста с пульта — только через штатную клавиатуру');
  assert.ok(src.includes("component: 'jut_catalog', jut_query"));
});

test('экран тайтла даёт все четыре точки входа', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentJutTitle');
  const fn = src.slice(i, src.indexOf('function ComponentJutEpisodes'));
  assert.ok(fn.includes("mkBtn('Смотреть'"));
  assert.ok(fn.includes("mkBtn('Скачать'"));
  assert.ok(fn.includes('JUT_MODE_LABEL'), 'кнопка слежения обязана показывать РЕЖИМ подписки');
  assert.ok(fn.includes('📄 Серии'));
});

// Жалоба владельца (2.32): на экране тайтла постер и текст лежали ВПРИТЫК к краям экрана —
// у экранов jut.su не было горизонтальных полей вообще (замер: left=0, описание во всю ширину).
test('экраны тайтла и серий: контент в контейнере с полями, описание с капом длины строки', () => {
  const src = H.qdlSource();
  for (const name of ['ComponentJutTitle', 'ComponentJutEpisodes']) {
    const i = src.indexOf('function ' + name);
    assert.ok(src.slice(i, i + 900).includes('class="qdl-jut-page"'),
      name + ': контейнер без класса с полями — контент прилипнет к краю');
  }
  const css = src.slice(src.indexOf('function injectCss'), src.indexOf('document.head.appendChild'));
  assert.ok(/\.qdl-jut-page\{padding:0 [0-9.]+em/.test(css), 'поля .qdl-jut-page не заданы');
  assert.ok(/\.qdl-jut-descr\{[^}]*max-width/.test(css), 'строка описания через весь ТВ не читается — нужен кап');
  assert.ok(/@media[^}]*max-width:580px/.test(css), 'на телефоне поля обязаны срезаться (рут-em там клампится)');
});

test('края пульта уводят в меню и шапку из каждого компонента', () => {
  const src = H.qdlSource();
  for (const name of ['ComponentJutCatalog', 'ComponentJutTitle', 'ComponentJutEpisodes']) {
    const i = src.indexOf('function ' + name);
    const fn = src.slice(i, i + 12000);
    assert.ok(fn.includes("Lampa.Controller.toggle('menu')"), name + ': влево — в меню');
    assert.ok(fn.includes("Lampa.Controller.toggle('head')"), name + ': вверх — в шапку');
    assert.ok(fn.includes('Lampa.Activity.backward()'), name + ': назад');
  }
});

test('экран серий обновляет отметки при каждом возврате (в т.ч. из плеера)', () => {
  const src = H.qdlSource();
  const i = src.indexOf('function ComponentJutEpisodes');
  const fn = src.slice(i, i + 6000);
  assert.ok(fn.includes('comp.refreshMarks()'), 'иначе ✓/►N% не обновятся после просмотра');
  assert.ok(fn.includes('collectionFocus(last'), 'возврат из плеера обязан вернуть фокус');
  assert.ok(fn.includes('if (cur && !last)'),
    'стартовый фокус на продолжаемой ставится только если пользователь ещё не выбрал сам');
});

test('оба режима слежения сказаны словами, и «уведомления» не обещают скачивание', () => {
  // 2.35: карточка тайтла = только уведомления, «Загрузки» = ещё и качаем. Пользователь
  // обязан видеть разницу в тексте, а не догадываться.
  const src = H.qdlSource();
  assert.ok(src.includes('Сообщу о новых сериях, качать не буду'), 'нет текста режима «только уведомления»');
  assert.ok(src.includes('Новые серии буду качать сам'), 'нет текста режима «качаю»');
  assert.ok(src.includes('Следить: качать новые серии'), 'в «Загрузках» пункт обязан говорить про скачивание');
});

// ═════════ 2.35: два режима слежения на экране карточки тайтла ═════════
// Требование владельца: «Следить» в КАРТОЧКЕ = только уведомления (серий на диске может
// не быть вовсе). Автоскачивание включается исключительно из «Загрузок».

const JUT_TITLE = {
  ok: true, title: 'Игра лжецов', original: 'Liar Game', count: 2, ongoing: true,
  years: [2026], genres: ['драма'], descr: 'Описание',
  items: [
    { kind: 'episode', season: 1, num: 1, key: 's1e1', url: '/liar-game/season-1/episode-1.html' },
    { kind: 'episode', season: 1, num: 2, key: 's1e2', url: '/liar-game/season-1/episode-2.html' },
  ],
};

// Lampa.Scroll харнесса отдаёт болванки вне DOM — компоненту нужен скролл на настоящем jQuery
function jsdomScroll(w) {
  return function () {
    const render = w.$('<div class="scroll"><div class="scroll__body"></div></div>');
    this.render = () => render;
    this.body = () => render.find('.scroll__body');
    this.append = (el) => render.find('.scroll__body').append(el);
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

// watchList: массив items для /qdl/jut/watch/list, либо null = запрос падает (режим неизвестен)
function cardRig(watchList, respond) {
  const calls = { reqs: [], noty: [], selects: [] };
  const { w, doc, qdl } = H.loadQdlDom({});
  w.Lampa.Scroll = jsdomScroll(w);
  w.Lampa.Noty = { show: (m) => calls.noty.push(String(m)) };
  w.Lampa.Select = { show: (o) => calls.selects.push(o) };
  w.Lampa.Reguest = function () {
    this.timeout = () => {}; this.clear = () => {};
    this.silent = (url, ok, err) => {
      const u = String(url);
      calls.reqs.push(u);
      if (u.indexOf('/qdl/jut/title') !== -1) { ok(JUT_TITLE); return; }
      if (u.indexOf('/qdl/jut/watch/list') !== -1) {
        if (watchList === null) { if (err) err(); return; }
        ok({ ok: true, items: watchList });
        return;
      }
      const h = (respond || (() => undefined))(u);
      if (h !== undefined) ok(h);
    };
  };
  w.fetch = () => Promise.resolve({ json: () => Promise.resolve({}) });

  const comp = new qdl.ComponentJutTitle({ jut_slug: 'liar-game' });
  comp.activity = { loader() {}, toggle() {} };
  w.$('body').append(comp.create());

  const btn = () => [...doc.querySelectorAll('.selector')].filter((b) => b.textContent.indexOf('🔔') !== -1)[0];
  const press = () => w.$(btn()).trigger('hover:enter');
  return { w, doc, calls, btn, press, label: () => btn().textContent.trim() };
}

test('2.35: «Следить» с карточки шлёт autoGrab=0 и НЕ шлёт season', () => {
  const r = cardRig([], () => ({ ok: true, season: 1, mode: 'notify', message: 'Слежу за сезоном 1: сообщу о новых сериях' }));
  assert.strictEqual(r.label(), '🔔 Следить');

  r.press();
  const url = r.calls.reqs[r.calls.reqs.length - 1];
  assert.ok(url.indexOf('/qdl/jut/watch?slug=liar-game') !== -1, 'ушло не туда: ' + url);
  assert.ok(url.indexOf('autoGrab=0') !== -1, 'карточка обязана включать ТОЛЬКО уведомления: ' + url);
  // регресс: раньше уходил season первой серии → у многосезонного тайтла подписывался сезон 1
  assert.ok(url.indexOf('season=') === -1, 'сезон выбирает сервер (последний вышедший): ' + url);
  assert.strictEqual(r.label(), '🔔 Слежу · уведомления');
  assert.ok(r.calls.noty[0].indexOf('сообщу') !== -1, r.calls.noty.join(' | '));
});

test('2.35: карточка честно показывает режим «качаю», поднятый из «Загрузок»', () => {
  const r = cardRig([{ slug: 'liar-game', mode: 'grab', autoGrab: true }]);
  assert.strictEqual(r.label(), '🔔 Слежу · качаю');
});

test('2.35: старый сервер без mode — autoGrab:false читается как «уведомления»', () => {
  const r = cardRig([{ slug: 'liar-game', autoGrab: false }]);
  assert.strictEqual(r.label(), '🔔 Слежу · уведомления');
  const r2 = cardRig([{ slug: 'liar-game', autoGrab: true }]);
  assert.strictEqual(r2.label(), '🔔 Слежу · качаю');
});

test('2.35: в меню карточки НЕТ пункта, поднимающего до автоскачивания', () => {
  // главный инвариант фичи: «качаю» включается только из «Загрузок»
  const r = cardRig([{ slug: 'liar-game', mode: 'grab' }], () => ({ ok: true, mode: 'notify' }));
  r.press();
  const menu = r.calls.selects[r.calls.selects.length - 1];
  const wants = menu.items.map((i) => String(i.want)).join(',');
  assert.strictEqual(wants, 'notify,off,undefined', 'набор пунктов карточки: ' + wants);
  assert.ok(menu.items.every((i) => String(i.title).indexOf('Качать') === -1));
});

test('2.35: понижение «качаю» → «уведомления» идёт через /watch/mode (baseline не сбрасывается)', () => {
  const r = cardRig([{ slug: 'liar-game', mode: 'grab' }], () => ({ ok: true, mode: 'notify', message: 'Только уведомления' }));
  r.press();
  const menu = r.calls.selects[r.calls.selects.length - 1];
  menu.onSelect(menu.items[0]);

  const url = r.calls.reqs[r.calls.reqs.length - 1];
  assert.ok(url.indexOf('/qdl/jut/watch/mode?slug=liar-game') !== -1, url);
  assert.ok(url.indexOf('autoGrab=0') !== -1, url);
  assert.strictEqual(r.label(), '🔔 Слежу · уведомления');
});

test('2.35: ни один запрос экрана тайтла не включает автоскачивание', () => {
  for (const list of [[], [{ slug: 'liar-game', mode: 'notify' }], [{ slug: 'liar-game', mode: 'grab' }]]) {
    const r = cardRig(list, () => ({ ok: true }));
    r.press();
    const menu = r.calls.selects[r.calls.selects.length - 1];
    if (menu) menu.items.filter((i) => i.want).forEach((i) => menu.onSelect(i));
    assert.ok(!r.calls.reqs.some((u) => u.indexOf('autoGrab=1') !== -1),
      'с карточки ушёл autoGrab=1: ' + r.calls.reqs.join(' | '));
  }
});

test('2.35: неизвестный режим не понижает подписку молча — переспрашиваем список', () => {
  // Если /qdl/jut/watch/list не ответил, слепая подписка с autoGrab=0 понизила бы
  // уже качающую подписку и сбросила её baseline.
  let listCalls = 0;
  const calls = { reqs: [] };
  const r = (function () {
    const c = { reqs: [], noty: [], selects: [] };
    const { w, doc, qdl } = H.loadQdlDom({});
    w.Lampa.Scroll = jsdomScroll(w);
    w.Lampa.Noty = { show: (m) => c.noty.push(String(m)) };
    w.Lampa.Select = { show: (o) => c.selects.push(o) };
    w.Lampa.Reguest = function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok, err) => {
        const u = String(url);
        c.reqs.push(u);
        if (u.indexOf('/qdl/jut/title') !== -1) { ok(JUT_TITLE); return; }
        if (u.indexOf('/qdl/jut/watch/list') !== -1) {
          listCalls++;
          if (listCalls === 1) { if (err) err(); return; }       // первый раз падает
          ok({ ok: true, items: [{ slug: 'liar-game', mode: 'grab' }] });   // на второй — «качаю»
          return;
        }
        ok({ ok: true });
      };
    };
    w.fetch = () => Promise.resolve({ json: () => Promise.resolve({}) });
    const comp = new qdl.ComponentJutTitle({ jut_slug: 'liar-game' });
    comp.activity = { loader() {}, toggle() {} };
    w.$('body').append(comp.create());
    const btn = [...doc.querySelectorAll('.selector')].filter((b) => b.textContent.indexOf('🔔') !== -1)[0];
    w.$(btn).trigger('hover:enter');
    return { c, btn };
  })();

  assert.strictEqual(listCalls, 2, 'режим неизвестен — обязан быть повторный запрос списка');
  assert.ok(!r.c.reqs.some((u) => u.indexOf('/qdl/jut/watch?slug=') !== -1),
    'слепая подписка при неизвестном режиме: ' + r.c.reqs.join(' | '));
  assert.strictEqual(r.btn.textContent.trim(), '🔔 Слежу · качаю', 'подпись обязана обновиться на реальный режим');
  assert.ok(r.c.selects.length === 1, 'дальше открывается меню режима, а не подписка');
});

test('2.35: список подписок недоступен дважды — честно говорим, что не знаем', () => {
  const r = cardRig(null, () => ({ ok: true }));
  assert.strictEqual(r.label(), '🔔 Следить');
  r.press();
  assert.ok(r.calls.noty.some((m) => m.indexOf('Не удалось узнать состояние слежения') !== -1),
    r.calls.noty.join(' | '));
  assert.ok(!r.calls.reqs.some((u) => u.indexOf('/qdl/jut/watch?slug=') !== -1),
    'подписка при неизвестном режиме отправлена: ' + r.calls.reqs.join(' | '));
});
