'use strict';
// Сторож доставки ПРОПАТЧЕННОГО бандла клиентам (qdl 2.108, маркер d1v:appver-patch).
//
// Боевой случай: 2.108 вырезал штатное меню карточки патчем при отдаче (AppPatch). На телевизоре
// всё применилось, а на маке, айфоне, Windows и в браузере осталось старое меню — и осталось бы
// на год. Причина не в патче: `app.min.js?v=<версия>` отдаётся с `Cache-Control: immutable,
// max-age=31536000`, а версия считалась ТОЛЬКО из mtime вендоренного файла. Файл не менялся
// (патч кладётся поверх него при отдаче) → тот же URL → клиент вообще не спрашивает сервер.
// Чистка серверного Staticache тут бессильна: она чинит ответ, которого никто не запрашивает.
//
// 🔒 Здесь заперта вся цепочка доставки:
//   1. в ?v входит штамп самого патчера (AppReplace.cs) — правка патча меняет URL;
//   2. штамп берётся именно у AppReplace.cs через ModuleFile (кросс-модульно, без ссылки на сборку);
//   3. у /app.min.js в ключе серверного кеша есть "v" — иначе старый и новый ?v делят одну запись
//      и клиент навечно закеширует СТАРОЕ тело под НОВЫМ адресом;
//   4. index.html отдаётся no-cache — иначе новый ?v до клиента не доедет вовсе;
//   5. ?v остаётся стабильным между рестартами (в нём нет cacheVersion=тиков старта процесса) —
//      иначе вернётся «рестартный налог» qdl 2.16: 2 МБ каждому клиенту на каждый рестарт.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const H = require('./harness');

const SRC = fs.readFileSync(
  path.join(H.REPO, 'Modules', 'LampaWeb', 'Controllers', 'ApiController.cs'), 'utf8').replace(/\r\n/g, '\n');

/** Тело метода по его сигнатуре — до строки, где закрывающая скоба на том же отступе. */
function body(signature) {
  const i = SRC.indexOf(signature);
  assert.ok(i >= 0, `в ApiController.cs нет метода ${signature}`);
  const rest = SRC.slice(i);
  const end = rest.indexOf('\n    }\n');
  assert.ok(end > 0, `не нашёл конец метода ${signature}`);
  return rest.slice(0, end);
}

test('AppCacheVersion подмешивает штамп патчера бандла', () => {
  const fn = body('static string AppCacheVersion()');
  assert.ok(/PatchStamp\(\)/.test(fn),
    'без штампа патчера правка AppPatch не доедет ни до одного клиента с непустым кешем');
  assert.ok(/\{pt:x\}/.test(fn), 'штамп обязан попасть в САМУ строку версии, а не только в сравнение');
  assert.ok(/pt != _appVerPatch/.test(fn), 'смена штампа обязана пересчитывать версию');
});

test('штамп считается по AppReplace.cs через ModuleFile', () => {
  const fn = body('static long PatchStamp()');
  assert.ok(/ModuleFile\("QbitDownload", "AppReplace\.cs"\)/.test(fn),
    'штамп обязан смотреть на файл патчера: он и определяет отдаваемое тело');
  assert.ok(/LastWriteTimeUtc/.test(fn) && /fi\.Length/.test(fn), 'штамп = mtime + length');
  assert.ok(/catch \{ return 0; \}/.test(fn),
    'модуль не загружен → деградация к прежнему поведению, а не исключение на главной странице');
});

test('ключ серверного кеша /app.min.js включает ?v', () => {
  const i = SRC.indexOf('public ActionResult LampaApp(');
  assert.ok(i > 0, 'нет метода LampaApp');
  // ближайший атрибут Staticache ВЫШЕ метода (в самом атрибуте есть `new[]`, поэтому
  // «до первой закрывающей скобки» тут не работает — берём хвост исходника перед методом)
  const head = SRC.slice(0, i);
  const at = head.lastIndexOf('[Staticache(');
  assert.ok(at > 0, 'у LampaApp нет атрибута Staticache перед роутами');
  const attr = head.slice(at);
  assert.ok(/\[Route\("\/app\.min\.js"\)\]/.test(attr), 'между Staticache и методом должен быть роут /app.min.js');
  assert.ok(/queryKeys:\s*new\[\]\s*\{\s*"v"\s*\}/.test(attr),
    'без queryKeys "v" старый и новый ?v делят одну запись кеша — клиент навечно закеширует старое тело');
  assert.ok(/immutable:\s*true/.test(attr),
    'immutable — часть контракта: ровно поэтому версия обязана меняться вместе с телом');
});

test('index.html отдаётся no-cache и подставляет версию вместо 15-минутного тика апстрима', () => {
  const fn = body('public ActionResult Index(');
  assert.ok(/SetHeadersNoCache\(\)/.test(fn),
    'если закешировать сам index.html, новый ?v до клиента не доедет — вся цепочка держится на этом');
  assert.ok(/AppCacheVersion\(\)/.test(fn) && /Math\\\.floor/.test(fn),
    'версия подставляется в index.html на место апстримового Math.floor(Date/9e5)');
});

test('?v не зависит от тиков старта процесса (иначе вернётся рестартный налог 2.16)', () => {
  const fn = body('static string AppCacheVersion()');
  const assigns = fn.match(/_appVer = [^;]+;/g) || [];
  assert.ok(assigns.length > 0, 'версия где-то должна присваиваться');
  for (const a of assigns)
    assert.ok(!/cacheVersion/.test(a), 'cacheVersion = тики старта процесса: 2 МБ каждому клиенту на каждый рестарт');
  // в catch — можно: это аварийная ветка, лучше лишняя перекачка, чем 500 на главной
  assert.ok(/catch \{ return cacheVersion; \}/.test(fn), 'аварийная ветка обязана остаться');
});
