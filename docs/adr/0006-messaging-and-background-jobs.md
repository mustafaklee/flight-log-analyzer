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

## Uygulama notları (Aşama 4, 2026-10-15)

Karar uygulandı. Kararın metninden farklı olanlar ve ayrıntılar:

- **Hangfire kullanılmadı.** Gereken iki periyodik bakım işi var: takılı işleri kurtarmak ve outbox'ı temizleyip kaybolan mesajları yeniden duyurmak. Bunlar worker içindeki küçük bir `BackgroundService` (`MaintenanceWorker`, dakikada bir) ile yapılıyor. Hangfire'ın kendi tabloları, panosu ve lisans yükü bu ihtiyaç için fazla. Raporlama ve saklama politikası gibi zamanlanmış işler gelirse yeniden değerlendirilecek.
- **İki mesaj tipi**, ikisi de `flightlog.processing` kuyruğunda: `flightlog.process-log-file.v1` ve `flightlog.analyze-flight.v1` (`FlightLog.Contracts`). Mesaj yalnızca kimlik taşır; işin durumu veritabanından okunur. Mesaj tipi AMQP `type` özelliğinde, gövde JSON.
- **Outbox satırları domain olaylarından üretilir.** `FlightLogFile` `Queued` olduğunda ve bir `AnalysisRun` kuyruğa alındığında olay oluşur. EF Core `SaveChanges` interceptor'ı bu olaylardan aynı transaction'da `OutboxMessages` satırı yazar. Yayıncı (`OutboxPublisher`) satırları `FOR UPDATE SKIP LOCKED` ile alır, publisher confirm bekler ve ancak ondan sonra `PublishedAt` yazar. Bu yüzden teslim en az bir kez olur ve tüketici idempotent olmalıdır.
- **İdempotency:** Worker dosyayı `Queued → Processing`, analizi de `Queued → Running` geçişiyle alır. Bu geçiş satır sürümüne (`xmin`) bağlı compare-and-set'tir. İş zaten alınmış, bitmiş ya da silinmişse mesaj hiçbir şey yapmadan ack'lenir.
- **Ack zamanı:** Mesaj işin sonucu veritabanına yazıldıktan sonra ack'lenir. Okunamayan mesaj (bilinmeyen tip, bozuk JSON) ve otomatik denemeleri tükenen iş DLQ'ya gider. Sonuç yazılamadıysa (ör. veritabanına ulaşılamıyor) mesaj bir kez kuyruğa döner, ikinci teslimde DLQ'ya gider.
- **Retry:** Geçici hata sınıflandırması `TransientErrorClassifier` ile yapılır (Npgsql geçici hataları, zaman aşımı, ağ, S3 5xx ve 429). En fazla 3 otomatik deneme yapılır; gecikme 30 s × 2^(n−1), üst sınır 10 dk. Gecikmeli deneme outbox satırının `AvailableAt` alanıyla yapılır, broker eklentisi gerekmez. Kalıcı hatalar (bozuk dosya) denenmez.
- **Kaybolan mesajlar:** `Queued` durumda 30 dakikadan uzun bekleyen dosya ya da analiz için bakım işi yeni bir outbox satırı yazar. Örneğin broker'ın verisi silinmişse iş böyle kurtarılır. Yayınlanmış outbox satırları 7 gün sonra silinir.
- **İki taşıma modu:** `Messaging:Transport = RabbitMq` (Compose ve üretim) veya `Database`. `Database` modunda worker veritabanını yoklar ve outbox satırı yazılmaz. Bu mod entegrasyon testlerinin çoğunda ve broker'sız tek makine kurulumunda kullanılır. RabbitMQ yolu kendi test sınıfında gerçek bir RabbitMQ ile (Testcontainers) test edilir.
- RabbitMQ API'nin hazır olma kontrolüne (`/health/ready`) eklendi. Broker kapalıyken yükleme yine kabul edilir, mesaj outbox'ta bekler ve broker dönünce iletilir. Bu davranış Compose ortamında elle doğrulandı.
