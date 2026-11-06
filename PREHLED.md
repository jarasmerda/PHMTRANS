# Přehled PHMTRANS

Jediný popis projektu, sdílený všemi feedy. Stav k 6. 11. 2026. Při změně chování nebo nastavení se upravuje ve stejném PR.
Jeden proces ASP.NET Core (.NET 10), stránky jsou statické soubory ve `wwwroot`.
Faktury čte Pohoda mServer, objednávky do něj zapisuje. Dispečink, CRM a účty intranetu jsou v SQL Serveru.

GitHub: https://github.com/jarasmerda/PHMTRANS

## Feedy (sessions Claude Code)

Práce na projektu je rozdělená do feedů podle modulů. Každý feed si sám načte `CLAUDE.md` (pravidla) a odtud tento soubor (popis projektu). Feed pracuje ve vývojovém klonu `C:\Projects\PHMTRANS`, každý ve vlastní větvi a pracovní kopii (worktree), změny jdou do `main` přes pull request.

| Feed | Řeší | Hlavní soubory |
| --- | --- | --- |
| **ERP** | reporty ERP (vydané faktury, měsíční přehledy), čtení z Pohody, oprávnění na reporty | `Bi/`, `Pohoda/`, `wwwroot/erp.*` |
| **Dispečink** | jízdy, zakázky, trasy a ceny, matice vzdáleností, fronta do Pohody | `Dispatch/`, `wwwroot/dispecink*` |

Sdílené soubory (`wwwroot/erp.css`, `Site/`, `Auth/`, `Program.cs`, `PREHLED.md`, `CLAUDE.md`) mění kterýkoli feed. Před prací `git pull origin main`, změna jako samostatný malý PR, v popisu PR napsat, že jde o sdílený soubor. `erp.css` nese verzi `?v=` ve všech stránkách: kdo ji zvedá, zvedne ji všude najednou (stránky i testy).

Nový feed (třeba CRM): přidat řádek do tabulky, založit session ve složce `C:\Projects\PHMTRANS` s vlastní pracovní kopií a do prvního zadání napsat, který modul řeší.

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
| `Dispatch/` | Jízdy, zakázky, trasy, ceny, verze, fronta do Pohody. |
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
| `Disp_Vehicle`, `Disp_Driver`, `Disp_Trip` | Vozidla, řidiči, jízdy. |
| `Disp_Order`, `Disp_OrderStop`, `Disp_OrderLeg` | Zakázka, vykládky, úseky. |
| `Disp_OrderVersion`, `Disp_SyncQueue` | Snímky verzí a fronta do Pohody. |
| `Disp_FuelType` | Číselník paliva. Seed: Motorová nafta, Benzín. |
| `Disp_Distance` | Matice vzdáleností. |
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

Deska má tři pohledy: Jízdy, Zakázky, Fronta Pohody. Založení jízdy i zakázky je samostatné okno. Seznamy umí víc stavů najednou a ve výchozím filtru vynechají storno.

**Jízdy.** Stavy Nová, Přiřazená, Na cestě, Vyložená, Zrušená, Stornováno. Kód `J{rok}-{čtyři číslice}`. Vozidlo a řidič z číselníku. Na jízdě je seznam jejích zakázek. Stornovaná jízda zakázky nepřijímá.

**Zakázky.** Stavy Nová, Naplánovaná, Na cestě, Doručeno, K fakturaci, Vyfakturováno, Stornováno. Kód `Z{rok}-{čtyři číslice}`. Zákazník z adresáře Pohody, na zakázce zůstane snímek názvu, IČO, DIČ a adresy. Hlavička má nakládku, datum, naložené litry, sazby a druh paliva. Vykládky tvoří úseky Nakládka → Vykládka 1 → Vykládka 2 … Každá vykládka má vlastní palivo, jinak převezme to z hlavičky.

**Ceny.** Prodej za kilometr, nebo za litr. Náklad úseku je km × Kč/km. Prodej je km × sazba, litry × sazba, nebo ruční cena úseku, plus příplatky. Deska ukazuje km, naloženo, vyloženo, náklad, prodej a marži v Kč, Kč/km a procentech. Vyložené litry nad naložené se jen upozorní.

**Matice vzdáleností.** `Disp_Distance` má normalizované `FromPlace` a `ToPlace`, A→B je totéž co B→A. U km je vidět `z matice`, nebo `ručně`. Chybějící nebo přepsané km se při uložení zakázky zapíšou i s uživatelem.

**Verze.** Každá obchodní změna přidá řádek `Disp_OrderVersion` se sazbami, vykládkami a úseky v `RouteJson`. Starší verze jde otevřít jen ke čtení. Přiřazení jízdy, vozidla a řidiče verzi ani frontu nedělá.

**Zápis do Pohody.** `Pohoda:SyncMode`: `Off` neposílá, `DryRun` jen uloží XML, `Live` (výchozí) volá mServer. Položka je `Leg N: odkud → kam, palivo, litry`. Náklad, vozidlo, řidič a jízda v XML nejsou.

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
