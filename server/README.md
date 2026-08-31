# Tequilas' Tavern — license activation server

Online activation for the app: the desktop client calls this small HTTPS API, which
checks each key against your **Azure SQL** database and enforces activation limits and
revocation. The database credentials live only here on the server — never in the app.

```
Desktop app ──HTTPS──► LicenseApi (Azure Function) ──► Azure SQL
 (embeds public key)     (holds DB creds + public key)   (Licenses, Activations)
```

Keys are RSA-signed offline by `tools/keygen`, so forged keys are rejected before they
ever touch the database, and keys **self-register** on first activation (you don't insert
anything when you sell).

---

## What you need
- The Azure SQL database you already have (server name, database name, a SQL login).
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (you already have it).
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) — `npm i -g azure-functions-core-tools@4`
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — `az login`

---

## 1. Create the tables
Run [`schema.sql`](schema.sql) once against your database — Azure Portal → your DB →
**Query editor**, or:
```bash
sqlcmd -S yourserver.database.windows.net -d yourdb -U youruser -P 'yourpassword' -i server/schema.sql
```

## 2. Grab your public key (one line)
The API verifies signatures with the SAME public key embedded in the app
(`Assets/license_public.xml`). You'll paste its contents into an app setting below.

## 3. Create the Function App
Pick globally-unique names. Example with the CLI:
```bash
az group create -n tt-license-rg -l eastus

az storage account create -n ttlicensestore -g tt-license-rg -l eastus --sku Standard_LRS

az functionapp create -n tt-license -g tt-license-rg \
  --storage-account ttlicensestore \
  --consumption-plan-location eastus \
  --runtime dotnet-isolated --functions-version 4 --os-type Windows
```

## 4. Configure secrets (server-side only)
```bash
# DB connection string
az functionapp config appsettings set -n tt-license -g tt-license-rg \
  --settings "SqlConnectionString=Server=tcp:yourserver.database.windows.net,1433;Database=yourdb;User ID=youruser;Password=yourpassword;Encrypt=True;"

# Public key — paste the whole contents of Assets/license_public.xml as one value
az functionapp config appsettings set -n tt-license -g tt-license-rg \
  --settings "LicensePublicKeyXml=<RSAKeyValue><Modulus>...</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>"
```
Also make sure your Azure SQL firewall **allows Azure services** (Portal → SQL server →
Networking → "Allow Azure services and resources to access this server").

## 5. Deploy
```bash
cd server/LicenseApi
func azure functionapp publish tt-license
```
Your endpoints are now:
```
https://tt-license.azurewebsites.net/api/activate
https://tt-license.azurewebsites.net/api/validate
```

## 6. Point the app at it
In `Services/LicenseService.cs` set:
```csharp
public const string ApiBaseUrl = "https://tt-license.azurewebsites.net/api";
```
Rebuild the app. (Leave it `""` to keep running fully offline.)

## 7. Test end to end
```bash
# mint a 2-device key
dotnet run --project tools/keygen -- issue --name "Jane" --email "jane@x.com" --order 123 --max 2
```
Launch the app, paste the key → it should activate and record a row in `dbo.Activations`.
Activate on a 3rd machine → refused with "activation limit". Then:
```sql
UPDATE dbo.Licenses SET IsRevoked = 1 WHERE Email = 'jane@x.com';  -- kill a leaked key
```

---

## Day-to-day
| Task | How |
|------|-----|
| Sell a copy | `dotnet run --project tools/keygen -- issue --name … --email … --order … --max 2` → send the key |
| See customers | `SELECT * FROM dbo.Licenses ORDER BY FirstSeenUtc DESC;` |
| See a key's devices | `SELECT a.* FROM dbo.Activations a JOIN dbo.Licenses l ON l.KeyId=a.KeyId WHERE l.Email='…';` |
| Revoke a key | `UPDATE dbo.Licenses SET IsRevoked=1 WHERE Email='…';` |
| Give an extra seat | `UPDATE dbo.Licenses SET MaxActivations=3 WHERE Email='…';` |

## Automated key email on purchase (optional)

The `/api/issue` endpoint mints a key server-side, records it, and emails the buyer — so an
Etsy sale delivers a key with no clicks from you.

### Extra app settings
```bash
az functionapp config appsettings set -n tt-license -g tt-license-rg --settings \
  "IssueSecret=<a long random string>" \
  "LicensePrivateKeyXml=<contents of tools/keygen/license_private.xml, one line>" \
  "Smtp__Host=smtp.sendgrid.net" "Smtp__Port=587" \
  "Smtp__User=apikey" "Smtp__Pass=<your SendGrid API key>" \
  "Smtp__From=you@yourdomain.com" "Smtp__FromName=Tequilas' Tavern"
```
> **Security:** this puts your **private signing key** in the cloud. Store it in **Azure Key
> Vault** and reference it (`@Microsoft.KeyVault(SecretUri=...)`) rather than pasting it as a
> plain app setting. Whoever holds this key can mint keys, so guard it like a password.

**Email provider** — any SMTP works. Easiest options:
- **SendGrid** (free 100/day): Host `smtp.sendgrid.net`, User `apikey`, Pass = your API key.
- **Gmail**: Host `smtp.gmail.com`, User = your address, Pass = a Google **App Password**.
- **Azure Communication Services Email**: use its SMTP settings.

### Wire it to Etsy
Etsy has no "run my code on sale" hook, so use an automation to call `/api/issue`:

**Zapier or Make.com** → trigger **"Etsy: New Paid Order"** → action **Webhook (POST)** to
`https://tt-license.azurewebsites.net/api/issue`:
- Header `x-admin-secret: <your IssueSecret>`
- JSON body mapped from the order:
  ```json
  { "name": "{{buyer name}}", "email": "{{buyer email}}", "order": "{{order id}}", "max": 2 }
  ```
The function mints the key, saves it, and emails the buyer. It also returns `{ "ok": true,
"key": "..." }` so your automation can log it.

Test without Etsy:
```bash
curl -X POST https://tt-license.azurewebsites.net/api/issue \
  -H "x-admin-secret: <your IssueSecret>" -H "Content-Type: application/json" \
  -d '{"name":"Test","email":"you@youremail.com","order":"t1","max":2}'
```

(Prefer no third-party automation? I can add a timer-triggered Etsy-API poller instead — it
needs an Etsy developer app + OAuth. Ask and I'll build it.)

## Notes / limits
- `activate` and `validate` are anonymous but only act on **validly-signed** keys, so junk
  requests are rejected cheaply. Add a Function key or Azure API Management rate-limit later
  if you want extra hardening.
- Once activated, the app launches **offline**; revocation is picked up on the next online
  launch (a background `validate` call), so a revoked key stops working within a day or two
  of the user next being online — not instantly.
- Keep `tools/keygen/license_private.xml` backed up and secret; it's the root of trust.
