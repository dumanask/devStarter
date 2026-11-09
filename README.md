# devStarter

Ekran kenarına yapışan, açılır-kapanır bir proje başlatıcı. Bir projeye bağladığın her şeyi tek tıkla aynı anda açar: Unity projesi, VS Code klasörü, Chrome sayfaları veya yer imi klasörü, Blender dosyası, Docker compose, klasör, terminal ya da herhangi bir uygulama.

## Özellikler

- Sol, sağ veya üst kenara yerleşir; fare uzaklaşınca kendini gizler
- Kart görünümü veya yalnızca simgelerden oluşan dar görünüm
- Unity Hub projelerini, VS Code klasörlerini, Chrome profillerini, yer imlerini ve açık sayfalarını kendisi bulur
- Unity projesi zaten açıksa ikinci kez açmaz, penceresini öne getirir
- Docker Desktop kapalıysa başlatır, hazır olunca `docker compose up -d` çalıştırır
- Projelere simge, renk ve favori; düzenleme ve silme

## Kurulum

Windows 10/11 (64 bit) gerekir.

**Kaynak koddan:** [.NET 10 SDK](https://dotnet.microsoft.com/download) kurulu olmalı. `install.cmd` dosyasına çift tıkla. Bu betik:

- uygulamayı derleyip `%LOCALAPPDATA%\Programs\devStarter` klasörüne kurar
- Başlat menüsüne ve masaüstüne kısayol koyar
- başkalarıyla paylaşılabilecek tek dosyalık exe'yi `dist\DevStarter.exe` olarak bırakır (.NET kurulumu gerektirmez)

**Hazır exe ile:** `DevStarter.exe` dosyasını kalıcı bir klasöre koyup çalıştır. Bilgisayar açılınca başlaması için kenar çubuğundaki ⋯ menüsünden "Windows ile başlat"ı aç.

İmzasız olduğu için Windows "bilgisayarınızı korudu" uyarısı gösterebilir: **Ek bilgi → Yine de çalıştır**.

## Veriler

Projeler ve ayarlar `%APPDATA%\devStarter` klasöründe tutulur. Uygulama internete bağlanmaz.
