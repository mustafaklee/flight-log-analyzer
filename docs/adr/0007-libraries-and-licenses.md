# ADR-0007: Kütüphane seçimi ve lisanslar

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Gereksiz bağımlılık eklenmemeli; sürüm ve lisanslar kontrol edilmeli. Son yıllarda bazı yaygın .NET kütüphaneleri ticari lisansa geçti.

## Karar (Aşama 2 itibarıyla)

| Amaç | Paket | Sürüm | Lisans |
|---|---|---|---|
| ORM | Microsoft.EntityFrameworkCore / Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.x | MIT / PostgreSQL |
| Kimlik | Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.x | MIT |
| OpenAPI | Microsoft.AspNetCore.OpenApi + Swashbuckle.AspNetCore.SwaggerUI | 10.0.x / 10.3.x | MIT |
| Loglama | Serilog.AspNetCore | 10.0.x | Apache-2.0 |
| Doğrulama | FluentValidation | 12.x | Apache-2.0 |
| Sağlık | Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 10.0.x | MIT |
| Nesne depolama istemcisi | AWSSDK.S3 | 4.0.x | Apache-2.0 |
| Test | xunit.v3, AwesomeAssertions, NSubstitute, Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing | güncel | Apache-2.0 / Apache-2.0 / BSD-3 / MIT / MIT |

Bilinçli olarak kullanılmayanlar:
- **MediatR** (v13+ ticari lisans) — use case'ler düz uygulama servisleri olarak yazılır; CQRS ayrımı gerekmedikçe aracı katman yok.
- **AutoMapper** (v15+ ticari lisans) — eşleme elle, açık kodla.
- **FluentAssertions** (v8+ ticari lisans) — Apache-2.0 lisanslı fork **AwesomeAssertions** kullanılır.
- **Moq** — 2023'teki SponsorLink olayı nedeniyle; **NSubstitute** kullanılır.

## Sonuçlar
- Yeni bağımlılık eklenirken bu tablo güncellenir; CI'da `dotnet list package --vulnerable` çalıştırılır.
