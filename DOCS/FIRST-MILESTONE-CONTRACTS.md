# First milestone API and session contract

Status: proposed contract, 31 October 2026. No endpoints below are implemented. This document describes product decisions for review and subsequent OpenAPI authoring.

## Scope and boundaries

One fictional Program, email login via an established identity provider, linked AUTH_SESSION and CORE_SESSION, authorized own-profile access, logout, audit and error records. Every public endpoint below enters Gateway then Core. User Accounts owns provider integration, identity linkage, membership and AUTH_SESSION. Core owns Program/customer and CORE_SESSION. Each service authenticates internal callers and validates applicable Program scope.

Proposed browser model: same-origin frontend and Gateway, server-managed opaque session cookie, provider authorization-code flow with PKCE. Browser JavaScript receives neither access nor refresh tokens. Keycloak is selected. Its local realm/client runtime setup is not yet verified. Exact provider endpoints, claims, logout support and verification screens must be verified after selection.

## Public routes

All paths use `/api/v1`. JSON property names use camelCase; uppercase naming applies to the database. UUIDs are canonical strings. Date-times are UTC RFC 3339 strings. Successful protected responses use `Cache-Control: no-store`.

| Method and path | Contract | Responses |
| --- | --- | --- |
| GET /cor/programs/resolve?key=cjora-demo | Public minimal branding: `programUuid`, `key`, `displayName`. Approved Program keys only; no arbitrary host lookup. | 200; 404 unavailable; 429 |
| GET /usr/csrf | Issues pre-login/browser CSRF context and returns `{ csrfToken }`; no identity data. | 200; 503 |
| POST /usr/sessions | CSRF-protected `{ programKey, email, returnPath }`. Starts provider login; email is only a login hint. Returns `{ authorizationUrl }` for an approved provider. Validate bounded email and allowlisted relative return paths. | 200; 400 invalid format; 429; 503. Membership/account-existence failures must not be revealed here. |
| GET /usr/sessions/callback | Provider code/state callback through Gateway/Core to User Accounts. Verify one-use state, PKCE, nonce, issuer, audience, signature and expiry. Resolve identity from immutable issuer/subject; verified email policy applies to initial linkage only. | 303 to allowlisted app path after linked sessions are active; otherwise 303 to generic login error with opaque support reference. Never return tokens in the URL. |
| GET /cor/sessions/current | Requires active linked sessions. Returns `sessionUuid`, `programUuid`, `customerUuid`, `expiresAtUtc`. | 200; 401 invalid/expired/revoked; 403 membership/restriction denial; 503 unable to validate |
| GET /cor/customers/me | Requires active linked sessions and own-profile permission. Returns `customerUuid`, `programUuid`, `displayName`, `email`. Derive customer from the session; accept no customer override. | 200; 401; 403; 503 |
| DELETE /usr/sessions/current | CSRF-protected, idempotent current-browser logout. Revoke authentication session and linked Core sessions. Expired/absent cookie returns success without disclosing identity. | 204 when application-session revocation is durable; 503 when completion cannot be confirmed. Clear the cookie on either outcome. |

Internal service routes are separate contracts and cannot be exposed by wildcard forwarding. Gateway forwards business paths only to Core. User Accounts and Core must not call Gateway to reach one another.

Example own-profile response:

```json
{
  "customerUuid": "1f21d607-2b62-4b14-bcb4-d46cfed72834",
  "programUuid": "864199d1-aef8-45ef-a0db-a7db5a733b67",
  "displayName": "Alex Demo",
  "email": "alex@example.test"
}
```

## Browser and authorization rules

Proposed cookie name: `__Host-cjora-session`; Secure, HttpOnly, Path=/, no Domain, SameSite=Lax. Local browser authentication requires HTTPS. Store only a digest of the opaque session secret, and rotate after login. Proposed initial expiry: 15-minute idle and 8-hour absolute maximum; Core expiry cannot exceed AUTH_SESSION expiry. These durations require product review before implementation.

