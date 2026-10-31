# Service-owned migrations

Every database-owning service has an independent executable migration project and embedded immutable SQL scripts:

| Service | Project | Database / environment prefix |
| --- | --- | --- |
| Core | SRC/CORE/CJora.Core.Migrations | CJORA_CORE |
| User Accounts | SRC/USER-ACCOUNTS/CJora.UserAccounts.Migrations | CJORA_USER_ACCOUNTS |
| TPP | SRC/THIRD-PARTY-PROVIDER/CJora.Tpp.Migrations | CJORA_TPP |
| Payments | SRC/PAYMENTS/CJora.Payments.Migrations | CJORA_PAYMENTS |
| Transactions | SRC/TRANSACTIONS/CJora.Transactions.Migrations | CJORA_TRANSACTIONS |
| Notifications | SRC/NOTIFICATIONS/CJora.Notifications.Migrations | CJORA_NOTIFICATIONS |
| Documents | SRC/DOCUMENTS/CJora.Documents.Migrations | CJORA_DOCUMENTS |

Gateway has no business database. Keycloak owns and migrates its own internal schema; CJora must not edit it or impose CJora business table conventions on vendor tables.

`CJora.DatabaseMigrations` shares only technical execution code. SQL and history belong to each service. This foundation uses explicit SQL scripts through MySqlConnector so uppercase names, column order and comments are reviewable. EF Core mappings can be introduced for business persistence separately; do not create a second competing migration history.

Inspect without a database:

```powershell
dotnet run --project SRC/CORE/CJora.Core.Migrations -- --list
```

A privileged operator provisions the service database and a dedicated migration account first. Set `CJORA_CORE_MIGRATION_CONNECTION` locally through a secure environment mechanism, pointing to database `CJORA_CORE`; do not put credentials in command arguments, chat or tracked files. Replace the prefix for other services. Use a distinct runtime account with no DDL rights and no other service database access.

```powershell
dotnet run --project SRC/CORE/CJora.Core.Migrations -- --apply
```

API startup never applies migrations. The runner rejects a mismatched database name, acquires a database-specific advisory lock, records SHA256 checksums and skips only matching APPLIED scripts. History starts ID then UUID and documents every column. The first owned script creates API_LOG and ERROR_LOGS; this is schema only, not implemented logging behavior.

MySQL DDL implicitly commits. Scripts are forward-only: a failure can leave partial DDL and a STARTED history row. Inspect the actual schema and original script securely, restore a disposable database or perform a reviewed repair, then reconcile history only after proving the schema matches. Never blindly delete history/retry or edit an applied script. Add numbered follow-up scripts. Back up before production changes. No production execution is authorized by these instructions.

Database integration remains unverified until MySQL is available: test first application, second no-op, changed-script refusal, concurrent-run locking, wrong-database refusal and interruption recovery before relying on this runner.