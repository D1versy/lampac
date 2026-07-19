const test = require('node:test');
const assert = require('node:assert');
const H = require('./harness');

// Characterization tests for qdl.js URL/format/pref helpers.
// STYLE: assert ACTUAL behavior. Base API is the literal '{localhost}'.

const API = '{localhost}';

// ─────────────────────────── isBrowser ───────────────────────────

test('isBrowser: Platform.tv() present takes precedence over UA', () => {
  // tv()=>true  => NOT a browser (inversion of tv()), UA ignored.
  {
    const { qdl } = H.loadQdl({
      lampa: H.makeLampa({ Platform: { tv: () => true } }),
      navigator: { userAgent: 'Mozilla/5.0 (X11; Linux x86_64) Chrome/120' },
    });
    assert.strictEqual(qdl.isBrowser(), false);
  }
  // tv()=>false => IS a browser, even if UA smells like a TV.
  {
    const { qdl } = H.loadQdl({
      lampa: H.makeLampa({ Platform: { tv: () => false } }),
      navigator: { userAgent: 'Mozilla/5.0 (SMART-TV; Tizen 6.0)' },
    });
    assert.strictEqual(qdl.isBrowser(), true);
  }
});

test('isBrowser: no Platform.tv falls back to UA regex', () => {
  const mk = (ua) =>
    H.loadQdl({
      lampa: H.makeLampa(), // Platform:{} => no tv()
      navigator: { userAgent: ua },
    }).qdl;

  // TV-like UAs => NOT browser.
  assert.strictEqual(mk('Tizen 6.0').isBrowser(), false);
  assert.strictEqual(mk('Mozilla webOS TV').isBrowser(), false);
  assert.strictEqual(mk('WebOS').isBrowser(), false); // Web0?S allows 'WebOS'
  assert.strictEqual(mk('SMART-TV browser').isBrowser(), false);
  assert.strictEqual(mk('SmartTV').isBrowser(), false);
  assert.strictEqual(mk('HbbTV/1.5').isBrowser(), false);
  assert.strictEqual(mk('AppleTV').isBrowser(), false);
  assert.strictEqual(mk('CrKey/1.0').isBrowser(), false);
  assert.strictEqual(mk('Android TV Build').isBrowser(), false);
  assert.strictEqual(mk('NetCast 4.0').isBrowser(), false);
  assert.strictEqual(mk('VIDAA').isBrowser(), false);
  assert.strictEqual(mk('MSX').isBrowser(), false);

  // Ordinary desktop / empty UA => browser.
  assert.strictEqual(mk('Mozilla/5.0 (X11; Linux x86_64) Chrome/120').isBrowser(), true);
  assert.strictEqual(mk('').isBrowser(), true);
  // Plain 'Android' (phone, no 'TV') => browser per this regex.
  assert.strictEqual(mk('Android 13; Mobile').isBrowser(), true);
});

test('isBrowser: Platform.tv throwing is swallowed, falls through to UA', () => {
  const { qdl } = H.loadQdl({
    lampa: H.makeLampa({ Platform: { tv: () => { throw new Error('boom'); } } }),
    navigator: { userAgent: 'Tizen' },
  });
  // try/catch swallows the throw, then UA 'Tizen' => not browser.
  assert.strictEqual(qdl.isBrowser(), false);
});

// ─────────────────────────── isMobile ───────────────────────────

test('isMobile: Platform.is("android") => true regardless of UA', () => {
  const { qdl } = H.loadQdl({
    lampa: H.makeLampa({ Platform: { is: (p) => p === 'android' } }),
    navigator: { userAgent: 'Mozilla/5.0 (X11; Linux) Chrome/120' }, // no mobile token
  });
  assert.strictEqual(qdl.isMobile(), true);
});

test('isMobile: no Platform.is => UA regex', () => {
  const mk = (ua) =>
    H.loadQdl({ lampa: H.makeLampa(), navigator: { userAgent: ua } }).qdl;

  assert.strictEqual(mk('Android 13').isMobile(), true);
  assert.strictEqual(mk('iPhone OS 17').isMobile(), true);
  assert.strictEqual(mk('iPad').isMobile(), true);
  assert.strictEqual(mk('iPod').isMobile(), true);
  assert.strictEqual(mk('Something Mobile Safari').isMobile(), true);

  assert.strictEqual(mk('Mozilla/5.0 (X11; Linux x86_64) Chrome/120').isMobile(), false);
  assert.strictEqual(mk('').isMobile(), false);
});

test('isMobile: Platform.is present but not android => falls back to UA', () => {
  const { qdl } = H.loadQdl({
    lampa: H.makeLampa({ Platform: { is: () => false } }),
    navigator: { userAgent: 'iPhone' },
  });
  assert.strictEqual(qdl.isMobile(), true);
});

