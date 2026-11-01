# Local login milestone

Implemented for Development. Browser traffic uses HTTPS Gateway -> Core. Core calls Identity Management using an independent local workload credential. Identity Management uses Keycloak authorization code flow with PKCE S256 and a confidential client. Keycloak validates credentials; no password-grant login endpoint exists in CJora.

## Start and verify

Run commands from the repository root in PowerShell:

```powershell
docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml up -d
./SCRIPTS/Initialize-GatewayDatabase.ps1
./SCRIPTS/Initialize-Identity.ps1
dotnet build CJora.sln -warnaserror
dotnet test CJora.sln --no-build
./SCRIPTS/Test-Login.ps1
```

The login test temporarily starts Core, Identity Management and Gateway, tests against real local Keycloak/MySQL, and stops only its own API processes. Requires Node 20+, Docker CLI, free API ports and the local HTTPS development certificate. It exports only the public certificate for TLS verification; it never skips verification. Credentials, cookies, authorization codes and HTML remain out of test output.

To run the APIs yourself, use three terminals:

```powershell
dotnet run --project SRC/CORE/CJora.Core.Api
dotnet run --project SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Api
dotnet run --project SRC/GATEWAY/CJora.Gateway.Api
```

Gateway: https://localhost:7000. Core: loopback 5075. Identity Management: loopback 5076. Keycloak's development listener is http://localhost:8080. Use Development launch profiles. The React browser application is available at /app after npm run build:web; see WEB-APPLICATION.md. A same-origin browser client follows the API sequence below. Test-Login automates the provider form and callback.

## Fictional fixture and configuration

Initializer provisions Program `cjora-demo`, customer Alex Demo and Keycloak user `alex@example.test`. The random demo password is stored only in ignored `INFRASTRUCTURE/LOCAL/.env` as `CJORA_DEMO_USER_PASSWORD`; inspect it locally in an editor. Initializer does not print credentials, reset an existing password or silently relink an existing identity.

Email verification for this fictional fixture is explicitly asserted by the local operator. SMTP, real verification messages, password recovery, MFA and customer enrollment are not implemented. Email is a provider login identifier, while local account linkage uses exact verified issuer + subject.

The initializer applies service-owned migrations, creates database-scoped migrators and table-scoped runtime accounts, seeds audited fictional records and writes .NET User Secrets. Runtime accounts cannot modify memberships, Programs or customers, and do not receive cross-database access. Local secrets, database volumes and data-protection keys must stay outside Git. Windows data-protection keys are DPAPI protected. User Secrets themselves are not encrypted storage.

## Public API sequence

| Method/path at Gateway | Behavior |
| --- | --- |
| GET /api/v1/cor/programs/resolve?key=cjora-demo | Enabled Program UUID and branding |
| GET /api/v1/idm/csrf | Issues secure CSRF cookie and JSON requestToken |
| POST /api/v1/idm/sessions | JSON programKey, email, returnPath; requires Origin and X-CSRF-TOKEN; returns authorizationUrl |
| GET /api/v1/idm/sessions/callback | Keycloak callback handled through Core; never call manually |
| GET /api/v1/cor/customers/me | Current customer's approved UUID, Program UUID, display name and email |
| GET /api/v1/cor/sessions/current | Current session UUID, customer UUID, Program UUID and absolute expiry |
| DELETE /api/v1/idm/sessions/current | CSRF + exact Origin required; revokes both sessions and clears cookie |

Origin is exactly `https://localhost:7000`. Fetch a new CSRF token after login because the token is bound to the current session cookie. Only `/app`, `/api/v1/cor/customers/me` and `/api/v1/cor/sessions/current` are approved returnPath values. Navigate the browser to authorizationUrl; provider redirects are handled by ASP.NET Core OIDC middleware, including state, correlation, nonce, signatures, issuer, audience and lifetime validation.

The browser receives only `__Host-cjora-session`: Secure, HttpOnly, SameSite=Lax, Path=/, no Domain. Access/refresh/ID tokens are not returned to the browser or persisted in application tables. Only the SHA256 digest of the random 256-bit application cookie is stored. A replayed activation cannot reissue its original secret.

Core resolves customer/Program scope from the session and checks Identity Management live on every protected request. Optional X-Program-UUID/X-Customer-UUID headers can only narrow access; mismatches return 403. Missing/expired/revoked session returns 401; authorization denial 403; unavailable dependencies 503. Successful profile reads do not create business audit rows.

## Sessions, audit and recovery

Identity Management creates PENDING AUTH_SESSION with a durable activation workflow; Core prepares PENDING CORE_SESSION. Identity Management activates first, Core validates that authority and activates second, then the browser cookie is issued. Both services audit their session creation/activation/revocation atomically. Sessions expire after 8 hours absolute or 15 minutes idle.

Partial login failures compensate by revoking both sessions. A pending activation left by a crash becomes eligible for revocation after 2 minutes. Logout revokes Identity Management first, then Core. If the identity call fails, Core revokes locally, queues durable revocation and returns 503 REVOCATION_PENDING while clearing the browser cookie. Recovery workers poll every 15 seconds, use idempotent revocation, retry at most 10 times and mark unresolved work MANUAL_REVIEW. Operators must monitor and resolve that state; no administration screen exists yet.

Errors are independently recorded in service-owned ERROR_LOGS with safe categories/reference IDs and local fallback. Security denials are audited; secrets, raw provider messages and request bodies are excluded. Application logout does not terminate the Keycloak SSO session. Provider backchannel/global logout is future work.

## Boundary and deployment limits

SERVICE_METHOD remains in Gateway's database. Login endpoints are explicit versioned contracts through Gateway -> Core; dynamic registry SESSION methods continue to fail closed until per-method authorization is implemented. Internal prepare/activate/validate/revoke endpoints are not public registry entries.

Local workload authentication requires independent 64-hex credentials, loopback peer and Development. Gateway replaces client-supplied internal metadata before proxying. Login is disabled unless Identity:Enabled is configured and is deliberately unavailable outside Development. Production needs proper service authentication/private networking, HTTPS identity provider, durable protected key storage, a production Keycloak database, operational monitoring, SMTP and security review. Nothing has been provisioned in AWS.
