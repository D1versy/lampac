'use strict';
// Дубли карточек на экране «Ещё» (qdl 2.94). Жалоба владельца: в «Сейчас смотрят» → «Ещё»
// каждый фильм показан двумя строчками по 6 карточек.
//
// Серверную половину закрыл RowFilter (страница = ровно одна апстримная, без добора соседних —
// см. CubRowFilterTests). Здесь — клиентская: дедуп по id при догрузке и насос, догружающий
// короткую страницу. Оба контракта дёргает патч бандла (grid-dedup-build / grid-dedup-next).
//
// 🔥 Два инварианта, которые тут под защитой:
//  • НЕ ТЕРЯТЬ: карточка без надёжного ключа не выбрасывается никогда, а фильм и сериал с одним
//    TMDB id — разные карточки (нумерация у TMDB раздельная);
//  • НЕ ЗАЦИКЛИТЬСЯ: после дедупа страница может добавить 0 карточек, грид не вырастет и
//    isFilled() останется false — бюджет авто-подтяжек обязан это оборвать.

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

const PUMP_MS = 1150;   // сверяется с gridConf() в первом же тесте

/** Песочница с ручными таймерами: насос отложен, ждать 1.15 с в прогоне тестов нельзя. */
function env(over) {
  const timers = [];
  const lampa = H.makeLampa();
  const r = H.loadQdl(Object.assign({
    lampa,
    setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
  }, over || {}));
  r.lampa = lampa;
  r.timers = timers;
  // Дёргаем только таймеры насоса: initGridDedup ставит ещё диагностический на 30 с.
  r.fire = (times) => {
    for (let i = 0; i < (times || 1); i++) {
      const due = timers.filter((t) => t.ms === PUMP_MS);
      timers.length = 0;
      due.forEach((t) => t.fn());
    }
  };
  return r;
}

/** Мок компонента сетки в объёме, который трогает политика (Items+Next из бандла). */
function makeGrid(over) {
  const emitted = [];
  const g = {
    items: [{}],
    loaded: [],
    destroyed: false,
    object: { page: 1 },
    total_pages: 86,
    limit_view: 6,
    scroll: {
      filled: false,
      isFilled() { return this.filled; },
      render: () => ({ isConnected: true }),
    },
    emit(ev) { emitted.push(ev); },
    _emitted: emitted,
  };
  return Object.assign(g, over || {});
}

const movie = (id) => ({ id, title: 'm' + id, release_date: '2026-01-01' });
const page = (...ids) => ids.map(movie);
// Array.from — из ТЕСТОВОГО реалма: массивы, рождённые внутри vm-песочницы, у deepStrictEqual
// не проходят сверку по прототипу («same structure but not reference-equal»).
const ids = (arr) => Array.from(arr, (c) => c.id);

// ─────────────────────────── контракт с патчами бандла ───────────────────────────

test('initGridDedup публикует оба хука — без них патчи бандла не делают ничего', () => {
  const r = env();
  r.qdl.initGridDedup();

  assert.strictEqual(typeof r.sandbox.window.qdl_grid_build, 'function');
  assert.strictEqual(typeof r.sandbox.window.qdl_grid_next, 'function');
  assert.strictEqual(r.qdl.gridConf().ms, PUMP_MS);
  // 🔴 задержка обязана быть больше гарда builded_time (1000 мс) в Next.onLoadNext, иначе
  // насос «работает», а бандл молча съедает каждый его loadNext — симптом неотличим от бага
  assert.ok(r.qdl.gridConf().ms > 1000, 'иначе насос съедается гардом builded_time');
});

// ─────────────────────────── дедуп ───────────────────────────

test('страница, целиком повторяющая показанное, не рисуется вовсе', () => {
  const r = env();
  const g = makeGrid();

  r.qdl.gridBuild(g, page(1, 2, 3, 4, 5, 6));
  assert.strictEqual(r.qdl.gridNext(g, page(1, 2, 3, 4, 5, 6)).length, 0);
});

