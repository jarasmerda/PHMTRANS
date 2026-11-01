# Návrh CRM modulu PHMTRANS

Podklad pro CRM na `crm.phmtrans.cz`. Aplikace je jeden ASP.NET Core (.NET 10) Minimal API projekt. Účetnictví čte z Pohoda mServeru, dispečink drží jízdy v SQL a stránky jsou statické soubory ve `wwwroot`. V produkci běží jako služba Windows `PHMTRANS` na portu 8089. Rozcestník na `www.phmtrans.cz` už má tlačítko CRM hned vedle Dispečinku a vede na `https://crm.phmtrans.cz/`.

Tento dokument popisuje první verzi, která je v kódu. Nenahrazuje Pohodu a do Pohody nic nezapisuje.

## Východiska z dnešního kódu

Modul kopíruje řez dispečinku, nepřidává novou hostitelskou aplikaci.

| Vzor v repozitáři | Použití v CRM |
| --- | --- |
| `Dispatch/DispatchEndpoints.cs` registruje `MapDispatch()` | `Crm/CrmEndpoints.cs` registruje `MapCrm()` |
| Host se bere z `X-Forwarded-Host`, jinak z `Request.Host` | Stejně. `erp.phmtrans.cz` zůstává u faktur, `disp.phmtrans.cz` u jízd |
| Cookie `phmtrans-auth`, 12 hodin, posuvná, login přes mServer | Stejný `POST /api/login` a `POST /api/logout`. Heslo se proti Pohodě jen ověří |
| `dispecink.html` + `dispecink.js` berou `erp.css` | `crm.html` + `crm.js` berou `erp.css?v=5` a nepřepisují ho |
| `SqlDispatchStore` jako singleton, schéma při startu | `SqlCrmStore` jako singleton, `CrmSchemaInitializer` vedle dispečinku |
| Connection string jen `ConnectionStrings:Dispatch` | Stejný klíč, stejná databáze `Smerco_26950430_2026`. V gitu je prázdný |

Cookie nemá `Domain=.phmtrans.cz`. Přihlášení na ERP, dispečinku a CRM jsou samostatné session se stejnými údaji Pohody. Rozšíření cookie na celou doménu by změnilo chování ERP, proto tu není.

ERP filtr odběratelů, přehled faktur, CPQ a jízdy se tímto modulem nemění.

## Pojmy

- **Zákazník** je firma (`Firma`) nebo člověk (`Osoba`). Má název, volitelné IČO, telefon, e-mail a adresu. IČO se neověřuje v Pohodě a není povinné ani u firmy.
- **Kontakt** je osoba u zákazníka: jméno, funkce, telefon, e-mail. Firma jich má víc. U osoby je to volitelné, její vlastní telefon a e-mail jsou přímo na zákazníkovi.
- **Příležitost** je obchodní případ jednoho zákazníka. Má fázi, volitelnou částku v Kč a volitelný předpokládaný termín.
- **Aktivita** je hovor, schůzka nebo poznámka. Vždy visí na zákazníkovi, na příležitosti, nebo na obou. Když je vyplněná jen příležitost, zákazník se doplní z ní. Když jsou vyplnění oba a neshodují se, zápis se odmítne.

Kódy generuje úložiště a v rámci roku rostou: zákazník `C{rok}-0001`, příležitost `O{rok}-0001` (O jako obchod), aktivita `A{rok}-0001`. Kontakt kód nemá. Rok se bere z času serveru. Čítač je nejvyšší známé číslo daného druhu v daném roce plus jedna. Zákazník nespotřebovává číslo příležitosti.

## První verze

Umí tohle:

- přihlášení stejnou Pohoda cookie jako ERP a dispečink
- přehled s počty zákazníků, příležitostí, aktivit, fází a druhů aktivit
- založení a úpravu zákazníka, kontaktu, příležitosti a aktivity
- kořen `crm.phmtrans.cz` a `www.crm.phmtrans.cz` otevře CRM
- úložiště je SQL Server, tabulky `Crm_*`, ve stejné databázi jako `Disp_*`. V odpovědi API je `storage: "sql"`. Do Pohody se nezapisuje

