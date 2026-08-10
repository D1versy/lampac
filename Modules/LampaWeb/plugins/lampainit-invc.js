// //////////////
// Переименуйте файл lampainit-invc.js в lampainit-invc.my.js
// //////////////


var lampainit_invc = {};

// Версия НАШЕГО форка (видна в консоли клиента: window.qdl_fork_version).
// ⚠️ Правило владельца: при КАЖДОМ фиксе клиентских плагинов (qdl.js / lampainit-invc.js)
// бампать минор. Кэш-бастер URL (?v=) обновляется сам при рестарте контейнера
// (cacheVersion в ApiController: lampainit.js в Index(), qdl.js в LamInit) —
// эта версия нужна как человекочитаемый маркер «какой код реально крутится у клиента».
window.qdl_fork_version = '2.27';   // полный changelog — E:\lampac\CHANGELOG-qdl.md (вынесен из этого файла в 2.16: комментарий инлайнился в /lampainit.js и отдавался каждому клиенту, ~6.5 КБ на старт)


// Лампа готова для использования
lampainit_invc.appload = function appload() {
  // qdl 2.16: appload зовут ДВОЕ — наш быстрый вотчер (25 мс, внизу файла) и upstream-таймер
  // lampainit.js (200 мс); гард делает вызов идемпотентным (без него — дубли putScriptAsync)
  if (lampainit_invc._apploaded) return;
  lampainit_invc._apploaded = true;
  Lampa.Utils.putScriptAsync(["{localhost}/qdl.js?v={version}"]);  // QbitDownload: кнопка «Скачать» + раздел «Загрузки» ({version} = cache-buster, подставляет сервер)
  Lampa.Utils.putScriptAsync(["{localhost}/music.js?v={version}"]);  // Music (upstream 1.45, manifest enable:true — наш флип): раздел «Музыка»; откат = убрать строку + рестарт
  // TorrServer — через прокси lampac (/ts/* → контейнер torrserver, модуль TorrServer external_url).
  // location.origin: в LAN → http://192.168.87.24:9118/ts, извне → https://tv.d1versy.com:9443/ts —
  // работает отовсюду и не светит LAN-IP (раньше тут был хардкод http://192.168.87.24:8090).
  Lampa.Storage.set('torrserver_url', location.origin + '/ts');
  Lampa.Storage.set('torrserver_auth', 'false');

  // ── D1Vision: бренд вкладки/окна ──
  // Актуальный бренд приезжает из /d1vision/hosts.json (init.conf → QbitDownload.brand):
  // смена названия проекта = правка init.conf, без пересборки чего-либо. Сам список hosts
  // из того же ответа потребляют НАТИВНЫЕ оболочки (кэшируют у себя) — вебу он не нужен.
  var applyBrandTitle = function () {
    try {
      if (window.d1vision_brand && document.title !== window.d1vision_brand)
        document.title = window.d1vision_brand;
    } catch (e) {}
  };
  applyBrandTitle();
  try {
    fetch('{localhost}/d1vision/hosts.json').then(function (r) { return r.json(); }).then(function (j) {
      if (j && j.brand) window.d1vision_brand = j.brand;
      applyBrandTitle();
    }).catch(function () {});
  } catch (e) {}

  // (разблокировка premium авторитетно ставится в appready — после Profile.check, который стирает account_user)
  // нейтральные строки вместо CUB/Premium (best-effort; основной эффект даёт скрытие CSS)
  try {
    Lampa.Lang.add({
      change_source_on_cub: { ru: 'Сменить источник', en: 'Change source', uk: 'Змінити джерело' },
      extensions_from_cub: { ru: 'Расширения', en: 'Extensions', uk: 'Розширення' },
      plugins_load_from: { ru: 'Загрузка плагинов', en: 'Loading plugins', uk: 'Завантаження плагінів' }
    });
  } catch (e) {}

  // убрать упоминание стороннего сервиса (Телеграм), кнопку профиля/аккаунта и раздел «Синхронизация» в настройках
  try {
    if (!document.getElementById('qdl-hide-extras')) {
      var st = document.createElement('style');
      st.id = 'qdl-hide-extras';
      st.textContent = '.feed-head__info{display:none!important}'
        + '.head__action.open--profile{display:none!important}'
        + '[data-component="account"]{display:none!important}'
        + '.menu__item[data-action="about"]{display:none!important}'
        + '.account-modal-split__text,.account-modal__site{display:none!important}'   // промо CUB Premium
        + '.selectbox-item:has(.selectbox-item__lock){display:none!important}'        // пункты с замком (логин/премиум)
        + '.extensions__cub,.extensions__item-premium,.cub-premium,.ad-video-block{display:none!important}'  // бренд/реклама CUB
        + '.button--subscribe,.full-start-new__reactions,.full-start__reactions{display:none!important}'      // серверные фичи CUB (подписки/реакции)
        // фолбэк к AppPatch (QbitDownload/AppReplace.cs): при смене tree якоря могут не сработать —
        // тогда CSS прячет уцелевший upstream-колокольчик и пункты меню Релизы/Расписание/Подписки
        + '.head__action.notice--icon{display:none!important}'
        + '.menu__item[data-action="relise"],.menu__item[data-action="timetable"],.menu__item[data-action="subscribes"]{display:none!important}';
      document.head.appendChild(st);
    }

    // фолбэк для браузеров без :has (напр. старый Tizen-ТВ) — прячем пункты с замком вручную
    var hideLocked = function () {
      try {
        var locks = document.querySelectorAll('.selectbox-item__lock');
        for (var i = 0; i < locks.length; i++) {
          var it = locks[i].closest ? locks[i].closest('.selectbox-item') : null;
          if (it) it.style.display = 'none';
        }
      } catch (e) {}
    };

    // После qdl-cut (AppPatch) это ФОЛБЭК: штатная модалка «Уведомления» недостижима (иконка вырезана),
    // но при смене tree якорь может не сработать — тогда функция снова прячет вкладку CUB.
    // Спрятать вкладку «CUB»: и в навигации, и в табах модалок (напр. модалка «Уведомления»,
    // .modal__title). CSS не умеет выбирать по тексту, а класс таба зависит от сборки Lampa —
    // поэтому среди таб-подобных элементов ищем тот, чей текст РОВНО 'CUB', и прячем его.
    // Селекторы бьют по САМИМ табам (нав-табы + таб-элементы внутри .modal), НЕ по контейнеру
    // всех табов, поэтому даже когда CUB — единственный таб, соседние табы не страдают.
    // ВАЖНО: вызывается в НЕмедленной ветке observer'а (как stripBrand). Колбэк MutationObserver
    // отрабатывает микротаском ДО отрисовки, поэтому таб гаснет без мерцания — в отличие от
    // прежнего варианта в debounced setTimeout(300ms), где CUB «проскакивал» на кадр.
    var hideCubTabs = function () {
      try {
        // observer шлёт МНОГО мутаций при рендере списков/карточек (особенно на слабых ТВ) — сначала
        // дёшево отсекаем случай «нет ни модалки, ни нав-табов», чтобы не гонять полный скан впустую
        if (!document.querySelector('.modal, .navigation-tabs')) return;
        var sel = '.navigation-tabs__button, .navigation__tab, .tabs__item, .simple-tabs__item,'
                + ' .modal .selector, .modal .simple-button, .modal .filter__item, .modal .tab, .modal .button';
        var els = document.querySelectorAll(sel);
        for (var i = 0; i < els.length; i++)
          if ((els[i].textContent || '').trim() === 'CUB' && els[i].style) els[i].style.display = 'none';
      } catch (e) {}
    };

    // убрать суффикс источника-бренда « - CUB» из заголовка шапки (.head__title').text(name+' - '+source.toUpperCase()))
    // source='cub' → 'CUB'; срезаем ТОЛЬКО " - CUB" в конце, чтобы не задеть реальные названия с « - » (фильмы)
    var stripBrand = function () {
      try {
        var titles = document.querySelectorAll('.head__title');
        for (var i = 0; i < titles.length; i++) {
          var t = titles[i].textContent || '';
          var fixed = t.replace(/\s*[-–—]\s*CUB\s*$/i, '');
          if (fixed !== t) titles[i].textContent = fixed;
        }
      } catch (e) {}
    };
    hideLocked();
    stripBrand();
    hideCubTabs();
    try {
      var deb = null;
      new MutationObserver(function () {
        stripBrand();                 // дёшево и сразу — реагирует на ту же мутацию, что ставит заголовок (без мерцания)
        hideCubTabs();                // тоже сразу — CUB-таб (модалка «Уведомления»/нав-табы) гаснет до кадра
        if (deb) return;
        deb = setTimeout(function () { deb = null; hideLocked(); }, 300);
      }).observe(document.body, { childList: true, subtree: true });
    } catch (e) {}
  } catch (e) {}
  // Lampa.Utils.putScriptAsync(["{localhost}/myplugin.js"]);  // wwwroot/myplugin.js
  // Lampa.Utils.putScriptAsync(["{localhost}/plugins/ts-preload.js", "https://nb557.github.io/plugins/online_mod.js"]);
  // Lampa.Storage.set('proxy_tmdb', 'true');
  // etc
};