// ─────────────────────────── streamUrl ───────────────────────────

test('streamUrl: TV + audio "o" => direct /qdl/stream', () => {
  const { qdl } = H.loadQdl({
    lampa: H.makeLampa({ Platform: { tv: () => true } }),
  });
  // ext=false (audio 'o' does not start with f/d), isBrowser=false => stream branch.
  assert.strictEqual(qdl.streamUrl('HASH', 0, 'o'), API + '/qdl/stream?hash=HASH&index=0');
});

test('streamUrl: TV + no audio, index>=0 => direct stream with index', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  assert.strictEqual(qdl.streamUrl('H', 3), API + '/qdl/stream?hash=H&index=3');
});

test('streamUrl: TV + index<0 (e.g. -1) => stream without index param', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  assert.strictEqual(qdl.streamUrl('H', -1, 'o'), API + '/qdl/stream?hash=H');
});

test('streamUrl: browser + audio "o" => HLS (isBrowser triggers HLS)', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => false } }) });
  // audio 'o' => not appended to key; index 0 => key HASH_0
  assert.strictEqual(qdl.streamUrl('HASH', 0, 'o'), API + '/qdl/hls/HASH_0/playlist.m3u8');
});

test('streamUrl: browser + no audio, index<0 => HLS key uses -1', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => false } }) });
  assert.strictEqual(qdl.streamUrl('H', -1), API + '/qdl/hls/H_-1/playlist.m3u8');
});

test('streamUrl: TV + audio "d123" => forced HLS (external dub always HLS)', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  // audio starts with 'd' => ext=true => HLS even on TV. audio !== 'o' => appended.
  assert.strictEqual(qdl.streamUrl('H', 2, 'd123'), API + '/qdl/hls/H_2_d123/playlist.m3u8');
});

test('streamUrl: TV + audio "f5" => forced HLS (external, starts with f)', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  assert.strictEqual(qdl.streamUrl('H', 0, 'f5'), API + '/qdl/hls/H_0_f5/playlist.m3u8');
});

test('streamUrl: browser + embedded audio "e2" => HLS with audio appended', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => false } }) });
  // 'e2' not f/d so ext=false, but isBrowser=true => HLS; audio!=='o' => appended.
  assert.strictEqual(qdl.streamUrl('H', 1, 'e2'), API + '/qdl/hls/H_1_e2/playlist.m3u8');
});

test('streamUrl: TV + embedded audio "e2" => direct stream (not external, not browser)', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  // ext=false, isBrowser=false => stream branch; audio ignored in stream URL.
  assert.strictEqual(qdl.streamUrl('H', 1, 'e2'), API + '/qdl/stream?hash=H&index=1');
});

test('streamUrl: audio falsy (undefined) on TV => direct stream, no audio suffix', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => true } }) });
  assert.strictEqual(qdl.streamUrl('H', 0), API + '/qdl/stream?hash=H&index=0');
});

// ─────────────────────────── posterUrl ───────────────────────────

test('posterUrl: has_poster => qdl/poster URL with hash', () => {
  const { qdl } = H.loadQdl({});
  assert.strictEqual(
    qdl.posterUrl({ has_poster: true, hash: 'ABC' }),
    API + '/qdl/poster?hash=ABC'
  );
});

test('posterUrl: no has_poster => broken img placeholder', () => {
  const { qdl } = H.loadQdl({});
  assert.strictEqual(qdl.posterUrl({ has_poster: false, hash: 'ABC' }), './img/img_broken.svg');
  assert.strictEqual(qdl.posterUrl({ hash: 'ABC' }), './img/img_broken.svg');
  assert.strictEqual(qdl.posterUrl(null), './img/img_broken.svg');
  assert.strictEqual(qdl.posterUrl(undefined), './img/img_broken.svg');
});

// ─────────────────────────── relTime ───────────────────────────

function pad(n) { return (n < 10 ? '0' : '') + n; }

test('relTime: same calendar day => "сегодня HH:MM"', () => {
  const { qdl } = H.loadQdl({});
  const d = new Date();
  d.setHours(9, 5, 0, 0); // ensures HH:MM padding is exercised
  const expected = 'сегодня ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
  assert.strictEqual(qdl.relTime(d.toISOString()), expected);
});

test('relTime: yesterday => "вчера HH:MM"', () => {
  const { qdl } = H.loadQdl({});
  const d = new Date();
  d.setDate(d.getDate() - 1);
  d.setHours(14, 30, 0, 0);
  const expected = 'вчера ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
  assert.strictEqual(qdl.relTime(d.toISOString()), expected);
});

