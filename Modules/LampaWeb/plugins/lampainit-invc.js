// //////////////
// Переименуйте файл lampainit-invc.js в lampainit-invc.my.js
// //////////////


var lampainit_invc = {};

// Версия НАШЕГО форка (видна в консоли клиента: window.qdl_fork_version).
// ⚠️ Правило владельца: при КАЖДОМ фиксе клиентских плагинов (qdl.js / lampainit-invc.js)
// бампать минор. Кэш-бастер URL (?v=) обновляется сам при рестарте контейнера
// (cacheVersion в ApiController: lampainit.js в Index(), qdl.js в LamInit) —
// эта версия нужна как человекочитаемый маркер «какой код реально крутится у клиента».
window.qdl_fork_version = '2.13';   // 2.0: умная выдача раздач (⭐ скоринг, серии/дата/🔔) + охота за сериями по всем раздачам (/qdl/episodes, серии-доноры «врем.», tl-таймлайн) + SWITCH-переключение; 2.1: фиксы ревью — аудио доноров по своему hash, SWITCH-тост без «скачана»; 2.2: пункт меню «D1VERSY LIVE» — записи видеорегистратора (день → камеры с записями → плеер, прокси /qdl/live/*); 2.3: весь день ОДНОЙ записью с одним таймлайном (склеенный HLS /qdl/live/day), фрагменты — запасной путь; 2.4: раздел «D1versy Live» — эфир (живая сетка камер, rolling-HLS /qdl/live/watch/*), прежний раздел записей переименован в «D1versy Records»; 2.5: «D1versy Records» → «D1versy Rec»; 2.6: iPhone — эфир в НАТИВНОМ плеере iOS (прямой click-жест, <video> без playsinline, фолбек в VLC, qdl_ios_live auto/off); сетка эфира по центру и во всю ширину; видимый фокус пульта в Rec/камере/Уведомлениях; 2.7: фикс iOS-эфира — видимый оверлей вместо скрытого <video> (авто-фуллскрин WebKit флакал со второго раза), явный webkitEnterFullscreen, попытки чистят только свой элемент (двойной тап больше не роняет в VLC); 2.8: Rec на пустом «сегодня» сам прыгает на последний день с записями (пустой день на ТВ выглядел как «нет навигации» — вниз некуда идти); 2.9: зеркало прогресса устройства (file_view ↔ нативное KV AndroidJS, ключ qdl_file_view) — resume переживает смену хоста LAN↔внешний на iPhone/Mac/Android, браузер не затронут; выкл. зеркала: qdl_tl_mirror='off', фолбэк «всегда с начала» — player_timecode='again'; rawPlay унифицирован на pickTimeline (tl-ключ при известном файле); инварианты слияния после адверсариального ревью: сид-метка t=1 (старьё не клоббрит свежее), кламп меток из будущего, уважение очистки истории, бюджет посева=кап (без re-seed-churn); 2.10: скорость — прогрев кеша сервера при открытии карточки скачанного (/qdl/warmup: голова+хвост файла в page cache + ffprobe-кеш; addContinueButton/openDownload) и прогрев следующей серии при старте плейбека (warmupNext); мемоизация fetchEpisodes (1 запрос /qdl/episodes вместо 3 на пути «Смотреть», TTL 45 с, инвалидация на del/SWITCH/транскод); сервер: кеш резолва hash+index→путь, кеш ffprobe-дорожек, переиспользование SID-сессии qBittorrent, immutable-кэширование app.min.js/qdl.js/постеров; 2.11: свой ЭКРАН СЕРИЙ (qdl_episodes) вместо селектбокса — длинные списки скроллятся на ТВ, «назад» из плеера возвращает в список серий (и «Продолжить» идёт через него, autoplay), озвучка помнится и меняется кнопкой без переспроса, отметки ✓/►N% + размер/«временная» у доноров обновляются на месте; общий фикс селектбоксов (upstream-баг: Select пишет mheight в jQuery data, Layer читает сырое свойство → низ длинных списков «за экраном»; fixSelectHeight по fullshow: свойство + Layer.update + px max-height >480) — чинит выбор раздачи (60 пунктов), озвучки и все селекты; qdl_card: scroll.minus() + фокус-скролл; 2.12: номер серии бейджем в начале строки экрана серий (epNumber: epkey сервера → парс имени → порядковый) — на iPhone длинные имена файлов обрезались и серии было не отличить; 2.13: синк с upstream 1.45 + включён модуль Music (флип manifest enable:true, клиентам подключён /music.js) + свежие yaml-источники NextHUB


// Лампа готова для использования 
lampainit_invc.appload = function appload() {
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
        + '.button--subscribe,.full-start-new__reactions,.full-start__reactions{display:none!important}';     // серверные фичи CUB (подписки/реакции)
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
// Нативные оболочки добавляют в UA токен "d1vision_<platform>/<версия>" (mac|ios|android|tizen);
// Tizen-виджет вместо UA сеет localStorage['d1vision_platform']='tizen' в loader.js.
// По токену сервер выставляет платформенные ключи, которые раньше форсил КАЖДЫЙ клиент у себя
// (BridgeConfig в LampaKit, и т.п.) — теперь единая точка правды здесь: поведение платформ
// меняется по воздуху, без пересборки бинарей. Канон: E:\Media-server\claude\08-clients.md.
// «4 замка» маршрутизации плеера (claude/07-mac-app.md): оболочки с мостом window.AndroidJS
// живут на андроидной ветке запуска плеера, поэтому mac/ios/android получают одинаковые
// андроидные ключи; ЧЕСТНАЯ идентичность платформы — d1vision_platform (window + localStorage).
// Старые бинари без токена, но с lampa_client в UA → 'android': их собственный форс-скрипт
// пишет ровно те же значения, операция идемпотентна — обратная совместимость при миграции.
window.d1vision_brand = window.d1vision_brand || 'D1Vision';   // дефолт; актуальное значение приезжает из /d1vision/hosts.json (init.conf → QbitDownload.brand)
try {
  var d1ua = navigator.userAgent || '';
  var d1tok = /d1vision_(mac|ios|android|tizen)\/(\S+)/i.exec(d1ua);
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
  if (d1plat === 'mac' || d1plat === 'ios' || d1plat === 'android') {
    localStorage.setItem('platform', 'android');
    localStorage.setItem('player', 'android');
    localStorage.setItem('player_torrent', 'android');
    localStorage.setItem('player_iptv', 'android');
    localStorage.setItem('internal_torrclient', 'true');
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
    var qdlXhrOpen = window.XMLHttpRequest.prototype.open;
    window.XMLHttpRequest.prototype.open = function (method, url) {
      try {
        if (String(method).toUpperCase() === 'GET') {
          var dm = /^https?:\/\/tmdb\.([^\/]+)\//.exec(String(url));
          if (dm) window.qdl_cub_domain = dm[1];          // домен CUB — qdl.js возьмёт для /blocked
          var ru = qdlRewriteCub(url);
          if (ru) arguments[1] = ru;
        }
      } catch (e) {}
      return qdlXhrOpen.apply(this, arguments);
    };
    lampainit_invc.rewriteCubUrl = qdlRewriteCub;         // наружу — для тестов
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
