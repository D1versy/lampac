'use strict';
// Тесты DMCA-фолбека (см. claude/06 Media-server): CUB отдаёт {"blocked":true} на карточки
// из своего DMCA-списка → Lampa рисует «Контент заблокирован» без кнопок. Наш обход:
//  1) rewriteCubUrl + XHR-патч — детали карточек идут через свой TMDB-прокси (/tmdb/api);
//  2) DMCA-список (tmdb.<cub>/blocked) → режим .qdl-dmca (только «Скачать» / зелёная «Смотреть»).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// ─────────────────────────────── rewriteCubUrl ───────────────────────────────

test('rewriteCubUrl: карточка movie у CUB → свой TMDB-прокси', () => {
  const { qdl } = H.loadQdl();
  const u = 'https://tmdb.cub.rip/3/movie/1084736?api_key=k&language=ru&append_to_response=keywords';
  assert.strictEqual(qdl.rewriteCubUrl(u),
    '{localhost}/tmdb/api/3/movie/1084736?api_key=k&language=ru&append_to_response=keywords');
});

test('rewriteCubUrl: форма через серверный CubProxy (<host>/cub/tmdb.<домен>/3/...) тоже переписывается', () => {
  const { qdl } = H.loadQdl();
  // реальный вид из браузера: cubproxy.js на request_before заворачивает cub-запросы на наш /cub/
  assert.strictEqual(
    qdl.rewriteCubUrl('http://192.168.87.24:9118/cub/tmdb./3/movie/1084736?api_key=k&language=ru&email='),
    '{localhost}/tmdb/api/3/movie/1084736?api_key=k&language=ru&email=');
  assert.strictEqual(
    qdl.rewriteCubUrl('http://192.168.87.24:9118/cub/tmdb.cub.rip/3/tv/1399/season/1?api_key=k'),
    '{localhost}/tmdb/api/3/tv/1399/season/1?api_key=k');
  // прочие /cub/-запросы (не детали карточек) не трогаем
  assert.strictEqual(qdl.rewriteCubUrl('http://192.168.87.24:9118/cub/tmdb.cub.rip/blocked'), null);
  assert.strictEqual(qdl.rewriteCubUrl('http://192.168.87.24:9118/cub/cub.rip/api/checker'), null);
});

test('rewriteCubUrl: tv и сабпуть сезона переписываются', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(qdl.rewriteCubUrl('http://tmdb.mirror.men/3/tv/1399?api_key=k'),
    '{localhost}/tmdb/api/3/tv/1399?api_key=k');
  assert.strictEqual(qdl.rewriteCubUrl('https://tmdb.cub.rip/3/tv/1399/season/1?api_key=k&language=ru'),
    '{localhost}/tmdb/api/3/tv/1399/season/1?api_key=k&language=ru');
});

test('rewriteCubUrl: без api_key НЕ трогаем (прямой TMDB ответит 401)', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(qdl.rewriteCubUrl('https://tmdb.cub.rip/3/movie/1084736?language=ru'), null);
});

test('rewriteCubUrl: каталог/поиск/служебные и чужие хосты не трогаем', () => {
  const { qdl } = H.loadQdl();
  for (const u of [
    'https://tmdb.cub.rip/3/discover/movie?api_key=k',        // каталог
    'https://tmdb.cub.rip/3/search/movie?api_key=k&query=x',  // поиск
    'https://tmdb.cub.rip/3/person/123?api_key=k',            // персоны
    'https://tmdb.cub.rip/blocked',                           // сам DMCA-список
    'https://cub.rip/api/checker',                            // cub API без tmdb-сабдомена
    'https://api.themoviedb.org/3/movie/1?api_key=k',         // уже прямой TMDB
    'https://image.tmdb.org/t/p/w300/x.jpg',                  // картинки
    'http://192.168.87.24:9118/tmdb/api/3/movie/1?api_key=k', // уже переписанный
  ]) assert.strictEqual(qdl.rewriteCubUrl(u), null, u);
});

// ─────────────────────────────── rewriteWatchUrl (qdl 2.15) ───────────────────────────────
// пинг «просмотрено» — raw $.get мимо request_before/cubproxy.js → заворачиваем на локальную заглушку