Záměrně v první verzi není: mazání řádků, role kromě přihlášeného uživatele Pohody, import adresáře, našeptávač z Pohody, nabídkové PDF, e-mail, úkoly s termínem splnění, historie polí kromě `UpdatedAt`, vazba vyhrané příležitosti na jízdu.

Chybějící `ConnectionStrings:Dispatch` aplikaci neshodí. ERP i dispečink běží dál a CRM vrátí text, že connection string není nastavený. Prázdná databáze nemá ukázkové řádky. Ukázky jsou jen v paměťovém úložišti testů.

## Obrazovky

Jedna stránka `/crm.html`. Čtyři části přepínají záložky, adresa v prohlížeči se nemění. Vzhled bere barvy, písmo, karty, tabulku a tlačítka z `erp.css`. Doplňky, které ERP nemá (záložky, šířka polí v tabulce, text aktivity), jsou jen ve stránce CRM. Skript je `crm.js?v=1`.

Hlavička je ve stejném tvaru jako dispečink: značka PHM TRANS, odkaz Rozcestník na `https://www.phmtrans.cz/`, odhlášení. Věta, že data jsou v SQL a do Pohody se nezapisuje, přichází z API (`storageNote`).

### Přehled

Karty: Zákazníci, Příležitosti, Aktivity, a počty fází Nový, V jednání, Nabídka, Vyhráno, Prohráno. Druhý řádek: Hovory, Schůzky, Poznámky. Kliknutí na kartu otevře odpovídající záložku a nastaví filtr. Pod kartami je osm naposledy změněných příležitostí a osm posledních aktivit.

### Zákazníci

Filtr Firma / Osoba. Tabulka: kód, druh, název, kontaktní údaje, adresa, IČO, Upravit. Řádek se přepne na pole a Uložit změny, stejně jako jízda na desce. Pod tabulkou je formulář Nový zákazník. Stát je předvyplněný Česká republika, prázdné pole se uloží jako prázdné.

Kontakty jsou na stejné záložce, protože patří k zákazníkovi. Výběr zákazníka seznam zúží. Nový kontakt i úprava mají vlastní pole: zákazník, jméno, funkce, e-mail, telefon, poznámka. Kontakt lze přepsat na jiného zákazníka.

### Příležitosti

Filtr fáze. Tabulka: kód, zákazník, název, částka, předpoklad, fáze, Upravit. Formulář Nová příležitost. Částka je v Kč, prázdná znamená nezadanou. Když se fáze mění na Prohráno, prohlížeč se zeptá.

### Aktivity

Filtr Hovor / Schůzka / Poznámka. Tabulka: kód, kdy, druh, zákazník, příležitost, předmět, Upravit. Formulář Nová aktivita. Vybraná příležitost doplní zákazníka. Alespoň jeden z těch dvou musí být vyplněný.

Na hostiteli CRM jde nepřihlášený kořen, `index.html` i `/crm.html` na `/login.html`. Po přihlášení se kořen, `index.html` a `login.html` vnitřně přepíší na `/crm.html`, adresa v prohlížeči zůstane `/`. To je stejný trik jako u dispečinku.

Mimo tyto hostitele (IP, `localhost:5053`) zůstane kořen rozcestník. CRM je na `/crm.html` a bez cookie vrací přesměrování na login. Po přihlášení z login formuláře prohlížeč přejde na `/`. Na běžném hostiteli je pak potřeba otevřít `/crm.html` znovu. Na `crm.phmtrans.cz` to není potřeba, kořen už je CRM.

## Fáze příležitosti

Řetězec je přesný český text, stejně jako stav jízdy.

| Fáze | Význam |
| --- | --- |
| `Nový` | Příležitost je založená, obchod se teprve otevírá |
| `V jednání` | Se zákazníkem se jedná |
| `Nabídka` | Nabídka je venku |
| `Vyhráno` | Obchod platí |
| `Prohráno` | Obchod skončil bez objednávky |

První verze dovolí nastavit kteroukoli fázi, ať se omyl dá opravit. Nová příležitost bez fáze dostane `Nový`. Úprava fázi vyžaduje. Neznámý text je `400`.

## Druhy

| Entita | Hodnoty |
| --- | --- |
| Zákazník | `Firma`, `Osoba` |
| Aktivita | `Hovor`, `Schůzka`, `Poznámka` |

