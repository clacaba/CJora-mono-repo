# Accounts, ledger and internal-transfer contracts

Status: **contracts and pure domain rules implemented; financial persistence and HTTP endpoints are not implemented**. No financial migration has been applied, no accounts or funds are created, and no transfer can currently be posted. The existing login/customer synchronization behavior is unchanged.

Machine-readable proposal: [transactions-v1.openapi.json](CONTRACTS/transactions-v1.openapi.json). Domain code: Transactions.Domain/Ledger. Request, idempotency, permission and review contracts: Transactions.Application/Finance. This document governs the next implementation milestone.

## Scope and ownership

Transactions owns accounts, journals, entries, transfer intents, receipts, history and financial idempotency. Core owns Programs/customers/application sessions; Identity Management owns memberships/permissions/authentication sessions. The customer UUID remains identical across Core and Transactions. No Property entity or cross-service SQL foreign key is introduced.

Initial scope is fictional internal transfers between different active customer accounts in the same Program and currency. The source must belong to the authenticated customer. The recipient may be another customer in that Program; receiving does not expose their balance, membership, email or customer UUID. Both customers must be currently eligible in Core, and both accounts active. Customer restriction or Program disablement denies new execution.

No overdrafts, FX, external beneficiaries, fees, holds, interest or asynchronous bank settlement are supported. Cross-Program transfers are denied. Preparing a review does not reserve funds. Posted and available amounts are equal while holds do not exist; the API does not invent held balances.

Catalog currency definitions are PHP/SGD/USD with two minor digits, JPY with zero and KWD with three. This is a deliberately limited verified catalog, not a complete ISO list and not a Program enablement decision. Every Program must explicitly configure enabled currencies and positive per-currency transfer limits before execution. There is no implicit demo currency, limit or financial permission.

## Money

Database amounts are signed BIGINT integer minor units, bounded by 0 through 9223372036854775807 for nonnegative balances; transfer and posting amounts must be strictly positive. BigInteger is used for intermediate journal sums to detect overflow before conversion.

Public amounts are canonical decimal **strings**, never JSON floating-point/numeric values. For example, USD "1250" denotes 12.50, JPY "1250" denotes 1250 and KWD "1250" denotes 1.250. The exponent is currency metadata; no implicit rounding or conversion occurs.

Reject decimals, exponent notation, signs, whitespace, non-ASCII digits, leading zeroes, overflow, unknown/lowercase currency codes and zero transfer amounts. "0" is valid for a balance. Browser calculations/formatting must preserve these strings or use BigInt rather than converting to JavaScript Number. Both accounts and the request must use the same currency. Destination balance addition must also remain within signed BIGINT.

## Ledger invariants

Customer deposits are **credit-normal liabilities** from the platform's perspective. An internal transfer of 250 minor units generates exactly:

| Entry | Account | Effect on displayed customer balance |
| --- | --- | --- |
| DEBIT 250 | Source customer liability | Decreases by 250 |
| CREDIT 250 | Destination customer liability | Increases by 250 |

The source and destination balances together are conserved. A source balance exactly equal to the amount may become zero; it may never become negative. A negative destination or invalid/unknown account state is rejected rather than repaired silently.

Every posted journal has at least two strictly positive entries, valid debit/credit sides, one Program and one currency, and equal debit/credit totals. The generic domain validator bounds journals at 1000 entries and requires the total on each side to fit signed BIGINT. The first transfer implementation uses exactly two entries. A valid plan is an immutable proposal, not evidence of a database commit.

Posted journals/entries and the original receipt are append-only. Never edit or delete a posting to correct it. Reversal is a later separately authorized compensating journal linked uniquely to the original transfer; it is outside this API proposal. The original remains POSTED.

Account creation starts at zero. Future fictional funding must be an explicit, idempotent, audited Development-only journal: debit a clearly labeled demo clearing asset and credit the customer liability. It must not claim to represent actual cash. Customer synchronization does not create an account, issue money or create a membership.

## Session and transaction authorization

All public paths remain Gateway -> Core -> Transactions. Gateway owns routing lookup. Neither dynamic method existence nor a customer projection grants permission. Existing profile permission CUSTOMERS.PROFILE.READ is insufficient.

| Route | Required financial permission |
| --- | --- |
| Own accounts | ACCOUNTS.READ |
| Prepare, inspect own intent and confirm | TRANSFERS.CREATE |
| Own account history/detail | TRANSACTIONS.READ |

These are reserved contract codes; no existing user has been granted them by this change. Current session validation checks profile access and must be extended explicitly before financial routes are enabled.

Core must validate the opaque browser session, active Program/customer, linked active authentication session and exact current membership/permission on each request. Before confirmation, Transactions must obtain a fresh authority result over an authenticated internal service channel and verify that scope against the stored intent and locked source account. Recipient eligibility is checked against Core's current state. Kafka projection state cannot substitute for these checks; authority unavailability fails closed.

