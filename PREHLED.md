# Přehled PHMTRANS

Jediný popis projektu, sdílený všemi feedy. Stav k 6. 11. 2026. Při změně chování nebo nastavení se upravuje ve stejném PR.
Jeden proces ASP.NET Core (.NET 10), stránky jsou statické soubory ve `wwwroot`.
Faktury čte Pohoda mServer, objednávky do něj zapisuje. Dispečink, CRM a účty intranetu jsou v SQL Serveru.

GitHub: https://github.com/jarasmerda/PHMTRANS

## Feedy (sessions Claude Code)

Práce na projektu je rozdělená do feedů. V aplikaci Claude jsou ve skupině **PHMTRANS** v postranním panelu. Každý feed si sám načte `CLAUDE.md` (pravidla) a odtud tento soubor (popis projektu). Všechny pracují ve vývojovém klonu `C:\Projects\PHMTRANS`, každý ve vlastní větvi a pracovní kopii (worktree). Změny jdou do `main` přes pull request.

| Feed | Řeší | Hlavní soubory |
| --- | --- | --- |
| **PHMTRANS** (hlavní) | celkový stav projektu a tento přehled, server a nasazení, propojení s Pohodou (mServer, adresář `dbo.AD`), databáze, zabezpečení, přihlášení a účty, veřejný web, společný vzhled | `PREHLED.md`, `CLAUDE.md`, `Program.cs`, `appsettings*.json`, `Site/`, `Auth/`, `Pohoda/PohodaMServerClient.cs`, `Pohoda/PohodaOptions.cs`, `wwwroot/erp.css`, `wwwroot/landing*`, `wwwroot/web.config` |
| **ERP** | reporty ERP (vydané faktury, měsíční přehledy) a oprávnění na ně | `Bi/`, `Pohoda/IssuedInvoiceExport.cs`, `wwwroot/erp.html`, `wwwroot/erp.js` |
| **DISPEČINK** | zakázky, kalendář řidičů, trasy a ceny, matice vzdáleností, fronta do Pohody | `Dispatch/`, `wwwroot/dispecink*` |

Pravidla mezi feedy:
- Soubory hlavního feedu mění ERP i DISPEČINK jen když to jejich úkol nutně potřebuje, jako samostatný malý PR a v popisu PR napíšou, že jde o sdílený soubor. Větší změny společných věcí (Pohoda, přihlášení, vzhled, server) patří do hlavního feedu.
- Když modulový feed změní nastavení, nasazení nebo propojení s Pohodou, upraví i příslušnou část tohoto přehledu ve stejném PR.
- `erp.css` nese verzi `?v=` ve všech stránkách: kdo ji zvedá, zvedne ji všude najednou (stránky i testy).
- Před prací vždy `git pull origin main`, feedy pracují souběžně.

Nový feed (třeba CRM): přidat řádek do tabulky, založit session ve složce `C:\Projects\PHMTRANS` s vlastní pracovní kopií, zařadit ji do skupiny PHMTRANS a do prvního zadání napsat, který modul řeší.

## Moduly

| Modul | Stránka | API | Data |
| --- | --- | --- | --- |
| Veřejný web | `landing.html` na `phmtrans.cz` | — | jen soubory |
| ERP | `/ERP` → `erp.html` | `/api/bi/issued-invoices`, `/api/bi/monthly-invoices`, `/api/bi/monthly-invoices-group-b` | Pohoda, vydané faktury, skupina z `dbo.AD` |
| Dispečink | `/Dispecink` → `dispecink.html` | `/api/dispatch/…` | SQL `Disp_*`, Pohoda objednávky |
| CRM | `/CRM` → `crm.html` | `/api/crm/…` | SQL `Crm_*` |
| Účty | `/login.html`, `/heslo`, `/Admin` | `/api/login`, `/api/auth/…`, `/api/admin/users` | SQL `Auth_User` |

`/health` vrací `status`, verzi s hashem commitu, `timeUtc` a větev. Slouží ke kontrole nasazení.

## Struktura

