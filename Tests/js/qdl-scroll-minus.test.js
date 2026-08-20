'use strict';
// Регресс-ловушка «список не скроллится на ТВ»: каждый наш компонент-активность обязан
// звать scroll.minus() в create() — иначе .scroll не получает класс layer--wheight/высоту,
// на ТВ (transform-скролл, maxOffset от offsetHeight) контент уезжает за экран без скролла.
// Исторический прецедент — qdl_card без minus() (починен в 2.11) и упавший на этом же
// механизме upstream-селектбокс (см. qdl-select-height.test.js).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function recordingScroll() {
  const made = [];
  function Scroll() {
    const rec = { minus: 0, updates: 0 };
    made.push(rec);
    this.render = () => H.makeEl();
    this.body = () => H.make$();   // jQuery-заглушка: компоненты зовут scroll.body().append(...)
    this.append = () => {};
    this.minus = () => { rec.minus++; };
    this.update = () => { rec.updates++; };
    this.destroy = () => {};
  }
  Scroll.made = made;
  return Scroll;
}

// все зарегистрированные компоненты qdl (Lampa.Component.add в start())
const COMPONENTS = [
  'ComponentDownloads',
  'ComponentEpisodes',
  'ComponentCard',
  'ComponentNotifications',
  'ComponentLive',
  'ComponentLiveWatch',
  'ComponentLiveCamera',
  // jut.su (qdl 2.26) — новые компоненты обязаны попадать в этот регресс-контур
  'ComponentJutCatalog',
  'ComponentJutTitle',
  'ComponentJutEpisodes',
  'ComponentJutSearch',
];

for (const name of COMPONENTS) {
  test(`${name}: create() зовёт scroll.minus() (высота скролла на ТВ)`, () => {
    const Scroll = recordingScroll();
    const { qdl: q } = H.loadQdl({ lampa: H.makeLampa({ Scroll }) });
    // qdl 2.54: экраны D1versy Live/Rec гейтятся правами устройства — без них create() уходит
    // в denySection() и до scroll.minus() не доходит. Права к этому тесту отношения не имеют.
    q.setPerms({ live: true, rec: true });
    const C = q[name];
    assert.ok(typeof C === 'function', name + ' экспортирован из qdl.js');

    const inst = new C({ qdl: {}, qdl_hash: 'h'.repeat(40), qdl_name: 'x', qdl_camera: {}, qdl_date: '' });
    inst.activity = { loader() {}, toggle() {} };
    inst.create();

    assert.ok(Scroll.made.length >= 1, 'Scroll создан');
    assert.ok(Scroll.made[0].minus >= 1, 'scroll.minus() вызван в create()');
  });
}
