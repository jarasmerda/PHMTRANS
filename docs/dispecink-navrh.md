# Návrh dispečerského modulu PHMTRANS

Podklad pro dispečink na `disp.phmtrans.cz`. Aplikace je jeden ASP.NET Core (.NET 10) Minimal API projekt. Účetnictví čte z Pohoda mServeru, stránky jsou statické soubory ve `wwwroot` a v produkci běží jako služba Windows `PHMTRANS` na portu 8089. ERP je na `erp.phmtrans.cz`, rozcestník odkazuje na Dispečink absolutní adresou `https://disp.phmtrans.cz/`.

Tento dokument popisuje cílový modul i to, co je v první verzi opravdu v kódu. První verze je pracovní deska jízd. Mapa, notifikace a zápis do Pohody jsou další fáze, ne nedodělek uvnitř stejné obrazovky.

## Východiska z dnešního kódu

Nový modul kopíruje existující řezy, nepřidává novou hostitelskou aplikaci.

| Vzor v repozitáři | Použití v dispečinku |
| --- | --- |
| `Bi/BiEndpoints.cs` registruje `MapBi()` a middleware podle hostitele | `Dispatch/DispatchEndpoints.cs` registruje `MapDispatch()` |
| Host se bere z `X-Forwarded-Host`, jinak z `Request.Host` | Stejně. `erp.phmtrans.cz` a `www.erp.phmtrans.cz` dál řeší jen BI middleware |
| Cookie `phmtrans-auth`, 12 hodin, posuvná, login přes mServer | Stejný `POST /api/login` a `POST /api/logout`. Heslo dispečera se proti Pohodě jen ověří |
| Stránka `erp.html` + `erp.js` + proměnné v `erp.css` | `dispecink.html` + `dispecink.js` linkují `erp.css` a nepřepisují ho |
| `IssuedInvoiceCache` jako jeden singleton | `SqlDispatchStore` jako singleton nad SQL Serverem |
| Hesla Pohody jen v proměnných počítače | Connection string dispečinku jen v `ConnectionStrings__Dispatch`, v gitu je prázdný |

ERP filtr odběratelů (`InvoiceCustomerFilter`), přehled faktur a CPQ se tímto modulem nemění. Cookie nemá `Domain=.phmtrans.cz`, takže přihlášení na ERP a na dispečinku jsou dvě samostatné session se stejnými údaji Pohody. Rozšíření cookie na celou doménu by změnilo chování ERP, proto tu není.

## Pojmy

- **Zakázka** je obchodní požadavek zákazníka. Později to bude přijatá objednávka Pohody. V datech je na jízdě volitelné `orderCode`.
- **Jízda** je jeden výkon: odkud, kam, termín, vozidlo, řidič a stav. První verze pracuje s jízdou. Jedna zakázka může později mít víc jízd (překládka, druhý závoz). Teď je vztah 1:1 a obě čísla mohou být vyplněná nezávisle.
- **Vozidlo** a **řidič** jsou číselníky v tabulkách `Disp_Vehicle` a `Disp_Driver`. Deska je jen čte a přiřazuje. Nový řádek se zatím vkládá do SQL, samostatná správa v aplikaci není.

## První verze

Umí tohle:

- přihlášení stejnou Pohoda cookie jako ERP
- přehled jízd se stavem a součty
- založení jízdy
- přiřazení vozidla a řidiče z číselníku
- ruční změnu stavu
- kořen `disp.phmtrans.cz` a `www.disp.phmtrans.cz` otevře desku (ERP host se nemění)
- úložiště je SQL Server, tabulky `Disp_*`. V odpovědi API je `storage: "sql"`. Do Pohody se nezapisuje

Záměrně v první verzi není: mapa, GPS, notifikace, import objednávek, zápis faktury, správa vozového parku v UI, účty řidičů, historie stavů.

Chybějící `ConnectionStrings:Dispatch` aplikaci neshodí. ERP běží dál a dispečink vrátí text, že connection string není nastavený. Prázdná databáze nemá ukázkové jízdy.

## Obrazovka