| Složka | Obsah |
| --- | --- |
| `Auth/` | Přihlášení, účty, limit pokusů. |
| `Bi/` | Vydané faktury pro ERP. |
| `Crm/` | CRM. |
| `Dispatch/` | Zakázky, kalendář řidičů, trasy, ceny, verze, fronta do Pohody. |
| `Pohoda/` | Klient mServeru, export faktur, adresář Pohody (SQL `dbo.AD`). |
| `Site/` | Hostitelé a přesměrování, kontrola oprávnění (`ModuleGate`), bezpečnostní hlavičky. |
| `wwwroot/` | Stránky, skripty, styly, obrázky. `web.config` je konfigurace IIS. |
| `tests/` | xUnit testy, úložiště v paměti a XSD Pohody. |

## Server

| Místo | Role |
| --- | --- |
| `C:\Projects\PHMTRANS` | Vývoj. `dotnet run` na `http://localhost:5053`, prostředí Development. |
| `C:\PHMTRANS` | Klon na Windows Serveru (`192.168.1.55`). Odtud `git pull`. |
| `C:\PHMTRANS\app` | Publikovaná služba. |
| `C:\PHMTRANS\wwwroot` | Stránky, které služba servíruje. |
| Služba Windows `PHMTRANS` | Start Automatic. Poslouchá jen na `http://127.0.0.1:8089`, jinou adresu nastaví `ASPNETCORE_URLS`. |
| IIS, web s id 3 | HTTPS pro všechny hostitele, ARR posílá na `http://127.0.0.1:8089`. Certifikát `*.phmtrans.cz`, SNI. |
| `SRV-POHODA` (`192.168.1.54`) | Pohoda mServer na portu 444, jen ve vnitřní síti. |

Z internetu jsou otevřené jen porty 80 a 443. Port 444 se na routeru nepřesměrovává a 8089 poslouchá jen na loopbacku. Pro `SRV-POHODA` má být na routeru rezervace DHCP, aby adresa zůstala `192.168.1.54`. V nastavení je IP, ne jméno: `SRV-POHODA` se může přeložit i na adresu Tailscale, přes kterou spojení neprojde.

## Nasazení

Změna C# i stránek (PowerShell jako správce):

```powershell
cd C:\PHMTRANS; git pull origin main; if ($?) { Stop-Service PHMTRANS; dotnet publish C:\PHMTRANS\PHMTRANS.csproj -c Release -o C:\PHMTRANS\app; Start-Service PHMTRANS; curl.exe http://127.0.0.1:8089/health }
```

Změna jen ve `wwwroot` stačí `git pull`, bez publikace a restartu. Schéma databáze se doplní samo při startu služby. Ve výpisu `/health` musí být hash posledního commitu. V PowerShellu je `curl` alias, volá se `curl.exe`.

## Adresy

| Host | Chování |
| --- | --- |
| `phmtrans.cz` | Veřejná prezentace. Totéž ukáže kořen každého hostitele kromě intranetu. |
| `www.phmtrans.cz` | `301` na `https://phmtrans.cz` se stejnou cestou. |
| `intranet.phmtrans.cz` | Moduly. Bez přihlášení jde kořen na login, po něm na první povolený modul. |
| `erp.`, `disp.`, `crm.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/ERP`, `/Dispecink`, `/CRM`. Platí i s `www.`. |

Cesta modulu nerozlišuje velikost písmen (`/erp` i `/ERP`). Pod prefixem fungují i soubory a API, třeba `/Dispecink/api/dispatch/board`.

Host se bere z `Request.Host`. `X-Forwarded-Host`, `X-Forwarded-Proto` a `X-Forwarded-For` se přijmou jen od důvěryhodné proxy, výchozí je loopback. Další proxy jdou doplnit v `ForwardedHeaders:KnownProxies`. Zkouška na serveru s `curl.exe -H "X-Forwarded-Host: intranet.phmtrans.cz" http://127.0.0.1:8089/` proto funguje.

## Zabezpečení

