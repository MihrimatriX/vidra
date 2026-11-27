<!--
  Bir sonraki sürümün notları. Kullanıcının fark edeceği her değişiklikte PR'da buraya bir madde ekle.
  "Sürüm yayınla" bu dosyayı docs/releases/v<sürüm>.md yapar ve boş şablona döndürür; başlık, İndir ve
  Gereksinim satırlarını kendisi ekler. Hiç madde yoksa notlar birleşen PR başlıklarından üretilir.
  Boş kalan bölüm yayına girmez.
-->

### Yenilikler

- Hata ekranlarında kısa açıklamanın altında yt-dlp'nin ham çıktısı görünür ve kopyalanabilir; analiz ve indirme hataları `%LOCALAPPDATA%\Vidra\vidra.log` dosyasına da yazılır.

### Düzeltmeler

- 1080p olan videolarda yalnızca 360p seçeneği gelmesi: YouTube akışları kısıtladığında (SABR, PO token, JS doğrulaması) Vidra TV istemcileriyle, gerekirse oturumsuz yeniden analiz eder; indirme de aynı yolu kullanır. İşe yaramazsa nedeni analiz ekranında yazar.
