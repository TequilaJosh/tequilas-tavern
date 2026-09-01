# Deploy in the browser (Azure Portal) — no command line

Everything below is point-and-click at **https://portal.azure.com**, except uploading the
code, which is a drag-and-drop of a zip I already built for you:

> **Deployment package:** `C:\Users\Admin\Downloads\TavernLicenseApi-deploy.zip`

Replace anything in `<ANGLE BRACKETS>`. **Never paste your SQL password / API keys anywhere
but the Azure settings screens.**

---

## 1. Create the Function App
1. Portal → search **“Function App”** → **Create** → choose **Consumption** (Serverless) → **Select**.
2. **Basics:**
   - **Resource Group:** Create new → `tt-license-rg`
   - **Function App name:** `<APP_NAME>` (globally unique, e.g. `tt-license-yourname`)
   - **Runtime stack:** **.NET**
   - **Version:** **8 (LTS), isolated worker model**
   - **Region:** pick one near you (e.g. East US)
   - **Operating System:** **Windows**
3. **Storage:** leave the auto-created storage account.
4. **Review + create** → **Create**. Wait ~1–2 min → **Go to resource**.

## 2. Create the database tables
1. Portal → open your **SQL database** (the database, not the server).
2. Left menu → **Query editor (preview)**.
3. Sign in with **SQL server authentication** (your SQL login + password).
   - If it says your IP isn't allowed, click the **“Allowlist IP …”** button it shows, then sign in again.
4. Open `server\schema.sql` on your PC, copy everything, paste it into the editor → **Run**.
   You should see `Licenses` and `Activations` created (refresh **Tables** on the left).

## 3. Let Azure reach your SQL server
1. Portal → open your **SQL server** (the server, not the database) → left menu → **Networking**.
2. Under **Firewall rules**, tick **“Allow Azure services and resources to access this server.”**
3. **Save.**

## 4. Add the app settings (secrets live here, server-side)
1. Portal → your **Function App** → left menu → **Settings → Environment variables** → **App settings** tab.
2. Click **+ Add** for each of these (Name / Value), then **Apply** at the bottom:

**Always needed:**
| Name | Value |
|------|-------|
| `SqlConnectionString` | `Server=tcp:<SQL_SERVER>,1433;Database=<SQL_DB>;User ID=<SQL_USER>;Password=<SQL_PASSWORD>;Encrypt=True;` |
| `LicensePublicKeyXml` | the one line inside `Assets\license_public.xml` |

**Only if you want auto-email on purchase, also add:**
| Name | Value |
|------|-------|
| `IssueSecret` | invent a long random string (you'll reuse it in Zapier/Make) |
| `LicensePrivateKeyXml` | the one line inside `tools\keygen\license_private.xml` (your master secret) |
| `Smtp__Host` | `smtp.sendgrid.net` |
| `Smtp__Port` | `587` |
| `Smtp__User` | `apikey` |
| `Smtp__Pass` | your SendGrid API key |
| `Smtp__From` | `you@yourdomain.com` |
| `Smtp__FromName` | `Tequilas' Tavern` |

3. Click **Apply / Save** and confirm the restart.

## 5. Upload the code (drag-and-drop)
1. In your browser go to: **`https://<APP_NAME>.scm.azurewebsites.net/ZipDeployUI`**
   (this is your app's built-in deploy tool; sign in with the same Azure account).
2. **Drag `C:\Users\Admin\Downloads\TavernLicenseApi-deploy.zip` onto the page.**
3. Wait for it to finish (progress shows top-right).
4. Back on the Function App → **Overview → Functions**, you should now see **activate**,
   **validate**, and **issue**.

Your endpoints are now live:
```
https://<APP_NAME>.azurewebsites.net/api/activate
https://<APP_NAME>.azurewebsites.net/api/validate
https://<APP_NAME>.azurewebsites.net/api/issue
```

## 6. Point the app at your API
Tell me your `<APP_NAME>` and I'll set `ApiBaseUrl` in `Services\LicenseService.cs` and rebuild
the app for you. (Or edit it yourself:
`public const string ApiBaseUrl = "https://<APP_NAME>.azurewebsites.net/api";`)

## 7. Test
- Make a key with the **Tavern Keygen** shortcut → activate it in the app → check the DB
  (Query editor: `SELECT * FROM dbo.Activations;`).
- Email test (if configured): Function App → **issue** function → **Code + Test → Test/Run**,
  set the request body to
  `{ "name":"Test", "email":"<your email>", "order":"t1", "max":2 }`
  and add header `x-admin-secret: <IssueSecret>` → **Run**. Check your inbox.

## 8. Connect Etsy (auto-email on sale)
Zapier or Make.com → trigger **“Etsy: New Paid Order”** → action **Webhook (POST)** to
`https://<APP_NAME>.azurewebsites.net/api/issue`
- Header: `x-admin-secret: <IssueSecret>`
- Body: `{ "name": "{{buyer name}}", "email": "{{buyer email}}", "order": "{{order id}}", "max": 2 }`

Done — a sale now mints, records, and emails a key automatically.

---

### If you'd rather deploy from VS Code
You have VS Code installed. Install the **Azure Functions** extension → sign in to Azure →
right-click the `server/LicenseApi` folder → **Deploy to Function App**. It handles MFA in a
friendly popup and uploads the code (skip step 5). Settings/DB steps are the same.