- Každé `/api/…` kromě `/api/login` a `/api/logout` vyžaduje přihlášení. Stránka modulu bez přihlášení jde na `/login.html`, modul bez oprávnění vrací 403.
- Cookie `phmtrans-auth`: 12 hodin s posunem, `HttpOnly`, `SameSite=Lax`, v produkci `Secure`, jen pro hostitele, kde vznikla.
- Pět špatných hesel zamkne účet na 15 minut. Z jedné adresy nejvýš 20 pokusů za minutu, adresa je skutečná IP klienta.
- Hlavičky: `Content-Security-Policy`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`, HSTS na HTTPS. API má `Cache-Control: no-store`.
- Zbývá v IIS: zapnout HSTS (IIS nepředává `X-Forwarded-Proto`, aplikace HTTPS nevidí) a odebrat `X-Powered-By` na úrovni serveru.
- Hesla, IČO a connection string do gitu nepatří. V `appsettings.json` jsou prázdné, `appsettings.Production.json` je v `.gitignore`.

## Přihlášení a účty

Uživatelé jsou v `dbo.Auth_User`, heslo je hash ASP.NET Core `PasswordHasher`. Účet má příznak správce, aktivní a povolené moduly ERP, Dispečink a CRM. U ERP se navíc vybírají jednotlivé reporty (`Auth_User.ErpReports`, kódy čárkami: `faktury`, `mesicni`, `mesicni-b`). `NULL` znamená všechny reporty, mají ho účty založené dřív. Vypnutý účet se nepřihlásí a otevřená relace na dalším požadavku spadne. Posledního aktivního správce nejde odebrat.

`/heslo` mění vlastní heslo. `/Admin` je jen pro správce: založení, moduly a reporty ERP, nové heslo, vypnutí. S modulem ERP musí být vybraný aspoň jeden report.

První správce se založí, jen když v tabulce žádný není, z proměnných počítače `Intranet__AdminUsername` a `Intranet__AdminPassword` (aspoň 8 znaků).

## Databáze

Connection string `ConnectionStrings:Dispatch` je na serveru proměnná `ConnectionStrings__Dispatch`. Databáze `Smerco_26950430_2026`. Skripty při startu tabulku založí nebo sloupec přidají, jen když chybí. Nemají `DROP`, `DELETE` ani `TRUNCATE`.

Bez connection stringu služba nastartuje a `/health` je v pořádku, přihlášení, dispečink a CRM vrátí, že připojení není nastavené. Bez SQL lokálně aplikace nejde vyzkoušet, testy používají úložiště v paměti.

Adresář Pohody `dbo.AD` čte našeptávač zákazníka a měsíční přehled faktur (sloupec `Skupina`) z `StwPh_26950430_2026` (`Pohoda:AddressBookDatabase`) trojdílným jménem, jen `SELECT`.

| Tabulky | K čemu |
| --- | --- |
| `Auth_User` | Účty intranetu. |
| `Disp_Vehicle`, `Disp_Driver` | Vozidla, řidiči. |
| `Disp_Trip` | Dřívější jízdy. Aplikace je už nepoužívá, tabulka a sloupec `Disp_Order.TripId` jen zůstaly v databázi. |
| `Disp_VehicleCompartment` | Přepážky cisteren: vozidlo, číslo přepážky 1–10, objem v litrech. |
| `Disp_Order`, `Disp_OrderStop`, `Disp_OrderLeg` | Zakázka, vykládky, úseky. |
| `Disp_OrderLoad` | Nakládka zakázky po palivech: palivo a naložené litry. |
| `Disp_OrderVersion`, `Disp_SyncQueue` | Snímky verzí a fronta do Pohody. |
| `Disp_FuelType` | Číselník paliva. Seed: Motorová nafta, Benzín. |
| `Disp_Point` | Body trasy: `Name`, `IsLoading` (nakládka), `IsUnloading` (vykládka), `Active`. |
| `Disp_LoadingPlace` | Dřívější seznam nakládek, jen zdroj pro první naplnění `Disp_Point`. |
| `Disp_Distance` | Matice vzdáleností: nákladové `Km` a fakturované `BilledKm` (může být prázdné). |
| `Disp_Setting` | Výchozí náklad Kč/km. |
| `Crm_Customer`, `Crm_Contact`, `Crm_Opportunity`, `Crm_Activity` | CRM. |

## Pohoda mServer

`Pohoda:BaseUrl` je `http://192.168.1.54:444`, aplikace `PHMTRANS`, timeout 180 s, bez proxy. Účet služby je mimo git: `Pohoda__Username`, `Pohoda__Password`, `Pohoda__Ico`, volitelně `Pohoda__WriteUsername` a `Pohoda__WritePassword` jen pro zápis. Bez účtu vrátí faktury `mServer vrátil 401:`.

XML je Windows-1250. Odpověď se čte podle deklarace, a když deklarace říká UTF-8 a bajty UTF-8 nejsou, použije se Windows-1250.

## ERP

