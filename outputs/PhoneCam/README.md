# PhoneCam

Prywatna aplikacja Windows, która zamienia telefon w kamerę internetową przez lokalne Wi-Fi albo, w przypadku iPhone'a, przez kabel USB. Nie korzysta z chmury. Telefon nadaje obraz ze strony w Safari, więc nie trzeba instalować na nim aplikacji. Każdy telefon jest osobną kamerą wirtualną w OBS, Teams, Zoomie i przeglądarkach.

Aktualny etap: **iPhone** przez Wi-Fi lub kabel USB, podgląd na PC i kamera wirtualna „PhoneCam iPhone”. Android pojawi się w kolejnym etapie. Plan, kryteria i wyniki sprawdzeń są w [`docs/design.md`](docs/design.md).

## Uruchomienie

Wymagany jest Windows 11 z .NET 10 SDK oraz Visual Studio z obciążeniem „Programowanie aplikacji klasycznych w języku C++” (do zbudowania kamery wirtualnej). Z katalogu projektu:

```powershell
dotnet build .\PhoneCam.slnx
dotnet run --project .\src\PhoneCam.Desktop
```

Pierwszy build pobiera MediaMTX (lokalny serwer wideo) i sprawdza jego sumę SHA-256. Później aplikacja działa bez internetu.

