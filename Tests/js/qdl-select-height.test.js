'use strict';
// Фикс высоты селектбокса (upstream-баг Lampa): Select.show кладёт шапку в jQuery
// .data('mheight'), а Layer.frameUpdate читает СЫРОЕ DOM-свойство elem.mheight →
// высота шапки не вычитается, длинные списки не докручиваются на ТВ.
// fixSelectHeight обязан: (1) выставить сырое свойство, (2) дёрнуть Layer.update,
// (3) на десктопе/ТВ поставить px max-height, (4) не трогать max-height на мобиле ≤480.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const SELECTBOX =
  '<div class="selectbox">' +
    '<div class="selectbox__content layer--height">' +
      '<div class="selectbox__head"><div class="selectbox__title">Серии — Сериал</div></div>' +
      '<div class="selectbox__body layer--wheight"></div>' +
    '</div>' +
  '</div>';

// ─────────────────────────────── fixSelectHeight (jsdom) ───────────────────────────────

test('fixSelectHeight: СЫРОЕ свойство mheight = шапка (не jQuery data) + Layer.update', () => {
  const r = H.loadQdlDom({ bodyHtml: SELECTBOX });
  r.qdl.fixSelectHeight({ html: r.$('.selectbox') });

  const body = r.doc.querySelector('.selectbox__body');
  const head = r.doc.querySelector('.selectbox__head');
  assert.strictEqual(body.mheight, head, 'сырое DOM-свойство — то, что читает Layer.frameUpdate');
  assert.strictEqual(r.lampa.Layer._calls.length, 1, 'Layer.update вызван сразу');
  assert.strictEqual(r.lampa.Layer._calls[0][0], body, 'пересчёт именно для body');
});

test('fixSelectHeight: страховочный max-height в px на десктопе/ТВ (>480)', () => {
  const r = H.loadQdlDom({ bodyHtml: SELECTBOX });
  assert.ok(r.w.innerWidth > 480, 'jsdom-окно шире 480 (десктоп-кейс)');
  r.qdl.fixSelectHeight({ html: r.$('.selectbox') });

  const mh = r.doc.querySelector('.selectbox__body').style.maxHeight;
  assert.ok(/px$/.test(mh), 'max-height в px: ' + mh);
  assert.ok(parseFloat(mh) > 0 && parseFloat(mh) <= r.w.innerHeight, 'в пределах экрана: ' + mh);
});

test('fixSelectHeight: мобила ≤480 — max-height не трогаем (родной кап 60vh)', () => {
  const r = H.loadQdlDom({ bodyHtml: SELECTBOX });
  Object.defineProperty(r.w, 'innerWidth', { value: 320, configurable: true });
  r.qdl.fixSelectHeight({ html: r.$('.selectbox') });

  const body = r.doc.querySelector('.selectbox__body');
  assert.strictEqual(body.mheight, r.doc.querySelector('.selectbox__head'), 'свойство ставится и на мобиле');
  assert.strictEqual(body.style.maxHeight, '', 'max-height не выставлен');
});

test('fixSelectHeight: идемпотентен и не бросает без селектбокса', () => {
  const r = H.loadQdlDom({ bodyHtml: SELECTBOX });
  r.qdl.fixSelectHeight({ html: r.$('.selectbox') });
  r.qdl.fixSelectHeight({ html: r.$('.selectbox') });   // повторно — те же значения, без исключений
  assert.strictEqual(r.doc.querySelector('.selectbox__body').mheight, r.doc.querySelector('.selectbox__head'));

  r.qdl.fixSelectHeight({ html: r.$('.no-such') });     // пустой jQuery → no-op
  r.qdl.fixSelectHeight(null);                          // без события: Select.render() → null → no-op
  r.qdl.fixSelectHeight(undefined);
});

// ─────────────────────────────── initSelectFix (vm) ───────────────────────────────

test('initSelectFix: одна подписка на fullshow даже при повторном вызове; событие дёргает фикс', () => {
  const lampa = H.makeLampa();
  const { qdl: q, sandbox } = H.loadQdl({ lampa });

  q.initSelectFix();
  q.initSelectFix();   // флаг window.__qdl_selectfix → вторая подписка не появляется
  assert.strictEqual(sandbox.window.__qdl_selectfix, true);
  assert.strictEqual(lampa.Select.listener._subs.fullshow.length, 1, 'ровно один хендлер');

  // send('fullshow') реально доходит до fixSelectHeight: свойство + Layer.update
  const bodyEl = {};
  const headEl = { getBoundingClientRect: () => ({ height: 50 }) };
  const fakeJq = (el) => ({ 0: el, length: 1, css() {}, find() { return { length: 0 }; } });
  const root = {
    find: (sel) => (sel === '.selectbox__body' ? fakeJq(bodyEl) : fakeJq(headEl)),
  };
  lampa.Select.listener.send('fullshow', { html: root });
  assert.strictEqual(bodyEl.mheight, headEl, 'фикс отработал по событию');
  assert.strictEqual(lampa.Layer._calls.length, 1);
});

test('initSelectFix: бандл без Select.listener → фолбэк-обёртка show (оригинал зовётся, фикс не бросает)', () => {
  const lampa = H.makeLampa();
  delete lampa.Select.listener;
  let shown = 0;
  lampa.Select.show = () => { shown++; };
  const { qdl: q, sandbox } = H.loadQdl({ lampa });

  q.initSelectFix();
  assert.strictEqual(sandbox.window.__qdl_selectfix, true);
  lampa.Select.show({ title: 'x', items: [] });
  assert.strictEqual(shown, 1, 'оригинальный show вызван через обёртку');
});
