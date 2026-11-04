# Přehled nastavení PHMTRANS

Podklad pro další úpravy. Stav větve `main` je revize `3397c16` (4. 11. 2026, 21:09 SEČ).
Jeden proces ASP.NET Core na .NET 10. Stránky jsou statické soubory ve `wwwroot`.
Účetnictví a adresář čte Pohoda mServer. Dispečink, CRM a účty intranetu jsou v SQL Serveru.

GitHub: https://github.com/jarasmerda/PHMTRANS

## Co v aplikaci je

| Modul | Stránka | API | Data |
| --- | --- | --- | --- |
| Veřejný web | `landing.html` na `phmtrans.cz` | — | jen soubory |
| ERP | `/ERP` → `erp.html` | `/api/bi/issued-invoices` | Pohoda, vydané faktury |
| CPQ | konfigurátor zásob | `/Cpq…`, `/api/pohoda/xml` | Pohoda, skladové karty |
| Dispečink | `/Dispecink` → `dispecink.html` | `/api/dispatch/…` | SQL, tabulky `Disp_*` |
| CRM | `/CRM` → `crm.html` | `/api/crm/…` | SQL, tabulky `Crm_*` |
| Účty | `/login.html`, `/heslo`, `/Admin` | `/api/login`, `/api/auth/…`, `/api/admin/users` | SQL, `Auth_User` |

`/health` vrací `status`, verzi, `timeUtc` a větev. Větev se do sestavení bere z gitu, za běhu ji přepíše `GIT_BRANCH`, při buildu `-p:GitBranch`.

## Kde co leží

| Místo | Role |
| --- | --- |
| `C:\Projects\PHMTRANS` | Vývoj na počítači. SDK 10.0.403, `dotnet run` na `http://localhost:5053`, prostředí Development. |
| `C:\PHMTRANS` | Klon na Windows Serveru. Odtud se dělá `git pull`. |
| `C:\PHMTRANS\app` | Publikace služby: `dotnet publish -c Release -o C:\PHMTRANS\app`. |
| `C:\PHMTRANS\wwwroot` | Stránky, které služba servíruje. Úprava jen HTML, CSS a JS je `git pull` bez publikace. |
| Služba Windows `PHMTRANS` | Port 8089, start Automatic. Mimo Development poslouchá na `http://*:8089`. |

Docker na serveru neběží. `Dockerfile` a `Dockerfile.windows` v repozitáři zůstávají.

Náhled bez SQL je jen Development a jen když je `Dispatch:Preview` true. Pak jsou dispečink, CRM účty a adresář v paměti a do SQL se nezapisuje.

## Adresy

| Host | Chování |
| --- | --- |
| `phmtrans.cz` | Veřejná prezentace (`landing.html`). |
| `www.phmtrans.cz` | `301` na `https://phmtrans.cz` se stejnou cestou. `/health` zůstává 200. |
| `intranet.phmtrans.cz` | Moduly. Bez cookie jde kořen na login, po přihlášení na první povolený modul, u správce na `/ERP`. |
| `erp.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/ERP`. |
| `disp.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/Dispecink`. |
| `crm.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/CRM`. |

Stejné platí pro `www.` u starých hostitelů. Cesta modulu je bez ohledu na velikost písmen: `/erp` i `/ERP`. Pod prefixem fungují i soubory a API, třeba `/Dispecink/api/dispatch/board`.

Aplikace na IP: http://185.219.164.45:8089/
mServer: http://185.219.164.45:444 (`/status` bez hesla, `/xml` s přihlášením).

Host se bere z `X-Forwarded-Host`, jinak z `Request.Host`. IIS ARR má posílat i `X-Forwarded-Proto`. Podrobnosti vazeb a certifikátu jsou v `docs/intranet-navrh.md`.

## Nasazení

Změna C# (schéma se při startu doplní samo, když je connection string):

```powershell
Stop-Service PHMTRANS
cd C:\PHMTRANS
git pull origin main
dotnet publish -c Release -o C:\PHMTRANS\app
Start-Service PHMTRANS
curl.exe http://localhost:8089/health
```

V PowerShellu je `curl` alias na `Invoke-WebRequest`. Na serveru se volá `curl.exe`.

Změna jen ve `wwwroot` je `git pull` bez publikace a bez restartu.

## Přihlášení

Uživatelé jsou v `dbo.Auth_User`, ne v Pohodě. Heslo je hash ASP.NET Core `PasswordHasher`. Účet má příznak správce, aktivní, a povolené moduly ERP, Dispečink a CRM.