Neznámý text je `400`. Prázdný druh se neukládá.

## Datový model

Cílová podoba první verze. Nové sloupce se později doplní stejně jako u dispečinku, podmínkou `COL_LENGTH`, ne přepsáním tabulky. Skript první verze tabulku jen založí, když chybí.

### Zákazník `dbo.Crm_Customer`

| Pole | Poznámka |
| --- | --- |
| `Id` | `nvarchar(64)`, stabilní klíč |
| `Code` | `nvarchar(20)`, unikátní, `C{rok}-0001` |
| `Kind` | `Firma` nebo `Osoba` |
| `Name` | povinný, nejvýše 160 znaků |
| `Ico` | volitelné, nejvýše 16 znaků |
| `Email` | volitelný, nejvýše 160 |
| `Phone` | volitelný, nejvýše 40 |
| `Street` | volitelná, nejvýše 160 |
| `City` | volitelné, nejvýše 80 |
| `Zip` | volitelné, nejvýše 16 |
| `Country` | volitelný, nejvýše 80 |
| `Note` | volitelná, nejvýše 500 |
| `UpdatedAt` | `datetimeoffset(3)` |

### Kontakt `dbo.Crm_Contact`

| Pole | Poznámka |
| --- | --- |
| `Id` | `nvarchar(64)` |
| `CustomerId` | cizí klíč na zákazníka, povinný |
| `Name` | povinné, nejvýše 160 |
| `Role` | funkce, volitelná, nejvýše 80 |
| `Email`, `Phone`, `Note` | stejné meze jako u zákazníka |
| `UpdatedAt` | `datetimeoffset(3)` |

### Příležitost `dbo.Crm_Opportunity`

| Pole | Poznámka |
| --- | --- |
| `Id` | `nvarchar(64)` |
| `Code` | `nvarchar(20)`, unikátní, `O{rok}-0001` |
| `CustomerId` | cizí klíč, povinný |
| `Title` | povinný, nejvýše 160 |
| `Stage` | jedna z pěti fází |
| `Amount` | `decimal(18, 2)`, volitelná, nula a víc, v Kč |
| `ExpectedClose` | `date`, volitelný |
| `Note` | volitelná, nejvýše 500 |
| `UpdatedAt` | `datetimeoffset(3)` |

Částka se před zápisem zaokrouhlí na dvě desetinná místa. Záporná nebo větší než se vejde do sloupce se odmítne.

### Aktivita `dbo.Crm_Activity`

| Pole | Poznámka |
| --- | --- |
| `Id` | `nvarchar(64)` |
| `Code` | `nvarchar(20)`, unikátní, `A{rok}-0001` |
| `Kind` | `Hovor`, `Schůzka` nebo `Poznámka` |
| `CustomerId` | cizí klíč, nebo prázdný, když je příležitost |
| `OpportunityId` | cizí klíč, nebo prázdný, když je zákazník |
| `Subject` | povinný, nejvýše 160 |
| `Body` | volitelný text, nejvýše 1000 |
| `OccurredAt` | povinný `datetimeoffset(3)` |
| `UpdatedAt` | `datetimeoffset(3)` |

Kontrola v tabulce: `CustomerId` nebo `OpportunityId` není prázdné. Aplikace navíc chce, aby zákazník seděl se zákazníkem příležitosti.

## API

Všechno pod `/api/crm` vyžaduje cookie. Bez ní je `401` a JSON `{ "message": "Přihlaste se." }`, ne přesměrování na HTML. Tak se chová i `/api/dispatch`.

