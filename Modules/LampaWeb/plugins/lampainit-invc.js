// //////////////
// Переименуйте файл lampainit-invc.js в lampainit-invc.my.js
// //////////////


var lampainit_invc = {};

// Версия НАШЕГО форка (видна в консоли клиента: window.qdl_fork_version).
// ⚠️ Правило владельца: при КАЖДОМ фиксе клиентских плагинов (qdl.js / lampainit-invc.js)
// бампать минор. Кэш-бастер URL (?v=) обновляется сам при рестарте контейнера
// (cacheVersion в ApiController: lampainit.js в Index(), qdl.js в LamInit) —
// эта версия нужна как человекочитаемый маркер «какой код реально крутится у клиента».
window.qdl_fork_version = '1.6';   // 1.5: единая сортировка «Загрузок» по дате загрузки; 1.6: платформы D1Vision (UA-токены d1vision_*, форс-ключи переехали на сервер) + OTA /d1vision/hosts.json + бренд


// Лампа готова для использования 
lampainit_invc.appload = function appload() {
  Lampa.Utils.putScriptAsync(["{localhost}/qdl.js?v={version}"]);  // QbitDownload: кнопка «Скачать» + раздел «Загрузки» ({version} = cache-buster, подставляет сервер)
  // TorrServer — указываем на внешний контейнер (стабильный, MatriX latest, кэш на D)
  Lampa.Storage.set('torrserver_url', 'http://192.168.87.24:8090');
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
