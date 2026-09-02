'use strict';
// D1versy Live → Detection (ComponentLiveDetect, qdl 2.95): лента скриншотов детектора.
//
// Главное, что здесь защищается:
//  1. курсор подгрузки берётся из ОТВЕТА сервера (r.cursor), а не как «минимальный показанный id»:
//     в режиме дня страница может целиком выпасть за окно локальных суток, и считать курсор
//     было бы не по чему — лента вставала бы намертво;
//  2. плитки просят уменьшёнку (w=640), а полноэкранный просмотр — оригинал: кадр весит ~340 КБ,
//     а в гриде их десятки;
//  3. просмотр живёт оверлеем и обязан уехать вместе с экраном (pause/destroy).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function evt(id, over) {
  return Object.assign({
    id, camera: 3, cameraName: 'Garage 2', type: 'human', confidence: 79,
    time: '18:36:24', day: '2026-09-01', dayLabel: 'Сегодня', recording: 0, thumb: true,
  }, over || {});
}

const PAGE1 = { items: [evt(100), evt(99), evt(98, { day: '2026-08-31', dayLabel: 'Вчера' })],
                hasNext: true, cursor: 98, today: '2026-09-01',
                cameras: [{ id: 3, name: 'Garage 2' }, { id: 5, name: 'Garage 1' }] };

function domScroll(w) {
  return function () {
    const root = w.document.createElement('div');
    const bodyEl = w.document.createElement('div');
    root.appendChild(bodyEl);
    this.render = () => w.$(root);
    this.body = () => w.$(bodyEl);
    this.append = (x) => { w.$(bodyEl).append(x); };
    this.minus = () => {};
    this.update = () => {};
    this.destroy = () => {};
  };
}

function mount(opts) {
  opts = opts || {};
  const calls = { urls: [], plays: [], selects: [], notys: [], appended: [], foreign: false };
  const pages = opts.pages || [PAGE1];
  let page = 0;

  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/live/detect') === -1 || u.indexOf('/thumb') !== -1) return;
        calls.urls.push(u);
        const p = pages[Math.min(page, pages.length - 1)];
        page++;
        ok(typeof p === 'function' ? p(u) : p);
      };
    },
    Player: { play: (x) => calls.plays.push(x), playlist: () => {} },
    Select: { show: (o) => { calls.selects.push(o); }, listener: { follow() {}, send() {} } },
    Noty: { show: (t) => calls.notys.push(t) },
    Controller: {
      add(name, o) { calls.ctrl = o; },
      toggle() {}, collectionSet() {}, collectionFocus() {},
      // Живой own(): «активен ли контроллер, зарегистрированный ЭТИМ экраном» — по нему
      // Lampa отличает свой экран от чужого (Controller.own → active.link == link).
      own: (link) => !calls.foreign && !!calls.ctrl && calls.ctrl.link === link,
      collectionAppend: (el) => calls.appended.push(el),
    },
    Layer: { visible() {}, update() {} },
  });

  const r = H.loadQdlDom({ lampa, perms: opts.perms });
  r.lampa.Scroll = domScroll(r.w);

  const inst = new r.qdl.ComponentLiveDetect({});
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  inst.start();

  return { r, inst, calls, root: inst.render(), body: r.$(r.w.document.body) };
}

const cards = (m) => m.root.find('.qdl-det-card');

// ─────────────────────────── лента ───────────────────────────

test('лента: карточка на событие, разделитель на смену дня', () => {
  const m = mount();
  assert.strictEqual(cards(m).length, 3);
  assert.strictEqual(m.root.find('.qdl-det-day').length, 2, 'два дня — два разделителя');
  assert.ok(m.root.text().indexOf('ЧЕЛОВЕК') !== -1 && m.root.text().indexOf('79%') !== -1);
  assert.ok(m.root.text().indexOf('Garage 2') !== -1 && m.root.text().indexOf('18:36:24') !== -1);
  m.inst.destroy();
});

test('плитки просят уменьшёнку, а не оригинал', () => {
  const m = mount();
  const src = m.r.$(cards(m).find('img')[0]).attr('src');
  assert.ok(src.indexOf('/qdl/live/detect/thumb?w=640&id=100') !== -1, 'w=640 в плитке: ' + src);
  m.inst.destroy();
});

