'use strict';
// «История просмотров» (qdl 2.61): наши экраны наполняют штатную favorite.history.
//
// Что здесь реально можно сломать и не заметить:
//  • карточка без TMDB id (jut-маркер, безымянная раздача) не должна попадать в историю —
//    открыть её потом будет нечем;
//  • сериал обязан нести name/original_name, иначе роутер Lampa строит method='movie' и
//    открывает ЧУЖОЙ объект TMDB: у movie и tv идентификаторы в разных пространствах;
//  • карточка jut.su обязана иметь source='jutsu' — иначе сканер рекомендаций пойдёт в TMDB
//    по мёртвому id, и обязана возвращать в jut_title, а не в пустую полную карточку;
//  • зелёная «Смотреть» на карточке фильма обязана писать КАРТОЧКУ, а не мету загрузки:
//    у меты может не быть вовсе (безымянная раздача).

const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

function boot(over) {
  const lampa = H.makeLampa(over || {});
  return { lampa, ...H.loadQdl({ lampa }) };
}

const added = (lampa) => lampa.Favorite._added;

// /qdl/episodes отвечает списком files, всё остальное — пусто (как в qdl-continue.test.js)
function fakeReq(files) {
  return {
    Reguest: function () {
      this.timeout = () => {};
      this.clear = () => {};
      this.silent = (url, ok) => ok(String(url).indexOf('/qdl/episodes') !== -1 ? (files || []) : []);
    },
  };
}

// Прогон watch() до конца: watchByHash → fetchEpisodes(ok) → gatePartial → старт плеера.
// files необязателен и строго последний: гейт с 2.93 смотрит на прогресс ФАЙЛА, а не карточки.
function watchWith(item, card, over, files) {
  const seen = [];
  const lampa = H.makeLampa(Object.assign(
    { Favorite: { _added: seen, add(where, c) { seen.push({ where, card: c }); } } },
    fakeReq(files || [{ index: 0, name: 'file.mkv' }]),
    over || {}));
  const { qdl } = H.loadQdl({ lampa });
  qdl.watch(item, card);
  return seen.filter((x) => x.where === 'history');
}

// ── что попадает в историю ────────────────────────────────────────────────

test('карточка с id уходит в историю с лимитом 100', () => {
  const { qdl, lampa } = boot();
  qdl.noteHistory({ id: 1315772, title: 'Фильм' });

  assert.strictEqual(added(lampa).length, 1);
  assert.strictEqual(added(lampa)[0].where, 'history');
  assert.strictEqual(added(lampa)[0].limit, 100);   // то же число, что у самой Lampa
  assert.strictEqual(added(lampa)[0].card.id, 1315772);
});

test('карточка без пригодного id в историю не идёт', () => {
  const { qdl, lampa } = boot();
  // id === 0 у сервера означает «TMDB id нет» — так помечены jut-маркеры
  [null, undefined, {}, { id: 0 }, { id: '0' }, { id: '' }, { id: null }].forEach((c) => qdl.noteHistory(c));
  assert.strictEqual(added(lampa).length, 0);
});

test('падение Lampa.Favorite не роняет запуск плеера', () => {
  const { qdl } = boot({ Favorite: { add() { throw new Error('boom'); } } });
  assert.doesNotThrow(() => qdl.noteHistory({ id: 1 }));
});

// ── нормализация сериала ──────────────────────────────────────────────────

test('сериалу дописываются name/original_name/first_air_date', () => {
  const { qdl } = boot();
  // ровно то, что отдаёт наш slimCard: полей сериала в нём нет вовсе
  const c = qdl.historyCard({
    id: 270603, media_type: 'tv', title: 'Укрытие', original_title: 'Silo', release_date: '2023-05-05',
  });

  assert.strictEqual(c.name, 'Укрытие');
  assert.strictEqual(c.original_name, 'Silo');
  assert.strictEqual(c.first_air_date, '2023-05-05');
});

test('фильм остаётся фильмом', () => {
  const { qdl } = boot();
  const c = qdl.historyCard({ id: 1, media_type: 'movie', title: 'Ф', release_date: '2025-01-01' });

  assert.strictEqual(c.name, undefined);            // иначе роутер решил бы, что это сериал
  assert.strictEqual(c.first_air_date, undefined);
});