// Лампа полностью загружена, можно работать с интерфейсом
lampainit_invc.appready = function appready() {
  // ── Авторитетная локальная разблокировка premium ──
  // appready вызывается ПОСЛЕ события app 'ready', т.е. после Account.init → Profile.check,
  // который при отсутствии токена стирает account_user в '' (permit.access=false).
  // Ставим через Lampa.Storage.set: оно пишет и в localStorage, и в in-memory кэш `readed`,
  // а именно из `readed` читает Account.hasPremium() (get → readed[name] первым).
  // hasPremium() = user.id ? countDays(Date.now(), user.premium) : 0 → вернёт ~3650 (>0).
  // hasPremium неперезаписываем (writable:false) — поэтому правим именно данные, не функцию.
  // Наружу ничего не уходит: серверные ветки гейтятся permit.access = token && account_use, токена нет.
  var spoofUser = function () { return { id: 1, premium: Date.now() + 3650 * 86400000 }; };
  var reinstall = function () {
    try { Lampa.Storage.set('account_user', spoofUser()); } catch (e) {}
  };
  reinstall();
  // Страж: если account_user когда-нибудь обнулят событием (logoff и т.п.) — восстановить.
  // Профиль-вайп на старте идёт с nolisten=true (события нет) и сюда не попадает, но мы уже переустановили выше.
  // Рекурсии нет: reinstall шлёт change со значением-объектом (есть .id) → ветка empty не срабатывает.
  try {
    Lampa.Storage.listener.follow('change', function (e) {
      if (!e || e.name !== 'account_user') return;
      var v = e.value;
      var empty = v === '' || v == null || (typeof v === 'object' && !v.id);
      if (empty) setTimeout(reinstall, 0);
    });
  } catch (e) {}
};


