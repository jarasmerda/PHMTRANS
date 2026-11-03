# Intranet na intranet.phmtrans.cz

Jeden ASP.NET Core (.NET 10) proces dál servíruje veřejný web i intranet. Účetnictví pořád čte Pohodu účtem služby. Přihlášení uživatelů už nejde přes mServer, ale přes tabulku `Auth_User` v databázi `Smerco_26950430_2026`. Do Pohody se z přihlášení ani ze správy účtů nezapisuje.

## Adresy

| Host | Co udělá |
| --- | --- |
| `phmtrans.cz` | Veřejná prezentace (`landing.html`) |
| `www.phmtrans.cz` | `301` na `https://phmtrans.cz` se stejnou cestou a query. `/health` zůstává `200` |
| `intranet.phmtrans.cz` | Moduly pod cestou. Po přihlášení `/` jde na první povolený modul, u správce na `/ERP` |
| `erp.phmtrans.cz`, `www.erp.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/ERP` + původní cesta a query |
| `disp.phmtrans.cz`, `www.disp.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/Dispecink` |
| `crm.phmtrans.cz`, `www.crm.phmtrans.cz` | `301` na `https://intranet.phmtrans.cz/CRM` |

Cesty modulů jsou bez ohledu na velikost písmen: `/erp` i `/ERP` otevřou ERP. Hash v prohlížeči server nevidí, ale zůstane na nové adrese. `https://erp.phmtrans.cz/#uctovani-faktur` proto skončí na `https://intranet.phmtrans.cz/ERP#uctovani-faktur`.

Pod prefixem fungují i soubory a API. `/ERP/erp.css` je totéž co `/erp.css`, `/Dispecink/api/dispatch/board` je totéž co `/api/dispatch/board`, `/CRM/api/crm/board` je totéž co `/api/crm/board`.

`/health` vrací JSON na každém hostiteli, včetně starých a `www`. Přesměrování se ho netýká.

Kdo nemá cookie, dostane na stránce modulu `/login.html` a na API `401`. Kdo modul nemá povolený, dostane `403`. Správa účtů (`/Admin`) je jen pro správce.

## Účty

Tabulka `dbo.Auth_User` se zakládá sama, skript nic nemaže. Sloupce: uživatelské jméno, zobrazované jméno, hash hesla (ASP.NET Core `PasswordHasher`, PBKDF2), příznak správce, aktivní účet, povolené moduly ERP / Dispečink / CRM, čas vytvoření, poslední přihlášení, počítadlo nezdarů a konec zámku.

Cookie `phmtrans-auth` platí jen pro hostitele, na kterém vznikla (intranet). Je `HttpOnly`, `SameSite=Lax`, v produkci `Secure`, 12 hodin s posunem. Ve Development (lokální `dotnet run` na HTTP) je `Secure` podle schématu požadavku, jinak by se na `localhost` cookie neuložila. Služba na serveru neběží jako Development, cookie je vždy `Secure`. IIS má posílat `X-Forwarded-Proto`, aby aplikace viděla HTTPS.

Pět špatných hesel účet na 15 minut zamkne. Z jedné adresy jde nejvýš 20 pokusů za minutu, další dostanou `429`. Heslo se nikam neloguje.

Uživatel mění vlastní heslo na `/heslo`. Správce na `/Admin` zakládá uživatele, nastavuje moduly, příznak správce, nové heslo a účet vypíná. Vypnutý účet se hned nepřihlásí a otevřená relace na dalším požadavku spadne. Posledního aktivního správce nejde odebrat.

Když chybí `ConnectionStrings:Dispatch`, služba nastartuje, `/health` zůstane v pořádku a přihlášení vrátí, že připojení k databázi není nastavené. Dispečink a CRM se chovají stejně jako dřív.

Faktury dál čte Pohoda mServer účtem služby (`Pohoda__Username`, `Pohoda__Password`, `Pohoda__Ico`). Heslo z formuláře se do Pohody neposílá.

### První správce

Jen když v `Auth_User` ještě žádný správce není. Proměnné počítače (ne soubor v gitu):

- `Intranet__AdminUsername`
- `Intranet__AdminPassword` (aspoň 8 znaků)

Stejné klíče v konfiguraci jsou `Intranet:AdminUsername` a `Intranet:AdminPassword`. Prázdné hodnoty v `appsettings.json` se do gitu ukládat smí. Skutečné heslo ne. Když správce už existuje, start heslo nepřepíše.

## Nasazení

Kód i `wwwroot` se mění, takže publish a restart ano.

```powershell
[System.Environment]::SetEnvironmentVariable("Intranet__AdminUsername", "admin", "Machine")
[System.Environment]::SetEnvironmentVariable("Intranet__AdminPassword", "heslo-jen-na-serveru", "Machine")
# Už nastavené a pořád potřebné:
# ConnectionStrings__Dispatch
# Pohoda__Username
# Pohoda__Password
# Pohoda__Ico

Stop-Service PHMTRANS
cd C:\PHMTRANS
git pull origin main
dotnet publish -c Release -o C:\PHMTRANS\app
Start-Service PHMTRANS
curl.exe http://localhost:8089/health
```

