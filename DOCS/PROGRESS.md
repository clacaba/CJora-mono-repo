# Current status - protected Development financial backend, 1 November 2026

Implemented the account list, immutable review, atomic confirmation, outcome lookup and scoped account history/detail APIs through HTTPS Gateway -> Core -> Transactions. Fresh linked-session/financial-permission checks and live Core recipient eligibility are required; profile permission and Kafka projection state alone do not authorize money operations. An independent signed Core/Transactions invocation binds session, route/query, body, key and nonce. Finance is Development-only.

Transactions migrations 0003/0004 are applied: explicit demo Program policy, accounts, intents, idempotent outcomes, transfers, immutable journals/entries, financial event outbox and durable integrity alerts. The locked definer owns financial routines; the API runtime has approved EXECUTE/reads and cannot directly mutate money. Numeric ID/UUID ordering and documentation comments pass convention checks. The solution has 46 projects.

Explicit fictional USD demo configuration was selected for integration testing: 100000 minor-unit initial funding and 1000000 per-transfer ceiling. Bootstrap uses an audited balanced demo-funding journal and preserves existing balances/permissions/configuration on replay. No actual cash, provider settlement or AWS resources exist. Financial event notifications remain queued; their dispatcher and reversal/admin APIs are not implemented.

Validation: zero build warnings/errors and 256 backend unit tests pass. Real MySQL tests pass concurrent overspending, same/different-key races, opposing transfers, terminal-rejection replay, ledger/audit rollback, abandoned-result recovery, direct-DML/helper/cross-database denial and corruption restriction/alerts, including already-restricted accounts. Real Edge/Keycloak HTTPS API tests pass session/CSRF/strict-body protections, current permissions, stale-projection denial, scoped history/cursors, discarded HTTP receipt recovery, expiry, logout and new-session review denial. The full financial API workflow also passed while Kafka was stopped, after which Kafka was restarted. Existing service health/Swagger, dynamic Gateway/upload and linked-login regressions pass.

See [FINANCIAL-BACKEND.md](FINANCIAL-BACKEND.md) for setup, endpoints, evidence and limitations. Next: React account, transfer entry/review/result and history screens. The current browser remains login/profile/logout. Production workload/step-up/operations are not claimed. Changes are local and unpushed.

Earlier entries below are historical and superseded by this status.

---

# Current status — account and ledger contracts, 1 November 2026

Account/ledger/internal-transfer contracts and pure domain/request rules are implemented. Money uses exact integer minor units and string API amounts; tested rules cover balanced immutable journals, ownership, Program/currency policy, no overdrafts, overflow, scoped idempotency, strict request DTOs, financial permission/freshness checks and session-bound five-minute reviews.

The full solution builds without warnings/errors; all 247 backend unit cases pass. Proposed OpenAPI reference/shape/security checks pass through node SCRIPTS/Test-FinancialContracts.mjs. The example currency minor-unit metadata was verified against the SIX maintenance-agency list. No currency/limit/financial permission was enabled for any Program/user.

See [FINANCIAL-CONTRACTS.md](FINANCIAL-CONTRACTS.md) and [proposed OpenAPI](CONTRACTS/transactions-v1.openapi.json). Financial endpoints, financial table migrations, a posting repository/routine, authority-provider implementation, accounts/funding and actual transfers are not implemented. No database changes or financial records were created by this milestone. Existing login/customer synchronization runtime wiring was not changed.

Next: implement Transactions migration 0003 and its single atomic posting routine, protected Core -> Transactions workflow and independent financial credentials; prove concurrency, rollback, idempotency and uncertain-commit recovery before enabling fictional transfers. Changes remain local and unpushed.

Earlier entries below are historical and superseded by this status.

---

# Current status — Kafka customer synchronization, 1 November 2026

Core -> Kafka -> Transactions customer synchronization is implemented for Development. Customer creation/update, audit and outbox insertion commit atomically through Core's CustomerWriter; the local operator CLI exposes that path without adding a public administration API. Existing customers can be reconciled safely. Transactions owns new Domain/Application/Infrastructure layers and an idempotent, versioned projection using the same customer UUID.

Local Kafka is running on loopback with persistent storage. Core 0006 and Transactions 0001–0002 are applied; migration replay and snapshot reconciliation are no-ops on repeated initialization. Runtime accounts are table-scoped and do not receive cross-database access. Secrets remain in ignored .env/User Secrets. The solution has 44 projects.

Validation: build has zero warnings/errors; 184 backend unit cases pass. Real Kafka/MySQL checks pass atomic rollback, command idempotency, transient database connection failure, duplicate/stale/out-of-order snapshots, immutable scope, poison-event quarantine, following valid messages, expired lease/old-owner rejection and broker restart recovery. Core commits while Kafka is stopped and the event delivers after restart. Runtime permission denials and independently persisted operator errors were verified. Existing eight-service health/Swagger, dynamic Gateway uploads/routing and secure login regressions pass.

