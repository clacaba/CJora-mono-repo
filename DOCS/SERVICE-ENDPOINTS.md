# Local service endpoints

All eight APIs contain their own Controllers/WeatherForecastController.cs, Controllers/HealthController.cs and WeatherForecast.cs model. Weather data is fictional template data. Health is process liveness only and does not verify MySQL, Keycloak or Kafka.

| Service | Project under SRC | Local HTTP port |
| --- | --- | --- |
| Core | CORE/CJora.Core.Api | 5075 |
| Identity Management | IDENTITY-MANAGEMENT/CJora.IdentityManagement.Api | 5076 |
| TPP | THIRD-PARTY-PROVIDER/CJora.Tpp.Api | 5077 |
| Payments | PAYMENTS/CJora.Payments.Api | 5078 |
| Transactions | TRANSACTIONS/CJora.Transactions.Api | 5079 |
| Gateway | GATEWAY/CJora.Gateway.Api | 7000 HTTPS; 5080 local HTTP diagnostics |
| Notifications | NOTIFICATIONS/CJora.Notifications.Api | 5081 |
| Documents | DOCUMENTS/CJora.Documents.Api | 5082 |

For each running service:

- `GET /WeatherForecast`: five fictional forecasts with date, temperatureC, temperatureF and summary.
- `GET /health` or `GET /health/live`: local service identity and alive status.
- `/swagger`: Swagger UI in Development only.
- `/swagger/v1/swagger.json`: OpenAPI document in Development only.

Non-Gateway APIs also expose `/api/v1/{serviceCode}/health` and `/api/v1/{serviceCode}/health/live`, using cor, idm, tpp, pay, txn, ntc and doc. Gateway has only its unversioned local diagnostic routes; `/api/v1/**` still forwards only to Core. Other service health aliases do not become public routes through Gateway automatically.

Example:

```powershell
dotnet run --project SRC/CORE/CJora.Core.Api --launch-profile http
```

Open http://localhost:5075/swagger. For other APIs, substitute their project and port. Each API has an HTTP development launch profile and loopback listener. These are local scaffolding settings; HTTPS ingress, workload authentication and restricted deployment exposure still need implementation. Do not expose the sample services on a public network.

Validation: `dotnet build CJora.sln -warnaserror` and `SCRIPTS/Test-Foundation.ps1`. The smoke script starts all eight APIs, checks health, sample payloads and Swagger, verifies Gateway-to-Core routing, then stops only the processes it started. It refuses to run if the configured ports are already occupied.

The five newly added API projects are controller/Swagger shells. Their existing migration projects remain independent; application/domain/infrastructure layers will be added when those services implement business features. No login or money-changing API was added.

Gateway now exposes registered /{service}/{method} routes over HTTPS with Gateway-owned database resolution; see DYNAMIC-GATEWAY.md. The Core Development echo and Documents Development upload-check are non-persisting transport diagnostics. Core migrations 0001-0004 and Gateway migrations 0001-0002 have been applied locally; remaining service migrations are unapplied.