test('из страницы остаются только новые карточки, в исходном порядке', () => {
  const r = env();
  const g = makeGrid();

  r.qdl.gridBuild(g, page(1, 2, 3, 4, 5, 6, 7, 8, 9));
  const out = r.qdl.gridNext(g, page(7, 8, 9, 10, 11, 12));

  assert.deepStrictEqual(ids(out), [10, 11, 12]);
  // повторный проход тем же — уже ничего
  assert.strictEqual(r.qdl.gridNext(g, page(10, 11, 12)).length, 0);
});

test('фильм и сериал с одним id — РАЗНЫЕ карточки (у TMDB нумерация раздельная)', () => {
  const r = env();
  const g = makeGrid();
  const film = { id: 5, release_date: '2026-01-01' };
  const serial = { id: 5, first_air_date: '2026-01-01' };

  r.qdl.gridBuild(g, [film]);
  const out = r.qdl.gridNext(g, [serial, film]);

  assert.strictEqual(out.length, 1, 'сериал обязан остаться, фильм — уйти как повтор');
  assert.ok(out[0].first_air_date, 'остался именно сериал');
  assert.strictEqual(r.qdl.gridKey(film), 'movie:5');
  assert.strictEqual(r.qdl.gridKey(serial), 'tv:5');
  // media_type, если он есть, главнее эвристики по дате
  assert.strictEqual(r.qdl.gridKey({ id: 5, media_type: 'tv', release_date: '2026-01-01' }), 'tv:5');
});

test('🔴 карточка без надёжного ключа не выбрасывается НИКОГДА — терять нельзя', () => {
  const r = env();
  const g = makeGrid();
  const noid = [{ title: 'без id' }, { id: null }, { id: '' }];

  r.qdl.gridBuild(g, noid);
  const out = r.qdl.gridNext(g, noid);

  assert.strictEqual(out.length, 3, 'три раза подряд — и все три на месте');
  assert.strictEqual(r.qdl.gridKey({ title: 'без id' }), null);
});

test('реестр показанного не растёт бесконечно', () => {
  const r = env();
  const g = makeGrid();
  const cap = r.qdl.gridConf().cap;

  for (let p = 0; p < 60; p++) {
    const chunk = [];
    for (let i = 0; i < 100; i++) chunk.push(movie(p * 100 + i));
    r.qdl.gridNext(g, chunk);
  }
  assert.ok(g.qdl_seen_n <= cap, 'кап реестра держит долгую сессию');
});

// ─────────────────────────── насос ───────────────────────────
// 🔴 Зачем он вообще: Scroll.isEnd() на незаполненном гриде отдаёт TRUE, но onEnd зовётся только
// из scrollEnded(), а тот на таком гриде достижим ровно один раз — из hover:focus первой карточки
// через startScroll, и ровно в этот момент Next.onLoadNext себя запрещает гардом builded_time.
// Без насоса короткая страница = экран, который не догрузится никогда.

test('короткая первая страница догружается сама', () => {
  const r = env();
  const g = makeGrid();

  r.qdl.gridBuild(g, page(1, 2, 3, 4, 5));
  assert.deepStrictEqual(g._emitted, [], 'до срабатывания таймера — тишина');

  r.fire();
  assert.deepStrictEqual(g._emitted, ['loadNext']);
});

test('страница, съеденная дедупом целиком, не оставляет экран висеть', () => {
  const r = env();
  const g = makeGrid();

  r.qdl.gridBuild(g, page(1, 2, 3));
  r.fire();                                  // → loadNext
  g._emitted.length = 0;

  assert.strictEqual(r.qdl.gridNext(g, page(1, 2, 3)).length, 0);   // прироста нет
  r.fire();
  assert.deepStrictEqual(g._emitted, ['loadNext'], 'следующая страница всё равно поехала');
});

test('🔴 бюджет обрывает вечный цикл: подряд пустые страницы не долбятся бесконечно', () => {
  const r = env();
  const g = makeGrid();
  const max = r.qdl.gridConf().max;

  r.qdl.gridBuild(g, page(1, 2, 3));
  for (let i = 0; i < max + 10; i++) {
    r.fire();
    r.qdl.gridNext(g, page(1, 2, 3));        // сервер отдаёт одно и то же — прироста нет
  }

  const calls = g._emitted.filter((e) => e === 'loadNext').length;
  assert.strictEqual(calls, max, 'ровно бюджет и ни одной подтяжки сверх него');
});