// Выполняется один раз, когда пользователь впервые открывает лампу
lampainit_invc.first_initiale = function firstinitiale() {
  // Здесь можно указать/изменить первоначальные настройки 
  // Lampa.Storage.set('source', 'tmdb');
};


// ── ДО загрузки Lampa: платформенные сущности D1Vision ──
// Нативные оболочки добавляют в UA токен "d1vision_<platform>/<версия>" (mac|ios|android|tizen|windows);
// Tizen-виджет вместо UA сеет localStorage['d1vision_platform']='tizen' в loader.js.
// По токену сервер выставляет платформенные ключи, которые раньше форсил КАЖДЫЙ клиент у себя
// (BridgeConfig в LampaKit, и т.п.) — теперь единая точка правды здесь: поведение платформ
// меняется по воздуху, без пересборки бинарей. Канон: E:\Media-server\claude\08-clients.md.
// «4 замка» маршрутизации плеера (claude/07-mac-app.md): оболочки с мостом window.AndroidJS
// живут на андроидной ветке запуска плеера, поэтому mac/ios/android/windows получают одинаковые
// андроидные ключи; ЧЕСТНАЯ идентичность платформы — d1vision_platform (window + localStorage).
// Старые бинари без токена, но с lampa_client в UA → 'android': их собственный форс-скрипт
// пишет ровно те же значения, операция идемпотентна — обратная совместимость при миграции.
window.d1vision_brand = window.d1vision_brand || 'D1Vision';   // дефолт; актуальное значение приезжает из /d1vision/hosts.json (init.conf → QbitDownload.brand)
try {
  var d1ua = navigator.userAgent || '';
  var d1tok = /d1vision_(mac|ios|android|tizen|windows)\/(\S+)/i.exec(d1ua);
  // Из localStorage доверяем ТОЛЬКО 'tizen' (его легитимно сеет loader.js виджета — у Tizen
  // нет UA-токена). mac/ios/android определяем ИСКЛЮЧИТЕЛЬНО по UA-токену: иначе залипшее в
  // одном браузере значение (импорт настроек, общий профиль) навсегда форсило бы андроидные
  // ключи в обычном вебе. Чужое залипшее значение самоисцеляется перезаписью на строке ниже.
  var d1saved = localStorage.getItem('d1vision_platform');
  var d1plat = d1tok ? d1tok[1].toLowerCase()
             : (d1saved === 'tizen' ? 'tizen'
             : (/lampa_client/i.test(d1ua) ? 'android' : 'web'));
  window.d1vision_platform = d1plat;
  window.d1vision_client_version = d1tok ? d1tok[2] : '';
  localStorage.setItem('d1vision_platform', d1plat);

  // Оболочки с нативным мостом AndroidJS → андроидный маршрут плеера (замки 2 и 3).
  // tizen и web НЕ трогаем: там платформу определяет сама Lampa, нативного моста нет.
  if (d1plat === 'mac' || d1plat === 'ios' || d1plat === 'android' || d1plat === 'windows') {
    localStorage.setItem('platform', 'android');
    localStorage.setItem('player', 'android');
    localStorage.setItem('player_torrent', 'android');
    localStorage.setItem('player_iptv', 'android');
    localStorage.setItem('internal_torrclient', 'true');
  }
} catch (e) {}

