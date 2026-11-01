# Unit tests

Nine xUnit test projects are included in CJora.sln under TESTS: one for each of the eight service APIs, plus CJora.DatabaseMigrations.UnitTests. Shared test package configuration is in TESTS/Directory.Build.props and imports the root .NET 8 settings.

Run all tests from the project root:

```powershell
dotnet test CJora.sln -warnaserror
```

Run one service:

```powershell
dotnet test TESTS/CJora.IdentityManagement.UnitTests/CJora.IdentityManagement.UnitTests.csproj
```

In VS Code, open CJora.code-workspace and use the Testing panel after the C# extension loads the solution.

## Current coverage

Each service has nine cases covering the health controller service identity/HTTP success, the weather controller five-item payload and future dates/value bounds, and known Celsius-to-Fahrenheit conversion values including negative temperatures. The formula uses exact 9/5 conversion with nearest-integer rounding, midpoint away from zero. Tests avoid exact random weather expectations and account for UTC date rollover.

Eight migration command tests cover SQL discovery and ordering, no command, unsupported commands, extra arguments, missing service-specific credentials, wrong database rejection and safe redaction of malformed connection settings. These tests never open a database connection. Process-wide environment/console state is restored in finally and test parallelization is disabled within that test assembly.

Validation on 1 November 2026: 133 tests passed, zero failed or skipped, across nine projects. The Gateway suite additionally covers local caller credentials, spoofed/oversized route metadata and production denial. The Core suite includes route policy, header/multipart preservation, redirect rejection, safe error fallback, bounded unknown-length bodies and the MVC form-filter regression. Compilation runs with warnings treated as errors. The tests caught and drove a fix for the weather template truncation error (20 C previously returned 67 F; now 68 F).

## Other validation

- SCRIPTS/Test-Foundation.ps1 starts API processes and checks HTTP routing/Swagger. This is an HTTP smoke check, not a unit test.
- SCRIPTS/Test-SchemaConventions.ps1 checks static SQL conventions. This is not database execution.
- MySQL constraints, migration application/replay/locking, Keycloak login, cookie/CSRF handling, Program isolation and session lifecycle require future integration tests and actual implementations. The current unit suite does not claim coverage of these unimplemented business flows. Docker is not required for these unit tests.

SCRIPTS/Test-DynamicGateway.ps1 additionally tests the Gateway-owned local MySQL registry and HTTPS JSON/document proxy with audited temporary registrations. It requires Core database provisioning and free ports 5075/5080/5082/7000, and stops only its own API processes. See DYNAMIC-GATEWAY.md for scope and security limitations.

## Login milestone

Run ./SCRIPTS/Initialize-Identity.ps1 after Gateway database setup, then ./SCRIPTS/Test-Login.ps1. Requires local Docker MySQL/Keycloak, Node 20+, free API ports and the development HTTPS certificate. The test starts/stops its own Core/Identity Management/Gateway processes and verifies actual code+PKCE login, cookie policy, linked database sessions/audits, CSRF/origin/return-path rejection, direct/internal access protection, membership revocation, scope denial, callback replay, logout and durable idempotent revocation workers. Test fixture membership changes are audited and restored in finally. Credentials and codes are not printed; TLS verification is enabled.

The unit suite now has 156 cases, including partial activation compensation, unavailable authority, expiry, cross-Program denial and durable logout fallback. Existing all-service weather/health/Swagger and dynamic Gateway tests remain separate regression checks.

Browser frontend: npm run test:web covers profile, sign-in, safe errors, logout and CSRF behavior. SCRIPTS/Test-Login.ps1 -Browser verifies the real Edge/Keycloak journey, secure cookies, reload, logout, token storage and mobile width. See WEB-APPLICATION.md.

Customer synchronization: SCRIPTS/Test-CustomerSync.ps1 uses real local MySQL/Kafka and isolated fictional customer fixtures. It covers outbox rollback, command idempotency, projection deduplication/ordering, schema/scope rejection, durable quarantine, database failure replay and publisher lease recovery. It temporarily restarts the dedicated Kafka container to check broker recovery. See CUSTOMER-SYNCHRONIZATION.md. Docker-independent backend unit coverage now has 184 cases.

Financial contracts: LedgerContractTests and FinancialRequestContractTests cover exact minor units (including values beyond JavaScript safe integers), overflow, balanced/immutable journals, transfer ownership/limits, idempotency, strict DTOs, financial permissions and review/session binding. node SCRIPTS/Test-FinancialContracts.mjs checks the proposed OpenAPI references and safety requirements. These checks do not implement or exercise financial persistence.

Current backend total after financial contracts: 247 tests across the nine unit projects. No financial API/database integration coverage is claimed yet; the posting implementation and migration are still future work.
