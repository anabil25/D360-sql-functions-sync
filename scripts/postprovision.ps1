# This script generates the local.settings.json file for local development
# using the provisioned Azure resources (DTS endpoint and task hub).

Write-Host "Generating local.settings.json for local development..."

if ([string]::IsNullOrEmpty($env:DTS_ENDPOINT) -or [string]::IsNullOrEmpty($env:TASKHUB_NAME)) {
    Write-Error "DTS_ENDPOINT and TASKHUB_NAME environment variables must be set."
    Write-Error "Run 'azd provision' first to set these values."
    exit 1
}

$settings = @{
    IsEncrypted = $false
    Values = @{
        AzureWebJobsStorage = "UseDevelopmentStorage=true"
        FUNCTIONS_WORKER_RUNTIME = "dotnet-isolated"
        DURABLE_TASK_SCHEDULER_CONNECTION_STRING = "Endpoint=$($env:DTS_ENDPOINT);Authentication=DefaultAzure"
        TASKHUB_NAME = $env:TASKHUB_NAME
    }
}

$settingsJson = $settings | ConvertTo-Json -Depth 10
$settingsJson | Set-Content -Path "./src/local.settings.json"

Write-Host "local.settings.json generated successfully at ./src/local.settings.json"
