'use strict';
const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Load the plugin's pure helpers once; they are stateless for the fns under test.
const { qdl } = H.loadQdl();

// Arrays produced entirely inside the plugin's vm realm (e.g. `arr || []` when arr is
// null) carry the vm's Array.prototype, which assert.deepStrictEqual rejects as
// "same structure but not reference-equal". Normalize to a host array first (spread
// rebuilds with the host prototype) so we compare CONTENTS, which is what we care about.
function arrEq(actual, expected, msg) {
  assert.ok(Array.isArray(actual), 'expected an array');
  assert.deepStrictEqual(Array.prototype.slice.call(actual).map(function (x) {
    return x && typeof x === 'object' ? Object.assign({}, x) : x;
  }), expected, msg);
}

// ─────────────────────────────── esc ───────────────────────────────
// esc: escapes & < > " (NOT single quote). null/undefined -> ''.
test('esc: escapes the four html specials in one string', () => {
  assert.strictEqual(qdl.esc('<a>&"'), '&lt;a&gt;&amp;&quot;');
});

test('esc: each special maps individually', () => {
  assert.strictEqual(qdl.esc('&'), '&amp;');
  assert.strictEqual(qdl.esc('<'), '&lt;');
  assert.strictEqual(qdl.esc('>'), '&gt;');
  assert.strictEqual(qdl.esc('"'), '&quot;');
});

test('esc: does NOT escape single quote', () => {
  assert.strictEqual(qdl.esc("it's a 'test'"), "it's a 'test'");
});

test('esc: leaves ordinary text untouched', () => {
  assert.strictEqual(qdl.esc('Hello World 123'), 'Hello World 123');
});

test('esc: null and undefined coerce to empty string', () => {
  assert.strictEqual(qdl.esc(null), '');
  assert.strictEqual(qdl.esc(undefined), '');
});

test('esc: numbers and other non-strings are stringified', () => {
  assert.strictEqual(qdl.esc(0), '0');            // 0 is not null -> String(0)
  assert.strictEqual(qdl.esc(42), '42');
  assert.strictEqual(qdl.esc(false), 'false');    // false != null -> String(false)
});

test('esc: replaces every occurrence (global)', () => {
  assert.strictEqual(qdl.esc('&&'), '&amp;&amp;');
  assert.strictEqual(qdl.esc('a<b<c'), 'a&lt;b&lt;c');
});

test('esc: mixed content preserves order and non-specials', () => {
  assert.strictEqual(qdl.esc('x & y < z > "q"'), 'x &amp; y &lt; z &gt; &quot;q&quot;');
});

// ─────────────────────────────── names ───────────────────────────────
// names: maps array of {name} objects OR raw strings; falsy filtered out.
test('names: maps array of {name} objects to their names', () => {
  arrEq(qdl.names([{ name: 'Action' }, { name: 'Drama' }]), ['Action', 'Drama']);
});

test('names: passes through raw string entries', () => {
  arrEq(qdl.names(['US', 'GB']), ['US', 'GB']);
});

test('names: mixes objects and strings', () => {
  arrEq(qdl.names([{ name: 'Action' }, 'Extra']), ['Action', 'Extra']);
});

test('names: object without .name falls back to the object then filtered by Boolean (truthy object kept)', () => {
  // {foo:1} has no .name -> returns the object itself; object is truthy so it survives filter.
  const out = qdl.names([{ foo: 1 }]);
  assert.strictEqual(out.length, 1);
  assert.deepStrictEqual(Object.assign({}, out[0]), { foo: 1 });
});

test('names: filters out falsy entries (null, empty string, 0)', () => {
  arrEq(qdl.names([null, '', 0, 'Keep', undefined, false]), ['Keep']);
});

test('names: entry {name:""} is NOT dropped — falsy .name makes ternary return the object itself', () => {
  // BUG?: `(x && x.name) ? x.name : x` — when x.name === '' (falsy), the ternary
  // returns the whole object (truthy) instead of the empty name, so a {name:''} entry
  // survives .filter(Boolean) as an object rather than being removed.
  arrEq(qdl.names([{ name: '' }, { name: 'X' }]), [{ name: '' }, 'X']);
});