test('подгрузка идёт по курсору СЕРВЕРА, а не по минимальному показанному id', () => {
  const m = mount({ pages: [PAGE1, { items: [evt(50)], hasNext: false, cursor: 50 }] });
  m.inst.load(false);
  assert.strictEqual(m.calls.urls.length, 2);
  assert.ok(m.calls.urls[1].indexOf('before=98') !== -1, 'курсор из ответа: ' + m.calls.urls[1]);
  assert.strictEqual(cards(m).length, 4);
  m.inst.destroy();
});

test('дубли между страницами не рисуются дважды', () => {
  const m = mount({ pages: [PAGE1, { items: [evt(99), evt(50)], hasNext: false, cursor: 50 }] });
  m.inst.load(false);
  assert.strictEqual(cards(m).length, 4, 'повторившееся событие не задвоилось');
  m.inst.destroy();
});

test('пустая страница в режиме дня тянет следующую, но не бесконечно', () => {
  const empty = { items: [], hasNext: true, cursor: 10, today: '2026-09-01' };
  const m = mount({ pages: [empty, empty, empty, empty, empty, empty, empty, empty] });
  // первая страница пустая → добор, но не больше пяти подряд
  assert.ok(m.calls.urls.length >= 2, 'добор был');
  assert.ok(m.calls.urls.length <= 6, 'и он ограничен: ' + m.calls.urls.length);
  m.inst.destroy();
});

test('без права live экран не строится', () => {
  const m = mount({ perms: {} });
  assert.strictEqual(cards(m).length, 0);
  m.inst.destroy();
});

// ─────────────────────────── фильтры ───────────────────────────

test('фильтры: день, камера и тип уходят в запрос', () => {
  const m = mount({ pages: [PAGE1, PAGE1, PAGE1, PAGE1] });
  const btns = m.root.find('.qdl-btn-focus');
  assert.strictEqual(btns.length, 3);

  m.r.$(btns[2]).trigger('hover:enter');            // тип
  m.calls.selects.pop().onSelect({ kind: 'human' });
  assert.ok(m.calls.urls[m.calls.urls.length - 1].indexOf('type=human') !== -1);

  m.r.$(btns[1]).trigger('hover:enter');            // камера
  m.calls.selects.pop().onSelect({ cam: 5 });
  const u = m.calls.urls[m.calls.urls.length - 1];
  assert.ok(u.indexOf('camera=5') !== -1 && u.indexOf('type=human') !== -1, 'фильтры складываются: ' + u);

  m.r.$(btns[0]).trigger('hover:enter');            // день
  const day = m.calls.selects.pop();
  assert.ok(day.items.length > 1 && day.items[0].date === '', 'первым пунктом — «Все даты»');
  assert.strictEqual(day.items[1].title, 'Сегодня');
  day.onSelect({ date: '2026-08-30' });
  assert.ok(m.calls.urls[m.calls.urls.length - 1].indexOf('date=2026-08-30') !== -1);
  m.inst.destroy();
});

test('смена фильтра чистит ленту, а не дописывает в неё', () => {
  const m = mount({ pages: [PAGE1, { items: [evt(7)], hasNext: false, cursor: 7 }] });
  m.r.$(m.root.find('.qdl-btn-focus')[2]).trigger('hover:enter');
  m.calls.selects.pop().onSelect({ kind: 'motion' });
  assert.strictEqual(cards(m).length, 1, 'осталась только новая выдача');
  m.inst.destroy();
});

// ─────────────────────────── просмотр ───────────────────────────

test('просмотр: Enter открывает оверлей с ОРИГИНАЛОМ кадра', () => {
  const m = mount();
  m.r.$(cards(m)[0]).trigger('hover:enter');

  const view = m.body.find('.qdl-det-view');
  assert.strictEqual(view.length, 1);
  const src = m.r.$(view.find('img')[0]).attr('src');
  assert.ok(src.indexOf('id=100') !== -1 && src.indexOf('w=') === -1, 'без w= — оригинал: ' + src);
  assert.ok(view.text().indexOf('1 из 3') !== -1, 'счётчик в подвале');
  m.inst.destroy();
});

