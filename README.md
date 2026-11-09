# devStarter

Ekran kenarına yapışan, açılır-kapanır bir proje başlatıcı. Bir projeye bağladığın her şeyi tek tıkla aynı anda açar: Unity projesi, VS Code klasörü, Chrome sayfaları veya yer imi klasörü, Blender dosyası, Docker compose, klasör, terminal ya da herhangi bir uygulama.

## Özellikler

- Sol, sağ veya üst kenara yerleşir; fare uzaklaşınca kendini gizler
- Kart görünümü veya yalnızca simgelerden oluşan dar görünüm
- Unity Hub projelerini, VS Code klasörlerini, Chrome profillerini, yer imlerini ve açık sayfalarını kendisi bulur
- Unity projesi zaten açıksa ikinci kez açmaz, penceresini öne getirir
- Docker Desktop kapalıysa başlatır, hazır olunca `docker compose up -d` çalıştırır
- Projelere simge, renk ve favori; düzenleme ve silme

### Ajanlar ve kullanım limitleri

Kenar çubuğundaki robot düğmesi ayrı bir panel açar:

- **Ajanlar:** Claude Code, Codex, Gemini CLI ve OpenCode oturumları projelere göre gruplanır. Her oturum için çalışıyor (yeşil), seni bekliyor (sarı: izin ya da soru) veya boşta (gri) durumu ve bekleniyorsa ne beklediği görünür. Proje kartlarında sayaçlar, kenar çubuğu gizliyken sarı yanan şerit ve uygulama içi bildirim vardır. Satıra tıklayınca oturumun penceresi öne gelir.
- **Limitler:** Claude, Codex, Antigravity ve OpenCode için kullanım yüzdeleri, sıfırlanma zamanları ve (Codex'te) reset hakları.
  - Claude: Claude Code'un kendi kaydettiği son değer okunur; ağ isteği atılmaz.
  - Codex: resmî `codex app-server` üzerinden okunur.
  - Antigravity: yalnızca uygulama açıkken, kendi yerel sunucusundan okunur.
  - OpenCode: yerel kayıtlardan maliyet ve token toplamları; Go aboneliği varsa opencode.ai'den limitler.
- **Bağlantılar:** Claude Code ek ayar gerektirmez. Codex, Gemini ve OpenCode için tek tıkla küçük bir hook (OpenCode'da eklenti) kurulur; değiştirilen dosya önce `%APPDATA%\devStarter\backups` altına yedeklenir ve Kaldır ile yalnızca devStarter'ın eklediği kısım silinir. Hook'lar yalnızca `127.0.0.1`'e bildirim gönderir; devStarter kapalıyken hiçbir şey yapmaz. Codex yeni hook'ları ancak Codex'te `/hooks` ile onaylandıktan sonra çalıştırır.

## Kurulum

Windows 10/11 (64 bit) gerekir.

**Kaynak koddan:** [.NET 10 SDK](https://dotnet.microsoft.com/download) kurulu olmalı. `install.cmd` dosyasına çift tıkla. Bu betik:

- uygulamayı derleyip `%LOCALAPPDATA%\Programs\devStarter` klasörüne kurar
- Başlat menüsüne ve masaüstüne kısayol koyar
- başkalarıyla paylaşılabilecek tek dosyalık exe'yi `dist\DevStarter.exe` olarak bırakır (.NET kurulumu gerektirmez)

**Hazır exe ile:** `DevStarter.exe` dosyasını kalıcı bir klasöre koyup çalıştır. Bilgisayar açılınca başlaması için kenar çubuğundaki ⋯ menüsünden "Windows ile başlat"ı aç.

İmzasız olduğu için Windows "bilgisayarınızı korudu" uyarısı gösterebilir: **Ek bilgi → Yine de çalıştır**.

## Veriler

Projeler ve ayarlar `%APPDATA%\devStarter` klasöründe tutulur. Uygulama yalnızca iki durumda internete bağlanır: Codex limitleri okunurken Codex'in kendi sunucusu (`codex app-server` aracılığıyla) ve OpenCode Go aboneliği varsa opencode.ai. İkisi de yalnızca Limitler sekmesi açıkken yapılır.
