# ADR-0010: Frontend teknolojileri

- Durum: Kabul edildi
- Tarih: 2026-10-15

## Bağlam
Aşama 5'te ilk arayüz yazılıyor. Prompt şunları istiyor: React ve TypeScript; kurumsal dashboard bileşenleri; büyük zaman serisi grafikleri; büyük tablolar için sanallaştırma; yalnızca konum verisi varsa gösterilen harita; erişilebilirlik ve koyu tema. Frontend iş mantığını kopyalamamalı.

## Karar

| Amaç | Paket | Lisans | Gerekçe |
|---|---|---|---|
| Uygulama | React 19, TypeScript 5.9, Vite 7 | MIT, Apache-2.0, MIT | Yaygın, hızlı geliştirme sunucusu; çıktı statik dosya |
| UI bileşenleri | Mantine 8 (`core`, `hooks`, `dates`, `notifications`, `dropzone`) | MIT | Veri yoğun formlar ve tablolar için hazır bileşenler; koyu tema ve erişilebilirlik (odak yönetimi, ARIA) yerleşik; ek CSS-in-JS çalışma zamanı yok |
| İkonlar | `@tabler/icons-react` | MIT | Mantine ile uyumlu, ağaçtan atılabilir |
| Yönlendirme | React Router 7 | MIT | |
| Sunucu durumu | TanStack Query 5 | MIT | Önbellek, yeniden deneme, geçersiz kılma; SignalR bildirimi gelince ilgili sorgu yenilenir |
| Zaman serisi grafikleri | uPlot | MIT | Canvas tabanlı, yüz binlerce noktayı akıcı çizer; birden çok seri ortak zaman ekseninde, yakınlaştırma ve imleç senkronizasyonu yerleşik |
| Tablo sanallaştırma | TanStack Virtual | MIT | Olay ve parametre listeleri binlerce satır olabilir |
| Harita | Leaflet 1.9 | BSD-2-Clause | Hafif; karo sunucusu yapılandırılabilir. `react-leaflet` kullanılmadı (Hippocratic lisansı OSI onaylı değil) |
| Gerçek zamanlı | `@microsoft/signalr` | MIT | Backend SignalR ile aynı ekosistem |
| API tipleri | `openapi-typescript` (yalnızca geliştirme) | MIT | Tipler API'nin OpenAPI belgesinden üretilir; DTO'lar elle kopyalanmaz |
| Test | Vitest, Testing Library, jsdom; uçtan uca Playwright | MIT / Apache-2.0 | |

- **API sözleşmesi:** API'nin OpenAPI belgesi `web/src/api/openapi.json` olarak repoda tutulur. Bir entegrasyon testi canlı belgenin bu dosyayla aynı olduğunu doğrular. TypeScript tipleri bu dosyadan üretilir; CI tiplerin güncel olduğunu kontrol eder. Backend değişip frontend güncellenmezse CI kırılır.
- **Dağıtım:** Arayüz `web` konteynerinde nginx ile statik dosya olarak sunulur. nginx `/api` ve `/hubs` isteklerini API'ye iletir; böylece tarayıcı tek origin görür, CORS gerekmez. Güvenlik başlıkları (CSP dahil) nginx'te verilir.
- **Oturum:** Access token yalnızca bellekte tutulur. Refresh token, sekmeler ve sayfa yenilemeleri arasında oturum sürsün diye `localStorage`'da tutulur. Bu, XSS durumunda refresh token'ın okunabilmesi demektir. Bunu azaltmak için CSP satır içi script'e izin vermez ve uygulama HTML'i ham olarak işlemez. HttpOnly çerezli bir oturuma geçiş Aşama 8'deki güvenlik sertleştirmesinde değerlendirilecek.
- **İş mantığı yok:** Durum geçişleri, yetki ve analiz kararları backend'de kalır. Arayüz düğmeleri yalnızca kullanıcının rolüne göre gizler; asıl kontrol API'dedir.

## Alternatifler
- **MUI:** Olgun, ancak gelişmiş veri tablosu ve tarih bileşenlerinin bir kısmı ticari (MUI X Pro).
- **Ant Design:** Kapsamlı, ancak tema özelleştirmesi ve paket boyutu daha ağır.
- **Recharts, Chart.js:** SVG ya da genel amaçlı; 100 bin+ noktalı çoklu sinyalde yavaş. **ECharts** (Apache-2.0) güçlü ama ağır; uPlot zaman serisine odaklı ve küçük.
- **Next.js:** Sunucu tarafı render gerekmiyor; API zaten ayrı.

## Sonuçlar
- Frontend bağımlılıkları `web/package.json`'da sürüm aralığıyla, `package-lock.json` ile kilitli tutulur. CI `npm audit --omit=dev --audit-level=high` çalıştırır.
- Harita karoları varsayılan olarak OpenStreetMap'ten gelir (`VITE_MAP_TILE_URL` ile değiştirilebilir). İnternetsiz kurulumda uçuş izi karo olmadan, düz zemin üzerinde çizilir.
