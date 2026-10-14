# ADR-0008: Yapay zekâ analiz sağlayıcısı

- Durum: Önerildi (Aşama 6'da kesinleşecek)
- Tarih: 2026-10-14

## Bağlam
AI raporu gerçek ölçümlere dayanmalı, uydurma sayı üretmemeli, sağlayıcıya bağımlı olmamalı ve ham log içeriğini kontrolsüz göndermemelidir.

## Karar (taslak)
- `IFlightAnalysisProvider` arayüzü; ilk adaptör Anthropic Claude Messages API (sunucu tarafında, anahtar ortam değişkeninden). Model adı yapılandırılabilir.
- Girdi: deterministik analizden gelen bulgular (kimlikleriyle), olay özetleri, sinyal istatistikleri ve seçili zaman pencereleri. Ham dosya gönderilmez. Log kaynaklı metinler ayrı, "güvenilmeyen veri" olarak etiketlenmiş bir bölümde iletilir.
- Çıktı: araç kullanımı (tool use) / JSON şeması ile yapılandırılmış yanıt; her iddia `evidenceRefs` (bulgu/olay/sinyal kimlikleri ve zaman aralığı) içermek zorunda. Sunucu bu referansları doğrular; geçersiz referanslı iddialar "desteklenmeyen" işaretlenir veya atılır. Sayısal değerler girdideki değerlerle karşılaştırılır.
- Kayıt: model, prompt sürümü, token kullanımı, maliyet, gecikme, girdi özeti hash'i. Anahtar ve kişisel veri kaydedilmez.
- Hatalar: zaman aşımı, 429/529, geçersiz şema için sınırlı ve maliyet farkındalıklı retry; sonuç `Failed` olarak raporlanır, kural bulguları etkilenmez.

## Sonuçlar
- Kesin model ve API ayrıntıları Aşama 6 başında güncel resmi dokümantasyonla doğrulanacak.
