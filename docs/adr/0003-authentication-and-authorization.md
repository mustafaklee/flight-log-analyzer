# ADR-0003: Kimlik doğrulama ve yetkilendirme

- Durum: Kabul edildi
- Tarih: 2026-10-14

## Bağlam
Kullanıcılar ekiplere üye olur; uçuş verisi ekip/proje bazında izole edilmelidir. Yetkilendirme sadece frontend'de olmamalı ve ID değiştirerek başka ekibin verisine erişim engellenmeli.

## Karar
**Kimlik doğrulama**
- ASP.NET Core Identity (`IdentityUser<Guid>` türevi `ApplicationUser`), PostgreSQL'de EF Core store'ları.
- Token: ASP.NET Core'un yerleşik **bearer token** şeması (`IdentityConstants.BearerScheme`, `AddIdentityApiEndpoints` ile gelir). Access token kısa ömürlü, refresh token ile yenilenir. Token'lar Data Protection ile şifrelenir.
- Kendi `AuthController`'ımız register/login/refresh/me endpoint'lerini sağlar; `MapIdentityApi` kullanılmaz çünkü açık kayıt endpoint'ini yapılandırmayla kapatmak, ek alan (DisplayName) almak ve rate limiting eklemek gerekir.
- Kendi kendine kayıt `Auth:AllowSelfRegistration` ile açılıp kapanır. İlk Administrator, yapılandırmadaki e-posta/parola ile başlangıçta oluşturulur (yalnızca ortam değişkeninden; repository'de parola yok).
- Hesap kilitleme ve parola politikası Identity ayarlarıyla; giriş endpoint'lerinde rate limiting.

**Yetkilendirme**
- Sistem rolü: `Administrator` (Identity rolü).
- Ekip rolleri `TeamMember.Role` alanında: `ProjectManager` > `Engineer` > `Viewer` (sıralı yetki).
- Kaynak bazlı kontrol Application katmanında yapılır: `ITeamAccessService` kullanıcının ekip rolünü çözer; sorgular `WHERE team_id IN (kullanıcının ekipleri)` ile filtrelenir. Üye olmayan kullanıcı için **404** döner (kaynağın varlığı sızdırılmaz); üye ama rolü yetersiz kullanıcı için **403** döner.
- Controller'lar yalnızca `[Authorize]` ile kimlik doğrulamayı zorunlu kılar; iş kuralı yetkisi servislerde, test edilebilir şekilde uygulanır.

## Alternatifler
- **Kendi JWT üretimi**: Yaygın ama imzalama anahtarı yönetimi, iptal ve refresh mantığını elle yazmayı gerektirir. Yerleşik bearer token yeterli; ileride harici OIDC sağlayıcısına (Keycloak, Entra ID) geçiş için JWT bearer şemasına geçmek kolaydır.
- **Sadece cookie**: SPA aynı origin'den sunulursa güvenli bir seçenek; Identity iki şemayı birlikte destekler. Frontend aşamasında (Aşama 5) cookie desteği yeniden değerlendirilecek.
- **ASP.NET Core policy + `IAuthorizationHandler`**: Kaynak bazlı kontrol için mümkün; ancak liste sorgularında filtreleme zaten servis katmanında gerektiğinden kontrol tek yerde toplandı.

## Sonuçlar
- Bearer token'lar opaque'tır; doğrulama için aynı Data Protection anahtarları gerekir. Birden fazla API örneğinde anahtarlar paylaşılmalıdır (PostgreSQL'de saklanır).
- Entegrasyon testleri: başka ekibin projesine ID ile erişim 404, Viewer yazma girişimi 403.
