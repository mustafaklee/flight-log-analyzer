# ADR-0008: Yapay zekâ analiz sağlayıcısı ve kanıta bağlı çıktı

- Durum: Kabul edildi
- Tarih: 2026-10-15 (taslak 2026-10-14)

## Bağlam
Kural tabanlı analiz (Aşama 4) ölçülebilir bulgular üretir. Mühendisler bu bulguların birlikte ne anlama geldiğini özetleyen bir yorum istiyor. Bir dil modeli bu yorumu yazabilir, ama:

- uydurma sayı veya olmayan bir olay üretebilir,
- sağlayıcıya ve modele bağımlılık yaratır,
- log içindeki metinler (STATUSTEXT, mesajlar) modele talimat gibi görünebilir,
- maliyeti ve gecikmesi değişkendir; hata, kota ve yeniden deneme yönetimi gerekir,
- ham log ve kişisel/konum verisi dışarı gönderilmemelidir.

## Karar

### Sağlayıcı soyutlaması
- `IFlightAnalysisProvider` (Application) yalnızca "sistem talimatı + kullanıcı mesajı + JSON şeması → metin, durma nedeni, model, token kullanımı, gecikme" sözleşmesini tanır. Hatalar `AiProviderException` türleriyle sınıflanır: `Transient`, `RateLimited`, `Authentication`, `Rejected`.
- İlk adaptör `AnthropicFlightAnalysisProvider` (Infrastructure), resmi `Anthropic` C# SDK'sı (MIT, 12.57.0) ile Messages API'yi çağırır. Varsayılan model `claude-opus-5-5`, `effort: high`. Model, effort, çıktı token sınırı ve zaman aşımı yapılandırmadan gelir.
- Yanıt formatı yapılandırılmış çıktıdır (`output_config.format` = JSON şeması, her nesnede `additionalProperties: false`). Şema min/max ve uzunluk kısıtı içermez; bu kontroller sunucu doğrulamasındadır.
- API anahtarı yalnızca worker konteynerine ortam değişkeniyle verilir (`Ai__ApiKey`). API ve arayüz anahtarı görmez; anahtar loglanmaz ve veritabanına yazılmaz.
- Özellik varsayılan olarak kapalıdır (`Ai:Enabled=false`). Açık ama anahtarsız bir worker isteği açık bir hata koduyla (`ai.not_configured`) bitirir; sahte rapor üretilmez.

### Bağlam (girdi)
- `FlightContextBuilder` (`flight-context-v1`) son başarılı kural analizinden deterministik bir JSON üretir. Bölümler ve referanslar:
  - `F1..` bulgular,
  - `R1..` kural sonuçları (her durum),
  - `S1..` sinyal istatistikleri (tüm uçuş ve bulgu pencereleri),
  - `E1..` olaylar,
  - `P1..` eşik kaynağında geçen parametreler ve değişiklikleri.
- Zamanlar log başlangıcından saniye olarak verilir. Sayılar sabit kurallarla yuvarlanır.
- Gönderilmeyenler: ham dosya ve ham örnekler, uçuş adı/notları, GPS enlem/boylamı, kullanıcı bilgileri.
- Log kaynaklı metinler temizlenir (kontrol karakterleri, uzunluk sınırı) ve `untrustedLogText` olarak işaretlenir; sistem talimatı bunların talimat değil veri olduğunu söyler.
- Bağlam karakter sınırını aşarsa sırayla olaylar, parametreler, istatistikler ve bulgular kırpılır; kırpılanlar `omittedForSize` alanında belirtilir.
- Bağlam metni olduğu gibi saklanır (`text` sütunu, `jsonb` değil; anahtar sırası korunur). SHA-256 hash'i de kaydedilir, böylece aynı girdi kanıtlanabilir ve mühendis tam bağlamı indirebilir.

