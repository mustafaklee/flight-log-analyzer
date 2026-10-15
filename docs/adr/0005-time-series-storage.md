# ADR-0005: Zaman serisi verisinin saklanması

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Tipik bir uçuş logu yüzlerce sinyal ve milyonlarca örnek içerir (ör. 250 Hz IMU, 20 dakika ≈ 300.000 örnek/sinyal). Erişim deseni: "bu uçuşta, bu sinyaller için, bu zaman aralığında, en fazla N nokta". Satır bazlı rastgele sorgu veya uçuşlar arası örnek düzeyinde JOIN beklenmiyor; karşılaştırmalar özet istatistikler üzerinden yapılıyor.

## Karar
- Normalize sinyal örnekleri, sinyal başına sabit boyutlu bloklar (ör. 65.536 örnek) halinde sıkıştırılmış ikili dosyalar olarak nesne depolamada tutulur: `derived/{flightId}/{normalizationVersion}/{signalId}/{chunk}.bin`.
- PostgreSQL'de yalnızca **sinyal kataloğu** (ad, birim, kaynak mesaj/alan, instance, örnek sayısı, zaman aralığı, min/max/ortalama) ve **blok indeksi** (blok başına ilk/son zaman damgası) tutulur.
- Grafik isteklerinde ilgili bloklar okunur ve min/max korumalı downsampling (ör. LTTB veya bucket min/max) uygulanır. Sık istenen önizlemeler (ör. 2.000 nokta) işleme sırasında üretilip saklanabilir.
- Olaylar, mod geçişleri ve parametreler küçük hacimli olduğu için PostgreSQL tablolarında tutulur.

## Alternatifler
- **Tek `samples(flight_id, signal, ts, value)` tablosu**: Uçuş başına milyonlarca satır, büyük indeks maliyeti; prompt açıkça kaçınılmasını istiyor.
- **TimescaleDB**: Hypertable ve sıkıştırma güçlü ama uçuşlar arası zaman bazlı sorgu ihtiyacı yok; ek uzantı ve işletim maliyeti. Ölçüm somut bir ihtiyaç gösterirse yeniden değerlendirilecek.
- **Parquet dosyaları**: Standart ve araç desteği iyi; .NET'te ek bağımlılık (Parquet.Net) gerektirir. Aşama 3'te basit ikili blok formatı ile Parquet ölçülerek karşılaştırılacak; karar ölçüme göre güncellenebilir.
- **PostgreSQL'de `bytea`/dizi olarak bloklar**: Mümkün, ancak veritabanı boyutunu ve yedekleme süresini büyütür.

## Sonuçlar
- Türetilmiş veri yeniden üretilebilir (ham dosya + parser sürümü); normalizasyon sürümü değişince yeniden işleme mümkün.
- Performans hedefleri Aşama 8'de benchmark ile doğrulanacak.

## Uygulama notu (Aşama 3, 2026-10-15)
Karar ölçüyle birlikte şu biçimde uygulandı:

- **Blok birimi sinyal değil seridir** (mesaj + instance). Bir mesajın tüm sayısal alanları aynı zaman damgalarını paylaştığı için seri başına blok, zaman damgalarını bir kez saklar ve nesne sayısını azaltır. Bir istek yalnızca istenen sütunları çözer.
- Nesne anahtarı: `derived/{flightId}/{jobId}/{seriIndex:D5}/{blok:D6}.fls`. Her işleme denemesi kendi önekine yazar; katalog commit edildikten sonra eski önek silinir, başarısız denemenin öneki hemen silinir.
- Format **FLS1** (`SignalChunkCodec`): 20 baytlık başlık (sihirli değer, sürüm, kayıt/sütun sayısı, sıkıştırılmış uzunluk) + zlib içinde zig-zag varint delta zaman damgaları ve sütun başına 8 bayt düzlemine ayrılmış float64 değerler. NaN/sonsuz değerler bit düzeyinde korunur. Blok başına en fazla 16.384 kayıt; seri arabelleği 2 MiB'ı, tüm arabellekler 96 MiB'ı aşınca boşaltılır.
- Ölçülen boyutlar (Docker Compose, gerçek fixture'lar): PX4 `sample.ulg` (64.542 kayıt, 303 sinyal) → 1,19 MB blok, 1,4 s; ArduPilot SITL `.bin` (5,05 MB ham, 125.722 kayıt, 908 sinyal) → 0,93 MB blok, 0,8 s.
- Downsampling: kova başına min/maks (`Downsampler`), yalnızca geçersiz örnek içeren kova boşluk olarak kalır. Önizleme önceden üretilmiyor; okuma zaten blok bazında.
- Katalog: `FlightSignalSeries`, `SignalChunks`, `FlightSignals` (birim, beyan edilen çarpan, kaynak/dönüşüm, örnek/geçersiz sayısı, min/maks).

**Parquet karşılaştırması yapılmadı.** Aşama 3 kapsamında yalnızca FLS1 ölçüldü; Parquet.Net ile aynı veri üzerinde boyut ve okuma süresi karşılaştırması Aşama 8'deki performans çalışmasına ertelendi. Format sürümlü olduğu (başlıkta sürüm baytı, katalogda iş/önek) için ileride geçiş, yeniden işleme ile yapılabilir.

