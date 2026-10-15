# Yol haritası

Süreler yaklaşık planlama aralığıdır; bir aşamanın bitişini kabul kriterleri ve geçen testler belirler. Bir madde yalnızca gerçekten çalışıp doğrulandığında işaretlenir.

| Aşama | Konu | Durum |
|---|---|---|
| 0 | Repository incelemesi ve ürün tasarımı | Tamamlandı |
| 1 | Temel backend ve altyapı | Tamamlandı |
| 2 | Araç, uçuş ve dosya yönetimi | Tamamlandı (PR incelemesinde) |
| 3 | PX4 ve ArduPilot parser entegrasyonu | Sırada |
| 4 | Asenkron işleme ve teknik analiz | Planlandı |
| 5 | Uçuş analiz arayüzü (React) | Planlandı |
| 6 | Yapay zekâ analizi | Planlandı |
| 7 | Ekip çalışması ve karşılaştırma | Planlandı |
| 8 | Performans, güvenlik ve dağıtım | Planlandı |
| 9 | Canlı izleme hazırlığı | Sonraki sürüm |

## Aşama 0 — İnceleme ve tasarım

Çıktılar: [README](../README.md), [architecture.md](architecture.md), [requirements.md](requirements.md), [adr/](adr/), bu doküman.

## Aşama 1 — Temel backend ve altyapı

Hedef: Uygulama yerel ortamda çalışır, kullanıcılar yalnızca yetkili oldukları kaynaklara erişir, testler geçer.

- [x] Solution yapısı (`Domain`, `Application`, `Infrastructure`, `Api`, `UnitTests`, `IntegrationTests`)
- [x] Clean Architecture bağımlılık yönü + mimari testleri
- [x] PostgreSQL + EF Core, `xmin` concurrency, UTC zaman
- [x] İlk migration (Identity, Teams, TeamMembers, Projects)
- [x] Identity + bearer token; register/login/refresh/me; giriş rate limiting
- [x] Ekip ve proje yetkilendirmesi (404/403 ayrımı)
- [x] Problem Details hata yönetimi, FluentValidation
- [x] Serilog yapılandırılmış loglama, korelasyon kimliği
- [x] OpenAPI + Swagger UI (Development)
- [x] Sağlık kontrolleri (`/health/live`, `/health/ready`)
- [x] Docker Compose (PostgreSQL + API), Dockerfile; `docker compose up` sonrası `/health/ready` Healthy, kayıt/giriş/ekip/proje akışı ve yeniden başlatma sonrası token geçerliliği elle doğrulandı
- [x] Unit testleri (37) ve Testcontainers ile entegrasyon testleri (28)
- [x] GitHub Actions CI (restore, build, format, güvenlik açığı kontrolü, testler, Docker build + compose smoke test); PR #1'de GitHub üzerinde yeşil

## Aşama 2 — Araç, uçuş ve dosya yönetimi

Hedef: Bir dosya güvenli biçimde yüklenebilir, kaydı veritabanında tutulur ve işlem durumu takip edilebilir.

- [x] Yönetici için kullanıcı oluşturma, devre dışı bırakma ve etkinleştirme endpoint'leri
- [x] `Aircraft` entity, CRUD servisleri ve endpoint'leri; ekip içi tekil kimlik; etiketler; yetki testleri
- [x] `Flight` ve `FlightLogFile` entity'leri; `ProcessingStatus` durum makinesi (domain'de geçiş tablosu + tüm durum çiftleri için unit test)
- [x] `IFileStorage` arayüzü, S3 adaptörü (AWSSDK.S3), Docker Compose'a SeaweedFS (MinIO yerine, [ADR-0009](adr/0009-object-storage-server.md)); hazırlık kontrolüne depolama eklendi
- [x] Upload endpoint'i: body stream olarak okunur, SHA-256 aynı anda hesaplanır, boyut sınırı (Kestrel + uygulama, 413), içerik başlık doğrulaması (ULog sihirli baytları, DataFlash FMT başlığı), uzantı/içerik uyumu, sunucu tarafı içerik adresli depolama anahtarı
- [x] Ekip içi hash tekilleştirmesi (unique (TeamId, Sha256)), farklı ekipler için ayrı kayıt ve paylaşılan nesne; son kayıt silinince nesnenin silinmesi
- [x] Uçuş listesi: ekip/proje/araç/otopilot/durum/firmware/tarih filtreleri, arama, sıralama, sayfalama + indeksler
- [x] Entegrasyon testleri: yükleme (gerçek PX4 örneği), tekrar yükleme, başka ekibin dosyasına erişim, aşırı boyut, sahte uzantı, iptal/yeniden deneme
- [x] `docker compose up` ile uçtan uca: gerçek PX4 dosyası yüklendi, indirilen içeriğin SHA-256'sı aynı, yeniden başlatma sonrası dosya korunuyor

