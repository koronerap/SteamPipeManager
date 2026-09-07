# M0 — SteamCMD Süreç Davranışı: Ölçüm Sonuçları

> Tarih: 2026-09-06
> Araç: `tools/SteamCmdProbe`
> SteamCMD sürümü: 1788292693 (Valve public standalone)

M0'ın amacı planın tek gerçek bilinmeyenini kapatmaktı: SteamCMD'yi bir süreç olarak yönetip
çıktısını canlı okuyabilir ve istemlerini cevaplayabilir miyiz?

---

## Bulgu 1 — Otomatik indirme ve bootstrap çalışıyor

`https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip` kimlik doğrulaması istemeden
indi. İlk çalıştırmada steamcmd kendini güncelledi (43 MB) ve **exit code 7** ile çıktı; bu
"güncellendim, yeniden başlat" anlamına geliyor, hata değil.

**Sonuç:** İlk çalıştırma sihirbazı steamcmd'yi indirip `+quit` ile bir kez çalıştırmalı ve
**exit code 7'yi başarı sayıp ikinci kez çalıştırmalı.** İkinci çalıştırma 1 saniyede exit 0 verdi.

## Bulgu 2 — Düz pipe'ta canlı çıktı YOK

`+login anonymous +app_update 1007 +quit` komutu 21 saniye sürdü. Yönlendirilmiş stdout'tan
karakter karakter okunmasına rağmen **21 saniyelik çıktının tamamı 21308 ms'de, süreç
sonlanırken tek blok halinde geldi.**

```
[   739ms] LINE  | Steam Console Client (c) Valve Corporation - version 1788292693
[   741ms] LINE  | -- type 'quit' to exit --
[ 21308ms] LINE  | Loading Steam API...OK
[ 21308ms] LINE  |  Update state (0x61) downloading, progress: 16.22 (10485760 / 64647928)
[ 21308ms] LINE  |  Update state (0x61) downloading, progress: 52.79 (34129768 / 64647928)
[ 21308ms] LINE  |  Update state (0x61) downloading, progress: 86.63 (56007112 / 64647928)
[ 21308ms] LINE  | Success! App '1007' fully installed.
```

steamcmd stdout bir pipe'a bağlıyken blok tamponlama yapıyor. Kısa çalışan komutlarda bu
fark edilmiyor (süreç hemen bittiği için tampon boşalıyor) — ilk `+quit` testi bu yüzden
yanıltıcı biçimde "canlı" görünmüştü.

**Sonuç:** Build ilerlemesi stdout'tan canlı okunamaz.

## Bulgu 3 — İstemler pipe üzerinden zamanında ulaşmıyor

`+login <geçersiz-kullanıcı>` çalıştırıldığında `password:` istemi (satır sonu ile bitmediği
için zaten `ReadLine()` ile okunamaz) **süreç 120 saniye boyunca beklerken hiç gelmedi**;
ancak süreç sonlandırıldığında tamponla birlikte boşaldı.

**Sonuç:** Şifre/Steam Guard istemleri düz pipe üzerinden sürülemez. Bu, PLAN'da "Yüksek"
olarak işaretlenen riskin gerçekleşmesidir.

## Bulgu 4 — `logs/console_log.txt` canlı yazılıyor ✅

steamcmd, konsol çıktısının tamamını kendi kurulum klasöründeki `logs/console_log.txt`
dosyasına zaman damgalarıyla yazıyor ve **bu dosyayı iş devam ederken artımlı olarak
boşaltıyor.** Çalışan bir işin sırasında dosya boyutu ölçüldü:

```
t=2s  boyut=yok      t=4s  boyut=190      t=6s  boyut=368      t=8s  boyut=549
```

Dosya her steamcmd başlangıcında sıfırlanıyor, yani tek aktif build kuralıyla birebir uyumlu.

**Sonuç:** Canlı log kaynağı stdout değil, `logs/console_log.txt` dosyasının takip edilmesi olacak.