test('просмотр: стрелки листают события, Back закрывает и не выходит из раздела', () => {
  const m = mount();
  m.r.$(cards(m)[0]).trigger('hover:enter');

  m.calls.ctrl.right();
  assert.ok(m.body.find('.qdl-det-view').text().indexOf('2 из 3') !== -1);
  m.calls.ctrl.left();
  assert.ok(m.body.find('.qdl-det-view').text().indexOf('1 из 3') !== -1);
  m.calls.ctrl.left();
  assert.ok(m.body.find('.qdl-det-view').text().indexOf('1 из 3') !== -1, 'левее первого не уходим');

  m.calls.ctrl.back();
  assert.strictEqual(m.body.find('.qdl-det-view').length, 0, 'оверлей закрыт');
  assert.ok(!m.calls.backward, 'Back из просмотра не выходит из раздела');
  m.inst.destroy();
});

test('просмотр: OK открывает привязанную запись', () => {
  const m = mount({ pages: [{ items: [evt(100, { recording: 8812 })], hasNext: false, cursor: 100, today: '2026-09-01' }] });
  m.r.$(cards(m)[0]).trigger('hover:enter');       // открыть просмотр
  m.r.$(cards(m)[0]).trigger('hover:enter');       // OK внутри просмотра

  assert.strictEqual(m.calls.plays.length, 1);
  assert.ok(String(m.calls.plays[0].url).indexOf('/qdl/live/stream?id=8812') !== -1, m.calls.plays[0].url);
  assert.strictEqual(m.body.find('.qdl-det-view').length, 0, 'просмотр закрылся перед плеером');
  m.inst.destroy();
});

test('просмотр: без права rec запись не открывается', () => {
  const m = mount({
    perms: { live: true },
    pages: [{ items: [evt(100, { recording: 8812 })], hasNext: false, cursor: 100, today: '2026-09-01' }],
  });
  m.r.$(cards(m)[0]).trigger('hover:enter');
  m.r.$(cards(m)[0]).trigger('hover:enter');

  assert.strictEqual(m.calls.plays.length, 0);
  assert.ok(m.calls.notys.join(' ').indexOf('недоступен') !== -1);
  m.inst.destroy();
});

test('оверлей просмотра уезжает вместе с экраном', () => {
  const m = mount();
  m.r.$(cards(m)[0]).trigger('hover:enter');
  assert.strictEqual(m.body.find('.qdl-det-view').length, 1);

  m.inst.pause();
  assert.strictEqual(m.body.find('.qdl-det-view').length, 0, 'ушли вперёд — оверлей не остался поверх чужого экрана');
  m.inst.destroy();
});

// ─────────────────── догрузка и пульт (qdl 2.99) ───────────────────
//
// Жалоба владельца с телевизора: «листаю вниз — на определённом моменте карточки просто не
// подгружаются, на маке всё листается». Замер боевого клиента через CDP: в DOM 90 карточек,
// в Navigator._collection — 63, canmove('down') = false. Мышь и тач фокусят элемент напрямую,
// мимо коллекции, поэтому на маке и телефоне бага не видно вовсе.

test('карточки догрузки уходят в коллекцию фокуса — иначе пульт упрётся в конец 1-й страницы', () => {
  const m = mount({ pages: [PAGE1, { items: [evt(50), evt(49)], hasNext: false, cursor: 49 }] });
  assert.strictEqual(m.calls.appended.length, 0,
    'первая страница собирается через activity.toggle → collectionSet, дублировать её не надо');
  m.inst.load(false);
  assert.strictEqual(m.calls.appended.length, 2, 'обе карточки 2-й страницы зарегистрированы у навигатора');
  assert.strictEqual(m.calls.appended[0][0], cards(m)[3], 'в коллекцию идёт сам DOM-узел карточки');
  m.inst.destroy();
});

test('link контроллера на месте — без него own(comp) всегда ложь и регистрация не сработает', () => {
  const m = mount();
  assert.strictEqual(m.calls.ctrl.link, m.inst);
  m.inst.destroy();
});

test('ответ, доживший до чужого экрана, не засоряет чужую коллекцию', () => {
  const m = mount({ pages: [PAGE1, { items: [evt(50)], hasNext: false, cursor: 50 }] });
  m.calls.foreign = true;              // зритель ушёл в меню/плеер: активен НЕ наш контроллер
  m.inst.load(false);
  assert.strictEqual(m.calls.appended.length, 0, 'при возврате toggle соберёт коллекцию заново');
  m.inst.destroy();
});
