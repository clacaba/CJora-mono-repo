# Development financial backend

The account, review, transfer-confirmation and per-account history APIs are implemented through **HTTPS Gateway -> Core -> Transactions**. Transactions migrations **0003** and **0004** are applied locally. This is a fictional Development ledger: no bank/provider connection, real funds, FX, overdraft, fees, holds or external payments.

The browser application still provides login/profile/logout. Account and transfer screens are the next UI milestone; the financial backend is currently exercised through API/integration tests.

## Setup

Complete local identity and customer synchronization first. From the repository root:

```powershell
dotnet build CJora.sln -warnaserror
./SCRIPTS/Initialize-Finance.ps1
```

Without demo options, initialization creates/updates the schema, locked definer, execute-only financial runtime account and independent Core/Transactions workload credential. It does not create a policy, financial permissions, accounts or money. Both services use ignored User Secrets; initialization does not provision AWS.

To explicitly create the supplied **fictional USD demonstration**:

```powershell
./SCRIPTS/Initialize-Finance.ps1 -DemoCurrency USD -MaxTransferMinor 1000000 -DemoFundingMinor 100000
```

Those are minor units: a fictional 1,000.00 USD opening funding journal and a 10,000.00 USD per-transfer ceiling. They are test configuration, not product defaults or actual cash. The existing Alex Demo user receives ACCOUNTS.READ, TRANSFERS.CREATE and TRANSACTIONS.READ in its existing Program membership through audited operator inserts. The recipient is a fictional receiving-only Core customer; no new login credential is created.

Customer creation uses Core's normal atomic writer/outbox, then the normal Kafka projection. Accounts start at zero; bootstrap records a balanced DEMO_FUNDING journal, debit to fictional clearing asset and credit to source liability. Bootstrap is idempotent and never resets an existing balance, adds the funding a second time, silently changes a policy, or reenables a previously disabled permission. Changed funding/policy requires explicit operator review.

Public fixture identifiers and configuration are in ignored .local/finance-fixture.json. Passwords/workload keys remain only in ignored .env and User Secrets. The schema validation helper owns a disposable database named CJORA_TRANSACTIONS_FINANCE_CHECKS; do not store other data there.

Start these four APIs in separate terminals, then use the browser login:

```powershell
dotnet run --project SRC/CORE/CJora.Core.Api
dotnet run --project SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Api
dotnet run --project SRC/TRANSACTIONS/CJora.Transactions.Api
dotnet run --project SRC/GATEWAY/CJora.Gateway.Api
```

Build the browser first with npm ci and npm run build:web on a fresh checkout. Open https://localhost:7000/app. The existing email is alex@example.test; its password remains in the ignored local environment file.

## API workflow

The [OpenAPI contract](CONTRACTS/transactions-v1.openapi.json) and [financial rules](FINANCIAL-CONTRACTS.md) describe request/response fields.

| Request | Behavior |
| --- | --- |
| GET /api/v1/txn/accounts | Owned customer liabilities, with exact string balances; no clearing/recipient accounts |
| POST /api/v1/txn/transfer-intents | Validates syntax/current eligibility/policy/funds and stores immutable five-minute review; no reservation |
| GET /api/v1/txn/transfer-intents/{intentUuid} | Resolves the owner's original operation, including uncertain outcomes |
| POST /api/v1/txn/transfer-intents/{intentUuid}/confirm | Accepts only reviewVersion=1; atomically posts or persists terminal rejection |
| GET /api/v1/txn/accounts/{accountUuid}/transactions | Owned account's internal-transfer history with protected keyset cursor |
| GET /api/v1/txn/accounts/{accountUuid}/transactions/{transferUuid} | Owned account perspective; no recipient balance/customer identity |

For POST, fetch a fresh token from /api/v1/idm/csrf, send X-CSRF-TOKEN and a canonical nonempty UUID Idempotency-Key. Browser Origin must be exactly https://localhost:7000. Cookies are sent same-origin. Browser request bodies cannot provide Program/customer/user/session authority, balances or journal entries. All amount minorUnits fields are decimal strings.

Example preparation (replace account IDs using the fixture file):

```json
{
  "sourceAccountUuid": "11111111-1111-4111-8111-111111111111",
  "destinationAccountUuid": "22222222-2222-4222-8222-222222222222",
  "amount": {"currency":"USD","minorUnits":"123"},
  "reference":"Fictional example"
}
```

Confirm the returned intent using a separate idempotency key and body {"reviewVersion":1}. Successful first writes return 201; replay returns 200 with the same receipt. Different payload under a used key returns 409. Another confirmation key for the same posted intent still cannot create a second transfer.

Insufficient funds/current policy restriction at confirmation commits REJECTED and its replayable error, without money entries. Expiry at execution commits EXPIRED. Session/permission failure makes no financial change. GET may show PREPARED with a passed deadline until confirmation evaluates/persists expiry; the deadline is always enforced before execution.

A timeout/503 is not proof of failure. Resolve the original intent and retry unchanged with its original key. Never submit a replacement amount/key automatically after an uncertain result. Already-posted/rejected outcomes can be resolved after reauthentication; a PREPARED review bound to the previous session pair cannot execute.

## Trust and money boundary

