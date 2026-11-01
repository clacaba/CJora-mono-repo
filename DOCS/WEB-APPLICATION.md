# Browser application

React/TypeScript source: SRC/WEB/CJora.Web. Root package.json defines the npm workspace; commit package-lock.json and use npm ci for repeatable installs.

## Run locally

Start Docker dependencies and initialize the backend as described in LOGIN-MILESTONE.md. From the repository root:

```powershell
npm ci
npm run build:web
dotnet build CJora.sln -warnaserror
```

Start Core, Identity Management and Gateway in three terminals using their dotnet run --project commands. Open **https://localhost:7000/app**. Build the frontend before starting Gateway. To rebuild while editing, run npm run dev:web in another terminal and refresh the browser. This uses Vite build watch; there is no separate public development server, CORS relaxation or second browser origin.

Email: alex@example.test. The fictional demo password is in ignored INFRASTRUCTURE/LOCAL/.env under CJORA_DEMO_USER_PASSWORD; inspect it locally in an editor. CJora collects only the email hint, then navigates to Keycloak for password entry. Use the localhost hostname consistently and trust your own local HTTPS certificate if the browser prompts.

## Implemented behavior

The interface resolves the fictional Program, checks the existing session, displays the authorized customer profile and signs out. Loading, expired-session, denied-access, provider-failure, rate-limit and unavailable-backend states have safe messages. Logout hides customer data on uncertain failures and explains queued revocation.

All API calls are relative to Gateway, with same-origin credentials. A fresh session-bound CSRF token is fetched before login/logout; the browser supplies Origin. No application access/refresh token is stored in localStorage/sessionStorage. The HttpOnly application cookie remains inaccessible to JavaScript. The provider callback returns to the exact allowlisted /app path; failure returns to /app/login-failed and the app cleans the URL.

Gateway serves only fixed app entry paths and built /assets files, with CSP, no-store, no-referrer and nosniff headers. Unknown API paths retain API behavior; there is no SPA fallback over /api or arbitrary dynamic routes. Asset serving occurs before the dynamic two-segment controller is selected. dist and node_modules are ignored.

## Checks

```powershell
npm run test:web
npm audit
dotnet test CJora.sln
./SCRIPTS/Test-Login.ps1 -Browser
./SCRIPTS/Test-Login.ps1
./SCRIPTS/Test-DynamicGateway.ps1
```

The browser check requires installed Microsoft Edge and runs headlessly against real local MySQL/Keycloak. It temporarily starts/stops its own APIs. It explicitly trusts only the SPKI fingerprint derived from the exported local development certificate, without globally disabling browser TLS validation. Credentials, authorization codes and provider HTML are excluded from test output; traces are not recorded. Screenshots of the fictional login/profile/mobile screens are saved in ignored .local/login-tests.

The existing Node 20.18 installation supports Vite 6.4; tooling versions are recorded in the lockfile. Upgrade the Node runtime before adopting Vite's newer major releases. The dependency audit was clean after selecting a patched test runner.

## Remaining work

Development only. This milestone does not add accounts, balances, payments or history. Application logout does not end Keycloak SSO. Real email verification, MFA, production identity/workload authentication and AWS deployment remain future work. Financial features need their own contracts and authorization rules.
