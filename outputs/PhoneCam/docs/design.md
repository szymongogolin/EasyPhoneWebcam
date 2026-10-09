# Projekt techniczny

## Ustalenia produktu

- To aplikacja prywatna dla jednego użytkownika; nie planujemy publikacji ani obsługi wielu kont.
- Komputerem odbierającym jest PC z Windows 11.
- Telefony łączą się z PC przez lokalne Wi-Fi. iPhone może też użyć kabla USB jako Osobistego hotspotu po USB: to ta sama strona w Safari i ten sam potok, tylko inna sieć. Natywna aplikacja iOS z usbmux (jak iVCam) wymagałaby Maca lub CI z macOS i konta Apple Developer, więc na tym etapie jej nie robimy.
- Maksymalnie dwa telefony wysyłają obraz jednocześnie: iPhone 15 Pro i Xiaomi Mi 9T.
- Każdy telefon ma być niezależnym źródłem i osobną kamerą wirtualną w Windows, wybieraną oddzielnie w programach takich jak OBS lub Teams.
- iPhone: priorytetem jest stałe 60 fps przy najniższym opóźnieniu. Zaczynamy od 720p60, a 1080p60 włączymy po pomiarach.
- Android: stabilny obraz jest ważniejszy niż 60 fps; pierwszy profil to 720p30, jeśli urządzenie go udostępni.
- MVP przesyła tylko obraz; audio/mikrofon nie wchodzi do pierwszego etapu.

## Zasady rozwoju

Rozwijamy pionowymi etapami. Każdy etap ma własny wynik możliwy do uruchomienia i kryteria sprawdzenia przed rozpoczęciem następnego. Nie przechodzimy do kamer wirtualnych, dopóki odbiór i podgląd obu strumieni nie będą działały niezależnie.

### Etapy i kryteria akceptacji

0. **Szkielet Windows** — aplikacja uruchamia panel, nasłuchuje osobno na UDP 5004 i 5006, pokazuje adres komputera, liczbę pakietów oraz stan obu wejść. Generator testowy potrafi wysyłać równoległe strumienie syntetyczne. Biblioteka odbioru ma automatyczne sprawdzenie dwóch odbiorników na dwóch portach równocześnie. Test interfejsu na rzeczywistym oknie Windows nadal jest do wykonania.
1. **RTP/H.264** — rozpoznawanie RTP, składanie NAL i klatek, wykrywanie luk w numeracji pakietów, pomijanie uszkodzonych klatek. Sprawdzenie obejmuje Single NAL, STAP-A, FU-A, błędny nagłówek, utratę pakietu, zawinięcie numeru sekwencyjnego i dwa odbiorniki UDP równolegle. Aktualnie 7/7 kontroli przechodzi.
2. **Podgląd Windows** — dwa niezależne procesy FFmpeg dekodują H.264 do klatek 1280×720; panel pokazuje najnowszą klatkę każdego źródła. FFmpeg wybiera sprzętowe dekodowanie, gdy jest dostępne (`-hwaccel auto`), i używa dekodera programowego jako rezerwy. Sprawdzenie: prawdziwy obraz testowy zakodowany do H.264 przechodzi równocześnie przez dwa odbiorniki UDP/RTP i dwa dekodery; utrata/awaria jednego portu nie blokuje startu drugiego.
3. **Test przeglądarkowy WebRTC** — własna strona HTTPS wybiera kamerę/profil i wysyła H.264 przez lokalne Wi-Fi do dwóch ścieżek MediaMTX. Kryterium: sprawdzić każdy telefon osobno, potem oba naraz; zmierzyć rzeczywiste FPS i opóźnienie oraz wykonać 30-minutowy test iPhone 720p60.
4. **Panel Windows dla WebRTC** — podłączyć dwa kanały RTSP z MediaMTX do dwóch podglądów WPF, pokazać stan, parametry obrazu i błędy niezależnie dla każdego telefonu.
5. **Kamery wirtualne** — dwie niezależne kamery DirectShow (decyzja z 2026-10-09: OBS, Teams, Zoom, przeglądarki; kamera Media Foundation dla aplikacji Aparat pominięta). Sprawdzenie: osobne wejścia widoczne w OBS i przetestowane równocześnie. Stan: kamera iPhone gotowa, Android dojdzie z etapem Android.
6. **Długi test i strojenie** — stabilność, wznowienie po utracie Wi-Fi, buforowanie, pomiary opóźnienia i utraty klatek w docelowej sieci.

