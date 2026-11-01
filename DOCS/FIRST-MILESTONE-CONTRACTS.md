# First milestone API contracts

The Development implementation and exact request/response sequence are documented in [LOGIN-MILESTONE.md](LOGIN-MILESTONE.md). All public API traffic uses HTTPS Gateway -> Core; Identity Management and Core internal session endpoints require independent trusted workload authentication.

Login-start body:

```json
{"programKey":"cjora-demo","email":"alex@example.test","returnPath":"/api/v1/cor/customers/me"}
```

GET /api/v1/idm/csrf first returns requestToken and sets an HttpOnly CSRF cookie. POST /api/v1/idm/sessions requires that cookie, X-CSRF-TOKEN and exact Origin https://localhost:7000. It returns authorizationUrl for a browser navigation to Keycloak. CJora never accepts the customer's password.

After the provider callback, the browser has a secure opaque application cookie and is redirected to its approved return path. GET /api/v1/cor/customers/me returns customerUuid, programUuid, displayName and email; it cannot select another customer. GET /api/v1/cor/sessions/current returns sessionUuid, customerUuid, programUuid and expiresAtUtc.

DELETE /api/v1/idm/sessions/current uses a newly fetched session-bound CSRF token and the exact Origin. Normal/idempotent logout returns 204. A remote revocation outage returns 503 with code REVOCATION_PENDING after local revocation has been queued durably and the browser cookie cleared.

Safe errors have code/reference or framework ProblemDetails. No raw tokens, password, exception text or downstream URLs are part of the business response. Ordinary profile reads are not audited. Session transitions are audited in each service's transaction; errors persist independently.

Customer synchronization is implemented. Fictional Development account/ledger/transfer/history backend is implemented; see FINANCIAL-BACKEND.md. Financial UI and production deployment remain future work. Production authentication, real email verification/MFA and global Keycloak logout remain separate work.