See [CUSTOMER-SYNCHRONIZATION.md](CUSTOMER-SYNCHRONIZATION.md) for setup, the operator command, event contract and recovery limits. This is at-least-once delivery with idempotent database effects. Kafka is a single local broker without production transport/security configuration. Test fixtures/rejections are retained; production monitoring/recovery administration remains future work. Changes are local and unpushed.

Next: define account ownership, currency/amount representation, ledger invariants and transfer permissions/idempotency before implementing accounts, ledger or transfers. No financial behavior or AWS resources were added.

Earlier entries below are historical and superseded by this status.

---

# Current status — React browser milestone, 1 November 2026

React/TypeScript browser login, authorized customer profile and logout are implemented at https://localhost:7000/app. Gateway hosts the built application on the same HTTPS origin as the API. Email is passed as a login hint; Keycloak handles password entry. Secure HttpOnly sessions and session-bound CSRF remain in use. Fixed /app and /app/login-failed destinations are allowlisted; arbitrary redirects remain denied.

Validation: 163 backend unit cases and 10 frontend cases pass; frontend build and .NET build pass; npm audit reports zero vulnerabilities. Real Edge testing passes login -> Keycloak -> profile -> reload -> logout, cookie policy, empty browser token storage and mobile width. Desktop/mobile screenshots were inspected. Dynamic Gateway and eight-service health/Swagger regression checks pass. Built files, browser screenshots, node_modules and secrets remain ignored. Changes are local and have not been committed or pushed in this milestone.

See [WEB-APPLICATION.md](WEB-APPLICATION.md) for setup and tests. Still Development only. Next work is customer synchronization and the Transactions foundation, with ledger and transfer contracts defined before financial behavior is implemented.

Earlier entries below are historical and superseded by this status.

---

# Current status — local login milestone, 1 November 2026

The Development login milestone is implemented: Keycloak authorization code + PKCE, explicit fictional Program/customer/issuer-subject identity, linked AUTH_SESSION/CORE_SESSION, own-profile access, coordinated logout, atomic session audit and redacted independent errors. The solution has 37 projects. Core migrations 0001–0005, Gateway 0001–0002 and Identity Management 0001–0002 are applied locally. Other service business schemas are not provisioned.

Build passes with zero warnings/errors; 156 unit tests pass. Real Keycloak/MySQL HTTPS tests verify login, scope, CSRF, callback replay, membership suspension, logout, audit rows and idempotent durable revocation recovery. All eight sample controllers and Swagger remain intact. No React frontend, real email verification, MFA, provider-global logout or financial features exist yet. Login currently requires Development and local workload credentials; it is not a production deployment.

See [LOGIN-MILESTONE.md](LOGIN-MILESTONE.md) for setup, exact API sequence and limitations. Next: add the React browser login/profile/logout flow and then customer synchronization/Transactions work. Production service authentication and identity hardening remain required before deployment.

The sections below are a chronological work log; earlier statements describe earlier stages and are superseded by this current status.

---

# CJora progress

Updated 1 November 2026. Workspace: `C:\Users\RIEN\Projects\CJora`. The rename is complete. The archived external plan remains unchanged.

## Completed foundation

- .NET SDK 8.0.403, net8.0 targets, recreated CJora.sln with 17 projects.
- Core retains Api/Application/Domain/Infrastructure and localhost:5075.
- Added Gateway with YARP on localhost:5080; its only upstream is Core.
- Added Identity Management Api/Application/Domain/Infrastructure on localhost:5076 with the intended layer references. Login endpoints are not implemented.
- Selected Keycloak. Prepared loopback-only Docker Compose for MySQL and persistent development Keycloak, initial realm and an empty credentials template. No secrets or users committed.
- Added independent executable migration projects for all seven database-owning services. Each owns its scripts and history; Gateway remains database-free. Added initial documented API_LOG/ERROR_LOGS SQL for each service. Scripts have not been applied.
- Added .NET User Secrets identifiers to Core and Identity Management; no credentials configured.
- Initialized local Git with ignore rules for outputs, local environment files and secrets. No remote configured or code pushed.
- Design remains deferred. Existing design/contract documents are drafts, not implemented behavior.

## Validation

- `dotnet build CJora.sln`: zero warnings and zero errors.
- `SCRIPTS/Test-Foundation.ps1`: passed Core/Gateway/Identity Management liveness, Gateway-to-Core forwarding, Core Swagger and unavailable public Swagger/sample/login routes. Temporary API processes were stopped after testing.
- All seven migration executables list their owned SQL resources.
- Core migration CLI rejects missing credentials and wrong-database configuration before connecting.
- MySqlConnector updated to 2.6.1 after NuGet flagged an older cached version.