Ertelenenler: gerçek ArduPilot örnek dosyası (Aşama 3), uçuş listesinde bulgu sayısı ve sağlık göstergesi (Aşama 4), çoklu dosya yükleme arayüzü (Aşama 5), denetim kaydı (Aşama 7).

## Aşama 3 — Parser entegrasyonu

Hedef: Her iki format için doğrulanmış örnekler işlenir; çıkarılan metadata ve sinyal verileri testlerle doğrulanır.

- [x] Fixture'lar: pyulog örnek `.ulg` dosyaları (BSD-3) ve ArduCopter 4.6.3 SITL ile üretilmiş `.bin`
- [x] Oracle betiği (`tools/fixtures/generate_expected.py`): pyulog/pymavlink ile beklenen JSON üretimi
- [x] `ULogParser`, `DataFlashParser` (`FlightLog.LogParsing`, yalnızca BCL); oracle ile birebir eşleşme; bozuk/kesik/rastgele girdi, sınır, zaman aşımı ve iptal testleri
- [x] Metadata ve olay çıkarımı: firmware, araç tipi, donanım, mod geçişleri, arm/disarm, failsafe, hatalar, EV olayları, mesajlar, dropout'lar, parametreler ve değişiklikleri
- [x] Zaman normalizasyonu: orijinal µs korunur; GPS zamanından UTC açılış/başlangıç (artık saniye dahil), oracle ile doğrulandı
- [x] Ortak sinyal modeli v1 (`attitude`, `gps`, `battery`) ve kaynak/dönüşüm kaydı; birim ve çarpan tablosu logdan
- [x] Sinyal blok depolama (FLS1, ADR-0005) ve katalog tabloları; min/max downsampling
- [x] Worker projesi (DB polling, compare-and-set ile iş alma, iptal, kapanışta kuyruğa dönüş, takılı iş kurtarma) ve işleme geçmişi (`LogProcessingJob`)
- [x] API: sinyaller, sinyal verisi, olaylar, parametreler, işleme denemeleri
- [x] Entegrasyon testleri: PX4 ve ArduPilot uçtan uca, bozuk dosya, iptal, kesinti, uçuş silme, ekip izolasyonu
- [x] Docker Compose'da `worker` servisi ve CI'da gerçek log yükleyip işlenmesini bekleyen smoke test

Ertelenenler: Parquet ile ölçümlü karşılaştırma (Aşama 8), titreşim ve barometrik irtifanın normalizasyonu (anlamları formatlar arasında farklı; ham sinyal olarak mevcut), RabbitMQ ile kuyruk (Aşama 4).

## Aşama 4 — Asenkron işleme ve teknik analiz

Hedef: Loglar kuyruk üzerinden işlenir ve analiz edilir; ilk kural tabanlı bulgular kanıtlarıyla (zaman aralığı, ölçülen değer, eşik ve kaynağı) üretilir.

