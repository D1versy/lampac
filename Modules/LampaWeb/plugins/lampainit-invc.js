// //////////////
// Переименуйте файл lampainit-invc.js в lampainit-invc.my.js
// //////////////


var lampainit_invc = {};


// Лампа готова для использования 
lampainit_invc.appload = function appload() {
  Lampa.Utils.putScriptAsync(["{localhost}/qdl.js"]);  // QbitDownload: кнопка «Скачать» + раздел «Загрузки»
  // TorrServer — указываем на внешний контейнер (стабильный, MatriX latest, кэш на D)
  Lampa.Storage.set('torrserver_url', 'http://192.168.87.24:8090');
  Lampa.Storage.set('torrserver_auth', 'false');

  // переустановить локальную разблокировку premium (на случай если значение перетёрлось); без токена → наружу ничего не уходит
  try {
    localStorage.setItem('account_user', JSON.stringify({ id: 1, premium: Date.now() + 3650 * 86400000 }));
    localStorage.setItem('developer_nopremium', 'false');
  } catch (e) {}
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
        // вкладка «CUB» в навигации (текст, не data-атрибут)
        var tabs = document.querySelectorAll('.navigation-tabs__button');
        for (var j = 0; j < tabs.length; j++)
          if ((tabs[j].textContent || '').trim() === 'CUB') tabs[j].style.display = 'none';
      } catch (e) {}
    };
    hideLocked();
    try {
      var deb = null;
      new MutationObserver(function () { if (deb) return; deb = setTimeout(function () { deb = null; hideLocked(); }, 300); })
        .observe(document.body, { childList: true, subtree: true });
    } catch (e) {}
  } catch (e) {}
  // Lampa.Utils.putScriptAsync(["{localhost}/myplugin.js"]);  // wwwroot/myplugin.js
  // Lampa.Utils.putScriptAsync(["{localhost}/plugins/ts-preload.js", "https://nb557.github.io/plugins/online_mod.js"]);
  // Lampa.Storage.set('proxy_tmdb', 'true');
  // etc
};


// Лампа полностью загружена, можно работать с интерфейсом 
lampainit_invc.appready = function appready() {
  // $('.head .notice--icon').remove();
};


// Выполняется один раз, когда пользователь впервые открывает лампу
lampainit_invc.first_initiale = function firstinitiale() {
  // Здесь можно указать/изменить первоначальные настройки 
  // Lampa.Storage.set('source', 'tmdb');
};


// ── ДО загрузки Lampa: локальная разблокировка premium ──
// hasPremium() = countDays(Date.now(), account_user.premium) > 0. Ставим premium далеко в будущее (мс).
// НЕ трогаем токен 'account' → permit.sync/access=false → наружу на cub.rip ничего не уходит.
try {
  localStorage.setItem('account_user', JSON.stringify({ id: 1, premium: Date.now() + 3650 * 86400000 }));
  localStorage.setItem('developer_nopremium', 'false');
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