## Bulgu 5 — Kritik satırlar İngilizce, sadece bootstrapper yerelleştirilmiş

Sistem dili Türkçe olduğu için steamcmd'nin **güncelleyici** katmanı Türkçe yazıyor
("Güncellemeler denetleniyor...", "Yükleme doğrulanıyor..."). Buna karşılık Steam **istemci**
katmanının satırları İngilizce kaldı:

```
Connecting anonymously to Steam Public...OK
Waiting for user info...OK
Success! App '1007' fully installed.
```

**Sonuç:** Log parser İngilizce desenlere dayanabilir, ancak **bootstrapper satırlarına asla
bağlanmamalı** — onlar kullanıcının diline göre değişir. Ayrıca sonuç tespiti tek başına
metne değil, exit code ile birlikte değerlendirilmeye devam edecek.

## Bulgu 6 — ConPTY denendi, MVP'den çıkarıldı

Bulgu 2 ve 3'ün standart çözümü Windows sözde konsolu (ConPTY): süreç karşısında gerçek bir
terminal olduğunu sanır ve tamponlama kalkar. Prototip yazıldı (`CreatePseudoConsole` +
`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`), ancak çocuk süreç sözde konsola bağlanmak yerine
üst süreçin konsolunu miras aldı ve çıktı okuyucuya hiç düşmedi.

Hata ayıklamaya devam etmek yerine kapsamdan çıkarıldı, çünkü Bulgu 4 canlı log ihtiyacını
zaten karşılıyor ve geriye kalan tek kullanım alanı (uygulama içi şifre kutusu) **güvenlik
açısından daha kötü** bir tasarım: şifrenin bizim sürecimizden geçmesini gerektirir.

Kod depoda bırakılmadı. v1.1'de uygulama içi login istenirse buradan devam edilir.

---

## Bulgu 7 — Gerçek build çıktısı (2026-09-06, ilk gerçek preview)

İlk gerçek preview build (example_partner / Pixel Racer, AppID 1300000) parser'da üç düzeltme
gerektirdi. Ham log `tests/fixtures/real_preview_build_console_log.txt` içinde saklandı.

**a) Preview başarısında BuildID yok.** Gerçek satır:

```
Successfully finished AppID 1300000 build preview.
```

Desen `(BuildID N)` zorunlu tutuyordu; eşleşmeyince build başarısız sayılıyor ve arayüz
"Build tamamlanamadı (exit code 0)" diyordu. Preview'da hiçbir içerik yüklenmediği için
Steam bir build kaydı oluşturmuyor, dolayısıyla BuildID de yok. Desen artık her iki biçimi
karşılıyor.

**b) Çift zaman damgası.** SteamPipe kendi build satırlarını `[tarih]: ` önekiyle yazıyor,
steamcmd de `console_log.txt`'e yazarken kendi damgasını başa ekliyor:

```
[2026-09-06 18:26:07] [2026-09-06 18:26:07]: Successfully finished AppID 1300000 build preview.
```

Ayrıştırıcı artık öneki tekrarlı olarak soyuyor ve damgadan sonra gelebilen iki noktayı
da hesaba katıyor.

**c) İlerleme biçimi.** Beklenen `Update state (0x61) … progress: 52.79` biçimi
build sırasında görünmüyor; SteamPipe şunu basıyor:

```
.......... 89.6MB (12%)
......... 167.6MB (23%)
```

Yüzde artık buradan okunuyor. Yüzde içermeyen saf nokta satırları (`.....`) log panelini
doldurmaktan başka işe yaramadığı için atılıyor.

**d) Doğrulanan satırlar.** `Logging in user 'x' [U:1:…] to Steam Public...OK`,
`Starting AppID N build (flags 0x2).`, `Building depot N...`, `Scanning content`,
`Unloading Steam API...OK` — hepsi beklendiği gibi. Bootstrapper satırları yine Türkçe
geldi ve Bulgu 5'teki karar doğrulandı.