FinancialAuthorityContext is a typed internal adapter input, **not a token or proof of authentication**. Its pure guard requires known permission, all linked identifiers, UTC timestamps, no future validation time, validation age under 30 seconds and a nonexpired session. IFinancialAuthorityProvider has no implementation or DI registration yet. Implementing secure transport/live validation and binding it to the exact intent/terms is mandatory before exposing endpoints.

Never accept Program/customer/user/session UUIDs, permission flags, balances or journal entries from the public request body. Internal context must be produced by trusted code, not deserialized browser claims. Gateway strips caller-supplied internal headers. The future Core -> Transactions service credential must be independent of the Core -> Identity Management credential, and production requires proper workload authentication/private transport.

CSRF and exact Origin remain mandatory for mutations. Login alone is not production transaction authorization; a real-payment implementation must add an appropriate step-up/transaction-authorization design bound to server-stored significant terms. The prototype review/session rules do not claim MFA or transaction signing.

Authorization is evaluated immediately before execution. Revocation after that decision does not retroactively erase a committed journal. Record the decision and linked scope in the audit; failures/timeouts before authority is confirmed must not execute.

## Review and API flow

Only exact versioned routes in the proposed OpenAPI contract are intended. Dynamic SESSION routing currently remains fail-closed. Implemented Swagger does not advertise these proposed endpoints.

1. GET /api/v1/txn/accounts returns only the authenticated customer's accounts and approved own balances.
2. POST /api/v1/txn/transfer-intents validates terms and eligibility, stores an immutable review and returns 201. Identical request replay returns 200.
3. Show the server's source, approved recipient label, currency, amount, zero fee, equal total debit, reference and expiry. Do not trust client-calculated totals.
4. POST /api/v1/txn/transfer-intents/{intentUuid}/confirm accepts **only** reviewVersion=1. No amount or destination can be changed during confirmation.
5. GET /api/v1/txn/transfer-intents/{intentUuid} resolves the original operation after an uncertain response.
6. GET /api/v1/txn/accounts/{accountUuid}/transactions and its /{transferUuid} detail path show activity only in the authenticated customer's account perspective.

Review lifetime is five minutes. Bind it to Program, customer, initiating user and both original Core/authentication session UUIDs. Terms are hashed on the server. Editing terms requires a new intent; reauthentication never automatically confirms an old review. A different session cannot execute a PREPARED intent. Authorized users may still resolve their own already-terminal operation after logging in again.

Reference is optional; null and empty are the same canonical business value. Otherwise preserve it exactly, including spaces/case. Limit it to 140 printable ASCII characters in version one; no control characters or Unicode normalization surprises. Treat labels/reference as text when rendering and never write them into raw diagnostic logs.

Intent transitions are PREPARED -> POSTED, REJECTED or EXPIRED. Terminal states cannot execute again. Repeating a successful confirmation reads its immutable receipt and does not transition/post again. A unique transfer-per-intent constraint protects against duplicate confirmation using a different key. Insufficient funds or a changed account/policy may reject confirmation; a rejected/expired intent needs a new explicit review.

## Idempotency and uncertain outcomes

Both POST operations require a nonempty canonical lowercase UUID Idempotency-Key. Scope is (authenticated Program, authenticated customer, operation code, key). It is a request identifier, not authentication.

Hash a versioned canonical UTF8 JSON object with fixed field order. Preparation includes scope, operation, source/destination account UUIDs, currency, exact minor-unit string and canonical reference. Confirmation includes scope, operation, intent UUID and review version. The key selects the stored record and is not itself a business term in the digest.

Same scope/key/fingerprint returns the original stored outcome. Different payload under that key returns 409 without another posting. A record from another scope must never be replayed. Preparation replay returns the original review and its original expiry; it does not refresh the deadline.

Every completed authorized business rejection has a durable replayable outcome. Later funding or restriction removal must not turn the same rejected intent/key into a transfer. Authentication/CSRF denial does not consume a financial idempotency key or mutate an intent.

For a local transfer, POSTED means the entire MySQL transaction committed. A timeout or 503 does **not** prove failure. UNKNOWN is a client state, not a new financial posting state. Resolve the original intent while authenticated; retry unchanged with the original key if necessary. Never silently submit a second operation with a new key after an uncertain result. Kafka acknowledgement is not part of money commit.

## Proposed persistence and transaction boundary

A future Transactions migration 0003 will define the tables, local foreign keys/checks and posting routine. No such migration is authored or applied in this milestone.

| Table | Required responsibility |
| --- | --- |
| ACCOUNT | UUID, Program, customer-reference FK for customer liabilities, immutable currency/type, status, cached posted minor units, last per-account entry sequence, version and metadata |
| TRANSFER_INTENT | Owner and both originating session UUIDs, immutable reviewed terms/fingerprint, five-minute expiry, review version 1, PREPARED/terminal state, safe rejection code and metadata |
| FINANCIAL_REQUEST | Scoped unique idempotency tuple, canonical fingerprint and immutable approved response/status for replay |
| TRANSFER | Public UUID, unique intent FK, Program/currency, source/destination account FKs, positive amount and immutable receipt data |
| LEDGER_JOURNAL | UUID/operation/transfer linkage, one Program/currency, equal totals, posted UTC time and immutable actor/creation data |
| LEDGER_ENTRY | UUID, journal/account FKs bound to the same Program/currency, debit/credit, positive amount and unique per-account entry sequence |
| FINANCIAL_EVENT_OUTBOX | Minimal future post-commit notification/synchronization events, independent of posting success |
| API_LOG / ERROR_LOGS | Existing service-owned business/security audit and independent redacted errors |