Require a session-bound CSRF token plus Origin validation for state-changing browser requests, including login and logout. The provider callback uses one-use state bound to the initiating browser transaction rather than the ordinary request CSRF token. Rate-limit login, callbacks and verification paths; never log email login hints, authorization URLs, codes, cookies, CSRF secrets or tokens.

Check current AUTH_SESSION status, Core session, Program enabled state, membership, customer linkage and permission before profile disclosure. If authoritative validation is unavailable, return a controlled 503 without profile data. Kafka revocation events are supplemental. Do not auto-link accounts by email or trust Program/customer UUIDs supplied by the browser. No Program-switching endpoint in this milestone.

## Cross-service session lifecycle

1. User Accounts validates provider authentication and active membership and persists a pending AUTH_SESSION with audit in its local transaction.
2. Core idempotently creates a pending CORE_SESSION using a stable operation UUID, authorized membership evidence and its own customer linkage. Audit in the Core transaction.
3. User Accounts activates AUTH_SESSION only for the expected pending operation. Core activates CORE_SESSION after authoritative confirmation. Issue the browser cookie only after both are confirmed active.
4. If any step fails, issue no usable cookie. Persist recovery state and reconcile/revoke pending or orphaned records. Retries use the same operation UUID with unique constraints, never duplicate sessions.
5. Logout durably revokes AUTH_SESSION first, immediately preventing protected access through authoritative checks. Revoke Core sessions idempotently with durable retry if Core is unavailable. Report 204 only once application revocation is durable and recovery work is recorded; provider global logout is a separate capability.

No distributed database transaction is assumed. A pending record is never sufficient for authorization. Recovery must handle a crash at each boundary, including after activation before cookie delivery.

## Errors and logging

Use `application/problem+json`: `type`, `title`, `status`, safe `detail`, `instance`, stable `code`, `correlationId`; optional field validation errors containing safe messages. Never include submitted credentials, raw exception messages or service URLs. Map malformed input to 400, expired/invalid sessions to 401, authenticated denial to 403, rate limits to 429 with Retry-After, dependency unavailability to 503.

Persist session creation/activation/revocation and security-relevant denials in API_LOG with operation/correlation UUIDs and approved field changes. Audit commits with each local business change. Record failures independently in ERROR_LOGS with redaction and durable fallback when the database is unavailable. No recursive auditing of log tables. Successful profile reads normally generate no API_LOG record.

## Schema impact and decisions required

Core: PROGRAM, CUSTOMER, CORE_SESSION, API_LOG, ERROR_LOGS and durable workflow/recovery state. User Accounts: USER_ACCOUNT, membership/permission tables, AUTH_SESSION, API_LOG, ERROR_LOGS and recovery state. Every table starts ID then UUID; use uppercase names, table/column comments, constraints, bounded fields, creation/update metadata where mutable, and an explicit MySQL concurrency strategy. Schema and migrations remain unimplemented.

Before schema/login implementation, settle: Keycloak client/realm configuration; verified email normalization and uniqueness; customer-to-Program cardinality; permission names; exact session expiry; recovery ownership and retention; HTTPS local ports. Proposed default cardinality is one customer per user per Program, with identity keyed by issuer/subject and membership unique by user/Program. This is a proposal, not a confirmed architecture decision.

## Required acceptance checks

- Successful authorized login creates exactly one linked active session pair and exposes only the session customer's profile.
- Unverified identity, forged/replayed callback, disabled Program, absent membership and cross-Program/customer substitutions are denied without data leakage.
- Missing/incorrect CSRF token, foreign Origin and unapproved return destination are rejected.
- Expiry, revoked membership and logout deny the next protected request; service failure cannot bypass validation.
- Repeated logout is safe; partial activation/revocation and crashes recover without usable orphan sessions.
- Every persisted session change has an atomic local audit; errors survive rolled-back work and redact secrets.
- Login-start behavior does not reveal account existence; successful ordinary profile reads are not unnecessarily audited.

## Later financial journey

Accounts, transfer creation/status and paginated history require separate Transactions contracts. Before implementing them, define currency, amount representation, ownership, chart of accounts, ledger invariants, idempotency scope/payload fingerprint, recovery and permission rules. The wireframes do not establish those contracts or authorize live payments.
