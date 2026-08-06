# Deployment Instructions

## What The Deployment Script Does

1. Loads environment variables from the specified .env file
2. Creates or updates Azure resources:
   - Resource Group
   - Azure Container Registry
   - App Service Plan
   - Web App
   - Application Insights
   - Log Analytics Workspace
3. Builds the Docker image locally using the specified Dockerfile and context
4. Pushes the image to Azure Container Registry
5. Configures the Web App with all environment variables

## Build and Test Locally with Docker

From the root directory, execute the following commands:

```bash
docker build -t sonic-drive-in-app -f ./app/Dockerfile ./app
docker run -p 8000:8000 --env-file ./app/backend/.env sonic-drive-in-app:latest
```

## Deploy the Application

After testing locally, deploy the application with:

```bash
./scripts/deploy.sh \
    --env-file ./app/backend/.env \
    --dockerfile ./app/Dockerfile \
    --context ./app \
    sonic-drive-in-assistant
```

## Enable Entra ID Authentication (EasyAuth)

Authentication is **opt-in** — a plain `azd up` deploys without auth. To protect
the app with Entra ID (single-tenant), follow these one-time steps.

### 1. Create the App Registration

```bash
# Set your tenant ID (the Azure AD tenant that owns the app)
TENANT_ID="<your-tenant-id>"

# Create the app registration (single tenant)
az ad app create \
  --display-name "Sonic AI Drive-Thru Demo" \
  --sign-in-audience AzureADMyOrg \
  --enable-id-token-issuance true \
  --web-redirect-uris "https://<YOUR-CONTAINER-APP-FQDN>/.auth/login/aad/callback" \
  --query appId -o tsv
```

Save the returned `appId` (e.g., `<your-app-id>`).

> **Note:** Replace `<YOUR-CONTAINER-APP-FQDN>` with the actual FQDN from the
> `BACKEND_URI` output of your deployment (minus the `https://` prefix).

### 2. Create a Service Principal and Restrict Access

```bash
APP_ID="<your-app-id>"

# Create the service principal
az ad sp create --id $APP_ID

# Restrict sign-in to explicitly assigned users/groups only
az ad sp update --id $APP_ID --set appRoleAssignmentRequired=true
```

> **`appRoleAssignmentRequired=true`** means only users explicitly assigned to
> this enterprise application can sign in. Without it, *any* member of the
> tenant can authenticate. Assign users in the Azure Portal under
> Enterprise Applications → Sonic AI Drive-Thru Demo → Users and groups,
> or via CLI:
>
> ```bash
> # Get the service principal object ID
> SP_OBJECT_ID=$(az ad sp show --id $APP_ID --query id -o tsv)
>
> # Get the user's object ID
> USER_OBJECT_ID=$(az ad user show --id user@contoso.com --query id -o tsv)
>
> # Assign the user (default app role — empty GUID)
> az rest --method POST \
>   --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP_OBJECT_ID/appRoleAssignments" \
>   --body "{\"principalId\": \"$USER_OBJECT_ID\", \"resourceId\": \"$SP_OBJECT_ID\", \"appRoleId\": \"00000000-0000-0000-0000-000000000000\"}"
> ```

### 3. Create a Client Secret

```bash
az ad app credential reset --id $APP_ID --display-name "azd-easyauth" --query password -o tsv
```

Save the secret value — it is shown only once.

### 4. Configure the azd Environment

```bash
azd env set AZURE_AUTH_ENABLED true
azd env set AZURE_AUTH_CLIENT_ID "<your-app-id>"
azd env set AZURE_AUTH_TENANT_ID "<your-tenant-id>"
azd env set AZURE_AUTH_CLIENT_SECRET "<secret-value-from-step-3>"
```

Then deploy normally:

```bash
azd up
```

The Bicep template will:
- Store `AZURE_AUTH_CLIENT_SECRET` as a Container App secret named `aad-client-secret`
- Deploy an `authConfigs/current` child resource with EasyAuth enabled
- Redirect unauthenticated requests to Entra ID login
- Protect all endpoints including WebSocket routes (`/realtime`)

### 5. Verify

```bash
# Anonymous GET should 302 redirect to Entra login
curl -s -o /dev/null -w "%{http_code}" https://<YOUR-CONTAINER-APP-FQDN>/

# Anonymous WebSocket handshake should return 401
curl -s -o /dev/null -w "%{http_code}" \
  -H "Upgrade: websocket" -H "Connection: Upgrade" \
  https://<YOUR-CONTAINER-APP-FQDN>/realtime
```

### Secret Management

The client secret **never** appears in source control or parameter files.
It flows through `azd env` (stored locally in `.azure/<env>/.env`, which is
gitignored) and is passed as a `@secure()` Bicep parameter at deployment time.

If you prefer to manage the secret entirely out-of-band (without putting it in
`azd env`), you can set it directly on the Container App after deployment:

```bash
az containerapp secret set \
  --name <container-app-name> \
  --resource-group <rg-name> \
  --secrets aad-client-secret=<secret-value>
```

In that case, leave `AZURE_AUTH_CLIENT_SECRET` empty — the Bicep template will
still deploy the auth config referencing the secret by name.