Every table begins numeric ID then UUID, with uppercase identifiers and table/column documentation. Mutable records have creation/update actor/time and version metadata. Posted immutable records have creation metadata and no mutable financial fields. Use unique UUIDs, transfer-per-intent, scoped idempotency and account/entry sequence constraints.

MySQL row CHECK constraints alone cannot enforce a sum across ledger rows. The selected persistence boundary is a single Transactions-owned posting routine that owns the complete local MySQL transaction and revalidates all monetary invariants. A dedicated posting runtime receives EXECUTE plus required reads/append-only error privileges, not arbitrary financial table INSERT/UPDATE/DELETE. Its definer has narrowly scoped rights within Transactions only. Synchronization credentials must not gain posting rights.

Required confirmation order:

1. Validate external session/permission and resolve server-stored intent terms through trusted service calls before holding money locks.
2. The routine claims/locks the scoped idempotency record, then the intent, Program financial policy and involved accounts in stable numeric account-ID order. Use the same lock ordering everywhere.
3. Recheck owner/review/session binding, expiry, current policy, active accounts, same Program/currency, positive amount, source funds and destination overflow under locks.
4. Insert one transfer, balanced journal and two entries; update both cached balances/versions and per-account entry sequences.
5. Append the financial API_LOG record and any post-commit outbox event; persist the immutable idempotent outcome and POSTED intent.
6. Commit once. An authorized terminal rejection instead commits only its intent/outcome/audit, with no monetary entries. Unexpected errors roll back everything; record ERROR_LOGS independently.

The application domain planner is prevalidation, not a substitute for database enforcement. The routine must calculate from locked balances and stored terms rather than trusting caller-provided resulting balances. Do not call Kafka or external services while holding the database transaction.

A deadlock/lock timeout or disconnected commit has an uncertain/retryable outcome. Resolve by the same idempotency key/intent; never compensate by deleting entries or blindly moving the amount again. A process crash before commit leaves no partial financial state; after commit, replay returns the persisted receipt. An outbox crash affects event delivery, not committed balances.

## History, privacy and reconciliation

History is initially per owned account, with a default page size 20 and maximum 50. Increment each account's entry sequence while holding its money row lock in the posting transaction. This gives that account a commit-ordered sequence; do not assume auto-increment journal IDs are commit order.

The first page captures an upper account sequence; protected opaque cursors carry that bound, last seen sequence, account, Program/customer and filters. Later pages traverse descending sequence within that bound. A refresh starts a new view including newer entries. Cursors must be authenticated/encrypted with service-owned keys and must not expose numeric database IDs.

Return public UUIDs, own direction/amount, timestamp, approved counterparty label and reference. Never return the recipient's balance, local numeric IDs, raw ledger administration data or another customer's intent/receipt. Wrong-scope account/intent/transfer lookups are generic 404 after authentication. Private responses use no-store.

Cached account balances must reconcile to their credit/debit-normal ledger entries and per-account sequence. Scheduled reconciliation detects imbalance, missing/duplicated postings and cache drift, records safe errors and restricts affected financial operations for investigation. Do not silently change a balance to match an unverified figure.

## Acceptance for the next implementation

Domain/request tests already cover minor-unit precision, overflow, balanced entries, immutable journals, source ownership, Program/currency boundaries, policy absence/limits, scoped idempotency, injected fields, permissions, session freshness, review expiry and terminal state rules.

Database/API implementation must additionally prove:

- Concurrent transfers cannot overspend; opposing transfers use stable lock order.
- Every commit/rejection/replay has the intended atomic state and audit; no money survives rollback.
- Concurrent same-key and different-key confirmations cannot debit one intent twice.
- An actual posting credential cannot bypass the routine to create an unbalanced journal.
- Session/permission/customer restrictions fail closed, including authority outages.
- Commit disconnect/restart, deadlock retry and outbox replay preserve one financial effect.
- Account history pagination remains scoped and stable across concurrent commits.
- Fictional funding balances and reversals are auditable compensating journals, never hidden opening amounts.

Run dotnet test CJora.sln and node SCRIPTS/Test-FinancialContracts.mjs. The latter checks proposed references/shape/security requirements; it is not proof of runtime OpenAPI conformance.

Sources: [ISO currency-code overview](https://www.iso.org/iso-4217-currency-codes.html), [SIX maintenance-agency currency list](https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml) (example minor units verified 1 November 2026), [OWASP transaction authorization](https://cheatsheetseries.owasp.org/cheatsheets/Transaction_Authorization_Cheat_Sheet.html), [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457.html). CJora's prototype rules above are implementation decisions, not a claim of production readiness.
