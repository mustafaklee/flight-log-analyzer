# Test talimatları

## Test projeleri

| Proje | Kapsam | Bağımlılık |
|---|---|---|
| `FlightLog.UnitTests` | Domain kuralları (ekip üyelik değişmezleri, rol sıralaması, proje doğrulaması, araç ve etiket kuralları, işleme durum makinesinin tüm durum çiftleri, log dosyası ve uçuş kuralları), format algılama, dosya adı temizleme, FluentValidation kuralları, sayfalama, Clean Architecture katman bağımlılıkları | Yok |
| `FlightLog.IntegrationTests` | Gerçek API (`WebApplicationFactory`) + gerçek PostgreSQL 17 ve SeaweedFS (Testcontainers); migration'lar her test sınıfı için temiz bir veritabanında, dosyalar her fabrika için ayrı bucket'ta | Docker |
| `FlightLog.ParserTests` | ULog ve DataFlash parser'larının pyulog/pymavlink çıktısıyla birebir karşılaştırılması (her seri, her alan), bozuk/kesik/rastgele girdi, sınırlar, zaman aşımı, iptal; yorumlayıcılar (firmware, modlar, arm, UTC zamanı); normalizasyon kuralları; blok formatı ve bloklardan geri okunan değerlerin oracle ile eşleşmesi | Yok |

## Çalıştırma

Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json` içinde etkin).

```bash
dotnet test --project tests/FlightLog.UnitTests
dotnet test --project tests/FlightLog.ParserTests
dotnet test --project tests/FlightLog.IntegrationTests
dotnet test                                   # hepsi
```

Entegrasyon testleri bir PostgreSQL ve bir SeaweedFS konteyneri başlatır; her test sınıfına ayrı veritabanı ve bucket verir. Konteynerler test bitince Testcontainers tarafından kaldırılır.

Fixture'lar `tests/fixtures/` altındadır:

| Dosya | Kaynak | Lisans |
|---|---|---|
| `px4/sample.ulg`, `sample_log_small.ulg`, `sample_appended_multiple.ulg`, `sample_logging_tagged_and_default_params.ulg`, `sample_px4_events.ulg` | pyulog deposu (gerçek PX4 logları ve SITL) | BSD-3-Clause (`px4/LICENSE-pyulog.md`) |
| `ardupilot/sitl-copter-4.6.3.bin` | Bu proje için ArduCopter 4.6.3 SITL ile üretildi: GUIDED'da kalkış, LOITER, RTL ve iniş. SITL kapatılırken yazıldığı için son mesaj kesik. | Projenin kendi verisi |
| `expected/*.json` | `tools/fixtures/generate_expected.py` çıktısı | — |

### Oracle verisini yeniden üretmek

Yeni bir fixture eklendiğinde veya oracle sürümü değiştiğinde (yalnızca geliştirici ortamında Python gerekir):

```bash
python3 -m venv .venv && . .venv/bin/activate
pip install -r tools/fixtures/requirements.txt     # pyulog 1.2.4, pymavlink 2.4.50, numpy
python tools/fixtures/generate_expected.py          # tests/fixtures/expected/*.json dosyalarını yazar
dotnet test --project tests/FlightLog.ParserTests
```

Beklenen JSON'lar elle düzenlenmez. Parser bir oracle'dan bilerek farklı davranırsa, fark testte açıkça yazılır ve gerekçesi ADR-0004'e eklenir.

## Entegrasyon testlerinin doğruladıkları

- Kayıt, giriş, refresh token, hesap kilitleme, kayıt kapatma, kimlik doğrulama endpoint'i rate limiting.
- **Ekip izolasyonu (FR-A7)**: Ekip dışı kullanıcı, başka ekibin ekip/üye/proje kaynaklarını ID ile okuyamaz veya değiştiremez; yanıt 404'tür ve var olmayan kaynakla aynıdır.
- Rol kuralları: Viewer okur ama yazamaz (403); son Project Manager ayrılamaz (422).
- Proje CRUD, ad tekilliği (ekip içinde), arama (`%` gibi özel karakterler literal), sayfalama sınırı, `xmin` tabanlı optimistic concurrency (409).
- Problem Details biçimi (`application/problem+json`, `code`, `traceId`, alan bazlı doğrulama hataları).
- Sağlık kontrolleri ve korelasyon kimliği.
- **Log yükleme**: gerçek PX4 dosyası yüklenir, SHA-256 ve boyut doğru hesaplanır, indirilen içerik birebir aynıdır; DataFlash algılama; desteklenmeyen uzantı, içerik/uzantı uyuşmazlığı, boş dosya, dosyasız istek; yapılandırılan sınırı aşan dosya 413 döner ve kayıt oluşturmaz; dosya adındaki dizin yolları atılır.
- Tekrar tespiti: aynı ekipte aynı içerik 409 ve mevcut uçuşu gösterir; başka ekip aynı içeriği ayrı kayıtla yükler, depolama nesnesi paylaşılır ve son kayıt silinince nesne silinir.
- İşleme durumu: iptal ve yeniden deneme kuralları.
- Araç CRUD, ekip içi kimlik tekilliği, optimistic concurrency, araç silinince uçuşların korunması; Viewer/ekip dışı erişim.
- Uçuş listesi: ekip, proje, otopilot, durum, tarih, arama filtreleri, sıralama, sayfalama; başka ekibin uçuşu ve dosyası 404.
- Yönetici kullanıcı yönetimi: hesap oluşturma, devre dışı bırakılan hesabın giriş ve refresh yapamaması, kendini devre dışı bırakamama, yönetici olmayanın 403 alması.
- **Log işleme (worker döngüsü testte doğrudan çalıştırılır)**: gerçek PX4 ve ArduPilot logu yüklenir ve işlenir; uçuşun firmware, araç tipi, UTC başlangıç ve süre değerleri, sinyal kataloğu, tam çözünürlüklü ve downsample edilmiş veri, zaman penceresi, beyan edilen çarpanın uygulanması, olaylar ve parametreler doğrulanır. Bozuk dosya kararlı bir kodla `Failed` olur ve yeniden denenebilir; işlem sırasında iptal ve worker kapanışı veri bırakmaz; takılı iş `processing.interrupted` olur; uçuş silinince sinyal blokları silinir; ekip dışı kullanıcı tüm uçlarda 404 alır.

Docker Compose ile uçtan uca kontrol (CI'daki `docker` işi de bunu çalıştırır):

```bash
docker compose up -d --build --wait
scripts/smoke-test.sh                                                   # PX4 örneği
LOG_FILE=tests/fixtures/ardupilot/sitl-copter-4.6.3.bin scripts/smoke-test.sh
```

## Kurallar

- Dış servisler unit testlerde çağrılmaz; veritabanı davranışı EF InMemory ile değil gerçek PostgreSQL ile test edilir.
- Testler birbirinden bağımsızdır; her test kendi kullanıcı ve ekibini oluşturur.
