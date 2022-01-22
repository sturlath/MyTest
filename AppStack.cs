using Pulumi;
using Pulumi.Azure.Core;
using System;
using AzureNative = Pulumi.AzureNative;
using AzureTerraform = Pulumi.Azure; //We should try to eliminate usage of AzureTerraform!

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
        SetupCodeBlobContainer();
        SetupServicePlan();
        SetSecrets();
        SetupRedis();
        SetupApplicationInsights();
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

        var clientConfig = Output.Create(GetClientConfig.InvokeAsync());
        CurrentPricipal = clientConfig.Apply(c => c.ObjectId);
        TenantId = clientConfig.Apply(c => c.TenantId);

        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            shouldBeProtected = true;
        }
    }
    private void SetKeyVault()
    {
        KeyVault = new AzureNative.KeyVault.Vault("vault", new AzureNative.KeyVault.VaultArgs
        {
            Location = "northeurope",
            Properties = new AzureNative.KeyVault.Inputs.VaultPropertiesArgs
            {
                AccessPolicies =
                {
                    new AzureNative.KeyVault.Inputs.AccessPolicyEntryArgs
                    {
                        // The current principal has to be granted permissions to Key Vault so that it can actually add and then remove
                        // secrets to/from the Key Vault. Otherwise, 'pulumi up' and 'pulumi destroy' operations will fail.
                        ObjectId = CurrentPricipal,
                        Permissions = new AzureNative.KeyVault.Inputs.PermissionsArgs
                        {
                            //Shouldn´t I restrict this somewhat? Classic had e.g. SecretPermissions = {"delete", "get", "list", "set"}
                            Certificates =
                            {
                                "get",
                                "list",
                                "delete",
                                "create",
                                "import",
                                "update",
                                "managecontacts",
                                "getissuers",
                                "listissuers",
                                "setissuers",
                                "deleteissuers",
                                "manageissuers",
                                "recover",
                                "purge",
                            },
                            Keys =
                            {
                                "encrypt",
                                "decrypt",
                                "wrapKey",
                                "unwrapKey",
                                "sign",
                                "verify",
                                "get",
                                "list",
                                "create",
                                "update",
                                "import",
                                "delete",
                                "backup",
                                "restore",
                                "recover",
                                "purge",
                            },
                            Secrets =
                            {
                                "get",
                                "list",
                                "set",
                                "delete",
                                "backup",
                                "restore",
                                "recover",
                                "purge",
                            },
                        },
                        TenantId = TenantId,
                    },
                },
                EnabledForDeployment = true,
                EnabledForDiskEncryption = true,
                EnabledForTemplateDeployment = true,
                Sku = new AzureNative.KeyVault.Inputs.SkuArgs
                {
                    Family = "A",
                    Name = AzureNative.KeyVault.SkuName.Standard,
                },
                TenantId = TenantId,
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),

            Tags =
                {
                    { "environment", StackName },
                },
        }, new CustomResourceOptions
        {
            // Please note that the imported resources are marked as protected. To destroy them
            // you will need to remove the `protect` option and run `pulumi update` *before*
            // the destroy will take effect.
            Protect = shouldBeProtected,
        });
    }
    private void SetupCertifications()
    {
        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            //TODO: Import again https://www.pulumi.com/registry/packages/azure-native/api-docs/certificateregistration/appservicecertificateorder/
            // *.iMyTest.is wildcard certificate! https://www.pulumi.com/registry/packages/azure/api-docs/appservice/certificateorder/
            CertificateOrder = new AzureTerraform.AppService.CertificateOrder("iMyTestiscertificate", new AzureTerraform.AppService.CertificateOrderArgs
            {
                AutoRenew = true,
                KeySize = 2048,
                Location = "global",
                Name = "iMyTestcert",
                ProductType = "WildCard",
                ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                ValidityInYears = 1,
                Tags =
                        {
                            { "environment", StackName },
                        },
            }, new CustomResourceOptions
            {
                Protect = shouldBeProtected,
            });

            var certOrderSecret = new AzureTerraform.KeyVault.Secret("CertOrderSecret", new AzureTerraform.KeyVault.SecretArgs
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
                        { "Thumbprint", "AA310F20BD5FB1181F3473540BA71C7EBFB94190" },
                        { "environment", StackName },
                    },
                Value = Configuration.RequireSecret("WildCardCertSecret"),
            }, new CustomResourceOptions
            {
                Protect = shouldBeProtected,
            });
        }
    }
    private void SetupCDN()
    {
        CDNProfile = new AzureNative.Cdn.Profile("iMyTest-cdn", new AzureNative.Cdn.ProfileArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            Sku = new AzureNative.Cdn.Inputs.SkuArgs
            {
                // https://docs.microsoft.com/en-us/azure/cdn/cdn-features
                Name = AzureNative.Cdn.SkuName.Standard_Microsoft // Standard_Microsoft, Standard_Microsoft, Standard_Akamai
            }
        });
    }
    private void SetupSQL()
    {
        SqlServer = new AzureNative.Sql.Server("sqlserverMyTest", new AzureNative.Sql.ServerArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            // The login and password are required but won't be used in our application
            AdministratorLogin = "manualadmin",
            AdministratorLoginPassword = Configuration.RequireSecret("dbPassword"),
            Version = "12.0",
            Tags =
            {
                { "environment", StackName },
            },
        });

        new AzureNative.Sql.FirewallRule("AllowAllWindowsAzureIps",
            new AzureNative.Sql.FirewallRuleArgs
            {
                ServerName = SqlServer.Name,
                ResourceGroupName = ResourceGroup.Apply(t => t.Name),
                StartIpAddress = "0.0.0.0",
                EndIpAddress = "0.0.0.0",
            });

        var databaseSku = "S0"; //Standard 10GB

        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            databaseSku = ""; //TODO: What size should we pick...?
        }

        // Azure SQL Database that we want to access from the application
        Database = new AzureNative.Sql.Database("MyTest_db", new AzureNative.Sql.DatabaseArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            ServerName = SqlServer.Name,
            Sku = new AzureNative.Sql.Inputs.SkuArgs
            {
                Name = databaseSku
            },
            Tags =
            {
                { "environment", StackName },
            },
        }, new CustomResourceOptions
        {
            // Please note that the imported resources are marked as protected. To destroy them
            // you will need to remove the `protect` option and run `pulumi update` *before*
            // the destroy will take effect.
            Protect = shouldBeProtected,
        });

        // The connection string that has no credentials in it: authertication will come through MSI
        ConnectionStringWithoutPassword = Output.Create($"Server=tcp:{SqlServer.Name}.database.windows.net;Database={Database.Name};").Apply(c => c);
        ConnectionStringWithPassword = Output.Create($"Server=tcp:{SqlServer.Name}.database.windows.net,1433;Initial Catalog={Database.Name};Persist Security Info=False;User ID=manualadmin;Password={Configuration.RequireSecret("dbPassword")};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;").Apply(c => c);
    }
    private void SetupActiveDirectory()
    {
        // This code got created when I ran the following command
        //pulumi import azure-native:sql:ServerAzureADAdministrator activeDirectory /subscriptions/.../resourceGroups/MyTest-rg42d3b5aa/providers/Microsoft.Sql/servers/MyTestsqlserveree7348a/administrators/ActiveDirectory

        var activeDirectory = new AzureNative.Sql.ServerAzureADAdministrator("activeDirectory", new AzureNative.Sql.ServerAzureADAdministratorArgs
        {
            AdministratorName = "ActiveDirectory",
            AdministratorType = "ActiveDirectory",
            Login = "adadmin",
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            ServerName = SqlServer.Name,
            Sid = "d4aee8f4-3aa3-42cf-b6b0-1260b11516d7",
            TenantId = CurrentPricipal,
        }
        ,
        new CustomResourceOptions
        {
            // Please note that the imported resources are marked as protected. To destroy them
            // you will need to remove the `protect` option and run `pulumi update` *before*
            // the destroy will take effect.
            Protect = shouldBeProtected,
        }
        );
    }
    private void SetupStorageAccounts()
    {
        //https://docs.microsoft.com/en-us/azure/storage/common/storage-account-overview

        // Create a storage account for Blobs (uploads and posters)
        var storageAccount = new AzureNative.Storage.StorageAccount("storage", new AzureNative.Storage.StorageAccountArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            Kind = AzureNative.Storage.Kind.StorageV2,
            Sku = new AzureNative.Storage.Inputs.SkuArgs
            {
                Name = AzureNative.Storage.SkuName.Standard_LRS,
            },
            Tags =
            {
                { "environment", StackName },
            },
        });

        StorageAccountId = Output.Create(storageAccount.Id).Apply(c => c);
        StorageAccountName = Output.Create(storageAccount.Name).Apply(c => c);

        var posterImagesStorageContainer = new AzureNative.Storage.BlobContainer("event-poster-images", new AzureNative.Storage.BlobContainerArgs
        {
            AccountName = StorageAccountName,
            PublicAccess = AzureNative.Storage.PublicAccess.None,
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        });

        var uploadedRecordingsStorageContainer = new AzureNative.Storage.BlobContainer("uploaded-videos", new AzureNative.Storage.BlobContainerArgs
        {
            AccountName = StorageAccountName,
            PublicAccess = AzureNative.Storage.PublicAccess.None,
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        });
    }
    private void SetupCodeBlobContainer()
    {
        CodeBlobContainer = new AzureNative.Storage.BlobContainer("zips", new AzureNative.Storage.BlobContainerArgs
        {
            AccountName = StorageAccountName,
            PublicAccess = AzureNative.Storage.PublicAccess.None,
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        });
    }
    private void SetupServicePlan()
    {
        // A plan to host the App Service
        AppServicePlan = new AzureNative.Web.AppServicePlan("MyTest-sp", new AzureNative.Web.AppServicePlanArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            Kind = "App",
            Sku = new AzureNative.Web.Inputs.SkuDescriptionArgs
            {
                Tier = "Basic",
                Name = "B1"
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
        var connectionStringSecret = new AzureNative.KeyVault.Secret("ConnectionString", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = ConnectionStringWithPassword,
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "connectionString-secret",
            VaultName = KeyVault.Name,
        });

        ConnectionStringSecretUri = connectionStringSecret.Properties.Apply(t => t.SecretUri);

        // This is an example of how you would then get secret somewhere else in the code!
        //var secretExample = AzureNative.KeyVault.GetSecret.Invoke(new AzureNative.KeyVault.GetSecretInvokeArgs()
        //{
        //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
        //    SecretName = "connectionString-secret",
        //    VaultName = KeyVault.Name
        //});
        //... = secretExample.Apply(t => t.Properties.SecretUri);

        #endregion

        #region Encryption
        var stringEncryptionDefaultPassPhraseSecret = new AzureNative.KeyVault.Secret("DefaultPassPhrase", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("StringEncryption-DefaultPassPhrase"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "defaultPassPhrase-secret",
            VaultName = KeyVault.Name,
        });

        DefaultEncryptionPassPhraseUri = stringEncryptionDefaultPassPhraseSecret.Properties.Apply(t => t.SecretUri);

        #endregion

        #region Rapyd
        var paymentRapydSecretKeySecret = new AzureNative.KeyVault.Secret("Rapyd-SecretKey", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("Payment-Rapyd-SecretKey"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "rapyd-secretKey-secret",
            VaultName = KeyVault.Name,
        });

        var paymentRapydAccessKeySecret = new AzureNative.KeyVault.Secret("Rapyd-AccessKey", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("Payment-Rapyd-AccessKey"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "rapyd-accessKey-secret",
            VaultName = KeyVault.Name,

        });

        RapydSecretKeyUri = paymentRapydSecretKeySecret.Properties.Apply(t => t.SecretUri);
        RapydAccessKeyUri = paymentRapydAccessKeySecret.Properties.Apply(t => t.SecretUri);

        #endregion

        #region Twilio
        var twilioSms_AccountSIdSecret = new AzureNative.KeyVault.Secret("Twilio-AccountSId", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("TwilioSms-AccountSId"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "twiliosms-accountsid-secret",
            VaultName = KeyVault.Name,
        });

        var twilioSms_AuthTokenSecret = new AzureNative.KeyVault.Secret("Twilio-AuthToken", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("TwilioSms-AuthToken"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "twiliosms-authtoken-secret",
            VaultName = KeyVault.Name,
        });

        TwilioSmsAccountSIdUri = twilioSms_AccountSIdSecret.Properties.Apply(t => t.SecretUri);
        TwilioSmsAuthTokenUri = twilioSms_AuthTokenSecret.Properties.Apply(t => t.SecretUri);

        #endregion

        #region Facebook
        var authentication_Facebook_AppId = new AzureNative.KeyVault.Secret("Facebook-AppId", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("Authentication-Facebook-AppId"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "authentication-facebook-appid",
            VaultName = KeyVault.Name,
        });

        var authentication_Facebook_AppSecret = new AzureNative.KeyVault.Secret("FacebookAppSecret", new AzureNative.KeyVault.SecretArgs
        {
            Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
            {
                Value = Configuration.RequireSecret("Authentication-Facebook-AppSecret"),
            },
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            SecretName = "authentication-facebook-appSecret",
            VaultName = KeyVault.Name,
        });

        FacebookAppIdUri = authentication_Facebook_AppId.Properties.Apply(t => t.SecretUri);
        FacebookAppSecret = authentication_Facebook_AppSecret.Properties.Apply(t => t.SecretUri);
        #endregion
    }
    private void SetupRedis()
    {
        if (Deployment.Instance.StackName.ToLower() == "dev") //<<-- not sure if you should do this!
        {
            Redis = new AzureNative.Cache.Redis("redisCacheMyTest", new AzureNative.Cache.RedisArgs
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
        var appInsights = new AzureNative.Insights.Component("applicationInsights", new AzureNative.Insights.ComponentArgs
        {
            ApplicationType = "web",
            FlowType = "Bluefield",
            Location = ResourceGroup.Apply(r => r.Location),
            Kind = "web",
            RequestSource = "rest",
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Tags =
            {
                { "environment", StackName },
            },
        });

        ApplicationInsightsInstrumentationKey = appInsights.InstrumentationKey;

    }
    private void SetupIdentityService()
    {
        // See more about code blob https://github.com/pulumi/examples/blob/master/azure-cs-appservice/AppServiceStack.cs and https://martink.me/articles/deploying-.net-to-azure-app-service-with-pulumi
        var codeBlob = new AzureNative.Storage.Blob("IdentityService-code", new AzureNative.Storage.BlobArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AccountName = StorageAccountName,
            ContainerName = CodeBlobContainer.Name,
            Type = AzureNative.Storage.BlobType.Block,
            Source = new FileArchive("..\\src\\IdentityServer\\bin\\Release\\net6.0\\publish"), //can be debug if we like!
        });

        IdentityCodeBlobEndpoint = SignedBlobReadUrl(codeBlob, CodeBlobContainer, StorageAccountName, ResourceGroup.Apply(t => t));

        var identityApp = Output.Create(new AzureNative.Web.WebApp("IdentityService", new AzureNative.Web.WebAppArgs
        {
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AzureNative.Web.Inputs.ManagedServiceIdentityArgs { Type = AzureNative.Web.ManagedServiceIdentityType.SystemAssigned },
            //KeyVaultReferenceIdentity = KeyVault. //?
            //Kind = "FunctionApp", <-- if this would be AzureFunction
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            ServerFarmId = AppServicePlan.Id,
            SiteConfig = new AzureNative.Web.Inputs.SiteConfigArgs
            {
                AppSettings = {
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "WEBSITE_RUN_FROM_PACKAGE",
                        Value = IdentityCodeBlobEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ASPNETCORE_ENVIRONMENT",
                        Value = Deployment.Instance.StackName.ToLower(),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                        Value = ApplicationInsightsInstrumentationKey.Apply(c=>c)
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                        Value = ApplicationInsightsInstrumentationKey.Apply(key => $"InstrumentationKey={key}"),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                        Value = "~2",
                    },
                    //Abp.io
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:SelfUrl",
                        Value = IdentityEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:HttpApiUrl",
                        Value = ApiEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:MVCPublicUrl",
                        Value = PublicWebAppEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:BlazorUrl",
                        Value = BlazorEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:CorsOrigins",
                        Value = Output.Format($"{PublicWebAppEndpoint.Apply(c=>c)},{ApiEndpoint.Apply(c=>c)},{BlazorEndpoint.Apply(c=>c)}"),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:RedirectAllowedUrls",
                        Value = Output.Format($"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}"),
                    },

                    // Redis
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Redis:Configuration",
                        Value = Redis.AccessKeys.Apply(c=>c.PrimaryKey)
                    },

                    // These setting points directly to key-vault secrets
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Rapyd:SecretKey",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={RapydSecretKeyUri})"),
                    },
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Rapyd:AccessKey",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={RapydAccessKeyUri})"),
                    },
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Authentication:Facebook:AppId",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={FacebookAppIdUri})"),
                    },
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Authentication:Facebook:AppSecret",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={FacebookAppSecret})"),
                    },
                },
                ConnectionStrings = {
                    new AzureNative.Web.Inputs.ConnStringInfoArgs
                    {
                        Name = "db",
                        Type = AzureNative.Web.ConnectionStringType.SQLAzure,
                        ConnectionString = Output.Format($"@Microsoft.KeyVault(SecretUri={ConnectionStringSecretUri})")
                    },
                },
                Cors = new AzureNative.Web.Inputs.CorsSettingsArgs
                {
                    AllowedOrigins = Output.Format($"{PublicWebAppEndpoint},{ApiEndpoint},{BlazorEndpoint}")
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));


        GiveWebAppAccessToKeyVault(identityApp, "identity-app-policy");

        IdentityEndpoint = Output.Format($"https://{identityApp.Apply(c => c.DefaultHostName)}");
    }
    private void SetupApiService()
    {
        var codeBlob = new AzureNative.Storage.Blob("ApiService-code", new AzureNative.Storage.BlobArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AccountName = StorageAccountName,
            ContainerName = CodeBlobContainer.Name,
            Type = AzureNative.Storage.BlobType.Block,
            Source = new FileArchive("..\\src\\HttpApi.Host\\bin\\Release\\net6.0\\publish"), //can be debug if we like!
        });

        ApiyCodeBlobEndpoint = SignedBlobReadUrl(codeBlob, CodeBlobContainer, StorageAccountName, ResourceGroup.Apply(t => t));

        var apiApp = Output.Create(new AzureNative.Web.WebApp("ApiService", new AzureNative.Web.WebAppArgs
        {
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AzureNative.Web.Inputs.ManagedServiceIdentityArgs { Type = AzureNative.Web.ManagedServiceIdentityType.SystemAssigned },
            //KeyVaultReferenceIdentity = KeyVault. //?
            //Kind = "FunctionApp", <-- if this would be AzureFunction
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            ServerFarmId = AppServicePlan.Id,
            SiteConfig = new AzureNative.Web.Inputs.SiteConfigArgs
            {
                AppSettings = {
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "WEBSITE_RUN_FROM_PACKAGE",
                        Value = ApiyCodeBlobEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ASPNETCORE_ENVIRONMENT",
                        Value = Deployment.Instance.StackName.ToLower(),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                        Value = ApplicationInsightsInstrumentationKey
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                        Value = ApplicationInsightsInstrumentationKey.Apply(key => $"InstrumentationKey={key}"),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                        Value = "~2",
                    },
                    //Abp.io
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:SelfUrl",
                        Value = ApiEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:MVCPublicUrl",
                        Value = PublicWebAppEndpoint.Apply(c=>c),
                    },
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:BlazorUrl",
                        Value = BlazorEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "AuthServer:Authority",
                        Value = IdentityEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:CorsOrigins",
                        Value = Output.Format($"{PublicWebAppEndpoint},{BlazorEndpoint}"),
                    },
                    // Redis
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Redis:Configuration",
                        Value = Redis.AccessKeys.Apply(c=>c.PrimaryKey),
                    },

                    // These setting points directly to key-vault secrets
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Rapyd:SecretKey",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={RapydSecretKeyUri})"),
                    },
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Rapyd:AccessKey",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={RapydAccessKeyUri})"),
                    },
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "AbpTwilioSms:AccountSId",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={TwilioSmsAccountSIdUri})"),
                    },
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "AbpTwilioSms:AuthToken",
                        Value =  Output.Format($"@Microsoft.KeyVault(SecretUri={TwilioSmsAuthTokenUri})"),
                    },
                },
                ConnectionStrings = {
                    new AzureNative.Web.Inputs.ConnStringInfoArgs
                    {
                        Name = "db",
                        Type = AzureNative.Web.ConnectionStringType.SQLAzure,
                        ConnectionString = Output.Format($"@Microsoft.KeyVault(SecretUri={ConnectionStringSecretUri})")
                    },
                },
                Cors = new AzureNative.Web.Inputs.CorsSettingsArgs
                {
                    AllowedOrigins = Output.Format($"{PublicWebAppEndpoint},{BlazorEndpoint}")
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));

        GiveWebAppAccessToKeyVault(apiApp, "api-app-policy");

        ApiEndpoint = Output.Format($"https://{apiApp.Apply(c => c.DefaultHostName)}");
    }

    private void SetupBlazorService()
    {
        var codeBlob = new AzureNative.Storage.Blob("Blazor-code", new AzureNative.Storage.BlobArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AccountName = StorageAccountName,
            ContainerName = CodeBlobContainer.Name,
            Type = AzureNative.Storage.BlobType.Block,
            Source = new FileArchive("..\\src\\Blazor\\bin\\Release\\net6.0\\publish"), //can be debug if we like!
        });

        BlazorCodeBlobEndpoint = SignedBlobReadUrl(codeBlob, CodeBlobContainer, StorageAccountName, ResourceGroup.Apply(t => t));

        // The application hosted in App Service
        var blazorApp = Output.Create(new AzureNative.Web.WebApp("Blazor", new AzureNative.Web.WebAppArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            ServerFarmId = AppServicePlan.Id,
            SiteConfig = new AzureNative.Web.Inputs.SiteConfigArgs
            {
                AppSettings = {
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "WEBSITE_RUN_FROM_PACKAGE",
                        Value = BlazorCodeBlobEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ASPNETCORE_ENVIRONMENT",
                        Value = Deployment.Instance.StackName.ToLower(),
                    },
                    //Abp.io
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:SelfUrl",
                        Value = BlazorEndpoint,
                    }
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));

        BlazorEndpoint = Output.Format($"https://{blazorApp.Apply(c => c.DefaultHostName)}");
    }

    private void SetupPublicWebService()
    {
        var codeBlob = new AzureNative.Storage.Blob("PublicWebService-code", new AzureNative.Storage.BlobArgs
        {
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            AccountName = StorageAccountName,
            ContainerName = CodeBlobContainer.Name,
            Type = AzureNative.Storage.BlobType.Block,
            Source = new FileArchive("..\\src\\Web.Public\\bin\\Release\\net6.0\\publish"), //can be debug if we like!
        });

        PublicWebCodeBlobEndpoint = SignedBlobReadUrl(codeBlob, CodeBlobContainer, StorageAccountName, ResourceGroup.Apply(t => t));

        // The application hosted in App Service
        var publicWebApp = Output.Create(new AzureNative.Web.WebApp("PublicWeb", new AzureNative.Web.WebAppArgs
        {
            // A system-assigned managed service identity to be used for authentication and authorization to the SQL Database and the Blob Storage
            Identity = new AzureNative.Web.Inputs.ManagedServiceIdentityArgs { Type = AzureNative.Web.ManagedServiceIdentityType.SystemAssigned },
            //KeyVaultReferenceIdentity = KeyVault. //?
            //Kind = "FunctionApp", <-- if this would be AzureFunction
            ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            Location = ResourceGroup.Apply(t => t.Location),
            ServerFarmId = AppServicePlan.Id,
            SiteConfig = new AzureNative.Web.Inputs.SiteConfigArgs
            {
                AppSettings = {
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "WEBSITE_RUN_FROM_PACKAGE",
                        Value = PublicWebCodeBlobEndpoint,
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ASPNETCORE_ENVIRONMENT",
                        Value = Deployment.Instance.StackName.ToLower(),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                        Value = ApplicationInsightsInstrumentationKey
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                        Value = ApplicationInsightsInstrumentationKey.Apply(key => $"InstrumentationKey={key}"),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                        Value = "~2",
                    },
                    //Abp.io
                     new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:SelfUrl",
                        Value = PublicWebAppEndpoint.Apply(c=>c),
                    },
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:BlazorUrl",
                        Value = BlazorEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "AuthServer:Authority",
                        Value = IdentityEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "RemoteServices:Default:BaseUrl",
                        Value = ApiEndpoint.Apply(c=>c),
                    },
                    new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "App:CorsOrigins",
                        Value = Output.Format($"{ApiEndpoint},{BlazorEndpoint}"),
                    },
                    // Redis
                   new AzureNative.Web.Inputs.NameValuePairArgs{
                        Name = "Redis:Configuration",
                        Value = Redis.AccessKeys.Apply(c=>c.PrimaryKey),
                    }
                },
                ConnectionStrings = {
                    new AzureNative.Web.Inputs.ConnStringInfoArgs
                    {
                        Name = "db",
                        Type = AzureNative.Web.ConnectionStringType.SQLAzure,
                        ConnectionString = Output.Format($"@Microsoft.KeyVault(SecretUri={ConnectionStringSecretUri})")
                    },
                },
                Cors = new AzureNative.Web.Inputs.CorsSettingsArgs
                {
                    AllowedOrigins = Output.Format($"{ApiEndpoint},{BlazorEndpoint}")
                },
            },
            Tags =
            {
                { "environment", StackName },
            },
        }));

        GiveWebAppAccessToKeyVault(publicWebApp, "publicweb-app-policy");

        PublicWebAppEndpoint = Output.Format($"https://{publicWebApp.Apply(c => c.DefaultHostName)}");

        if (Deployment.Instance.StackName.ToLower() == "production")
        {
            // My custom domain question https://pulumi-community.slack.com/archives/C01PF3E1B8V/p1641648951021600
            //TODO: Don´t I need this?  https://www.pulumi.com/registry/packages/azure-native/api-docs/appplatform/customdomain/
            //var customDomain = new AzureNative.AppPlatform.CustomDomain("publicCustomDomain", new AzureNative.AppPlatform.CustomDomainArgs
            //{
            //    AppName = publicWebApp.Name,
            //    DomainName = "MyTest.is",
            //    Properties = new AzureNative.AppPlatform.CustomDomainPropertiesArgs
            //    {
            //        CertName = CertificateOrder.Name,
            //        Thumbprint = CertificateOrder.SignedCertificateThumbprint, //There are other thumbprints on there.. no idea what to use (if any)!
            //    },
            //    ResourceGroupName = ResourceGroup.Apply(t => t.Name),
            //    ServiceName = "???",
            //});
        }
    }


    bool shouldBeProtected;
    public AzureTerraform.AppService.CertificateOrder CertificateOrder { get; set; }
    public AzureNative.Web.Certificate WebCertificate { get; set; }
    public AzureNative.Cdn.Profile CDNProfile { get; set; }
    public AzureNative.Sql.Database Database { get; set; }
    public AzureNative.Sql.Server SqlServer { get; set; }
    public Config Configuration { get; set; }
    public AzureNative.KeyVault.Vault KeyVault { get; set; }
    public AzureNative.Web.AppServicePlan AppServicePlan { get; set; }
    public AzureNative.Storage.BlobContainer CodeBlobContainer { get; set; }

    public AzureNative.Cache.Redis Redis { get; set; }
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
    [Output] public Output<string> ConnectionStringWithoutPassword { get; set; }
    [Output] public Output<string> ConnectionStringWithPassword { get; set; }
    [Output] public Output<string> ConnectionStringSecretUri { get; set; }
    [Output("currentSubscriptionId")]
    public Output<string> CurrentSubscriptionId { get; set; }
    [Output("currentSubscriptionDisplayName")]
    public Output<string> CurrentSubscriptionDisplayName { get; set; }
    [Output] public Output<string> StorageAccountId { get; set; }
    [Output] public Output<string> StorageAccountName { get; set; }
    [Output] public Output<string> ApplicationInsightsInstrumentationKey { get; set; }
    [Output] public Output<string> TenantId { get; set; }
    [Output] public Output<ResourceGroup> ResourceGroup { get; set; }
    [Output] public Output<string> IdentityEndpoint { get; set; }
    [Output] public Output<string> IdentityCodeBlobEndpoint { get; set; }
    [Output] public Output<string> ApiEndpoint { get; set; }
    [Output] public Output<string> ApiyCodeBlobEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppEndpoint { get; set; }
    [Output] public Output<string> PublicWebCodeBlobEndpoint { get; set; }
    [Output] public Output<string> PublicWebAppCDNEndpoint { get; set; }
    [Output] public Output<string> BlazorEndpoint { get; set; }
    [Output] public Output<string> BlazorCodeBlobEndpoint { get; set; }

    private void GiveWebAppAccessToKeyVault(Input<AzureNative.Web.WebApp> webApp, string accessPolicyName)
    {
        // Work around a preview issue https://github.com/pulumi/pulumi-azure/issues/192
        var principalId = webApp.Apply(c => c.Identity.Apply(id => id.PrincipalId ?? "11111111-1111-1111-1111-111111111111"));

        // Grant App Service access to KeyVault secrets
        var policy = new AzureTerraform.KeyVault.AccessPolicy(accessPolicyName, new AzureTerraform.KeyVault.AccessPolicyArgs
        {
            KeyVaultId = KeyVault.Id,
            TenantId = TenantId,
            ObjectId = principalId,
            SecretPermissions = { "get", "list" },
        });
    }

    private static Output<string> SignedBlobReadUrl(AzureNative.Storage.Blob blob, AzureNative.Storage.BlobContainer container, Input<string> accountName, Input<ResourceGroup> resourceGroup)
    {
        var serviceSasToken = AzureNative.Storage.ListStorageAccountServiceSAS.Invoke(new AzureNative.Storage.ListStorageAccountServiceSASInvokeArgs
        {
            AccountName = accountName,
            Protocols = AzureNative.Storage.HttpProtocol.Https,
            SharedAccessStartTime = "2021-01-01",
            SharedAccessExpiryTime = "2030-01-01",
            Resource = AzureNative.Storage.SignedResource.C,
            ResourceGroupName = resourceGroup.Apply(f => f.Name),
            Permissions = AzureNative.Storage.Permissions.R,
            CanonicalizedResource = Output.Format($"/blob/{accountName}/{container.Name}"),
            ContentType = "application/json",
            CacheControl = "max-age=5",
            ContentDisposition = "inline",
            ContentEncoding = "deflate",
        }).Apply(blobSAS => blobSAS.ServiceSasToken);

        return Output.Format($"https://{accountName}.blob.core.windows.net/{container.Name}/{blob.Name}?{serviceSasToken}");
    }
}