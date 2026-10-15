# FlightLog Intelligence

PX4 ve ArduPilot tabanlı İHA'ların uçuş loglarını yüklemek, arşivlemek, incelemek, karşılaştırmak ve yapay zekâ destekli analiz etmek için ekip odaklı bir uçuş verisi analiz platformu.

> Bu proje bir uçuş kontrol sistemi değildir: araca komut göndermez, uçuş sonrası inceleme ve analiz içindir.

## Durum

Proje aşamalı geliştiriliyor ([yol haritası](docs/roadmap.md)). Şu an çalışan kısım **Aşama 1–4: temel backend, araç/uçuş yönetimi, log yükleme, PX4/ArduPilot log işleme, RabbitMQ ile asenkron işleme ve kural tabanlı teknik analiz**:

| Özellik | Durum |
|---|---|
| Kullanıcı kaydı, giriş, token yenileme, profil | Çalışıyor, testli |
| Ekip oluşturma, üye ekleme/çıkarma, rol yönetimi | Çalışıyor, testli |
| Ekip içinde proje CRUD (sayfalama, arama, optimistic concurrency) | Çalışıyor, testli |
| Ekip bazlı veri izolasyonu (başka ekibin kaynağına 404) | Entegrasyon testleriyle doğrulandı |
| Problem Details hata yanıtları, Serilog, korelasyon kimliği, sağlık kontrolleri | Çalışıyor |
| Yönetici kullanıcı yönetimi (oluştur, devre dışı bırak, etkinleştir) | Çalışıyor, testli |
| Araç (filo) CRUD: etiketler, ekip içi tekil kimlik, optimistic concurrency | Çalışıyor, testli |
| Log yükleme: stream SHA-256, boyut sınırı, içerikten format algılama (.ulg / .bin), ekip içi tekrar tespiti | Çalışıyor, testli (gerçek PX4 örneği ile) |
| Uçuş listesi: filtre, arama, sıralama, sayfalama; uçuş düzenleme/silme; orijinal dosyayı indirme | Çalışıyor, testli |
| PX4 ULog / ArduPilot DataFlash parser (native C#) | Çalışıyor; gerçek loglarda pyulog/pymavlink ile birebir doğrulandı |
| Log işleme worker'ı: uçuş başlangıcı (UTC), süre, firmware, araç tipi, olaylar, parametreler, sinyaller | Çalışıyor, testli; iptal, yeniden deneme ve işleme geçmişi dahil |
| Ortak sinyaller v1 (`attitude`, `gps`, `battery`) ve ham log alanları; zaman aralığı ve downsampling ile okuma | Çalışıyor, testli |
| RabbitMQ ile kuyruk: transactional outbox, dead-letter kuyruğu, idempotent tüketici, geçici hatalarda otomatik deneme | Çalışıyor; gerçek RabbitMQ ile test edildi |
| Kural tabanlı teknik analiz: 13 kural (GPS, EKF, titreşim, batarya, failsafe, hatalar, mod geçişleri, parametre değişiklikleri, veri boşlukları); bulgularda zaman aralığı, ölçülen değer, eşik ve eşiğin kaynağı | Çalışıyor; gerçek PX4 ve ArduPilot loglarıyla testli |
| Yönetici için işleme istatistikleri ve işleme metrikleri | Çalışıyor, testli |
| React arayüzü | Aşama 5 |
| Yapay zekâ analizi | Aşama 6 |

Henüz yapılmamış özellikler çalışıyormuş gibi gösterilmez.

## Teknoloji

.NET 10 (LTS) · ASP.NET Core Web API · EF Core 10 + PostgreSQL 17 · ASP.NET Core Identity (bearer token) · S3 uyumlu nesne depolama (SeaweedFS, AWSSDK.S3) · FluentValidation · Serilog · OpenAPI/Swagger UI · xUnit v3 · Testcontainers · Docker Compose · GitHub Actions.

Kararlar ve gerekçeler: [docs/adr](docs/adr/). Mimari: [docs/architecture.md](docs/architecture.md). Gereksinimler: [docs/requirements.md](docs/requirements.md).

## Hızlı başlangıç (Docker)

Gereksinim: Docker ve Docker Compose v2.

```bash
cp .env.example .env        # parolaları değiştirin
docker compose up --build -d
curl http://127.0.0.1:8080/health/ready   # "Healthy"
```

- Swagger UI: http://127.0.0.1:8080/swagger (Development ortamında)
- OpenAPI belgesi: http://127.0.0.1:8080/openapi/v1.json
- İlk yönetici: `.env` içindeki `ADMIN_EMAIL` / `ADMIN_PASSWORD`

Örnek akış:

```bash
B=http://127.0.0.1:8080
curl -s -H 'content-type: application/json' \
  -d '{"email":"pilot@example.com","password":"Pilot-Password-1","displayName":"Pilot"}' $B/api/v1/auth/register
TOKEN=$(curl -s -H 'content-type: application/json' \
  -d '{"email":"pilot@example.com","password":"Pilot-Password-1"}' $B/api/v1/auth/login | jq -r .accessToken)
curl -s -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"name":"Flight Test"}' $B/api/v1/teams
# Proje oluşturduktan sonra log yükleme (.ulg veya .bin):
curl -s -H "authorization: Bearer $TOKEN" -F "file=@00000042.ulg" $B/api/v1/projects/<projectId>/logs
# İşlendikten sonra:
curl -s -H "authorization: Bearer $TOKEN" "$B/api/v1/flights/<flightId>/signals?kind=Normalized"
curl -s -H "authorization: Bearer $TOKEN" "$B/api/v1/flights/<flightId>/signals/data?names=attitude.roll,battery.voltage&maxPoints=2000"
curl -s -H "authorization: Bearer $TOKEN" "$B/api/v1/flights/<flightId>/events?kind=ModeChange"
# Analiz sonucu ve bulgular:
curl -s -H "authorization: Bearer $TOKEN" "$B/api/v1/flights/<flightId>/analysis-runs"
curl -s -H "authorization: Bearer $TOKEN" "$B/api/v1/analysis-runs/<runId>/findings?minSeverity=Warning"
```

Docker Compose beş servis başlatır: `postgres`, `storage` (SeaweedFS, S3 API `127.0.0.1:8333`), `rabbitmq` (yönetim arayüzü `127.0.0.1:15672`), `api` ve yüklenen logları işleyip analiz eden `worker`. Yükledikten birkaç saniye sonra dosya `Completed` olur; uçtan uca deneme için `scripts/smoke-test.sh`. Yükleme sınırı `.env` içindeki `UPLOAD_MAX_BYTES` ile ayarlanır (varsayılan 512 MiB).

### Windows + Docker Desktop

PowerShell'de repo klasöründe:

```powershell
Copy-Item .env.example .env      # parolaları değiştirin
docker compose up --build -d
Invoke-RestMethod http://127.0.0.1:8080/health/ready   # "Healthy"
```

Durdurmak için `docker compose down`, veritabanını da silmek için `docker compose down -v`.

### Visual Studio

`FlightLog.slnx` dosyasını açın (.NET 10 SDK ve onu destekleyen bir Visual Studio sürümü, ör. Visual Studio 2026, gerekir).
API'yi Visual Studio'dan çalıştırmak için önce veritabanını ve depolamayı başlatın: `docker compose up -d postgres storage`, ardından `FlightLog.Api` projesini başlangıç projesi yapıp F5. Development ortamında API yüklenen logları kendi içinde işler (`Processing:Enabled`); ayrı worker gerekmez. Swagger UI: http://localhost:5177/swagger.
`appsettings.Development.json` içindeki bağlantı dizesi `.env.example` varsayılanlarıyla eşleşir; `.env` içinde `POSTGRES_PASSWORD`'u değiştirdiyseniz aynı değeri User Secrets ile verin (`dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=flightlog;Username=flightlog;Password=..." --project src/FlightLog.Api`).
Testler Test Explorer'da görünür; entegrasyon testleri için Docker Desktop açık olmalıdır.

## Yerel geliştirme (Docker'sız API)

Gereksinim: .NET SDK 10.0.1xx, çalışan PostgreSQL ve S3 uyumlu depolama (ör. `docker compose up -d postgres storage`).

```bash
dotnet tool restore
dotnet run --project src/FlightLog.Api   # Development ayarları: appsettings.Development.json
```

Development ortamı açılışta migration'ları uygular ve kendi kendine kaydı açar. Üretimde ikisi de kapalıdır.

Yeni migration:

```bash
dotnet ef migrations add <Ad> -p src/FlightLog.Infrastructure -s src/FlightLog.Api -o Persistence/Migrations
```

## Testler

```bash
dotnet test --project tests/FlightLog.UnitTests          # Docker gerekmez
dotnet test --project tests/FlightLog.ParserTests        # Docker gerekmez; gerçek loglar, oracle karşılaştırması
dotnet test --project tests/FlightLog.IntegrationTests   # Docker gerekir (Testcontainers: PostgreSQL + SeaweedFS + RabbitMQ)
```

Ayrıntı: [docs/testing.md](docs/testing.md).

## Proje yapısı

```
src/
  FlightLog.Domain          İş kuralları, entity'ler (bağımlılıksız)
  FlightLog.Contracts       Kuyruk mesajları (sürümlü)
  FlightLog.LogParsing      ULog/DataFlash parser'ları, yorumlayıcılar, normalizasyon, sinyal blok formatı (yalnızca BCL)
  FlightLog.Application     Use case servisleri, DTO'lar, doğrulama, yetki kontrolleri, log işleme, analiz kuralları
  FlightLog.Infrastructure  EF Core, PostgreSQL, Identity, migration'lar, S3 depolama, outbox ve RabbitMQ, worker döngüsü
  FlightLog.Api             HTTP API, kimlik doğrulama, hata yönetimi, OpenAPI
  FlightLog.Worker          Log işleme ve analiz servisi (Docker'da ayrı konteyner)
tests/
  FlightLog.UnitTests        Domain, doğrulama, mimari katman testleri
  FlightLog.ParserTests      Parser'lar ve normalizasyon, pyulog/pymavlink oracle'ına karşı
  FlightLog.IntegrationTests Gerçek API + PostgreSQL + SeaweedFS + RabbitMQ (Testcontainers), uçtan uca işleme ve analiz
  fixtures/                  Gerçek PX4 logları (pyulog, BSD-3) ve kendi ürettiğimiz ArduPilot SITL logu
tools/fixtures/              Oracle verisini üreten Python betiği (yalnızca geliştirme)
scripts/smoke-test.sh        Çalışan Compose ortamında log yükleyip işlenmesini ve analizini bekler
docs/                        Mimari, gereksinimler, ADR'ler, yol haritası, dağıtım
```

## API özeti (v1)

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/api/v1/auth/register` | Hesap oluşturur (yapılandırmayla kapatılabilir) |
| POST | `/api/v1/auth/login` | Access + refresh token |
| POST | `/api/v1/auth/refresh` | Token yeniler |
| GET | `/api/v1/auth/me` | Profil, roller, ekip üyelikleri |
| GET/POST | `/api/v1/teams` | Ekiplerim / ekip oluştur |
| GET | `/api/v1/teams/{id}` | Ekip ayrıntısı |
| GET/POST | `/api/v1/teams/{id}/members` | Üyeler / üye ekle (Project Manager) |
| PUT/DELETE | `/api/v1/teams/{id}/members/{userId}` | Rol değiştir / üye çıkar |
| GET/POST | `/api/v1/teams/{id}/projects` | Projeler (`search`, `page`, `pageSize`) / proje oluştur |
| GET/PUT/DELETE | `/api/v1/projects/{id}` | Proje oku / güncelle (`version` gerekli) / sil |
| GET/POST | `/api/v1/admin/users` | Kullanıcılar / kullanıcı oluştur (Administrator) |
| POST | `/api/v1/admin/users/{id}/disable`, `/enable` | Girişi ve token yenilemeyi kapatır / açar |
| GET/POST | `/api/v1/teams/{id}/aircraft` | Araçlar (`search`, `autopilot`) / araç ekle (Engineer) |
| GET/PUT/DELETE | `/api/v1/aircraft/{id}` | Araç oku / güncelle (`version`) / sil (Project Manager; uçuşlar kalır) |
| POST | `/api/v1/projects/{id}/logs?aircraftId=&name=` | Log yükle (multipart, alan `file`); uçuş oluşturur, 201 |
| GET | `/api/v1/flights` | Uçuş listesi (`teamId`, `projectId`, `aircraftId`, `autopilot`, `status`, `firmware`, `from`, `to`, `search`, `sort`, `page`, `pageSize`) |
| GET/PUT/DELETE | `/api/v1/flights/{id}` | Uçuş ayrıntısı / ad, not, araç güncelle (`version`) / sil (Project Manager) |
| GET | `/api/v1/log-files/{id}`, `/api/v1/log-files/{id}/content` | Dosya durumu / orijinal dosyayı indir |
| POST | `/api/v1/log-files/{id}/cancel`, `/retry` | İşlemeyi iptal et / yeniden kuyruğa al |
| GET | `/api/v1/log-files/{id}/jobs` | İşleme denemeleri: süre, bellek, parser istatistikleri, hata kodu |
| GET | `/api/v1/flights/{id}/signals` | Sinyal kataloğu (`kind=Raw|Normalized`, `search`): birim, kaynak, örnek sayısı, min/maks |
| GET | `/api/v1/flights/{id}/signals/data` | En fazla 8 sinyalin değerleri (`names`, `fromUs`, `toUs`, `maxPoints`); uzun aralıklar min/maks korunarak azaltılır |
| GET | `/api/v1/flights/{id}/events` | Mesajlar, mod geçişleri, arm/disarm, failsafe, hatalar (`kind`, `maxSeverity`, `fromUs`, `toUs`, `search`, sayfalama) |
| GET | `/api/v1/flights/{id}/parameters` | Başlangıç değerleri, varsayılanlar ve uçuştaki değişiklikler (`search`, `changedOnly`) |
| POST/GET | `/api/v1/flights/{id}/analysis-runs` | Yeniden analiz iste (Engineer, 202) / analiz geçmişi |
| GET | `/api/v1/analysis-runs/{id}` | Analiz durumu, sayılar ve her kuralın sonucu (uygulandı / uygulanamadı ve neden / hata) |
| GET | `/api/v1/analysis-runs/{id}/findings` | Bulgular (`minSeverity`, `category`, `ruleId`, sayfalama): zaman aralığı, ölçülen değerler, eşik ve kaynağı, güven |
| GET | `/api/v1/analysis-rules` | Kurallar: ne kontrol ettikleri, gereken veri, uygulanamadıkları durumlar |
| GET | `/api/v1/admin/processing/stats?hours=` | İşleme istatistikleri (Administrator) |
| GET | `/health/live`, `/health/ready` | Sağlık kontrolleri (hazırlık: veritabanı, depolama, RabbitMQ) |

Hatalar RFC 9457 Problem Details biçimindedir ve sabit bir `code` alanı içerir (ör. `not_found`, `forbidden`, `project.version_mismatch`, `team.last_project_manager`, `log_file.duplicate`, `log_file.extension_mismatch`, `upload.too_large`, `analysis_run.already_pending`).

## Dağıtım

Sunucu kurulumu, migration, gizli bilgi yönetimi, yedekleme ve log saklama: [docs/deployment.md](docs/deployment.md).

## Bilinen eksikler

- Analiz kuralları ilk sürümde 13 kuraldır; IMU ve sensörler arası tutarsızlık, gecikme ve akım anomalisi kuralları belgelenmiş eşik ve gerçek uçuş verisi olmadan eklenmedi. PX4'ün titreşim metriği için belgelenmiş bir sınır olmadığından titreşim seviyesi kuralı yalnızca ArduPilot'ta uygulanır.
- Bulgu inceleme durumu, atama ve kanıt bağlantıları Aşama 7'de (bulgu iş akışı).
- GPS UTC zamanı olmayan loglarda uçuşun başlangıç tarihi bilinemez ve boş kalır (tahmin edilmez); liste tarihi yükleme zamanıdır.
- Normalize sinyaller v1 yalnızca tutum, GPS ve batarya içerir; titreşim ve barometrik irtifa formatlar arasında farklı anlam taşıdığı için şimdilik ham sinyal olarak okunur ([mimari](docs/architecture.md#51-sinyaller-ve-normalizasyon-v1)).
- Sinyal blok formatı ile Parquet'in ölçümlü karşılaştırması Aşama 8'e ertelendi ([ADR-0005](docs/adr/0005-time-series-storage.md)).
- Tek istekte tek dosya yüklenir; çoklu yükleme istemci tarafında (Aşama 5) ardışık isteklerle yapılacak.
- Devre dışı bırakılan kullanıcının daha önce aldığı access token süresi dolana kadar (`Auth:AccessTokenMinutes`) geçerli kalır; refresh ve giriş hemen engellenir.
- Yönetici davet akışı ve e-posta doğrulama yok.
- Denetim kaydı (audit log) Aşama 7'de eklenecek.
- OpenTelemetry Aşama 8'de eklenecek; şu an yapılandırılmış loglar, korelasyon kimliği, `FlightLog.Processing` metrikleri (dışa aktarım yok) ve yönetici istatistik endpoint'i var.