`/dispecink.html`

- hlavička ve stejném tvaru jako ERP: značka PHM TRANS, odkaz Rozcestník na `https://www.phmtrans.cz/`, odhlášení
- věta z API, že data jsou v SQL a do Pohody se nezapisuje
- pět karet: Nové, Přiřazené, Na cestě, Vyložené, Zrušené
- filtr stavu a obnovení
- tabulka jízd: kód jízdy, zakázka, termín, zákazník, trasa, náklad, výběr vozidla, výběr řidiče, výběr stavu, Uložit
- formulář Nová jízda pod tabulkou

Vzhled bere barvy, písmo, karty, tabulku a tlačítka z `erp.css` (`--bg`, `--surface`, `--text`, `--accent`, `--border`, …). Doplňky, které ERP nemá (poznámka, šířka selectů v tabulce), jsou jen ve stránce dispečinku.

Na hostiteli dispečinku jde nepřihlášený kořen, `index.html` i deska na `/login.html`. Po přihlášení se kořen, `index.html` a `login.html` vnitřně přepíší na `/dispecink.html`, adresa v prohlížeči zůstane `/`. To je stejný trik jako ERP middleware, který na `erp.phmtrans.cz` přepisuje kořen na `erp.html`.

Mimo tyto hostitele (IP, `localhost:5053`) zůstane kořen rozcestník. Deska je na `/dispecink.html` a bez cookie vrací přesměrování na login. Po přihlášení z login formuláře prohlížeč přejde na `/`, protože tak je napsané `login.html` pro ERP. Na běžném hostiteli je pak potřeba otevřít `/dispecink.html` znovu. Na `disp.phmtrans.cz` to není potřeba, kořen už je deska.

## Stavy jízdy

Řetězec stavu je český text, stejně jako u faktur (`Uhrazeno`, `Po splatnosti`).

| Stav | Význam |
| --- | --- |
| `Nová` | Jízda je založená, chybí vozidlo nebo řidič |
| `Přiřazená` | Vozidlo i řidič jsou vybraní, auto ještě nejelo |
| `Na cestě` | Řidič vyjel |
| `Vyložená` | Vykládka je hotová |
| `Zrušená` | Jízda se nepojede |

První verze dovolí dispečerovi nastavit kterýkoli z těchto stavů. Má opravovat omyl bez slepé uličky. Pozdější fáze může přechody přitvrdit, až bude stav měnit i řidič z telefonu.

Výjimka při uložení přiřazení, když dispečer stav ve formuláři nezměnil:

- `Nová` a jsou vyplněná vozidla i řidič → `Přiřazená`
- `Přiřazená` a vozidlo nebo řidič chybí → `Nová`
- `Na cestě`, `Vyložená` a `Zrušená` se samotným přiřazením nemění

Když dispečer stav ve výběru změní, uloží se ta vybraná hodnota a automatika ji nepřebije.

## Datový model

Cílová podoba. V SQL jsou teď vozidla, řidiči a jízdy. Ostatní entity jsou smlouva pro další fáze, nové sloupce se doplní migrací, ne přepsáním cizí tabulky. Pole, která verze 1 neukládá, jsou označená.

### Vozidlo

| Pole | Verze 1 | Poznámka |
| --- | --- | --- |
| `id` | ano | stabilní klíč, ne SPZ |
| `plate` | ano | SPZ, text |
| `label` | ano | např. `Cisterna MAN 32 m³` |
| `active` | ne | vyřazené auto se nemá nabízet |
| `capacity` | ne | objem nebo hmotnost |

### Řidič

| Pole | Verze 1 | Poznámka |
| --- | --- | --- |
| `id` | ano | |
| `name` | ano | |
| `phone` | ano | zobrazí se jako nápověda u volby |
| `active` | ne | |
| `userName` | ne | vazba na účet Pohody, až bude řidič sám měnit stav |

### Jízda

