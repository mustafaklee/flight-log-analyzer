# ADR-0001: Modüler monolit + ayrı worker

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Ürün tek bir ekip tarafından geliştiriliyor; modüller (kimlik, filo, uçuş, işleme, analiz, ekip incelemesi) aynı veritabanını ve aynı yetki modelini paylaşıyor. Tek bileşen farklı kaynak profiline sahip: log parse işlemi CPU ve bellek yoğun, uzun süreli ve güvenilmeyen girdiyle çalışıyor.

## Karar
- API, tek bir ASP.NET Core süreci olarak modüler monolit şeklinde çalışır. Modül sınırları klasör/namespace düzeyinde (`Application/Teams`, `Application/Projects`, ...) tutulur.
- Log işleme `FlightLog.Worker` adlı ayrı bir süreçte çalışır; API ile yalnızca veritabanı ve kuyruk mesajları üzerinden konuşur. Böylece parser çökmesi veya kaynak tüketimi API'yi etkilemez ve worker bağımsız ölçeklenebilir.
- Domain, Application ve Infrastructure projeleri API ve Worker tarafından paylaşılır.

## Alternatifler
- **Mikroservisler**: Ayrı veritabanları, dağıtık işlem ve ağ sözleşmeleri getirir; mevcut ekip ve ölçekte maliyeti faydasından yüksek.
- **Tamamen tek süreç (parse API içinde, `BackgroundService`)**: Basit ama parser'ın kaynak tüketimi API'yi etkiler, bağımsız ölçeklenemez.

## Sonuçlar
- Tek repository, tek veritabanı şeması, tek migration geçmişi.
- Worker ve API aynı EF Core modelini kullanır; şema değişikliği ikisini birlikte dağıtmayı gerektirir.
- Gerekirse bir modül ileride ayrı servise çıkarılabilir; modül sınırları buna hazırlık sağlar.
