@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource myidentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('myidentity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource fedcred_nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn_sssssssssssssssssssssssssssssssssssssss_3ed3126ac8d85f8f 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  name: 'nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn-sssssssssssssssssssssssssssssssssssssss-3ed3126ac8d85f8f'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: 'https://oidc.example.com/cluster/'
    subject: 'system:serviceaccount:nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn:ssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssssss'
  }
  parent: myidentity
}

output id string = myidentity.id

output clientId string = myidentity.properties.clientId

output principalId string = myidentity.properties.principalId

output principalName string = myidentity.name

output name string = myidentity.name