## Blocked dependency verification

Docker CLI and Compose are installed; the last check could not reach the desktop-linux engine. Docker integration is now deferred by request. Containers, MySQL DDL, migration replay/concurrency/recovery and Keycloak realm import remain unverified. Do not treat liveness checks as dependency readiness.

## Next milestone work

1. Start local MySQL/Keycloak and provision isolated service databases and migration/runtime accounts; apply and integration-test migrations.
2. Register the confidential Keycloak client, configure HTTPS ingress, exact callbacks, PKCE and SMTP; settle verified-email/customer cardinality/session-expiry policies.
3. Implement Core Program/customer/session and Identity Management membership/authentication-session schemas with documented columns and constraints.
4. Implement login, authoritative Program/session authorization, own-profile access and coordinated logout with local atomic audit and independent redacted error persistence.
5. Prove cross-Program denial, revoked-session denial, CSRF protection and partial-failure recovery. Kafka/customer synchronization and ledger follow later.

Business login, linked sessions, profile authorization, runtime audit/error writes and financial features are not implemented. No AWS resources provisioned.
## Editor and template cleanup

Removed unused WeatherForecast controller/model and empty Core Class1 placeholders. Updated the HTTP request examples to the actual Core health route and added CJora.code-workspace with explicit solution selection. Cleared generated SRC bin/obj outputs and restored/rebuilt all 17 projects with warnings treated as errors: zero warnings and zero errors. Foundation smoke check passed after cleanup.

VS Code logs showed prior standalone-file loading and MonkSearch project discovery. Those projects are not in the current CJora solution or current root directory. No diagnostics were disabled. Reopen the CJora workspace or reload the editor to refresh stale Problems entries; the live Problems list itself has not been directly inspected.
## Identity Management service naming

The identity service is now `Identity Management`, service code `idm`, under `SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.*`. Updated all five projects, solution paths/references, liveness identity, draft API/callback paths, documentation, smoke script and local User Secrets identifier. Migration target is `CJORA_IDENTITY_MANAGEMENT`, configured through `CJORA_IDENTITY_MANAGEMENT_MIGRATION_CONNECTION`. No existing database was renamed or changed. The identity migration resource enumerates correctly; full solution build has zero warnings/errors and routing smoke checks pass after the rename.
## Login schema preparation with Docker deferred

Authored migration 0002 in Core (PROGRAM, CUSTOMER, CORE_SESSION) and Identity Management (USER_ACCOUNT, MEMBERSHIP, MEMBERSHIP_PERMISSION, AUTH_SESSION, SESSION_WORKFLOW). Documented ownership, one-customer-per-user/Program initial model, cross-service UUID linkage, local composite scope constraints, application-managed concurrency, pending/active/revoked states and durable coordination fields in LOGIN-SCHEMA.md. No credentials, customers, permission grants, login handlers or database changes created. Build and embedded discovery pass; static schema convention validation is separate from deferred MySQL execution. Next independent work is domain/session rules and unit tests; database integration and OIDC activation remain deferred.
## WeatherForecast and Health controllers in all services

Restored the fictional weather model/controller in Core and added WeatherForecastController and HealthController to all eight APIs, replacing inline liveness routes with controller routes. Added the missing TPP, Payments, Transactions, Notifications and Documents API shells and their development launch profiles. The solution now contains 22 projects. Swagger is enabled in Development for all eight APIs; migration projects remain separate.

Validation: full build with warnings treated as errors passed with zero warnings/errors. Updated foundation smoke script verified each API health route, five-item weather response, Swagger UI/OpenAPI controller entries, seven non-Gateway versioned health aliases, and Gateway-to-Core forwarding. Login and direct Identity Management proxy routes remain unavailable. Temporary API processes were stopped. Ports and routes are documented in SERVICE-ENDPOINTS.md. Docker remains deferred; no database changes applied.
## Docker-independent unit tests

Added eight service API unit-test projects and one shared migration command test project under TESTS, bringing the solution to 31 projects. Tests verify sample controller contracts/conversions and migration CLI safety/redaction without Docker or MySQL. The tests exposed a temperature conversion truncation issue, fixed consistently in all eight sample models using 9/5 and explicit nearest-integer rounding.

Validation: `dotnet test CJora.sln --no-restore -warnaserror` with TRX output passed all 80 tests across nine projects, zero failures/skips. Results are in ignored .local/test-results. README and TESTING.md document all-service/single-service commands, VS Code Testing, and the distinction between unit, smoke and future database/identity integration checks. No business authentication/session behavior is claimed by the sample tests.
## Local dependency startup verified

