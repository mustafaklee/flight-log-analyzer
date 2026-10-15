# ADR-0012: Ekip çalışması, denetim kaydı, karşılaştırma ve dışa aktarma

- Durum: Kabul edildi
- Tarih: 2026-10-15

## Bağlam
Aşama 7 ile mühendisler bir uçuş üzerinde birlikte çalışacak: yorum ve zaman aralığı notu yazacak, bulguları birbirine atayacak, inceleme durumunu (doğrulandı, geçersiz, çözüldü) işaretleyecek, uçuşları etiketleyecek ve karşılaştıracak. Bu kararlar şu sorulara cevap veriyor:

- Bulgular her analizde yeniden yazılıyor (Aşama 4). Bir bulguya verilen inceleme kararı yeniden analizden sonra kaybolmamalı.
- Kim neyi ne zaman değiştirdi sorusu her kayıt için cevaplanabilmeli. Silinen kayıtların izi de kalmalı.
- Karşılaştırılan uçuşlar farklı otopilot, firmware, birim ve örnekleme hızlarına sahip olabilir. Rakamlar yan yana konunca yanıltmamalı.
- Dışa aktarılan dosyalar güvenilmeyen metin (log mesajları, yorumlar) taşıyor ve tablo programlarında açılıyor.

## Karar

### Bulgu incelemesi
- `FindingReview` bir bulgunun kimliğine değil, **kimliğini oluşturan değerlere** bağlıdır: `(FlightId, RuleId, StartUs, EndUs)` benzersiz anahtar. Kural motoru deterministik olduğu için aynı log yeniden analiz edilince aynı kural aynı aralığı tekrar bulur ve inceleme kendiliğinden eşleşir. Eşik değişikliği aralığı değiştirirse inceleme eski aralıkta kalır; iş listesinde "son analizde bu bulgu artık yok" olarak görünür.
- Durumlar: `Open` (varsayılan; inceleme kaydı olmayan bulgu da açık sayılır), `Investigating`, `Confirmed`, `Dismissed`, `Resolved`.
- Atanan kişi ekipte Engineer veya üstü olmalı (`review.assignee_read_only`, `review.assignee_not_member`).
- İlk incelemeyi iki kişi aynı anda oluşturursa benzersiz indeks ikinciyi 409 `review.version_mismatch` ile reddeder.

### Yorumlar ve notlar
- `FlightComment` uçuşa bağlıdır. İsteğe bağlı olarak bir zaman aralığı (`StartUs`, `EndUs`; tek an = sıfır uzunluklu aralık) veya bir bulgu incelemesi (`FindingReviewId`) taşır. En fazla 4000 karakter.
- Düzenleme sürüm kontrollüdür (xmin, `comment.version_mismatch`).
- Silme yumuşaktır: metin silinir, `DeletedAt` ve `DeletedBy` kalır. Silinen metin denetim kaydında saklanır.
- Yetki: okuma Viewer; yazma Engineer; düzenleme yalnızca yazarı; silme yazarı veya Project Manager (yönetici her ekipte Project Manager gibidir).

### Etiketler
- Uçuş başına en fazla 20 etiket, her biri en fazla 40 karakter. Baştaki ve sondaki boşluk kırpılır, büyük-küçük harf farkıyla tekrar edenler bir kez sayılır.
- PostgreSQL `text[]` sütunu ve GIN indeksi. Liste filtresi `tag`; ekip etiketleri sayılarıyla `GET teams/{id}/flight-tags`.

### Denetim kaydı
- `AuditEntry` değişiklikle **aynı transaction'da** yazılır (`IAuditTrail.Record` yalnızca bağlama ekler, kaydetmeyi çağıran servis yapar). Değişiklik geri alınırsa kayıt da yazılmaz.
- Alanlar: ekip, kişi, işlem kodu (`flight.updated`, `finding.status_changed`, `data.exported`…), varlık türü ve kimliği, uçuş, kısa özet, değişen değerler (`from`/`to`; atamada isimler de), istek iz kimliği (`X-Correlation-Id`), zaman.
- Yabancı anahtar yoktur; uçuş, yorum veya üye silinse de kayıt kalır. Parola, token, API anahtarı ve dosya içeriği yazılmaz.
- Okuma: ekip kaydı Project Manager; bir uçuşun etkinliği Viewer (yalnızca o uçuş); tüm ekipler yönetici (`admin/audit-log`).
- Saklama: `Audit:RetentionDays` (varsayılan 730 gün, 0 = süresiz). Worker bakımı eski kayıtları siler.