Cookie `phmtrans-auth` platí 12 hodin a posouvá se. Je `HttpOnly`, `SameSite=Lax`, v produkci `Secure`, a platí jen pro hostitele, na kterém vznikla. Ve Development na HTTP se `Secure` řídí schématem požadavku, jinak by se na `localhost` neuložila.

Pět špatných hesel účet na 15 minut zamkne. Z jedné adresy jde nejvýš 20 pokusů za minutu. Heslo se neloguje. Vypnutý účet se nepřihlásí a otevřená relace na dalším požadavku spadne. Posledního aktivního správce nejde odebrat.

`/heslo` mění vlastní heslo. `/Admin` je jen pro správce: založení, moduly, nové heslo, vypnutí.

První správce se založí jen když v tabulce ještě žádný není. Proměnné počítače, ne soubor v gitu:

- `Intranet__AdminUsername`
- `Intranet__AdminPassword` (aspoň 8 znaků)

Nepřihlášené `/api` vrací 401 a stránka modulu jde na `/login.html`. Modul bez oprávnění je 403.

## Databáze

Connection string je `ConnectionStrings:Dispatch`. V gitu je prázdný. Na serveru je to proměnná `ConnectionStrings__Dispatch`. Databáze je `Smerco_26950430_2026`. Skripty při startu tabulku založí, jen když chybí, a sloupec přidají, jen když chybí. Nemají `DROP`, `DELETE` ani `TRUNCATE` a neobsahují jméno databáze.

Když connection string chybí, služba nastartuje a `/health` zůstane v pořádku. Přihlášení, dispečink a CRM vrátí, že připojení není nastavené.

Adresář Pohody `dbo.AD` v té databázi není. Našeptávač zákazníka ho čte z `StwPh_26950430_2026` (`Pohoda:AddressBookDatabase`) trojdílným jménem, jen `SELECT`.

| Tabulky | K čemu |
| --- | --- |
| `Auth_User` | Účty intranetu. |
| `Disp_Vehicle`, `Disp_Driver`, `Disp_Trip` | Vozidla, řidiči, jízdy. |
| `Disp_Order`, `Disp_OrderStop`, `Disp_OrderLeg` | Zakázka, vykládky, úseky. |
| `Disp_OrderVersion`, `Disp_SyncQueue` | Snímky verzí a fronta do Pohody. |
| `Disp_FuelType` | Číselník paliva. Seed: Motorová nafta, Benzín. |
| `Disp_Distance` | Matice vzdáleností. |
| `Disp_Setting` | Výchozí náklad Kč/km. |
| `Crm_Customer`, `Crm_Contact`, `Crm_Opportunity`, `Crm_Activity` | CRM. |

## Dispečink

Deska má tři pohledy: Jízdy, Zakázky, Fronta Pohody. Založení jízdy i zakázky je samostatné okno. Seznamy umí víc stavů najednou a ve výchozím filtru vynechají storno.

### Jízdy

Stavy: Nová, Přiřazená, Na cestě, Vyložená, Zrušená, Stornováno. Kód je `J{rok}-{čtyři číslice}`. Jízda má vozidlo a řidiče z číselníku. Na jízdě je vidět seznam jejích zakázek. Stornovaná jízda už zakázky nepřijímá.

### Zakázky, vykládky a úseky

Stavy: Nová, Naplánovaná, Na cestě, Doručeno, K fakturaci, Vyfakturováno, Stornováno. Kód je `Z{rok}-{čtyři číslice}`. Zákazník se vybírá z adresáře Pohody a na zakázce zůstane snímek názvu, IČO, DIČ a adresy.

Hlavička má nakládku, datum, naložené litry, sazby a druh paliva (`Disp_Order.FuelTypeId`). Sekce Vykládky a úseky je řada zastávek. Z nich vzniknou úseky Nakládka → Vykládka 1, Vykládka 1 → 2, a dál. Každá vykládka má vlastní palivo (`Disp_OrderStop.FuelTypeId`). Když ho dispečer nezmění, převezme se z hlavičky.

Režim prodeje je za kilometr, nebo za litr. Náklad úseku je km × nákladová sazba Kč/km. Prodej je km × sazba, litry × sazba, nebo ruční cena úseku. Příplatky se přičtou k prodeji. Deska ukazuje km, naloženo, vyloženo, náklad, prodej, marži v Kč, Kč/km a procentech. Vyložené litry nad naložené se jen upozorní.

### Matice vzdáleností