test('rewriteWatchUrl: watch-пинг CUB → локальная заглушка /cub/api/checker', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(qdl.rewriteWatchUrl('https://tmdb.cub.rip/watch?id=1084736&cat=movie'),
    '{localhost}/cub/api/checker');
  assert.strictEqual(qdl.rewriteWatchUrl('http://tmdb.mirror.men/watch?id=5'),
    '{localhost}/cub/api/checker', 'зеркальный домен тоже');
  assert.strictEqual(qdl.rewriteWatchUrl('http://192.168.87.24:9118/cub/tmdb.cub.rip/watch?id=5'),
    '{localhost}/cub/api/checker', 'уже завёрнутая форма через /cub/ тоже');
});

test('rewriteWatchUrl: посторонние URL не трогаем', () => {
  const { qdl } = H.loadQdl();
  for (const u of [
    'https://tmdb.cub.rip/3/movie/1?api_key=k',   // детали — зона rewriteCubUrl
    'https://tmdb.cub.rip/blocked',               // DMCA-список (идёт через Reguest→cubproxy.js)
    'https://tmdb.cub.rip/watch',                 // без query такого запроса в бандле нет
    'https://cub.rip/watch?id=1',                 // не tmdb-сабдомен
    'https://example.com/watch?id=1',             // чужой хост
  ]) assert.strictEqual(qdl.rewriteWatchUrl(u), null, u);
});

// ─────────────────────────────── XHR-патч ───────────────────────────────

// фабрика: у каждого теста СВОЙ класс, иначе патчи из разных sandbox наслаиваются на общий прототип
function makeFakeXhr() {
  function FakeXhr() {}
  FakeXhr.prototype.open = function (method, url) { this._method = method; this._url = String(url); };
  return FakeXhr;
}

test('XHR-патч: GET карточки CUB переписан на прокси, POST и посторонние URL — нет', () => {
  const { sandbox } = H.loadQdl({ windowExtra: { XMLHttpRequest: makeFakeXhr() } });
  const Xhr = sandbox.window.XMLHttpRequest;

  const a = new Xhr();
  a.open('GET', 'https://tmdb.cub.rip/3/movie/1084736?api_key=k&language=ru');
  assert.strictEqual(a._url, '{localhost}/tmdb/api/3/movie/1084736?api_key=k&language=ru');

  const b = new Xhr();
  b.open('POST', 'https://tmdb.cub.rip/3/movie/1084736?api_key=k');
  assert.strictEqual(b._url, 'https://tmdb.cub.rip/3/movie/1084736?api_key=k', 'POST не трогаем');

  const c = new Xhr();
  c.open('GET', 'https://cub.rip/api/notice');
  assert.strictEqual(c._url, 'https://cub.rip/api/notice', 'не-tmdb URL не трогаем');

  const d = new Xhr();
  d.open('GET', 'https://tmdb.cub.rip/watch?id=1084736&cat=movie');
  assert.strictEqual(d._url, '{localhost}/cub/api/checker', 'watch-пинг → локальная заглушка (2.15)');
});

test('XHR-патч: домен CUB запоминается из живых запросов → /blocked идёт на зеркало (через наш прокси)', () => {
  const reqs = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url) => { reqs.push(String(url)); };
    },
  });
  const { qdl, sandbox } = H.loadQdl({ lampa, windowExtra: { XMLHttpRequest: makeFakeXhr() } });

  new sandbox.window.XMLHttpRequest().open('GET', 'https://tmdb.mirror-kurwa.men/3/movie/5?api_key=k');
  qdl.whenDmca(() => {});
  assert.deepStrictEqual(reqs, ['{localhost}/cub/tmdb.mirror-kurwa.men/blocked'], '2.19: URL сразу локальный (наш CubProxy), а не через cubproxy.js на request_before');
});