// ── ДО загрузки Lampa: стабильный uid устройства (серверный синк per-device, qdl 2.18) ──
// Серверные плагины синка (sync.js → bookmark.js + timecode.js, Modules/Sync) идентифицируют
// устройство query-параметром uid = localStorage['lampac_unic_id']. Но localStorage per-origin:
// LAN (192.168.87.24:9118) и tv.d1versy.com:9443 дали бы РАЗНЫЕ uid = «два устройства» на
// сервере, и прогресс снова расщепился бы при переключении хоста — ровно та грабля, ради
// которой существовало зеркало qdl_file_view (claude/06 §AM в Media-server).
// AndroidJS — per-app KV нативных оболочек (mac/ios/android), ОБЩИЙ для всех origin: держим
// канонический uid там (ключ qdl_device_uid) и пересеваем в localStorage каждого origin.
// Браузер/Tizen без AndroidJS живут на одном хосте — их per-origin uid и так стабилен.
// ⚠️ Android: AndroidJS.set() бросает после одноразового dump() (поток «Мигрировать») — глотаем;
// на следующем старте запись повторится. Выполняется ДО Lampa: sync-плагины читают uid на init.
try {
  var d1kv = window.AndroidJS;
  if (d1kv && typeof d1kv.get === 'function' && typeof d1kv.set === 'function') {
    var d1dev = '';
    try { d1dev = d1kv.get('qdl_device_uid') || ''; } catch (e) {}
    var d1ls = '';
    try { d1ls = localStorage.getItem('lampac_unic_id') || ''; } catch (e) {}
    if (!d1dev) {
      // усыновление: если у origin уже был uid — делаем его каноническим, иначе генерим.
      // Первый символ — буква: Lampa.Storage.get() парсит значения как JSON, чисто цифровая
      // строка превратилась бы в number.
      d1dev = d1ls;
      if (!d1dev) {
        d1dev = 'd';
        for (var d1i = 0; d1i < 7; d1i++) d1dev += 'abcdefghijklmnopqrstuvwxyz0123456789'.charAt(Math.floor(Math.random() * 36));
      }
      try { d1kv.set('qdl_device_uid', d1dev); } catch (e) {}
    }
    if (d1ls !== d1dev) localStorage.setItem('lampac_unic_id', d1dev);
  }
} catch (e) {}

