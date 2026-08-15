'use strict';
// qdl 2.45: серверный кеш выдачи поиска раздач со stale-семантикой.
//
// Со стороны клиента важны ровно две вещи:
//  1) /qdl/search обязан остаться ЖСОН-МАССИВОМ. Пометка stale приезжает полем на элементах, а не
//     сменой формы ответа: у части клиентов ещё живёт закешированный старый qdl.js, и он делает
//     list.length / list.slice сразу по ответу — объект вместо массива дал бы им «Раздачи не найдены».
//  2) когда сервер отдал снимок из кеша, пользователь должен об этом узнать: сиды в снимке могли
//     устареть, хотя магнеты валидны.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function rig(opts) {
  opts = opts || {};
  const calls = { selects: [], noty: [], reqs: [] };
  const lampa = H.makeLampa({
    Select: { show: (o) => calls.selects.push(o) },
    Noty: { show: (m) => calls.noty.push(String(m)) },
    Activity: { push() {}, replace() {}, active: () => ({}) },
    Controller: { add() {}, toggle() {}, collectionSet() {}, collectionFocus() {} },
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => {
        calls.reqs.push(String(url));
        const h = (opts.respond || (() => undefined))(String(url));
        if (h !== undefined) ok(h);
      };
    },
  });
  const { qdl } = H.loadQdl({ lampa, setInterval: () => 0, clearInterval: () => {} });
  return { qdl, calls };
}

function searchRig(list) {
  return rig({
    respond: (u) => {
      if (u.indexOf('/qdl/search') !== -1) return list;
      if (u.indexOf('/qdl/add') !== -1) return { success: true, hash: 'h1' };
      return undefined;
    },
  });
}

const last = (a) => a[a.length - 1];

const FRESH = [
  { title: 'A 1080p', quality: 1080, size: '6 GB', tracker: 'tr', sid: 10, magnet: 'magnet:?xt=urn:btih:a' },
  { title: 'B 720p', quality: 720, size: '3 GB', tracker: 'tr', sid: 5, magnet: 'magnet:?xt=urn:btih:b' },
];

const STALE = FRESH.map((t) => Object.assign({ stale: true }, t));

test('свежая выдача: заголовок Select без пометки о кеше', () => {
  const r = searchRig(FRESH);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const menu = last(r.calls.selects);
  assert.strictEqual(menu.title.indexOf('кеш'), -1, 'пометки быть не должно');
  assert.strictEqual(menu.items.length, 2);
});

test('stale-выдача: в заголовке Select появляется пометка «из кеша, обновляю»', () => {
  const r = searchRig(STALE);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const menu = last(r.calls.selects);
  assert.ok(menu.title.indexOf('из кеша') !== -1, 'пользователь должен видеть, что список из снимка');
  assert.ok(menu.title.indexOf('обновляю') !== -1, 'и что сервер уже обновляет его в фоне');
});

test('stale не ломает список: элементы и выбор работают как обычно', () => {
  const r = searchRig(STALE);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  const menu = last(r.calls.selects);

  assert.strictEqual(menu.items.length, 2);
  assert.ok(menu.items[0].title.indexOf('A 1080p') !== -1);

  menu.onSelect(menu.items[0]);
  assert.strictEqual(r.calls.reqs.filter((u) => u.indexOf('/qdl/add') !== -1).length, 1,
    'скачивание из stale-списка должно уходить — магнет валиден, устареть могли только сиды');
});

test('пометка берётся с первого элемента и не требует её у остальных', () => {
  // сервер метит все элементы, но клиент не должен зависеть от того, сколько их помечено
  const mixed = [Object.assign({ stale: true }, FRESH[0]), FRESH[1]];
  const r = searchRig(mixed);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  assert.ok(last(r.calls.selects).title.indexOf('из кеша') !== -1);
});

test('пустая выдача обрабатывается как раньше — «Раздачи не найдены», без Select', () => {
  const r = searchRig([]);
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  assert.strictEqual(r.calls.selects.length, 0);
  assert.ok(r.calls.noty.some((m) => m.indexOf('не найдены') !== -1));
});

test('ответ-массив остаётся контрактом: объект вместо массива не должен приниматься за выдачу', () => {
  // страховка от соблазна поменять форму ответа на {items:[…],stale:true}
  const r = searchRig({ items: FRESH, stale: true });
  r.qdl.chooseAndDownload({ title: 'Кино', media_type: 'movie' });
  assert.strictEqual(r.calls.selects.length, 0, 'объект не имеет length → выдача считается пустой');
});