| Metoda a cesta | Tělo / odpověď |
| --- | --- |
| `GET /api/crm/board` | `storage`, `storageNote`, `counts`, zákazníci, kontakty, příležitosti, aktivity |
| `GET /api/crm/customers?kind=` | pole zákazníků, `kind` volitelný |
| `GET /api/crm/customers/{id}` | `{ "customer", "contacts" }` |
| `POST /api/crm/customers` | založí zákazníka včetně `code` |
| `PUT` a `PATCH /api/crm/customers/{id}` | celý záznam, kód se nemění |
| `GET /api/crm/contacts?customerId=` | pole kontaktů |
| `POST /api/crm/contacts` | založí kontakt |
| `PUT` a `PATCH /api/crm/contacts/{id}` | úprava, včetně přesunu na jiného zákazníka |
| `GET /api/crm/opportunities?stage=` | pole příležitostí |
| `POST /api/crm/opportunities` | založí, prázdná fáze je `Nový` |
| `PUT` a `PATCH /api/crm/opportunities/{id}` | úprava, fáze je povinná |
| `GET /api/crm/activities?customerId=&opportunityId=&kind=` | pole aktivit |
| `POST /api/crm/activities` | založí aktivitu |
| `PUT` a `PATCH /api/crm/activities/{id}` | úprava |

`POST /api/crm/customers`:

```json
{
  "kind": "Firma",
  "name": "Velkoobchod Demo s.r.o.",
  "ico": "12345678",
  "email": "info@demo.cz",
  "phone": "+420 601 000 001",
  "street": "Skladová 12",
  "city": "Cerekvice nad Loučnou",
  "zip": "569 53",
  "country": "Česká republika",
  "note": ""
}
```

`POST /api/crm/opportunities`:

```json
{
  "customerId": "…",
  "title": "Nafta na zimu",
  "stage": "Nový",
  "amount": 120000,
  "expectedClose": "2026-12-01",
  "note": ""
}
```

`POST /api/crm/activities`:

```json
{
  "kind": "Hovor",
  "customerId": null,
  "opportunityId": "…",
  "subject": "Poptávka nafty",
  "body": "Voláno na nákup",
  "occurredAt": "2026-11-02T06:00:00Z"
}
```

`counts` na desce:

```json
{
  "customers": 0,
  "opportunities": 0,
  "activities": 0,
  "stages": [{ "stage": "Nový", "count": 0 }],
  "activityKinds": [{ "kind": "Hovor", "count": 0 }]
}
```

Neznámý zákazník, kontakt, příležitost nebo aktivita při úpravě je `404` s českým `{ "message" }`. Neznámý druh, fáze, zákazník, příležitost, záporná částka nebo chybějící povinné pole je `400`. Nedostupný SQL Server nebo chybějící connection string je `500` s českým `{ "message" }`. `storage` je vždy `"sql"`.

Řazení dělá API, ne stránka: zákazníci a kontakty podle jména, příležitosti podle fáze a názvu, aktivity od nejnovějšího `occurredAt`. Stránka desky si z příležitostí navíc vybere osm posledních podle `updatedAt`.

Prázdný řetězec u volitelného pole ho smaže. `PUT` posílá celý záznam. Kód se úpravou nemění.

## SQL úložiště

Třída `SqlCrmStore`, přístup přes `Microsoft.Data.SqlClient` a Dapper. Databáze je ta samá jako u dispečinku: `Smerco_26950430_2026`. Tabulky mají předponu `Crm_`, ať se nepotkají s `Disp_*`. Aplikace do Pohody nezapisuje a účet v connection stringu se nemění.

Connection string je jen `ConnectionStrings:Dispatch`. Na serveru je to proměnná počítače `ConnectionStrings__Dispatch`. V `appsettings.json` je prázdný řetězec, heslo v gitu není. Nový klíč se nezakládá.

Při startu služby se na pozadí spustí idempotentní skript. Když connection string chybí nebo SQL neodpoví, start ERP pokračuje a chyba se jen zaloguje. Další požadavek na CRM schéma zkusí znovu.

Skript zakládá tabulku jen když `OBJECT_ID` je prázdné. Nemá `DROP`, `DELETE`, `ALTER`, `UPDATE` ani `TRUNCATE` a neobsahuje jméno databáze, takže nepřepne kontext na jinou databázi. Cizí tabulky, včetně `Disp_*` a Pohody, nemění.

Zápis bere aplikační zámek `PHMTRANS_Crm` v transakci, ať dva požadavky nevyrobí stejný kód. Zámek dispečinku `PHMTRANS_Disp` se tím neblokuje. Číselníky se neseedují. Prázdná databáze zůstane prázdná, dokud někdo nepřidá zákazníka ve stránce.

## Napojení na ERP a Pohodu

