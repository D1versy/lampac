using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты серверного патчера бандла Lampa (<c>AppPatch.PatchAppJs</c>, см. AppReplace.cs):
/// вырезание upstream-колокольчика (иконка Notice + NoticeCub), пунктов меню
/// Релизы/Расписание/Подписки, фонового TimeTable-парсера и рядов на главной.
/// Живой бандл в репо не хранится — тесты идут по синтетическому js со всеми якорями;
/// живая отдача проверяется после деплоя (grep qdl-cut по app.min.js).
/// </summary>
public class AppPatchTests
{
    // все 5 якорей + окружение, как в бандле (пин tree в LampaWeb/ModInit)
    const string AllAnchors = @"
      key: 'init',
      value: function init() {
        var _this = this;
        this.icon = Head.addIcon(Template.string('icon_bell'), this.open.bind(this));
        this.icon.addClass('notice--icon');
        this.classes.all = new NoticeAll();
        this.classes.lampa = new NoticeLampa();
        this.classes.cub = new NoticeCub();
      }
      if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;
      if (window.lampa_settings.disable_features.persons && item.action == 'myperson') return false;
      Timer.add(time_favorites, favorites);
      Timer.add(time_extract, extract);
      if (screen == 'category' && params.url == 'movie') return;
      other_lately_code();
      if (screen == 'category' && params.url == 'movie') return;
      other_recently_code();
    ";

    [Fact]
    public void PatchAppJs_AllAnchors_AllMarkersApplied()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("/*qdl-cut:notice-icon*/", result);
        Assert.Contains("/*qdl-cut:notice-cub*/", result);
        Assert.Contains("/*qdl-cut:menu*/", result);
        Assert.Contains("/*qdl-cut:tt-extract*/", result);
        Assert.Contains("/*qdl-cut:tt-rows*/", result);
    }

    [Fact]
    public void PatchAppJs_CutsNoticeIconAndCub()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("Head.addIcon(Template.string('icon_bell')", result);
        Assert.DoesNotContain("new NoticeCub()", result);
        // NoticeLampa живёт: classes.lampa читается бандлом без гарда (socket premiere, pushNotice)
        Assert.Contains("new NoticeLampa()", result);
        Assert.Contains("new NoticeAll()", result);
    }

    [Fact]
    public void PatchAppJs_MenuFilter_DropsAllThreeActions()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("item.action == 'relise'", result);
        Assert.Contains("item.action == 'timetable'", result);
        Assert.Contains("item.action == 'subscribes'", result);
        // штатная строка фильтра сохранена (замена дописывает, а не сносит)
        Assert.Contains("item.action == 'mytorrents') return false;", result);
    }

    [Fact]
    public void PatchAppJs_TtRows_ReplacesBothOccurrences()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("if (screen == 'category' && params.url == 'movie') return;", result);
        // оба ряда (lately + recently) получили безусловный return
        int count = result.Split("return;/*qdl-cut:tt-rows*/").Length - 1;
        Assert.Equal(2, count);
        Assert.DoesNotContain("Timer.add(time_extract, extract);", result);
        Assert.Contains("Timer.add(time_favorites, favorites);", result);   // сосед не задет
    }

    [Fact]
    public void PatchAppJs_NoAnchors_InputUnchangedNoThrow()
    {
        // смена tree: якорей нет — вход возвращается как есть (warn в лог), без исключений
        const string js = "var lampa = 'совсем другой бандл';";
        Assert.Equal(js, AppPatch.PatchAppJs(js));
    }

    [Fact]
    public void PatchAppJs_Idempotent()
    {
        // Staticache может прогнать отдачу повторно — второй прогон не должен менять результат
        // (критично для menu-патча, чей replacement содержит собственный якорь)
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void PatchAppJs_PartialAnchors_PatchesWhatItFinds()
    {
        // будущий tree: часть якорей уехала — уцелевшие всё равно режутся
        const string js = "this.classes.cub = new NoticeCub(); Timer.add(time_extract, extract);";
        string result = AppPatch.PatchAppJs(js);
        Assert.DoesNotContain("new NoticeCub()", result);
        Assert.DoesNotContain("Timer.add(time_extract, extract);", result);
    }
}