test('names: null/undefined input -> empty array', () => {
  arrEq(qdl.names(null), []);
  arrEq(qdl.names(undefined), []);
});

test('names: empty array -> empty array', () => {
  arrEq(qdl.names([]), []);
});

// ─────────────────────────────── slimCard ───────────────────────────────
// null input -> null
test('slimCard: null input returns null', () => {
  assert.strictEqual(qdl.slimCard(null), null);
});

test('slimCard: undefined input returns null', () => {
  assert.strictEqual(qdl.slimCard(undefined), null);
});

// isTv branch 1: explicit media_type === 'tv'
test('slimCard: media_type==="tv" -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', media_type: 'tv' });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 2: method === 'tv'
test('slimCard: method==="tv" -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', method: 'tv' });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 3a: number_of_seasons present
test('slimCard: number_of_seasons present -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', number_of_seasons: 3 });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 3b: number_of_episodes present
test('slimCard: number_of_episodes present -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', number_of_episodes: 10 });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 3c: seasons present (truthy array)
test('slimCard: seasons array present -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', seasons: [{}] });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 3d: episode_run_time present (truthy array)
test('slimCard: episode_run_time present -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', episode_run_time: [45] });
  assert.strictEqual(c.media_type, 'tv');
});

// isTv branch 4: first_air_date && !release_date
test('slimCard: first_air_date without release_date -> tv', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', first_air_date: '2021-05-06' });
  assert.strictEqual(c.media_type, 'tv');
});

test('slimCard: first_air_date AND release_date both present -> NOT via that branch (falls to movie unless other signal)', () => {
  // has both dates, no name-only signal, title present -> movie
  const c = qdl.slimCard({ id: 1, title: 'X', first_air_date: '2021-05-06', release_date: '2021-01-01' });
  assert.strictEqual(c.media_type, 'movie');
});

// isTv branch 5: name && !title
test('slimCard: name without title -> tv', () => {
  const c = qdl.slimCard({ id: 1, name: 'The Show' });
  assert.strictEqual(c.media_type, 'tv');
});

test('slimCard: both name and title present -> movie (name-only branch fails, no other signal)', () => {
  const c = qdl.slimCard({ id: 1, name: 'N', title: 'T' });
  assert.strictEqual(c.media_type, 'movie');
});

// otherwise -> movie
test('slimCard: plain movie with title only -> movie', () => {
  const c = qdl.slimCard({ id: 9, title: 'A Film', release_date: '2019-03-04' });
  assert.strictEqual(c.media_type, 'movie');
});

test('slimCard: empty-ish object (no signals) -> movie', () => {
  const c = qdl.slimCard({ id: 0 });
  assert.strictEqual(c.media_type, 'movie');
});

// ── field mapping ──
test('slimCard: maps id straight through', () => {
  assert.strictEqual(qdl.slimCard({ id: 777, title: 'X' }).id, 777);
});

test('slimCard: title falls back to name when title absent', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, name: 'OnlyName' }).title, 'OnlyName');
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'T', name: 'N' }).title, 'T');
});

test('slimCard: original_title falls back to original_name', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', original_name: 'ON' }).original_title, 'ON');
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', original_title: 'OT', original_name: 'ON' }).original_title, 'OT');
});

test('slimCard: overview and tagline mapped', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', overview: 'plot', tagline: 'a line' });
  assert.strictEqual(c.overview, 'plot');
  assert.strictEqual(c.tagline, 'a line');
});

// release_date: movie prefers release_date, else first_air_date, else ''
test('slimCard: release_date field prefers release_date', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', release_date: '2019-03-04', first_air_date: '2020-01-01' });
  assert.strictEqual(c.release_date, '2019-03-04');
});

test('slimCard: release_date field falls back to first_air_date', () => {
  const c = qdl.slimCard({ id: 1, name: 'X', first_air_date: '2020-01-01' });
  assert.strictEqual(c.release_date, '2020-01-01');
});

test('slimCard: release_date defaults to empty string when no dates', () => {
  const c = qdl.slimCard({ id: 1, title: 'X' });
  assert.strictEqual(c.release_date, '');
});

// year = first 4 chars of the chosen date
test('slimCard: year is first 4 chars of date', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', release_date: '2019-03-04' }).year, '2019');
});

test('slimCard: year is empty string when no date', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X' }).year, '');
});

