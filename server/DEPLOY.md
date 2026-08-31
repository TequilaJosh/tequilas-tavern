# Deploy checklist — license server + auto-email

Follow top to bottom. Replace anything in `<ANGLE BRACKETS>`. **Never paste your SQL
password or API keys into a chat** — they go straight into Azure settings.

Tools are already installed on this PC: **Azure CLI (`az`)**, **Functions Core Tools
(`func`)**, **`dotnet`**, **`sqlcmd`**.

---

## 0. Open a NEW terminal
Close and reopen your terminal so `az` and `func` are on the PATH, then check:
```bash
az version
func --version
```

## 1. Sign in to Azure
```bash
az login
```
A browser opens — sign in. Then pick the subscription your SQL database is in:
```bash
az account set --subscription "<YOUR SUBSCRIPTION NAME OR ID>"
```

## 2. Fill in your names (used below)
| Placeholder | What it is | Example |
|---|---|---|
| `<SQL_SERVER>` | your Azure SQL server | `myshop.database.windows.net` |
| `<SQL_DB>` | your database name | `tavern` |
| `<SQL_USER>` | your SQL login | `shopadmin` |
| `<REGION>` | Azure region | `eastus` |
| `<APP_NAME>` | new, globally-unique function app name | `tt-license-yourname` |
| `<RG>` | resource group name | `tt-license-rg` |
| `<STORAGE>` | storage acct (3-24 lowercase letters/numbers) | `ttlicensestore1` |

## 3. Create the database tables
```bash
sqlcmd -S <SQL_SERVER> -d <SQL_DB> -U <SQL_USER> -P '<SQL_PASSWORD>' -i server/schema.sql
```
You should see it create `Licenses` and `Activations`.

## 4. Allow Azure services to reach the SQL server
Azure Portal → your SQL **server** → **Networking** → toggle **"Allow Azure services and
resources to access this server"** → Save. (One-time.)

## 5. Create the Function App
```bash
az group create -n <RG> -l <REGION>

az storage account create -n <STORAGE> -g <RG> -l <REGION> --sku Standard_LRS

az functionapp create -n <APP_NAME> -g <RG> \
  --storage-account <STORAGE> \
  --consumption-plan-location <REGION> \
  --runtime dotnet-isolated --functions-version 4 --os-type Windows
```
Cost on the Consumption plan at your volume is basically nothing (pennies for storage).

## 6. Configure settings (secrets live only here, server-side)
Get your **public key** (one line) — it's in `Assets/license_public.xml`.

```bash
az functionapp config appsettings set -n <APP_NAME> -g <RG> --settings \
  "SqlConnectionString=Server=tcp:<SQL_SERVER>,1433;Database=<SQL_DB>;User ID=<SQL_USER>;Password=<SQL_PASSWORD>;Encrypt=True;" \
  "LicensePublicKeyXml=<PASTE public key one line>"
```

### Only if you want auto-email on purchase — add these too:
```bash
az functionapp config appsettings set -n <APP_NAME> -g <RG> --settings \
  "IssueSecret=<INVENT a long random string>" \
  "LicensePrivateKeyXml=<PASTE tools/keygen/license_private.xml one line>" \
  "Smtp__Host=smtp.sendgrid.net" "Smtp__Port=587" \
  "Smtp__User=apikey" "Smtp__Pass=<YOUR SENDGRID API KEY>" \
  "Smtp__From=<you@yourdomain.com>" "Smtp__FromName=Tequilas' Tavern"
```
> ⚠️ `LicensePrivateKeyXml` is your master key. For real use, store it in **Azure Key Vault**
> and reference it instead of pasting it here (ask me and I'll walk you through it).

## 7. Deploy the code
```bash
cd server/LicenseApi
func azure functionapp publish <APP_NAME>
```
When it finishes it prints your URLs:
```
https://<APP_NAME>.azurewebsites.net/api/activate
https://<APP_NAME>.azurewebsites.net/api/validate
https://<APP_NAME>.azurewebsites.net/api/issue
```

## 8. Point the app at your API
In `Services/LicenseService.cs`, set:
```csharp
public const string ApiBaseUrl = "https://<APP_NAME>.azurewebsites.net/api";
```
Rebuild the app. (Ask me and I'll make this edit + rebuild for you.)

## 9. Test end to end
Make a key with the **Tavern Keygen** shortcut → activate it in the app. Then check the DB:
```sql
SELECT * FROM dbo.Licenses; SELECT * FROM dbo.Activations;
```
If you set up email, test issuance:
```bash
curl -X POST https://<APP_NAME>.azurewebsites.net/api/issue \
  -H "x-admin-secret: <IssueSecret>" -H "Content-Type: application/json" \
  -d '{"name":"Test","email":"<your email>","order":"t1","max":2}'
```
You should get the key in your inbox.

## 10. Connect Etsy (auto-email on sale)
Zapier or Make.com → trigger **"Etsy: New Paid Order"** → action **Webhook POST** to
`https://<APP_NAME>.azurewebsites.net/api/issue`
- Header: `x-admin-secret: <IssueSecret>`
- Body: `{ "name": "{{buyer name}}", "email": "{{buyer email}}", "order": "{{order id}}", "max": 2 }`

Done — a sale now mints, records, and emails a key automatically.

---

### Day-to-day after launch
- **Manual key / gift:** the **Tavern Keygen** shortcut.
- **See sales:** `SELECT * FROM dbo.Licenses ORDER BY FirstSeenUtc DESC;`
- **Revoke a leaked key:** `UPDATE dbo.Licenses SET IsRevoked=1 WHERE Email='<buyer>';`
- **Give an extra device:** `UPDATE dbo.Licenses SET MaxActivations=3 WHERE Email='<buyer>';`
