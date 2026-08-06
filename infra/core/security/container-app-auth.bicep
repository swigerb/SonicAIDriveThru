metadata description = 'Configures Entra ID (Azure AD) authentication on an Azure Container App via EasyAuth.'

@description('Name of the existing Container App to protect')
param containerAppName string

@description('Entra ID application (client) ID')
param clientId string

@description('Entra ID tenant ID for the issuer URL')
param tenantId string

@description('Name of the Container App secret holding the client secret. Must be provisioned out-of-band.')
#disable-next-line secure-secrets-in-params // This is a secret *name*, not a secret value
param clientSecretSettingName string = 'aad-client-secret'

resource containerApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: containerAppName
}

resource authConfig 'Microsoft.App/containerApps/authConfigs@2024-03-01' = {
  name: 'current'
  parent: containerApp
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
    }
    identityProviders: {
      azureActiveDirectory: {
        registration: {
          clientId: clientId
          clientSecretSettingName: clientSecretSettingName
          openIdIssuer: 'https://login.microsoftonline.com/${tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${clientId}'
          ]
        }
      }
    }
  }
}