Po startu se v logu služby objeví, že se založil první správce, a to jen jeho jméno. Heslo v logu není. Když proměnné chybí a správce ještě není, log řekne, že se účet nezaložil.

Ověření na serveru:

```powershell
curl.exe -D - http://localhost:8089/health
curl.exe -D - -H "X-Forwarded-Host: www.phmtrans.cz" http://localhost:8089/vozovy-park?x=1
curl.exe -D - -H "X-Forwarded-Host: phmtrans.cz" http://localhost:8089/
curl.exe -D - -H "X-Forwarded-Host: erp.phmtrans.cz" http://localhost:8089/
curl.exe -D - -H "X-Forwarded-Host: disp.phmtrans.cz" http://localhost:8089/api/dispatch/board
curl.exe -D - -H "X-Forwarded-Host: crm.phmtrans.cz" "http://localhost:8089/?x=1"
curl.exe -D - -H "X-Forwarded-Host: intranet.phmtrans.cz" http://localhost:8089/health
curl.exe -D - -H "X-Forwarded-Host: intranet.phmtrans.cz" http://localhost:8089/ERP
```

`www` vrací `301` na `https://phmtrans.cz/...`. Starý ERP host vrací `301` na `https://intranet.phmtrans.cz/ERP`. Kořen intranetu bez cookie vrací `302` na `/login.html`. `/health` je všude `200`.

## IIS, web s id 3

Aplikace poslouchá na `127.0.0.1:8089`. Veřejný HTTPS končí na IIS a ARR posílá požadavek na službu. Nový hostitel musí být na stejném webu, který už má `phmtrans.cz`, `erp.phmtrans.cz`, `disp.phmtrans.cz` a `crm.phmtrans.cz`. V IIS je to web s id **3**.

DNS:

- `intranet.phmtrans.cz` A `185.219.164.45`
- `www.phmtrans.cz` už má mířit na stejnou adresu, aplikace ho přesměruje na apex

Vazba a certifikát na serveru (PowerShell jako správce). Certifikát musí krýt `intranet.phmtrans.cz`. Hodí se stávající `*.phmtrans.cz`, nebo certifikát, který má to jméno v SAN. Samotný certifikát pro `phmtrans.cz` bez hvězdičky a bez SAN intranet nestačí.

```powershell
Import-Module WebAdministration

$site = Get-Website | Where-Object { $_.Id -eq 3 }
$site.Name

& "$env:windir\system32\inetsrv\appcmd.exe" set site "$($site.Name)" /+"bindings.[protocol='https',bindingInformation='*:443:intranet.phmtrans.cz']"

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object {
        $_.NotAfter -gt (Get-Date) -and (
            $_.DnsNameList.Unicode -contains "intranet.phmtrans.cz" -or
            $_.DnsNameList.Unicode -contains "*.phmtrans.cz")
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

$cert.Thumbprint
$cert.Subject

# SNI, aby se certifikát nepletl s ostatními hostiteli na 443
New-Item -Path "IIS:\SslBindings\0.0.0.0!443!intranet.phmtrans.cz" -Thumbprint $cert.Thumbprint -SSLFlags 1
```

Když `New-Item` hlásí, že vazba už je, starou smažte (`Remove-Item IIS:\SslBindings\0.0.0.0!443!intranet.phmtrans.cz`) a založte znovu. V IIS Manageru je totéž: web id 3, Bindings, `https`, port `443`, host `intranet.phmtrans.cz`, zaškrtnuté SNI, vybraný certifikát.

ARR na webu 3 má dál posílat celý požadavek na `http://127.0.0.1:8089` a zachovat `Host` (nebo nastavit `X-Forwarded-Host`) a `X-Forwarded-Proto=https`. Bez toho cookie `Secure` v prohlížeči na HTTPS sedí, ale stará vazba hostitele by na službu vůbec nedorazila a IIS vrátí 404.

Starým hostitelům vazbu nechte. Aplikace je přesměruje. `www.phmtrans.cz` taky nechte, `301` dělá aplikace.

Když IIS neslouží jako ARR, ale kořen webu je `C:\PHMTRANS\wwwroot`, stejná přesměrování a přepisy prefixů jsou ve `wwwroot/web.config`. `/health` se ani tam nepřesměruje. Po `git pull` stačí nový `web.config`, publish služby je pořád potřeba kvůli C#.

## Co do gitu nepatří

- `Intranet__AdminPassword` a `Pohoda__Password`
- connection string s heslem (`ConnectionStrings__Dispatch`)
- `appsettings.Production.json`
