'use strict';
// Регресс-ловушка «на ТВ не работает пульт», она же — НЕВИДИМЫЙ ФОКУС.
//
// Lampa вешает класс .focus на .selector (только на ТВ/десктопе), но генерического
// правила `.selector.focus` в её CSS НЕТ — каждый компонент стилизует фокус сам.
// Компонент без своего focus-правила выглядит на ТВ как «стрелки ничего не делают»:
// фокус реально ходит, его просто не видно. Дважды наступали:
//   - Rec/Live/Уведомления (qdl 2.8, claude/06 §AK.3) — строки dayBar/camRow/recRow
//   - jut.su (qdl 2.26 → 2.27) — кнопки карточки тайтла и строки серий
//
// Проверяем два условия, каждого по отдельности недостаточно:
//   1) компонент зовёт injectCss() — иначе правил нет в документе вообще
//      (раньше Rec полагался на то, что стили инъецировал другой экран);
//   2) каждый НАШ .selector несёт focus-класс — иначе правило есть, а вешать не на что.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const SRC = fs.readFileSync(
  path.join(__dirname, '..', '..', 'Modules', 'QbitDownload', 'plugins', 'qdl.js'), 'utf8');

// Классы, у которых focus-правило есть: наши (в injectCss) и штатные Lampa.
const OUR_FOCUS = ['qdl-row-focus', 'qdl-btn-focus', 'qdl-watch-tile', 'qdl-watch', 'qdl-fs'];
const LAMPA_STYLED = ['full-start__button', 'card', 'menu__item', 'settings-param',
                      'selectbox-item', 'player-panel', 'head__action', 'simple-button'];

// Компоненты-активности, у каждого свой экран → свои стили
const COMPONENTS = [
  'ComponentDownloads', 'ComponentEpisodes', 'ComponentCard', 'ComponentNotifications',
  'ComponentLive', 'ComponentLiveWatch', 'ComponentLiveCamera',
  'ComponentJutCatalog', 'ComponentJutTitle', 'ComponentJutEpisodes',
];

// Тело функции компонента: от `function Name(` до следующего `\n    function ` того же уровня.
function bodyOf(name) {
  const start = SRC.indexOf('function ' + name + '(');
  assert.ok(start > 0, `компонент ${name} не найден в qdl.js`);
  const next = SRC.indexOf('\n    function ', start + 10);
  return SRC.slice(start, next > 0 ? next : SRC.length);
}

// Инвариант-пара: компонент, который вешает НАШ класс, обязан сам инъецировать стили.
// injectCss() при старте плагина глобально НЕ зовётся, поэтому «другой экран уже
// инъецировал» — это не гарантия, а совпадение: экран, открытый первым после загрузки,
// останется без стилей (ровно так проявлялся баг в Rec, §AK.3).
const ALL_OUR_CLASSES = OUR_FOCUS.concat(['qdl-col-card', 'qdl-ep--cur', 'qdl-noti-head']);

for (const name of COMPONENTS) {
  const body = bodyOf(name);
  const used = ALL_OUR_CLASSES.filter((c) => body.includes(c));
  if (!used.length) continue;   // компонент живёт на штатных стилях Lampa — injectCss не нужен
  test(`${name}: вешает ${used.join('/')} → обязан звать injectCss()`, () => {
    assert.match(body, /injectCss\(\)/,
      `${name} использует наши классы (${used.join(', ')}), но не зовёт injectCss(). ` +
      `Если этот экран откроют первым после загрузки — стилей в документе не будет.`);
  });
}

test('jut.su: каждый наш .selector несёт focus-класс', () => {
  // Скоуп — только jut-компоненты: в остальном коде часть selector-ов сидит внутри
  // штатных шаблонов Lampa, которые она стилизует сама.
  const region = bodyOf('ComponentJutCatalog') + bodyOf('ComponentJutTitle') + bodyOf('ComponentJutEpisodes');
  const found = region.match(/class="selector[^"]*"/g) || [];
  assert.ok(found.length > 0, 'в jut-компонентах не нашлось ни одного .selector — тест протух');

  for (const cls of found) {
    const ok = OUR_FOCUS.concat(LAMPA_STYLED).some((c) => cls.includes(c));
    assert.ok(ok, `${cls} — без focus-класса: на ТВ фокус невидим (§AK.3). ` +
                  `Добавь qdl-row-focus (строки) или qdl-btn-focus (кнопки).`);
  }
});

test('injectCss объявляет правила для всех наших focus-классов', () => {
  const css = SRC.slice(SRC.indexOf('function injectCss'), SRC.indexOf('document.head.appendChild'));
  for (const c of ['qdl-row-focus', 'qdl-btn-focus']) {
    assert.ok(css.includes('.' + c + '.focus'),
      `в injectCss нет правила .${c}.focus — класс вешается, но ничего не подсвечивает`);
  }
});