Jeżeli pomiar etapu nie spełni kryterium, naprawiamy ten etap przed kontynuacją. Wyniki i znane ograniczenia dopisujemy do tego pliku.

## Dziennik implementacji

- **2026-10-02:** Utworzono rozwiązanie .NET 10 z WPF desktop, niezależną biblioteką odbioru RTP/H.264, konsolowymi kontrolami protokołu i generatorem UDP do sprawdzania obu wejść. `dotnet build PhoneCam.slnx` zakończył się bez ostrzeżeń; wszystkie 7 kontroli przechodzi, w tym równoczesny odbiór UDP na dwóch portach. Generator sprawdzono dla 60 fps przez sekundę: wysłał dokładnie 60 pakietów. Samo okno WPF nie było jeszcze sprawdzane interaktywnie.
- Brak obrazu na tym etapie jest oczekiwany: generator wysyła syntetyczne NAL-e, a odbiornik jeszcze ich nie dekoduje.
- **2026-10-02:** Dodano do panelu dwa niezależne dekodery FFmpeg, wybór sprzętowego dekodowania z programową rezerwą, prezentację klatek 1280×720 i osobną obsługę błędów portów. Podgląd scala aktualizacje do najnowszej dostępnej klatki, aby interfejs nie budował kolejki opóźniającej obraz. Test `PhoneCam.VideoChecks` tworzy wzorzec obrazu, koduje go H.264, pakuje do RTP, wysyła równolegle do dwóch lokalnych portów i potwierdza dekodowanie do dwóch buforów klatek 1280×720. Wynik: test obrazu 1/1, kontrole RTP/UDP 7/7, build 0 ostrzeżeń i 0 błędów. Okno WPF uruchamia się i odpowiada; nie wykonano jeszcze testu 60 fps z telefonem ani pomiaru opóźnienia.
- **2026-10-02:** Stan stanowiska do testów telefonicznych: dostępny jest wyłącznie Windows; brak Xcode/Maca oraz brak Android SDK, Android Studio i `adb`. Dodano przeglądarkowy nadajnik WebRTC/WHIP z własną mobilną stroną: osobne sloty, wybór przedniej/tylnej kamery, 720p/1080p, FPS, bitrate, podgląd i statystyki; strona żąda wyłącznie obrazu i nie pobiera audio. MediaMTX obsługuje dwa kanały (`iphone`, `android`), HTTPS i RTSP do lokalnego podglądu. Skrypt tworzy certyfikat CA/TLS z aktualnym IP, udostępnia CA do pobrania z telefonu, pobiera oficjalne wydanie MediaMTX v1.21.1 i weryfikuje SHA-256. Rzeczywistego połączenia telefonu nie można zweryfikować z tego stanowiska; użytkownik musi uruchomić skrypt, zaufać CA na telefonie i wykonać test. Obecny WPF nadal odbiera dotychczasowy RTP/UDP, więc etap następny to integracja wejść RTSP z panelem.

