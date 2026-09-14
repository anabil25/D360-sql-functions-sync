# D360 SQL Functions Sync

A minimal .NET 10 Azure Functions application for moving ordered SQL Server changes into Dataverse. Durable Functions and the Azure-managed Durable Task Scheduler (DTS) own orchestration history, retries, timers, and the per-partition checkpoint.

The repository is based on Microsoft's `durable-functions-quickstart-dotnet-azd` template. It retains the template's Flex Consumption, managed identity, monitoring, DTS, and optional virtual network deployment.

> [!IMPORTANT]
> The durable workflow is implemented and validated, but `SyncDataAccess` is currently a deterministic local simulator. Replace its read and upsert methods with SQL Server and Dataverse adapters before production use.

## How it works

Each partition has one orchestration instance and one Durable Entity checkpoint:

1. Read the committed `(ModifiedUtc, SourceKey)` cursor from the entity.
2. Capture a fixed upper timestamp for the source window.
3. Read one keyset-paginated page from SQL Server.
4. Upsert that page to Dataverse using an alternate key.
5. Advance the entity only after the upsert succeeds.
6. Use `ContinueAsNew` for the next page, keeping orchestration history bounded.

The cursor is monotonic, so replaying an already committed page cannot move it backward. A configured recurrence uses a durable timer and opens a new source window without an external scheduler.

See [the architecture diagram](docs/architecture/sql-to-dataverse-dts-architecture.png) for the solution layout.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local?pivots=programming-language-csharp#install-the-azure-functions-core-tools)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) for local DTS and Azurite
- [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) for Azure deployment

## Run locally

Start the official DTS emulator and Azurite:

```powershell
docker run --name dts-emulator -d -p 8080:8080 -p 8082:8082 mcr.microsoft.com/dts/dts-emulator:latest
docker run --name azurite -d -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite:latest
```

If the containers already exist, use `docker start dts-emulator azurite` instead.

In a second terminal, start the Function host:

```powershell
cd src
func start
```

In a third terminal, run a 1,000-row simulated sync:

```powershell
pwsh -NoProfile -File scripts/run-sync.ps1 -Partition demo -PageSize 100
```

The expected result is `Completed`, 10 pages, 1,000 rows, and cursor `ACCOUNT-00001000`. Open <http://localhost:8082> to inspect the instance in the DTS dashboard.

Local configuration lives in the gitignored `src/local.settings.json`. Start from `src/local.settings.example.json`; the simulator recognizes:

| Setting | Default | Purpose |
| --- | ---: | --- |
| `SIMULATED_ROW_COUNT` | `1000` | Number of deterministic source rows |
| `SIMULATED_WRITE_LATENCY_MS` | `25` | Delay for each simulated Dataverse request |

To start a recurring local run directly:

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:7071/api/sync/tax-account?pageSize=100&recurrenceSeconds=300'
```

## Configuration and secrets

Azure Functions does not use a project `.env` file by default:

- Local runtime values go in gitignored `src/local.settings.json`.
- Azure runtime values go in the Function App's application settings.
- Secret Azure application settings should be Key Vault references.
- The `.azure/<environment>/.env` file created by `azd` stores deployment environment values. It is gitignored, but it is not the application's long-term secret store.

The planned adapter settings are:

| Setting | Secret | Recommended Azure source |
| --- | --- | --- |
| `SQL_CONNECTION_STRING` | Usually | Omit when the adapter uses managed identity; otherwise use a Key Vault reference |
| `DATAVERSE_URL` | No | Function App setting |
| `DATAVERSE_TENANT_ID` | No | Function App setting |
| `DATAVERSE_CLIENT_ID` | No | Function App setting |
| `DATAVERSE_CLIENT_SECRET` | Yes | Omit for identity/certificate auth; otherwise use a Key Vault reference |

A Function App Key Vault reference has this shape:

```text
@Microsoft.KeyVault(VaultName=<vault-name>;SecretName=<secret-name>)
```

The Function's user-assigned managed identity needs the `Key Vault Secrets User` role on that vault. No dedicated vault is provisioned yet because the simulator has no secrets and the final SQL/Dataverse authentication choices are not known. Reuse an approved organizational vault or add a dedicated vault when implementing the real adapters.

## Add the real adapters

Replace the three method bodies in `src/SyncDataAccess.cs`; the durable contracts and orchestrator do not need to change.

- `GetWindowEndUtcAsync`: obtain the SQL Server source time, such as `SYSUTCDATETIME()`.
- `ReadPageAsync`: query after `(ModifiedUtc, SourceKey)`, at or before the captured window end, ordered by those same columns.
- `UpsertAsync`: map the page and call Dataverse `UpsertMultiple` using a configured alternate key.

Use managed identity in Azure where supported and keep credentials out of source and orchestration payloads. Activities may execute more than once, so Dataverse writes must remain idempotent.

## Deploy with azd

From the repository root, the easy path provisions and deploys everything:

```powershell
azd auth login
azd up
```

Choose an environment name, subscription, supported Azure region, and whether to enable the virtual network. The deployment creates:

- an Azure Functions Flex Consumption app;
- a Durable Task Scheduler and task hub;
- managed identity and required RBAC assignments;
- host storage and deployment container;
- Application Insights and Log Analytics;
- optional virtual network and private storage endpoint.

After provisioning, the `postprovision` hook writes `src/local.settings.json` with the remote DTS endpoint and task hub for authenticated local debugging. Use `azd deploy` for later code-only updates and `azd provision` for infrastructure changes.

To list the deployed HTTP endpoint and function key:

```powershell
func azure functionapp list-functions "$(azd env get-value AZURE_FUNCTION_NAME)" --show-keys
```

Send a `POST` request to the listed `StartSync` URL, appending a partition such as `/tax-account?pageSize=100`.

## Clean up

Remove the Azure resources when they are no longer needed:

```powershell
azd down
```

Stop the local emulators with `docker stop dts-emulator azurite`.