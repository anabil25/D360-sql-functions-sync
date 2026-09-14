param(
    [string] $Partition = 'tax-account',
    [int]    $PageSize  = 100,
    [int]    $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

$start = Invoke-RestMethod -Method Post -Uri "http://localhost:7071/api/sync/$Partition`?pageSize=$PageSize"
Write-Host "Instance : $($start.id)"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $status = Invoke-RestMethod -Uri $start.statusQueryGetUri
} while ($status.runtimeStatus -in @('Pending', 'Running') -and (Get-Date) -lt $deadline)

Write-Host "Status   : $($status.runtimeStatus)"
Write-Host "Output   : $($status.output | ConvertTo-Json -Depth 5 -Compress)"

if ($status.runtimeStatus -ne 'Completed') {
    Write-Host "Failure  : $($status.output)"
    exit 1
}
