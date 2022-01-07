Trying to deploy .net C# infra to Azure

**Disclaimer**: I have been at this for 1 day and have gone through few tutorials and read the docs so I'm sure I´m using Output wrong and probably other things. 


But I have these 3 errors that I can’t get over even though I´m doing the exact same thing as shown in this example [here](https://github.com/pulumi/examples/blob/master/classic-azure-cs-msi-keyvault-rbac/AppStack.cs)
<img width="1121" alt="image" src="https://user-images.githubusercontent.com/2386572/147911485-c7659d9b-21b2-49dd-a11f-b37250d0a892.png">

```xml
readposterblobpermission (azure:authorization:Assignment)
error: 1 error occurred:
	* loading Role Definition List: could not find role 'Poster Images Storage Blob Data Reader'
 ```
```xml
uploadrecordingblobpermission (azure:authorization:Assignment)
error: 1 error occurred:
	* loading Role Definition List: could not find role 'Uploaded Recordings Storage Blob Data Reader'
```

```xml
identityadmin (azure:sql:ActiveDirectoryAdministrator)
error: 1 error occurred:
	* A resource with the ID "/subscriptions/[subscription_id]/resourceGroups/beinni-rg42d3b5aa/providers/Microsoft.SQL/servers/mysqlserveree7348a/administrators/ActiveDirectory" already exists - to be managed via Terraform this resource needs to be imported into the State. Please see the resource documentation for "azurerm_sql_active_directory_administrator" for more information.
```