test('relTime: 3 days ago (0<days<7) => "N дн назад"', () => {
  const { qdl } = H.loadQdl({});
  // Use noon 3 days ago to stay safely inside the day and away from DST edges.
  const d = new Date();
  d.setDate(d.getDate() - 3);
  d.setHours(12, 0, 0, 0);
  const now = new Date();
  const days = Math.floor((now - d) / 86400000);
  // days should be 3 (2 or 3 possible only near midnight; noon anchor keeps it 3).
  const expected = days + ' дн назад';
  assert.strictEqual(qdl.relTime(d.toISOString()), expected);
});

test('relTime: ~10 days ago => "dd.mm.yyyy" padded date', () => {
  const { qdl } = H.loadQdl({});
  const d = new Date();
  d.setDate(d.getDate() - 10);
  d.setHours(12, 0, 0, 0);
  const expected = pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + '.' + d.getFullYear();
  assert.strictEqual(qdl.relTime(d.toISOString()), expected);
});

test('relTime: fixed old date has both day and month zero-padded', () => {
  const { qdl } = H.loadQdl({});
  // 2020-01-05 is far in the past => absolute-date branch, exercises padding on day & month.
  const out = qdl.relTime('2020-01-05T00:00:00');
  assert.strictEqual(out, '05.01.2020');
});

test('relTime: invalid iso => "" (NaN date throws/parses to empty)', () => {
  const { qdl } = H.loadQdl({});
  // Invalid Date: toDateString() = 'Invalid Date', getDate() = NaN, days = NaN.
  // No branch matches (days>=0&&<7 false), falls to absolute: pad(NaN)... => 'NaN.NaN.NaN'.
  // BUG?: invalid input yields 'NaN.NaN.NaN' rather than '' (the catch is never reached).
  const out = qdl.relTime('not-a-date');
  assert.strictEqual(out, 'NaN.NaN.NaN');
});

// ─────────────────────────── getAudioPref / setAudioPref ───────────────────────────

test('getAudioPref: unset hash => undefined', () => {
  const { qdl } = H.loadQdl({});
  assert.strictEqual(qdl.getAudioPref('nope'), undefined);
});

test('setAudioPref/getAudioPref: round-trip per hash under qdl_audio2', () => {
  const lampa = H.makeLampa();
  const { qdl } = H.loadQdl({ lampa });

  qdl.setAudioPref('serA', 'd10');
  qdl.setAudioPref('serB', 'e3');

  assert.strictEqual(qdl.getAudioPref('serA'), 'd10');
  assert.strictEqual(qdl.getAudioPref('serB'), 'e3');
  assert.strictEqual(qdl.getAudioPref('serC'), undefined);

  // Stored as a single map object under 'qdl_audio2'.
  // NB the object crosses the VM realm boundary, so compare fields (not deepStrictEqual,
  // which would fail cross-realm prototype identity).
  const stored = lampa.Storage.get('qdl_audio2', null);
  assert.ok(stored && typeof stored === 'object');
  assert.strictEqual(stored.serA, 'd10');
  assert.strictEqual(stored.serB, 'e3');
  assert.deepStrictEqual(Object.keys(stored).sort(), ['serA', 'serB']);

  // Overwrite existing hash keeps the other entry.
  qdl.setAudioPref('serA', 'o');
  assert.strictEqual(qdl.getAudioPref('serA'), 'o');
  assert.strictEqual(qdl.getAudioPref('serB'), 'e3');
});

test('setAudioPref persists via Lampa.Storage.set (logged in _calls)', () => {
  const lampa = H.makeLampa();
  const { qdl } = H.loadQdl({ lampa });
  qdl.setAudioPref('h1', 'd7');
  const setCalls = lampa.Storage._calls.filter((c) => c[0] === 'qdl_audio2');
  assert.strictEqual(setCalls.length, 1);
  // cross-realm object: compare fields rather than deepStrictEqual.
  assert.strictEqual(setCalls[0][1].h1, 'd7');
  assert.deepStrictEqual(Object.keys(setCalls[0][1]), ['h1']);
});

// ─────────────────────────── badge / chip ───────────────────────────

test('badge: contains esc\'d value and label', () => {
  const { qdl } = H.loadQdl({});
  const html = qdl.badge('1080p', 'Quality');
  assert.match(html, /<b>1080p<\/b>/);
  assert.match(html, />Quality<\/span>/);
  // structural bits
  assert.match(html, /^<span /);
  assert.match(html, /inline-flex/);
});

test('badge: escapes HTML-special chars in val and label', () => {
  const { qdl } = H.loadQdl({});
  const html = qdl.badge('<b>&"', 'a<b>');
  // val escaped
  assert.ok(html.includes('<b>&lt;b&gt;&amp;&quot;</b>'));
  // label escaped
  assert.ok(html.includes('a&lt;b&gt;'));
  // raw unescaped injection must not appear
  assert.ok(!html.includes('<b>&"</b>'));
});

