# Gereksinimler

Bu doküman FlightLog Intelligence'ın ilk sürümü (v1) için işlevsel ve işlevsel olmayan gereksinimleri tanımlar. Her gereksinimin bir kimliği vardır; backlog ve testler bu kimliklere referans verir.

## 1. Kapsam

FlightLog Intelligence, PX4 ve ArduPilot tabanlı İHA'ların uçuş loglarını yüklemek, arşivlemek, incelemek, karşılaştırmak ve yapay zekâ destekli olarak analiz etmek için ekip odaklı bir uçuş sonrası inceleme platformudur.

Kapsam dışı (v1):

- Araca komut gönderme, uçuş kontrolü veya otomatik güvenlik kararı uygulama.
- Canlı telemetri alımı (mimari buna hazır olacak, ancak v1 dosya analizine odaklanır; bkz. Aşama 9).
- PDF rapor üretimi (sonraki sürüm).

## 2. Kullanıcılar ve roller

| Rol | Kapsam | Yetkiler (özet) |
|---|---|---|
| Administrator | Sistem geneli | Kullanıcı yönetimi, tüm ekipleri görme, AI sağlayıcı yapılandırması, sistem sağlığı |
| Project Manager | Ekip | Ekip üyelerini ve projeleri yönetme, uçuş/araç oluşturma ve silme, bulgu atama |
| Engineer | Ekip | Araç/uçuş oluşturma, log yükleme, analiz başlatma, yorum ve bulgu durumu değiştirme |
| Viewer | Ekip | Yalnızca okuma |

Roller ekip üyeliği üzerinden verilir (bir kullanıcı farklı ekiplerde farklı rollere sahip olabilir). Administrator sistem rolüdür. Ayrıntı: [ADR-0003](adr/0003-authentication-and-authorization.md).

## 3. İşlevsel gereksinimler

### A — Kullanıcı ve ekip yönetimi
- **FR-A1** Kullanıcı e-posta/parola ile kayıt olabilir (yapılandırmayla kapatılabilir) veya yönetici tarafından eklenebilir.
- **FR-A2** Güvenli oturum açma/kapatma; token süresi sınırlı, yenilenebilir.
- **FR-A3** Kullanıcı kendi profilini görebilir.
- **FR-A4** Kullanıcı ekip oluşturabilir; oluşturan kişi ekibin Project Manager'ı olur.
- **FR-A5** Project Manager ekibe kayıtlı kullanıcıları rol ile ekleyebilir, rolünü değiştirebilir, çıkarabilir. Ekipte en az bir Project Manager kalmalıdır.
- **FR-A6** Proje ekip içinde oluşturulur; projeye erişim ekip üyeliğinden türetilir.
- **FR-A7** Kullanıcı başka bir ekibin kaynaklarını ID değiştirerek okuyamaz veya değiştiremez (404 döner, kaynağın varlığı sızdırılmaz). Entegrasyon testiyle doğrulanır.
- **FR-A8** Kritik değişiklikler (üyelik, rol, durum değişiklikleri) kullanıcı ve zaman bilgisiyle denetim kaydına yazılır.

### B — Filo ve araç yönetimi
- **FR-B1** Araç; ad, kimlik (seri no/sysid), tip, otopilot (PX4/ArduPilot), firmware adı/sürümü, uçuş kontrolcüsü, donanım bilgisi, etiketler, açıklama ile kaydedilir.
- **FR-B2** Araç bir ekibe aittir; projelerle ilişkilendirilebilir.
- **FR-B3** Bilinmeyen alanlar `null` olarak saklanır ve arayüzde "bilinmiyor" gösterilir; uydurma değer üretilmez.