- **2026-10-09:** Aplikacja desktopowa w stylu iVCam/DroidCam, etap „połączenie iOS”. Panel WPF sam uruchamia lokalny serwer: wbudowany Kestrel (CA po HTTP 8787, strona telefonu po HTTPS 8443) i MediaMTX jako proces potomny. MediaMTX pobiera się przy buildzie (wersja i SHA-256 przypięte), a procesy potomne giną razem z aplikacją. Ekran startowy pozwala wybrać iOS lub Android (Android nieaktywny). Ekran iOS pokazuje dwa kody QR (certyfikat i strona kamery), instrukcję dla iOS oraz status połączenia z API MediaMTX: kodek, rozdzielczość, FPS, Mb/s i czas. Gdy iPhone zaczyna nadawać, otwiera się osobne okno podglądu z samym obrazem (RTSP → FFmpeg → WPF, potrójny bufor, portret w ramce letterbox); po zerwaniu połączenia dekoder próbuje się połączyć ponownie. PKI: CA tworzone raz, z kluczem w magazynie CNG użytkownika i krytycznymi name constraints (tylko prywatne zakresy IPv4). Przy zmianie IP wystawiany jest wyłącznie nowy certyfikat serwera, więc telefon instaluje CA tylko raz. Zapora: reguły per program w profilu prywatnym, dodawane jednym UAC. Wybór IP: punktacja interfejsów, lista do zmiany i ostrzeżenie o sieci publicznej. Wynik: build 0 ostrzeżeń, HostingChecks 9/9 (m.in. odrzucenie certyfikatu dla publicznego IP, TLS WHIP, publikacja RTSP → dekoder 1280×720, letterbox 720×1280), ProtocolChecks 7/7, VideoChecks 1/1. **Nie zweryfikowano:** samego okna WPF (UAC, kody QR, automatyczne otwieranie podglądu) ani testu z iPhone'em, w tym czy iOS akceptuje CA z name constraints. Panel nie odbiera już UDP 5004/5006; ta ścieżka zostaje w bibliotece i testach.
- **2026-10-09:** Użytkownik potwierdził działanie całego przepływu iOS z iPhone'em, w tym kodów QR, instrukcji i CA z name constraints. Poprawka orientacji: telefon leży poziomo z blokadą orientacji, więc Safari wysyła klatkę 720×1280 z obrazem bokiem. Strona telefonu czyta czujnik orientacji (zgoda iOS), liczy obrót względem orientacji interfejsu i wysyła go do PC (`POST /api/orientation/<slot>`). PC obraca obraz w FFmpeg i zawsze wypełnia kadr 16:9; telefon w pionie daje środkowy wycinek 16:9 zamiast czarnych pasów. Wynik: HostingChecks 12/12. Nowe kontrole sprawdzają przycięcie pionu, kierunek obrotu 90°/270° na kolorowym obrazie testowym i walidację endpointu. **Nie zweryfikowano z telefonem:** znaku obrotu wyliczanego z czujnika iOS (lewy/prawy poziom) ani przedniej kamery.
- **2026-10-09:** Użytkownik potwierdził poprawną orientację. Etap „iPhone przez kabel USB”. Rozpoznanie: aplikacje typu iVCam łączą się po USB przez usbmux (usługa Apple Mobile Device z iTunes lub Apple Devices) z natywną aplikacją na telefonie, a Safari nie ma dostępu do USB. Wybrano Osobisty hotspot po USB: sterownik Apple tworzy kartę „Apple Mobile Device Ethernet” w 172.20.10.0/28 i strona telefonu działa bez zmian. MediaMTX ogłasza tylko wybrany adres, więc WebRTC idzie kablem. Aplikacja wykrywa iPhone'a (PnP), sterownik (DriverStore) i kartę sieciową, pokazuje w panelu sieci stan z instrukcją (brak sterownika → przycisk do Apple Devices w Microsoft Store, hotspot wyłączony, dostępny, aktywny), przełącza serwer na kabel po pojawieniu się adresu (o ile nikt nie nadaje) i wraca na Wi-Fi po odłączeniu. Zapora dostała drugi zestaw reguł: profil publiczny, tylko karty przewodowe i 172.20.10.0/28, bo Windows zwykle uznaje sieć iPhone'a za publiczną. Gdy Windows kieruje internet PC przez iPhone'a, przycisk podnosi metrykę karty (UAC). Wynik: build 0 ostrzeżeń. Wywołania systemowe (lista urządzeń PnP, `GetBestInterface`, sonda) sprawdzone na PC bez iPhone'a: brak telefonu i sterownika wykryty poprawnie. Doszły 2 kontrole hostingu (certyfikat dla 172.20.10.2, wykrywanie USB), razem 14. **Nie zweryfikowano:** pełnego przebiegu HostingChecks (porty zajmowała otwarta aplikacja), niczego z iPhone'em na kablu: nazwy karty i sterownika po instalacji Apple Devices, klasyfikacji sieci, ICE w Safari przez hotspot ani zysku opóźnienia względem Wi-Fi.
- **2026-10-09:** Test z iPhone'em 15 Pro na kablu (Windows 11, Apple Devices 1.1540). Apple Devices instaluje tylko `appleusb.inf` 538: filtr `AppleLowerFilter` + usbccgp z konfiguracją, w której są wyłącznie MI_00 (MTP) i MI_01 (usbmux). Funkcja sieciowa MI_02 pojawiła się dopiero po włączeniu Osobistego hotspotu: filtr przełączył konfigurację. Windows Update sam doinstalował wtedy `netaapl64` 1.8.5.1 („Apple Mobile Device Ethernet”) w około 13 s. Łącze wstało dopiero po ponownym podłączeniu kabla przy włączonym hotspocie: sieć „Network”, kategoria publiczna, DHCP z iPhone'a. Przełączenie hotspotu przy podłączonym kablu kładzie łącze do następnego podłączenia. Poprawiona diagnostyka: zamiast szukać `netaapl*` w DriverStore (mylny komunikat „zainstaluj Apple Devices”) aplikacja patrzy na funkcje USB MI_01/MI_02. Metryka karty iPhone'a (1,1 Gb/s → 25) jest równa metryce Ethernetu PC, więc ostrzeżenie o danych komórkowych jest realne. Wynik: build 0 ostrzeżeń, HostingChecks 14/14. **Nie zweryfikowano jeszcze:** adresu 172.20.10.x w aplikacji, publikacji z Safari przez kabel (ICE) ani opóźnienia względem Wi-Fi.
- **2026-10-09:** Użytkownik potwierdził, że kamera po kablu USB działa prawidłowo. Etap „kamera wirtualna iPhone”, decyzje użytkownika: DirectShow (OBS, Teams, Zoom, przeglądarki), a bez sygnału plansza. Natywny filtr `src/PhoneCam.VirtualCamera` (C++, x64, statyczne CRT) zbudowany na softcam (MIT, commit 8f545e2) i DirectShow BaseClasses (MIT). Zmiany: tabela kamer (CLSID, nazwa „PhoneCam iPhone”, pamięć `PhoneCam/iphone`), stały format 1280×720 przy 60 fps (kamera otwiera się także bez PhoneCam), ochrona przed pustą ostatnią klatką, znaczniki czasu z zegara grafu. Ta sama DLL jest nadawcą (`pcCreateCamera`/`pcSendFrame`, P/Invoke) i filtrem. Dekodowanie przeniesione z okna podglądu do `SlotFeed`: każda klatka trafia do kamery w wątku dekodera bez kolejki, a bez obrazu feed wysyła planszę 10 razy na sekundę. Rejestracja `regsvr32` w HKLM idzie razem z regułami zapory w jednym UAC. Build: target MSBuild uruchamia MSBuild z Visual Studio (`vswhere`) tylko przy zmianie źródeł. Wynik: build 0 ostrzeżeń (także C++), HostingChecks 17/17 przy tymczasowej rejestracji w HKCU (potem usuniętej). Nowe kontrole odczytują kamerę przez FFmpeg dshow, jak OBS: obraz nie jest odwrócony ani lustrzany, plansza w spoczynku, obraz na żywo przy nadawaniu, otwarcie bez nadawcy. Planszę obejrzano na zrzucie. **Nie zweryfikowano:** rejestracji przez UAC w aplikacji, kamery w samym OBS, Teams i Zoomie oraz opóźnienia, które kamera dodaje w tych programach.