1. **Przy pierwszym uruchomieniu** Windows poprosi o zgodę administratora (UAC). Aplikacja doda wtedy reguły Zapory Windows (sieć prywatna i połączenie USB z iPhone'em) i zarejestruje kamerę wirtualną. Jeśli odmówisz, możesz później wybrać „Skonfiguruj”.
2. Serwer startuje automatycznie. U góry okna sprawdź, czy wybrany jest adres z tej samej sieci Wi-Fi, w której jest telefon. Jeśli nie, zmień go na liście.
3. Wybierz **Połącz iPhone**. Pojawią się dwa kody QR.
4. **Kod 1, certyfikat (tylko raz):** zeskanuj go aparatem, otwórz link w Safari i wybierz „Zezwól”. Następnie:
   - Ustawienia → Pobrany profil → Instaluj;
   - Ustawienia → Ogólne → To urządzenie → Ustawienia zaufania certyfikatów → włącz **PhoneCam Local CA**.
5. **Kod 2, strona kamery:** zeskanuj go, otwórz w Safari i wybierz „Uruchom kamerę i połącz”. Zezwól na dostęp do ruchu i orientacji oraz do kamery. Połóż telefon poziomo, np. na statywie. Obraz na PC jest zawsze poziomy (16:9) i obraca się sam, także przy włączonej blokadzie orientacji. Telefon ustawiony pionowo daje środkowy wycinek obrazu.
6. Gdy obraz dotrze do komputera, otworzy się osobne okno podglądu. Główne okno pokazuje stan połączenia: kodek, rozdzielczość, FPS, przepływność i czas połączenia.

Certyfikat zostaje ważny także po zmianie adresu IP komputera. Instalujesz go ponownie tylko wtedy, gdy usuniesz dane aplikacji z `%LOCALAPPDATA%\PhoneCam`. Jeśli na telefonie jest jeszcze profil „PhoneCam Local Test CA” ze starego skryptu testowego, możesz go usunąć.

## Połączenie kablem USB (iPhone)

Kabel daje stabilniejsze i zwykle niższe opóźnienie niż Wi-Fi, bo eliminuje zakłócenia i gubienie pakietów w sieci bezprzewodowej. Strona w Safari i kody QR działają tak samo, zmienia się tylko droga, którą płynie obraz. Kabel wykorzystuje funkcję iPhone'a „Osobisty hotspot”.

Wymagania:

- Na PC aplikacja **Apple Devices** z Microsoft Store (albo iTunes). Sam sterownik sieci USB („Apple Mobile Device Ethernet”) Windows pobiera z Windows Update dopiero wtedy, gdy iPhone z włączonym hotspotem pierwszy raz udostępni sieć po kablu. Za pierwszym razem PC potrzebuje więc internetu.
- Na iPhonie włączone dane komórkowe i Osobisty hotspot. Opcja musi być dostępna u Twojego operatora.

Kroki:

1. Na iPhonie: Ustawienia → Osobisty hotspot → włącz „Pozwalaj innym na łączenie”.
2. **Dopiero teraz** podłącz kabel. Na telefonie wybierz „Zaufaj” i wpisz kod. Jeśli hotspot włączasz przy podłączonym kablu albo go przełączasz, iPhone nie uruchamia łącza. Wtedy odłącz i podłącz kabel. Za pierwszym razem odczekaj kilkanaście sekund na instalację sterownika, a potem też podłącz kabel ponownie.
3. PhoneCam sam przełączy się na adres „iPhone przez kabel USB”, jeśli w tej chwili nic nie nadaje. Stan widać w panelu „Kabel USB” u góry okna. Jeśli trwa nadawanie przez Wi-Fi, użyj przycisku „Przełącz na USB”.
4. Zeskanuj ponownie **kod 2**, bo adres strony jest teraz inny. Certyfikatu nie trzeba instalować drugi raz.

Po odłączeniu kabla aplikacja wraca do Wi-Fi. Jeśli panel pokaże, że Windows wysyła internet komputera przez iPhone'a (czyli zużywa dane komórkowe), wybierz „Nie używaj internetu iPhone'a”. To jednorazowa zmiana priorytetu karty sieciowej i wymaga zgody administratora.

Po aktualizacji do tej wersji Windows raz jeszcze poprosi o zgodę administratora, bo aplikacja dodaje reguły zapory dla połączenia USB.

## Kamera w OBS, Teams i innych programach

Po pierwszym uruchomieniu w systemie jest kamera **„PhoneCam iPhone”**. Działa tylko wtedy, gdy PhoneCam jest uruchomiony, ale okno podglądu nie musi być otwarte.

- **OBS:** Źródła → + → „Urządzenie przechwytywania wideo” → Urządzenie: „PhoneCam iPhone”.
- **Teams, Zoom, Discord, Chrome, Edge:** w ustawieniach kamery wybierz „PhoneCam iPhone”.

Kamera zawsze daje obraz poziomy 1280×720, obrócony i przycięty tak samo jak w podglądzie. Obraz nie jest kodowany ponownie, więc kamera nie dodaje zauważalnego opóźnienia. Gdy telefon nie nadaje, kamera pokazuje planszę „brak sygnału”. Po zamknięciu PhoneCam programy widzą przyciemnioną ostatnią klatkę albo czarny obraz. Stan kamery („gotowa”, „używana przez aplikację”) widać w głównym oknie pod statusem połączenia.

Kamera nie pojawi się w aplikacji Aparat Windows ani w programach 32-bitowych. Po przeniesieniu aplikacji do innego katalogu (np. przy przejściu z Debug na Release) Windows raz jeszcze poprosi o zgodę administratora.

## Wymagania sieci

- Telefon i PC w tej samej sieci lokalnej. Izolacja klientów Wi-Fi (AP/client isolation) musi być wyłączona.
- Sieć musi być w Windows oznaczona jako **prywatna**. Aplikacja ostrzega, gdy jest publiczna.
- Zalecane pasmo 5/6 GHz z dobrym sygnałem albo PC po kablu Ethernet do tego samego routera. Najstabilniejsza opcja dla iPhone'a to kabel USB (patrz wyżej).

## Sprawdzenia automatyczne

```powershell
dotnet .\tools\PhoneCam.HostingChecks\bin\Debug\net10.0-windows\PhoneCam.HostingChecks.dll   # serwer, certyfikaty, USB, MediaMTX, podgląd, kamera wirtualna
dotnet .\tools\PhoneCam.ProtocolChecks\bin\Debug\net10.0\PhoneCam.ProtocolChecks.dll         # RTP/H.264
dotnet .\tools\PhoneCam.VideoChecks\bin\Debug\net10.0-windows\PhoneCam.VideoChecks.dll       # ścieżka UDP → dekoder
```

Przed uruchomieniem HostingChecks zamknij aplikację, bo test potrzebuje tych samych portów i kamery. Kontrole kamery wymagają, żeby PhoneCam wcześniej raz ją zarejestrował.

## Logi i dane

`%LOCALAPPDATA%\PhoneCam\logs\mediamtx.log` zawiera log serwera wideo, a `crash.log` nieoczekiwane błędy aplikacji. Certyfikaty, konfiguracja MediaMTX i wybrany interfejs sieciowy też są w `%LOCALAPPDATA%\PhoneCam`.

## Granice

60 fps to cel, nie gwarancja. Liczba klatek zależy od światła (czasu ekspozycji), temperatury telefonu i obciążenia Wi-Fi. Najpierw stroimy stabilne 720p60.