| Pole | Verze 1 | Poznámka |
| --- | --- | --- |
| `id` | ano | `trip-…` u ukázky, dál `Guid` |
| `code` | ano | `J2026-0145`, generuje úložiště, v rámci roku roste |
| `orderCode` | ano | číslo zakázky, později číslo objednávky Pohody |
| `customer` | ano | text, ne adresář Pohody |
| `loadingPlace`, `unloadingPlace` | ano | text adresy, bez souřadnic |
| `cargo` | ano | např. `motorová nafta` |
| `plannedStart` | ano | `DateTimeOffset` |
| `plannedEnd`, `startedAt`, `finishedAt` | ne | skutečné časy pro zpoždění a notifikace |
| `status` | ano | jeden z pěti textů |
| `vehicleId`, `driverId` | ano | cizí klíč do číselníku, nebo `null` |
| `note` | ano | |
| `updatedAt` | ano | čas poslední změny v souboru |
| `pohodaOrderId`, `pohodaInvoiceNumber` | ne | vazba na objednávku a vydanou fakturu |
| `loadingPoint`, `unloadingPoint` | ne | `{ lat, lng }` až s mapou |

### Poloha (fáze mapy)

`vehicleId`, `lat`, `lng`, `recordedAt`, `source` (`phone` nebo `unit`). Do první verze se nezapisuje.

### Událost (fáze notifikací a auditu)

`tripId`, `at`, `kind` (`status`, `assign`, `delay`), `message`, `channel` (`board`, `mail`, `sms`). Dnes je audit jen `updatedAt` a log služby.

## API

Všechno pod `/api/dispatch` vyžaduje cookie. Bez ní je `401` a JSON `{ "message": "Přihlaste se." }`, ne přesměrování na HTML. Tak se chová i `/api/bi`.

| Metoda a cesta | Verze 1 | Tělo / odpověď |
| --- | --- | --- |
| `GET /api/dispatch/board` | ano | deska: `storage`, `storageNote`, `counts`, `trips`, `vehicles`, `drivers` |
| `GET /api/dispatch/trips?status=` | ano | pole jízd, `status` volitelný |
| `POST /api/dispatch/trips` | ano | založí `Nová`, vrátí jízdu včetně `code` |
| `POST /api/dispatch/trips/{id}/assign` | ano | `{ "vehicleId", "driverId" }`, prázdný řetězec maže |
| `POST /api/dispatch/trips/{id}/status` | ano | `{ "status" }` |
| `GET/POST /api/dispatch/vehicles` | ne | správa parku |
| `GET/POST /api/dispatch/drivers` | ne | správa řidičů |
| `POST /api/dispatch/import/pohoda` | ne | načte přijaté objednávky do jízd |
| `GET /api/dispatch/positions` | ne | poslední polohy pro mapu |
| `POST /api/dispatch/positions` | ne | zápis polohy z telefonu nebo jednotky |
| `POST /api/dispatch/notify` | ne | ruční nebo plánovaná notifikace |

`POST /api/dispatch/trips`:

```json
{
  "orderCode": "Z2026-0150",
  "customer": "Velkoobchod Demo s.r.o.",
  "loadingPlace": "Sklad Cerekvice",
  "unloadingPlace": "ČS Praha – ukázka",
  "cargo": "motorová nafta",
  "plannedStart": "2026-11-02T06:00:00Z",
  "note": ""
}
```

Neznámá jízda je `404`. Neznámé vozidlo, řidič nebo stav je `400` s `{ "message" }`. Nedostupný SQL Server nebo chybějící connection string je `500` s českým `{ "message" }`. `storage` je vždy `"sql"`.

Kód jízdy je `J{rok}-{čtyři číslice}`. Čítač je nejvyšší známé číslo v daném roce plus jedna. Rok se bere z času serveru.

## SQL úložiště

Třída `SqlDispatchStore`, přístup přes `Microsoft.Data.SqlClient` a Dapper. Connection string ukazuje na `Smerco_26950430_2026` na `192.168.1.54\POHODASQL`. V ní jsou tabulky dispečinku, CRM i přihlášení. Aplikace do Pohody nezapisuje.

