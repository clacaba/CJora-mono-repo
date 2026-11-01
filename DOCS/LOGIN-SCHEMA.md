# Login milestone schema

Status: authored and embedded, not applied to MySQL. Docker integration is deferred by request. No login, profile or session authorization endpoint is enabled by these schema files.

## Ownership

| Database | Tables added in migration 0002 |
| --- | --- |
| CJORA_CORE | PROGRAM, CUSTOMER, CORE_SESSION |
| CJORA_IDENTITY_MANAGEMENT | USER_ACCOUNT, MEMBERSHIP, MEMBERSHIP_PERMISSION, AUTH_SESSION, SESSION_WORKFLOW |

Both services already own separate 0001 scripts for API_LOG and ERROR_LOGS. The other five database-owning services retain their initial logging scripts. Keycloak remains responsible for its own schema and migrations.

## Initial model

One Core customer and one Identity Management membership per user per Program. A user may belong to multiple Programs, but each authentication/application session pair has exactly one Program. No Property entity, automatic registration, seeded customer, password or grant is added.

USER_ACCOUNT.UUID is the cross-service user identity. Provider linkage is unique by exact configured issuer and opaque subject; email is not unique or used for automatic linking. Email verification is rechecked at login. Email normalization and any email uniqueness policy require a separate decision before provisioning real users. Core owns its customer contact profile; verified identity email updates must not silently overwrite it.

CUSTOMER.UUID will be the same UUID used by the future Transactions projection. Numeric IDs are local only. Identity Management stores PROGRAM_UUID without a foreign key to Core; Core stores USER_UUID and AUTH_SESSION_UUID without foreign keys to Identity Management. Each service authenticates the caller and resolves authoritative scope in application code.

Core uses a composite customer key to bind CORE_SESSION.CUSTOMER_ID, PROGRAM_ID and USER_UUID. Identity Management uses a composite membership key to bind AUTH_SESSION.MEMBERSHIP_ID, PROGRAM_UUID and USER_ACCOUNT_ID. Local integrity prevents mismatching those fields; it does not prove the membership/Program is currently enabled or replace service authorization.

All records begin ID then UUID, use uppercase names and documented columns, and include creation/update actors, UTC timestamps and ROW_VERSION. UUIDs must be parsed and emitted in canonical lowercase D format by application code. Contact text uses utf8mb4; external subject and UUID comparisons are case sensitive. Each update must compare the expected ROW_VERSION and increment it explicitly; MySQL does not supply SQL Server rowversion behavior.

## Sessions and recovery

PENDING records grant no access. ACTIVE requires activation metadata; Core additionally requires the SHA256 digest of a random high-entropy browser session secret, and Identity Management requires an acknowledged Core session UUID. The raw cookie, passwords, access/refresh tokens, authorization codes and OTPs have no columns here. Secure temporary OIDC transaction state and encrypted server-side token storage, if required, are a later implementation concern.

Unique OPERATION_UUID values prevent duplicate local session creation for the same login workflow. A unique AUTH_SESSION_UUID on Core and unique CORE_SESSION_UUID on Identity Management enforce one linked pair for this first milestone. Program switching is out of scope.

SESSION_WORKFLOW supports durable ACTIVATE/REVOKE coordination with a unique action per authentication session, bounded retry policy implemented by the future worker, and lease owner/expiry fields. It contains identifiers and safe error codes only. Creating recovery work must commit with the corresponding session change and audit; workers require compare-and-swap/row locking, lease expiry recovery and idempotent downstream operations. No worker exists yet.

The database checks valid state labels and selected timestamp/presence invariants. The application must still enforce legal state transitions, fresh membership/permission/Program checks, idle timeout, no Core expiry beyond AUTH_SESSION, definitive revocation, cookie rotation, CSRF and provider validation. Session duration policy remains proposed, not hardcoded into SQL.

Permission grants are scoped to membership, initially using implemented permission `CUSTOMERS.PROFILE.READ` for the own-profile journey. New records default to disabled/suspended/restricted or pending. A stored permission string alone cannot create an executable operation. Roles can be added through later migrations when the administrative model is defined.

Business writes and API_LOG must commit in one local transaction. ERROR_LOGS persists independently from failed business work. These scripts provide schema only; no runtime audit/error writer or data access is implemented yet.

## Validation and later integration

Run `SCRIPTS/Test-SchemaConventions.ps1` for static table/column conventions and migration `--list` to confirm embedded ordering. Neither command executes SQL or proves runtime isolation.

When Docker integration resumes, provision isolated databases and accounts, apply both scripts to fresh MySQL 8.4 databases, apply again to verify no-op behavior, and inspect INFORMATION_SCHEMA comments, ordinal positions, indexes and constraints. Attempt duplicate user/Program memberships, mismatched session scope, invalid ACTIVE/revocation/timestamp states and stale ROW_VERSION updates. Exercise changed-script refusal, interruption recovery, concurrent migrations and real service authorization before enabling login. No schema has been tested against a running database in this step.

Reference: https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html and https://dev.mysql.com/doc/refman/8.4/en/create-table-check-constraints.html .