// ── ДО загрузки Lampa: ранний seed premium ──
// hasPremium() = countDays(Date.now(), account_user.premium) > 0. premium = метка в будущем (мс).
// ВАЖНО: на старте Account.init → Profile.check СТИРАЕТ account_user в '' (нет токена → permit.access=false),
// поэтому этот seed — лишь подстраховка для самых ранних чтений; авторитетная установка — в appready (ниже).
// НЕ трогаем токен 'account' → permit.access/sync=false → наружу на cub.rip ничего не уходит.
try {
  localStorage.setItem('account_user', JSON.stringify({ id: 1, premium: Date.now() + 3650 * 86400000 }));
  localStorage.removeItem('developer_nopremium'); // дефолт 'false'; любая иная строка → truthy → hasPremium()=0
} catch (e) {}

// ── ДО загрузки Lampa: выключение upstream-фич ──
// Штатный флаг Lampa: не создавать пункт меню «Подписки» и кнопку подписки на карточке.
// Пояс и подтяжки к AppPatch (menu-items) — работает даже при смене tree, когда якорь не найдётся.
// Дефолт subscribe=false ставит lampainit.js ВЫШЕ по файлу (наш код инлайнится после него, до app.min.js).
try { window.lampa_settings.disable_features.subscribe = true; } catch (e) {}