Adresář Pohody `dbo.AD` v té databázi není. Našeptávač zákazníka ho čte z firemní databáze `StwPh_26950430_2026` trojdílným jménem `[StwPh_26950430_2026].dbo.AD` (`Pohoda:AddressBookDatabase`). Účet `phmtrans_dispatch` potřebuje na té tabulce jen `SELECT`.

Connection string je jen `ConnectionStrings:Dispatch`. Na serveru je to proměnná počítače `ConnectionStrings__Dispatch`. V `appsettings.json` je prázdný řetězec, heslo v gitu není.

Při startu služby se na pozadí spustí idempotentní skript. Když connection string chybí nebo SQL neodpoví, start ERP pokračuje a chyba se jen zaloguje. Další požadavek na dispečink schéma zkusí znovu.

Skript zakládá tabulku jen když `OBJECT_ID` je prázdné. Nemá `DROP`, `DELETE`, `ALTER` ani `UPDATE` a neobsahuje jméno databáze, takže nepřepne kontext na jinou databázi.

| Tabulka | Sloupce |
| --- | --- |
| `dbo.Disp_Vehicle` | `Id nvarchar(64)` PK, `Plate nvarchar(32)`, `Label nvarchar(160)` |
| `dbo.Disp_Driver` | `Id nvarchar(64)` PK, `Name nvarchar(160)`, `Phone nvarchar(40)` NULL |
| `dbo.Disp_Trip` | `Id` PK, `Code nvarchar(20)` unique, `OrderCode nvarchar(40)` NULL, `Customer`, `LoadingPlace`, `UnloadingPlace` po `nvarchar(160)`, `Cargo nvarchar(80)`, `PlannedStart datetimeoffset(3)`, `Status nvarchar(20)`, `VehicleId` a `DriverId` NULL s cizím klíčem na číselník, `Note nvarchar(500)` NULL, `UpdatedAt datetimeoffset(3)` |

Zápis jízdy bere aplikační zámek `PHMTRANS_Disp` v transakci, ať dva požadavky nevyrobí stejný kód. Číselníky deska nemění. Vozidlo a řidič se vloží ručně, například:

```sql
INSERT INTO dbo.Disp_Vehicle (Id, Plate, Label) VALUES (N'veh-1', N'1A2 3456', N'Cisterna MAN 32 m³');
INSERT INTO dbo.Disp_Driver (Id, Name, Phone) VALUES (N'drv-1', N'Jan Novák', N'+420 601 000 001');
```

## Napojení na ERP a Pohodu

Dispečer se hlásí jménem a heslem uživatele mServeru (`CredentialsAreValidAsync`). Další volání Pohody, stejně jako faktury a CPQ, půjdou účtem služby (`Pohoda__Username`, `Pohoda__Password`, `Pohoda__Ico`), ne heslem z formuláře.

První verze Pohodu po přihlášení už nevolá a nic do ní nezapisuje.

Další fáze, ve stejném klientovi `PohodaMServerClient` a se stejnou obálkou `dataPack` ve Windows-1250:

1. **Čtení přijatých objednávek** (`receivedOrder`) za období, analogicky k `listIssuedInvoice`. Z objednávky vznikne jízda: číslo dokladu → `orderCode`, partner → `customer`, text dokladu se navrhne do poznámky. Nakládka a vykládka v Pohodě často nejsou strukturované, dispečer je doplní.
2. **Adresář** partnerů jako našeptávač zákazníka. Do té doby je zákazník volný text, ať modul nevisí na číselníku, který ještě nemáme načtený.
3. **Vazba na vydanou fakturu**, kterou ERP už umí. Po vykládce se na jízdu doplní `pohodaInvoiceNumber`. Vznik faktury dál zůstane v Pohodě. Dispečink ji v první vlně nezakládá, aby se účetní doklad nezaložil omylem z desky.
4. **Středisko / činnost** podle vozidla, až bude v Pohodě číselník, který odpovídá SPZ.

ERP stránka faktur se kvůli tomu nemění. Proklik z jízdy na fakturu může být později jen odkaz na `erp.phmtrans.cz` s číslem dokladu, filtr odběratele zůstane, jak je.

