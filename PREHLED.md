# Přehled nastavení PHMTRANS

Podklad pro další úpravy. Stav větve `main` je revize `2b54aec` (1. 11. 2026).
Naposledy ověřený běh služby na serveru byl ještě `9834993`, tedy před opravou
češtiny z Pohody. Ta se na serveru projeví až po `git pull`, publikaci a restartu služby.

## Co to je

Jeden projekt ASP.NET Core na .NET 10, bez databáze. Skládá se z přehledu
vydaných faktur (BI), konfigurátoru zásob (CPQ) a statických stránek ve `wwwroot`.
Účetní data čte z Pohoda mServeru.

GitHub: https://github.com/jarasmerda/PHMTRANS

## Kde co leží

| Místo | Role |
| --- | --- |
| `C:\Projects\PHMTRANS` | Vývoj na počítači. SDK 10.0.403, `dotnet run` na `http://localhost:5053`, prostředí Development. |
| `C:\PHMTRANS` | Klon na Windows Serveru. Odtud se dělá `git pull`. |
| `C:\PHMTRANS\app` | Publikace služby: `dotnet publish -c Release -o C:\PHMTRANS\app`. |
| `C:\PHMTRANS\wwwroot` | Stránky, které služba servíruje. Úprava HTML, CSS a JS je jen `git pull`. |
| Služba Windows `PHMTRANS` | Drží port 8089. Start Automatic. |

Docker na serveru není potřeba. `Dockerfile` a `Dockerfile.windows` v repozitáři zůstávají, provoz je nespouští.

## Adresy

- Aplikace: http://185.219.164.45:8089/
- Stejná aplikace přes IIS ARR: http://erp.phmtrans.cz/
- mServer: http://185.219.164.45:444 (`/status` bez hesla, `/xml` s přihlášením). Účetní jednotka v odpovědi je `PHMTRANS/PROD`.
- `www.erp.phmtrans.cz` není totéž. Na `/health` vracelo 404.
- Port 80 bez ERP hostitele je samostatný web IIS, ne tato aplikace.

Mimo Development aplikace poslouchá na `http://*:8089`. Lokální `dotnet run` zůstává na portu 5053 a v Development zapíná OpenAPI.

## Nasazení změny kódu

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

## Přihlášení a Pohoda

Cookie `phmtrans-auth` platí 12 hodin a posouvá se. Nepřihlášené `/api` vrací 401, stránka jde na `/login.html`.

Host `erp.phmtrans.cz` a `www.erp.phmtrans.cz` se pozná i z `X-Forwarded-Host` (IIS ARR). Bez přihlášení jde kořen na login, po přihlášení se kořen přepíše na `erp.html`.

Formulář ověří zadané jméno a heslo proti mServeru. Faktury a zásoby pak volají účet z nastavení služby, ne účet z formuláře.

Jméno, heslo a IČO nejsou v gitu. Služba je má v proměnných počítače:

- `Pohoda__Username`
- `Pohoda__Password`
- `Pohoda__Ico`

`/api/pohoda/status` heslo nepotřebuje. Když chybí nebo nesedí účet služby, faktury vrátí `mServer vrátil 401:`, zatímco status zůstane v pořádku.

Další nastavení Pohody v `appsettings.json`: `BaseUrl` `http://185.219.164.45:444`, aplikace `PHMTRANS`, prefix kódu zásoby `PE` a pět číslic, timeout 180 sekund. HTTP klient nejde přes proxy.

XML do Pohody je Windows-1250. Odpověď se čte podle deklarace v XML. Když deklarace říká UTF-8, ale bajty UTF-8 nejsou, použije se Windows-1250.

## Endpointy

| Cesta | Účel | Kdo smí |
| --- | --- | --- |
| `GET /health` | `status`, verze, `timeUtc`, větev | kdokoli |
| `/`, `index.html` | Rozcestník | na ERP hostu až po přihlášení |
| `/login.html`, `POST /api/login`, `POST /api/logout` | Přihlášení | kdokoli |
| `/erp.html`, `GET /api/bi/issued-invoices` | Vydané faktury | cookie |
| `GET /api/pohoda/status` | Stav mServeru | kdokoli |
| `GET /CpqAttrValues1` až `7`, `GET /CpqFilterValues` | Hodnoty konfigurátoru | kdokoli |
| `GET /CpqItemPohodaCheck`, `GET /CpqNextCode` | Kontrola zásoby a další kód | účet služby |
| `POST /CpqItemCreationPohoda`, `POST /api/pohoda/xml` | Přeposlání XML do Pohody | účet služby |
| `GET /vlastovka`, `GET /weatherforecast` | Původní ukázky | kdokoli |

Faktury jsou `issuedInvoice`, částky v domácí měně. Stavy: Uhrazeno, Částečně, Po splatnosti, Neuhrazeno.

Data konfigurátoru se hledají v `Data/cpq` vedle kořene obsahu. V gitu ta složka není.

`/health` skládá větev při buildu z gitu (`git rev-parse`). `GIT_BRANCH` ji přepíše za běhu, `-p:GitBranch` při buildu.

## Na co myslet při další úpravě

- Neměnit účet Pohody jen v `appsettings.json` v gitu. Tam jsou heslo a IČO prázdné.
- HTML ve `wwwroot` a C# mají jiný postup nasazení.
- Zkouška na `localhost:8089` neukáže chování ERP hostitele. To závisí na `X-Forwarded-Host`.
- Publikace přepisuje `C:\PHMTRANS\app`. `appsettings.Production.json` je v `.gitignore`, ale služba ho dnes nemá jako jistý zdroj hesla.
