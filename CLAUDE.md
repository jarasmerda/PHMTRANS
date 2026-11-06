# PHMTRANS – pokyny pro Claude

Tento soubor si načte každý feed (session) Claude Code automaticky. Je krátký schválně, podrobnosti jsou v `PREHLED.md`.

## Než začneš
1. Přečti `PREHLED.md`, hlavně část **Feedy** (feedy PHMTRANS, ERP, DISPEČINK a kdo řeší co) a sekci svého modulu.
2. `git pull origin main`. Feedy pracují souběžně, `main` se mění.

## Jak pracovat
- Komunikuj česky, stručně a srozumitelně i pro neprogramátora.
- Každá změna: nová větev → `dotnet test tests/PHMTRANS.Tests` → commit → push → pull request.
  PR slučuj do `main` až po výslovném „slouč“ od uživatele.
- Jeden PR = jedna věc. Malé PR, ať se feedy nepotkávají v konfliktech.
- Drž se souborů svého modulu (tabulka v `PREHLED.md` → Feedy). Sdílené soubory měň jen když je to nutné a napiš to do PR.
- Měníš chování, nastavení nebo nasazení? Uprav ve stejném PR i `PREHLED.md`.
- Hesla, IČO a connection string do gitu nepatří.
- Po sloučení dej uživateli příkaz k nasazení (viz `PREHLED.md` → Nasazení) a řekni, jestli stačí `git pull`, nebo je potřeba publikace.
