using Pulumi;
using Pulumi.Azure.AppService;
using Pulumi.Azure.AppService.Inputs;
using Pulumi.Azure.Authorization;
using Pulumi.Azure.Core;
using Pulumi.Azure.KeyVault;
using Pulumi.Azure.KeyVault.Inputs;
using Pulumi.Azure.Sql;
using Pulumi.Azure.Storage;
using Pulumi.AzureNative.Insights;
using System.Linq;
using AzureNative = Pulumi.AzureNative;

class AppStack : Stack
{
    public AppStack()
    {
        Config();
        SetupStorageAccounts();
        SetupServicePlan();
        SetKeyVault();
        SetupRedis();
        SetupApplicationInsights();
        SetupAzureMediaService();
        SetupSQL();
        SetupIdentityService();
        SetupApiService();
        SetupPublicWebService();
        SetupBlazorService();
    }

    private void Config()
    {
        Configuration = new Config();
        StackName = Output.Create(Deployment.Instance.StackName);
        var current = Output.Create(GetSubscription.InvokeAsync());
        CurrentSubscriptionId = current.Apply(current => current.Id);
        CurrentSubscriptionDisplayName = current.Apply(current => current.DisplayName);

        ResourceGroup = Output.Create(new ResourceGroup("MyTest-rg", new ResourceGroupArgs
        {
            Location = Configuration.Require("location"),
            Tags =
            {
                { "environment", StackName },
            },
        }));
    }
    private void SetupStorageAccounts()
    {
        // Create a storage account for Blobs (uploads and posters)
        var storageAccount = new Account("myteststorage", new AccountArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AccountReplicationType = "LRS",
            AccountTier = "Standard",
            Tags =
            {
                { "environment", StackName },
            },
        });