`Disp_Distance` má normalizované `FromPlace` a `ToPlace`, `Km`, `UpdatedAt`, `UpdatedBy`. Místo se ořeže a srovná bez velikosti písmen. Pár je jeden řádek: A→B je totéž co B→A. U pole km je text `z matice`, nebo `ručně`. Chybějící nebo přepsané km se při uložení zakázky upsertují. Kdo hodnotu zapsal, je přihlášený uživatel.

### Verze

Každá obchodní změna přidá řádek `Disp_OrderVersion` a do `RouteJson` uloží sazby, vykládky a úseky včetně paliva a zdroje km. Starší verze se otevírá jen ke čtení. Samotné přiřazení jízdy, vozidla a řidiče verzi ani frontu nedělá.

### Pohoda

`Pohoda:SyncMode`: `Off` frontu neposílá, `DryRun` jen uloží XML, `Live` volá mServer. Výchozí je `Live`. Text položky je `Leg N: odkud → kam, palivo, litry`. Náklad, km při ruční prodejní ceně, vozidlo, řidič a jízda v XML nejsou.

Neodeslaná změna se přepíše v otevřené položce fronty. Po odeslání se další změna neuzamčené objednávky posílá jako smazání původního dokladu a založení nového (`ord:delete` a `ord:add`), ne jako `ord:update`. Když smazání v Pohodě projde a založení ne, další pokus už doklad nemaže a jen ho zakládá. Když už v Pohodě není, bere se smazání jako hotové.

Uzamčená objednávka (vyfakturovaná) se v Pohodě nemění. Vznikne opravný doklad s číslem `{kód zakázky}/{pořadí}`, tedy `Z2026-0001/1`, `/2`, … Storno položky mají záporné množství. Storno uzamčené zakázky je stejný opravný doklad, ne smazání originálu. Neuzamčená odeslaná zakázka se v Pohodě maže a v aplikaci zůstane ve stavu Stornováno. Zakázka, která v Pohodě ještě nebyla, se dá smazat natrvalo, nebo jen stornovat.

Číslo dokladu v Pohodě má nejvýš 32 znaků. Delší opravné číslo se odmítne dřív, než jde do fronty.

## CRM

Zákazník je `Firma` nebo `Osoba`. Kontakt visí na zákazníkovi. Příležitost má fázi Nový, V jednání, Nabídka, Vyhráno, Prohráno. Aktivita je hovor, schůzka nebo poznámka a visí na zákazníkovi, na příležitosti, nebo na obou. Kódy: zákazník `C{rok}-0001`, příležitost `O{rok}-0001`, aktivita `A{rok}-0001`. Do Pohody CRM nezapisuje.

## ERP a CPQ

Faktury jsou `issuedInvoice`, částky v domácí měně. Stavy: Uhrazeno, Částečně, Po splatnosti, Neuhrazeno. Volá je účet služby, ne heslo z formuláře.

CPQ čte hodnoty z `Data/cpq` vedle kořene obsahu. Ta složka v gitu není. Založení zásoby a `POST /api/pohoda/xml` přeposílají XML do mServeru. XML je Windows-1250. Odpověď se čte podle deklarace. Když deklarace říká UTF-8 a bajty UTF-8 nejsou, použije se Windows-1250.

Účet služby, ne v gitu:

- `Pohoda__Username`, `Pohoda__Password`, `Pohoda__Ico`
- volitelně `Pohoda__WriteUsername` a `Pohoda__WritePassword` jen pro zápis

V `appsettings.json` je `BaseUrl` `http://185.219.164.45:444`, aplikace `PHMTRANS`, prefix zásoby `PE` a pět číslic, timeout 180 sekund. HTTP klient nejde přes proxy. `appsettings.Production.json` je v `.gitignore`.

`/api/pohoda/status` heslo nepotřebuje. Když chybí účet služby, faktury vrátí `mServer vrátil 401:`, zatímco status zůstane v pořádku.

## Na co myslet při další úpravě

- Hesla, IČO a connection string do gitu nepatří. V `appsettings.json` zůstávají prázdné.
- HTML ve `wwwroot` a C# mají jiný postup nasazení. Nová tabulka `Disp_*` nebo `Crm_*` se na serveru objeví až po publikaci a startu služby.
- Zkouška na `localhost:8089` bez `X-Forwarded-Host` neukáže přesměrování hostitelů.
- Cookie nemá `Domain=.phmtrans.cz`. Intranet a staré hostitele nespojovat do jedné relace.
- `docs/dispecink-navrh.md` a `docs/crm-navrh.md` popisují první verze. Aktuální chování zakázek, synchronizace a účtů je v tomto souboru a v kódu.
