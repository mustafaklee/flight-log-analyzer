# Test talimatları

## Test projeleri

| Proje | Kapsam | Bağımlılık |
|---|---|---|
| `FlightLog.UnitTests` | Domain kuralları (ekip üyelik değişmezleri, rol sıralaması, proje doğrulaması), FluentValidation kuralları, sayfalama, Clean Architecture katman bağımlılıkları | Yok |
| `FlightLog.IntegrationTests` | Gerçek API (`WebApplicationFactory`) + gerçek PostgreSQL 17 (Testcontainers); migration'lar her test sınıfı için temiz bir veritabanında çalışır | Docker |
| `FlightLog.ParserTests` | Aşama 3'te eklenecek (gerçek ULog/DataFlash fixture'ları, pyulog/pymavlink oracle karşılaştırması) | — |

## Çalıştırma

Test çalıştırıcısı Microsoft.Testing.Platform'dur (`global.json` içinde etkin).

```bash
dotnet test --project tests/FlightLog.UnitTests
dotnet test --project tests/FlightLog.IntegrationTests
dotnet test                                   # hepsi
```

Entegrasyon testleri tek bir PostgreSQL konteyneri başlatır ve her test sınıfına ayrı bir veritabanı verir; konteyner test bitince Testcontainers tarafından kaldırılır.

## Entegrasyon testlerinin doğruladıkları

- Kayıt, giriş, refresh token, hesap kilitleme, kayıt kapatma, kimlik doğrulama endpoint'i rate limiting.
- **Ekip izolasyonu (FR-A7)**: Ekip dışı kullanıcı, başka ekibin ekip/üye/proje kaynaklarını ID ile okuyamaz veya değiştiremez; yanıt 404'tür ve var olmayan kaynakla aynıdır.
- Rol kuralları: Viewer okur ama yazamaz (403); son Project Manager ayrılamaz (422).
- Proje CRUD, ad tekilliği (ekip içinde), arama (`%` gibi özel karakterler literal), sayfalama sınırı, `xmin` tabanlı optimistic concurrency (409).
- Problem Details biçimi (`application/problem+json`, `code`, `traceId`, alan bazlı doğrulama hataları).
- Sağlık kontrolleri ve korelasyon kimliği.

## Kurallar

- Dış servisler unit testlerde çağrılmaz; veritabanı davranışı EF InMemory ile değil gerçek PostgreSQL ile test edilir.
- Testler birbirinden bağımsızdır; her test kendi kullanıcı ve ekibini oluşturur.
