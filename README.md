<p align="center"><img src="docs/icon.png" width="72" alt=""></p>

<h1 align="center">Vidra</h1>

<p align="center">Windows için sade ve hızlı video, playlist, kanal ve müzik indirici. <a href="https://github.com/yt-dlp/yt-dlp">yt-dlp</a> üzerine kurulu.</p>

<p align="center">
  <a href="https://github.com/MihrimatriX/vidra/releases/latest/download/Vidra-Setup.exe"><b>Kurulumu indir</b></a> ·
  <a href="https://github.com/MihrimatriX/vidra/releases/latest/download/Vidra-Portable.exe">Taşınabilir sürüm</a> ·
  <a href="https://mihrimatrix.github.io/vidra/">Web sitesi</a>
</p>

![Vidra](docs/shot-light.png)

- **Orijinal kalite:** YouTube'daki akışlar dönüştürülmeden kaydedilir; bir seçim yeniden kodlamaya yol açıyorsa (MP3, kesin kesim) ekranda yazar
- **Tek video, Shorts, canlı yayın:** kalite, biçim, kodek, altyazı, klip kesme, SponsorBlock
- **Playlist ve kanal:** Videolar / Shorts / Canlı / Playlistler / Üyeler sekmeleri, filtreler, seçerek indirme
- **Üyelere özel içerik:** üye olduğun kanalların üyelere özel videoları ve playlistleri
- **Müzik modu:** MP3/FLAC, kare kapak, ses eşitleme, sanatçı/albüm/parça etiketleri
- **Kuyruk ve geçmiş:** eşzamanlı indirme, duraklat/sürdür, bitince uyut, istatistikler

Windows 10 1809+ / 11 (x64). Kurulum yönetici izni istemez; yt-dlp, ffmpeg ve Deno ilk açılışta otomatik indirilir.
Uygulama imzalı olmadığı için SmartScreen uyarısında **Ek bilgi → Yine de çalıştır** demen gerekebilir.

---

## Geliştirme

.NET 9 + WPF + CommunityToolkit.Mvvm. Tüm yt-dlp mantığı platformdan bağımsız `Vidra.Core` kütüphanesinde;
arayüz `Vidra.App` (WPF) içinde. yt-dlp, ffmpeg/ffprobe ve Deno ilk açılışta uygulamanın kendisi tarafından
`%LOCALAPPDATA%\Vidra\bin` altına indirilir — kullanıcı hiçbir şey kurmaz, yönetici izni gerekmez.

```powershell
dotnet run --project src/Vidra.App            # geliştirme
dotnet test                                    # 98 birim testi (Core)
dotnet publish src/Vidra.App -c Release -r win-x64 -o publish   # tek, bağımsız Vidra.exe (~175 MB)

# Küçük sürüm (.NET 9 Desktop Runtime kurulu makineler için, ~3 MB):
dotnet publish src/Vidra.App -c Release -r win-x64 -p:SelfContained=false -p:PublishReadyToRun=false -o publish-fd

# Sürüm dosyaları → dist/Vidra-Setup.exe + dist/Vidra-Portable.exe (Inno Setup 6 gerekir: winget install JRSoftware.InnoSetup)
pwsh installer/build.ps1
pwsh installer/make-icon.ps1                   # ikonu yeniden üret (renk/şekil değişince)
```

Kurulum yönetici izni istemez (`%LOCALAPPDATA%\Programs\Vidra`), Başlat menüsüne ve isteğe bağlı masaüstüne kısayol
ekler, Ayarlar › Uygulamalar'dan kaldırılabilir. Kaldırınca ayarlar ve geçmiş silinmez; yeni sürüm eskisinin üzerine kurulur.