test('lampainit-invc: ранний XHR-патч (deep-link) — стоит до qdl.js, ставит флаг и домен', () => {
  const { mod, sandbox } = H.loadLampaInit({ windowExtra: { XMLHttpRequest: makeFakeXhr() } });

  const x = new sandbox.window.XMLHttpRequest();
  x.open('GET', 'https://tmdb.cub.rip/3/movie/1084736?api_key=k&language=ru');
  assert.strictEqual(x._url, '{localhost}/tmdb/api/3/movie/1084736?api_key=k&language=ru');
  assert.strictEqual(sandbox.window.qdl_xhr_patch, 1, 'флаг не даст qdl.js навесить второй патч');
  assert.strictEqual(sandbox.window.qdl_cub_domain, 'cub.rip', 'домен CUB запомнен для /blocked');
  assert.strictEqual(mod.rewriteCubUrl('https://tmdb.cub.rip/3/discover/movie?api_key=k'), null);

  // 2.15: watch-пинг тоже ловится ранним патчем (deep-link может дёрнуть его до загрузки qdl.js)
  const w = new sandbox.window.XMLHttpRequest();
  w.open('GET', 'https://tmdb.cub.rip/watch?id=7&cat=tv');
  assert.strictEqual(w._url, '{localhost}/cub/api/checker');
  assert.strictEqual(mod.rewriteWatchUrl('https://tmdb.cub.rip/blocked'), null);
});

test('qdl.js: при уже стоящем раннем патче второй не вешается (window.qdl_xhr_patch)', () => {
  const FakeXhr = makeFakeXhr();
  const baseOpen = FakeXhr.prototype.open;
  H.loadQdl({ windowExtra: { XMLHttpRequest: FakeXhr, qdl_xhr_patch: 1 } });
  assert.strictEqual(FakeXhr.prototype.open, baseOpen, 'прототип не тронут');
});

// ─────────────────────────────── isDmca / setDmcaList ───────────────────────────────

test('isDmca: до загрузки списка — всегда false', () => {
  const { qdl } = H.loadQdl();
  assert.strictEqual(qdl.isDmca('movie', 1084736), false);
});

test('isDmca: матч по cat+id, записи с id:0 не матчатся', () => {
  const { qdl } = H.loadQdl();
  qdl.setDmcaList([{ id: 1084736, cat: 'movie', kpid: 0 }, { id: 0, kpid: 5303314 }, { id: 50532, cat: 'tv' }]);
  assert.strictEqual(qdl.isDmca('movie', 1084736), true);
  assert.strictEqual(qdl.isDmca('tv', 1084736), false, 'другой cat — не матч');
  assert.strictEqual(qdl.isDmca('tv', 50532), true);
  assert.strictEqual(qdl.isDmca('movie', 999), false);
  assert.strictEqual(qdl.isDmca('movie', 0), false, 'id:0 никогда не матчится');
});

// ─────────────────────────────── whenDmca: лень, кэш, фолбэки ───────────────────────────────

function rigNet(respond) {
  const reqs = [];
  const lampa = H.makeLampa({
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = (url, ok, err) => {
        reqs.push(String(url));
        const h = respond(String(url));
        if (h === 'ERR') err();
        else if (h !== undefined) ok(h);
      };
    },
  });
  return { lampa, reqs };
}

test('whenDmca: одна загрузка /blocked на всех ожидающих + кэш в Storage', () => {
  const { lampa, reqs } = rigNet((u) => (u.indexOf('/blocked') !== -1 ? [{ id: 7, cat: 'movie' }] : undefined));
  const { qdl } = H.loadQdl({ lampa });

  let calls = 0;
  qdl.whenDmca(() => calls++);
  qdl.whenDmca(() => calls++);   // список уже загружен первым вызовом → сразу
  assert.strictEqual(calls, 2);
  assert.deepStrictEqual(reqs, ['{localhost}/cub/tmdb.cub.rip/blocked'], 'ровно один сетевой запрос, и он локальный (2.19)');
  assert.strictEqual(qdl.isDmca('movie', 7), true);

  const cache = lampa._storage.get('qdl_dmca_cache');
  assert.ok(cache && Array.isArray(cache.list) && cache.list.length === 1, 'список закэширован');
});

test('whenDmca: свежий кэш в Storage → без сети', () => {
  const { lampa, reqs } = rigNet(() => undefined);
  lampa._storage.set('qdl_dmca_cache', { ts: Date.now(), list: [{ id: 42, cat: 'tv' }] });
  const { qdl } = H.loadQdl({ lampa });

  let called = false;
  qdl.whenDmca(() => { called = true; });
  assert.strictEqual(called, true);
  assert.deepStrictEqual(reqs, [], 'сеть не дёргалась');
  assert.strictEqual(qdl.isDmca('tv', 42), true);
});