Tři reporty, přepínají se záložkami (`/ERP#faktury`, `/ERP#mesicni`, `/ERP#mesicni-b`). Uživatel vidí jen záložky povolených reportů, API nepovoleného reportu vrací 403 (`ModuleGate`, `ErpReports.ForApiPath`). Nový report se přidává do `ErpReports` v `Auth/AuthModels.cs`, do `admin.html` a do `erp.js`. Faktury jsou `issuedInvoice`, částky v domácí měně.

- **Vydané faktury:** doklady za období s filtrem odběratele, částky s DPH. Stavy: Uhrazeno, Částečně, Po splatnosti, Neuhrazeno.
- **Vydané faktury – měsíční přehled:** za zvolený rok tabulka odběratel × měsíc s mezisoučty 1Q–4Q a celkem, částky **bez DPH** podle data vystavení. Odběratel se páruje s adresářem podle `partnerIdentity/id` z faktury (ID v `dbo.AD`), když chybí, tak podle IČO. Odběratelé s `dbo.AD.Skupina = 'B'` jsou ve druhé tabulce, ostatní v první. Řádky jsou seřazené od největšího obratu, zobrazí se jen odběratelé s fakturou v daném roce.
- **Vydané faktury – měsíční přehled skupiny B:** stejná tabulka jen s odběrateli skupiny B. API (`/api/bi/monthly-invoices-group-b`) ostatní odběratele vůbec nevrací, takže uživatel s tímto reportem nevidí obrat ostatních.

## Dispečink

Deska má dvě záložky: Zakázky a Fronta Pohody. Založení zakázky je samostatné okno. Na záložce Zakázky je tlačítko **Kalendář řidičů** a pod seznamem nákladové sazby. Seznamy umí víc stavů najednou a ve výchozím filtru vynechají storno.

**Cisterny a přepážky.** Vozidlo má až 10 přepážek, každá s vlastním objemem v litrech (`Disp_VehicleCompartment`). Zadávají se zatím přímo v SQL, deska je ukáže u nákladových sazeb vozidel (počet, součet a objemy). Vozidlo bez řádků má „přepážky nezadané“. Řádek s číslem mimo 1–10 nebo nulovým objemem se ignoruje. Příklad:

```sql
INSERT INTO dbo.Disp_VehicleCompartment (VehicleId, Number, CapacityLiters)
VALUES (N'id-vozidla', 1, 8000), (N'id-vozidla', 2, 6000);
```

**Body (nakládky a vykládky).** Nakládka zakázky i místo každé vykládky se vybírá ze seznamu `Disp_Point` (jen `Active = 1`). Nakládka nabízí body s `IsLoading = 1`, vykládka body s `IsUnloading = 1` (API `/api/dispatch/loading-places` a `/api/dispatch/points`). Při prvním startu se `Disp_Point` naplní z `Disp_LoadingPlace` a z míst vykládek uložených zakázek (příznak `PointsSeeded` v `Disp_Setting`). Další body se zadávají v SQL, např. `INSERT INTO dbo.Disp_Point (Name, IsLoading, IsUnloading) VALUES (N'ČS Brno', 0, 1);`. Starší hodnota mimo seznam se ukáže jako „mimo seznam“. Když je seznam prázdný, zůstane textové pole. Na zakázce se ukládá název bodu a km se hledají v matici podle něj.

**Kalendář řidičů.** Tabulka řidič × den (1, 2 nebo 4 týdny, výchozí od pondělí tohoto týdne), API `GET /api/dispatch/calendar?from=RRRR-MM-DD&days=14`. Řidič je obsazený od data nakládky do data vykládky zakázky, stornované se nepočítají. U řidiče je počet obsazených dní a procento. Den s víc zakázkami najednou je červeně. Zakázky bez řidiče jsou v řádku „Bez řidiče“. Kliknutí na zakázku ji otevře. Řidič a vozidlo se vybírají přímo na zakázce. Výběr **Řidič** v nové i otevřené zakázce podle data nakládky a vykládky ukazuje, kdo je volný a kdo obsazený (a kterou zakázkou). Volní jsou nahoře. Obsazeného jde vybrat, jen se ukáže upozornění. Tlačítko **Kalendář řidičů** přímo ve formuláři rozbalí tabulku řidič × den od 3 dnů před nakládkou do 7 dnů po vykládce, dny zakázky jsou orámované (zeleně volno, červeně jiná zakázka) a klepnutím na jméno se řidič vybere. Data bere ze stejného API kalendáře.