Obchodník se hlásí jménem a heslem uživatele mServeru (`CredentialsAreValidAsync`). CRM po přihlášení Pohodu už nevolá. V kódu `Crm/` není klient mServeru.

Další fáze, pořád jen čtení, ve stejném klientovi `PohodaMServerClient`:

1. **Našeptávač partnera** z adresáře Pohody při zakládání zákazníka. Do té doby je zákazník vlastní záznam v `Crm_Customer`, ať modul nevisí na číselníku, který ještě nenačítáme.
2. **Proklik na vydané faktury** partnera na `erp.phmtrans.cz`. Vznik faktury zůstane v Pohodě.
3. **Vazba vyhrané příležitosti na jízdu** v dispečinku, ručně, číslem zakázky. Dispečink ji v první vlně CRM nezakládá.

Žádná z těch fází nezapisuje XML do Pohody. Stránka faktur ani deska jízd se kvůli CRM nemění.

## Fáze

1. **Deska CRM.** Zákazníci, kontakty, příležitosti, aktivity, počty, cookie, hostitel. Data jsou v SQL, tabulky `Crm_*`.
2. **Pohoda jen ke čtení.** Našeptávač partnera a odkaz na faktury. Pořád bez zápisu dokladu.
3. **Vazba na dispečink.** Z vyhrané příležitosti se navrhne jízda, dispečer ji potvrdí. Samostatný zápis, ne jeden společný řádek.
4. **Nabídka.** Soubor nebo text nabídky u fáze Nabídka. Až bude šablona, ne dřív.

Fáze 1 je podmínka pro zbytek. Nové sloupce půjdou migrací `COL_LENGTH`, ne smazáním `Crm_*`.

## Nasazení `crm.phmtrans.cz`

Odkaz z rozcestníku už vede na `crm.phmtrans.cz`. Dokud IIS hostitele nezná, `404` vrací IIS a požadavek se ke službě na 8089 nedostane. Samotný `git pull` to nespraví.

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
curl.exe -D - http://localhost:8089/crm.html
curl.exe http://localhost:8089/api/crm/board
curl.exe -D - -H "X-Forwarded-Host: crm.phmtrans.cz" http://localhost:8089/
curl.exe -D - -H "X-Forwarded-Host: erp.phmtrans.cz" http://localhost:8089/
curl.exe -D - -H "X-Forwarded-Host: disp.phmtrans.cz" http://localhost:8089/
```

Bez cookie je CRM i API odmítnuté a kořen CRM hostitele jde na login. Kořen ERP hostitele musí dál jít na faktury, kořen dispečinku na desku jízd.

IIS a DNS, stejným způsobem jako u `disp.phmtrans.cz`:

- DNS `crm.phmtrans.cz` a `www.crm.phmtrans.cz` na stejný server jako ERP, adresa `185.219.164.45`.
- Vazba hostitele na stejném webu IIS, který už posílá ERP a dispečink na službu. Čistý ARR na `http://127.0.0.1:8089` stačí, služba hostitele pozná z `Host` nebo `X-Forwarded-Host`.
- Rozcestník míří na `https://crm.phmtrans.cz/`, takže web potřebuje i certifikát. Ukončení TLS nechat stejně jako u ERP.
- Když IIS neslouží jako celý ARR, ale kořen webu je `C:\PHMTRANS\wwwroot`, platí pravidla ve `wwwroot/web.config`: pro hostitele `crm.phmtrans.cz` se `/` přepíše na `crm.html` a `/api/*` se pošle na `http://127.0.0.1:8089/api/*`. Pravidla ERP a dispečinku se nemění.

## Připojení SQL

Heslo nepatří do gitu a nepřidává se nové. CRM bere stejnou proměnnou `ConnectionStrings__Dispatch` jako dispečink. Účet `phmtrans_dispatch` v databázi `Smerco_26950430_2026` už má `db_ddladmin`, takže při startu založí chybějící `Crm_*` vedle `Disp_*`. Nový login ani nové heslo se nezakládá. Postup k účtu, portu a firewallu je v `docs/dispecink-navrh.md`.

`/health` zůstane v pořádku i když SQL ještě nejede nebo connection string chybí. Po přihlášení CRM buď ukáže zákazníky, nebo českou chybu připojení.
