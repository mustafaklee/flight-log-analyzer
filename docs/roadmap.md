# Yol haritası

Süreler yaklaşık planlama aralığıdır; bir aşamanın bitişini kabul kriterleri ve geçen testler belirler. Bir madde yalnızca gerçekten çalışıp doğrulandığında işaretlenir.

| Aşama | Konu | Durum |
|---|---|---|
| 0 | Repository incelemesi ve ürün tasarımı | Tamamlandı |
| 1 | Temel backend ve altyapı | Tamamlandı; CI'ın GitHub'da ilk çalışması bekleniyor |
| 2 | Araç, uçuş ve dosya yönetimi | Sırada |
| 3 | PX4 ve ArduPilot parser entegrasyonu | Planlandı |
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
- [ ] GitHub Actions CI (restore, build, format, güvenlik açığı kontrolü, testler, Docker build + compose smoke test) — workflow yazıldı, adımlar yerelde çalıştırıldı; GitHub'da ilk çalıştırma push sonrası doğrulanacak

## Aşama 2 — Araç, uçuş ve dosya yönetimi (sıradaki sprint)

Başlangıç noktası ve backlog:

0. Yönetici için kullanıcı oluşturma/devre dışı bırakma endpoint'leri (kendi kendine kayıt kapalıyken hesap açmanın yolu).
1. `Aircraft` entity, CRUD servisleri ve endpoint'leri, yetki testleri.
2. `Flight` ve `FlightLogFile` entity'leri; `ProcessingStatus` durum makinesi (domain'de geçiş tablosu + unit testler).
3. `IFileStorage` arayüzü; MinIO adaptörü (AWSSDK.S3 veya Minio paketi lisans/uyumluluk kontrolüyle), Docker Compose'a MinIO.
4. Upload endpoint'i: stream olarak SHA-256 hesaplama, boyut sınırı (Kestrel + uygulama), içerik başlık doğrulaması (sihirli baytlar), sunucu tarafı depolama anahtarı.
5. Ekip içi hash tekilleştirmesi (unique (TeamId, Sha256)), farklı ekipler için ayrı kayıt.
6. Uçuş listesi: filtre, arama, sıralama, sayfalama + indeksler.
7. Entegrasyon testleri: yükleme, tekrar yükleme, başka ekibin dosyasına erişim, aşırı boyut, sahte uzantı.

Kabul kriteri: Bir dosya güvenli biçimde yüklenebilir, kaydı veritabanında tutulur ve işlem durumu takip edilebilir.

## Aşama 3 — Parser entegrasyonu

- Fixture toplama: pyulog örnek `.ulg` dosyaları (BSD-3), lisansı uygun ArduPilot `.bin` dosyaları veya SITL ile üretilmiş loglar.
- Oracle betiği (`tools/fixtures/`): pyulog/pymavlink ile beklenen JSON üretimi.
- `UlogParser`, `DataFlashParser`, format algılayıcı; bozuk/kesik/bilinmeyen mesaj testleri.
- Ortak sinyal modeli, birim tablosu, zaman normalizasyonu; sinyal blok depolama (ADR-0005) ve Parquet karşılaştırması.
- Worker projesi (ilk sürüm: kuyruk yerine DB polling ile, Aşama 4'te RabbitMQ).

## Aşama 4 — Asenkron işleme ve teknik analiz

RabbitMQ + outbox, retry, DLQ, idempotency testleri; ilk kurallar (GPS fix kaybı, EKF uyarıları, titreşim, batarya, veri boşlukları, failsafe), bulgu birleştirme, işleme metrikleri.

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
