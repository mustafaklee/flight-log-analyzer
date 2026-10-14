# FlightLog Intelligence

PX4 ve ArduPilot tabanlı İHA'ların uçuş loglarını yüklemek, arşivlemek, incelemek, karşılaştırmak ve yapay zekâ destekli analiz etmek için ekip odaklı bir uçuş verisi analiz platformu.

> Bu proje bir uçuş kontrol sistemi değildir: araca komut göndermez, uçuş sonrası inceleme ve analiz içindir.

## Durum

Proje aşamalı geliştiriliyor ([yol haritası](docs/roadmap.md)). Şu an çalışan kısım **Aşama 1 — temel backend**:

| Özellik | Durum |
|---|---|
| Kullanıcı kaydı, giriş, token yenileme, profil | Çalışıyor, testli |
| Ekip oluşturma, üye ekleme/çıkarma, rol yönetimi | Çalışıyor, testli |
| Ekip içinde proje CRUD (sayfalama, arama, optimistic concurrency) | Çalışıyor, testli |
| Ekip bazlı veri izolasyonu (başka ekibin kaynağına 404) | Entegrasyon testleriyle doğrulandı |
| Problem Details hata yanıtları, Serilog, korelasyon kimliği, sağlık kontrolleri | Çalışıyor |
| Araç/uçuş yönetimi, log yükleme | Aşama 2 |
| PX4 ULog / ArduPilot DataFlash parser | Aşama 3 |
| Asenkron işleme (RabbitMQ), kural tabanlı analiz | Aşama 4 |
| React arayüzü | Aşama 5 |
| Yapay zekâ analizi | Aşama 6 |

Henüz yapılmamış özellikler çalışıyormuş gibi gösterilmez.

## Teknoloji

.NET 10 (LTS) · ASP.NET Core Web API · EF Core 10 + PostgreSQL 17 · ASP.NET Core Identity (bearer token) · FluentValidation · Serilog · OpenAPI/Swagger UI · xUnit v3 · Testcontainers · Docker Compose · GitHub Actions.

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
```

## Yerel geliştirme (Docker'sız API)

Gereksinim: .NET SDK 10.0.1xx, çalışan bir PostgreSQL (ör. `docker compose up -d postgres`).

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
dotnet test --project tests/FlightLog.IntegrationTests   # Docker gerekir (Testcontainers PostgreSQL)
```

Ayrıntı: [docs/testing.md](docs/testing.md).

## Proje yapısı

```
src/
  FlightLog.Domain          İş kuralları, entity'ler (bağımlılıksız)
  FlightLog.Application     Use case servisleri, DTO'lar, doğrulama, yetki kontrolleri
  FlightLog.Infrastructure  EF Core, PostgreSQL, Identity, migration'lar
  FlightLog.Api             HTTP API, kimlik doğrulama, hata yönetimi, OpenAPI
tests/
  FlightLog.UnitTests        Domain, doğrulama, mimari katman testleri
  FlightLog.IntegrationTests Gerçek API + PostgreSQL (Testcontainers)
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
| GET | `/health/live`, `/health/ready` | Sağlık kontrolleri |

Hatalar RFC 9457 Problem Details biçimindedir ve sabit bir `code` alanı içerir (ör. `not_found`, `forbidden`, `project.version_mismatch`, `team.last_project_manager`).

## Dağıtım

Sunucu kurulumu, migration, gizli bilgi yönetimi, yedekleme ve log saklama: [docs/deployment.md](docs/deployment.md).

## Bilinen eksikler

- Yönetici davet akışı ve e-posta doğrulama yok; kendi kendine kayıt kapalıyken hesaplar şimdilik yalnızca bootstrap yöneticisiyle oluşur (Aşama 2'de yönetici kullanıcı oluşturma endpoint'i).
- Denetim kaydı (audit log) Aşama 7'de eklenecek.
- OpenTelemetry Aşama 8'de eklenecek; şu an yapılandırılmış loglar ve korelasyon kimliği var.
