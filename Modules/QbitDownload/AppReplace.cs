using System;
using System.Text;
using Shared.Models.Events;

namespace QbitDownload;

// Патчи upstream-бандла Lampa (app.min.js) ПРИ ОТДАЧЕ — через EventListener.AppReplace
// (дёргается в LampaWeb/ApiController.LampaApp с arg "appjs"), чтобы не добавлять
// конфликтных точек в upstream-файлы. Что вырезаем и почему:
//   notice-icon — штатный колокольчик уведомлений в шапке (у нас свой, qdl-noti-head);
//   notice-cub  — NoticeCub: 5-мин опрос cub-уведомлений + TMDB-обогащение (не нужны);
//                 NoticeLampa/pushNotice НЕ трогаем — classes.lampa читается бандлом без гарда;
//   menu-items  — пункты левого меню «Релизы»/«Расписание»/«Подписки»;
//   tt-extract  — фоновый парсер TimeTable: TMDB-запрос каждые 30 сек вечным циклом;
//   tt-rows     — ряды «Скоро выйдут»/«Недавно вышли» на главной (питались от TimeTable);
//   dev-wait    — 1000 мс ожидания «тройного вверх» перед стартом очереди загрузки
//                 (оставлено под кукой qdl_unlock=1);
//   splash-*    — 1000 мс + fadeOut 500 мс логотипа УЖЕ ПОСЛЕ готовности интерфейса;
//   shots       — сторонний JS с cub.red каждый старт (services=false его не ловил);
//   plugin-reset— reset=Math.random() у URL плагинов: кеш-бастер, бивший по внешним клиентам;
//   swr         — stale-while-revalidate у клиентского кеша Request: попадание в ЖИВОЙ кеш
//                 отдаётся как раньше, но параллельно тихо дотягивается свежий ответ — он
//                 переписывает запись кеша и уходит событием 'request_revalidate'. Что и когда
//                 перестраивать, решает qdl.js (чтобы политика менялась по воздуху);
//   preroll     — рекламный преролл перед плеером: Preroll.show() тянет Google IMA SDK
//                 (https://imasdk.googleapis.com/js/sdkloader/ima3.js) и крутит VAST из data.vast_url.
//                 Штатного выключателя нет: disable_features.ads в текущем бандле не читается
//                 (0 вхождений) — только патч. Заменяем ветку на безусловный запуск плеера.
// Якоря — неминглящиеся литералы бандла (пин tree в LampaWeb/ModInit). Смена tree →
// якорь тихо не найдётся (warn в лог), остатки прячет CSS-фолбэк в lampainit-invc.js.
public static class AppPatch
{
    sealed record P(string label, string anchor, string replacement);

