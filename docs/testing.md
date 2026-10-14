# Test talimatları

## Test projeleri

| Proje | Kapsam | Bağımlılık |
|---|---|---|
| `FlightLog.UnitTests` | Domain kuralları (ekip üyelik değişmezleri, rol sıralaması, proje doğrulaması, araç ve etiket kuralları, işleme durum makinesinin tüm durum çiftleri, log dosyası ve uçuş kuralları), format algılama, dosya adı temizleme, FluentValidation kuralları, sayfalama, Clean Architecture katman bağımlılıkları | Yok |
| `FlightLog.IntegrationTests` | Gerçek API (`WebApplicationFactory`) + gerçek PostgreSQL 17 ve SeaweedFS (Testcontainers); migration'lar her test sınıfı için temiz bir veritabanında, dosyalar her fabrika için ayrı bucket'ta | Docker |
| `FlightLog.ParserTests` | Aşama 3'te eklenecek (gerçek ULog/DataFlash fixture'ları, pyulog/pymavlink oracle karşılaştırması) | — |

## Çalıştırma

Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json` içinde etkin).

```bash
dotnet test --project tests/FlightLog.UnitTests
dotnet test --project tests/FlightLog.IntegrationTests
dotnet test                                   # hepsi
```

Entegrasyon testleri bir PostgreSQL ve bir SeaweedFS konteyneri başlatır; her test sınıfına ayrı veritabanı ve bucket verir. Konteynerler test bitince Testcontainers tarafından kaldırılır.

Fixture'lar `tests/fixtures/` altındadır. `px4/sample.ulg` pyulog deposundan alınmış gerçek bir PX4 logudur (BSD-3-Clause, lisans metni yanında). ArduPilot için gerçek örnek Aşama 3'te eklenecek; şimdilik yalnızca format algılama sentetik başlıkla test edilir.

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

## Kurallar

- Dış servisler unit testlerde çağrılmaz; veritabanı davranışı EF InMemory ile değil gerçek PostgreSQL ile test edilir.
- Testler birbirinden bağımsızdır; her test kendi kullanıcı ve ekibini oluşturur.