### Çıktı doğrulaması
- Model; özet, genel değerlendirme ve maddeler (gözlem, olası neden, öneri, veri eksikliği) döndürür. Her madde `evidenceRefs` ve güven düzeyi içerir.
- `FlightReportValidator`:
  - referansları bağlamdaki öğelere çözer; bilinmeyen referanslar `unknownRefs` olarak listelenir;
  - geçerli referansı olmayan maddeyi "desteklenmeyen" olarak işaretler (atmaz, ayrı bölümde gösterir);
  - metindeki sayıları bağlamdaki sayılarla karşılaştırır ve bulunamayanları `unverifiedNumbers` olarak işaretler. Tolerans: yazılan son basamağın yarısı veya %0,5; 10'a kadar tam sayılar atlanır.
- Şemaya uymayan veya ayrıştırılamayan yanıt için tek bir onarım çağrısı yapılır. Yine geçersizse `ai.invalid_output` ile biter. Ham çıktı inceleme için saklanır.
- Arayüz bulguları kural bulgularından ayrı, "Model yorumu" uyarısıyla gösterir. Kanıtlar "Grafikte göster" ile grafiğe bağlanır.

### İşleme, hata ve kota
- İstek `AiAnalysis` satırı ve outbox mesajı (`flightlog.ai-analyze-flight.v1`) oluşturur; worker RabbitMQ veya yoklama ile alır, satırı sürüm karşılaştırmalı (xmin) olarak sahiplenir. Takılı kalan çalıştırmalar bakımda kurtarılır.
- Durma nedenleri: `refusal` → `ai.refused`, `max_tokens` → `ai.truncated`, `model_context_window_exceeded` → `ai.context_too_large`.
- 429, 5xx ve ağ hataları sınırlı sayıda otomatik yeniden denenir (üstel gecikme, `retry-after` dikkate alınır). 401/403 → `ai.provider_auth`, diğer 4xx → `ai.provider_rejected` (yeniden denenmez).
- Ekip başına günlük kota (varsayılan 20); aşılırsa 429 ve `Retry-After`. Aynı uçuş için bekleyen istek varsa 409.
- Her çağrı ayrı kaydedilir, başarısız olanlar da: model, yanıt modeli, prompt ve bağlam sürümü, giriş/çıkış/önbellek token'ları, gecikme, tahmini maliyet (`Ai:Prices` tablosu; fiyatı bilinmeyen model için maliyet boş kalır), worker, deneme sayısı. Yönetici ekranı dönemlik kullanımı, maliyeti ve hata kodlarını gösterir.
- AI hatası kural bulgularını hiçbir zaman değiştirmez.

## Alternatifler
- **Doğrudan HTTP istemcisi:** Bağımlılık yok, ama hata türleri, yeniden deneme ve şema yardımcıları elle yazılmalı. Resmi SDK MIT lisanslı ve küçük olduğu için tercih edildi; SDK yalnızca Infrastructure'da ve arayüzün arkasında.
- **Araç kullanımı (tool use) ile yapılandırılmış çıktı:** Çalışır, ama yapılandırılmış çıktı formatı daha doğrudan ve şemaya uyumu sağlayıcı garanti ediyor.
- **Ham sinyal örneklerini göndermek:** Maliyet ve gizlilik riski yüksek, doğrulanabilirliği düşük. İstatistikler ve pencereler yeterli bulundu.
- **Desteklenmeyen maddeleri atmak:** Bilgi kaybına ve "model ne dedi" sorusunun cevapsız kalmasına yol açar. İşaretleyip ayrı göstermek seçildi.

## Sonuçlar
- Sağlayıcı değiştirmek yeni bir `IFlightAnalysisProvider` adaptörü ve fiyat tablosu girdisi demektir; bağlam, doğrulama ve arayüz değişmez.
- Gerçek sağlayıcıya CI'da çağrı yapılmaz. Adaptör sahte HTTP uç noktasına karşı (istek gövdesi ve hata eşlemesi), akış ise yalnızca test projesinde bulunan betikli bir sağlayıcıyla test edilir.
- Sayı doğrulaması sezgiseldir: birim dönüşümü veya hesaplanmış değerler (örn. fark) "doğrulanmamış" görünebilir. Bu bir hata değil, mühendise kontrol etmesi gereken yeri gösteren bir işarettir.
