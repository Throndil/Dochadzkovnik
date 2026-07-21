# Roadmap — zlúčená verzia po 2. hovore (18.07.2026)

Dva hovory so zákazníkom v jeden deň; druhý priniesol veľkú architektúrnu zmenu:
**divízie firmy** a **príjmové faktúry**. Tento súbor je jediný zdroj pravdy —
pôvodný plán je tu zlúčený s novým. S/M/L = odhad náročnosti.

---

## ✅ Hotové (18.07.2026)

| # | Čo | Pozn. |
|---|----|-------|
| B1 | KPI dlaždice Faktúr sledujú zvolený dátumový rozsah | + default rozsah = predošlý + aktuálny mesiac, riadok „Zobrazené: …", ochrana proti obrátenému rozsahu |
| B2 | **Účtovníctvo všade S DPH** (faktúrové riadky sa prepočítavajú pri čítaní; sklad/kiosk ceny ostávajú ako zaplatené) | Materiál na pracoviskách ≈ Faktúry s DPH — čísla „narástli", vysvetliť zákazníkovi |
| B3 | Pred uložením faktúry sa **vždy pýta kam** — per dodací list, Sklad je explicitná voľba, aj „Uložiť bez priradenia" | AZ Profistav rieši zatiaľ pracovisko „Firma" — nahradí DIVÍZIA (viď D-fáza) |
| B4 | **Bezpečnostný PIN po prihlásení** (Účet → Bezpečnostný PIN; zmena vyžaduje starý PIN; 5 zlých pokusov = 5 min zámok) | |
| B5 | Prepínač svetlý/tmavý režim pre každého (kiosk + login) | |
| R1+R2 | **Záchrana nepodarených skenov**: žiadne tvrdé požiadavky pri uploade (číslo sa syntetizuje BEZ-CISLA-…, dodávateľ „neznámy", suma 0), „Odfotiť prázdny doklad" bez AI, ručné riadky na kontrole, „Ručný doklad" chip, bunky sa označia po kliknutí | Papier „betónovanie 500 €" funguje end-to-end |
| P2 | Zmluvná hodnota pracoviska (existovalo) | |
| P5 | Vyhľadávanie dodávateľ / IČO / číslo na Faktúrach | |
| — | Premenovanie Inventár → **Sklad** v celej aplikácii | |
| — | Scan pipeline: **Sonnet primárny, Gemini fallback**, Document AI zrušené; orez na rámik v živom náhľade | |

---

## Fáza D — DIVÍZIE FIRMY (architektúrne jadro, robiť PRED flotilou)

Z 2. hovoru: firma má dve prevádzkové divízie — **stavby** a **stroje**.
Podskupiny pre financie: **AZ Profistav, AZ Stavba, AZ Stroje** (potvrdiť presnú
množinu — viď otázky). Divízia je nový rozmer účtovníctva, nezávislý od
pracovísk a strojov.

| # | Úloha | Poznámka zo zdroja | Odhad |
|---|-------|--------------------|-------|
| D1 | **Divízia ako entita** + pole na doklade (faktúra/bloček/ručný doklad): pri nahrávaní sa vyberá, do ktorej divízie doklad patrí. **POTVRDENÉ: dve divízie — AZ Profistav (stavby) a AZ Stroje (bagre, vozidlá na presun materiálu…).** | „pridat do faktur do ktorej divizie to chce pridat" | M |
| D2 | **Smer dokladu: náklad vs. PRÍJEM** — určuje sa podľa toho, ČO faktúra je: práca pre niekoho = príjem (+); olej, nafta, servis… = náklad (−). Divízia má z toho svoju bilanciu. | „faktury naklady faktury prijem" | M/L |
| D3 | **Prepínač divízií = „burger" v lište Financií** (nápad zákazníka). Stránky ostávajú tie isté (Faktúry flow ako dnes), len sa prepína divízny kontext; pod skenovaním mesiac + **Príjem / Výdaj / Rozdiel** (zatiaľ len z dokladov — potvrdené). | „klikne na AZ stroje… prijem, vydaj, rozdiel" | M/L |
| D4 | **Doklady divízie Stroje sa MAPUJÚ NA DIVÍZIU, nie na stroj** (štrky, kontajnery, odvoz zeminy — nie je kam). Voliteľný „backtrack": náklad sa dá PRIRADIŤ k stroju/autu len pre prehľad („aby vedeli"), bilancia sa počíta na divízii. | „nespecifikovat ku akemu stroju…" + 3. hovor | M |
| D5 | **Súhrn (Financie) zobrazuje OBE divízie** — príjem, výdaj, rozdiel per divízia + spolu | „do suhrnu potom pridat obe divizie" | M |
| D6 | **Mesačné reporty per divízia** — export (Excel) mesiaca: príjem / výdaj / rozdiel + rozpis dokladov. Nahrádza P6 (export Súhrnu). | „mesacne reporty" (2×); ✅ tlačidlo „Report (Excel)" pri karte Divízie na Súhrne → `GET /api/invoices/monthly-report?month=` — hárok Súhrn (P/V/R + spolu za obe divízie) + hárok s rozpisom dokladov per divízia (podpísané sumy s DPH, stav). | ✅ 18.07. |
| D7 | Migrácia: existujúce doklady dostanú divíziu (default AZ Profistav/Stavba); pracovisko „Firma" a „Servis auta" sa premapujú podľa F-fázy | | S/M |
| D8 | **Mzdy v divíziách:** každý zamestnanec má priradenú divíziu (detail zamestnanca → Divízia); Mzdy aj export sa filtrujú podľa aktívnej divízie z burgeru. Rieši aj „mzda šoféra pod AZ Stroje" (F7). | rozšírenie 18.07.; **oprava 18.07. večer:** zamestnanci s prázdnou (legacy) divíziou sa v Mzdách nezobrazovali v ŽIADNEJ divízii — filter teraz počíta všetko okrem „stroje" pod AZ Profistav (rovnaká konvencia ako doklady) | ✅ 18.07. |

