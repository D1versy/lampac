'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { loadQdl, loadLampaInit, makeLampa } = require('./harness');

test('qdl: harness loads and exports pure helpers', () => {
  const { qdl } = loadQdl();
  assert.strictEqual(typeof qdl.slimCard, 'function');
  assert.strictEqual(typeof qdl.cleanName, 'function');
});

test('qdl: esc escapes html specials', () => {
  const { qdl } = loadQdl();
  assert.strictEqual(qdl.esc('<a>&"'), '&lt;a&gt;&amp;&quot;');
});

test('qdl: slimCard detects tv via structural signal', () => {
  const { qdl } = loadQdl();
  const c = qdl.slimCard({ id: 5, name: 'Show', number_of_seasons: 2, first_air_date: '2020-01-02' });
  assert.strictEqual(c.media_type, 'tv');
  assert.strictEqual(c.year, '2020');
});

test('qdl: cleanName strips year/quality tail', () => {
  const { qdl } = loadQdl();
  assert.strictEqual(qdl.cleanName('The.Show.2019.1080p.WEB-DL'), 'The Show');
});

test('qdl: streamUrl picks HLS in browser, direct on TV', () => {
  const tv = loadQdl({ lampa: makeLampa({ Platform: { tv: () => true } }) });
  assert.match(tv.qdl.streamUrl('h', 0, 'o'), /\/qdl\/stream\?hash=h/);
  const br = loadQdl({ lampa: makeLampa({ Platform: { tv: () => false } }) });
  assert.match(br.qdl.streamUrl('h', 0, 'o'), /\/qdl\/hls\//);
});

test('lampainit: top-level seeds premium account_user', () => {
  const { localStorage } = loadLampaInit();
  const u = JSON.parse(localStorage.getItem('account_user'));
  assert.strictEqual(u.id, 1);
  assert.ok(u.premium > Date.now());
});

test('lampainit: appready reinstalls spoof user via Lampa.Storage', () => {
  const { mod, lampa } = loadLampaInit();
  mod.appready();
  const u = lampa.Storage.get('account_user');
  assert.ok(u && u.id === 1 && u.premium > Date.now());
});
