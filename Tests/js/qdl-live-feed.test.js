'use strict';
// D1versy Rec → лента записей (ComponentRecFeed, qdl 2.99).
//
// Экран-близнец Detection: та же бесконечная лента, та же мина. Догруженные строки попадали
// в DOM, но не в коллекцию фокуса Navigator — и для ПУЛЬТА лента кончалась на последней строке
// первой страницы (мышь и тач фокусят элемент напрямую, мимо коллекции, и бага не видят).
// Замер боевого клиента на телевизоре через CDP: 90 карточек в DOM против 63 в коллекции.
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rec(id, over) {
  return Object.assign({
    id, camera: 3, cameraName: 'Garage 2', start: '18:00:00', end: '18:10:00',
    seconds: 600, size: 1024 * 1024 * 200, trigger: 'human',
    day: '2026-09-01', dayLabel: 'Сегодня',
  }, over || {});
}

const PAGE1 = { items: [rec(10), rec(9), rec(8)], hasNext: true };

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
  const calls = { urls: [], plays: [], appended: [], foreign: false };
  const pages = opts.pages || [PAGE1];
  let page = 0;

  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        const u = String(url);
        if (u.indexOf('/qdl/live/feed') === -1) return;
        calls.urls.push(u);
        const p = pages[Math.min(page, pages.length - 1)];
        page++;
        ok(p);
      };
    },
    Player: { play: (x) => calls.plays.push(x), playlist: () => {} },
    Select: { show: () => {}, listener: { follow() {}, send() {} } },
    Noty: { show: () => {} },
    Controller: {
      add(name, o) { calls.ctrl = o; },
      toggle() {}, collectionSet() {}, collectionFocus() {},
      own: (link) => !calls.foreign && !!calls.ctrl && calls.ctrl.link === link,
      collectionAppend: (el) => calls.appended.push(el),
    },
    Layer: { visible() {}, update() {} },
  });

  const r = H.loadQdlDom({ lampa, perms: opts.perms });
  r.lampa.Scroll = domScroll(r.w);

  const inst = new r.qdl.ComponentRecFeed({});
  inst.activity = { loader() {}, toggle() {} };
  inst.create();
  inst.start();

  return { r, inst, calls, root: inst.render() };
}

const rows = (m) => m.root.find('.qdl-row-focus');

test('лента: строка на запись, разделитель на смену дня', () => {
  const m = mount({ pages: [{ items: [rec(10), rec(9, { day: '2026-08-31', dayLabel: 'Вчера' })], hasNext: false }] });
  assert.strictEqual(rows(m).length, 2);
  assert.ok(m.root.text().indexOf('Вчера') !== -1, 'смена дня рисует разделитель');
  m.inst.destroy();
});

test('окно сдвигается на ОТДАННОЕ сервером, дубли не рисуются дважды', () => {
  const m = mount({ pages: [PAGE1, { items: [rec(9), rec(7)], hasNext: false }] });
  m.inst.load(false);
  assert.ok(m.calls.urls[1].indexOf('offset=3') !== -1, 'смещение по отданному: ' + m.calls.urls[1]);
  assert.strictEqual(rows(m).length, 4, 'повторившаяся запись не задвоилась');
  m.inst.destroy();
});

test('строки догрузки уходят в коллекцию фокуса — иначе пульт упрётся в конец 1-й страницы', () => {
  const m = mount({ pages: [PAGE1, { items: [rec(7), rec(6)], hasNext: false }] });
  assert.strictEqual(m.calls.appended.length, 0,
    'первая страница собирается через activity.toggle → collectionSet');
  m.inst.load(false);
  assert.strictEqual(m.calls.appended.length, 2);
  assert.strictEqual(m.calls.appended[0][0], rows(m)[3], 'в коллекцию идёт сам DOM-узел строки');
  m.inst.destroy();
});

test('link контроллера на месте — без него own(comp) всегда ложь и регистрация не сработает', () => {
  const m = mount();
  assert.strictEqual(m.calls.ctrl.link, m.inst);
  m.inst.destroy();
});

test('ответ, доживший до чужого экрана, не засоряет чужую коллекцию', () => {
  const m = mount({ pages: [PAGE1, { items: [rec(7)], hasNext: false }] });
  m.calls.foreign = true;
  m.inst.load(false);
  assert.strictEqual(m.calls.appended.length, 0, 'при возврате toggle соберёт коллекцию заново');
  m.inst.destroy();
});

test('без права rec экран не строится', () => {
  const m = mount({ perms: {} });
  assert.strictEqual(rows(m).length, 0);
  m.inst.destroy();
});
