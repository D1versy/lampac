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

  // убрать упоминание стороннего сервиса (Телеграм), кнопку профиля/аккаунта и раздел «Синхронизация» в настройках
  try {
    if (!document.getElementById('qdl-hide-extras')) {
      var st = document.createElement('style');
      st.id = 'qdl-hide-extras';
      st.textContent = '.feed-head__info{display:none!important}'
        + '.head__action.open--profile{display:none!important}'
        + '[data-component="account"]{display:none!important}'
        + '.menu__item[data-action="about"]{display:none!important}';
      document.head.appendChild(st);
    }
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