        StorageAccountId = Output.Create(storageAccount.Id).Apply(c => c);
        StorageAccountName = Output.Create(storageAccount.Name).Apply(c => c);
    }
    private void SetupServicePlan()
    {
        // A plan to host the App Service
        AppServicePlan = Output.Create(new Plan("MyTest-sp", new PlanArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Kind = "App",
            Sku = new PlanSkuArgs
            {
                Tier = "Basic",
                Size = "B1",
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));
    }
    private void SetKeyVault()
    {
        var clientConfig = Output.Create(GetClientConfig.InvokeAsync());
        var currentPrincipal = clientConfig.Apply(c => c.ObjectId);
        TenantId = clientConfig.Apply(c => c.TenantId);

        // Key Vault to store secrets
        KeyVault = Output.Create(new KeyVault("vault", new KeyVaultArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SkuName = "standard",
            TenantId = TenantId,
            AccessPolicies =
            {
                new KeyVaultAccessPolicyArgs
                {
                    TenantId = TenantId,
                    // The current principal has to be granted permissions to Key Vault so that it can actually add and then remove
                    // secrets to/from the Key Vault. Otherwise, 'pulumi up' and 'pulumi destroy' operations will fail.
                    ObjectId = currentPrincipal,
                    SecretPermissions = {"delete", "get", "list", "set"},
                }
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));
    }
    private void SetupRedis()
    {
        var redis = new AzureNative.Cache.Redis("mytestredis", new AzureNative.Cache.RedisArgs
        {
            EnableNonSslPort = true,
            Location = ResourceGroup.Apply(t => t.Location),
            MinimumTlsVersion = "1.2",
            Name = "cache1",
            RedisConfiguration = new AzureNative.Cache.Inputs.RedisCommonPropertiesRedisConfigurationArgs
            {
                MaxmemoryPolicy = "allkeys-lru",
            },
            ReplicasPerMaster = 2,
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ShardCount = 2,
            Sku = new AzureNative.Cache.Inputs.SkuArgs
            {
                Capacity = 1,
                Family = "P",
                Name = "Premium",
            },
            //StaticIP = "192.168.0.5",
            //SubnetId = $"/subscriptions/{CurrentSubscriptionId}/resourceGroups/{ResourceGroup.Apply(t => t.Name)}/providers/Microsoft.Network/virtualNetworks/network1/subnets/subnet1",
            Zones =
            {
                "1",
            },
            Tags =
            {
                { "environment", StackName },
            },
        });
    }
    private void SetupApplicationInsights()
    {
        var appInsights = new Component("appInsights", new ComponentArgs
        {
            ApplicationType = "web",
            Location = ResourceGroup.Apply(r => r.Location),
            Kind = "web",
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Tags =
            {
                { "environment", StackName },
            },
        });

        InstrumentationKey = Output.Create(appInsights.InstrumentationKey).Apply(c => c);
    }
    private void SetupAzureMediaService()
    {
        var mediaServiceAccountName = "MyTestmediaservice";
        var mediaService = new AzureNative.Media.MediaService("MyTestMediaService", new AzureNative.Media.MediaServiceArgs
        {
            AccountName = mediaServiceAccountName,
            Location = ResourceGroup.Apply(t => t.Location),
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            StorageAccounts =
            {
                new AzureNative.Media.Inputs.StorageAccountArgs
                {
                    Id = StorageAccountId,
                    //Id = $"/subscriptions/{CurrentSubscriptionId}/resourceGroups/{ResourceGroup.Apply(t => t.Name)}/providers/Microsoft.Storage/storageAccounts/{mediaServiceAccountName}",
                    Type = "Primary",
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        });
    }
    private void SetupSQL()
    {
        // Azure SQL Server that we want to access from the application
        var sqlServer = new SqlServer("mytestsqlserver", new SqlServerArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            // The login and password are required but won't be used in our application
            AdministratorLogin = "manualadmin",
            AdministratorLoginPassword = Configuration.RequireSecret("dbPassword"),
            Version = "12.0",
            Tags =
            {
                { "environment", StackName },
            },
        });

        SqlServerName = Output.Create(sqlServer.Name).Apply(c => c);

        // Azure SQL Database that we want to access from the application
        var database = new Database("MyTest_db", new DatabaseArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ServerName = sqlServer.Name,
            RequestedServiceObjectiveName = "S0",
            Tags =
            {
                { "environment", StackName },
            },
        });

        // The connection string that has no credentials in it: authertication will come through MSI
        ConnectionString = Output.Format($"Server=tcp:{sqlServer.Name}.database.windows.net;Database={database.Name};");

        ConnectionStringSecret = Output.Create(new Secret("Connection-String-Secret", new SecretArgs
        {
            Name = "connectionStringSecret",
            KeyVaultId = KeyVault.Apply(k => k.Id),
            Value = ConnectionString,
        }));

    }

    private void SetupIdentityService()
    {
        var paymentRapydSecretKeySecret = new Secret("Payment-Rapyd-SecretKey", new SecretArgs
        {
            Name = "paymentRapydSecretKey",
            KeyVaultId = KeyVault.Apply(k => k.Id),
            Value = "SomeTestValue...",
        });
        var secretUri = Output.Format($"{KeyVault.Apply(k => k.VaultUri)}secrets/{paymentRapydSecretKeySecret.Name}/{paymentRapydSecretKeySecret.Version}");

        var secretConnectionStringUri = Output.Format($"{KeyVault.Apply(k => k.VaultUri)}secrets/{ConnectionStringSecret.Apply(s => s.Name)}/{ConnectionStringSecret.Apply(s => s.Version)}");

        // The application hosted in App Service
        var identityApp = new AppService("MyTestIdentity", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Apply(t => t.Id),
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},

                // The setting points directly to the KV setting
                { "Test:Level:Key", Output.Format($"@Microsoft.KeyVault(SecretUri={secretUri})")},
                { "ConnectionStrings:Default", Output.Format($"@Microsoft.KeyVault(SecretUri={secretConnectionStringUri})")}
            },
            ConnectionStrings =
            {
                new AppServiceConnectionStringArgs
                {
                    Name = "db",
                    Type = "SQLAzure",
                    Value = ConnectionString.Apply(c=>c),
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        });

        // Work around a preview issue https://github.com/pulumi/pulumi-azure/issues/192
        var principalId = identityApp.Identity.Apply(id => id.PrincipalId ?? "11111111-1111-1111-1111-111111111111");

        // Grant App Service access to KV secrets
        var policy = new AccessPolicy("identity-app-policy", new AccessPolicyArgs
        {
            KeyVaultId = KeyVault.Apply(t => t.Id),
            TenantId = TenantId,
            ObjectId = principalId,
            SecretPermissions = { "get" },
        });

        // Make the App Service the admin of the SQL Server (double check if you want a more fine-grained security model in your real app)
        var sqlAdmin = new ActiveDirectoryAdministrator("identityadmin", new ActiveDirectoryAdministratorArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            TenantId = TenantId,
            ObjectId = principalId,
            Login = "adadmin",
            ServerName = SqlServerName,
        });

        // Add SQL firewall exceptions
        var firewallRules = identityApp.OutboundIpAddresses.Apply(
            ips => ips.Split(",").Select(
                ip => new FirewallRule($"FRI{ip}", new FirewallRuleArgs
                {
                    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                    StartIpAddress = ip,
                    EndIpAddress = ip,
                    ServerName = SqlServerName,
                })
            ).ToList());

        this.IdentityEndpoint = Output.Format($"https://{identityApp.DefaultSiteHostname}");
    }
    private void SetupApiService()
    {
        var posterImagesStorageContainer = new Container("event-poster-images", new ContainerArgs
        {
            StorageAccountName = StorageAccountName,
            ContainerAccessType = "private",
        });

        var uploadedRecordingsStorageContainer = new Container("uploaded-videos", new ContainerArgs
        {
            StorageAccountName = StorageAccountName,
            ContainerAccessType = "private",
        });

        // The application hosted in App Service
        var apiApp = new AppService("MyTestApi", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Apply(t => t.Id),
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},
            },
            ConnectionStrings =
            {
                new AppServiceConnectionStringArgs
                {
                    Name = "db",
                    Type = "SQLAzure",
                    Value = ConnectionString.Apply(c=>c),
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        });

        // Work around a preview issue https://github.com/pulumi/pulumi-azure/issues/192
        var principalId = apiApp.Identity.Apply(id => id.PrincipalId ?? "11111111-1111-1111-1111-111111111111");

        // Grant App Service access to KV secrets
        var policy = new AccessPolicy("api-app-policy", new AccessPolicyArgs
        {
            KeyVaultId = KeyVault.Apply(t => t.Id),
            TenantId = TenantId,
            ObjectId = principalId,
            SecretPermissions = { "get" },
        });

        // Make the App Service the admin of the SQL Server (double check if you want a more fine-grained security model in your real app)
        var sqlAdmin = new ActiveDirectoryAdministrator("apiadmin", new ActiveDirectoryAdministratorArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            TenantId = TenantId,
            ObjectId = principalId,
            Login = "adadmin",
            ServerName = SqlServerName,
        });

        // Grant access from App Service to the container in the storage
        var posterImagesBlobPermission = new Assignment("readposterblobpermission", new AssignmentArgs
        {
            PrincipalId = principalId,
            Scope = Output.Format($"{StorageAccountId}/blobServices/default/containers/{posterImagesStorageContainer.Name}"),
            RoleDefinitionName = "Poster Images Storage Blob Data Reader",
        });

        // Grant access from App Service to the container in the storage
        var uploadedRecordingsBlobPermission = new Assignment("uploadrecordingblobpermission", new AssignmentArgs
        {
            PrincipalId = principalId,
            Scope = Output.Format($"{StorageAccountId}/blobServices/default/containers/{uploadedRecordingsStorageContainer.Name}"),
            RoleDefinitionName = "Uploaded Recordings Storage Blob Data Reader",
        });

        // Add SQL firewall exceptions
        var firewallRules = apiApp.OutboundIpAddresses.Apply(
            ips => ips.Split(",").Select(
                ip => new FirewallRule($"FRA{ip}", new FirewallRuleArgs
                {
                    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                    StartIpAddress = ip,
                    EndIpAddress = ip,
                    ServerName = SqlServerName,
                })
            ).ToList());

        this.ApiEndpoint = Output.Format($"https://{apiApp.DefaultSiteHostname}");
    }
    private void SetupPublicWebService()
    {
        // The application hosted in App Service
        var publicWebApp = new AppService("MyTestPublicWeb", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Apply(t => t.Id),
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},
            },
            //ConnectionStrings =
            //{
            //    new AppServiceConnectionStringArgs
            //    {
            //        Name = "db",
            //        Type = "SQLAzure",
            //        Value = connectionString,
            //    },
            //},
            Tags =
            {
                { "environment", StackName },
            },
        });

        this.PublicWebAppEndpoint = Output.Format($"https://{publicWebApp.DefaultSiteHostname}");
    }
    private void SetupBlazorService()
    {
        // The application hosted in App Service
        var blazorApp = new AppService("MyTestBlazor", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Apply(t => t.Id),
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                //{"App:SelfUrl", Output.Format($"https://{this.???}")}, // is this even possible since it. Maybe its not correct in azure to have this config...
            },
            Tags =
            {
                { "environment", StackName },
            },
        });

        this.BlazorEndpoint = Output.Format($"https://{blazorApp.DefaultSiteHostname}");
    }

    [Output] public Output<Secret> ConnectionStringSecret { get; set; }
    [Output] public Output<string> StackName { get; set; }
    [Output] public Output<string> ConnectionString { get; set; } //remove this one and use ConnectionStringSecret?

   public Config Configuration { get; set; }

    [Output("currentSubscriptionId")]
    public Output<string> CurrentSubscriptionId { get; set; }
    [Output("currentSubscriptionDisplayName")]
    public Output<string> CurrentSubscriptionDisplayName { get; set; }
    [Output] public Output<string> StorageAccountId { get; set; }
    [Output] public Output<string> StorageAccountName { get; set; }
    [Output] public Output<string> SqlServerName { get; set; }
    [Output] public Output<string> InstrumentationKey { get; set; }
    [Output] public Output<KeyVault> KeyVault { get; set; }
    [Output] public Output<string> TenantId { get; set; }
    [Output] public Output<Plan> AppServicePlan { get; set; }
    [Output] public Output<ResourceGroup> ResourceGroup { get; set; }
    [Output] public Output<string> IdentityEndpoint { get; set; }
    [Output] public Output<string> ApiEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppEndpoint { get; set; }
    [Output] public Output<string> BlazorEndpoint { get; set; }
}