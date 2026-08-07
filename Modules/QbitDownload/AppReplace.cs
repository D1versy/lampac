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
//   tt-rows     — ряды «Скоро выйдут»/«Недавно вышли» на главной (питались от TimeTable).
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