## Mapa

Samostatný krok. Deska souřadnice vozidel neukládá.

- stránka `/dispecink-mapa.html` vedle desky, stejná cookie
- podkladové dlaždice (OpenStreetMap nebo firemní podklad, až bude licence)
- bod vozidla z poslední polohy, barva podle stavu jízdy
- nakládka a vykládka až po geokódování textu, ne ručně u každé jízdy v první mapě
- deska v první verzi mapu neimituje

Zdroj polohy až potom: nejdřív telefon řidiče (fáze mobilního pohledu), jednotka ve vozidle jako náhrada, když telefon nebude.

## Notifikace

Na desce jsou zatím jen součty stavů. To je celý signál první verze.

Pořadí, až bude komu je posílat:

1. V desce zvýraznit `Nová` jízda s termínem do dvou hodin a `Na cestě` po plánovaném konci. K tomu chybí `plannedEnd`.
2. Mail dispečerovi na adresu z konfigurace, ne z gitu.
3. SMS nebo zpráva řidiči na `phone`. Odeslání přes poskytovatele, šablona v češtině, u jízdy záznam události.
4. Prohlížečové notifikace jen jako doplněk, ne jako jediný kanál. Dispečink bývá otevřený pořád, ale záložka uspaná notifikaci neunese.

Kanál se zapne až vypínačem v nastavení služby, ne sám od sebe při uložení jízdy.

## Fáze

1. **Deska.** Jízdy, přiřazení, stavy, cookie, hostitel. Data jsou v SQL, tabulky `Disp_*`.
2. **Správa číselníků.** Obrazovka pro vozidla a řidiče, `active`, historie stavů. Endpointy desky zůstanou.
3. **Pohoda.** Import přijatých objednávek, našeptávač partnera, číslo vydané faktury na jízdě. Pořád bez automatického vystavení faktury.
4. **Mapa a poloha.** Stránka mapy, body vozidel, později geokódování nakládky a vykládky.
5. **Notifikace.** Nejdřív zvýraznění na desce, pak mail, pak SMS řidiči.
6. **Pohled řidiče.** Úzká stránka, jen jeho jízdy, přechod `Přiřazená` → `Na cestě` → `Vyložená`, odeslání polohy. Dispečer dál smí stav přepsat.

Fáze 2 je podmínka pro pohodlnou správu parku. Mapa a notifikace potřebují další sloupce, ne další JSON.

## Nasazení `disp.phmtrans.cz`

Odkaz z webu už vede na `disp.phmtrans.cz`. `404` vrací IIS, požadavek se ke službě na 8089 nedostane. Samotný `git pull` to nespraví.

