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
using System.Collections.Generic;
using System.Linq;
using AzureNative = Pulumi.AzureNative;

class AppStack : Stack
{
    public AppStack()
    {
        Config();
        SetKeyVault();
        SetupCertifications();
        SetupCDN();
        SetupSQL();
        SetupActiveDirectory();
        SetupStorageAccounts();
        SetupServicePlan();
        SetSecrets();
        SetupRedis();
        SetupApplicationInsights();
        SetupAzureMediaService();

        // My code goes into these services!
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

        ResourceGroup = Output.Create(new ResourceGroup("MyProject-rg", new ResourceGroupArgs
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
    private void SetKeyVault()
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

    }
    private void SetupCertifications()
    {
        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            // *.myProject.is wildcard certificate! https://www.pulumi.com/registry/packages/azure/api-docs/appservice/certificateorder/
            CertificateOrder = new CertificateOrder("myProjectiscertificate", new CertificateOrderArgs
            {
                AutoRenew = true,
                KeySize = 2048,
                Location = "global",
                Name = "myProjectcert",
                ProductType = "WildCard",
                ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                Tags =
            {
                { "environment", StackName },
            },
                ValidityInYears = 1,
            }, new CustomResourceOptions
            {
                Protect = true,
            });

            //TODO: Add this CertificateOrder Certificate to KeyVault when I have my question answered.. https://pulumi-community.slack.com/archives/C84L4E3N1/p1641716620098800
            // I imported it like this.. but is it enough? pulumi import azure:keyvault/secret:Secret CertificateOrderSecret "https://vault723aa5a.vault.azure.net/secrets/.../..."
            var certOrderSecret = new Secret("CertOrderSecret", new SecretArgs
            {
                ContentType = "application/x-pkcs12",
                //ExpirationDate = "2023-01-09T09:28:06Z", //Do we want this?
                KeyVaultId = KeyVault.Id,
                NotBeforeDate = "2022-01-09T09:28:06Z",
                Tags =
            {
                { "CertificateId", CertificateOrder.Id },
                { "CertificateState", "Ready" },
                { "SerialNumber", "01D774B64A545663" },
                { "Thumbprint", "AA410F20BD5FB1179F3473540BA71B7EBFB94160" },
            },
                Value = Configuration.RequireSecret("WildCardCertSecret"),
            }, new CustomResourceOptions
            {
                Protect = true,
            });
        }

        //not sure what to do with this one... was just trying it out
        //WebCertificate = new AzureNative.Web.Certificate("webcertificate", new AzureNative.Web.CertificateArgs
        //{
        //    //KeyVaultId = KeyVault.Id, //This throws an error "The parameter Properties.KeyVaultId has an invalid value."! No idea why because it works everywhere else!
        //    HostNames =
        //    {
        //        "ServerCert",
        //    },
        //    Location = "northeurope",
        //    Name = "myProjectc4321",
        //    Password = Configuration.RequireSecret("WebCertificate"),
        //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        //});
    }
    private void SetupCDN()
    {
        CDNProfile = new AzureNative.Cdn.Profile("myProject-cdn", new AzureNative.Cdn.ProfileArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Sku = new AzureNative.Cdn.Inputs.SkuArgs
            {
                // https://docs.microsoft.com/en-us/azure/cdn/cdn-features
                Name = "Standard_Microsoft" //or Standard_Microsoft or Standard_Akamai
            }
        });
    }
    private void SetupSQL()
    {
        // Azure SQL Server that we want to access from the application
        SqlServer = new SqlServer("sqlservermyProject", new SqlServerArgs
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
        Database = new Database("myProject_db", new DatabaseArgs
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
        // Connection string with password until this issue is solved https://github.com/pulumi/pulumi-azure-native/issues/1416
        ConnectionStringWithPassword = Output.Create($"Server=tcp:{SqlServer.Name}.database.windows.net,1433;Initial Catalog={Database.Name};Persist Security Info=False;User ID=manualadmin;Password={Configuration.RequireSecret("dbPassword")};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;").Apply(c => c);
    }
    private void SetupActiveDirectory()
    {
        // This code got created when I ran the following command
        //pulumi import azure-native:sql:ServerAzureADAdministrator activeDirectory /subscriptions/.../resourceGroups/myProject-rg42d3b5aa/providers/Microsoft.Sql/servers/myProjectsqlserveree7348a/administrators/ActiveDirectory

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
        AppServicePlan = new Plan("MyProject-sp", new PlanArgs
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
    private void SetSecrets()
    {
        #region ConnectionString
        var connectionStringSecret = new Secret("ConnectionString", new SecretArgs
        {
            KeyVaultId = KeyVault.Id,
            Value = ConnectionStringWithPassword
        });
        #endregion

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
            Redis = new AzureNative.Cache.Redis("redisCacheMyProject", new AzureNative.Cache.RedisArgs
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
        var mediaServiceAccountName = "mediaservicemyProject";
        var mediaService = new AzureNative.Media.MediaService("mediaservicemyProject", new AzureNative.Media.MediaServiceArgs
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
        // Question about this: https://pulumi-community.slack.com/archives/C01PF3E1B8V/p1641743241030600
        // Maybe I don´t need this because of AppService -> SourceControl! But else make this work and add it to the API and Blazor AppServices!
        // Deploy the zipped code to a blob https://www.pulumi.com/blog/level-up-your-azure-platform-as-a-service-applications-with-pulumi/#2-deployment-artifact
        //var zippedCodeContainer = new Container("IdentityService-code", new ContainerArgs
        //{
        //    StorageAccountName = StorageAccountName,
        //    ContainerAccessType = "private",
        //});

        //var blob = new Blob("IdentityServiceCodeBlob", new BlobArgs
        //{
        //    StorageAccountName = StorageAccountName,
        //    StorageContainerName = zippedCodeContainer.Name,
        //    Type = "Block",
        //    Source = new FileArchive("../src/MyProject.IdentityServer/debug/net6.0/publish.zip") //<-- How do we zip it? Why is it set here? And what if prod/release?
        //});
        //IdentityCodeBlobEndpoint = blob.Url; // remove IdentityCodeBlobEndpoint if its only used here

        // The application hosted in App Service
        var identityApp = new AppService("IdentityService", new AppServiceArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AppServicePlanId = AppServicePlan.Id,
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AppServiceIdentityArgs { Type = "SystemAssigned" },
            SourceControl = new AppServiceSourceControlArgs
            {
                Branch = "dev", //TODO: If StackName == "production" then "master"
                RepoUrl = "https://dev.azure.com/myProject/StreamWorks/_git/StreamWorks"
            },
            AppSettings =
            {
                { "WEBSITE_RUN_FROM_PACKAGE", IdentityCodeBlobEndpoint},
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
                    //Value = ConnectionStringWithoutPassword.Apply(c=>c), //TODO: When issue solved add this back https://github.com/pulumi/pulumi-azure-native/issues/1416
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
        var principalId = identityApp.Identity.Apply(id => id.PrincipalId ?? "11111111-1111-1111-1111-111111111111");

        // Grant App Service access to KV secrets
        var policy = new AccessPolicy("identity-app-policy", new AccessPolicyArgs
        {
            KeyVaultId = KeyVault.Id,
            TenantId = TenantId,
            ObjectId = principalId,
            SecretPermissions = { "get" },
        });

        //TODO: Add back after https://github.com/pulumi/pulumi-azure-native/issues/1416 (see also https://pulumi-community.slack.com/archives/C84L4E3N1/p1641632588082900?thread_ts=1641199066.239100&cid=C84L4E3N1)
        // Make the App Service the admin of the SQL Server (double check if you want a more fine-grained security model in your real app)
        //var sqlAdmin = new ActiveDirectoryAdministrator("identityadmin", new ActiveDirectoryAdministratorArgs
        //{
        //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        //    TenantId = TenantId,
        //    ObjectId = principalId,
        //    Login = "adadmin",
        //    ServerName = SqlServer.Name,
        //});

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
            SourceControl = new AppServiceSourceControlArgs
            {
                Branch = "dev", //TODO: If StackName == "production" then "master"
                RepoUrl = "https://dev.azure.com/myProject/StreamWorks/_git/StreamWorks"
            },
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
                    //Value = ConnectionStringWithoutPassword.Apply(c=>c), //TODO: When issue solved add this back https://github.com/pulumi/pulumi-azure-native/issues/1416
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

        //TODO: Add back after https://github.com/pulumi/pulumi-azure-native/issues/1416
        // Make the App Service the admin of the SQL Server (double check if you want a more fine-grained security model in your real app)
        //var sqlAdmin = new ActiveDirectoryAdministrator("apiadmin", new ActiveDirectoryAdministratorArgs
        //{
        //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        //    TenantId = TenantId,
        //    ObjectId = principalId,
        //    Login = "adadmin",
        //    ServerName = SqlServer.Name,
        //});

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
            SourceControl = new AppServiceSourceControlArgs
            {
                Branch = "dev", //TODO: If StackName == "production" then "master"
                RepoUrl = "https://dev.azure.com/myProject/StreamWorks/_git/StreamWorks"
            },
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

        // CDN (do I need this?
        var endpoint = new AzureNative.Cdn.Endpoint("public-web-cdn-ep", new AzureNative.Cdn.EndpointArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ProfileName = CDNProfile.Name,
            IsHttpAllowed = false,
            IsHttpsAllowed = true,
            OriginHostHeader = publicWebApp.DefaultSiteHostname,
            Origins = new List<AzureNative.Cdn.Inputs.DeepCreatedOriginArgs> {
                {
                    new AzureNative.Cdn.Inputs.DeepCreatedOriginArgs { Name = "public-web", HostName = publicWebApp.DefaultSiteHostname }
                }
            }
        });

        PublicWebAppCDNEndpoint = Output.Format($"https://{endpoint.HostName}");

        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            // My custom domain question https://pulumi-community.slack.com/archives/C01PF3E1B8V/p1641648951021600
            //TODO: Don´t I need this?  https://www.pulumi.com/registry/packages/azure-native/api-docs/appplatform/customdomain/
            //var customDomain = new AzureNative.AppPlatform.CustomDomain("publicCustomDomain", new AzureNative.AppPlatform.CustomDomainArgs
            //{
            //    AppName = publicWebApp.Name,
            //    DomainName = "myProject.is",
            //    Properties = new AzureNative.AppPlatform.Inputs.CustomDomainPropertiesArgs
            //    {
            //        CertName = CertificateOrder.Name,
            //        Thumbprint = CertificateOrder.SignedCertificateThumbprint, //There are other thumbprints on there.. no idea what to use (if any)!
            //    },
            //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            //    ServiceName = "???",
            //});
        }
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
            SourceControl = new AppServiceSourceControlArgs
            {
                Branch = "dev", //TODO: If StackName == "production" then "master"
                RepoUrl = "https://dev.azure.com/myProject/StreamWorks/_git/StreamWorks"
            },
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

    public CertificateOrder CertificateOrder { get; set; }
    public AzureNative.Web.Certificate WebCertificate { get; set; }
    public AzureNative.Cdn.Profile CDNProfile { get; set; }
    public Database Database { get; set; }
    public SqlServer SqlServer { get; set; }
    public AzureNative.Cache.Redis Redis { get; set; }
    public Config Configuration { get; set; }
    public KeyVault KeyVault { get; set; }
    public Plan AppServicePlan { get; set; }

    // Do I need to have these as outputs? 
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
    [Output] public Output<string> ConnectionStringWithPassword { get; set; }
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
    [Output] public Output<string> IdentityCodeBlobEndpoint { get; set; }
    [Output] public Output<string> ApiEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppCDNEndpoint { get; set; }
    [Output] public Output<string> BlazorEndpoint { get; set; }
}