test('исходную карточку не портим — её дают из активности', () => {
  const { qdl } = boot();
  const src = { id: 1, media_type: 'tv', title: 'X' };
  qdl.historyCard(src);
  assert.strictEqual(src.name, undefined);
});

// ── карточка jut.su ───────────────────────────────────────────────────────

test('карточка jut несёт слаг внутри id и не притворяется TMDB', () => {
  const { qdl } = boot();
  const c = qdl.jutHistoryCard('joutai-ijou-skill', 'Статус-скилл', 7);

  assert.strictEqual(c.id, 'jut:joutai-ijou-skill');
  assert.strictEqual(c.source, 'jutsu');            // сканер рекомендаций берёт только cub/tmdb
  assert.strictEqual(c.title, 'Статус-скилл');
  assert.match(c.img, /\/qdl\/jut\/poster\?slug=joutai-ijou-skill/);
  assert.strictEqual(qdl.jutSlugFromCardId(c.id), 'joutai-ijou-skill');
});

test('слаг восстанавливается только из наших id', () => {
  const { qdl } = boot();
  assert.strictEqual(qdl.jutSlugFromCardId(270603), '');
  assert.strictEqual(qdl.jutSlugFromCardId('270603'), '');
  assert.strictEqual(qdl.jutSlugFromCardId(null), '');
});

test('поля карточки jut переживают белый список Lampa (card_fields)', () => {
  // Utils.clearCard хранит только поля из card_fields — произвольного jut_slug там нет,
  // поэтому слаг и едет внутри id. Список — из отдаваемого бандла.
  const CARD_FIELDS = ['poster_path', 'overview', 'release_date', 'genre_ids', 'id', 'original_title',
    'original_language', 'title', 'backdrop_path', 'popularity', 'vote_count', 'vote_average', 'imdb_id',
    'kinopoisk_id', 'original_name', 'name', 'first_air_date', 'origin_country', 'status', 'pg',
    'release_quality', 'imdb_rating', 'kp_rating', 'source', 'number_of_seasons', 'number_of_episodes',
    'next_episode_to_air', 'img', 'poster', 'background_image'];

  const { qdl } = boot();
  Object.keys(qdl.jutHistoryCard('slug', 'T')).forEach((k) => {
    assert.ok(CARD_FIELDS.indexOf(k) >= 0, 'поле ' + k + ' не переживёт clearCard');
  });
});

// ── вход из истории ───────────────────────────────────────────────────────

function routed(cardOverrides) {
  const pushes = [];
  const lampa = H.makeLampa({ Activity: { push(o) { pushes.push(o); } } });
  const { qdl } = H.loadQdl({ lampa });
  qdl.initHistoryRouting();
  lampa.Activity.push(Object.assign({ component: 'full', id: 1, card: {} }, cardOverrides));
  return pushes;
}

test('jut-карточка из истории открывает jut_title, а не пустую полную карточку', () => {
  const p = routed({ card: { id: 'jut:one-piece', source: 'jutsu', title: 'One Piece' } });

  assert.strictEqual(p.length, 1);
  assert.strictEqual(p[0].component, 'jut_title');
  assert.strictEqual(p[0].jut_slug, 'one-piece');
  assert.strictEqual(p[0].title, 'One Piece');
});

test('обычная карточка проходит насквозь', () => {
  const p = routed({ card: { id: 270603, source: 'tmdb', name: 'Silo' } });

  assert.strictEqual(p.length, 1);
  assert.strictEqual(p[0].component, 'full');
  assert.strictEqual(p[0].card.id, 270603);
});

test('чужая активность не перехватывается', () => {
  const pushes = [];
  const lampa = H.makeLampa({ Activity: { push(o) { pushes.push(o); } } });
  const { qdl } = H.loadQdl({ lampa });
  qdl.initHistoryRouting();

  // тот же source, но компонент не 'full' — трогать нельзя
  lampa.Activity.push({ component: 'jut_episodes', card: { id: 'jut:x', source: 'jutsu' } });
  assert.strictEqual(pushes[0].component, 'jut_episodes');
});

test('перехват ставится один раз', () => {
  const pushes = [];
  const lampa = H.makeLampa({ Activity: { push(o) { pushes.push(o); } } });
  const { qdl, sandbox } = H.loadQdl({ lampa });
  qdl.initHistoryRouting();
  const first = sandbox.Lampa.Activity.push;
  qdl.initHistoryRouting();
  assert.strictEqual(sandbox.Lampa.Activity.push, first);
});

