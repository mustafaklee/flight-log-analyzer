# Mimari Karar Kayıtları (ADR)

Her ADR bir kararı, alternatiflerini ve sonuçlarını kaydeder. Durumlar: Önerildi, Kabul edildi, Yerini aldı.

| No | Karar | Durum |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modüler monolit + ayrı log işleme worker'ı | Kabul edildi |
| [0002](0002-dotnet-postgres-efcore.md) | .NET 10 LTS, PostgreSQL, EF Core; genel repository katmanı yok | Kabul edildi |
| [0003](0003-authentication-and-authorization.md) | ASP.NET Core Identity + bearer token; ekip bazlı kaynak yetkilendirmesi | Kabul edildi |
| [0004](0004-native-csharp-parsers.md) | Native C# parser'lar, pyulog/pymavlink ile oracle doğrulaması | Kabul edildi |
| [0005](0005-time-series-storage.md) | Sinyal örnekleri nesne depolamada blok dosyaları; katalog PostgreSQL'de | Kabul edildi |
| [0006](0006-messaging-and-background-jobs.md) | RabbitMQ + outbox ile işleme; periyodik bakım worker içinde (Hangfire kullanılmadı) | Kabul edildi |
| [0007](0007-libraries-and-licenses.md) | Kütüphane seçimi ve lisans kontrolleri | Kabul edildi |
| [0008](0008-ai-analysis-provider.md) | LLM sağlayıcı soyutlaması (Anthropic Claude), kanıta bağlı ve doğrulanan çıktı | Kabul edildi |
| [0009](0009-object-storage-server.md) | Nesne depolama: S3 API (AWSSDK.S3) + SeaweedFS | Kabul edildi |
| [0010](0010-frontend-stack.md) | Frontend: React + TypeScript + Vite, Mantine, uPlot, TanStack Query | Kabul edildi |
| [0011](0011-realtime-status-updates.md) | İşlem durumu bildirimleri: PostgreSQL LISTEN/NOTIFY + SignalR | Kabul edildi |

Şablon: Bağlam → Karar → Alternatifler → Sonuçlar.