## Fáza F — STROJE a AUTÁ (flotila; stavia na divíziách)

| # | Úloha | Poznámka | Odhad |
|---|-------|----------|-------|
| F0 | **Stránka Stroje** — evidencia strojov (bager…): /admin/stroje pod Prevádzkou, foto, inline úpravy, deaktivácia; TimeEntry.MachineId pripravené pre kiosk F3 | „stroje stranka / pridaju sa stroje" | ✅ 18.07. |
| F1 | **Voliteľný rozpad nákladov na mašinu/auto** — na kontrole dokladu výber „Mašina/Auto (voliteľné)", chip na zozname, súčet nákladov na kartách Mašín aj Áut; čisto informačné, bilancia divízie z dokladov (D4) | „rozdelit aj ze tankovanie - olej atd… to iste aj pre auta" | ✅ 18.07. |
| F2 | ~~Príjem mapovaný na stroj~~ → ZRUŠENÉ: príjem aj náklad sa účtujú na DIVÍZIU (D2/D4); stroj má len voliteľný nákladový backtrack (F1) | 3. hovor | — |
| F3 | **Kiosk: tri možnosti dopravy — Auto / Stroj / Pešo.** Bagrista zvolí bager namiesto auta; Pešo = bez nákladu (potvrdené). TimeEntry nesie MachineId. | „v kiosku tri moznosti auto alebo stroje alebo peso" | ✅ 18.07. |
| F4 | **Servis áut inak:** servisy priradené ku konkrétnemu autu; migrácia z pracoviska „Servis auta" | z 1. hovoru; ✅ ledger `GET /api/cars/{id}/costs` + karta „Náklady na auto" na detaile auta (mirror aj pre mašiny). Migrácia starých dokladov z pracoviska „Servis auta": ručne cez existujúcu kontrolu dokladu (priradiť Mašinu/Auto per riadok/DL/doklad — funguje aj na uložených) — automaticky sa nedá, cieľové auto na starých dokladoch nie je zaznamenané. | ✅ 18.07. |
| F5 | **Výjazdy:** auto ako položka 30 €/výjazd (Cenník, editovateľné); jazda sa počíta RAZ za auto aj pri viacerých pracovníkoch | z 1. hovoru; ✅ výjazd = unikát (auto, deň) zo zavretých šícht pracoviska; sadzba z Odvody („vyjazd_auta", živá — zmena tarify prepíše aj históriu). V P&L pracoviska, v Súhrne (Náklady podľa pracoviska) aj v Excel exporte; Čistý zisk ju odpočítava. | ✅ 18.07. |
| F6 | **Palivové karty (6) + pozície zamestnancov** (šofér, karta X, stroj Y); niektorí držitelia ešte nie sú v systéme | z 1. hovoru; ✅ `Employee.Position` (voľný text, detail zamestnanca) + tabuľka `FuelCards` (označenie, poznámka, držiteľ voliteľný, aktívna) + stránka /admin/palivove-karty (link „Karty" v Prevádzke). Migrácia `AddFuelCardsAndPositions` (CLI, aditívna). | ✅ 18.07. |
| F7 | **Commander faktúry pre autá a stroje**; mzda šoféra sa zahŕňa pod AZ Stroje | z 1. hovoru; vyriešené existujúcim flow (potvrdené 18.07.): Commander faktúra = bežná faktúra — nahrá sa ako každá iná, priradí divízia + voliteľne auto/stroj (F1). Mzda šoféra pod AZ Stroje = D8. Ak sa objaví layout, ktorý parser nezvládne, doplní sa fixture. | ✅ — |

## Fáza P — Pracovisko = kompletná zložka (z 1. hovoru, stále platí)

| # | Úloha | Odhad |
|---|-------|-------|
| P1 | Denník podľa dátumu na hlavnej stránke pracoviska — „vyroluje sa celá zložka" (denník, náklady, materiál, faktúry, fotky) | ✅ 18.07. — sekcia „Zložka podľa dní" na detaile pracoviska: `GET /api/locations/{id}/daily-log` skladá per-deň šichty (meno, hodiny, poznámka, auto/stroj), stavebný denník, materiál, doklady (DL/nákupy s preklikom na faktúru) a fotky (šichtové + samostatné). Režimy: Týždeň (default, ‹›), Deň, Vlastné od–do; dni rozbaliteľné, prázdne dni sa nezobrazujú. |
| P3 | ~~Réžia~~ → nahradené divíziami (Fáza D) | — |
| P4 | Dodatočné priraďovanie Nezaradené/Sklad/nákupy na stavbu — **odložené** (vrátime sa) | M |
| P7 | **Stránka „Odvody"** (pomenovanie zákazníka, nie Cenník) — sumy, ktoré firma platí navyše: odvody, ubytovanie (1 €), výjazd auta (30 €)… editovateľné + vlastné položky; W1 a F5 z nej čítajú | ✅ 18.07. |

## Fáza W — Mzdy (z 1. hovoru, stále platí)

| # | Úloha | Odhad |
|---|-------|-------|
| W1 | **Dve sadzby per zamestnanec**: výplatná (dnešná) + hrubá (cestovné, ubytovanie, odvody) — náklady pracovísk a P&L čítajú HRUBÚ | ✅ 18.07. — hrubá = výplatná (WageAtTime) + súčet položiek z Odvodov s jednotkou obsahujúcou „€/h" (odvody, ubytovanie, vlastné riadky). Použité v P&L pracoviska + Súhrne + Excel exporte; Mzdy (výplaty) ostávajú výplatné. Nie je to per-zamestnanec sadzba — ak treba individuálnu hrubú, doplniť neskôr. |
| W2 | Mzdový list: rozpis záloh (dátum, suma, poznámka) | ✅ už hotové (overené 18.07.) — Mzdy stránka má drawer záloh (dátum/suma/poznámka, pridanie aj úprava); mzdový list zamestnanca (Excel) má hárok „Zálohy" (Dátum, Suma, Poznámka, Zaznamenal + Spolu). |
| W3 | Výplatné pásky na tlač — viac zamestnancov na A4 | ✅ 18.07. — tlačidlo „Tlačiť pásky" na Mzdách; print-only mriežka 2 stĺpce (~6 pások/A4, čierna na bielej): meno, obdobie, hodiny, sadzba, hrubá, zálohy, výplata + riadky dátum/podpis. |

## Fáza Z — Zariadenia, drobnosti, nápady

| # | Úloha | Poznámka | Odhad |
|---|-------|----------|-------|
| Z1 | **Premenovať „Manažér" → „Vladimír Sroka"** | „zmenit meno z manazer" | ✅ 18.07. — Účet → „Zobrazované meno" (self-service; zákazník si meno nastaví sám) |
| Z2 | Admin kód pri prvom spustení nového zariadenia (jeden kód, zmena = starý kód) — **PIN z B4 sa dá znovu použiť** | z 1. hovoru | M |
| Z3 | **Chatbot na pomoc?** — nápad zo záveru hovoru; návrh: FAQ/nápoveda ako prvý krok, AI chat až keď bude treba | „pridat chatbota na pomoc?" | ? — na diskusiu |

---

## Odporúčané poradie

1. **Z1** (premenovanie — 5 minút) → 2. **Fáza D** (divízie + príjmy + divízne stránky + Súhrn + mesačné reporty) → 3. **P7 Cenník** → 4. **Fáza F** (stroje/autá — potrebuje D aj P7) → 5. **W** (mzdy) → 6. **P1** (zložka pracoviska) → 7. **Z2/Z3**.

## Na budúci hovor

- **Zamestnanec v OBOCH divíziách?** Dnes má každý práve jednu (D8). Ak niekto reálne robí pre stavby aj stroje, treba rozhodnúť: delenie hodín per šichta (výber divízie v kiosku?) alebo percentuálny split. Zatiaľ jedna divízia na osobu.

## Otázky — ZODPOVEDANÉ (3. hovor, 18.07.)

1. **Divízie: DVE.** AZ Profistav = stavby; AZ Stroje = stroje (bagre, vozidlá na presun materiálu…).
2. **Príjmové faktúry:** rovnaký flow ako dnes, len sa rozlišuje divízia; prepínanie cez „burger" v lište Financií (nápad zákazníka).
3. **Rozdiel** na divíznej stránke: zatiaľ len z dokladov. ✅
4. **Mesačný report:** Excel per divízia + celkový za obe — NESKÔR (veľký súbor, rozsah treba dohodnúť).
5. **Pešo:** žiadne náklady, len voľba v kiosku.
