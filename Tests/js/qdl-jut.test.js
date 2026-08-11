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

test('пункт меню встаёт строго после «D1versy Rec» (.qdl-live-menu)', () => {
  const src = H.qdlSource();
  const i = src.indexOf("dedupe('.menu .qdl-jut-menu')");
  assert.ok(i > 0, 'вставка пункта jut.su должна идти через dedupe');
  const before = src.slice(Math.max(0, i - 400), i);
  assert.ok(before.includes(".qdl-live-menu"),
    'якорем должен быть .qdl-live-menu (это «D1versy Rec» — имена классов обманчивы)');
});

test('dedupe применяется — иначе .after() на jQuery-наборе клонирует пункт', () => {
  const src = H.qdlSource();
  const block = src.slice(src.indexOf("var lv = dedupe('.menu .qdl-live-menu')"));
  assert.ok(block.slice(0, 400).includes("dedupe('.menu .qdl-jut-menu')"));
  // перепозиционирование при пере-рендере меню
  assert.ok(block.slice(0, 500).includes('jut.detach()'),
    'пункт должен возвращаться на место после пере-рендера меню');
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
  const fn = src.slice(i, i + 1400);
  assert.ok(fn.includes('/qdl/jut/resolve'), 'без resolve ссылки нет');
  assert.ok(fn.includes('Lampa.Player.play'), 'после resolve — запуск плеера');
  assert.ok(fn.includes('Lampa.Player.playlist'), 'плейлист сезона даёт автопереход');
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
  const i = src.indexOf('this.empty = function');
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
  assert.ok(fn.includes('Следить'));
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

test('«Следить» качает только будущие серии — это сказано словами', () => {
  const src = H.qdlSource();
  assert.ok(src.includes('новые серии будут скачиваться сами'),
    'пользователь должен понимать, что подписка = автоскачивание');
});
