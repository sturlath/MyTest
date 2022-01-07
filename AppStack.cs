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
using System;
using System.Linq;
using AzureNative = Pulumi.AzureNative;

class AppStack : Stack
{
    public AppStack()
    {
        Config();
        SetupSQL();
        SetupActiveDirectory();
        SetupStorageAccounts();
        SetupServicePlan();
        SetKeyVaultAndSecrets();
        SetupRedis();
        SetupApplicationInsights();
        SetupAzureMediaService();
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

        ResourceGroup = Output.Create(new ResourceGroup("Beinni-rg", new ResourceGroupArgs
        {
            Location = Configuration.Require("location"),
            Tags =
            {
                { "environment", StackName },
            },
        }));

        var clientConfig = Output.Create(GetClientConfig.InvokeAsync());
        CurrentPricipal = clientConfig.Apply(c => c.ObjectId);
        TenantId = clientConfig.Apply(c => c.TenantId);
    }
    private void SetupSQL()
    {
        // Azure SQL Server that we want to access from the application
        SqlServer = new SqlServer("sqlserverbeinni", new SqlServerArgs
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

        // Azure SQL Database that we want to access from the application
        Database = new Database("beinni_db", new DatabaseArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ServerName = SqlServer.Name,
            RequestedServiceObjectiveName = "S0", //Standard 10GB
            Tags =
            {
                { "environment", StackName },
            },
        }
        , new CustomResourceOptions
        {
            // Please note that the imported resources are marked as protected. To destroy them
            // you will need to remove the `protect` option and run `pulumi update` *before*
            // the destroy will take effect.
            //Protect = true,
        }
        );

        // The connection string that has no credentials in it: authertication will come through MSI
        ConnectionString = Output.Create($"Server=tcp:{SqlServer.Name}.database.windows.net;Database={Database.Name};").Apply(c => c);
        //ConnectionString =  Output.Format($"Server=tcp:{SqlServer.Name}.database.windows.net;Database={Database.Name};");
    }
    private void SetupActiveDirectory()
    {
        // This code got created when I ran the following command
        //pulumi import azure-native:sql:ServerAzureADAdministrator activeDirectory /subscriptions/0e96528a-9029-46fc-b37c-b313473b5ae5/resourceGroups/beinni-rg42d3b5aa/providers/Microsoft.Sql/servers/beinnisqlserveree7348a/administrators/ActiveDirectory

        var activeDirectory = new AzureNative.Sql.ServerAzureADAdministrator("activeDirectory", new AzureNative.Sql.ServerAzureADAdministratorArgs
        {
            AdministratorName = "ActiveDirectory",
            AdministratorType = "ActiveDirectory",
            Login = "adadmin",
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ServerName = SqlServer.Name,
            Sid = "d4aee8f4-2aa3-42cf-b6b0-1260b11516d7",
            TenantId = CurrentPricipal,
        }
        , new CustomResourceOptions
        {
            // Please note that the imported resources are marked as protected. To destroy them
            // you will need to remove the `protect` option and run `pulumi update` *before*
            // the destroy will take effect.
            //Protect = true,
        }
        );
    }
    private void SetupStorageAccounts()
    {
        // Create a storage account for Blobs (uploads and posters)
        var storageAccount = new Account("storage", new AccountArgs
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
        AppServicePlan = new Plan("Beinni-sp", new PlanArgs
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
        });
    }
    private void SetKeyVaultAndSecrets()
    {
        // Key Vault to store secrets
        KeyVault = new KeyVault("vault", new KeyVaultArgs
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
                    ObjectId = CurrentPricipal,
                    SecretPermissions = {"delete", "get", "list", "set"},
                }
            },
            Tags =
            {
                { "environment", StackName },
            },
        });

        var connectionStringSecret = new Secret("ConnectionString", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Output.Format($"Server=tcp:{SqlServer.Name}.database.windows.net;Database={Database.Name};"),
        });

        #region Encryption
        var stringEncryptionDefaultPassPhraseSecret = new Secret("DefaultPassPhrase", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("StringEncryption-DefaultPassPhrase"),
        });

        #endregion

        #region Rapyd
        var paymentRapydSecretKeySecret = new Secret("Rapyd-SecretKey", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("Payment-Rapyd-SecretKey"),
        });

        var paymentRapydAccessKeySecret = new Secret("Rapyd-AccessKey", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("Payment-Rapyd-AccessKey"),
        });
        #endregion

        #region Twilio
        var twilioSms_AccountSIdSecret = new Secret("Twilio-AccountSId", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("TwilioSms-AccountSId"),
        });

        var twilioSms_AuthTokenSecret = new Secret("Twilio-AuthToken", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("TwilioSms-AuthToken"),
        });
        #endregion

        #region Facebook
        var authentication_Facebook_AppId = new Secret("Facebook-AppId", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("Authentication-Facebook-AppId"),
        });

        var authentication_Facebook_AppSecret = new Secret("FacebookAppSecret", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = Configuration.RequireSecret("Authentication-Facebook-AppSecret"),
        });
        #endregion

        ConnectionStringSecretUri = Output.Format($"{KeyVault.VaultUri}secrets/{connectionStringSecret.Name}/{connectionStringSecret.Version}");
        DefaultEncryptionPassPhraseUri = Output.Format($"{KeyVault.VaultUri}secrets/{stringEncryptionDefaultPassPhraseSecret.Name}/{stringEncryptionDefaultPassPhraseSecret.Version}");
        RapydSecretKeyUri = Output.Format($"{KeyVault.VaultUri}secrets/{paymentRapydSecretKeySecret.Name}/{paymentRapydSecretKeySecret.Version}");
        RapydAccessKeyUri = Output.Format($"{KeyVault.VaultUri}secrets/{paymentRapydAccessKeySecret.Name}/{paymentRapydAccessKeySecret.Version}");
        TwilioSmsAccountSIdUri = Output.Format($"{KeyVault.VaultUri}secrets/{twilioSms_AccountSIdSecret.Name}/{twilioSms_AccountSIdSecret.Version}");
        TwilioSmsAuthTokenUri = Output.Format($"{KeyVault.VaultUri}secrets/{twilioSms_AuthTokenSecret.Name}/{twilioSms_AuthTokenSecret.Version}");
        FacebookAppIdUri = Output.Format($"{KeyVault.VaultUri}secrets/{authentication_Facebook_AppId.Name}/{authentication_Facebook_AppId.Version}");
        FacebookAppSecret = Output.Format($"{KeyVault.VaultUri}secrets/{authentication_Facebook_AppSecret.Name}/{authentication_Facebook_AppSecret.Version}");
    }
    private void SetupRedis()
    {
        if (Deployment.Instance.StackName.ToLower() == "dev") //<<-- not sure if you should do this!
        {
            Redis = new AzureNative.Cache.Redis("redisCacheBeinni", new AzureNative.Cache.RedisArgs
            {
                EnableNonSslPort = false,
                Location = ResourceGroup.Apply(t => t.Location),
                MinimumTlsVersion = "1.2",
                RedisConfiguration = new AzureNative.Cache.Inputs.RedisCommonPropertiesRedisConfigurationArgs
                {
                    MaxmemoryPolicy = "allkeys-lru",
                },
                ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                Sku = new AzureNative.Cache.Inputs.SkuArgs
                {
                    Capacity = 0,
                    Family = "C",
                    Name = "Basic",
                },
                //StaticIP = "192.168.0.5",
                //SubnetId = $"/subscriptions/{CurrentSubscriptionId}/resourceGroups/{ResourceGroup.Apply(t => t.Name)}/providers/Microsoft.Network/virtualNetworks/network1/subnets/subnet1",
                //ReplicasPerMaster = 1,//needs premium Sku!
                //ShardCount = 1,//needs premium Sku!
                //Zones = //needs premium Sku!
                //{
                //    "1",
                //},
                Tags =
            {
                { "environment", StackName },
            },
            });
        }
        else
        {
            throw new NotImplementedException("TODO: bigger?");
        }
    }
    private void SetupApplicationInsights()
    {
        var appInsights = new Component("applicationInsights", new ComponentArgs
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
        var mediaServiceAccountName = "mediaservicebeinni";
        var mediaService = new AzureNative.Media.MediaService("mediaservicebeinni", new AzureNative.Media.MediaServiceArgs
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
    private void SetupIdentityService()
    {
        // The application hosted in App Service
        var identityApp = new AppService("IdentityService", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Id,
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },
            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},

                //Abp.io 
                { "App:SelfUrl", IdentityEndpoint},
                { "App:HttpApiUrl", ApiEndpoint},
                { "App:CorsOrigins", $"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"},
                { "App:RedirectAllowedUrls", $"{PublicWebAppEndpoint},{BlazorEndpoint}"},
                { "App:MVCPublicUrl", PublicWebAppEndpoint},
                { "App:BlazorUrl", BlazorEndpoint},

                { "Redis:Configuration", Redis.RedisConfiguration.Apply(c=>c.AofStorageConnectionString0)}, //TODO: Should you do it like this?

                // The setting points directly to the KV setting               
                { "Rapyd:SecretKey", Output.Format($"@Microsoft.KeyVault(SecretUri={RapydSecretKeyUri})")},
                { "Rapyd:AccessKey", Output.Format($"@Microsoft.KeyVault(SecretUri={RapydAccessKeyUri})")},
                { "Authentication:Facebook:AppId", Output.Format($"@Microsoft.KeyVault(SecretUri={FacebookAppIdUri})")},
                { "Authentication:Facebook:AppSecret", Output.Format($"@Microsoft.KeyVault(SecretUri={FacebookAppSecret})")}
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
            SiteConfig = new AppServiceSiteConfigArgs
            {
                Cors = new AppServiceSiteConfigCorsArgs
                {
                    AllowedOrigins = $"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"
                }
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
            KeyVaultId = KeyVault.Id,
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
            ServerName = SqlServer.Name,
        });

        // Add SQL firewall exceptions
        var firewallRules = identityApp.OutboundIpAddresses.Apply(
            ips => ips.Split(",").Select(
                ip => new FirewallRule($"FRI{ip}", new FirewallRuleArgs
                {
                    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                    StartIpAddress = ip,
                    EndIpAddress = ip,
                    ServerName = SqlServer.Name
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
        var apiApp = new AppService("ApiService", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Id,
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},

                //Abp.io 
                { "App:SelfUrl", ApiEndpoint},
                { "App:MVCPublicUrl", PublicWebAppEndpoint},
                { "App:BlazorUrl", BlazorEndpoint},
                { "App:CorsOrigins", $"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"},

                { "AuthServer:Authority", IdentityEndpoint},
                { "Redis:Configuration", Redis.RedisConfiguration.Apply(c=>c.AofStorageConnectionString0)}, //TODO: Should you do it like this?

                // The setting points directly to the KV setting   
                { "Rapyd:SecretKey", Output.Format($"@Microsoft.KeyVault(SecretUri={RapydSecretKeyUri})")},
                { "Rapyd:AccessKey", Output.Format($"@Microsoft.KeyVault(SecretUri={RapydAccessKeyUri})")},
                { "AbpTwilioSms:AccountSId", Output.Format($"@Microsoft.KeyVault(SecretUri={TwilioSmsAccountSIdUri})")},
                { "AbpTwilioSms:AuthToken", Output.Format($"@Microsoft.KeyVault(SecretUri={TwilioSmsAuthTokenUri})")}
            },
            ConnectionStrings =
            {
                new AppServiceConnectionStringArgs
                {
                    Name = "db",
                    Type = "SQLAzure",
                    Value = Output.Format($"@Microsoft.KeyVault(SecretUri={ConnectionStringSecretUri})"),
                },
            },
            SiteConfig = new AppServiceSiteConfigArgs
            {
                Cors = new AppServiceSiteConfigCorsArgs
                {
                    AllowedOrigins = $"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"
                }
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
            KeyVaultId = KeyVault.Id,
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
            ServerName = SqlServer.Name,
        });

        // Grant access from App Service to the container in the storage
        var posterImagesBlobPermission = new Assignment("readposterblobpermission", new AssignmentArgs
        {
            PrincipalId = principalId,
            Scope = Output.Format($"{StorageAccountId}/blobServices/default/containers/{posterImagesStorageContainer.Name}"),
            RoleDefinitionName = "Storage Blob Data Reader",
        });

        // Grant access from App Service to the container in the storage
        var uploadedRecordingsBlobPermission = new Assignment("uploadrecordingblobpermission", new AssignmentArgs
        {
            PrincipalId = principalId,
            Scope = Output.Format($"{StorageAccountId}/blobServices/default/containers/{uploadedRecordingsStorageContainer.Name}"),
            RoleDefinitionName = "Storage Blob Data Reader",
        });

        // Add SQL firewall exceptions
        var firewallRules = apiApp.OutboundIpAddresses.Apply(
            ips => ips.Split(",").Select(
                ip => new FirewallRule($"FRA{ip}", new FirewallRuleArgs
                {
                    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                    StartIpAddress = ip,
                    EndIpAddress = ip,
                    ServerName = SqlServer.Name,
                })
            ).ToList());

        this.ApiEndpoint = Output.Format($"https://{apiApp.DefaultSiteHostname}");
    }
    private void SetupPublicWebService()
    {
        // The application hosted in App Service
        var publicWebApp = new AppService("PublicWeb", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Id,
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },

            AppSettings =
            {
                { "APPINSIGHTS_INSTRUMENTATIONKEY",InstrumentationKey},
                { "APPLICATIONINSIGHTS_CONNECTION_STRING",InstrumentationKey.Apply(key => $"InstrumentationKey={key}")},
                { "ApplicationInsightsAgent_EXTENSION_VERSION","~2"},

                //Abp.io 
                { "App:SelfUrl", PublicWebAppEndpoint},
                { "App:BlazorUrl", BlazorEndpoint},
                { "App:CorsOrigins", $"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"},
                { "RemoteServices:Default:BaseUrl", ApiEndpoint},
                { "AuthServer:Authority", IdentityEndpoint},

                { "Redis:Configuration", Redis.RedisConfiguration.Apply(c=>c.AofStorageConnectionString0)}, //TODO: Should you do it like this?
            },
            ConnectionStrings =
            {
                new AppServiceConnectionStringArgs
                {
                    Name = "db",
                    Type = "SQLAzure",
                    Value = Output.Format($"@Microsoft.KeyVault(SecretUri={ConnectionStringSecretUri})"),
                },
            },
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
        var blazorApp = new AppService("Blazor", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Id,
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
    public Database Database { get; set; }
    public SqlServer SqlServer { get; set; }
    public AzureNative.Cache.Redis Redis { get; set; }
    public Config Configuration { get; set; }
    public KeyVault KeyVault { get; set; }
    public Plan AppServicePlan { get; set; }

    [Output] public Output<AzureNative.Sql.ServerAzureADAdministrator> ServerAzureADAdministrator { get; set; }
    [Output] public Output<string> DefaultEncryptionPassPhraseUri { get; set; }
    [Output] public Output<string> FacebookAppIdUri { get; set; }
    [Output] public Output<string> FacebookAppSecret { get; set; }
    [Output] public Output<string> TwilioSmsAccountSIdUri { get; set; }
    [Output] public Output<string> TwilioSmsAuthTokenUri { get; set; }
    [Output] public Output<string> RapydSecretKeyUri { get; set; }
    [Output] public Output<string> RapydAccessKeyUri { get; set; }
    [Output] public Output<string> StackName { get; set; }
    [Output] public Output<string> CurrentPricipal { get; set; }
    [Output] public Output<string> ConnectionString { get; set; }
    [Output] public Output<string> ConnectionStringSecretUri { get; set; }
    [Output("currentSubscriptionId")]
    public Output<string> CurrentSubscriptionId { get; set; }
    [Output("currentSubscriptionDisplayName")]
    public Output<string> CurrentSubscriptionDisplayName { get; set; }
    [Output] public Output<string> StorageAccountId { get; set; }
    [Output] public Output<string> StorageAccountName { get; set; }
    [Output] public Output<string> InstrumentationKey { get; set; }
    [Output] public Output<string> TenantId { get; set; }
    [Output] public Output<ResourceGroup> ResourceGroup { get; set; }
    [Output] public Output<string> IdentityEndpoint { get; set; }
    [Output] public Output<string> ApiEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppEndpoint { get; set; }
    [Output] public Output<string> BlazorEndpoint { get; set; }
}