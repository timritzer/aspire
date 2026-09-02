@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param oidc_issuer_url string

resource myidentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('myidentity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource fedcred_my_namespace_my_workload_fedcred 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  name: 'my-namespace-my-workload-fedcred'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: oidc_issuer_url
    subject: 'system:serviceaccount:my-namespace:my-workload'
  }
  parent: myidentity
}

output id string = myidentity.id

output clientId string = myidentity.properties.clientId

output principalId string = myidentity.properties.principalId

output principalName string = myidentity.name

output name string = myidentity.name