Geliştirme gereksinimleri: Windows 10 1809+ / 11, .NET 9 SDK, Microsoft Edge WebView2 Runtime (Windows 11'de hazır gelir).

## Ekranlar

| Ekran | Neler var |
|---|---|
| **İndir** | Tek kutu: bağlantı yapıştır ya da yaz ve ara (birden çok link → hepsi kuyruğa). Video / Sadece ses; kalite kartları (boyut, kodek); biçim **Orijinal**/MP4/MKV/WebM; ses Orijinal/MP3/M4A/FLAC/WAV; **dönüştürme satırı** (kopyalanıyor mu, neyin yeniden kodlandığı). "Diğer seçenekler" altında: kodek (H.264/VP9/AV1), ≤30 fps, altyazı dilleri, **klip kesme** (anahtar karede kopyalayarak ya da kesin kesim), SponsorBlock, bölümler, hazır ayarlar, akış tablosu. |
| **Ara** | YouTube'da arama; sonuçtan tek tıkla analiz ya da kuyruğa ekleme. |
| **Playlist & kanal** | Videolar/Shorts/Canlı/Playlistler/**Üyeler** sekmeleri (sekmeli bağlantı doğrudan o sekmeyi açar); aralık, tarih, süre, arama, "indirilmemiş/üye" filtreleri; süre ve boyut tahmini; sanallaştırılmış liste (binlerce video); **Takip et** (yeni videolar otomatik iner); "Playlistlerim" gibi alt listeleri açma. |
| **Kuyruk & geçmiş** | Eşzamanlılık ±, duraklat/sürdür/iptal/tekrar dene, sıranın başına al, canlı hız grafiği, seçili işin yt-dlp günlüğü, "komutu kopyala" (çerez yolu maskelenir), **bitince uyut**; geçmişte filtre/arama, oynat, klasörde göster, tekrar indir. Bitmemiş işler kapanışta kaydedilir, açılışta duraklatılmış olarak geri gelir. |
| **İstatistik** | Toplam indirme, boyut ve süre; video/ses dağılımı; bu hafta; son 14 günün grafiği; en çok indirilen kanallar. |
| **Hesabım** | **Uygulama içi YouTube girişi** (WebView2), oturum testi, çıkış; hesap akışları: Abonelikler, Daha sonra izle, Beğenilenler, Geçmiş, Playlistlerim; takip edilen listeler ve kontrol aralığı; nazik mod. |
| **Ayarlar** | Çerez kaynağı (Vidra hesabı / Firefox / Chrome / Edge / Brave / cookies.txt), tarayıcı profili; klasör ve dosya adı şablonları (önizlemeli); format varsayılanları; -N/-R/hız sınırı/proxy; tema (açık/koyu/sistem), pano izleme, bildirimler, tepsi; bileşen sürümleri, yt-dlp güncellemesi (kararlı/nightly), yeniden indirme. |

Ayrıca: sistem tepsisi + baloncuk bildirimleri, pano izleme (yoklamasız, `AddClipboardFormatListener`), tek örnek (ikinci açılış bağlantıyı mevcut pencereye iletir: `Vidra.exe <url>`), koyu başlık çubuğu.

## Mimari

```
src/Vidra.Core/                      (net9.0 — Windows'a bağımlı değil, Linux'ta test edilir)
  Models/        DownloadOptions (değişmez record), AppSettings, CookieSource (JSON polimorfik), DownloadJob (gözlemlenebilir), HistoryEntry
  YtDlp/         YtDlpArgs (saf: ayar → argüman listesi), OutputParser, ProbeParser (-J → model), YtDlpService, YouTubeUrl, Templates
  Process/       IProcessRunner + ProcessRunner (ArgumentList, UTF-8, süreç ağacını öldürme)
  Queue/         DownloadQueue (eşzamanlılık, duraklat = öldür + .part'tan devam, geçmiş, bekleyen işler)
  Follow/        FollowService (takip edilen listeleri aralıkla kontrol, arşivle karşılaştır)
  Auth/          NetscapeCookies (cookies.txt yazıcı, oturum algılama)
  Tools/         ToolManager (bileşenleri indir/güncelle/sürüm oku)
  Storage/       AppPaths, JsonStore (atomik yazma), SettingsStore (debounce), ArchiveStore
src/Vidra.App/                       (net9.0-windows — WPF)
  Themes/        Light/Dark paletleri (DynamicResource), Styles (tüm kontrol şablonları), Icons (çizgi ikon geometrileri)
  Views/         DownloadView, SearchView, PlaylistView, QueueView, StatsView, AccountView, SettingsView, LoginWindow (WebView2)
  ViewModels/    her ekran için bir VM (CommunityToolkit.Mvvm kaynak üreticileri)
  Services/      ThemeService, AccountService, ClipboardWatcher, TrayService, PowerService
tests/Vidra.Core.Tests/              xUnit: argümanlar, ayrıştırma, probe, çerezler, kuyruk (sahte süreç), depolama, canlı yt-dlp (isteğe bağlı)
```

**Neden bu ayrım?** yt-dlp entegrasyonunun %90'ı saf fonksiyon: argüman üretimi, çıktı ayrıştırma, JSON eşleme.
Bunlar Core'da olduğu için UI olmadan, hızlı ve deterministik test edilir. Kuyruk `IProcessRunner` üzerinden
sahte bir yt-dlp ile test edilir. Canlı test için: `VIDRA_BIN_DIR=<yt-dlp/ffmpeg/deno klasörü> dotnet test`.

**İlerleme takibi:** yt-dlp'nin insan okunur çıktısını regex'le kazımak yerine `--progress-template` ile makine okunur
satırlar (`VIDRA:dl|…`) basılır; bitmiş dosya yolu `--print after_move:` ile alınır.

**Kalite seçimi:** `-f bv*+ba/b -S res:1080,fps:30,vcodec:h264,acodec:m4a` — sıralama, `-f "bv[height<=1080]"`
filtrelerinden dayanıklıdır. yt-dlp `-S` alanlarını kendi varsayılan sıralamasının *önüne* koyar; bu yüzden "En iyi"
seçiliyken de `res` en başa yazılır, yoksa kodek tercihi çözünürlüğü ezer (4K varken 1080p H.264 iner).

**Dönüştürme:** video ve ses her zaman kopyalanarak birleştirilir. **Orijinal** biçimde kodek ve kap zorlanmaz
(`--merge-output-format` yok): VP9/AV1 + Opus → WebM (kapak gömülecekse MKV), H.264 + AAC → MP4. Yeniden kodlama
yalnızca şu durumlarda olur ve indirme ekranında yazar: ses MP3/FLAC/WAV'a dönüştürülürken ve klipte **kesin kesim**
(`--force-keyframes-at-cuts`) açıkken. M4A/Opus seçilince o kodekteki akış seçildiği için ses kopyalanır.

## YouTube girişi nasıl çalışır

1. Hesabım → "YouTube'a giriş yap" ayrı bir WebView2 profili (`%LOCALAPPDATA%\Vidra\WebLogin`) ile Google giriş sayfasını açar.
2. youtube.com'a dönüldüğünde `SAPISID`/`SID` çerezleri varsa `CookieManager.GetCookiesAsync` ile okunur ve
   `%LOCALAPPDATA%\Vidra\youtube-cookies.txt` (Netscape biçimi) yazılır; yt-dlp'ye `--cookies` ile verilir.
3. Oturum tarayıcından **ayrı** olduğu için YouTube'un tarayıcıda yaptığı çerez yenilemesi onu geçersiz kılmaz.
4. Google gömülü tarayıcıda girişi reddederse ("Bu tarayıcı güvenli olmayabilir") pencere "Tarayıcıdan al" seçeneği sunar.
5. Çıkış: çerez dosyası ve WebView2 profil klasörü silinir.

## Bilinen sınırlar ve notlar

- **Bot doğrulaması / 429:** veri merkezi ve VPN IP'lerinde YouTube "Sign in to confirm you're not a bot" der; uygulama
  bunu ayrı bir hata türü olarak gösterir ve girişe yönlendirir. Ev bağlantısında genelde sorun olmaz.
- **Hesap riski:** yt-dlp ekibi, çerezle yoğun indirmenin hesabın/IP'nin işaretlenmesine yol açabileceği konusunda uyarıyor.
  Nazik mod (varsayılan açık) oturumla istekler arasına bekleme ekler; mümkünse ikincil bir hesap kullan.
- **Chromium tarayıcı çerezleri:** Windows'ta Chrome/Edge açıkken çerez veritabanını kilitleyebilir; Firefox ya da uygulama içi giriş daha güvenilir.
- `Playlistlerim` (`feed/playlists`) akışının yt-dlp desteği sürüme göre değişebilir; boş dönerse yt-dlp'yi güncelle.
- **Üyeler sekmesi:** yt-dlp kanalın `/membership` sekmesini okuyamadığı için Vidra kanal kimliğinden (`UC…`) üyelere özel
  listeyi (`UUMO…`) açar; bunun için kanalın videolar sekmesine bir ek sorgu yapılır.
- Yalnızca indirme hakkına sahip olduğun içerikleri indir; YouTube Kullanım Şartları geçerlidir.