test('slimCard: year from first_air_date fallback', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, name: 'X', first_air_date: '2005-12-31' }).year, '2005');
});

test('slimCard: vote_average, poster_path, backdrop_path, status mapped through', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', vote_average: 7.8, poster_path: '/p.jpg', backdrop_path: '/b.jpg', status: 'Released' });
  assert.strictEqual(c.vote_average, 7.8);
  assert.strictEqual(c.poster_path, '/p.jpg');
  assert.strictEqual(c.backdrop_path, '/b.jpg');
  assert.strictEqual(c.status, 'Released');
});

// genres via names
test('slimCard: genres flattened via names()', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', genres: [{ name: 'Action' }, { name: 'Drama' }] });
  arrEq(c.genres, ['Action', 'Drama']);
});

test('slimCard: genres absent -> empty array', () => {
  arrEq(qdl.slimCard({ id: 1, title: 'X' }).genres, []);
});

// runtime: m.runtime || episode_run_time[0] || 0
test('slimCard: runtime uses m.runtime when present', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', runtime: 120 }).runtime, 120);
});

test('slimCard: runtime falls back to episode_run_time[0]', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, name: 'X', episode_run_time: [42, 40] }).runtime, 42);
});

test('slimCard: runtime defaults to 0 when neither present', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X' }).runtime, 0);
});

test('slimCard: runtime is 0 when episode_run_time is empty array', () => {
  // empty array is truthy, but [0] is undefined -> falls to 0
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', episode_run_time: [] }).runtime, 0);
});

// countries: names(production_countries).concat(origin_country || [])
test('slimCard: countries concats production_countries names with origin_country', () => {
  const c = qdl.slimCard({
    id: 1, title: 'X',
    production_countries: [{ name: 'United States' }],
    origin_country: ['US']
  });
  arrEq(c.countries, ['United States', 'US']);
});

test('slimCard: countries only from production_countries when no origin_country', () => {
  const c = qdl.slimCard({ id: 1, title: 'X', production_countries: [{ name: 'France' }] });
  arrEq(c.countries, ['France']);
});

test('slimCard: countries only origin_country when no production_countries', () => {
  const c = qdl.slimCard({ id: 1, name: 'X', origin_country: ['JP'] });
  arrEq(c.countries, ['JP']);
});

test('slimCard: countries empty when neither present', () => {
  arrEq(qdl.slimCard({ id: 1, title: 'X' }).countries, []);
});

// number_of_seasons / number_of_episodes mapped through
test('slimCard: number_of_seasons and number_of_episodes mapped through', () => {
  const c = qdl.slimCard({ id: 1, name: 'X', number_of_seasons: 4, number_of_episodes: 40 });
  assert.strictEqual(c.number_of_seasons, 4);
  assert.strictEqual(c.number_of_episodes, 40);
});

// age: m.age || m.certification || ''
test('slimCard: age uses m.age first', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', age: '18', certification: 'R' }).age, '18');
});

test('slimCard: age falls back to certification', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', certification: 'PG-13' }).age, 'PG-13');
});

test('slimCard: age defaults to empty string', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X' }).age, '');
});

// source: m.source || 'tmdb'
test('slimCard: source uses m.source when present', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X', source: 'jackett' }).source, 'jackett');
});

test('slimCard: source defaults to "tmdb"', () => {
  assert.strictEqual(qdl.slimCard({ id: 1, title: 'X' }).source, 'tmdb');
});

// A representative full tv object exercising many mappings at once
test('slimCard: full tv object maps all fields coherently', () => {
  const c = qdl.slimCard({
    id: 100,
    name: 'Breaking Show',
    original_name: 'Breaking Show Orig',
    overview: 'desc',
    tagline: 'tag',
    first_air_date: '2008-01-20',
    vote_average: 9.5,
    poster_path: '/p.jpg',
    backdrop_path: '/b.jpg',
    genres: [{ name: 'Crime' }, { name: 'Drama' }],
    episode_run_time: [47],
    production_countries: [{ name: 'United States of America' }],
    origin_country: ['US'],
    status: 'Ended',
    number_of_seasons: 5,
    number_of_episodes: 62,
    certification: 'TV-MA'
  });
  assert.strictEqual(c.media_type, 'tv');
  assert.strictEqual(c.id, 100);
  assert.strictEqual(c.title, 'Breaking Show');
  assert.strictEqual(c.original_title, 'Breaking Show Orig');
  assert.strictEqual(c.release_date, '2008-01-20');
  assert.strictEqual(c.year, '2008');
  assert.strictEqual(c.runtime, 47);
  arrEq(c.genres, ['Crime', 'Drama']);
  arrEq(c.countries, ['United States of America', 'US']);
  assert.strictEqual(c.status, 'Ended');
  assert.strictEqual(c.age, 'TV-MA');
  assert.strictEqual(c.source, 'tmdb');
});