// ── фолбэк карточки для восстановленной активности ────────────────────────

test('карточка берётся из ближайшей ПОЛНОЙ активности', () => {
  const stack = [
    { component: 'favorite', card: { id: 999 } },     // чужой экран — брать нельзя
    { component: 'full', card: { id: 270603 } },
    { component: 'qdl_episodes', qdl_hash: 'h' },
  ];
  const { qdl } = boot({ Activity: { all: () => stack } });
  assert.strictEqual(qdl.activityCard().id, 270603);
});

test('без полной карточки в стеке фолбэк молчит', () => {
  const { qdl } = boot({ Activity: { all: () => [{ component: 'qdl_downloads' }] } });
  assert.strictEqual(qdl.activityCard(), null);
});

// ── воронка «Загрузок» ────────────────────────────────────────────────────

test('watch пишет переданную карточку, а не мету загрузки', () => {
  // мета у раздачи может быть чужой/пустой — на полной карточке правда именно в card
  const hist = watchWith({ hash: 'h', progress: 1, meta: { id: 111, title: 'мета' } },
                         { id: 270603, title: 'карточка' });

  assert.strictEqual(hist.length, 1);
  assert.strictEqual(hist[0].card.id, 270603);
});

test('без карточки берётся мета загрузки', () => {
  const hist = watchWith({ hash: 'h', progress: 1, meta: { id: 111, title: 'мета' } });
  assert.strictEqual(hist.length, 1);
  assert.strictEqual(hist[0].card.id, 111);
});

test('сериал уходит на экран серий и историю пишет ОН, а не watch', () => {
  // два видеофайла → chooseEpisode: запись должна случиться в момент старта конкретной серии
  const hist = watchWith({ hash: 'h', progress: 1, meta: { id: 5 } }, null,
    fakeReq([{ index: 0, name: 's1e1.mkv' }, { index: 1, name: 's1e2.mkv' }]));
  assert.strictEqual(hist.length, 0);
});

test('jut-маркер без TMDB id всё равно попадает в историю своей карточкой', () => {
  const hist = watchWith({ hash: 'h', progress: 1, meta: { id: 0 }, name: 'Anime',
                           jut: { slug: 'one-piece', titleRu: 'Ван-Пис' } });

  assert.strictEqual(hist.length, 1);
  assert.strictEqual(hist[0].card.id, 'jut:one-piece');
  assert.strictEqual(hist[0].card.title, 'Ван-Пис');
});

test('недокачанная раздача не пишет историю (qdl 2.93 — играть нельзя вовсе)', () => {
  // Гейт жёсткий: диалог «Дождитесь загрузки» пути в плеер не даёт, значит и истории нет.
  // Прогресс берётся у ФАЙЛА (гейт переехал внутрь watchByHash), поэтому недокачанность
  // задаём на нём, а не на карточке.
  const hist = watchWith({ hash: 'h', progress: 0.4, meta: { id: 5 } }, { id: 5 },
    { Select: { show() {}, listener: { follow() {}, send() {} } } },
    [{ index: 0, name: 'file.mkv', progress: 0.4 }]);
  assert.strictEqual(hist.length, 0);
});

// ── статический гард ──────────────────────────────────────────────────────

test('воронки «Загрузок» и jut.su не обходят noteHistory', () => {
  // Регрессия-ловушка того же рода, что qdl-live-uid: код легко добавить мимо воронки.
  // Требование: каждая из четырёх точек старта несёт вызов noteHistory рядом.
  const src = H.qdlSource();

  // экран серий (окно расширено: перед noteHistory встал гейт недокачанной серии, qdl 2.93)
  assert.match(src, /this\.play = function \(i\) \{[\s\S]{0,500}?noteHistory\(/);
  // одиночный файл и фолбэк «серий не нашли»
  assert.match(src, /function watchByHash\(hash, name, card, gateItem\)[\s\S]{0,1400}?noteHistory\(card\)/);
  // онлайн jut.su
  assert.match(src, /function jutPlay\([\s\S]{0,900}?noteHistory\(jutHistoryCard\(slug/);
  // и сама воронка одна — не расползлась копиями
  assert.strictEqual((src.match(/function noteHistory\(/g) || []).length, 1);
});
