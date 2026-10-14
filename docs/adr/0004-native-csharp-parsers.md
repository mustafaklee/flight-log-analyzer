# ADR-0004: Native C# log parser'ları ve oracle doğrulaması

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
PX4 ULog ve ArduPilot DataFlash formatlarını okumamız gerekiyor. Parser ürünün en kritik bileşeni; güvenilmeyen girdiyle çalışır, büyük dosyalarda performanslı olmalıdır.

Değerlendirilen kaynaklar:

| Seçenek | Dil | Lisans | Not |
|---|---|---|---|
| pyulog | Python | BSD-3-Clause (pip metadata ile doğrulandı) | PX4'ün resmi ULog okuyucusu |
| pymavlink `DFReader` | Python | LGPL-3.0 (pip metadata ile doğrulandı) | ArduPilot DataFlash okuyucusu |
| Mission Planner log okuyucusu | C# | GPL-3.0 | Kod kopyalamak projeyi GPL'e bağlar; kullanılmayacak |
| Bakımlı, yaygın bir .NET ULog/DataFlash NuGet paketi | — | — | Doğrulanabilir bir aday bulunamadı |

Her iki format da resmi olarak dokümante edilmiş, kendini tanımlayan ikili formatlardır (ULog: `F` format mesajları; DataFlash: `FMT`/`FMTU` mesajları). Bu, genel ve küçük bir parser yazmayı mümkün kılar.

## Karar
- Her format için C# ile stream tabanlı bir parser yazılır (`UlogParser`, `DataFlashParser`), ortak `IFlightLogParser` arayüzünü uygular.
- Format algılama içerikten yapılır: ULog için 7 baytlık sihirli değer (`ULog` + `0x01 0x12 0x35`), DataFlash için `0xA3 0x95` başlık baytları ve ilk mesajın `FMT` (tip 128) olması.
- **pyulog ve pymavlink yalnızca test oracle'ı olarak** kullanılır: fixture dosyalarından beklenen mesaj sayıları, alanlar ve örnek değerler bir Python betiğiyle JSON'a dökülür ve repository'ye eklenir; C# parser testleri bu JSON'larla karşılaştırır. Çalışma zamanında Python bağımlılığı yoktur.
- Parser; maksimum mesaj boyutu, maksimum format sayısı, iptal (CancellationToken) ve zaman aşımı sınırlarıyla çalışır. Bilinmeyen mesaj türleri atlanır ve istatistiğe yazılır.

## Alternatifler
- **Python parser'ı ayrı süreç olarak çalıştırmak**: Doğrulanmış okuyucuları doğrudan kullanır; ancak worker imajına Python çalışma zamanı ekler, süreçler arası serileştirme maliyeti getirir ve normalizasyon mantığını iki dile böler. Native parser'ın oracle testlerinden geçememesi durumunda geri dönüş planı olarak saklanır.
- **Mission Planner kodunu uyarlamak**: GPL-3.0 lisans uyumsuzluğu.

## Sonuçlar
- Parser kodu bizim bakımımızda; format sürümleri ve uç durumlar (ULog appended data, `O` dropout mesajları, çoklu instance; DataFlash FMTU birimleri, `c/C/e/E/L` ölçek çarpanları) testlerle kapsanmalı.
- Python ve pyulog/pymavlink yalnızca fixture üretimi için geliştirici ortamında gerekir.