// ─────────────────────────────── cleanName ───────────────────────────────
// cleanName: split at [ or (, at /, replace . and _ with space, strip year+after, strip quality+after, trim.
test('cleanName: splits at open bracket "[" keeping only the head', () => {
  assert.strictEqual(qdl.cleanName('Movie Name [tracker info]'), 'Movie Name');
});

test('cleanName: splits at open paren "(" keeping only the head', () => {
  assert.strictEqual(qdl.cleanName('Movie Name (2019)'), 'Movie Name');
});

test('cleanName: splits at first of [ or ( — whichever appears (regex alternation)', () => {
  assert.strictEqual(qdl.cleanName('Head (paren) [bracket]'), 'Head');
});

test('cleanName: splits at slash "/" keeping only first segment', () => {
  assert.strictEqual(qdl.cleanName('Title / Alt Title'), 'Title');
});

test('cleanName: replaces dots with spaces', () => {
  assert.strictEqual(qdl.cleanName('The.Movie.Name'), 'The Movie Name');
});

test('cleanName: replaces underscores with spaces', () => {
  assert.strictEqual(qdl.cleanName('The_Movie_Name'), 'The Movie Name');
});

test('cleanName: strips year (19xx) and everything after it', () => {
  assert.strictEqual(qdl.cleanName('Old Film 1999 extra stuff'), 'Old Film');
});

test('cleanName: strips year (20xx) and everything after it', () => {
  assert.strictEqual(qdl.cleanName('New Film 2021 extra stuff'), 'New Film');
});

test('cleanName: strips quality token and everything after (case-insensitive)', () => {
  assert.strictEqual(qdl.cleanName('Movie 1080p rest ignored'), 'Movie');
  assert.strictEqual(qdl.cleanName('Movie WEB-DL rest'), 'Movie');
  assert.strictEqual(qdl.cleanName('Movie webrip rest'), 'Movie');       // lowercase matched (i flag)
  assert.strictEqual(qdl.cleanName('Movie BluRay rest'), 'Movie');
  assert.strictEqual(qdl.cleanName('Movie x265 rest'), 'Movie');
  assert.strictEqual(qdl.cleanName('Movie 4K rest'), 'Movie');
  assert.strictEqual(qdl.cleanName('Movie HEVC rest'), 'Movie');
});

test('cleanName: combined dot-separated title with year and quality tail', () => {
  assert.strictEqual(qdl.cleanName('The.Show.2019.1080p.WEB-DL'), 'The Show');
});

test('cleanName: trims surrounding whitespace', () => {
  assert.strictEqual(qdl.cleanName('  Padded Name  '), 'Padded Name');
});

test('cleanName: null/undefined -> empty string', () => {
  assert.strictEqual(qdl.cleanName(null), '');
  assert.strictEqual(qdl.cleanName(undefined), '');
});

test('cleanName: plain title unchanged', () => {
  assert.strictEqual(qdl.cleanName('Simple Title'), 'Simple Title');
});

test('cleanName: order — bracket split happens before dot replacement', () => {
  // '(' split first removes the parenthetical, then dots -> spaces
  assert.strictEqual(qdl.cleanName('A.B.C (junk)'), 'A B C');
});

test('cleanName: WEB-DL with optional dash absent (WEBDL) also matched', () => {
  // regex: WEB-?DL — dash optional
  assert.strictEqual(qdl.cleanName('Film WEBDL group'), 'Film');
});

test('cleanName: quality requires word boundary — embedded "1080p" inside a word still matches token at boundary', () => {
  // '720p' is at a boundary here
  assert.strictEqual(qdl.cleanName('Series 720p HDRip'), 'Series');
});

