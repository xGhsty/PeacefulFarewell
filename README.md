# Peaceful Farewell

RimWorld mod (1.6) pozwalający kolonistom **pokojowo opuścić kolonię**, zamiast ginąć, znikać bez śladu albo zamieniać się w mentalny break. Kod C# napisany z użyciem [Harmony](https://github.com/pardeike/HarmonyRimWorld).

Nie wymaga żadnego DLC. Wymaga Harmony.

## O co chodzi

W vanilla RimWorld koloniści praktycznie nigdy nie odchodzą sami z siebie: jedyne wyjścia to śmierć, porwanie albo załamanie psychiczne. Peaceful Farewell dodaje alternatywę: jeśli kolonista ma silną więź z kimś poza kolonią (rodzina, partner, bliski przyjaciel) albo po prostu czuje się niechciany/znudzony życiem w kolonii, może **poprosić o odejście** zamiast przechodzić przez negatywne mechaniki vanilla. Gracz zawsze dostaje list z prośbą i decyduje: akceptuje albo odrzuca.

## Główne mechaniki

### 🚶 Dołączenie do odwiedzającej frakcji
Gdy do mapy przybywa przyjazna karawana/grupa odwiedzających, jest szansa, że kolonista mający bliską relację (rodzina, małżonek/partner, bliski przyjaciel) z kimś w tej grupie poprosi o dołączenie do niej. Po akceptacji kolonista udaje się do gości i opuszcza mapę razem z nimi, stając się normalnym członkiem ich frakcji w świecie gry, bez kary do nastroju.

### 😔 Samotność ("nie czuję się tu mile widziany")
Kolonista, którego **średnia opinia** wszystkich innych o nim jest bardzo niska, a jednocześnie **nikt pojedynczy** go na tyle nie lubi (żaden pojedynczy wynik opinii nie przekracza progu), może poprosić o opuszczenie kolonii z powodu poczucia odrzucenia. Oba progi są konfigurowalne w ustawieniach moda.

### 🧭 Wanderlust (zew wędrówki)
Niezależnie od relacji społecznych, kolonista może po prostu poczuć potrzebę zobaczenia świata i poprosić o wyruszenie w drogę na jakiś czas, bez porzucania kolonii na stałe (patrz niżej: powrót wędrowców).

### 📨 List z daleka
Koloniści, którzy odeszli, mogą z czasem przysłać list informujący bliskich w kolonii, że u nich wszystko w porządku, priorytetowo do partnera, potem rodziny, potem przyjaciół.

### 🔁 Powrót wędrowców
Koloniści, którzy odeszli z powodu samotności lub wanderlustu, nie znikają na zawsze, są śledzeni w świecie gry (`PF_WandererTracker`) i po pewnym czasie (losowy zakres dni, konfigurowalny) ich los się rozstrzyga: mogą **wrócić do kolonii** albo dołączyć do wrogiej frakcji (najgorszy, celowo niejednoznaczny scenariusz, gracz dostaje tylko lakoniczny list "brak wieści").

- Po powrocie kolonista odzyskuje swój dawny **harmonogram pracy i grafik** (przywracane z zapisu).
- Ubranie wędrowca jest odświeżane kosmetycznie (część noszonych przedmiotów może zostać wymieniona na nowe egzemplarze tego samego typu, symulacja "zużycia w podróży").
- Jeśli kolonista dołączy do wrogiej frakcji, bliscy pozostawieni w kolonii (partner/rodzina/przyjaciele) dostają negatywną myśl o niepewnym losie tej osoby, siła efektu skaluje się z bliskością relacji (partner > rodzina > przyjaciel).

### 🐾 Zwierzęta ze związkiem (bond)
Jeśli odchodzący kolonista ma zwierzę, z którym łączy go więź (bond), zwierzę **podąża za nim** i opuszcza kolonię razem z właścicielem. Dotyczy to wszystkich trzech ścieżek odejścia (dołączenie do frakcji, samotność, wanderlust).

### 💭 Reakcje kolonii
Odejście kolonisty nie przechodzi bez echa:
- Inni koloniści, którzy go nie lubili, odczuwają **ulgę** (plusowa myśl).
- Ci, którzy go lubili, odczuwają **żal** (minusowa myśl).
- Przed samym odejściem kolonista odwiedza swój pokój/łóżko na 1-3 godziny w grze, moment na "pożegnanie", zanim opuści mapę.

### 🌐 Lokalizacja
Pełne tłumaczenie **polskie i angielskie** (`Languages/Polish`, `Languages/English`), łącznie z rozróżnieniem płci w tekstach tam, gdzie to ma znaczenie.

## Ustawienia moda

Wszystkie progi i szanse są konfigurowalne w oknie ustawień moda.

| Sekcja | Co reguluje |
|---|---|
| Dołączanie do wizytujących | Szansa na prośbę o dołączenie na wizytę, minimalna waga relacji wymagana do rozważenia |
| Samotność | Szansa sprawdzenia, próg średniej opinii, próg pojedynczej opinii blokujący event |
| Wędrowcy | Min/max liczba dni do rozstrzygnięcia losu wędrowca, włącz/wyłącz list o dołączeniu do wrogiej frakcji |
| Wanderlust | Włącz/wyłącz mechanikę, szansa sprawdzenia |
| List z daleka | Włącz/wyłącz |
| Debug | Tryb debug, logowanie do pliku, generowanie raportu diagnostycznego, podgląd wszystkich aktywnych wędrowców |

Każdą odrzuconą prośbę pamięta osobny cooldown na kolonistę i typ eventu. Odrzucenie jednej prośby (np. dołączenia do wizytujących) nie blokuje tej samej osoby od innych typów próśb (samotność, wanderlust), ani nie wpływa na innych kolonistów.

## Struktura projektu

```
Core/           logika mechanik, skanery, ustawienia, generator raportów
DefOf/          referencje do Defów RimWorld (thoughty, joby, listy)
Harmony/        patche Harmony
JobDrivers/      niestandardowe joby (odejście, podążanie zwierzęcia, itp.)
Letters/        listy z prośbami (akceptuj/odrzuć) i powiadomieniami
FarewellUtility.cs   współdzielone funkcje pomocnicze
```

## Wymagania

- RimWorld 1.6
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) (ładowany przed tym modem)

## Autor

Ghost, with use of AI tool (Claude Code)
