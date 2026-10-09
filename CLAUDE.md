# CLAUDE.md — EasyPhoneWebcam (PhoneCam)

Prywatna aplikacja dla jednego użytkownika: obraz z telefonów przez lokalne Wi-Fi → PC z Windows 11 → docelowo osobne kamery wirtualne (OBS, Teams). Wzorzec UX: aplikacja desktopowa w stylu iVCam/DroidCam — uruchamiasz, serwer startuje sam, wybierasz telefon, skanujesz kody QR. Komunikacja z użytkownikiem, UI, komunikaty błędów i dokumentacja po polsku; identyfikatory i komentarze w kodzie po angielsku.

## Układ repozytorium

Kod żyje w `outputs/PhoneCam/` (pozostałość po generowaniu w innym narzędziu; katalog `work/` zawiera tylko artefakty testowe .raw/.h264 i nie jest częścią aplikacji). Wszystkie polecenia poniżej uruchamiaj z `outputs/PhoneCam/`.

| Ścieżka | Rola |
|---|---|
| `src/PhoneCam.Desktop` | Aplikacja WPF (.NET 10, `net10.0-windows`) + wbudowany Kestrel (`FrameworkReference Microsoft.AspNetCore.App`) |
| `src/PhoneCam.Desktop/MainWindow.*` | Ekran startowy (wybór iOS/Android), ekran iOS z dwoma kodami QR, status sieci/serwera/połączenia |
| `src/PhoneCam.Desktop/PreviewWindow.*` | Osobne okno tylko z obrazem (najnowsza klatka z `SlotFeed`); otwiera się samo, gdy slot zaczyna nadawać |
| `src/PhoneCam.Desktop/Hosting/` | `PhoneCamServer` (orkiestracja), `LocalCertificates` (PKI), `PhoneWebServer` (Kestrel), `MediaMtxProcess` (proces + generowany config), `MediaMtxApi` (status slotu), `FirewallRules`, `NetworkCandidates`, `IphoneUsb` (wykrywanie iPhone'a na kablu), `Elevated` (skrypt przez UAC), `ChildProcessJob`, `AppPaths`/`Ports`/`Slots`, `AppSettings` |
| `src/PhoneCam.Desktop/Video/FfmpegStreamDecoder.cs` | RTSP z MediaMTX → FFmpeg (obrót wg telefonu + przycięcie do 16:9) → BGR24 1280×720, potrójny bufor bez blokad |
| `src/PhoneCam.Desktop/Video/SlotFeed.cs` | Dekoder slotu (start przy nadawaniu, restart przy obrocie/awarii) → każda klatka od razu do kamery wirtualnej (`FrameDecoded`) i do podglądu; bez obrazu plansza `NoSignalCard` |
| `src/PhoneCam.Desktop/Video/VirtualCamera.cs` | P/Invoke do `PhoneCam.VirtualCamera.dll` (nadawca: `pcCreateCamera`/`pcSendFrame`/`pcIsConnected`) |
| `src/PhoneCam.VirtualCamera` | Natywny filtr DirectShow (C++, x64, statyczne CRT) = kamera „PhoneCam iPhone” dla OBS/Teams/Zoom/przeglądarek. Oparty na softcam (MIT, commit 8f545e2) + DirectShow BaseClasses (MIT); zmiany oznaczone komentarzem `PhoneCam:`. Budowany przez target `BuildVirtualCamera` w `PhoneCam.Desktop.csproj` |
| `src/PhoneCam.Desktop/Video/FfmpegH264Decoder.cs` | Stary dekoder Annex-B przez stdin (ścieżka UDP; używany tylko przez VideoChecks) |
| `src/PhoneCam.Desktop/Web/phonecam.html` | Strona nadawcza na telefon (getUserMedia → WebRTC/WHIP, H.264, bez audio) |
| `src/PhoneCam.Streaming` | Biblioteka bez zależności od Windows: `UdpRtpReceiver`, `RtpH264Depacketizer` (RFC 6184) — ścieżka natywna, obecnie poza UI |
| `tools/PhoneCam.HostingChecks` | 17 kontroli hostingu: PKI + name constraints (też adres hotspotu USB), wykrywanie USB (PnP, trasa), kamera wirtualna przez FFmpeg dshow (orientacja, plansza, brak nadawcy), Kestrel, MediaMTX/TLS/API, publikacja RTSP → dekoder, przycięcie pionu i kierunek obrotu, endpoint orientacji |
| `tools/PhoneCam.ProtocolChecks` | 7 kontroli RTP/UDP |
| `tools/PhoneCam.VideoChecks` | Test end-to-end ścieżki UDP: obraz → H.264 → RTP → 2×UDP → 2×FFmpeg |
| `tools/PhoneCam.StreamSimulator` | Generator syntetycznych pakietów RTP |
| `tools/PhoneCam.WebTest` | **Przestarzałe** — skrypt PowerShell + CertShare sprzed aplikacji desktopowej. Nie rozwijać; aplikacja zastępuje go w całości |
| `docs/design.md` | Pełny projekt techniczny, etapy i dziennik implementacji |

Stan użytkownika (poza repo): `%LOCALAPPDATA%\PhoneCam\` — `cert\` (CA .cer, server.crt/.key), `mediamtx\mediamtx.yml`, `logs\` (mediamtx.log, crash.log), `settings.json`. Klucz prywatny CA jest w magazynie kluczy CNG użytkownika pod nazwą „PhoneCam Local CA” (nieeksportowalny).

## Polecenia

```powershell
dotnet restore .\PhoneCam.slnx --configfile .\NuGet.Config
dotnet build .\PhoneCam.slnx --no-restore
dotnet run --project .\src\PhoneCam.Desktop                                                       # aplikacja
dotnet .\tools\PhoneCam.HostingChecks\bin\Debug\net10.0-windows\PhoneCam.HostingChecks.dll        # oczekiwane 17/17 (kamera musi być zarejestrowana)
dotnet .\tools\PhoneCam.ProtocolChecks\bin\Debug\net10.0\PhoneCam.ProtocolChecks.dll              # oczekiwane 7/7
dotnet .\tools\PhoneCam.VideoChecks\bin\Debug\net10.0-windows\PhoneCam.VideoChecks.dll            # oczekiwane 1/1
```

Nie ma frameworka testów — testy to programy konsolowe wypisujące `PASS`/`FAIL`. HostingChecks używa tymczasowego katalogu danych i osobnego klucza CA (`AppPaths.Root`, `LocalCertificates.CaKeyName` są nadpisywalne właśnie dla nich), nie rusza zapory; wymaga prywatnego IPv4 i wolnych portów PhoneCam (zamknij aplikację przed uruchomieniem). Build ma przechodzić z 0 ostrzeżeń.

Zależności binarne: FFmpeg/FFplay z NuGet `FFmpegDotNet.bin.winx64` (→ `bin\...\ffmpeg\win-x64\`). MediaMTX pobiera target MSBuild `FetchMediaMtx` w `PhoneCam.Desktop.csproj` (wersja i SHA-256 przypięte, cache w `obj\mediamtx\<wersja>\`) i kopiuje do `bin\...\mediamtx\`. Aktualizacja MediaMTX = zmiana `MediaMtxVersion` + `MediaMtxSha256`. Kamera wirtualna to C++: build wymaga Visual Studio z obciążeniem C++ (MSVC x64) — target `BuildVirtualCamera` znajduje MSBuild z VS przez `vswhere`, buduje `PhoneCam.VirtualCamera.vcxproj` tylko przy zmianie źródeł i kopiuje DLL obok exe. Projektu C++ nie ma w `PhoneCam.slnx` (`dotnet build` nie umie C++).

## Jak działa aplikacja

1. Start: pojedyncza instancja (mutex) → sprawdzenie reguł zapory (brak → jedno okno UAC) → wybór adresu IP (ostatnio używany interfejs albo najlepszy wg punktacji) → `PhoneCamServer.StartAsync(ip)`.
2. `PhoneCamServer`: certyfikat serwera dla IP (CA tworzone raz) → Kestrel (HTTP 8787 tylko `/PhoneCam-RootCA.cer`, HTTPS 8443 tylko `/iphone` i `/android`) → MediaMTX z wygenerowanym configiem. Zmiana IP w liście = restart całości, nowy certyfikat serwera, ten sam CA.
3. Ekran iOS: QR 1 = `http://IP:8787/PhoneCam-RootCA.cer`, QR 2 = `https://IP:8443/iphone`.
4. Telefon publikuje WebRTC/WHIP do MediaMTX (`:8889/<slot>/whip`, media UDP 8189). `MainWindow` co 1 s pyta API MediaMTX (`127.0.0.1:9997/v3/paths/get/<slot>`): gdy `ready` → otwiera `PreviewWindow`, który dekoduje `rtsp://127.0.0.1:8554/<slot>`. Status w głównym oknie: kodek, rozdzielczość źródła, fps (z dekodera podglądu), Mb/s (z `bytesReceived`), czas połączenia.
5. Wszystkie procesy potomne (MediaMTX, FFmpeg) są w job object z kill-on-close — giną razem z aplikacją, nawet po crashu.

Sloty to stałe ścieżki `iphone` i `android`: `Slots` w `AppPaths.cs` (routing Kestrela i config MediaMTX) oraz `<select>` w `phonecam.html`.

Porty: 8443 TCP (strona HTTPS), 8787 TCP (pobranie CA), 8889 TCP (WHIP), 8189 UDP (WebRTC media) — LAN; 8554 (RTSP) i 9997 (API) — tylko loopback. Stała ścieżka UDP 5004/5006 nie jest już w UI.

iPhone przez USB = Osobisty hotspot po kablu, nie natywna aplikacja: sterownik Apple `netaapl64` tworzy kartę „Apple Mobile Device Ethernet” w 172.20.10.0/28 (iPhone .1, PC zwykle .2) i Safari łączy się przez nią tak samo jak przez Wi-Fi — strona, WHIP i MediaMTX bez zmian, a ICE idzie kablem, bo MediaMTX ogłasza tylko wybrany adres (`webrtcAdditionalHosts`). `IphoneUsb.Probe()` (co 1 s) odróżnia stany po funkcjach USB iPhone'a (PnP `USB\VID_05AC&PID_12*`): brak MI_01 = brak oprogramowania Apple, brak MI_02 = hotspot wyłączony, MI_02 bez karty = brak sterownika `netaapl64`, karta bez IPv4, dostępny, aktywny. Pojawienie się adresu USB przełącza serwer na kabel (`NetworkChange` + 1,5 s debounce), chyba że trwa nadawanie. Gdy Windows kieruje internet PC przez iPhone'a (dane komórkowe), przycisk podnosi metrykę karty (UAC).

Kamera wirtualna: `MainWindow` tworzy przy starcie `SlotFeed` dla iPhone'a; ten od razu tworzy kamerę („PhoneCam iPhone”, indeks 0 w tabeli `g_cameras` w `PhoneCam.VirtualCamera.cpp`) i co 100 ms wysyła planszę „brak sygnału”, gdy od 0,5 s nie było klatki na żywo. Gdy slot nadaje, `SlotFeed` uruchamia dekoder, a każdą zdekodowaną klatkę (1280×720 BGR24, już obróconą i przyciętą) kopiuje do pamięci współdzielonej `PhoneCam/iphone/SharedMemory` w wątku dekodera — bez kolejki. Filtr DirectShow w procesie OBS/Teams czyta ją i podaje RGB24 1280×720, deklaruje 60 fps, znaczniki czasu z zegara grafu. Bez PhoneCam kamera też się otwiera (czarny obraz albo przyciemniona ostatnia klatka). Kamera działa niezależnie od okna podglądu. Rejestracja: `regsvr32` (HKLM, UAC) razem z regułami zapory w jednym skrypcie `setup.cmd` (`VirtualCameraRegistration`, `Elevated`).

Orientacja: Safari obraca klatki zgodnie z orientacją *interfejsu*, która przy blokadzie orientacji iOS zostaje pionowa — telefon leżący poziomo wysyła wtedy pionową klatkę z obrazem bokiem. Strona telefonu czyta czujnik (`deviceorientation`, zgoda iOS w geście „Uruchom kamerę”), liczy obrót = orientacja fizyczna − orientacja interfejsu (odwrotnie dla przedniej kamery) i wysyła go `POST /api/orientation/<slot>` `{"rotation":0|90|180|270}` (stopnie przeciwnie do wskazówek zegara). PC obraca w FFmpeg (`FfmpegStreamDecoder.BuildFilter`) i zawsze wypełnia kadr 16:9; pion jest przycinany do środka. Zmiana obrotu = restart dekodera podglądu (~1 s).

## Ustalenia produktowe (nie zmieniać bez pytania)

- Wi-Fi w LAN albo iPhone przez kabel USB (Osobisty hotspot po USB, ta sama strona w Safari); bez chmury, bez STUN/TURN. Natywna aplikacja iOS (usbmux jak iVCam) odrzucona na tym etapie: wymaga Maca/CI macOS i konta Apple Developer.
- Maks. 2 telefony naraz: iPhone 15 Pro (cel 720p60, potem 1080p60 po pomiarach; priorytet: stałe 60 fps i niskie opóźnienie) i Xiaomi Mi 9T (cel 720p30; priorytet: stabilność). **Obecny etap: tylko iOS**; przycisk Android jest nieaktywny („wkrótce”).
- Każdy telefon = niezależne źródło, niezależny dekoder, osobna kamera wirtualna. Nigdy nie łączyć w jeden obraz; awaria jednego slotu nie może blokować drugiego.
- Główne okno = status; podgląd obrazu = osobne okno zawierające wyłącznie obraz.
- Każdy telefon = kamera DirectShow (OBS „Urządzenie przechwytywania wideo”, Teams, Zoom, Discord, Chrome/Edge), jak OBS Virtual Camera. Kamera Media Foundation (aplikacja Aparat Windows) świadomie pominięta. Brak sygnału → plansza z nazwą kamery i komunikatem.
- Obraz wyjściowy zawsze poziomy 16:9. Typowe użycie: telefon poziomo na statywie, często z blokadą orientacji. Telefon w pionie → przycięcie do środkowego kadru 16:9 (bez czarnych pasów).
- Zapora: aplikacja sama dodaje reguły przez jedno UAC: LAN w profilu prywatnym + USB w profilu publicznym zawężone do kart przewodowych i 172.20.10.0/28. MediaMTX: pobierany przy buildzie, nie przy uruchomieniu. IP: automatyczny wybór + lista do zmiany.
- MVP tylko wideo; audio poza zakresem. 60 fps to cel pomiarowy, nie gwarancja.
- Stanowisko deweloperskie to wyłącznie Windows: brak Maca/Xcode i Android SDK. Telefony obsługujemy przez przeglądarkę, nie natywne aplikacje.

## Zasady pracy

- Rozwój pionowymi etapami (lista w `docs/design.md`): każdy etap kończy się czymś uruchamialnym i sprawdzonym; nie przeskakujemy dalej, póki bieżący nie spełnia kryteriów. Przy niejasnych wymaganiach dopytaj użytkownika przed implementacją.
- Niskie opóźnienie > płynność bufora: zawsze pokazujemy najnowszą klatkę i odrzucamy starsze (potrójny bufor w `FfmpegStreamDecoder`), nie budujemy kolejek. Uszkodzone klatki pomijamy.
- Po zakończeniu etapu dopisz wpis do „Dziennik implementacji” w `docs/design.md` (data, co zrobiono, wynik testów, czego nie zweryfikowano). Uczciwie odróżniaj „sprawdzone automatycznie” od „wymaga testu z telefonem” — testów z telefonem nie da się wykonać bez użytkownika.
- `README.md` jest dla użytkownika; aktualizuj go, gdy zmienia się sposób uruchamiania.

## Pułapki

- `mediamtx.yml` jest generowany przy każdym starcie — zmiany konfiguracji MediaMTX rób w `MediaMtxProcess.BuildConfig`, nie w pliku.
- CA ma krytyczne name constraints (tylko 10/8, 172.16/12, 192.168/16 + DNS `phonecam.invalid`). Adres spoza tych zakresów nie dostanie ważnego certyfikatu. Certyfikat serwera nie ma CN — IP jest wyłącznie w SAN.
- Utrata klucza CNG lub pliku CA (np. wyczyszczenie `%LOCALAPPDATA%\PhoneCam`) = nowe CA = ponowna instalacja i zaufanie certyfikatu na telefonie. iOS wymaga dodatkowo włączenia pełnego zaufania dla CA.
- iOS odrzuca certyfikaty TLS ważne > 825 dni; serwerowy ma 397 dni i jest odnawiany automatycznie na 30 dni przed końcem.
- Kestrel na Windows (SChannel) nie przyjmie efemerycznego klucza z PEM — `LocalCertificates.ForSchannel` robi round-trip przez PKCS#12.
- `webrtcAllowOrigins` jest przypięte do `https://<ip>:8443` — strona z innego originu nie połączy się z WHIP.
- Reguły zapory są per program i mają w nazwie hash ścieżki exe. Przeniesienie aplikacji (np. Debug → Release) = nowe reguły = ponowne UAC. Reguły LAN działają tylko w profilu prywatnym; sieć oznaczona jako publiczna jest wykrywana (COM Network List Manager) i pokazywane jest ostrzeżenie (poza kartą USB iPhone'a, którą Windows zwykle klasyfikuje jako publiczną — obejmują ją reguły „PhoneCam USB”). Dodanie reguł USB sprawiło, że istniejące instalacje raz jeszcze pokazują UAC.
- Projekty WPF z `ImplicitUsings` nie importują `System.IO` — dodawaj `using System.IO;` jawnie.
- Hotspot iPhone'a wymaga włączonych danych komórkowych i operatora, który na niego pozwala; bez tego nie ma karty USB z adresem. Adres PC w 172.20.10.0/28 może się zmienić przy ponownym podłączeniu → nowy certyfikat serwera i nowy kod QR 2 (CA bez zmian, mieści się w 172.16/12).
- Funkcja sieciowa iPhone'a (MI_02) istnieje tylko w konfiguracji USB, o którą telefon prosi przy włączonym Osobistym hotspocie. `AppleLowerFilter` (sterownik `appleusb.inf` z Apple Devices) co 5 s pyta o nią telefon i ponownie wylicza urządzenie; domyślnie `OriginalConfigurationValue=2` = tylko MI_00 (MTP) + MI_01 (usbmux). **Apple Devices nie zawiera sterownika `netaapl64` („Apple Mobile Device Ethernet”) dla MI_02** — pochodzi z Windows Update / Microsoft Update Catalog (1.8.5.1, 2017). Łącze (karta „Up” z DHCP 172.20.10.x) wstaje tylko, gdy kabel podłącza się przy już włączonym hotspocie; przełączenie hotspotu przy podłączonym kablu zostawia kartę „Disconnected” do ponownego podłączenia (sprawdzone 2026-10-09). Kartę rozpoznajemy po opisie „Apple Mobile Device Ethernet”; przy problemach sprawdź `Get-PnpDevice -PresentOnly | ? InstanceId -match VID_05AC` i `Get-NetAdapter`.
- Kamera wirtualna: rejestracja wskazuje DLL obok exe, więc Debug ↔ Release albo przeniesienie aplikacji = ponowne UAC. OBS/Teams trzymają DLL otwartą — rebuild przy otwartym OBS kończy się błędem kopiowania DLL; zamknij aplikację, która używa kamery. Jedna kamera = jeden nadawca: druga kopia PhoneCam albo HostingChecks przy otwartej aplikacji nie utworzy kamery. Gdy aplikacja trzyma bufor poprzedniej sesji, `SlotFeed` ponawia tworzenie co 2 s.
- Vendorowany kod w `src/PhoneCam.VirtualCamera` (softcam + BaseClasses): BaseClasses nie kompilują się z `/permissive-` (ConformanceMode=false), `wxdebug.cpp` ma wyciszone C4005. Zmiany względem softcam są oznaczone `PhoneCam:` — stały format per kamera, tabela kamer, nazwy pamięci per kamera, ochrona przed pustym `m_screenshot`, znaczniki czasu ze `StreamTime`. Do testów bez UAC można zarejestrować filtr w HKCU (`Software\Classes\CLSID\…` + `Instance` w kategorii `{860BB310-…}`), ale wpisy HKCU przesłaniają HKLM — usuń je po teście.
- Tylko x64: 32-bitowe aplikacje nie zobaczą kamery.
