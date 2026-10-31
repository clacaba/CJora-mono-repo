# CJora progress

Updated 31 October 2026. Workspace: `C:\Users\RIEN\Projects\CJora`. The rename is complete. The archived external plan remains unchanged.

## Completed foundation

- .NET SDK 8.0.403, net8.0 targets, recreated CJora.sln with 17 projects.
- Core retains Api/Application/Domain/Infrastructure and localhost:5075.
- Added Gateway with YARP on localhost:5080; its only upstream is Core.
- Added User Accounts Api/Application/Domain/Infrastructure on localhost:5076 with the intended layer references. Login endpoints are not implemented.
- Selected Keycloak. Prepared loopback-only Docker Compose for MySQL and persistent development Keycloak, initial realm and an empty credentials template. No secrets or users committed.
- Added independent executable migration projects for all seven database-owning services. Each owns its scripts and history; Gateway remains database-free. Added initial documented API_LOG/ERROR_LOGS SQL for each service. Scripts have not been applied.
- Added .NET User Secrets identifiers to Core and User Accounts; no credentials configured.
- Initialized local Git with ignore rules for outputs, local environment files and secrets. No remote configured or code pushed.
- Design remains deferred. Existing design/contract documents are drafts, not implemented behavior.

## Validation

- `dotnet build CJora.sln`: zero warnings and zero errors.
- `SCRIPTS/Test-Foundation.ps1`: passed Core/Gateway/User Accounts liveness, Gateway-to-Core forwarding, Core Swagger and unavailable public Swagger/sample/login routes. Temporary API processes were stopped after testing.
- All seven migration executables list their owned SQL resources.
- Core migration CLI rejects missing credentials and wrong-database configuration before connecting.
- MySqlConnector updated to 2.6.1 after NuGet flagged an older cached version.

## Blocked dependency verification

Docker CLI/Desktop was not found. Containers, Compose runtime, MySQL DDL, migration replay/concurrency/recovery and Keycloak realm import have not been verified. Install/start Docker Desktop before following `LOCAL-DEVELOPMENT.md`. Do not treat liveness checks as dependency readiness.

## Next milestone work

1. Start local MySQL/Keycloak and provision isolated service databases and migration/runtime accounts; apply and integration-test migrations.
2. Register the confidential Keycloak client, configure HTTPS ingress, exact callbacks, PKCE and SMTP; settle verified-email/customer cardinality/session-expiry policies.
3. Implement Core Program/customer/session and User Accounts membership/authentication-session schemas with documented columns and constraints.
4. Implement login, authoritative Program/session authorization, own-profile access and coordinated logout with local atomic audit and independent redacted error persistence.
5. Prove cross-Program denial, revoked-session denial, CSRF protection and partial-failure recovery. Kafka/customer synchronization and ledger follow later.

Business login, linked sessions, profile authorization, runtime audit/error writes and financial features are not implemented. No AWS resources provisioned.