### Decyzje o zakresie

- Potwierdzone: pierwszy etap i MVP są wideo-only. Audio można rozważyć później jako osobne rozszerzenie.

## Kształt aplikacji

Aktualna aplikacja Windows to panel C#/WPF z dwoma odbiornikami i dekoderami RTP/H.264. Do rzeczywistych testów telefonów na stanowisku Windows dodano przeglądarkowy nadajnik WebRTC i serwer MediaMTX. Nie wymaga on kompilowania aplikacji iOS/Android.

```text
iPhone 15 Pro / Safari ─ getUserMedia ─ WebRTC/WHIP ─┐
                                                     ├─ Wi-Fi ─ MediaMTX ─ RTSP ─ FFplay/VLC preview
Xiaomi Mi 9T / Chrome ─ getUserMedia ─ WebRTC/WHIP ─┘                   ├─ WPF integration (next)
                                                                         └─ virtual cameras (later)
```

Strumienie pozostają niezależne. Każdy dostaje osobną ścieżkę (`iphone`, `android`), stan połączenia, dekoder i urządzenie wirtualne. Panel PC będzie pokazywać podgląd obu kamer i pozwoli uruchamiać je niezależnie.

## Transport

Pierwotny odbiornik RTP/UDP pozostaje użyteczny do testów i jako możliwy przyszły nadajnik natywny. Dla przeglądarki używamy WebRTC/WHIP, ponieważ Safari/Chrome udostępniają kamerę przez `getUserMedia`, a MediaMTX przyjmuje WebRTC i wystawia lokalny strumień RTSP. Na testowej ścieżce nie ma STUN/TURN; telefony muszą być w tej samej sieci i łączyć się bezpośrednio z adresem PC. WebRTC szyfruje połączenie medialne; HTTPS jest też potrzebny, aby przeglądarka uznała dostęp do kamery.