**Naplánovat čas řidiče.** Tlačítko ve formuláři zakázky (nové i otevřené) rozbalí den po hodinách s řádkem pro každého řidiče, šedě jsou jeho jiné zakázky. Tažením myší přes volný úsek (na mobilu klepnutím na začátek, délka podle formuláře, jinak 8 h) se vyplní řidič, datum a čas nakládky a vykládky, po 15 minutách. Kolize se stejným řidičem se zvýrazní červeně. Uložením zakázky se čas objeví v kalendáři po hodinách. Výběr Řidič počítá obsazenost po hodinách (překryv času od–do), když jsou časy k dispozici. Skript `dispecink-booking.js`.

**Kalendář po hodinách.** Tlačítko na záložce Zakázky a tlačítko „Vytíženost po hodinách“ ve formuláři zakázky (otevře týden data nakládky, `#tyden=RRRR-MM-DD`) otevře vlastní okno (dispečink s `#tyden`): týden Po–Ne, řádky hodiny, každý řidič má svou barvu a nahoře zaškrtávátka, koho zobrazit (pamatuje si prohlížeč). Zakázka je blok od času nakládky do času vykládky, zakázky bez času jsou čárkované (6:00–14:00), překryvy jsou vedle sebe. Klepnutí na zakázku ji otevře v hlavním okně dispečinku (`#zakazka=ID`), klepnutí do volného místa tam založí novou zakázku na ten den a půlhodinu, s řidičem, když je zobrazený jen jeden (`#nova=RRRR-MM-DDTHH:MM&ridic=ID`). Okno se obnovuje každou minutu a při návratu do okna. API `GET /api/dispatch/calendar/events?from=RRRR-MM-DD&days=7`.

**Doklady k zakázce.** V detailu zakázky je sekce Doklady s tlačítky **Vyfotit doklad** (v mobilu otevře fotoaparát) a **Přiložit soubor**. Povolené jsou JPG, PNG, WEBP, HEIC a PDF, nejvýš 10 souborů po 25 MB najednou. Velkou fotku prohlížeč před odesláním zmenší na 2400 px. Soubory nejsou v SQL, ukládají se do složky `Dispatch:DocumentsPath`, do podsložky podle kódu zakázky, např. `…\Zakázky\Z2026-0001\20261106-083015-dodací list.jpg`. Nic se nepřepisuje ani nemaže. Seznam dokladů se čte přímo ze složky, takže tam jsou vidět i soubory přidané přes OneDrive. Složka má být uvnitř OneDrive, který běží na serveru pod firemním účtem a synchronizuje ji do cloudu. Účet služby `PHMTRANS` potřebuje do složky právo zápisu. Cesta se nastaví mimo git v `appsettings.Production.json` (`"Dispatch": { "DocumentsPath": "…" }`) nebo proměnnou prostředí `Dispatch__DocumentsPath`, platí po restartu služby. Bez nastavené cesty sekce napíše, že složka není nastavená. API je `GET` a `POST /api/dispatch/orders/{id}/documents` a `GET /api/dispatch/orders/{id}/documents/{název}`.

**Zakázky.** Stavy Nová, Naplánovaná, Na cestě, Doručeno, K fakturaci, Vyfakturováno, Stornováno. Kód `Z{rok}-{čtyři číslice}`. Zákazník z adresáře Pohody, na zakázce zůstane snímek názvu, IČO, DIČ a adresy. Hlavička má nakládku, data a sazby. U data nakládky a vykládky je i čas (`Disp_Order.LoadingTime`, `UnloadingTime`, typ `time`), nová zakázka má předvyplněno 6:00 až 14:00. Zakázka bez času se v hodinovém kalendáři bere jako 6:00 až 14:00. Čas do Pohody nejde, změna času jen přidá verzi. **Nakládka po palivech** (`Disp_OrderLoad`) má řádek pro každé palivo s naloženými litry, nejvýš 10 paliv a každé jen jednou. `Disp_Order.Liters` je součet a `Disp_Order.FuelType` je první palivo. Starší zakázka bez řádků se bere jako jedno palivo z hlavičky. Každé palivo může mít přiřazené **přepážky** vybraného vozidla (`Disp_OrderLoad.Compartments`, např. `1,2`). Jedna přepážka smí mít jen jedno palivo, vozidlo ji musí mít a naložené litry se musí vejít do kapacity přepážek. Jinak zakázka nejde uložit. Bez vozidla přepážky vybrat nejde. Přepážky do Pohody nejdou, jejich změna jen přidá verzi. Vykládky tvoří úseky Nakládka → Vykládka 1 → Vykládka 2 … Každá vykládka má vlastní palivo, jinak převezme první palivo nakládky. Deska upozorní, když se u některého paliva vyloží víc, než se naložilo. Změna nakládky je obchodní změna a přidá verzi.

