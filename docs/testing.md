# Test talimatları

## Test projeleri

| Proje | Kapsam | Bağımlılık |
|---|---|---|
| `FlightLog.UnitTests` | Domain kuralları (ekip üyelik değişmezleri, rol sıralaması, proje doğrulaması, araç ve etiket kuralları, işleme durum makinesinin tüm durum çiftleri, log dosyası ve uçuş kuralları, analiz run'ı ve otomatik deneme), analiz kuralları ve kural motoru, zaman serisi aralık hesapları, outbox mesajları ve teslim kararları, AI bağlamı (referanslar, deterministik hash, kırpma, güvenilmeyen metin), AI rapor doğrulaması (referans çözümleme, desteklenmeyen madde, doğrulanamayan sayı), maliyet hesabı, Anthropic adaptörü (sahte HTTP uç noktasına karşı istek gövdesi ve hata eşlemesi), bulgu incelemesi ve yorum kuralları (eşleşme anahtarı, değişiklik bilgisi, zaman aralığı, yumuşak silme), uçuş etiketleri, karşılaştırma hesapları (yüzdelik, pencere içi istatistik, mod süreleri, notlar), CSV yazıcı (formül etkisizleştirme, BOM, değişmez kültür), geçici hata sınıflandırması, format algılama, dosya adı temizleme, FluentValidation kuralları, sayfalama, Clean Architecture katman bağımlılıkları | Yok |
| `FlightLog.IntegrationTests` | Gerçek API (`WebApplicationFactory`) + gerçek PostgreSQL 17, SeaweedFS ve RabbitMQ 4.1 (Testcontainers); migration'lar her test sınıfı için temiz bir veritabanında, dosyalar her fabrika için ayrı bucket'ta, mesajlar ayrı virtual host'ta | Docker |
| `web` (Vitest) | API istemcisi (token yenileme, Problem Details, indirme dosya adı), GPS izi eşleme, zaman penceresi hesapları, bulgular paneli, AI raporu paneli, yorumlar ve zaman aralığı notu, bulgu inceleme paneli ve geçmişi, karşılaştırma görünümü, yönlendirme ve oturum geri yükleme | Yok (`npm test`) |
| `web/e2e` (Playwright) | Docker Compose ortamına karşı tarayıcıda: kayıt, ekip ve proje oluşturma, gerçek PX4 ve ArduPilot logu yükleme, canlı durum, grafikler, olay seçme, sinyal ekleme, bulgunun eşik ve kaynağı, "grafikte göster", parametreler, işleme geçmişi, AI raporu sekmesinin kapalı durumu, harita; etiket, zaman aralığı notu, bulgu incelemesi ve geçmişi, etkinlik sekmesi, iki otopilotun karşılaştırılması, iş listesi ve denetim kaydı; telefon genişliğinde giriş ekranı | Çalışan Compose (`npm run e2e`) |
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

- **Analiz (worker adımları testte doğrudan çalıştırılır)**: ArduPilot SITL ve PX4 logları işlendikten sonra otomatik analiz `Succeeded` olur; 13 kuralın sonucu, uygulanamayan kuralların gerekçesi, bilinen veri boşluğu ve mod geçişi bulguları, filtreler ve sayfa sınırı doğrulanır. Elle yeniden analiz 202 döner, beklerken ikinci istek 409 alır ve sonuç ilk analizle birebir aynıdır. Tekrar gelen işleme ve analiz mesajları iş yapmaz. Viewer analiz isteyemez (403), ekip dışı kullanıcı 404 alır. İşleme istatistikleri yalnızca yöneticiye açıktır.
- **Geçici hata**: nesne depolama okuması yapay olarak hata verdirilir; bir hatadan sonra dosya otomatik denenip tamamlanır, sürekli hatada 4 denemeden sonra `processing.retries_exhausted` ile `Failed` olur ve elle yeniden deneme çalışır.
- **RabbitMQ (Testcontainers ile gerçek broker, her test sınıfı kendi virtual host'unda)**: yükleme outbox → RabbitMQ → arka plan worker'ı yoluyla işlenip analiz edilir; outbox satırları bir kez yayınlanır; okunamayan mesajlar DLQ'ya gider; tekrar gelen mesajlar ack'lenir ve yeni iş açmaz; hazır olma kontrolü broker'ı içerir.

- **AI raporu (yalnızca test projesinde bulunan betikli sağlayıcıyla; gerçek sağlayıcı çağrılmaz)**: rapor gerçek ölçümlere referans verir, uydurma referans ve sayı işaretlenir; bağlam indirilebilir ve hash'i tutar; Markdown rapor; 429 sonrası yeniden deneme ve maliyetin korunması; sağlayıcı hep erişilemezse denemelerin bitmesi; reddetme ve kesilme; okunamayan yanıtta tek onarım çağrısı; geçersiz anahtar; anahtarsız worker; günlük kota (429 + `Retry-After`), bekleyen istek (409), Viewer 403, ekip dışı 404; yönetici kullanım özeti. Özellik kapalıyken istek `ai.disabled` alır.
- **Ekip çalışması**: rol matrisi (yeni uçların hepsi Viewer, Engineer, Project Manager, yönetici ve ekip dışı kullanıcı için); yorum ve zaman aralığı notu, sürüm kontrollü düzenleme, yazar dışı düzenlemenin reddi, yumuşak silme; bulgu incelemesi, atama kuralları, işaret ve not; **yeniden analizden sonra incelemenin aynı bulguya bağlı kalması**; bulgu listesi inceleme filtreleri ve ekip iş listesi; etiketler ve etiket filtresi.
- **Denetim kaydı**: işlemlerin kişi, özet, değişen değerler ve iz kimliğiyle yazılması; yalnızca Project Manager'ın ekip kaydını okuması; kaydın silinen uçuştan sonra da kalması; saklama süresini aşan kayıtların silinmesi.
- **Karşılaştırma ve dışa aktarma**: gerçek PX4 ve ArduPilot uçuşunun karşılaştırılması ve notlar; 1 veya 7 uçuş ve aynı uçuşun iki kez verilmesi 400; başka ekibin uçuşu 404; bulgular/olaylar CSV'sinde BOM, meta satırları ve başlık; JSON raporun formatı ve kaynakları; e-posta ve konum bulunmaması; karşılaştırma CSV'sinde formül etkisizleştirme; her indirmenin denetim kaydına yazılması.
- **Canlı durum**: SignalR hub'ına gerçek token ile bağlanılır; işleme ve analiz durum değişiklikleri kullanıcıya gelir, başka ekibin bildirimi gelmez; token'sız bağlantı reddedilir.
- **Dashboard**: sayılar, uçuş süresi, durum dağılımı ve son analizlerdeki bulgular; ekip dışı kullanıcı 404 alır.
- **API sözleşmesi**: canlı OpenAPI belgesi `web/src/api/openapi.json` ile aynıdır (`OpenApiContractTests`). API değiştiyse `UPDATE_OPENAPI_SNAPSHOT=1` ile dosyayı yeniden yazın ve `cd web && npm run api:types` çalıştırın.

Kural mantığı unit testlerde sahte sinyal kaynağıyla (`RuleTestContext`) test edilir: eşik kaynağı, aralık birleştirme, arm pencereleri, uygulanamama gerekçeleri.

Docker Compose ile uçtan uca kontrol (CI'daki `docker` işi de bunu çalıştırır):

```bash
docker compose up -d --build --wait
scripts/smoke-test.sh                                                   # PX4 örneği
LOG_FILE=tests/fixtures/ardupilot/sitl-copter-4.6.3.bin scripts/smoke-test.sh
scripts/smoke-test.sh http://127.0.0.1:8081                             # web konteyneri (nginx) üzerinden
cd web && npm ci && npx playwright install chromium && npm run e2e      # tarayıcı testleri
```

## Kurallar

- Dış servisler unit testlerde çağrılmaz; veritabanı davranışı EF InMemory ile değil gerçek PostgreSQL ile test edilir.
- Testler birbirinden bağımsızdır; her test kendi kullanıcı ve ekibini oluşturur.
