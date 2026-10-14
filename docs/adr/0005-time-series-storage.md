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
