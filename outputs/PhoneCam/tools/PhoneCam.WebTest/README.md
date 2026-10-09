# Rzeczywisty test telefonów przez przeglądarkę

> **Przestarzałe.** Aplikacja `PhoneCam.Desktop` sama uruchamia serwer i pokazuje kody QR (zob. główny README). Ten skrypt zostaje tylko jako ścieżka awaryjna i nie jest rozwijany.

To pierwszy wariant przeglądarkowy PhoneCam. MediaMTX przyjmuje dwa osobne strumienie WebRTC/WHIP z Safari lub Chrome, a udostępnia je lokalnie przez RTSP do podglądu na PC. Cały strumień medialny pozostaje w sieci lokalnej; nie ma STUN/TURN ani konta w chmurze.

## Uruchomienie

1. Uruchom PowerShell w Windows 11 i przejdź do katalogu projektu.
2. Wykonaj:

   ```powershell
   .\tools\PhoneCam.WebTest\Start-PhoneCamWebTest.ps1
   ```

   Jeśli PC ma kilka kart sieciowych, zamiast powyższego polecenia wskaż adres z tej samej sieci co telefony:

   ```powershell
   .\tools\PhoneCam.WebTest\Start-PhoneCamWebTest.ps1 -IpAddress 192.168.1.20
   ```

3. Przy pierwszym uruchomieniu skrypt utworzy certyfikat lokalnego CA i certyfikat serwera z adresem IP PC w SAN, pobierze oficjalne wydanie MediaMTX v1.21.1, sprawdzi SHA-256 i uruchomi odbiornik. Skrypt nie instaluje lokalnego CA w magazynie zaufania Windows.
4. Zezwól `dotnet.exe` na TCP 8443 (strona) i 8787 (pobranie certyfikatu) oraz `mediamtx.exe` na TCP 8889 (HTTPS/WHIP) i UDP 8189 (WebRTC), wyłącznie dla sieci prywatnej Windows. RTSP jest związany tylko z `127.0.0.1`, więc jest dostępny na PC i nie wymaga otwierania portu w LAN.
5. Z poziomu telefonu otwórz `http://ADRES-PC:8787/PhoneCam-RootCA.cer`, pobierz certyfikat i zainstaluj go jako zaufany certyfikat CA. iOS wymaga dodatkowego włączenia pełnego zaufania w ustawieniach certyfikatów. Android może poprosić o PIN/biometrię. Nie akceptuj ostrzeżenia certyfikatu jako obejścia: certyfikat CA musi być zaufany przez urządzenie.
6. Otwórz z Safari/Chrome, wpisując adres wypisany w konsoli:

   - iPhone 15 Pro: `https://ADRES-PC:8443/iphone`
   - Xiaomi Mi 9T: `https://ADRES-PC:8443/android`

7. Wybierz tylną kamerę. Domyślnie iPhone używa 1280×720 / 60 fps / 10 Mb/s, a Xiaomi 1280×720 / 30 fps / 6 Mb/s. Wybierz „Uruchom kamerę i połącz” i przyznaj stronie dostęp do kamery. Strona wymusza kodek H.264 i nie prosi o mikrofon.
8. Na PC otwórz podgląd RTSP z adresów wypisanych w konsoli. FFplay znajduje się w `src\PhoneCam.Desktop\bin\Debug\net10.0-windows\ffmpeg\win-x64\ffplay.exe`, jeśli aplikacja była już zbudowana; w innym wypadku użyj dowolnego odtwarzacza RTSP. Przykładowe polecenie PowerShell uruchomione z katalogu projektu:

   ```powershell
   & .\src\PhoneCam.Desktop\bin\Debug\net10.0-windows\ffmpeg\win-x64\ffplay.exe -fflags nobuffer -flags low_delay -rtsp_transport tcp rtsp://127.0.0.1:8554/iphone
   ```

Każdy telefon korzysta z osobnej strony/ścieżki i może nadawać równocześnie. Strona pokazuje parametry źródła i statystyki wysyłanego obrazu. Zamknięcie karty kończy publikację. Zatrzymanie serwera: `Ctrl+C` w PowerShell.

## Co mierzyć

- Sprawdź najpierw każdy telefon osobno przez 5 minut, a potem oba jednocześnie.
- W panelu publikatora sprawdź, czy przeglądarka zaakceptowała żądane wymiary i FPS; ustawienia są preferencjami przeglądarki, a nie gwarancją stałego FPS.
- Oceniaj płynność, artefakty i stabilność. Opóźnienie można wstępnie oszacować, filmując jednocześnie telefon i ekran PC z widocznym stoperem lub szybkim ruchem; dokładniejszy pomiar wymaga późniejszego instrumentowania zegara i statystyk WebRTC.
- Test 1080p60 wykonuj po stabilnym teście 720p60. Włącz dobre światło, bo ekspozycja kamery może ograniczyć FPS.

## Bezpieczeństwo i ograniczenia

- Telefon i PC muszą być w tej samej sieci; izolacja klientów Wi-Fi (AP/client isolation) musi być wyłączona. Skrypt wybiera jedno IP i wpisuje je do certyfikatu oraz konfiguracji ICE. Przy wielu kartach można podać je przez `-IpAddress`.
- Certyfikat jest lokalny i nie jest podpisany przez publiczny urząd certyfikacji. Skrypt zapisuje certyfikat serwera w magazynie osobistym bieżącego użytkownika Windows; Root CA należy zainstalować ręcznie na obu telefonach.
- MediaMTX v1.21.1 jest pobierany z oficjalnego GitHub projektu przy pierwszym uruchomieniu. To osobny komponent serwera. Skrypt tworzy certyfikat dla aktualnego adresu IP i zachowuje go między startami; `-NewCertificate` wymusza utworzenie nowego certyfikatu.
- W tej fazie podgląd jest przez RTSP w odtwarzaczu na PC. Integracja odbioru WebRTC z panelem WPF i kamery wirtualne pozostają kolejnymi etapami.
- Ten skrypt przygotowuje niezależny certyfikat i konfigurację dla bieżącego adresu IP. Jeśli IP komputera się zmieni, ponownie uruchom skrypt, usuń poprzednie zaufanie PhoneCam CA z telefonów i zainstaluj nowy certyfikat.