// ─────────────────────────────── videoFiles ───────────────────────────────
// videoFiles: filter by video-extension regex, then natural sort by name.
test('videoFiles: keeps only recognized video extensions', () => {
  const out = qdl.videoFiles([
    { name: 'a.mkv' }, { name: 'b.mp4' }, { name: 'c.avi' },
    { name: 'd.ts' }, { name: 'e.m4v' }, { name: 'f.webm' }, { name: 'g.mov' },
    { name: 'sub.srt' }, { name: 'readme.txt' }, { name: 'cover.jpg' }
  ]);
  assert.deepStrictEqual(out.map(function (f) { return f.name; }),
    ['a.mkv', 'b.mp4', 'c.avi', 'd.ts', 'e.m4v', 'f.webm', 'g.mov']);
});

test('videoFiles: extension match is case-insensitive', () => {
  const out = qdl.videoFiles([{ name: 'Movie.MKV' }, { name: 'Clip.Mp4' }]);
  assert.strictEqual(out.length, 2);
});

test('videoFiles: filters out files with no name property', () => {
  const out = qdl.videoFiles([{ index: 1 }, { name: 'ok.mp4' }]);
  assert.deepStrictEqual(out.map(function (f) { return f.name; }), ['ok.mp4']);
});

test('videoFiles: natural numeric sort by name (2 before 10)', () => {
  const out = qdl.videoFiles([
    { name: 'ep10.mkv' }, { name: 'ep2.mkv' }, { name: 'ep1.mkv' }
  ]);
  assert.deepStrictEqual(out.map(function (f) { return f.name; }),
    ['ep1.mkv', 'ep2.mkv', 'ep10.mkv']);
});

test('videoFiles: sort is stable/natural across mixed digits', () => {
  const out = qdl.videoFiles([
    { name: 'S01E03.mp4' }, { name: 'S01E20.mp4' }, { name: 'S01E01.mp4' }
  ]);
  assert.deepStrictEqual(out.map(function (f) { return f.name; }),
    ['S01E01.mp4', 'S01E03.mp4', 'S01E20.mp4']);
});

test('videoFiles: null/undefined input -> empty array', () => {
  arrEq(qdl.videoFiles(null), []);
  arrEq(qdl.videoFiles(undefined), []);
});

test('videoFiles: empty input -> empty array', () => {
  arrEq(qdl.videoFiles([]), []);
});

test('videoFiles: extension must be at end of name (mkv.txt excluded)', () => {
  const out = qdl.videoFiles([{ name: 'file.mkv.txt' }, { name: 'real.mkv' }]);
  assert.deepStrictEqual(out.map(function (f) { return f.name; }), ['real.mkv']);
});

// ─────────────────────────────── baseName ───────────────────────────────
// baseName: last segment after both / and \.
test('baseName: extracts file after forward slashes', () => {
  assert.strictEqual(qdl.baseName('a/b/c/file.mkv'), 'file.mkv');
});

test('baseName: extracts file after backslashes', () => {
  assert.strictEqual(qdl.baseName('C:\\dir\\sub\\file.mp4'), 'file.mp4');
});

test('baseName: handles mixed forward and back slashes', () => {
  assert.strictEqual(qdl.baseName('a/b\\c/d\\movie.avi'), 'movie.avi');
});

test('baseName: plain filename returned unchanged', () => {
  assert.strictEqual(qdl.baseName('file.mkv'), 'file.mkv');
});

test('baseName: null/undefined/empty -> empty string', () => {
  assert.strictEqual(qdl.baseName(null), '');
  assert.strictEqual(qdl.baseName(undefined), '');
  assert.strictEqual(qdl.baseName(''), '');
});

test('baseName: trailing slash yields empty last segment', () => {
  // 'a/b/'.split('/').pop() === '' -> '' after \\ split too
  assert.strictEqual(qdl.baseName('a/b/'), '');
});

test('baseName: forward-slash split runs before backslash split', () => {
  // 'x\\y/z' -> split('/') -> ['x\\y','z'] -> pop 'z' -> split('\\') -> 'z'
  assert.strictEqual(qdl.baseName('x\\y/z'), 'z');
});