Core validates the browser cookie and linked sessions. Identity Management separately checks the exact current financial membership permission. Profile permission alone is insufficient. Core owns live Program/customer eligibility; no Kafka projection may substitute for that decision.

Core signs a short-lived invocation bound to session UUID, method, internal path/query, body SHA256, idempotency key and nonce. The separate Core/Transactions credential and invocation headers are replaced/removed at Gateway; clients cannot supply authority. Transactions requires loopback Development callers, verifies signature/request binding and rejects same-process nonce replay. Its provider makes authenticated live calls back to Core immediately before execution, including recipient eligibility and immutable intent terms. The SQL transaction checks the authority's age/expiry again after acquiring money locks. Process-local nonce memory is additional defense; durable idempotency/unique-intent constraints protect financial effects across retries/restarts.

The signed invocation is integrity/workload protection for the local prototype, not custom browser encryption. Browser traffic uses HTTPS and the opaque HttpOnly session cookie. Production workload identity/private TLS, transaction step-up and provider-global logout remain future work; enabling the finance feature outside Development fails startup.

FINANCE_PREPARE and FINANCE_CONFIRM own complete local MySQL transactions. They lock scoped idempotency, intent, policy and accounts in stable numeric account order, then recompute from locked balances. Confirmation commits two balanced entries, both cached balances/sequences, one transfer, POSTED intent, immutable outcome, API_LOG and FINANCIAL_EVENT_OUTBOX together. Unexpected errors roll everything back. No HTTP/Kafka call occurs inside that transaction.

The posting account can read required service-owned tables, execute only approved routines and append safe audit/errors. It cannot directly INSERT/UPDATE/DELETE financial state or invoke private JSON/audit helper routines. Customer synchronization credentials have no financial table/routine rights. The definer is locked against login and has only its required Transactions table permissions. The migrator's SET_ANY_DEFINER privilege is granted only during the local routine migration and revoked in finally; no root/migrator connection is used by APIs.

The financial outbox is durable but its notification dispatcher is **not implemented**; PENDING events do not delay/undo money commit. Journal entries/receipts are immutable. Reversals and financial administration APIs are not implemented.

## Reconciliation and history

A worker checks accounts in batches every minute through FINANCE_RECONCILE. It compares normal-side entry totals, account sequence/count and journal debit/credit totals. An inconsistent active account becomes RESTRICTED without changing its cached balance or entries.

Migration 0004 adds deduplicated ACCOUNT_INTEGRITY_ALERT investigations. A previously restricted or closed account still gets an alert/error for a newly detected failure; a closed account is not reopened. Alerts are not automatically cleared when a later check happens to match. Operators must investigate, correct the cause through an approved audited process and explicitly resolve the alert before considering restoration. No administration UI, automatic financial repair or complete production recovery runbook is claimed.

History is internal transfers only; demo funding is retained in ledger/audit for inspection rather than presented as a customer-to-customer transfer. Protected cursors bind Program/customer/account/page-size and upper entry sequence, expire after 15 minutes, and never expose numeric IDs. The per-account entry sequence is allocated under the account money lock, avoiding auto-increment/commit-order ambiguity. New transfers appear on refresh.

## Verification

```powershell
dotnet test CJora.sln --no-restore -warnaserror
./SCRIPTS/Test-Finance.ps1
./SCRIPTS/Test-Finance.ps1 -Api
./SCRIPTS/Test-Foundation.ps1
./SCRIPTS/Test-DynamicGateway.ps1
./SCRIPTS/Test-Login.ps1
node SCRIPTS/Test-FinancialContracts.mjs
```

The backend suite has 256 unit tests. Database checks retain isolated synthetic Programs/customers/accounts unreachable through live Core authorization. They use the actual execute-only credential for financial operations and a service-scoped migrator for inspection. A bootstrap administrator is used only to create/drop temporary, fixture-Program-scoped fault triggers; API processes never inherit those test credentials. Deliberate fixture corruption is audited and retained as restricted investigation data.

The database checks cover concurrent overspending, same/different-key races, opposing transfers, permanent rejection replay after funds return, ledger/audit fault rollback, lost outcome resolution, direct-DML/helper/cross-service denials and integrity detection for already-restricted accounts. Fault triggers are removed in finally. Do not interrupt the harness while it has installed a trigger; if interrupted, inspect only its named CJORA_FINANCE_TEST_* triggers before rerunning.

The Edge/real-Keycloak HTTPS API check covers strict DTOs/CSRF/keys, immutable reviews, one posting, owner privacy, permission revocation, stale-ACTIVE-projection denial, cursor tamper/scope/pagination, deliberately discarded HTTP receipt, expiry, logout and reauthentication binding. Its Transactions customer-sync consumer is disabled only for that test process to prove fresh Core authority, then restored. Tests use the pinned local certificate/public CA, not global TLS skipping. Credentials, signatures, cookies and raw provider pages stay out of output.

The API workflow was also verified while the dedicated local Kafka broker was stopped, then the broker was restarted. Money success is independent of Kafka availability. Existing all-service health/WeatherForecast/Swagger, dynamic JSON/multipart routing and linked-session tests remain separate regressions.

## Next

Build the React account list, transfer-entry/review/result and per-account history screens against these implemented endpoints. Production security/operations, external payment integrations, holds/fees/FX, reversals and financial notification delivery remain separate work.
