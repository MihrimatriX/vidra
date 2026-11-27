<!--
  Bir sonraki sürümün notları. Kullanıcının fark edeceği her değişiklikte PR'da buraya bir madde ekle.
  "Sürüm yayınla" bu dosyayı docs/releases/v<sürüm>.md yapar ve boş şablona döndürür; başlık, İndir ve
  Gereksinim satırlarını kendisi ekler. Hiç madde yoksa notlar birleşen PR başlıklarından üretilir.
  Boş kalan bölüm yayına girmez.
-->

### Yenilikler

### Düzeltmeler

- İndirmenin "0 bytes read … Giving up after 10 retries" ile düşmesi: yt-dlp'nin boş bıraktığı hata satırı yerine asıl neden gösterilir; YouTube akışı keserse iş oturumsuz ve taze bilgiyle kendiliğinden yeniden denenir.
- Altyazı indirmede 429 hatası: nazik mod kapalıyken de altyazı istekleri arasında kısa bekleme var.
