# Customer synchronization

Core publishes minimal customer snapshots to local Kafka. Transactions stores a customer reference using the **same customer UUID**. No accounts, balances, ledger entries, transfers or real payments are created by this milestone.

## Local setup

Start from the initialized login milestone and run from the repository root:

```powershell
./SCRIPTS/Initialize-CustomerSync.ps1
dotnet build CJora.sln -warnaserror
./SCRIPTS/Test-CustomerSync.ps1
```

Initializer applies Core migration 0006 and Transactions 0001�0002; creates database-scoped migration accounts, restricted runtime accounts and a separate local customer-operator account; writes Core/Transactions User Secrets; starts Kafka and creates its topic; reconciles existing Core customers into the outbox without changing their version or emitting duplicate customer/version snapshots. Re-running is safe.

Kafka is apache/kafka:4.1.1 in KRaft mode, with a persistent cjora-local_kafka-data volume. Host listener is bound to 127.0.0.1:9092; Docker clients use kafka:19092. Topic cjora.core.customer-snapshots.v1 has 3 partitions, replication 1, seven-day retention and 16 KiB message limit. Automatic topic creation is disabled. This is a single-node, unauthenticated, local Development broker; production requires separate deployment, TLS, client authentication/ACLs, appropriate replication, retention and operations.

Run Core and Transactions APIs using their existing launch profiles to activate the workers. CustomerSync:Enabled defaults off and is configured in local User Secrets by initialization; enabling this implementation outside Development fails startup. Liveness endpoints remain process checks rather than proof that Kafka/projections are ready.

## Writes and event contract

The reusable Core CustomerWriter performs customer creation/update, version increment, business audit and outbox insertion in **one MySQL transaction**. A local operator tool exposes it without introducing an unauthenticated customer administration HTTP endpoint. Credentials stay in ignored .env and are passed via environment variables, never command arguments.

Create a local JSON command file with these fields; use actual existing Program and user UUIDs and a new customer/operation UUID:

```json
{
  "customerUuid": "11111111-1111-4111-8111-111111111111",
  "programUuid": "c6a8a500-0000-4000-8000-000000000001",
  "userUuid": "22222222-2222-4222-8222-222222222222",
  "displayName": "Fictional Customer",
  "email": "customer@example.test",
  "status": "ACTIVE",
  "expectedVersion": 0,
  "operationUuid": "33333333-3333-4333-8333-333333333333"
}
```

```powershell
./SCRIPTS/Invoke-CustomerChange.ps1 -InputFile .local/customer-command.json
./SCRIPTS/Invoke-CustomerChange.ps1 -Reconcile
```

Operator failures are recorded independently in Core ERROR_LOGS with a safe local fallback; raw input and native errors are excluded.

ExpectedVersion is 0 for creation, otherwise the current Core customer ROW_VERSION. Reuse the identical command/operation UUID after an uncertain response. An operation with changed input is rejected; concurrent version conflicts require rereading state and a new intentional command. Program/user scope cannot be reassigned for an existing customer. This tool provisions a customer record, not a user account or membership.

All new application customer writes must use this writer. Existing local seed/operator SQL predates this path; run reconciliation after legacy/import seed writes. Direct SQL updates bypass the transactional outbox and are not a supported application write path. Reconciliation queues missing current versions only, not a full historical replay or a replacement for protected writes.

A schema-version-one full snapshot includes event/operation UUIDs, event type core.customer.snapshot.v1, customer/Program/user UUIDs, source version, ACTIVE/RESTRICTED status and UTC occurrence time. Kafka key is the canonical lowercase customer UUID. The serialized snapshot also includes its derived customer key. No display name, email, password, session cookie or provider token is sent. Schema, key, UUID, size, version and status are validated before projection.

