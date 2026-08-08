using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Нормализация языка аудиодорожки (qdl 2.24). Клиент сам ничего не угадывает — он читает
/// готовый код из lang2, поэтому вся ответственность за «не соврать» лежит здесь.
/// </summary>
public class AudioLangTests
{
    [Theory]
    // по ffprobe-тегу
    [InlineData("rus", null, "ru")]
    [InlineData("ru", null, "ru")]
    [InlineData("eng", null, "en")]
    [InlineData("jpn", null, "ja")]
    [InlineData("ukr", null, "uk")]
    [InlineData("ita", null, "it")]
    // тега нет → эвристика по осмысленной подписи
    [InlineData("", "Дубляж", "ru")]
    [InlineData("", "LostFilm", "ru")]
    [InlineData("", "Многоголосый закадровый", "ru")]
    [InlineData("und", "HDRezka Studio", "ru")]
    [InlineData("", "English", "en")]
    [InlineData("", "Japanese", "ja")]
    public void Определяет_язык(string raw, string label, string expected)
        => Assert.Equal(expected, Access.LangCode(raw, label));

    [Theory]
    // ⚠️ При пустом теге подпись формируется как LangName("") = «Оригинал» — она НИЧЕГО
    // не говорит о языке, и классифицировать её нельзя (иначе любая дорожка стала бы «русской»).
    [InlineData("", "Оригинал")]
    [InlineData("", "Track 1")]
    [InlineData("", null)]
    [InlineData("", "")]
    // экзотический тег: не выдумываем
    [InlineData("qqq", "Дубляж")]
    public void Не_выдумывает_язык(string raw, string label)
        => Assert.Null(Access.LangCode(raw, label));

    [Theory]
    [InlineData("ru", "Русский")]
    [InlineData("en", "Английский")]
    [InlineData("uk", "Украинский")]
    [InlineData("it", "Итальянский")]
    [InlineData("", "Оригинал")]
    public void Человеческое_название(string code, string expected)
        => Assert.Equal(expected, Access.LangName(code));
}
