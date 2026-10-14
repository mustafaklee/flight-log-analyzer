# ADR-0006: Mesajlaşma ve arka plan işleri

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Log işleme uzun sürer, tekrar denenebilir olmalı ve aynı dosya iki kez işlendiğinde yinelenen sonuç üretmemelidir. Prompt RabbitMQ ve Hangfire'ı birlikte istiyor; sorumlulukları ayrılmalı.

## Karar
- **PostgreSQL gerçeğin kaynağıdır**: `LogProcessingJob` satırı işin durumunu, deneme sayısını ve hatasını tutar.
- **RabbitMQ** işleme işlerini taşır (`flightlog.processing` kuyruğu, `flightlog.processing.dlq` dead-letter kuyruğu). Mesaj sözleşmeleri `FlightLog.Contracts` içinde, sürüm alanıyla tanımlanır. İstemci: resmi `RabbitMQ.Client` (MIT/Apache-2.0/MPL-2.0 üçlü lisans).
- **Transactional outbox**: API dosya kaydını ve outbox satırını aynı transaction'da yazar; bir yayıncı outbox'ı RabbitMQ'ya iletir. Böylece "kayıt var ama mesaj yok" durumu oluşmaz.
- **İdempotency**: Worker işi `Queued → Processing` geçişini koşullu `UPDATE` ile alır; başarısız olursa mesajı ack'leyip çıkar. Türetilmiş veri yazımı uçuşun önceki çıktılarını aynı transaction'da değiştirir.
- **Retry**: Geçici hatalarda sınırlı deneme (ör. 3) ve artan gecikme; kalıcı hatalarda (bozuk dosya) deneme yapılmaz. Tükenen iş DLQ'ya gider ve `Failed` olur; kullanıcı API'den yeniden deneyebilir.
- **Hangfire** yalnızca zamanlanmış/periyodik bakım işleri için: takılı kalmış `Processing` işlerini kurtarma, outbox temizliği, saklama politikası. İşleme kuyruğu olarak kullanılmaz.
- **Redis** başlangıçta yalnızca dağıtık önbellek (ör. sinyal önizlemeleri) ve rate limiting için değerlendirilecek; somut ihtiyaç doğduğunda eklenecek.

## Alternatifler
- **MassTransit**: Outbox, retry ve DLQ'yu hazır sunar; v9 ile ticari lisansa geçtiği duyurulmuştu, lisans durumu kullanım öncesi doğrulanmalı. Resmi istemciyle ihtiyaç duyulan küçük alt küme yazılabilir.
- **Sadece Hangfire**: Kuyruk olarak da kullanılabilir; ancak prompt mesaj tabanlı iş akışı ve DLQ istiyor, ayrıca worker'ı bağımsız ölçeklemek için RabbitMQ daha uygun.

## Sonuçlar
- Outbox yayıncısı ve tüketici kodu bizim bakımımızda; entegrasyon testleri Testcontainers ile gerçek RabbitMQ üzerinde çalışacak (Aşama 4).
