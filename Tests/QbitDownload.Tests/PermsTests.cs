using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using QbitDownload;
using Shared.Models.Base;
using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// qdl 2.54 — права на скрытые разделы (D1versy Live / D1versy Rec) по айди устройства.
/// Здесь ровно то, что дорого сломать: санация айди (он приходит из query БЕЗ проверки символов
/// и становится ключом в JSON), разбор платформы из UA-токена и главный инвариант хранилища —
/// частый «пульс» устройства не имеет права затирать редко выдаваемые гранты.
/// </summary>
public class PermsTests
{
    static RequestModel Req(string uid, string ua = null, string ip = "192.168.87.31")
        => new RequestModel { user_uid = uid, UserAgent = ua, IP = ip };

    #region NormUid

    [Theory]
    [InlineData("dueq3shm", "dueq3shm")]
    [InlineData("DUEQ3SHM", "dueq3shm")]          // ext4 хранит регистр, зеркало на drvfs — нет
    [InlineData(" dueq3shm ", "dueq3shm")]
    [InlineData("a-b_c.d", "a-b_c.d")]
    [InlineData("../../etc/passwd", "etcpasswd")]  // слэши и «..» вырезаются, путь не собирается
    [InlineData("uid<script>", "uidscript")]
    public void NormUid_чистит_и_приводит_к_нижнему_регистру(string raw, string expected)
        => Assert.Equal(expected, Perms.NormUid(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]     // после чистки пусто
    [InlineData("..")]      // после Trim('.') пусто
    public void NormUid_пустой_или_мусорный_дает_null_а_не_общий_бакет(string raw)
    {
        // 🔴 Отличие от JutHistoryBucket: там пустой uid уходит в общий бакет _shared, а здесь это
        // означало бы «права для всех безымянных». Нет айди — нет доступа.
        Assert.Null(Perms.NormUid(raw));
    }

    [Fact]
    public void NormUid_режет_длину()
    {
        string s = Perms.NormUid(new string('a', 200));
        Assert.Equal(48, s.Length);
    }

    #endregion

    #region платформа из UA

    [Theory]
    [InlineData("Mozilla/5.0 ... lampa_client d1vision_mac/1.0.9-515", "mac", "1.0.9-515")]
    [InlineData("Mozilla/5.0 ... d1vision_ios/1.0.9-516", "ios", "1.0.9-516")]
    [InlineData("Mozilla/5.0 ... d1vision_android/1.2.3-590", "android", "1.2.3-590")]
    [InlineData("Mozilla/5.0 ... d1vision_windows/1.0.4", "windows", "1.0.4")]
    public void PlatformOf_читает_токен_как_и_клиентский_lampainit(string ua, string plat, string ver)
    {
        var (platform, client) = Perms.PlatformOf(ua);
        Assert.Equal(plat, platform);
        Assert.Equal(ver, client);
    }

    [Fact]
    public void PlatformOf_старый_бинарь_без_токена_это_android()
    {
        // Ровно как в lampainit-invc.js: lampa_client без d1vision_-токена исторически = Android.
        var (platform, client) = Perms.PlatformOf("Mozilla/5.0 (Linux) lampa_client");
        Assert.Equal("android", platform);
        Assert.Equal("", client);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/120")]
    [InlineData("VLC/3.0.20 LibVLC/3.0.20")]   // нативный плеер ходит за сегментами своим UA
    public void PlatformOf_без_токена_это_web(string ua)
        => Assert.Equal("web", Perms.PlatformOf(ua).platform);

    #endregion

    #region гранты

    [Fact]
    public void Неизвестное_устройство_прав_не_имеет()
    {
        TestEnv.FreshCache();
        Assert.False(Perms.Allowed("dueq3shm", Perms.FeatureLive));
        Assert.False(Perms.Allowed("dueq3shm", Perms.FeatureRec));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    public void Пустой_айди_прав_не_имеет(string uid)
    {
        TestEnv.FreshCache();
        Perms.Grant("dueq3shm", Perms.FeatureLive, true);   // чужие права на это не влияют
        Assert.False(Perms.Allowed(uid, Perms.FeatureLive));
    }

    [Fact]
    public void Grant_выдаёт_и_отзывает_поштучно()
    {
        TestEnv.FreshCache();

        Assert.True(Perms.Grant("dueq3shm", Perms.FeatureLive, true));
        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureLive));
        Assert.False(Perms.Allowed("dueq3shm", Perms.FeatureRec));

        Perms.Grant("dueq3shm", Perms.FeatureRec, true);
        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureRec));

