# ADR-0009: Nesne depolama — S3 API ve SeaweedFS

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Ham log dosyaları ve (Aşama 3'ten itibaren) sinyal blokları nesne depolamada tutulur ([ADR-0005](0005-time-series-storage.md)). Yerel geliştirme ve tek sunuculu kurulum için Docker Compose'a S3 uyumlu bir sunucu gerekir; üretimde AWS S3 veya başka bir S3 uyumlu servis kullanılabilmelidir.

İlk plan MinIO idi. Aşama 2 sırasında yapılan kontrolde:
- `minio/minio` imajı Docker Hub'dan çekilemedi ("repository does not exist"); MinIO topluluk sürümü için resmi ikili/imaj dağıtımı durdurulmuş durumda.
- Kalan imajlar farklı registry'lerde ve bakımı belirsiz; topluluk sürümünün yönetim arayüzü de kısıtlandı.

## Karar
1. Uygulama yalnızca S3 API'sine bağlıdır: `IFileStorage` (Application) → `S3FileStorage` (Infrastructure, **AWSSDK.S3**, Apache-2.0). Path-style adresleme ve `RequestChecksumCalculation = WHEN_REQUIRED` ayarı, S3 uyumlu sunucularla çalışmak için yapılandırılabilir.
2. Yerel ve tek sunuculu kurulumda **SeaweedFS** (`chrislusf/seaweedfs:4.48`, Apache-2.0) kullanılır; `weed mini` modu S3 geçidini tek konteynerde çalıştırır. Kimlik bilgileri `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` ile verilir, telemetri kapalıdır (`-master.telemetry=false`).
3. Ham dosya anahtarı sunucuda üretilir ve içerik adreslidir: `raw/{sha256}`. İstemci dosya adı yalnızca görüntüleme verisidir.
4. Entegrasyon testleri aynı imajı Testcontainers ile başlatır; her test fabrikası ayrı bucket kullanır.

## Alternatifler
- **MinIO**: Yukarıdaki dağıtım sorunu nedeniyle bırakıldı. S3 API'sine bağlı kaldığımız için ileride tekrar seçilebilir; kod değişikliği gerekmez.
- **Garage** (AGPL-3.0): Hafif, ama AGPL lisansı ve çok düğüm odaklı kurulum gerektirmesi nedeniyle tercih edilmedi.
- **Yerel dosya sistemi adaptörü**: Basit, fakat çok örnekli worker/API dağıtımında paylaşılan disk gerektirir; S3 yolu ile üretim ortamı arasında fark yaratır.
- **Minio .NET paketi**: AWSSDK.S3 tüm S3 uyumlu sunucularla çalıştığı için ek bir istemciye gerek görülmedi.

## Sonuçlar
- Üretimde AWS S3 veya başka bir S3 uyumlu servis yalnızca `Storage__*` ortam değişkenleriyle seçilir.
- SeaweedFS konteyneri tek düğümdür; yedekleme `storage-data` volume'ünün yedeklenmesiyle yapılır (bkz. [deployment.md](../deployment.md)). Yüksek erişilebilirlik gerekiyorsa yönetilen bir S3 servisine geçilir.
- `/health/ready` depolamaya erişilebildiğini kontrol eder.