Docker engine 29.8.1 and Compose v5.5.1 are reachable. Created ignored .env with independent random local MySQL root and Keycloak bootstrap-admin passwords; values were not displayed. Started cjora-local MySQL 8.4.7 on 127.0.0.1:3307 and Keycloak 26.8.1 on 127.0.0.1:8080 with persistent volumes. MySQL health is healthy; Keycloak imported cjora successfully and its discovery endpoint returns issuer http://localhost:8080/realms/cjora. Initial startup readiness polling expired before verification; a subsequent direct check confirmed both dependencies ready. Containers remain running. Service databases/accounts, migration application, OIDC client and business login remain unfinished.
## Dynamic registry and HTTPS foundation - 1 November 2026

Reviewed PSA_DOTNET reference routing and adopted Core-owned SERVICE_METHOD lookup with Gateway -> Core -> approved destination flow. Added migration 0003 with audited diagnostic registrations, parameterized read-only lookup, fail-closed validation, JSON/multipart forwarding, per-operation body/time bounds, safe error persistence/fallback and development transport check endpoints. Core migrations 0001-0003 are now applied to local CJORA_CORE; reapplication is a no-op. Provisioned separate migrator and restricted registry runtime accounts; generated credentials remain in ignored .env and User Secrets, never displayed. Runtime route updates are denied.

The user selected HTTPS for browser/API security. Gateway now listens on https://localhost:7000, redirects HTTP dynamic/versioned requests, retains loopback diagnostic HTTP and applies rate limits. No reference custom encryption was copied. Exported only the existing development certificate public portion for tests; no certificate validation bypass or automatic OS trust change.

Validation: zero build warnings/errors; 121 unit tests pass across nine projects. Full eight-service smoke tests pass. Live MySQL/HTTPS integration verifies health lookup, JSON, binary multipart/repeated fields, disabled/untested/missing method handling, verb/media errors, fixed/chunked upload limits, metadata and Gateway destination rejection, session-policy denial, ERROR_LOGS, audited database-only registration changes and HTTP 429. All temporary API processes and registry fixtures were cleaned up; MySQL/Keycloak containers remain running. Database metadata confirms comments and ID/UUID order; three seeded method registrations each have an API_LOG record.

Multipart testing caught MVC pre-reading forms and a chunked-body reset; fixed with a form-preservation resource filter and bounded unknown-length preflight. Database testing caught CHAR(36) GUID mapping; now read with GetGuid. Repeated local provisioning caught a PowerShell piped-JSON encoding issue; fixed with UTF8 without BOM. Remaining scope: real session authorization, production workload/TLS configuration, real document storage/scanning and formal penetration testing. Details in DYNAMIC-GATEWAY.md.
## Registry ownership correction - 1 November 2026

The user specified Gateway database ownership. Live SERVICE_METHOD and lookup now belong to CJORA_GATEWAY, with Gateway Application/Infrastructure/Migrations projects and independent history. Core receives authenticated immutable route metadata and performs no registry lookup. Shared building blocks contain technical routing DTO/policy/transport/error-writing code only; no runtime cross-database access is introduced. Solution: 36 projects.

Preserved immutable Core migration 0003; new Core 0004 archives its table and audits the retirement. Gateway 0001/0002 create its logs/live registry; the explicit local operator setup imports the three legacy UUIDs/metadata with three target API_LOG records, regenerating local IDs. Archive and original Core audits/history remain intact. Setup replay is a no-op for rows/history. Former Core registry account revoked/locked; Core now has INSERT-only error-log runtime access. Gateway has SELECT registry/INSERT own errors only. Separate credentials and a local-development bridge credential remain in ignored .env/User Secrets, never printed.

The route-context bridge is loopback-only, uses constant-time credential validation and strips forged/internal headers. It is denied outside Development until real production workload identity is implemented. Browser HTTPS and Gateway -> Core -> backend flow remain intact; protected session operations still fail closed.

Validation: 133 unit tests pass across nine projects; build has zero warnings/errors; all eight service smoke checks pass. HTTPS JSON/binary multipart, fixed/chunked size bounds, dynamic Gateway database changes/audits, errors, destination checks and 429 checks pass. Forged metadata is rejected directly at Core and through the versioned Gateway proxy; internal credentials do not reach backend endpoints. Actual MySQL denial checks prove Gateway cannot read Core business tables or edit routes, and Core cannot read Gateway registrations or its historical archive. Source inspection confirms no Core registry provider/connection remains. Core has no live SERVICE_METHOD table. Details rewritten in DYNAMIC-GATEWAY.md and MIGRATIONS.md.