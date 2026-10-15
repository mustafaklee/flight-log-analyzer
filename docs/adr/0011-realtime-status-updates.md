# ADR-0011: İşlem durumu için gerçek zamanlı bildirim

- Durum: Kabul edildi
- Tarih: 2026-10-15

## Bağlam
Prompt, işlem durumu güncellemelerinin gerektiği yerde SignalR ile verilmesini istiyor. Durumu değiştiren kod worker sürecinde, tarayıcının bağlı olduğu SignalR hub'ı ise API sürecinde çalışıyor. Bu iki süreç arasında bir bildirim yolu gerekiyor. Bu yol iki taşıma modunda da (`RabbitMq`, `Database`) çalışmalı ve birden fazla API örneğinde de doğru olmalı.

## Karar
- **PostgreSQL `LISTEN/NOTIFY`.** Bir dosyanın ya da analiz run'ının durumu değiştiğinde, EF Core interceptor'ı aynı transaction içinde `pg_notify('flightlog_status', …)` çağırır. PostgreSQL bildirimi yalnızca commit olursa ve commit'ten sonra iletir. Böylece "bildirim geldi ama değişiklik geri alındı" durumu oluşmaz.
- Her API örneği ayrı bir bağlantıyla kanalı dinler. Gelen bildirimi SignalR hub'ında (`/hubs/processing`) ekibin grubuna (`team:{id}`) iletir.
- Bildirim yalnızca kimlik ve durum taşır: ekip, uçuş, dosya ya da run kimliği, durum, hata kodu. İstemci ayrıntıyı API'den yeniden okur. Yetki kontrolü bu sayede tek yerde kalır.
- Hub'a bağlanan kullanıcı, bağlandığı anda üyesi olduğu ekiplerin gruplarına eklenir. Bağlantı sırasında değişen üyelik, yeniden bağlanınca geçerli olur.
- Bildirim garanti değildir. Bağlantı koparsa bildirim kaçabilir. Bu yüzden arayüz, işlemi süren dosyaları ayrıca seyrek aralıkla yeniden sorgular. Gerçeğin kaynağı her zaman API'dir.

## Alternatifler
- **RabbitMQ fanout exchange:** Ölçeklenebilir, ancak `Database` taşıma modunda broker yok. Ayrıca durum değişikliği ile mesaj arasında ikinci bir outbox gerekirdi.
- **SignalR Redis backplane:** Redis henüz yok ve worker SignalR istemcisi olmak zorunda kalırdı.
- **Yalnızca polling:** En basit yol, ama gereksiz istek üretir ve gecikmeli.

## Sonuçlar
- Bildirim yükü PostgreSQL'e biner, ancak durum değişikliği başına bir küçük mesajdır.
- Canlı telemetri (ileride) bu kanalı kullanmaz; o veri akışı ayrı tasarlanacak (prompt: dosya ve canlı akış aynı yola bağlanmamalı).