Kód aplikace, včetně `wwwroot` (stránka je v `wwwroot`, ale endpointy jsou v C#):

```powershell
Stop-Service PHMTRANS
cd C:\PHMTRANS
git pull origin main
dotnet publish -c Release -o C:\PHMTRANS\app
Start-Service PHMTRANS
curl.exe http://localhost:8089/health
```

Samotná změna HTML ve `wwwroot` by restart nepotřebovala. Tady se mění `Program.cs` a nové třídy, takže publish a restart ano.

Ověření na serveru:

```powershell
curl.exe -D - http://localhost:8089/dispecink.html
curl.exe http://localhost:8089/api/dispatch/board
curl.exe -D - -H "X-Forwarded-Host: disp.phmtrans.cz" http://localhost:8089/
curl.exe -D - -H "X-Forwarded-Host: erp.phmtrans.cz" http://localhost:8089/
```

Bez cookie je deska i API odmítnuté a kořen dispečerského hostitele jde na login. Kořen ERP hostitele musí dál jít na login a po přihlášení na faktury, ne na dispečink.

IIS a DNS:

- DNS `disp.phmtrans.cz` (a případně `www.disp.phmtrans.cz`) na stejný server jako ERP, adresa `185.219.164.45`.
- Vazba hostitele na stejném webu IIS, který už posílá `erp.phmtrans.cz` na službu. Čistý ARR na `http://127.0.0.1:8089` stačí, služba hostitele pozná z `Host` nebo `X-Forwarded-Host`.
- Rozcestník míří na `https://disp.phmtrans.cz/`, takže web potřebuje i certifikát. Ukončení TLS nechat stejně jako u ERP.
- Když IIS neslouží jako celý ARR, ale kořen webu je `C:\PHMTRANS\wwwroot`, platí pravidla ve `wwwroot/web.config`: pro hostitele `disp.phmtrans.cz` se `/` přepíše na `dispecink.html` a `/api/*` se pošle na `http://127.0.0.1:8089/api/*`. Pravidla ERP se nemění.
- Prázdný jiný web IIS na tom samém hostiteli bude dál vracet 404, dokud se vazba nepřehodí.

## Připojení SQL

Heslo nepatří do gitu. V `appsettings.json` je `ConnectionStrings:Dispatch` prázdné. Služba bere proměnnou počítače `ConnectionStrings__Dispatch`. Bez ní `/health` zůstane v pořádku a deska po přihlášení řekne, že connection string chybí.

Účet zapisuje jen do databáze `Smerco_26950430_2026`. Není `sysadmin` a do ostatních databází se nezakládá jako zapisovatel. Heslo v příkazu vyměnit:

```sql
CREATE LOGIN [phmtrans_dispatch] WITH PASSWORD = N'heslo-jen-na-serveru', CHECK_POLICY = ON;
GO
USE [Smerco_26950430_2026];
GO
CREATE USER [phmtrans_dispatch] FOR LOGIN [phmtrans_dispatch];
ALTER ROLE db_datareader ADD MEMBER [phmtrans_dispatch];
ALTER ROLE db_datawriter ADD MEMBER [phmtrans_dispatch];
ALTER ROLE db_ddladmin ADD MEMBER [phmtrans_dispatch];
```

`db_ddladmin` je jen v téhle databázi, aby služba při startu založila chybějící `Disp_*`. Do jiných databází se účet nezakládá jako zapisovatel. Jediná výjimka je čtení adresáře Pohody. Tabulka `dbo.AD` je ve firemní databázi `StwPh_26950430_2026`, connection string se kvůli tomu nemění:

```sql
USE [StwPh_26950430_2026];
GO
CREATE USER [phmtrans_dispatch] FOR LOGIN [phmtrans_dispatch];
GRANT SELECT ON dbo.AD TO [phmtrans_dispatch];
```

TCP pro pojmenovanou instanci `POHODASQL`:

1. SQL Server Configuration Manager → Protocols for POHODASQL → TCP/IP → Enabled.
2. U TCP/IP, záložka IP Addresses, u IPAll doplnit pevný port (třeba 1433, když je volný) a vymazat dynamický port.
3. Služba SQL Server Browser zapnutá a Automatic, jinak se `server\POHODASQL` bez portu nerozjede. UDP 1434.
4. Restart služby SQL Server (POHODASQL).
5. Firewall na `192.168.1.54`: příchozí TCP na zvolený port a UDP 1434, ze serveru, kde běží služba PHMTRANS.

Proměnná se nastavuje z vyvýšeného PowerShellu. Služba Windows ji uvidí až po restartu. `Encrypt=True` a `TrustServerCertificate=True` patří k internímu SQL bez důvěryhodného certifikátu. Heslo se středníkem se v connection stringu dává do složených závorek.

```powershell
[Environment]::SetEnvironmentVariable(
  'ConnectionStrings__Dispatch',
  'Server=192.168.1.54\POHODASQL;Database=Smerco_26950430_2026;User ID=phmtrans_dispatch;Password=heslo-jen-na-serveru;Encrypt=True;TrustServerCertificate=True;',
  'Machine')
```

Po nastavení proměnné teprve zastavit službu, publikovat a spustit. `/health` musí být v pořádku i když SQL ještě nejede. Po přihlášení deska buď ukáže jízdy, nebo českou chybu připojení. Vozidla a řidiči v prázdné databázi nejsou, vloží se do `Disp_Vehicle` a `Disp_Driver`. Tabulky založí sama služba.