- [x] `FlightLog.Contracts`: sürümlü mesajlar (`process-log-file.v1`, `analyze-flight.v1`)
- [x] Transactional outbox (domain olayı → EF interceptor → `OutboxMessages`), `OutboxPublisher` (SKIP LOCKED, publisher confirm)
- [x] RabbitMQ topolojisi, DLQ, tüketici (sonuç yazıldıktan sonra ack); veritabanı taşıması yedek mod olarak korundu
- [x] Idempotent iş alma (compare-and-set); tekrar gelen mesajlar etkisiz (testli)
- [x] Geçici hata sınıflandırması, en fazla 3 otomatik deneme (üstel bekleme), tükenince DLQ ve `Failed`
- [x] Bakım: takılı işleri kurtarma, kaybolan mesajları yeniden duyurma, outbox temizliği
- [x] `AnalysisRun`, `AnalysisRuleResult`, `AnalysisFinding` modeli ve migration; işleme sonrası otomatik analiz, elle yeniden analiz
- [x] Kural motoru `rules-1`: 13 kural (GPS fix kaybı ve kalite, EKF innovation ve mesajları, titreşim ve clipping, batarya voltajı ve kapasitesi, failsafe, hatalar, mod geçişleri, uçuşta parametre değişikliği, veri boşlukları); uygulanamayan kural gerekçesiyle raporlanır
- [x] Bulgu birleştirme (örtüşen aralıklar, tekrar sayısı), güven ve veri kapsamı
- [x] API: analiz run'ları, bulgular (filtre, sayfalama), kural listesi, yönetici işleme istatistikleri; uçuş listesinde son analiz özeti
- [x] İşleme metrikleri (`FlightLog.Processing` meter)
- [x] Testler: kural unit testleri, gerçek loglarla analiz, gerçek RabbitMQ ile uçtan uca, DLQ ve tekrar teslim, geçici hata denemesi
- [x] Compose'a RabbitMQ; smoke test analiz sonucunu da kontrol ediyor

Ertelenenler: IMU tutarsızlığı, sensörler arası tutarsızlık, gecikme ve akım anomalisi kuralları (belgelenmiş eşik ve gerçek uçuş verisiyle doğrulama gerekiyor); bulgu kanıtı (`AnalysisEvidence`) ve inceleme durumu (Aşama 7, bulgu iş akışıyla birlikte); metriklerin OpenTelemetry ile dışa aktarımı (Aşama 8); PX4 titreşim metriği için eşik (belgelenmiş bir sınır yok).

## Aşama 5 — Arayüz

React + TypeScript (Vite), UI kütüphanesi seçimi (lisans kontrolüyle), uçuş listesi, uçuş detay, zaman serisi grafikleri (büyük veri için canvas tabanlı kütüphane), olay işaretleme, SignalR durum güncellemeleri.

## Aşama 6 — AI analizi

ADR-0008 kesinleştirme, Claude adaptörü, bağlam hazırlama, şema doğrulama, kanıt referans doğrulaması, maliyet/gecikme kaydı, hata ve kota yönetimi.

## Aşama 7 — Ekip çalışması ve karşılaştırma

Yorumlar, bulgu atama, inceleme durumları, audit log, karşılaştırma, CSV/JSON raporlar.

## Aşama 8 — Performans, güvenlik, dağıtım

Benchmark'lar, OpenTelemetry, güvenlik incelemesi, imaj optimizasyonu, sunucu dağıtımı, yedekleme/geri yükleme testi, kullanıcı dokümantasyonu.

## Riskler ve bağımlılıklar

| Risk | Etki | Azaltma |
|---|---|---|
| Native parser'ların format uç durumlarını kaçırması | Yanlış/eksik veri | Oracle testleri (pyulog/pymavlink), bozuk dosya testleri, geri dönüş planı olarak Python süreci (ADR-0004) |
| Lisansı uygun gerçek ArduPilot test logu bulunamaması | Parser testi zayıf kalır | SITL ile log üretimi; kullanıcıdan örnek dosya |
| Büyük dosyalarda bellek tüketimi | Worker çökmesi | Stream okuma, blok yazımı, bellek sınırı, benchmark |
| Zaman damgası/GPS zamanı yorumlama hataları | Yanlış mutlak zaman | Orijinal µs saklama, artık saniye testleri, mutlak zaman bilinmiyorsa `null` |
| LLM'in desteksiz iddia üretmesi | Yanıltıcı rapor | Şemalı çıktı, kanıt referans doğrulaması, görsel ayrım |
| Kapsamın büyüklüğü (18 ekran, 11 modül) | Gecikme | Aşamalı teslim; her aşama sonunda çalışan ürün |
| Bearer token'ların çoklu örnekte Data Protection anahtarı paylaşımı | Oturum geçersizliği | Anahtarların PostgreSQL'de saklanması (Aşama 8 öncesi) |