**Ceny.** Prodej za kilometr, nebo za litr. Náklad úseku je nákladové km × Kč/km. Prodej za km počítá s fakturovanými km, a když jsou prázdné, s nákladovými. Jinak litry × sazba, nebo ruční cena úseku, plus příplatky. Deska ukazuje km, naloženo, vyloženo, náklad, prodej a marži v Kč, Kč/km a procentech. Vyložené litry nad naložené (celkem nebo u jednoho paliva) se jen upozorní.

**Matice vzdáleností.** `Disp_Distance` má normalizované `FromPlace` a `ToPlace`, A→B je totéž co B→A. Každý pár má **nákladové km** (`Km`) a **fakturované km** (`BilledKm`, může být prázdné). Každá vykládka ukazuje svůj úsek (Nakládka → Vykládka 1, Vykládka 1 → Vykládka 2 …) a obě hodnoty se doplní z matice. U km je vidět `z matice`, nebo `ručně`. Když pár chybí nebo se km ručně změní, objeví se tlačítko **Uložit do matice**, které zapíše nákladové i fakturované km i s uživatelem (`POST /api/dispatch/distances`). Chybějící nebo přepsané nákladové km se zapíšou i při uložení zakázky, fakturované km v matici tím nezmizí. Úsek zakázky si obě hodnoty pamatuje (`Disp_OrderLeg.BilledKm`).

**Verze.** Každá obchodní změna přidá řádek `Disp_OrderVersion` se sazbami, vykládkami a úseky v `RouteJson`. Starší verze jde otevřít jen ke čtení. Přiřazení vozidla a řidiče verzi ani frontu nedělá.

**Zápis do Pohody.** `Pohoda:SyncMode`: `Off` neposílá, `DryRun` jen uloží XML, `Live` (výchozí) volá mServer. Položka je `Leg N: odkud → kam, palivo, litry`. Náklad, vozidlo a řidič v XML nejsou.

- Neodeslaná změna přepíše otevřenou položku fronty.
- Odeslaná neuzamčená objednávka se mění smazáním a novým založením (`ord:delete` a `ord:add`). Když smazání projde a založení ne, další pokus už jen zakládá. Doklad, který v Pohodě už není, se bere jako smazaný.
- Uzamčená (vyfakturovaná) objednávka se nemění. Vznikne opravný doklad `{kód}/{pořadí}`, např. `Z2026-0001/1`, se zápornými položkami. Stejně se dělá storno.
- Neuzamčená odeslaná zakázka se při stornu v Pohodě smaže a v aplikaci zůstane Stornováno. Zakázka, která v Pohodě nebyla, jde smazat natrvalo.
- Číslo dokladu má nejvýš 32 znaků, delší opravné číslo se odmítne před frontou.

## CRM

Zákazník je `Firma` nebo `Osoba`, kontakt visí na zákazníkovi. Příležitost má fázi Nový, V jednání, Nabídka, Vyhráno, Prohráno. Aktivita (hovor, schůzka, poznámka) visí na zákazníkovi, příležitosti, nebo na obou. Kódy `C{rok}-0001`, `O{rok}-0001`, `A{rok}-0001`. Do Pohody CRM nezapisuje.

## Fotky a písmo

| Soubor ve `wwwroot/landing/` | Autor | Licence |
| --- | --- | --- |
| `truck-white-scania-*.webp` | Zieben VH, Unsplash | Unsplash License |
| `truck-sunset-*.webp` | Natalia Marcelewicz, Unsplash | Unsplash License |
| `tanker-scania-*.webp` | Boris Hamer, Pexels | Pexels License |
| `logo.png`, `logo-dark.png`, `logo-drop-pale.webp` | vlastní logo firmy | — |

Písmo Barlow a Barlow Condensed je pod SIL Open Font License 1.1, text licence je v `wwwroot/landing/fonts/OFL.txt`.