### C — Uçuş geçmişi
- **FR-C1** Uçuş listesi: tarih aralığı, araç, proje, firmware, işleme/analiz durumu filtreleri; arama, sıralama, sayfalama.
- **FR-C2** Listede uçuş süresi, bulgu sayısı ve sağlık göstergesi görünür. Sağlık göstergesi alt metrikleri ve kanıtlarıyla saklanır.
- **FR-C3** 100.000 uçuş kaydında liste sorgusu indeksli çalışır (hedef: p95 < 300 ms, Aşama 8'de ölçülecek).

### D — Log yükleme
- **FR-D1** Tek veya çoklu dosya yükleme (drag-and-drop, dosya seçimi), ilerleme gösterimi.
- **FR-D2** Format içerikten algılanır (PX4 ULog sihirli baytları, DataFlash `0xA3 0x95` başlığı ve FMT mesajı); uzantı tek başına yeterli kabul edilmez.
- **FR-D3** Maksimum dosya boyutu yapılandırılabilir; aşan istek işlenmeden reddedilir.
- **FR-D4** SHA-256 ve boyut stream üzerinde hesaplanır; aynı ekip içinde aynı hash tekrar yüklenirse mevcut kayıt bildirilir. Farklı ekipler aynı içeriği yüklese bile kayıtlar ve erişim ayrıdır (nesne depolamada içerik paylaşılabilir; sahiplik ayrı tutulur).
- **FR-D5** Dosya depolandıktan sonra işleme işi kuyruğa alınır; HTTP isteği parser'ı beklemez.
- **FR-D6** İşleme durumu izlenebilir; başarısız işler açıklanır, yeniden denenebilir veya iptal edilebilir.
- **FR-D7** Dosya adı yalnızca görüntüleme metadata'sıdır; depolama anahtarı sunucuda üretilir (path traversal yok).

### E — Uçuş inceleme
- **FR-E1** Uçuş özeti, metadata, zaman çizelgesi, telemetri grafikleri, olay listesi, bulgular, konum izi, mod geçişleri, AI raporu, yorumlar, ham/normalize veri ayrıntısı.
- **FR-E2** Birden fazla sinyal aynı zaman ekseninde; zaman aralığı seçimi, zoom, olay işaretleri; birimler gösterilir.
- **FR-E3** Sinyal sorguları istenen zaman aralığı ve çözünürlük için downsample edilmiş veri döner (min/max korumalı); ham veriye erişim sınırlı sayfalama ile korunur.
- **FR-E4** Harita yalnızca geçerli GPS fix'i olan konum verisi varsa gösterilir.

### F — Kural tabanlı analiz
- **FR-F1** Kurallar kimlik + sürüm taşır; gerekli sinyalleri ve uygulanamayacağı koşulları beyan eder. Gerekli sinyal yoksa kural "uygulanamadı" sonucu üretir, arıza üretmez.
- **FR-F2** Bulgu: kural kimliği/sürümü, başlık, açıklama, zaman aralığı, ilgili sinyaller, ölçülen değerler, eşik, şiddet, güven, veri kapsamı, sınırlamalar.
- **FR-F3** Aynı olaydan art arda çıkan bulgular zaman penceresiyle birleştirilir.
- **FR-F4** Analiz tekrarlanabilir: aynı dosya + aynı kural sürümü aynı bulguları üretir.

### G — Yapay zekâ analizi
- **FR-G1** Sağlayıcı `IFlightAnalysisProvider` arkasındadır; anahtarlar yalnızca sunucuda.
- **FR-G2** Modele ham dosya gönderilmez; yalnızca deterministik analizden çıkan özet istatistikler, olaylar, bulgular ve zaman aralıkları gönderilir.
- **FR-G3** Çıktı şemaya göre doğrulanır; her AI iddiası mevcut bir bulgu/sinyal/olay kimliğine referans vermek zorundadır, aksi halde "desteklenmeyen" olarak işaretlenir.
- **FR-G4** Log içindeki metinler güvenilmeyen veri olarak, talimat bölümünden ayrılarak iletilir.
- **FR-G5** Model adı, prompt sürümü, zaman, token/maliyet, gecikme ve girdi özeti saklanır.
- **FR-G6** Arayüzde AI yorumları kural bulgularından görsel olarak ayrılır.

### H — Karşılaştırma
- **FR-H1** İki veya daha fazla uçuş: süre, ortak sinyaller, istatistikler (min/max/ortalama/p50/p95), GPS/EKF olayları, batarya, titreşim, mod dağılımı, bulgular.
- **FR-H2** Farklı birim, örnekleme hızı, firmware veya sensör eksikliği nedeniyle doğrudan karşılaştırılamayan değerler işaretlenir.

### I — Ekip incelemesi
- **FR-I1** Yorum (isteğe bağlı zaman aralığıyla), bulgu durumu (Open, Investigating, Confirmed, Dismissed, Resolved), sorumlu atama, etiket.
- **FR-I2** Tüm değişiklikler kullanıcı ve zamanla denetim kaydında.

### J — Raporlama
- **FR-J1** Uçuş özeti, teknik analiz, AI raporu, olay/bulgu listesi, karşılaştırma raporu; CSV ve JSON dışa aktarma.
- **FR-J2** Rapor analiz sürümü, oluşturma zamanı, kaynaklar ve sınırlamaları içerir.

### K — Canlı izleme hazırlığı
- **FR-K1** Normalize sinyal ve olay sözleşmeleri dosya parser'ından bağımsızdır; canlı adaptör aynı sözleşmeyi üretebilir.

## 4. İşlevsel olmayan gereksinimler

| Kimlik | Gereksinim | Doğrulama |
|---|---|---|
| NFR-1 | Tüm zamanlar UTC (`timestamptz`) saklanır; uçuş içi zaman orijinal mikro saniye değeriyle birlikte tutulur | Unit + migration testi |
| NFR-2 | Parser bozuk girdi karşısında süreç çökmez; zaman ve bellek sınırı uygulanır | Parser testleri (fuzz benzeri bozuk dosyalar) |
| NFR-3 | Büyük dosyalar stream olarak okunur; 500 MB dosya < 1 GB bellekle işlenir (hedef, Aşama 8'de ölçülecek) | Benchmark |
| NFR-4 | Aynı işleme işi iki kez çalışırsa yinelenen veri oluşmaz | Entegrasyon testi |
| NFR-5 | API hataları RFC 9457 Problem Details biçiminde; iç hata ayrıntısı sızdırılmaz | Entegrasyon testi |
| NFR-6 | Yapılandırılmış loglar (Serilog), korelasyon kimliği, OpenTelemetry izleri | Manuel + test |
| NFR-7 | Sağlık kontrolleri: canlılık ve hazırlık (veritabanı, kuyruk, depolama) | Entegrasyon testi |
| NFR-8 | Gizli bilgiler repository'de bulunmaz; environment variable veya secret store | CI kontrolü |
| NFR-9 | Kimlik doğrulama endpoint'lerinde rate limiting | Entegrasyon testi |
| NFR-10 | Docker Compose ile tek komutla yerel kurulum | README adımları |

Performans hedefleri ölçülmeden "sağlandı" olarak raporlanmaz.

## 5. Varsayımlar ve açık sorular

Varsayımlar (aksi belirtilmedikçe geçerli):

1. Dokümantasyon Türkçe, kod ve API isimleri İngilizce.
2. Ekip tek kiracılı bir kurulumda çalışır (çok kiracılı SaaS değil); veri izolasyonu ekip düzeyindedir.
3. Kullanıcı kimliği yerel (ASP.NET Core Identity). Harici SSO (OIDC) sonraki sürüm konusu.
4. Yapay zekâ sağlayıcısı olarak Anthropic Claude API ilk adaptör olacak; arayüz başka sağlayıcıya izin verir.
5. Örnek log dosyaları için lisansı uygun açık kaynak test dosyaları (pyulog BSD-3 örnekleri, ArduPilot test logları) kullanılacak; gerçek saha logları kullanıcı tarafından sağlanırsa repository'ye eklenmeyecek.

Açık sorular (cevap gelene kadar varsayımla ilerlenir):

- Hedef sunucu ortamı (tek VM + Docker Compose mu, Kubernetes mi)? Varsayım: tek Linux VM + Docker Compose.
- Ham log saklama süresi ve silme politikası? Varsayım: süresiz saklama, ekip yöneticisi silebilir.
