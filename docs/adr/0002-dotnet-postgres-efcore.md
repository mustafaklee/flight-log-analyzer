# ADR-0002: .NET 10 LTS, PostgreSQL ve EF Core

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Prompt .NET 10 LTS, EF Core ve PostgreSQL istiyor. Clean Architecture uygulanırken gereksiz soyutlamadan kaçınılmalı.

## Karar
- **.NET 10 (LTS)**, `net10.0` hedefi. Ortamda SDK 10.0.112 doğrulandı.
- **EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL 10.x**; migration'lar `FlightLog.Infrastructure/Persistence/Migrations` altında.
- Application katmanı veriye `IApplicationDbContext` arayüzü üzerinden erişir (`DbSet<T>` + `SaveChangesAsync`). Genel amaçlı `IRepository<T>`/`IUnitOfWork` eklenmez; EF Core `DbContext` zaten bir unit of work, `DbSet` zaten bir repository'dir. Karmaşık ve tekrar kullanılan sorgular gerektiğinde özel sorgu servislerine taşınır.
- Optimistic concurrency için PostgreSQL `xmin` sistem sütunu concurrency token olarak kullanılır.
- Zamanlar `timestamptz` (UTC) olarak saklanır; uygulama `TimeProvider` ile zaman alır (testte sabitlenebilir).
- Domain projesi hiçbir NuGet paketine bağımlı değildir.

## Alternatifler
- **Dapper**: Büyük okuma sorgularında ileride kullanılabilir; migration ve ilişki yönetimi için EF Core daha uygun.
- **Repository + UoW katmanı**: Test edilebilirlik argümanı, gerçek PostgreSQL ile entegrasyon testleri (Testcontainers) yapıldığı için zayıf; ek kod ve sızdıran soyutlama getirir.

## Sonuçlar
- Application katmanı `Microsoft.EntityFrameworkCore` paketine (yalnızca soyutlamalar için) bağımlıdır; bu bilinçli bir ödündür.
- Sorgu davranışı gerçek PostgreSQL ile test edilir; EF InMemory sağlayıcısı kullanılmaz.