### Karşılaştırma
- 2 ile 6 uçuş, istek anında hesaplanır, saklanmaz. Sürüm `comparison-v1`. Hesaplama saf bir fonksiyondur (`FlightComparisonCalculator`); aynı girdi aynı sonucu verir.
- Her uçuşun "analiz edilen süresi" arm edilmiş aralıklarıdır. Arm bilgisi yoksa logun tamamı kullanılır ve bu bir not olarak belirtilir.
- Karşılaştırılan normalize sinyaller: roll, pitch, GPS uydu sayısı, HDOP, yer hızı, deniz seviyesinden irtifa, batarya voltajı, akımı ve kalan yüzdesi. Her biri için örnek sayısı, örnekleme hızı, en düşük, en yüksek, ortalama, P5, P50, P95 (tip 7 yüzdelik). Yaw (açısal büyüklük) ve konum dahil edilmez.
- Titreşim otopilotun kendi metriğiyle verilir (ArduPilot VIBE, PX4 `accel_vibration_metric`) ve yalnızca aynı otopilot içinde karşılaştırılabilir diye işaretlenir.
- Notlar (`ComparabilityNoteDto.Code`): `different_autopilot`, `different_firmware`, `different_vehicle_type`, `different_or_unknown_aircraft`, `not_processed`, `no_arming_information`, `no_rule_analysis`, `different_rule_engine`, `no_vibration_metric`. Sinyal satırında farklı birim, eksik veri, 2 kattan büyük örnekleme hızı farkı ve irtifa ile kalan batarya için uyarılar.
- Yetki: her uçuş için Viewer. Erişilemeyen bir uçuş varsa istek 404 ile biter; diğer uçuşların verisi dönmez.

### Dışa aktarma
- Uçuş raporu JSON (`flightlog.flight-report.v1`): uçuş özeti, son analizin bulguları ve inceleme durumları, kural sonuçları, olaylar, parametre değişiklikleri, son AI raporu, yorumlar; kaynaklar ve sınırlamalar listesi. Bulgular CSV, olaylar CSV ve karşılaştırma CSV/JSON.
- CSV: UTF-8 + BOM, RFC 4180, değişmez kültürle sayılar, başta `# anahtar: değer` meta satırları (pandas `comment='#'` ile atlar). `=`, `+`, `-`, `@`, tab veya CR ile başlayan metin hücrelerinin başına `'` eklenir (formül enjeksiyonu).
- Dosyalarda GPS konumu ve e-posta adresi yoktur. Her indirme `data.exported` olarak denetim kaydına yazılır.

## Alternatifler
- **İncelemeyi bulgu kimliğine bağlamak:** Basit, ama her yeniden analizde incelemeler kaybolur ya da elle taşınması gerekir. Reddedildi.
- **Bulguları yeniden analizde güncellemek (silmeden):** Analiz çalıştırmalarının değişmezliğini (Aşama 4) bozar ve geçmiş çalıştırmaların bulguları değişir. Reddedildi.
- **Denetim kaydını veritabanı tetikleyicileriyle yazmak:** Kişi ve iz kimliği bilgisi tetikleyicide yoktur; anlamlı özet üretilemez. Reddedildi.
- **Denetim kaydını log/OTel üzerinden tutmak:** Sorgulanabilirlik ve ekip yetkisi için PostgreSQL tablosu tercih edildi. Log ve metrik aktarımı Aşama 8'de ayrıca ele alınacak.
- **Karşılaştırmayı önceden hesaplayıp saklamak:** Kombinasyon sayısı büyük ve kaynak veri değişebilir. Talep anında hesaplama yeterince hızlı (sinyal blokları zaten aralık okumasına uygun).

## Sonuçlar
- Kural motoru sürümü veya eşik değişirse eski incelemeler yeni aralıklarla eşleşmeyebilir. İş listesi bunu açıkça gösterir; otomatik taşıma yapılmaz.
- Yüzdelikler zamana göre ağırlıklı değil, örnek sayısına göredir. Düzensiz örneklemede yoğun bölgeye kayabilir; sınırlamalarda yazılıdır.
- Denetim kaydı büyür. Saklama süresi ve `(TeamId, CreatedAt)`, `(FlightId, CreatedAt)`, `(EntityType, EntityId)` indeksleri ile sınırlanır.