// ── ДО загрузки Lampa: локализация внешних источников (qdl 2.15, см. claude/11 Media-server) ──
// Клиент должен ходить ТОЛЬКО на наш сервер (осознанные исключения: YouTube-трейлеры, стрим балансеров).
// mirrors=false — гейт пробы зеркал CUB: 6 raw-$.ajax на cub.rip/durex.monster/cubnotrip.top//api/checker
//   при каждом старте (raw jQuery мимо request_before — cubproxy.js их не видит); гейт в бандле:
//   `if (!window.lampa_settings.mirrors) return call && call();` — колбэк зовётся, поток старта цел.
// geo=false — гейт raw-$.ajax на geo.<cub>; без пробы страна = 'RU', что нам и нужно. In-bundle
//   TMDBProxy.init() при этом не стартует — неважно: наш tmdbproxy.js перекрывает TMDB.api/image сам.
// socket_use=false — ЕДИНСТВЕННАЯ постоянно активная утечка: WebSocket на cub.rip (qdl 2.19).
//   Гейт в бандле: `if (!window.lampa_settings.socket_use) return;` в Socket.connect(), а сам
//   Socket.init() зовётся в стартовой цепочке БЕЗУСЛОВНО. Подмена cub_domain тут не помогает:
//   адреса берутся из ОТДЕЛЬНОГО литерального массива soc_mirrors ['cub.rip','kurwa-bober.ninja',
//   'nackhui.com'] (Object.defineProperty, сеттер-пустышка — перезаписать нельзя). В сообщениях
//   наружу уходят device_id, имя устройства, account и terminal; при недоступности — ретраи ~10 с.
// socket_methods=false — второй замок: даже если сокет кто-то поднимет (socket_url извне),
//   входящие команды (open/devices/terminal — чужой Activity.push!) обрабатываться не будут.
// services=false — гигиена, не утечка: /plugin/sport, /plugin/tsarea, /plugin/shots приезжают
//   через НАШ релей /cub/red/plugin/*, но это сторонний НЕстатический JS, исполняемый в нашем
//   origin, где в cookie лежит ключ периметра D1Vision.
// ВАЖНО: работает только потому, что бандл наливает дефолты через Arrays.extend(lampa_settings, …)
//   БЕЗ флага replase — extend пишет лишь в ключи со значением undefined, наши false переживают.
try {
  window.lampa_settings.mirrors = false;
  window.lampa_settings.geo = false;
  window.lampa_settings.socket_use = false;
  window.lampa_settings.socket_methods = false;
  window.lampa_settings.services = false;
} catch (e) {}
// ── ДО загрузки Lampa: выключение скринсейвера (qdl 2.19) ──
// Скринсейвер включён ПО УМОЛЧАНИЮ (Params.trigger('screensaver', true), задержка 5 мин), и любой
// его тип ходит наружу: 'aerial' (дефолт!) — raw.githubusercontent.com/OrangeJedi/Aerial/master/
// videos.json + видео с чужого CDN, причём со схемой http:; 'nature' — picsum.photos каждые 30 с;
// 'cub' — видео с cub_domain, ДА ЕЩЁ и с ?token= аккаунта в URL; 'chrome' (и любой НЕизвестный тип —
// класс Chrome стоит фолбэком) — iframe на clients3.google.com.
// Гейт в бандле: `if (!Storage.field('screensaver') || …) return;` в Screensaver.resetTimer.
// Форсим ровно как proxy_tmdb ниже — строкой в localStorage: Lampa ещё не загружена, а Lampa.Storage
// читает те же ключи, и его get() отдельно разбирает 'true'/'false' в булево (JSON-кавычки не нужны).
// Каждую загрузку, а не в first_initiale: иначе тумблер, включённый руками, пережил бы перезапуск.
// screensaver_type — пояс и подтяжки на случай, если тумблер включат руками (он живёт до
// перезагрузки страницы): единственный тип, который НЕ ходит наружу с клиента, — 'cub'. В
// отдаваемом бандле класс Cub собран уже с нашим адресом: default = 'http://192.168.87.24:9118/
// cub/red' + '/img/background/default.mp4' (подмена cub_domain на этапе отдачи). Пустой
// Storage['cub_screensaver'] даёт относительный 'token=…', который резолвится в НАШ origin → 404,
// то есть наружу всё равно ничего не уходит. 'nature' (picsum.photos каждые 30 с) и 'chrome'
// (iframe clients3.google.com, он же фолбэк для неизвестного типа) — прямые обращения с клиента.
try {
  if (localStorage.getItem('screensaver') !== 'false') localStorage.setItem('screensaver', 'false');
  if (localStorage.getItem('screensaver_type') !== 'cub') localStorage.setItem('screensaver_type', 'cub');
} catch (e) {}
// proxy_tmdb=true КАЖДУЮ загрузку (не в first_initiale): upstream lampainit.js ставит его только при
// первом запуске (гард lampac_initiale) и по GeoIP — LAN-клиент получал null→false и ходил за
// постерами/TMDB-API на image.tmdb.org/api.themoviedb.org напрямую, мимо сервера. Форс до старта
// Lampa уводит на {localhost}/tmdb/img (кеш 72ч, SSD) и /tmdb/api (кеш 24ч) через tmdbproxy.js.
// Строка 'true' — ровно то, что кладёт Lampa.Storage.set(…, true). Ручной тумблер «Прокси TMDB»
// в настройках теперь живёт до перезагрузки — это осознанно.
try { localStorage.setItem('proxy_tmdb', 'true'); } catch (e) {}

