# FlightLog Intelligence

PX4 ve ArduPilot tabanlı İHA'ların uçuş loglarını yüklemek, arşivlemek, incelemek, karşılaştırmak ve yapay zekâ destekli analiz etmek için ekip odaklı bir uçuş verisi analiz platformu.

> Bu proje bir uçuş kontrol sistemi değildir: araca komut göndermez, uçuş sonrası inceleme ve analiz içindir.

## Durum

Proje aşamalı geliştiriliyor ([yol haritası](docs/roadmap.md)). Şu an çalışan kısım **Aşama 1–2: temel backend, araç/uçuş yönetimi ve log yükleme**:

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
| İşleme durumu (Queued, iptal, yeniden deneme) | Durum makinesi çalışıyor; dosyayı işleyen worker Aşama 3–4 |
| PX4 ULog / ArduPilot DataFlash parser | Aşama 3 |
| Asenkron işleme (RabbitMQ), kural tabanlı analiz | Aşama 4 |
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
```

Docker Compose üç servis başlatır: `postgres`, `storage` (SeaweedFS, S3 API `127.0.0.1:8333`) ve `api`. Yükleme sınırı `.env` içindeki `UPLOAD_MAX_BYTES` ile ayarlanır (varsayılan 512 MiB).

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
API'yi Visual Studio'dan çalıştırmak için önce veritabanını ve depolamayı başlatın: `docker compose up -d postgres storage`, ardından `FlightLog.Api` projesini başlangıç projesi yapıp F5. Swagger UI: http://localhost:5177/swagger.
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
dotnet test --project tests/FlightLog.IntegrationTests   # Docker gerekir (Testcontainers: PostgreSQL + SeaweedFS)
```

Ayrıntı: [docs/testing.md](docs/testing.md).

## Proje yapısı

```
src/
  FlightLog.Domain          İş kuralları, entity'ler (bağımlılıksız)
  FlightLog.Application     Use case servisleri, DTO'lar, doğrulama, yetki kontrolleri
  FlightLog.Infrastructure  EF Core, PostgreSQL, Identity, migration'lar, S3 depolama
  FlightLog.Api             HTTP API, kimlik doğrulama, hata yönetimi, OpenAPI
tests/
  FlightLog.UnitTests        Domain, doğrulama, mimari katman testleri
  FlightLog.IntegrationTests Gerçek API + PostgreSQL + SeaweedFS (Testcontainers)
  fixtures/                  Lisanslı örnek log dosyaları (pyulog, BSD-3-Clause)
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
| GET | `/health/live`, `/health/ready` | Sağlık kontrolleri (hazırlık: veritabanı + depolama) |

Hatalar RFC 9457 Problem Details biçimindedir ve sabit bir `code` alanı içerir (ör. `not_found`, `forbidden`, `project.version_mismatch`, `team.last_project_manager`, `log_file.duplicate`, `log_file.extension_mismatch`, `upload.too_large`).

## Dağıtım

Sunucu kurulumu, migration, gizli bilgi yönetimi, yedekleme ve log saklama: [docs/deployment.md](docs/deployment.md).

## Bilinen eksikler

- Yüklenen loglar `Queued` durumunda bekler: dosyayı ayrıştıran worker Aşama 3'te, RabbitMQ ile asenkron işleme Aşama 4'te gelir. Uçuş başlangıç zamanı, süre ve firmware bu yüzden şimdilik boş (`null`) görünür.
- ArduPilot `.bin` için gerçek örnek dosya henüz repoda yok; format algılama sentetik başlıkla test edildi. Gerçek DataFlash örnekleri parser ile birlikte (Aşama 3) eklenecek.
- Tek istekte tek dosya yüklenir; çoklu yükleme istemci tarafında (Aşama 5) ardışık isteklerle yapılacak.
- Devre dışı bırakılan kullanıcının daha önce aldığı access token süresi dolana kadar (`Auth:AccessTokenMinutes`) geçerli kalır; refresh ve giriş hemen engellenir.
- Yönetici davet akışı ve e-posta doğrulama yok.
- Denetim kaydı (audit log) Aşama 7'de eklenecek.
- OpenTelemetry Aşama 8'de eklenecek; şu an yapılandırılmış loglar ve korelasyon kimliği var.
