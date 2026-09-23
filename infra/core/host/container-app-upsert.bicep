metadata description = 'Creates or updates an existing Azure Container App.'
param name string
param location string = resourceGroup().location
param tags object = {}


@description('The number of CPU cores allocated to a single container instance, e.g., 0.5')
param containerCpuCoreCount string = '0.5'

@description('The maximum number of replicas to run. Must be at least 1.')
@minValue(1)
param containerMaxReplicas int = 10

@description('The amount of memory allocated to a single container instance, e.g., 1Gi')
param containerMemory string = '1.0Gi'

@description('The minimum number of replicas to run. Must be at least 1.')
@minValue(1)
param containerMinReplicas int = 1

@description('The name of the container')
param containerName string = 'main'

@description('The environment name for the container apps')
param containerAppsEnvironmentName string = '${containerName}env'

@description('The name of the container registry')
param containerRegistryName string

@description('Hostname suffix for container registry. Set when deploying to sovereign clouds')
param containerRegistryHostSuffix string = 'azurecr.io'

@allowed(['http', 'grpc'])
@description('The protocol used by Dapr to connect to the app, e.g., HTTP or gRPC')
param daprAppProtocol string = 'http'

@description('Enable or disable Dapr for the container app')
param daprEnabled bool = false

@description('The Dapr app ID')
param daprAppId string = containerName

@description('Specifies if the resource already exists')
param exists bool = false

@description('Specifies if Ingress is enabled for the container app')
param ingressEnabled bool = true

@description('The type of identity for the resource')
@allowed(['None', 'SystemAssigned', 'UserAssigned'])
param identityType string = 'None'

@description('The name of the user-assigned identity')
param identityName string = ''

@description('The name of the container image')
param imageName string = ''

@description('The secrets required for the container')
@secure()
param secrets object = {}

@description('The keyvault identities required for the container')
@secure()
param keyvaultIdentities object = {}

@description('The environment variables for the container in key value pairs')
param env object = {}

@description('Environment variables sourced from Container App secrets: { ENV_VAR_NAME: \'secret-name\' }')
#disable-next-line secure-secrets-in-params // env var name -> secret NAME, never a secret value
param secretEnv object = {}

@description('Names of secrets that already exist on the app (e.g. set out-of-band with `az containerapp secret set`) and must survive this deployment when `secrets` does not supply them.')
param preserveExistingSecretNames array = []

@description('Specifies if the resource ingress is exposed externally')
param external bool = true

@description('The service binds associated with the container')
param serviceBinds array = []

@description('The target port for the container')
param targetPort int = 80

@description('Health probe path for liveness and readiness checks')
param healthProbePath string = ''

@description('Enable WebSocket transport for the container app ingress')
param enableWebSocket bool = false

@description('Ingress session affinity: none | sticky (sticky requires single revision mode)')
@allowed(['none', 'sticky'])
param stickySessionsAffinity string = 'none'

@allowed(['Consumption', 'D4', 'D8', 'D16', 'D32', 'E4', 'E8', 'E16', 'E32', 'NC24-A100', 'NC48-A100', 'NC96-A100'])
param workloadProfile string = 'Consumption'

param allowedOrigins array = []

resource existingApp 'Microsoft.App/containerApps@2023-05-02-preview' existing = if (exists) {
  name: name
}

module app 'container-app.bicep' = {
  name: '${deployment().name}-update'
  params: {
    name: name
    workloadProfile: workloadProfile
    location: location
    tags: tags
    identityType: identityType
    identityName: identityName
    ingressEnabled: ingressEnabled
    containerName: containerName
    containerAppsEnvironmentName: containerAppsEnvironmentName
    containerRegistryName: containerRegistryName
    containerRegistryHostSuffix: containerRegistryHostSuffix
    containerCpuCoreCount: containerCpuCoreCount
    containerMemory: containerMemory
    containerMinReplicas: containerMinReplicas
    containerMaxReplicas: containerMaxReplicas
    daprEnabled: daprEnabled
    daprAppId: daprAppId
    daprAppProtocol: daprAppProtocol
    // ARM evaluates only the taken branch of the ternary, so listSecrets() never
    // runs against an app that doesn't exist yet.
    secrets: union(
      exists && !empty(preserveExistingSecretNames)
        ? toObject(
            #disable-next-line BCP422
            filter(existingApp.listSecrets().value, s => contains(preserveExistingSecretNames, s.name) && !contains(secrets, s.name)),
            s => s.name,
            s => s.value)
        : {},
      secrets)
    keyvaultIdentities: keyvaultIdentities
    allowedOrigins: allowedOrigins
    external: external
    env: concat(
      map(objectKeys(env), key => { name: key, value: '${env[key]}' }),
      map(objectKeys(secretEnv), key => { name: key, secretRef: secretEnv[key] }))
    imageName: !empty(imageName) ? imageName : exists ? existingApp.properties.template.containers[0].image : ''
    targetPort: targetPort
    healthProbePath: healthProbePath
    enableWebSocket: enableWebSocket
    stickySessionsAffinity: stickySessionsAffinity
    serviceBinds: serviceBinds
  }
}

output defaultDomain string = app.outputs.defaultDomain
output imageName string = app.outputs.imageName
output name string = app.outputs.name
output uri string = app.outputs.uri
output id string = app.outputs.id
output identityPrincipalId string = app.outputs.identityPrincipalId
