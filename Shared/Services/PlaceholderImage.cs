using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Shared.Services;

// ── Заглушка вместо редиректа наружу (qdl 2.88) ────────────────────────────────────────────────
// Зачем вообще: картиночные прокси (/tmdb/img, /proxyimg) на ЛЮБОЙ отказ апстрима отдавали клиенту
// 302 на исходный чужой CDN — то есть наш собственный сервер отправлял устройство наружу, молча и
// в самой частой ветке (битый poster_path, лимит TMDB, спекулятивная обложка MusicBrainz, которой
// нет). Инвариант владельца — «клиент ходит ТОЛЬКО на наш сервер», и 404 его тоже не спасает:
// дырка в сетке постеров — такой же брак, как утечка. Поэтому на отказ отдаём СВОИ байты.
//
// Почему PNG, а не SVG: этот же URL забирает системный лаунчер Android TV (каналы/рекомендации,
// D1VAuth.signUri → card.img), а он растр рисует надёжно, вектор — как повезёт.
//
// Размер 8×12 = пропорция постера 2:3, цвет #1d1f20 = фон приложения (index.html body). В сетке
// растягивается CSS'ом в ровный тёмный прямоугольник, а не в размытую кашу.
//
// 🔴 ОБЯЗАТЕЛЬНО NoCache у вызывающего: ручки постеров помечены [Staticache(immutable: true)], и
// без запрета записи заглушка залипла бы на этом URL на ГОД — один сетевой чих TMDB навсегда
// похоронил бы постер. Редирект раньше был безопасен именно этим (StaticacheWriter не пишет 3xx);
// заменяя его на 200, запрет кеша надо ставить руками.
public static class PlaceholderImage
{
    /// <summary>Сплошной #1d1f20, 8×12, 75 байт.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAMCAIAAADQ/GvKAAAAEklEQVR42mOQlVfAihhGJdARADg4IoFy/vEpAAAAAElFTkSuQmCC");

    /// <summary>
    /// Отдать заглушку. Если ответ уже начал уходить клиенту (отказ случился на середине стрима),
    /// не делаем ничего: дописывать картинку в середину чужого тела бессмысленно.
    /// </summary>
    public static async Task WriteAsync(HttpContext context, int statusCode = StatusCodes.Status200OK)
    {
        if (context == null || context.Response.HasStarted)
            return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = Png.Length;
        context.Response.Headers["Cache-Control"] = "no-store";

        await context.Response.Body.WriteAsync(Png).ConfigureAwait(false);
    }
}
