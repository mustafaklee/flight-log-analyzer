# Dağıtım

Bu doküman Aşama 1 itibarıyla geçerli durumu anlatır; Aşama 8'de sunucu dağıtımı, yedekleme ve geri yükleme adımları gerçek bir ortamda doğrulanarak genişletilecek.

## Hedef ortam (varsayım)

Tek Linux VM üzerinde Docker Compose; önünde TLS sonlandıran bir reverse proxy (ör. Caddy veya Nginx). API konteyneri yalnızca `127.0.0.1` üzerinden yayınlanır.

## Yapılandırma ve gizli bilgiler

Tüm ayarlar ortam değişkeniyle verilir (`__` iç içe anahtar ayırıcıdır). Repository'de gizli bilgi bulunmaz; `.env` git dışındadır.

| Değişken | Açıklama |
|---|---|
| `ConnectionStrings__Postgres` | PostgreSQL bağlantısı |
| `Database__MigrateOnStartup` | Açılışta migration uygulansın mı (üretimde `false` önerilir) |
| `Auth__AllowSelfRegistration` | Kendi kendine kayıt (üretimde `false`) |
| `Auth__AccessTokenMinutes`, `Auth__RefreshTokenDays` | Token süreleri |
| `Auth__RateLimitPerMinute` | Kimlik doğrulama endpoint'lerinde IP başına dakikalık istek sınırı |
| `Auth__BootstrapAdministrator__Email` / `__Password` | İlk yönetici; hesap varsa değiştirilmez. İlk açılıştan sonra parolayı değiştirip değişkeni kaldırın |
| `ASPNETCORE_ENVIRONMENT` | `Production` ortamında Swagger UI kapalıdır |

Data Protection anahtarları (bearer token şifreleme) PostgreSQL'deki `DataProtectionKeys` tablosunda saklanır; böylece token'lar yeniden başlatmada ve birden fazla API örneğinde geçerli kalır. Bu anahtarlar şu an şifrelenmeden saklanır (log uyarısı bunu belirtir); üretimde bir sertifika veya KMS ile `ProtectKeysWith...` yapılandırması Aşama 8'de eklenecek.

## Migration'lar

Üretimde migration'ı uygulama açılışından ayrı, kontrollü bir adım olarak çalıştırın:

```bash
dotnet ef migrations bundle -p src/FlightLog.Infrastructure -s src/FlightLog.Api -o efbundle --self-contained -r linux-x64
./efbundle --connection "Host=...;Database=flightlog;Username=...;Password=..."
```

Migration öncesi mutlaka yedek alın.

## Yedekleme ve geri yükleme (PostgreSQL)

```bash
# Yedek
docker compose exec -T postgres pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc > flightlog-$(date +%F).dump
# Geri yükleme (boş veritabanına)
docker compose exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists < flightlog-YYYY-MM-DD.dump
```

Nesne depolama (ham loglar) Aşama 2'de eklendiğinde yedekleme planı buna göre genişletilecek. Geri yükleme testi Aşama 8 kabul kriteridir; henüz doğrulanmadı.

## Loglar

API JSON (Compact) formatında stdout'a log yazar. Docker log sürücüsünde boyut sınırı ayarlayın (ör. `max-size: 50m`, `max-file: 5`) veya merkezi bir log sistemine yönlendirin. Loglara parola, token veya API anahtarı yazılmaz.

## Sağlık kontrolleri

- `GET /health/live`: süreç ayakta mı.
- `GET /health/ready`: PostgreSQL erişilebilir mi. Reverse proxy ve orkestratör hazır olma kontrolü için bunu kullanın.