    static readonly P[] patches = new[]
    {
        new P("notice-icon",
            "this.icon = Head.addIcon(Template.string('icon_bell'), this.open.bind(this));",
            "this.icon = $('<div class=\"notice--icon hide\"></div>');/*qdl-cut:notice-icon*/"),
        new P("notice-cub",
            "this.classes.cub = new NoticeCub();",
            "/*qdl-cut:notice-cub*/"),
        new P("menu-items",
            "if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;",
            "if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;\n      if (item.action == 'relise' || item.action == 'timetable' || item.action == 'subscribes') return false;/*qdl-cut:menu*/"),
        new P("tt-extract",
            "Timer.add(time_extract, extract);",
            "/*qdl-cut:tt-extract*/"),
        // якорь встречается ровно 2 раза — оба в ContentRows timetable_lately/recently, режем оба
        new P("tt-rows",
            "if (screen == 'category' && params.url == 'movie') return;",
            "return;/*qdl-cut:tt-rows*/"),
        // Ранний выход из Preroll.show — ровно та же ветка, которой бандл сам пропускает рекламу
        // (`if (type.any) return call();` чуть ниже по коду), поэтому плеер стартует как обычно,
        // а IMA SDK не грузится вовсе. Точка выбрана по ОТДАВАЕМОМУ бандлу: прежний однострочный
        // якорь `if (data.vast_url) Preroll.show(...)` в нём не встречается — там `Preroll.show`
        // зовётся из четырёх мест с колбэком-функцией.
        new P("preroll",
            "function show$5(data, call) {",
            "function show$5(data, call) {return call();/*qdl-cut:preroll*/"),
        // Автопилот jut.su: при ВЫКЛЮЧЕННОМ тумблере автопереход к следующей серии не нужен.
        // Гейт именно здесь, потому что штатный выключатель `playlist_next` глобальный —
        // выключив его, мы отняли бы автопереход у «Загрузок» и у всего остального.
        // Флаг ставит qdl.js на сам элемент плейлиста, `work` — текущий элемент плеера.
        // Ручное переключение серий (кнопки панели) при этом остаётся.
        new P("jut-autonext",
            "if (Storage.field('playlist_next') && !$('body').hasClass('selectbox--open')) PlayerPlaylist.next();",
            "if (!(work && work.qdl_no_autonext) && Storage.field('playlist_next') && !$('body').hasClass('selectbox--open')) PlayerPlaylist.next();/*qdl-cut:jut-autonext*/"),
        // Segments наружу не экспортирован, а нам нужно доставить разметку опенинга серии,
        // которая приехала уже ПОСЛЕ старта плеера (сегменты соседних серий сервер знает
        // только после резолва их страниц). Без экспорта такая серия просто останется без
        // скипа — поэтому патч не критичный, но полезный.
        new P("segments-export",
            "PlayerPlaylist: PlayerPlaylist,",
            "PlayerPlaylist: PlayerPlaylist,\n      Segments: Segments,/*qdl-cut:segments-export*/"),
        // ── qdl 2.53: стартовые паузы бандла ───────────────────────────────────────────
        // Обе — чистое ожидание, а НЕ сеть. Замер живого сервера: index.html 2.3 мс,
        // app.min.js 5.5 мс, /lampainit.js 4–8 мс, /qdl/notifications 7 мс. «Загрузка»,
        // которую видит пользователь, — вот эти 2.5 с, и они платятся каждым стартом
        // каждого клиента (веб, mac, iOS, Windows, Android — страница у всех одна).
        //
        // dev-wait: developerApp() жёстко ждёт 1000 мс, не нажмут ли трижды «вверх» (вход в
        // меню разработчика), и только потом зовёт loadLang → loadTask, то есть пауза стоит
        // ДО всей очереди загрузки. Режем не насухо, а гейтим кукой qdl_unlock=1 — той же,
        // что уже открывает «Настройки»/«Консоль» (lampainit-invc.js и qdl.js): у обычных
        // клиентов 0 мс, у владельца с кукой прежние 1000 мс и рабочий тройной «вверх».
        new P("dev-wait",
            "function developerApp(proceed) {",
            "function developerApp(proceed) {if (!/(?:^|;\\s*)qdl_unlock=1/.test(document.cookie || '')) return proceed();/*qdl-cut:dev-wait*/"),
        // splash-wait / splash-fade: showApp() зовёт startApp() СРАЗУ, а логотип .welcome
        // снимает через setTimeout 1000 мс + fadeOut 500 мс — полторы секунды заставки уже
        // ПОСЛЕ того, как интерфейс готов и работает. 150 мс / fade 200 мс — выбор владельца
        // (нолём не делаем: на слабом ТВ первый кадр застал бы недорисованный экран).
        new P("splash-wait",
            "}, 1000); // Старт приложения",
            "}, 150);/*qdl-cut:splash-wait*/ // Старт приложения"),
        new P("splash-fade",
            "$('.welcome').fadeOut(500, function () {",
            "$('.welcome').fadeOut(200, function () {/*qdl-cut:splash-fade*/"),
        // shots: сторонний JS с cub.red (177 КБ сырых / 45 КБ br) грузится КАЖДЫЙ старт и
        // исполняется в НАШЕМ origin, где в cookie лежит ключ периметра D1Vision. Мы считали,
        // что его гасит services=false — неправда, и в lampainit-invc.js это было написано
        // ошибочно: у sport и tsarea гейт действительно по services, а у shots СВОЙ, только
        // по iptv и hostname !== 'localhost'. Подчиняем его тому же флагу, а не вырезаем:
        // вернуть = services:true, и поведение совпадёт с тем, что обещает комментарий.
        // ⚠️ Якорь БЕЗ хоста: {localhost} подставляется в ApiController.LampaApp ДО того,
        // как дёргается EventListener.AppReplace.
        new P("shots",
            "if (window.location.hostname !== 'localhost' && !window.lampa_settings.iptv) include.push(",
            "if (window.lampa_settings.services && window.location.hostname !== 'localhost' && !window.lampa_settings.iptv) include.push(/*qdl-cut:shots*/"),
        // plugin-reset: addPluginParams дописывает к URL КАЖДОГО плагина reset=<random>. Весь
        // блок гейтится «в адресе нет IPv4», поэтому дома (192.168.87.24) не срабатывает, а на
        // tv.d1versy.com срабатывает всегда — внешние клиенты качают все шесть плагинов
        // (~42 КБ br) мимо любого кеша на каждом старте, и заставку держит именно это:
        // showApp() зовётся из Plugins.load(showApp), то есть ждёт ВСЕ плагины.
        // 🔴 Снимать ТОЛЬКО в паре с revalidate-ветвью Staticache (no-cache + ETag): reset=
        // был единственным механизмом доставки новой версии плагина внешнему клиенту.
        // Якорь с Utils$1 — как и preroll (show$5) выше: tree бандла пинится в LampaWeb/ModInit,
        // при его смене якорь тихо не найдётся и уедет warn в лог.
        new P("plugin-reset",
            "encode = Utils$1.addUrlComponent(encode, 'reset=' + Math.random());",
            "/*qdl-cut:plugin-reset*/"),
        // ── qdl 2.63: stale-while-revalidate у клиентского кеша Request ────────────────────
        // Ряды каталога лежат в IndexedDB со сроком 2–7 СУТОК (life: day*2 … day*7). Пока запись
        // «живая», cacheGet отдаёт снимок и сети НЕ БЫВАЕТ ВООБЩЕ: появившийся на сервере фильм и
        // новый порядок популярности клиент не увидит до протухания. Кеш при этом нужен — он и
        // даёт мгновенную отрисовку. Требование владельца: рисовать быстро, но решать — серверу.
        //
        // 🔴 Патч НИЧЕГО не решает сам, вся политика — в qdl.js (меняется по воздуху). Снимок
        // отдаётся ровно как раньше (secuses(cached, true) → fromcache=true → повторного cacheSet
        // нет), ветка промаха и аварийный cache_old не тронуты. Добавлено ровно две вещи:
        //   1) метка cached.qdl_req — по ней qdl.js связывает готовый ряд с URL. Иначе связать
        //      нечем: в line.data.url лежит МЕТОД ('?sort=now_playing'), полный адрес туда не
        //      попадает. В кеш метка не утекает: тело из кеша обратно не пишется.
        //   2) тихий догон свежего ответа: переписывает запись кеша (cacheSet РОВНО ОДИН РАЗ) и
        //      уходит событием 'request_revalidate'.
        // 🔴 complite/secuses для свежего ответа звать НЕЛЬЗЯ: Progress в Api.partNext досчитал бы
        // задачу второй раз, partLoaded ушёл бы повторно, а Main.build() не идемпотентен —
        // ряды бы задвоились.
        //
        // Выключатели, оба читаются в рантайме (второй патч бандла не нужен):
        //   • window.lampa_settings.qdl_swr = false — серверный, из lampainit-invc.js (образец: shots);
        //   • window.qdl_swr(params) — предикат из qdl.js, там же троттлинг (образец: кука dev-wait).
        // Якорь уникален (1 вхождение и в вендоренном, и в отдаваемом бандле). Смена tree → warn
        // в лог, поведение возвращается к прежнему (мягкая деградация).
        new P("swr",
            "secuses(cached, true);",
            "try { if (cached && typeof cached === 'object') cached.qdl_req = params.url; } catch (e) {} " +
            "secuses(cached, true); " +
            "try { if ((!window.lampa_settings || window.lampa_settings.qdl_swr !== false) && " +
            "typeof window.qdl_swr === 'function' && window.qdl_swr(params)) { " +
            "var qdl_swr_req = $.extend({}, data); " +
            "qdl_swr_req.success = function (fresh) { if (!fresh) return; cacheSet(params, fresh); " +
            "Lampa.Listener.send('request_revalidate', { params: params, url: params.url, data: fresh, cached: cached }); }; " +
            "qdl_swr_req.error = function () {}; $.ajax(qdl_swr_req); } } catch (e) {}/*qdl-cut:swr*/"),
    };

    public static void Attach() => EventListener.AppReplace += OnAppReplace;
    public static void Detach() => EventListener.AppReplace -= OnAppReplace;

    static StringBuilder OnAppReplace(string name, EventAppReplace e)
    {
        if (name != "appjs")
            return e.bulder;   // appcss/online/sisi не трогаем

        return new StringBuilder(PatchAppJs(e.bulder.ToString()));
    }

    // чистая функция — покрыта Tests/QbitDownload.Tests/AppPatchTests
    public static string PatchAppJs(string js)
    {
        foreach (var p in patches)
        {
            if (js.Contains(p.replacement))
                continue;   // уже пропатчено (Staticache может прогнать повторно)

            if (!js.Contains(p.anchor))
            {
                Console.WriteLine($"[QbitDownload] appjs patch '{p.label}': anchor NOT found (tree changed?) — skip");
                continue;
            }

            js = js.Replace(p.anchor, p.replacement);
        }

        return js;
    }
}