test('whenDmca: протухший кэш → сеть; ошибка сети → фолбэк на протухший список', () => {
  const { lampa, reqs } = rigNet((u) => (u.indexOf('/blocked') !== -1 ? 'ERR' : undefined));
  lampa._storage.set('qdl_dmca_cache', { ts: Date.now() - 7 * 3600 * 1000, list: [{ id: 42, cat: 'tv' }] });
  const { qdl } = H.loadQdl({ lampa });

  let called = false;
  qdl.whenDmca(() => { called = true; });
  assert.strictEqual(called, true);
  assert.deepStrictEqual(reqs, ['{localhost}/cub/tmdb.cub.rip/blocked']);
  assert.strictEqual(qdl.isDmca('tv', 42), true, 'протухший список лучше пустого');
});

// ─────────────────────────────── addButton: режим .qdl-dmca (jsdom + jQuery) ───────────────────────────────

// В контейнере уже есть «чужая» кнопка (онлайн) — проверяем, что наша встаёт ПЕРВОЙ
const PAGE = '<div class="full-start__buttons"><div class="full-start__button view--online lampac--button"><span>Смотреть онлайн</span></div></div>';

function lampaFor(method) {
  return H.makeLampa({
    Activity: { active: () => ({ method, source: 'cub' }) },
    Reguest: function () {
      this.timeout = () => {}; this.clear = () => {};
      this.silent = () => {};   // /qdl/list и прочее молчат — здесь не важны
    },
  });
}

function fireAddButton(w, movie) {
  w.fetch = () => Promise.resolve({ json: () => Promise.resolve({}) });
  w.__qdl.addButton({
    type: 'complite',
    object: { activity: { render: () => w.$('body') } },
    data: { movie },
  });
}

test('UI: DMCA-карточка → класс qdl-dmca, «Скачать» первая в ряду, CSS прячет чужие кнопки', () => {
  const { w, doc, qdl } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie') });
  qdl.setDmcaList([{ id: 1084736, cat: 'movie', kpid: 0 }]);
  fireAddButton(w, { id: 1084736, title: 'Граф Монте-Кристо' });

  assert.ok(doc.body.classList.contains('qdl-dmca'), 'режим qdl-dmca включён');
  const btns = doc.querySelectorAll('.full-start__buttons .full-start__button');
  assert.ok(btns.length >= 2);
  assert.ok(btns[0].classList.contains('qdl-download'), '«Скачать» первая в ряду');

  const css = doc.getElementById('qdl-css');
  assert.ok(css && css.textContent.indexOf('.qdl-dmca') !== -1, 'CSS-правило qdl-dmca заинжекчено');
  assert.ok(css.textContent.indexOf(':not(.qdl-download):not(.qdl-watch-btn)') !== -1,
    'прячем всё, кроме «Скачать» и зелёной «Смотреть»');
});

test('UI: обычная карточка → без qdl-dmca, «Скачать» первая (детерминированный порядок 2.30)', () => {
  const { w, doc, qdl } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('movie') });
  qdl.setDmcaList([{ id: 1084736, cat: 'movie', kpid: 0 }]);
  fireAddButton(w, { id: 693134, title: 'Дюна 2' });

  assert.strictEqual(doc.body.classList.contains('qdl-dmca'), false);
  const btns = doc.querySelectorAll('.full-start__buttons .full-start__button');
  assert.ok(btns[0].classList.contains('qdl-download'), '«Скачать» первая — кнопок просмотра из загрузок нет');
  assert.ok(btns[btns.length - 1].classList.contains('view--online'), 'чужая кнопка после наших');
});

test('UI: id в списке, но другой cat (tv vs movie) → фолбек не включается', () => {
  const { w, doc, qdl } = H.loadQdlDom({ bodyHtml: PAGE, lampa: lampaFor('tv') });
  qdl.setDmcaList([{ id: 1084736, cat: 'movie', kpid: 0 }]);
  fireAddButton(w, { id: 1084736, name: 'Сериал-тёзка' });

  assert.strictEqual(doc.body.classList.contains('qdl-dmca'), false);
});
