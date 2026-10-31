# CJora

Program-based financial platform foundation using .NET 8. Business authentication and financial features are not implemented.

- Build: `dotnet build CJora.sln`
- Core: `dotnet run --project SRC/CORE/CJora.Core.Api --launch-profile http` (localhost:5075)
- Gateway: `dotnet run --project SRC/GATEWAY/CJora.Gateway.Api` (localhost:5080)
- User Accounts: `dotnet run --project SRC/USER-ACCOUNTS/CJora.UserAccounts.Api` (localhost:5076)
- Route check: `GET http://localhost:5080/api/v1/cor/health/live` returns Core's liveness response.

Gateway has only Core as an upstream. User Accounts exposes local liveness only, no login or unprotected business routes. All listeners use loopback defaults. Local HTTP is scaffolding only; configure HTTPS and authenticated internal callers before enabling browser login.

See [local dependencies](DOCS/LOCAL-DEVELOPMENT.md), [migration ownership](DOCS/MIGRATIONS.md), and [progress](DOCS/PROGRESS.md).