// ── ДО загрузки Lampa: XHR-перехват для DMCA-фолбека (пара к qdl.js, см. claude/06 Media-server) ──
// CUB на заблокированные правообладателем карточки отдаёт {"blocked":true} → Lampa рисует
// «Контент заблокирован» без единой кнопки. Детали карточек (tmdb.<cub>/3/movie|tv/<id>)
// заворачиваем на свой TMDB-прокси lampac. Патч обязан стоять ЗДЕСЬ (синхронно, до старта
// приложения): при deep-link (?card=...) запрос карточки уходит раньше, чем putScriptAsync
// успевает подгрузить qdl.js. В qdl.js — такой же патч как фолбек (для клиентов, подключающих
// только /qdl.js); от двойной обёртки защищает флаг window.qdl_xhr_patch.
try {
  if (!window.qdl_xhr_patch && window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
    window.qdl_xhr_patch = 1;
    // две формы: прямая https://tmdb.<cub>/3/... и через серверный CubProxy
    // (плагин cubproxy.js на request_before превращает её в <host>/cub/tmdb.<cub>/3/...)
    var qdlRewriteCub = function (u) {
      var m = /^https?:\/\/(?:[^\/]+\/cub\/)?tmdb\.[^\/]*\/(3\/(?:movie|tv)\/\d+(?:\/[^?]*)?)(\?.*)$/.exec(String(u));
      if (!m) return null;
      if (m[2].indexOf('api_key=') === -1) return null;   // прямому TMDB без ключа нельзя (401)
      return '{localhost}/tmdb/api/' + m[1] + m[2];
    };
    // пинг «просмотрено» (qdl 2.15): $.get('https://tmdb.<cub>/watch?id=…') — raw jQuery МИМО
    // Lampa.Reguest, request_before не стреляет, cubproxy.js его не видит; единственный ловец — XHR.
    // Заворачиваем на локальную заглушку /cub/api/checker (CubProxy отвечает 'ok' без похода наружу);
    // ответ фронт игнорирует. Форма с /cub/ — на случай, если URL уже кем-то завёрнут.
    var qdlRewriteWatch = function (u) {
      return /^https?:\/\/(?:[^\/]+\/cub\/)?tmdb\.[^\/]*\/watch\?/.test(String(u))
        ? '{localhost}/cub/api/checker' : null;
    };
    var qdlXhrOpen = window.XMLHttpRequest.prototype.open;
    window.XMLHttpRequest.prototype.open = function (method, url) {
      try {
        if (String(method).toUpperCase() === 'GET') {
          var dm = /^https?:\/\/tmdb\.([^\/]+)\//.exec(String(url));
          if (dm) window.qdl_cub_domain = dm[1];          // домен CUB — qdl.js возьмёт для /blocked
          var ru = qdlRewriteCub(url) || qdlRewriteWatch(url);
          if (ru) arguments[1] = ru;
        }
      } catch (e) {}
      return qdlXhrOpen.apply(this, arguments);
    };
    lampainit_invc.rewriteCubUrl = qdlRewriteCub;         // наружу — для тестов
    lampainit_invc.rewriteWatchUrl = qdlRewriteWatch;     // наружу — для тестов
  }
} catch (e) {}

// ── Быстрый вотчер появления Lampa (qdl 2.16) ──
// Upstream-таймер lampainit.js проверяет Lampa раз в 200 мс → старт qdl.js/music.js опаздывал
// в среднем на ~100 мс. Свой поллер 25 мс зовёт appload сразу; идемпотентность — гард
// _apploaded в самом appload (upstream-таймер позовёт повторно — no-op). Страховочный стоп
// через 30 с: если Lampa так и не появилась (сломанный бандл), не крутимся вечно.
try {
  // Гейт typeof: в проде Lampa на этом этапе НЕ существует (бандл грузится после invc) → вотчер
  // взводится; в тестах (vm/jsdom) мок Lampa уже стоит → вотчер не нужен (и его 30-с страховка
  // держала бы node-процесс тестов), appload зовут руками/upstream-таймером.
  if (typeof Lampa === 'undefined') {
    var qdlFastWatch = setInterval(function () {
      if (typeof Lampa === 'undefined') return;
      clearInterval(qdlFastWatch);
      try { lampainit_invc.appload(); } catch (e) {}
    }, 25);
    setTimeout(function () { clearInterval(qdlFastWatch); }, 30000);   // Lampa не появилась — не крутимся вечно
  }
} catch (e) {}

// Ниже код выполняется до загрузки лампы, например можно изменить настройки
// window.lampa_settings.push_state = false;
// localStorage.setItem('cub_domain', 'mirror-kurwa.men');
// localStorage.setItem('cub_mirrors', '["mirror-kurwa.men"]');


/* Контекстное меню в online.js
window.lampac_online_context_menu = {
  push: function(menu, extra, params) {
    menu.push({
      title: 'TEST',
      test: true
    });
  },
  onSelect: function onSelect(a, params) {
    if (a.test)
      console.log(a);
  }
};
*/