        Perms.Grant("dueq3shm", Perms.FeatureLive, false);
        Assert.False(Perms.Allowed("dueq3shm", Perms.FeatureLive));
        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureRec));   // сосед не задет
    }

    [Fact]
    public void Grant_не_плодит_дублей_при_повторной_выдаче()
    {
        TestEnv.FreshCache();
        Perms.Grant("dueq3shm", Perms.FeatureLive, true);
        Perms.Grant("dueq3shm", Perms.FeatureLive, true);
        Perms.Grant("dueq3shm", Perms.FeatureLive, true);

        var row = Perms.List().Single(x => (string)x["uid"] == "dueq3shm");
        Assert.Single((JArray)row["grants"]);
    }

    [Fact]
    public void Grant_игнорирует_неизвестную_фичу()
    {
        TestEnv.FreshCache();
        Assert.False(Perms.Grant("dueq3shm", "root", true));
        Assert.False(Perms.Allowed("dueq3shm", "root"));
    }

    [Fact]
    public void Grant_принимает_айди_в_любом_регистре_и_с_мусором()
    {
        TestEnv.FreshCache();
        Perms.Grant(" DUEQ3SHM ", Perms.FeatureRec, true);
        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureRec));
    }

    [Fact]
    public void Grant_может_опережать_устройство()
    {
        // Владелец вправе вписать айди раньше, чем устройство впервые пришло на сервер.
        TestEnv.FreshCache();
        Perms.Grant("newphone", Perms.FeatureLive, true);
        Assert.True(Perms.Allowed("newphone", Perms.FeatureLive));

        Perms.Touch(Req("newphone", "lampa_client d1vision_ios/1.0.9-516"), force: true);
        Assert.True(Perms.Allowed("newphone", Perms.FeatureLive));
    }

    [Fact]
    public void FeaturesOf_отдаёт_обе_фичи_всегда()
    {
        TestEnv.FreshCache();
        Perms.Grant("dueq3shm", Perms.FeatureRec, true);

        var map = Perms.FeaturesOf("dueq3shm");
        Assert.Equal(Perms.Features.Length, map.Count);
        Assert.False(map[Perms.FeatureLive]);
        Assert.True(map[Perms.FeatureRec]);
    }

    [Fact]
    public void Киллсвитч_permsEnabled_false_открывает_всё()
    {
        TestEnv.FreshCache();
        try
        {
            ModInit.conf.permsEnabled = false;
            Assert.True(Perms.Allowed("кто-угодно", Perms.FeatureLive));
            Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureRec));
        }
        finally { ModInit.conf.permsEnabled = true; }
    }

    #endregion

    #region реестр устройств

    [Fact]
    public void Touch_записывает_платформу_версию_и_IP()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req("dueq3shm", "lampa_client d1vision_mac/1.0.9-515", "192.168.87.31"), force: true);

        var row = Perms.List().Single(x => (string)x["uid"] == "dueq3shm");
        Assert.Equal("mac", (string)row["platform"]);
        Assert.Equal("1.0.9-515", (string)row["client"]);
        Assert.Equal("192.168.87.31", (string)row["ip"]);
    }

    [Fact]
    public void Touch_не_затирает_гранты()
    {
        // 🔴 Главный инвариант хранилища: реестр (частый «пульс») и гранты (редкая правка из админки)
        // лежат в одном файле. Раздельные пути read→write затирали бы именно выданные права.
        TestEnv.FreshCache();
        Perms.Grant("dueq3shm", Perms.FeatureLive, true);
        Perms.Grant("dueq3shm", Perms.FeatureRec, true);

        for (int i = 0; i < 20; i++)
            Perms.Touch(Req("dueq3shm", "d1vision_mac/1.0.9-515"), force: true);

        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureLive));
        Assert.True(Perms.Allowed("dueq3shm", Perms.FeatureRec));
    }

    [Fact]
    public void Touch_не_затирает_платформу_запросом_нативного_плеера()
    {
        // VLC/ExoPlayer тянет сегменты своим UA — без защиты платформа устройства схлопнулась бы
        // в «web» ровно в момент просмотра, и список в админке стал бы нечитаемым.
        TestEnv.FreshCache();
        Perms.Touch(Req("dueq3shm", "lampa_client d1vision_mac/1.0.9-515"), force: true);
        Perms.Touch(Req("dueq3shm", "VLC/3.0.20 LibVLC/3.0.20"), force: true);

        var row = Perms.List().Single(x => (string)x["uid"] == "dueq3shm");
        Assert.Equal("mac", (string)row["platform"]);
    }

    [Fact]
    public void Touch_троттлится_но_первый_вызов_проходит()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req("dueq3shm", "d1vision_mac/1.0.9-515"));
        Assert.Single(Perms.List());

        // второй заход в ту же минуту не должен ничего ломать
        Perms.Touch(Req("dueq3shm", "d1vision_mac/1.0.9-515"));
        Assert.Single(Perms.List());
    }

    [Fact]
    public void Touch_без_айди_устройства_не_заводит()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req(null, "d1vision_mac/1.0.9-515"), force: true);
        Perms.Touch(Req("", "d1vision_mac/1.0.9-515"), force: true);
        Assert.Empty(Perms.List());
    }

    [Fact]
    public void List_отдаёт_свежие_сверху()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req("aaaaaaaa", "d1vision_mac/1"), force: true);
        System.Threading.Thread.Sleep(5);
        Perms.Touch(Req("bbbbbbbb", "d1vision_ios/1"), force: true);

        var list = Perms.List();
        Assert.Equal("bbbbbbbb", (string)list[0]["uid"]);
    }

    [Fact]
    public void Rename_и_Forget()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req("dueq3shm", "d1vision_mac/1.0.9-515"), force: true);

        Assert.True(Perms.Rename("dueq3shm", "  Мак в гостиной  "));
        Assert.Equal("Мак в гостиной", (string)Perms.List().Single()["name"]);

        Perms.Grant("dueq3shm", Perms.FeatureLive, true);
        Assert.True(Perms.Forget("dueq3shm"));
        Assert.Empty(Perms.List());
        Assert.False(Perms.Allowed("dueq3shm", Perms.FeatureLive));   // забыли — значит и право снято
    }

    [Fact]
    public void Rename_режет_слишком_длинное_имя()
    {
        TestEnv.FreshCache();
        Perms.Rename("dueq3shm", new string('я', 300));
        Assert.Equal(64, ((string)Perms.List().Single()["name"]).Length);
    }

    [Fact]
    public void Card_отдаёт_нормализованный_айди_и_подпись()
    {
        TestEnv.FreshCache();
        Perms.Touch(Req("dueq3shm", "d1vision_mac/1.0.9-515"), force: true);
        Perms.Rename("dueq3shm", "Мак в гостиной");

        var card = Perms.Card(" DUEQ3SHM ");
        Assert.Equal("dueq3shm", (string)card["uid"]);
        Assert.Equal("Мак в гостиной", (string)card["name"]);
        Assert.Equal("mac", (string)card["platform"]);
    }

    [Fact]
    public void Card_без_айди_не_падает()
    {
        TestEnv.FreshCache();
        var card = Perms.Card(null);
        Assert.Equal("", (string)card["uid"]);
    }

    #endregion
}
