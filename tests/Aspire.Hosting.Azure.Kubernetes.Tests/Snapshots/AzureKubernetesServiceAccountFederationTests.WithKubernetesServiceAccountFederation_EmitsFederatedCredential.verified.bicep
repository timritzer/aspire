@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource myidentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('myidentity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource fedcred_my_namespace_my_workload 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  name: 'my-namespace-my-workload-fedcred'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: 'https://oidc.prod-aks.azure.com/11111111-2222-3333-4444-555555555555/'
    subject: 'system:serviceaccount:my-namespace:my-workload'
  }
  parent: myidentity
}

output id string = myidentity.id

output clientId string = myidentity.properties.clientId

output principalId string = myidentity.properties.principalId

output principalName string = myidentity.name

output name string = myidentity.name