Transactions owns its Application/Domain/Infrastructure layers and CUSTOMER_REFERENCE, CUSTOMER_EVENT_INBOX, CUSTOMER_EVENT_REJECTIONS, API_LOG and ERROR_LOGS. It never reads Core's database. The customer reference is a read model; **it does not grant Program access or authorize financial actions**.

## Delivery and recovery

The publisher uses idempotent Kafka production and all-broker acknowledgements, but MySQL and Kafka are separate systems, so delivery is at least once. It leases a due outbox row for 30 seconds, publishes and marks PUBLISHED only after broker acknowledgement. A crash between acknowledgement and marking can produce a duplicate. Only the current lease owner can acknowledge; expired leases are recoverable. Failed deliveries retry after five seconds, up to ten claims, then enter MANUAL_REVIEW. Broker outages do not roll back already committed customer changes.

The consumer disables automatic offset storage/commit. It commits the Kafka offset only after the projection/inbox/audit transaction or permanent rejection transaction commits. A transient failure closes/recreates the consumer without acknowledging the failed record. Replay is safe:

- Same event UUID and canonical payload: no additional effect.
- Older source version or equivalent current snapshot: durable STALE inbox entry, no business audit noise.
- Newer full snapshot: update projection and audit atomically.
- Same event UUID with changed content, equal-version conflict or immutable scope change: quarantine.
- Unsupported, malformed, oversized or wrongly keyed event: quarantine.

Rejections persist topic/partition/offset, safe error code, SHA256 fingerprint and MANUAL_REVIEW state. Raw rejected bytes are not stored or logged. The rejection audit and error record commit before acknowledging, allowing later valid events to proceed. Review the original Kafka record using restricted operator tools if needed while retention permits; no public payload viewer or automatic poison-message replay endpoint is implemented.

Operators must monitor pending age, expired leases, attempts, MANUAL_REVIEW rows, consumer lag and safe errors. After correcting a delivery problem, an authorized Core operator can transactionally reset a specific outbox row to PENDING, clear its lease, reset attempts, set NEXT_ATTEMPT_DATETIME to UTC now and append an API_LOG recovery audit, preserving its UUID/payload. No unauthenticated retry endpoint is provided. Quarantined data requires investigation; do not acknowledge an incompatible schema as an applied customer update.

Do not delete volumes or reset consumer offsets casually. Restoring an empty Transactions database after Kafka retention expires requires an explicit snapshot republish plan; ordinary reconciliation skips already-recorded customer/version outbox rows. Retention/purge policy, dashboards and production recovery automation remain future operations work.

## Validation

dotnet test CJora.sln runs Docker-independent command, contract and projection rule tests. SCRIPTS/Test-SchemaConventions.ps1 checks ID/UUID order, comments and local database boundaries.

SCRIPTS/Test-CustomerSync.ps1 uses real local MySQL and Kafka and retained fictional fixture customers. It starts its own publisher/consumer instances, not API listeners. Core and Transactions worker runtime connections remain separate; test inspection uses each database's own migrator credentials. It checks transactional rollback, command replay/conflicts, creation/update, database-failure replay, duplicates, stale ordering, conflicting identities, durable poison-message quarantine, subsequent valid messages, expired leases and duplicate publication. It temporarily stops/restarts only cjora-local-kafka-1 for broker recovery, restoring the broker in finally. Avoid running it while another local Kafka exercise relies on uninterrupted availability.

Health/WeatherForecast/Swagger, dynamic Gateway uploads/routing and login remain separate regression scripts. Secrets, raw command bodies and native exception messages are suppressed from test output.

## Next financial work

Define account ownership, currency and integer minor-unit amounts, double-entry ledger invariants, transfer permissions, idempotency and recovery contracts before adding accounts or transfers. Customer synchronization alone is insufficient authorization.

Implementation guidance: [Apache Kafka Docker example](https://github.com/apache/kafka/blob/trunk/docker/examples/docker-compose-files/single-node/plaintext/docker-compose.yml), [Confluent .NET client](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html).
