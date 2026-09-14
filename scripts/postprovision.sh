#!/bin/bash

# This script generates the local.settings.json file for local development
# using the provisioned Azure resources (DTS endpoint and task hub).

echo "Generating local.settings.json for local development..."

if [ -z "$DTS_ENDPOINT" ] || [ -z "$TASKHUB_NAME" ]; then
    echo "Error: DTS_ENDPOINT and TASKHUB_NAME environment variables must be set."
    echo "Run 'azd provision' first to set these values."
    exit 1
fi

cat <<EOF > ./src/local.settings.json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "DURABLE_TASK_SCHEDULER_CONNECTION_STRING": "Endpoint=${DTS_ENDPOINT};Authentication=DefaultAzure",
        "TASKHUB_NAME": "${TASKHUB_NAME}"
    }
}
EOF

echo "local.settings.json generated successfully at ./src/local.settings.json"
