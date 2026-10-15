# Dağıtım

Bu doküman Aşama 5 itibarıyla geçerli durumu anlatır; Aşama 8'de sunucu dağıtımı, yedekleme ve geri yükleme adımları gerçek bir ortamda doğrulanarak genişletilecek.

## Hedef ortam (varsayım)

Tek Linux VM üzerinde Docker Compose; önünde TLS sonlandıran bir reverse proxy (ör. Caddy veya Nginx). API ve `web` konteynerleri yalnızca `127.0.0.1` üzerinden yayınlanır; reverse proxy `web` konteynerine (`127.0.0.1:8081`) yönlendirir, `web` de `/api` ve `/hubs` isteklerini API'ye iletir. Reverse proxy WebSocket yükseltmesine izin vermelidir (`/hubs/processing`); vermezse arayüz yoklamaya düşer. `worker` konteyneri dışarıya port açmaz; veritabanı ve depolamaya API ile aynı ayarlarla bağlanır. Birden çok worker çalıştırılabilir (iş alma veritabanında compare-and-set ile yapılır).

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
| `Storage__ServiceUrl` | S3 uyumlu servis adresi (Compose içinde `http://storage:8333`; AWS S3 için boş bırakılıp `Storage__Region` verilir) |
| `Storage__Bucket`, `Storage__Region`, `Storage__ForcePathStyle` | Bucket adı, bölge, path-style adresleme (SeaweedFS/MinIO için `true`) |
| `Storage__AccessKey` / `Storage__SecretKey` | Depolama kimlik bilgileri (`.env`: `STORAGE_ACCESS_KEY`, `STORAGE_SECRET_KEY`) |
| `Storage__CreateBucketIfMissing` | Açılışta bucket yoksa oluştur (yerel kurulum için `true`) |
| `Uploads__MaxFileSizeBytes` | Maksimum log dosyası boyutu (`.env`: `UPLOAD_MAX_BYTES`, varsayılan 512 MiB). Önündeki reverse proxy'nin gövde sınırı da en az bu kadar olmalı |
| `Uploads__TempDirectory` | Yükleme sırasında kullanılan geçici dizin (boşsa sistem geçici dizini); en büyük dosya kadar boş alan gerekir |
| `WEB_PORT` | Arayüzün yayınlandığı port (8081) |
| `MAP_TILE_URL`, `MAP_TILE_ATTRIBUTION` | Harita karo adresi ve atıf metni; imaj derlenirken gömülür (boşsa OpenStreetMap). OpenStreetMap'in kullanım politikası yoğun kullanıma izin vermez; üretimde kendi karo sunucunuzu kullanın |
| `MAP_TILE_ORIGIN` | Karo sunucusunun origin'i; CSP `img-src` buna izin verir (`web` konteyneri, çalışma zamanında) |
| `API_UPSTREAM` | `web` konteynerinin istekleri ileteceği API adresi (Compose'da `http://api:8080`) |
| `ASPNETCORE_ENVIRONMENT` | `Production` ortamında Swagger UI kapalıdır |
| `Processing__Enabled` | API'nin logları kendi içinde işlemesi (yalnızca geliştirme; Compose'da `false`, işleme `worker` servisinde) |
| `Processing__PollInterval`, `Processing__CancellationCheckInterval` | Kuyruk yoklama aralığı (2 s) ve çalışan işin iptal kontrolü (5 s) |
| `Processing__ParserTimeout`, `Processing__StaleAfter` | Dosya başına parse süresi sınırı (15 dk); bu süreden uzun `Processing` kalan dosya çöken worker sayılıp `Failed` olur (30 dk) |
| `Processing__TempDirectory`, `Processing__MaxParallelUploads` | Worker'ın ham dosyayı ve blokları koyduğu geçici dizin (en büyük log + blokları kadar alan); blok yükleme paralelliği (8) |
| `Messaging__Transport` | `RabbitMq` (Compose ve üretim) veya `Database` (broker'sız tek makine; worker veritabanını yoklar) |
| `RabbitMq__HostName`, `RabbitMq__Port`, `RabbitMq__VirtualHost` | Broker adresi (Compose içinde `rabbitmq:5672`, `/`) |
| `RabbitMq__UserName` / `RabbitMq__Password` | Broker kimlik bilgileri (`.env`: `RABBITMQ_USER`, `RABBITMQ_PASSWORD`) |
| `RabbitMq__PrefetchCount` | Worker başına aynı anda alınan mesaj (1; bellek kullanımını dosya başına sınırlar) |
| `Processing__MaxAutomaticRetries`, `Processing__RetryBaseDelay`, `Processing__RetryMaxDelay` | Geçici hatalarda otomatik deneme sayısı (3) ve üstel bekleme (30 s'den başlar, en fazla 10 dk) |
| `Processing__AnalysisStaleAfter` | Bu süreden uzun `Running` kalan analiz çöken worker sayılır (10 dk) |
| `Messaging__OrphanAfter`, `Messaging__OutboxRetention` | Bu süreden uzun kuyrukta bekleyen iş için mesaj yeniden yazılır (30 dk); yayınlanmış outbox satırlarının saklama süresi (7 gün) |

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

## Yedekleme (nesne depolama)

Ham loglar SeaweedFS'in `storage-data` volume'ünde tutulur ([ADR-0009](adr/0009-object-storage-server.md)). Veritabanı kayıtları nesnelere `raw/{sha256}` anahtarıyla başvurur; tutarlı bir yedek için PostgreSQL yedeğinden hemen sonra depolama yedeği alın (fazladan nesne zararsızdır, eksik nesne indirme hatasına yol açar):

```bash
docker compose stop storage
docker run --rm -v flightlog_storage-data:/data -v "$PWD":/backup alpine tar czf /backup/storage-$(date +%F).tgz -C /data .
docker compose start storage
```

Alternatif olarak herhangi bir S3 aracıyla (`aws s3 sync --endpoint-url http://127.0.0.1:8333 s3://flightlog ./yedek`) bucket kopyalanabilir. Yönetilen S3 kullanılıyorsa sürümleme ve yaşam döngüsü kuralları sağlayıcıda açılır.

Geri yükleme testi Aşama 8 kabul kriteridir; henüz doğrulanmadı.

## Loglar

API JSON (Compact) formatında stdout'a log yazar. Docker log sürücüsünde boyut sınırı ayarlayın (ör. `max-size: 50m`, `max-file: 5`) veya merkezi bir log sistemine yönlendirin. Loglara parola, token veya API anahtarı yazılmaz.

## Arayüz (`web`)

nginx (`nginxinc/nginx-unprivileged`, root olmayan kullanıcı) derlenmiş arayüzü sunar. `index.html` önbelleğe alınmaz, `/assets/` altındaki hash'li dosyalar bir yıl önbelleğe alınır. Yanıtlarda CSP, `X-Content-Type-Options`, `Referrer-Policy` ve `Permissions-Policy` başlıkları vardır. Yüklemeler `/api` üzerinden tamponlanmadan API'ye akar; boyut sınırını API uygular. Erişim loglarına sorgu dizesi yazılmaz (SignalR token'ı orada taşınır). Sağlık kontrolü `GET /healthz`.

## Sağlık kontrolleri

- `GET /health/live`: süreç ayakta mı.
- `GET /health/ready`: PostgreSQL, nesne depolama ve (RabbitMQ taşımasında) RabbitMQ erişilebilir mi. Broker geçici olarak kapalıyken API yükleme kabul etmeye devam eder; mesajlar outbox'ta bekler ve broker dönünce iletilir. Reverse proxy ve orkestratör hazır olma kontrolü için bunu kullanın.

## RabbitMQ

- Kuyruklar ve exchange'ler uygulama tarafından açılışta tanımlanır (dayanıklı, kalıcı mesaj): `flightlog.work` → `flightlog.processing`, reddedilenler `flightlog.dead-letter` → `flightlog.processing.dlq`.
- DLQ'daki mesajlar okunamayan veya otomatik denemeleri tükenen işlerdir; ilgili dosya zaten `Failed` durumundadır ve kullanıcı API'den yeniden deneyebilir. DLQ'yu yönetim arayüzünden (Compose'da `127.0.0.1:15672`) izleyin ve inceledikten sonra boşaltın.
- Broker verisi (`rabbitmq-data` volume) kaybolursa iş kaybolmaz: kuyrukta 30 dakikadan uzun bekleyen işler için mesaj yeniden yazılır.
- Kuyruk derinliği ve bekleyen outbox sayısı `GET /api/v1/admin/processing/stats` ile görülür.
