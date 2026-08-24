namespace JacRed.Engine
{
    /// <summary>
    /// Разметка одной строки выдачи rutracker (<c>tracker.php</c>). Вынесена отдельным файлом
    /// сознательно: здесь нет ни ModInit, ни BaseController, поэтому файл линкуется в тестовый
    /// проект и паттерны проверяются на реальном куске html, а не «на глаз».
    ///
    /// 🔥 Зачем это появилось (24.08.2026). Ссылка на раздел в выдаче стала АБСОЛЮТНОЙ
    /// (<c>href="https://rutracker.org/forum/tracker.php?f=796&amp;nm=..."</c>), а паттерн ждал
    /// относительную (<c>href="tracker.php?f=</c>). Раздел в парсере обязателен — и каждая
    /// строка молча отбрасывалась. Снаружи это выглядело как «трекер работает, но раздач нет»:
    /// html залогинен, ошибок в логе ноль, выдача пустая. Поэтому префикс хоста везде
    /// опционален: работать обязаны обе формы.
    ///
    /// Стрелку размера сайт отдаёт то сущностью <c>&amp;#8595;</c>, то живым символом ↓ —
    /// принимаем оба варианта.
    /// </summary>
    public static class RutrackerRow
    {
        const string Href = "href=\"(?:https?://[^\"/]+/forum/)?";

        /// <summary>Название раздачи (ссылка на тему).</summary>
        public const string Title = Href + "viewtopic\\.php\\?t=[0-9]+\">([^\n\r]+)</a>";

        /// <summary>Id темы — он же id раздачи для parseMagnet.</summary>
        public const string Topic = Href + "viewtopic\\.php\\?t=([0-9]+)\"";

        /// <summary>Id раздела форума: по нему определяется тип (фильм/сериал/аниме/…).</summary>
        public const string Forum = Href + "tracker\\.php\\?f=([0-9]+)";

        /// <summary>Размер: «518&amp;nbsp;MB ↓» в ссылке на скачивание.</summary>
        public const string Size = Href + "dl\\.php\\?t=[0-9]+\">([^<]+?)\\s*(?:&#8595;|↓)</a>";

        public const string Seeds = "class=\"seedmed\">([0-9]+)";
        public const string Peers = "title=\"Личи\">([0-9]+)";
        public const string Created = "<p>([0-9]{2}-[^-<]+-[0-9]{2})</p>";
    }
}