Konfiguracja jest ręczna: telefon otwiera lokalny adres PC i osobną ścieżkę `iphone` albo `android`. Później można dodać stronę startową z wyborem slotu i Bonjour/mDNS.

## Profile i sieć

- iPhone: start od 1280×720 przy 60 fps; 1920×1080 przy 60 fps jako tryb opcjonalny po pomiarach.
- Xiaomi: 1280×720 przy 30 fps jako cel początkowy. Nadajnik sprawdzi rzeczywiste formaty i zakresy Camera2, zamiast zakładać możliwości na podstawie modelu.
- WebRTC/WHIP koduje obraz do H.264; strona prosi o iPhone 720p60 i Xiaomi 720p30 oraz ustawia limit bitrate. Przeglądarka może te wartości skorygować.
- Pierwotny natywny wariant RTP używa osobnych portów UDP 5004/5006, datagramów do ok. 1200 bajtów i timestampu RTP w zegarze 90 kHz.
- Zalecane 5/6 GHz z dobrym sygnałem; PC po Ethernet do tego samego punktu dostępowego. Dwie kamery oznaczają sumowanie bitrate i obciążenia dekodera.

## Przepływ obrazu

1. Rzeczywisty test przeglądarkowy publikuje strumienie WebRTC/WHIP do MediaMTX.
2. MediaMTX udostępnia obrazy przez lokalny RTSP, a FFplay/VLC dekoduje je do podglądu.
3. Następny etap po weryfikacji jakości to podpięcie tych kanałów pod panel WPF.
4. Na końcu udostępnimy oba obrazy jako osobne kamery Media Foundation.

## Granice gwarancji

60 fps jest celem pomiarowym, nie deklaracją gwarantowanego wyniku dla każdego oświetlenia i warunków termicznych. Przy dłuższym czasie ekspozycji kamera może obniżyć liczbę klatek; ruch wymaga dobrego światła. Tryb 1080p60 będzie zależał od przepustowości Wi-Fi i wydajności kodera/dekodera, gdy drugi telefon także wysyła obraz.
