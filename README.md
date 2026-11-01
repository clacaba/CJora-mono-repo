# CJora

Program-based financial platform foundation using .NET 8. Business authentication and financial features are not implemented.

- VS Code: open `CJora.code-workspace` to select CJora.sln explicitly.
- Build: `dotnet build CJora.sln`
- Unit tests (no Docker): `dotnet test CJora.sln -warnaserror`
- Core: `dotnet run --project SRC/CORE/CJora.Core.Api --launch-profile http` (localhost:5075)
- Gateway: `dotnet run --project SRC/GATEWAY/CJora.Gateway.Api` (HTTPS localhost:7000; local diagnostics localhost:5080)
- Identity Management: `dotnet run --project SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Api` (localhost:5076)
- Route check: `GET https://localhost:7000/core/health` returns Core's liveness response.

Gateway has only Core as an upstream. All eight APIs have WeatherForecast and Health controllers with local sample/liveness routes; login and business routes are not implemented. All listeners use loopback defaults. Local HTTP is scaffolding only; configure HTTPS and authenticated internal callers before enabling browser login.

See [dynamic Gateway and HTTPS](DOCS/DYNAMIC-GATEWAY.md), [unit tests](DOCS/TESTING.md), [all service endpoints](DOCS/SERVICE-ENDPOINTS.md), [local dependencies](DOCS/LOCAL-DEVELOPMENT.md), [migration ownership](DOCS/MIGRATIONS.md), and [progress](DOCS/PROGRESS.md).

Gateway owns CJORA_GATEWAY.SERVICE_METHOD and resolves /{service}/{method}. Core orchestrates the selected operation without reading that database. Initialize with SCRIPTS/Initialize-GatewayDatabase.ps1; see DOCS/DYNAMIC-GATEWAY.md for the local-only caller bridge and production security boundaries.