test('chip: wraps esc\'d text', () => {
  const { qdl } = H.loadQdl({});
  const html = qdl.chip('HEVC');
  assert.match(html, /^<span /);
  assert.ok(html.includes('>HEVC</span>'));
  assert.match(html, /inline-block/);
});

test('chip: escapes HTML-special chars', () => {
  const { qdl } = H.loadQdl({});
  const html = qdl.chip('<script>&"');
  assert.ok(html.includes('&lt;script&gt;&amp;&quot;'));
  assert.ok(!html.includes('<script>'));
});

test('chip: null/undefined text => empty (esc coerces nullish to "")', () => {
  const { qdl } = H.loadQdl({});
  assert.ok(qdl.chip(null).includes('></span>'));
  assert.ok(qdl.chip(undefined).includes('></span>'));
});

// ─────────────────────────── mobileHls / мобильный профиль _m ───────────────────────────
// «iOS на сотовой» → live-720p HLS (ключ _m). Флаг сети ставит нативная оболочка
// (window.d1vision_network); все прочие платформы флага не имеют → поведение прежнее.

const tvLampa = () => H.makeLampa({ Platform: { tv: () => true } });

test('mobileHls: ios + cellular => true (авто-режим)', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'cellular' },
  });
  assert.strictEqual(qdl.mobileHls(), true);
});

test('mobileHls: ios + wifi => false', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'wifi' },
  });
  assert.strictEqual(qdl.mobileHls(), false);
});

test('mobileHls: другие платформы (mac/android/web) => false даже при cellular-флаге', () => {
  for (const p of ['mac', 'android', 'tizen', 'web', undefined]) {
    const { qdl } = H.loadQdl({
      lampa: tvLampa(),
      windowExtra: { d1vision_platform: p, d1vision_network: 'cellular' },
    });
    assert.strictEqual(qdl.mobileHls(), false, 'platform=' + p);
  }
});

test('mobileHls: без флага сети (старый бинарь оболочки) => false', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios' },
  });
  assert.strictEqual(qdl.mobileHls(), false);
});

test('mobileHls: qdl_mobile_quality=off глушит даже ios+cellular', () => {
  const lampa = tvLampa();
  lampa.Storage.set('qdl_mobile_quality', 'off');
  const { qdl } = H.loadQdl({
    lampa,
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'cellular' },
  });
  assert.strictEqual(qdl.mobileHls(), false);
});

test('mobileHls: qdl_mobile_quality=always форсит на любой платформе', () => {
  const lampa = tvLampa();
  lampa.Storage.set('qdl_mobile_quality', 'always');
  const { qdl } = H.loadQdl({ lampa, windowExtra: {} });
  assert.strictEqual(qdl.mobileHls(), true);
});

test('streamUrl: ios+cellular на ТВ-платформе => HLS с суффиксом _m (пересиливает оригинал)', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'cellular' },
  });
  assert.strictEqual(qdl.streamUrl('HASH', 0, 'o'), API + '/qdl/hls/HASH_0_m/playlist.m3u8');
  assert.strictEqual(qdl.streamUrl('H', -1), API + '/qdl/hls/H_-1_m/playlist.m3u8');
});

test('streamUrl: mobile + встроенная озвучка e2 => суффикс _m ПОСЛЕ audio', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'cellular' },
  });
  assert.strictEqual(qdl.streamUrl('H', 1, 'e2'), API + '/qdl/hls/H_1_e2_m/playlist.m3u8');
});

test('streamUrl: mobile + внешняя озвучка d-id => _m после d-суффикса', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'cellular' },
  });
  assert.strictEqual(qdl.streamUrl('H', 2, 'd123'), API + '/qdl/hls/H_2_d123_m/playlist.m3u8');
});

test('streamUrl: ios на wifi => прежний оригинал /qdl/stream (регрессия)', () => {
  const { qdl } = H.loadQdl({
    lampa: tvLampa(),
    windowExtra: { d1vision_platform: 'ios', d1vision_network: 'wifi' },
  });
  assert.strictEqual(qdl.streamUrl('HASH', 0, 'o'), API + '/qdl/stream?hash=HASH&index=0');
});

test('streamUrl: браузер без mobile-флагов => HLS БЕЗ _m (регрессия)', () => {
  const { qdl } = H.loadQdl({ lampa: H.makeLampa({ Platform: { tv: () => false } }) });
  assert.strictEqual(qdl.streamUrl('HASH', 0, 'o'), API + '/qdl/hls/HASH_0/playlist.m3u8');
});
