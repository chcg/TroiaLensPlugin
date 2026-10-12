# TroiaLens — Notepad++ eklentisi

![C#](https://img.shields.io/badge/C%23-.NET_Framework_4.8-512BD4?logo=dotnet&logoColor=white)
![Notepad++](https://img.shields.io/badge/Notepad++-x64-90E59A?logo=notepadplusplus&logoColor=black)
![WebView2](https://img.shields.io/badge/WebView2-Evergreen-0078D7?logo=microsoftedge&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10_%7C_11-0078D6?logo=windows&logoColor=white)
![React](https://img.shields.io/badge/UI-React_19-61DAFB?logo=react&logoColor=black)

Canias ERP **TROIA CODE EXECUTION TRACE** dosyalarını Notepad++ içinde, sağa yerleşen bir panelde analiz eder.

Analiz arayüzü ayrı bir repodur: **TroiaLens web** uygulaması. Bu repoya `web/` klasöründe git submodule olarak bağlıdır.
Parser, analiz kuralları, trace formatı ve gömülü mod mesajları web reposunun `README.md` dosyasında anlatılır.
Bu repo yalnızca Notepad++ tarafını içerir: C# eklenti, paketleme ve kurulum.

## Ekran görüntüleri

Görüntüler web reposundaki uydurma örnek trace ile alındı.

![Özet paneli](docs/screenshots/npp-light-overview.png)

| | |
|---|---|
| ![Satır detayı](docs/screenshots/npp-light-detail.png) | ![Koyu mod, çağrı ağacı](docs/screenshots/npp-dark-tree.png) |
| Satır detayı; editör aynı satırda (Ln 45) | Koyu mod; çağrı ağacında seçilen satır editörde |
| ![Koyu mod, özet](docs/screenshots/npp-dark-overview.png) | |
| Koyu mod, özet | |

## Davranış

- `.trace` uzantılı bir dosya aktif olunca panel kendiliğinden açılır ve analiz başlar.
- Trace olmayan bir sekmeye geçince otomatik açılan panel gizlenir. Paneli menüden elle açtıysanız açık kalır.
  Bu durumda trace olmayan dosyada "Bu bir Troia trace dosyası değil" mesajı görünür.
- Panelde bir satıra tıklayınca editör imleci o satıra gider. Detaydaki **Editörde göster** odağı da editöre verir.
- Dosya kaydedilince yeniden analiz edilir. Notepad++ koyu modundaysa panel de koyu açılır.
- Bir trace sekmesinde paneli kapatırsanız, o sekme için bir daha otomatik açılmaz.
- Notepad++ kapanırken panel açıksa, bir sonraki açılışta Notepad++ paneli geri getirir.

Menü: **Eklentiler > TroiaLens**

| Komut | |
|---|---|
| TroiaLens paneli | Paneli göster / gizle |
| Aktif dosyayı analiz et | Uzantıya bakmadan analiz eder |
| Trace dosyalarında paneli otomatik aç | Açık / kapalı |
| Sekme değişince yeniden analiz et | Panel açıkken her sekme değişiminde analiz |

Ayarlar `plugins\config\TroiaLens.ini` içinde:

| Anahtar | Varsayılan | |
|---|---|---|
| `AutoOpen` | `1` | Trace uzantılı dosyada paneli otomatik aç |
| `AnalyzeOnTabSwitch` | `1` | Panel açıkken her sekme değişiminde analiz et |
| `Extensions` | `.trace` | Trace sayılan uzantılar, `;` ile ayrılır |
| `DevTools` | `0` | `1` ise panelde F12 geliştirici araçları açılır |

Uzantı dışında, ilk satırı `TROIA CODE EXECUTION TRACE` olan ya da `▎` içeren dosyalar da trace sayılır.

## Gereksinimler

- Notepad++ x64 (8.4+; 8.9.8 ile test edildi)
- Microsoft Edge WebView2 Runtime (Windows 11'de hazır gelir)
- Derlemek için: .NET SDK, .NET Framework 4.8 targeting pack ve SDK araçları (`ildasm.exe`), Node.js 22.12+

## Kaynağı alma

```powershell
git clone --recurse-submodules <bu repo>
# ya da klonladıktan sonra:
git submodule update --init
```

Web arayüzünün yeni sürümünü almak için:

```powershell
git submodule update --remote web
git add web
git commit -m "web: güncelle"
```

`web/` klasörü web reposunun ([ufukt51/TroiaLens](https://github.com/ufukt51/TroiaLens)) kendisidir: kendi git
geçmişi ve remote'u vardır. Web arayüzünü doğrudan burada geliştirebilirsiniz:

```powershell
cd web
git switch main                  # submodule ilk açılışta belirli bir commit'te durur; çalışmadan önce dala geçin
# ... değişiklik, npm test ...
git commit -am "..."
git push                         # web reposuna (public)
cd ..
git add web                      # eklenti reposu web'in bu sürümünü kullansın
git commit -m "web: güncelle"
git push                         # eklenti reposuna (private)
```

## Derleme ve kurulum

```powershell
.\build.ps1          # web + eklenti -> out\TroiaLens\ ve out\TroiaLens-<sürüm>-x64.zip
.\build.ps1 -SkipWeb # web\dist hazırsa sadece eklenti
.\install.ps1        # Notepad++'ı kapatın; gerekirse yönetici izni ister
.\install.ps1 -NppDir D:\Apps\Npp   # başka (ör. taşınabilir) kurulum
```

Kurulum klasörü:

```
Notepad++\plugins\TroiaLens\
  TroiaLens.dll
  Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.WinForms.dll, WebView2Loader.dll
  web\            (web\dist)
```

## Mimari

```
Notepad++ ──(Scintilla mesajları)──> TroiaLens.dll (C#, .NET Framework 4.8, x64)
                                       └─ Dock panel (WinForms) ─> WebView2 ─> https://troialens.local/index.html
                         <──── gotoLine ────────────── window.chrome.webview.postMessage
                         ───── loadUrl / theme ──────> PostWebMessageAsJson
```

Neden bu yol: parser, analiz ve tüm görünümler web uygulamasında hazır ve test edilmiş. Eklenti yalnızca ince bir
köprüdür; web tarafındaki her iyileştirme `build.ps1` ile eklentiye de geçer.

```
src/TroiaLens/
  Npp/Exports.cs          Notepad++'ın çağırdığı 6 native export (isUnicode, setInfo, getFuncsArray, messageProc, getName, beNotified)
  Npp/Interop.cs          NPPM_ / SCI_ / NPPN_ sabitleri, NppData, tTbData, Win32
  Npp/FuncItems.cs        Menü komutları için native FuncItem dizisi
  Plugin.cs               Menü, bildirimler, aktif metni okuma, trace tespiti, satıra gitme
  PanelForm.cs            Dock panel + WebView2, sayfa ile mesajlaşma
  Settings.cs             TroiaLens.ini
tools/DllExport.ps1       Derleme sonrası native export ekler
build.ps1, install.ps1    Paketleme ve kurulum
web/                      Submodule: TroiaLens web arayüzü
```

### Native export'lar

Notepad++ eklentileri native DLL export'larıyla çağrılır. `[DllExport]` ile işaretli statik metotlar derlemeden sonra
dışa açılır: `tools/DllExport.ps1` derlenmiş assembly'yi `ildasm` ile IL'e çevirir, öznitelik satırını
`.export [n] as <ad>` ile değiştirir, `.corflags` değerini sıfırlar ve `ilasm /x64` ile yeniden derler.
`TroiaLens.csproj` içindeki `ExportNativeEntryPoints` hedefi bunu `CoreCompile` sonrasında, assembly değiştiyse çalıştırır.

`Exports` sınıfı WebView2 tiplerine doğrudan dokunmaz: statik kurucusu önce bağımlılıkları eklenti klasöründen
yükleyen bir `AssemblyResolve` işleyicisi kaydeder.

### Panel ve WebView2

- Panel `NPPM_DMMREGASDCKDLG` ile kaydedilir. `dlgID` panel komutunun indeksidir; Notepad++ açılışta paneli bu
  komutla geri getirir. Panelin kendi kapatma düğmesi `WM_NOTIFY / DMN_CLOSE` ile bildirilir.
- WebView2 kullanıcı verisi: `plugins\config\TroiaLens\WebView2` (Notepad++ kurulum klasörü yazılamaz olabilir).
- Arayüz `SetVirtualHostNameToFolderMapping("troialens.local", <eklenti>\web)` ile açılır. `file://` kullanılmaz:
  modül Web Worker `file://` üzerinden yüklenmez.
- Trace metni diske yazılmaz. Sayfa `https://troialens-data.local/<token>/<ad>` adresini `fetch` eder; eklenti bu
  isteği `WebResourceRequested` ile bellekten yanıtlar. Token her analizde değişir.
- `WebView2Loader.dll` eklenti klasöründen yüklenir (`CoreWebView2Environment.SetLoaderDllFolderPath`).
- Tema: `NPPM_ISDARKMODEENABLED` sayfaya `?theme=` ve `theme` mesajıyla geçer; `NPPN_DARKMODECHANGED` ile güncellenir.

### Aktif metni okuma ve satıra gitme

- Aktif editör `NPPM_GETCURRENTSCINTILLA`, metin `SCI_GETCHARACTERPOINTER` + `SCI_GETLENGTH` ile byte olarak okunur.
- `SCI_GETCODEPAGE` 65001 ise metin UTF-8'dir; değilse sistem ANSI kod sayfası (Türkçe Windows'ta 1254) ile
  çözülüp UTF-8'e çevrilir.
- Sayfanın gönderdiği satır, parser'ın saydığı satırdır (`\r?\n` ile bölünmüş). Scintilla tek başına CR'yi de satır
  sonu sayar; bu yüzden panelde görünen satır numarası ile Notepad++'ın satır numarası farklı olabilir. Eklenti her
  analizde `\n` konumlarını tutar, satırı byte konumuna çevirir ve `SCI_GOTOPOS` kullanır. İmleç her zaman doğru
  satıra gider.
- Aynı tampon aynı uzunluktaysa sekme değişiminde yeniden analiz edilmez; panel durumu (sekme, açık detay) korunur.

### Host ↔ sayfa mesajları

| Yön | Mesaj |
|---|---|
| sayfa → host | `{ type: 'ready' }`, `{ type: 'gotoLine', line, focus }`, `{ type: 'reload' }` |
| host → sayfa | `{ type: 'theme', dark }`, `{ type: 'loadUrl', name, url }`, `{ type: 'notTrace', name }` |

Host, metni ve temayı ancak `ready` geldikten sonra gönderir; sayfa yeniden yüklenirse son durumu tekrar yollar.

## Test

### Web arayüzü

Web reposunda `npm test` (parser regresyon testi) ve `npm run dev` ile tarayıcıda kontrol. Ayrıntılar web reposunun README'sinde.

### Eklenti

Gerçek kurulumu bozmadan denemek için:

1. Notepad++ klasörünü (plugins hariç) geçici bir yere kopyalayın, içine boş `doLocalConf.xml` koyun (taşınabilir mod).
2. `.\install.ps1 -NppDir <klasör>`
3. `notepad++.exe -multiInst -nosession <dosya.trace>`

Paneli Playwright veya DevTools ile incelemek için Notepad++'ı
`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` ortam değişkeniyle başlatın ve
`chromium.connectOverCDP('http://127.0.0.1:9333')` ile bağlanın.

Kontrol listesi:

- Büyük (4 MB, 140 bin satır) bir trace açıkken panel analizi 2 saniye içinde gösteriyor.
- Panelde bir SQL satırına tıklayınca editör doğru satıra gidiyor.
- Sekme değişince panel yeni dosyayı gösteriyor; trace olmayan dosyada otomatik açılan panel gizleniyor,
  elle açılan panelde "Bu bir Troia trace dosyası değil" mesajı çıkıyor.
- Notepad++ koyu modundayken panel koyu açılıyor.
- Panel kapatılıp açılınca ve Notepad++ yeniden başlatılınca hata çıkmıyor.
- İnternet bağlantısı olmadan çalışıyor (tüm dosyalar yerel).

## İleride

- Trace dosyaları için UDL (User Defined Language): `[N ms]` süreleri, `SELECT` / `IF` / `LOOP` anahtar kelimeleri,
  scope satırları farklı renklerde.
- Function List desteği: scope açılış satırlarını (`▎`) listeleyen bir parser tanımı.
- Panelde odak varken Notepad++ kısayollarının (Ctrl+C vb.) sayfaya ulaştığı doğrulanmadı.
