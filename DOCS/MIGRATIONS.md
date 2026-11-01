# Service-owned migrations

All eight services now own independent executable migrations and history. Gateway owns its method registry database; this supersedes the earlier database-free Gateway plan.

| Service | Project | Database / environment prefix |
| --- | --- | --- |
| Gateway | SRC/GATEWAY/CJora.Gateway.Migrations | CJORA_GATEWAY |
| Core | SRC/CORE/CJora.Core.Migrations | CJORA_CORE |
| Identity Management | SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Migrations | CJORA_IDENTITY_MANAGEMENT |
| TPP | SRC/THIRD-PARTY-PROVIDER/CJora.Tpp.Migrations | CJORA_TPP |
| Payments | SRC/PAYMENTS/CJora.Payments.Migrations | CJORA_PAYMENTS |
| Transactions | SRC/TRANSACTIONS/CJora.Transactions.Migrations | CJORA_TRANSACTIONS |
| Notifications | SRC/NOTIFICATIONS/CJora.Notifications.Migrations | CJORA_NOTIFICATIONS |
| Documents | SRC/DOCUMENTS/CJora.Documents.Migrations | CJORA_DOCUMENTS |

Keycloak owns and migrates its own vendor schema. Do not impose CJora business conventions on it.

CJora.DatabaseMigrations shares technical execution only; each service owns immutable embedded SQL and its MIGRATION_HISTORY. Scripts use uppercase names, numeric ID first, UUID second and comments. Runtime APIs never apply migrations. A migrator uses an isolated account with only its service database privileges; runtime accounts have narrower table-specific grants.

Inspect without a database:

```powershell
dotnet run --project SRC/GATEWAY/CJora.Gateway.Migrations -- --list
```

Apply explicitly after configuring the service-specific environment variable, for example CJORA_GATEWAY_MIGRATION_CONNECTION pointing to CJORA_GATEWAY. Never put real credentials in command arguments, chat or tracked files.

```powershell
dotnet run --project SRC/GATEWAY/CJora.Gateway.Migrations -- --apply
```

The runner rejects another database name, takes a database-specific advisory lock, records SHA256 checksums and skips only matching APPLIED scripts. MySQL DDL implicitly commits: interrupted scripts may leave partial DDL plus STARTED history and require reviewed recovery. Never edit an applied script or blindly delete history/retry; add follow-up migrations and back up before production work.

## Current local database state

Core 0001-0004 and Gateway 0001-0002 are applied. Other services' scripts are authored but unapplied. Core 0003 historically created SERVICE_METHOD; it stays immutable. Core 0004 archives it as SERVICE_METHOD_ARCHIVE with audit records. Gateway 0001/0002 own its logs/live SERVICE_METHOD. Initialize-GatewayDatabase.ps1 performs the local operator-only audited import, preserves public UUIDs/metadata and regenerates local numeric IDs. Runtime accounts do not share database access. Original Core audit/history and archived registrations are retained.

```powershell
./SCRIPTS/Initialize-GatewayDatabase.ps1
```

The setup configures ignored local credentials/User Secrets and a Development-only authenticated route-context bridge. Re-running setup applies no duplicate scripts, imports no duplicate registrations/audits and never overwrites existing Gateway rows. Conflicting aliases fail. The old Core registry credential is revoked/locked and removed from runtime settings. Read DYNAMIC-GATEWAY.md for caller validation and production limitations.

Validated on local MySQL: initial Core/Gateway application, no-op replay, complete table/column comments, ID/UUID order, audited import and runtime account isolation. Full interruption/restore/concurrent-migration recovery drills remain future integration work.