---

## Bulgu 8 — stdin tamponsuz: konsol penceresi gereksiz

Bulgu 3'te şifre isteminin bize **ulaşmadığını** ölçmüştük ve giriş için görünür konsol
penceresine geçmiştik. Ama o ölçüm yalnızca **çıktı** yönüne bakıyordu. Girdi yönü ayrı
bir kanal ve tamponsuz çıktı:

```
[     4ms] şifre stdin'e yazıldı
...
password:
Proceeding with login using username/password.
Logging in user '…' [U:1:0] to Steam Public...ERROR (Invalid Password)
--- exit: 5 , süre 3.3s ---
```

Şifre **istem beklenmeden** stdin'e yazıldığında SteamCMD onu ihtiyaç duyduğu anda okuyor.
Süreç donmadı, 3.3 saniyede sonuç verdi. Yani giriş uygulama içinden sürülebilir; konsol
penceresi gerekmiyor.

Ayrıca iki düzeltme çıktı:

**a) Hata biçimi `FAILED` değil `ERROR`.** Gerçek çıktı `ERROR (Invalid Password)` veriyor;
parser yalnızca `FAILED (...)` arıyordu ve giriş hatasını hiç görmüyordu. İkisi de destekleniyor.

**b) "mobile authenticator" iki farklı akışta geçiyor.** Hem telefon onayı bildiriminde
(`This account is protected by a Steam Guard mobile authenticator.`) hem kod isteminde
(`Enter the current code from your Steam Guard Mobile Authenticator app:`). Ayırt edici olan
istem biçimi: kod istenirken satır iki nokta ile bitiyor. Ayrıca `FAILED (Two-factor code
mismatch)` gibi bir **hata** mesajı da "Two-factor code" içerdiği için, hata kontrolü Steam
Guard kontrolünden önce yapılmalı — bu sıralama hatasını testler yakaladı.

**Sonuç:** Giriş artık uygulama içi bir pencereden yapılıyor. Şifre yalnızca giriş süresince
bellekte tutuluyor, diske yazılmıyor ve komut satırına konmuyor (process listesinde görünürdü).
Görünür konsol yolu `InteractiveLoginAsync` olarak yedekte duruyor.

---

## Ortaya Çıkan Tasarım

| İhtiyaç | Çözüm |
|---|---|
| İlk kurulum | steamcmd indir, `+quit` çalıştır, exit 7 görürsen tekrar çalıştır |
| İlk giriş (hesap başına bir kez) | steamcmd'yi **kendi görünür konsol penceresinde** başlat; kullanıcı şifresini ve Steam Guard kodunu doğrudan oraya yazar. Şifre uygulamaya hiç girmez. |
| Sonraki build'ler | Oturum cache'lendiği için tamamen etkileşimsiz: `+login <user> +run_app_build <script> +quit` |
| Canlı ilerleme | `logs/console_log.txt` takibi |
| Tam log arşivi | Süreç bitiminde stdout + `console_log.txt` birlikte build kaydına yazılır |
| Sonuç tespiti | exit code + `Successfully finished appID … (BuildID …)` satırı |

### Bunun getirdiği yeni gereksinim: oturum ön kontrolü

Oturum süresi dolmuşsa build komutu **görünmeyen bir şifre istemi karşısında sonsuza kadar
bekler**. Bu yüzden her build'den önce zaman aşımlı bir `+login <user> +quit` ön kontrolü
çalıştırılacak; başarısızsa build başlatılmadan görünür konsol login akışına yönlendirilecek.

Ek güvenlik ağı: build sırasında `console_log.txt` belirli bir süre büyümezse (varsayılan
120 sn) kullanıcıya "SteamCMD yanıt vermiyor, oturum düşmüş olabilir" uyarısı gösterilir ve
iptal seçeneği sunulur.
