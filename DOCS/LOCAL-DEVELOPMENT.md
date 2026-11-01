# Local dependencies and Keycloak

Keycloak is selected for identity. CJora Identity Management still owns memberships and AUTH_SESSION; Core owns Programs, customers and CORE_SESSION. Provider authentication alone grants no Program access.

## Prerequisites

.NET SDK 8.0.403 and Git are installed. Docker engine and Compose are verified. The cjora-local MySQL and Keycloak containers are running with persistent named volumes. MySQL reports healthy and the cjora realm discovery endpoint returns the expected issuer. Independently generated local passwords are stored in ignored INFRASTRUCTURE/LOCAL/.env and were not displayed. Service databases/accounts and migrations are not provisioned/applied yet.

Copy `INFRASTRUCTURE/LOCAL/.env.example` to `.env` in the same directory and fill in unique local credentials using an editor. `.env` is ignored by Git; never commit it or display expanded Compose configuration in shared logs. Keep real API credentials in .NET User Secrets, environment variables or a deployed secret manager. User Secrets are not encrypted storage.

```powershell
docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml up -d
```

MySQL: localhost:3307. Keycloak: http://localhost:8080. MySQL stores persistent data; provision separate uppercase service databases and database-scoped accounts before applying service migrations. Do not use the root account for CJora APIs or routine migrations. Volumes survive `down`; do not use `down -v` unless intentionally destroying disposable data.

Keycloak uses its own persistent development database in this local profile. It is separate from CJora service databases and not a production configuration. A `cjora` realm is imported initially; existing realms are not overwritten on subsequent starts. No user, password, client secret, auto-registration or usable CJora application client is committed.

Next identity steps: register a confidential `cjora-web` OIDC client, require authorization code with PKCE S256, disable implicit/direct access grants, use exact HTTPS Gateway callback URI and approved origins, and store its secret only in Identity Management User Secrets. The proposed callback is `https://localhost:7000/api/v1/idm/sessions/callback`; HTTPS Gateway configuration and callback are not implemented yet. Do not register a wildcard redirect or use HTTP scaffolding for real login.

Email verification and password recovery require configured SMTP. Decide email policy and session expiry, create fictional test users manually, and implement the linked-session protocol before exposing login. Do not assume the imported realm implements MFA, verification, logout propagation or Program isolation.

Useful checks once Docker works:

```powershell
docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml ps
Invoke-RestMethod http://localhost:8080/realms/cjora/.well-known/openid-configuration
```

Kafka is deferred until customer synchronization. No cloud resources are provisioned.

Sources: https://www.keycloak.org/getting-started/getting-started-docker and https://www.keycloak.org/server/db . Image tags are pinned local defaults and require routine update review.

Gateway registry ownership is active: run SCRIPTS/Initialize-GatewayDatabase.ps1 to configure CJORA_GATEWAY, its migrations/runtime account, the legacy Core archive handoff and local Gateway-to-Core caller credential. Core migrations 0001-0004 and Gateway 0001-0002 are applied locally. Other service databases are not provisioned. Details: DYNAMIC-GATEWAY.md.