test('прирост карточек обнуляет бюджет — длинный ряд не обрывается на восьмой странице', () => {
  const r = env();
  const g = makeGrid();
  const max = r.qdl.gridConf().max;
  let id = 100;

  r.qdl.gridBuild(g, page(1, 2, 3));
  for (let i = 0; i < max * 3; i++) {
    r.fire();
    r.qdl.gridNext(g, page(id++, id++));     // каждый раз что-то новое
  }

  const calls = g._emitted.filter((e) => e === 'loadNext').length;
  assert.ok(calls > max, 'живая выдача листается дальше бюджета: ' + calls);
});

test('заполненный грид насос не трогает — дальше решает зритель', () => {
  const r = env();
  const g = makeGrid();
  g.scroll.filled = true;

  r.qdl.gridBuild(g, page(1, 2, 3, 4, 5, 6));
  r.fire();
  assert.deepStrictEqual(g._emitted, []);
});

test('зависшие порции сливаются в DOM, но не больше жёсткого гарда', () => {
  const r = env();
  // isFilled() врёт (экран не отрисован) — гард обязан оборвать слив
  const g = makeGrid({ loaded: Array.from({ length: 50 }, () => [movie(1)]) });

  r.qdl.gridBuild(g, page(1, 2, 3));
  r.fire();

  const push = g._emitted.filter((e) => e === 'pushLoaded').length;
  assert.strictEqual(push, 12);
  assert.ok(!g._emitted.includes('loadNext'), 'пока очередь не пуста, новую страницу не тянем');
});

test('последняя страница каталога — насос молчит', () => {
  const r = env();
  const g = makeGrid({ total_pages: 3, object: { page: 3 } });

  r.qdl.gridBuild(g, page(1, 2));
  r.fire();
  assert.deepStrictEqual(g._emitted, []);
});

test('ушли с экрана — насос молчит (и не держит компонент)', () => {
  const r = env();
  const dead = makeGrid({ destroyed: true });
  const detached = makeGrid({ scroll: { isFilled: () => false, render: () => ({ isConnected: false }) } });

  r.qdl.gridBuild(dead, page(1, 2));
  r.qdl.gridBuild(detached, page(1, 2));
  r.fire();

  assert.deepStrictEqual(dead._emitted, []);
  assert.deepStrictEqual(detached._emitted, []);
});

test('один таймер на компонент: десять вызовов подряд не дают десять подтяжек', () => {
  const r = env();
  const g = makeGrid();

  for (let i = 0; i < 10; i++) r.qdl.gridPumpLater(g);
  assert.strictEqual(r.timers.filter((t) => t.ms === PUMP_MS).length, 1);
});

// ─────────────────────────── выключатели ───────────────────────────

test('выключатель устройства: дедуп и насос отключены целиком', () => {
  const r = env();
  const g = makeGrid();
  r.lampa.Storage.set('qdl_grid_dedup_off', true);

  r.qdl.gridBuild(g, page(1, 2, 3));
  const same = page(1, 2, 3);
  assert.strictEqual(r.qdl.gridNext(g, same), same, 'вход возвращается как есть');
  r.fire();
  assert.deepStrictEqual(g._emitted, []);
});

test('серверный выключатель lampa_settings.qdl_grid_dedup=false', () => {
  const r = env({ windowExtra: { lampa_settings: { qdl_grid_dedup: false } } });
  const g = makeGrid();

  r.qdl.gridBuild(g, page(1, 2, 3));
  const same = page(1, 2, 3);
  assert.strictEqual(r.qdl.gridNext(g, same), same);
});

test('сломанный компонент не роняет догрузку: на входе мусор — на выходе исходный список', () => {
  const r = env();
  const results = page(1, 2);

  assert.strictEqual(r.qdl.gridNext(null, results), results);
  assert.strictEqual(r.qdl.gridNext(undefined, results), results);
  assert.doesNotThrow(() => r.qdl.gridBuild(null, results));
});
