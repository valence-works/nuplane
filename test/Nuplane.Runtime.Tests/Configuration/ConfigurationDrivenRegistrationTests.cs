using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Runtime.Configuration;
using Nuplane.Setup;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class ConfigurationDrivenRegistrationTests
{
    [Fact]
    public void AddNuplane_FromConfiguration_RegistersAutomaticSchedulerAndDispatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-config-registration", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        var stateFilePath = Path.Combine(root, "state.json");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:AutomaticReconciliation"] = "true",
                    ["Nuplane:Setup:PollInterval"] = "00:00:45",
                    ["Nuplane:Setup:StateFilePath"] = stateFilePath,
                    ["Nuplane:Setup:Feeds:0:Name"] = "local-packages",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath,
                    ["Nuplane:Setup:Feeds:0:IncludePatterns:0"] = "*",
                    ["Nuplane:Setup:Feeds:0:Directory:Watch"] = "false",
                    ["Nuplane:Setup:Feeds:0:Directory:DebounceWindow"] = "00:00:02",
                    ["Nuplane:Reconciliation:MaxRetryAttempts"] = "5",
                    ["Nuplane:StoreRegistry:StateFilePath"] = Path.Combine(root, "ignored-by-setup.json")
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            var hostedServiceTypes = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(descriptor => descriptor.ImplementationType)
                .Where(static type => type is not null)
                .ToArray();

            Assert.Equal(2, hostedServiceTypes.Length);
            Assert.Contains(typeof(ReconciliationHostedService), hostedServiceTypes);
            Assert.Contains(typeof(ReconciliationTriggerDispatcherHostedService), hostedServiceTypes);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_FromConfiguration_WithoutFilters_DoesNotAllowlistAnyPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-no-filter-config", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Empty(sourceTrust.AllowedPackageIds);
            Assert.Contains("drop-folder", sourceTrust.AllowedSourceNames);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_FromConfiguration_IncludeAllAlias_CollapsesAllowlistToWildcard()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-include-all-config", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath,
                    ["Nuplane:Setup:Feeds:0:IncludeAll"] = "true",
                    ["Nuplane:Setup:Feeds:1:Name"] = "nuget.org",
                    ["Nuplane:Setup:Feeds:1:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
                    ["Nuplane:Setup:Feeds:1:IncludePatterns:0"] = "Elsa.*"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Single(sourceTrust.AllowedPackageIds);
            Assert.Contains("*", sourceTrust.AllowedPackageIds);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AutoloadPackages_FromBuilder_EnablesLoadingByDefault_AndAppliesCodeConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.AutoloadPackages(load =>
            {
                load.WithDeactivationTimeout(TimeSpan.FromSeconds(12));
                load.SharedAssembly("Nuplane.Abstractions", "31bf3856ad364e35", 1);
            });
        });

        using var provider = services.BuildServiceProvider();

        var loading = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;

        Assert.True(loading.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(12), loading.DeactivationTimeout);

        var sharedAssembly = Assert.Single(loading.SharedAssemblies);
        Assert.Equal("Nuplane.Abstractions", sharedAssembly.Name);
        Assert.Equal("31bf3856ad364e35", sharedAssembly.PublicKeyToken);
        Assert.Equal(1, sharedAssembly.MajorVersion);
    }

    [Fact]
    public void AutoloadPackages_FromConfiguration_PreservesDisabledState_WhenNotOverridden()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Loading:Enabled"] = "false",
                ["Nuplane:Loading:DeactivationTimeout"] = "00:00:20"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
        {
            nuplane.AutoloadPackages(configuration.GetSection("Nuplane"));
        });

        using var provider = services.BuildServiceProvider();

        var loading = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;

        Assert.False(loading.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(20), loading.DeactivationTimeout);
    }

    [Fact]
    public void AutoloadPackages_FromConfiguration_BindsLoadingOptions_AndAllowsCodeOverride()
    {
        var activeStoreRoot = Path.Combine(Path.GetTempPath(), "nuplane-active-store", Guid.NewGuid().ToString("N"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Loading:Enabled"] = "false",
                ["Nuplane:Loading:DeactivationTimeout"] = "00:00:20",
                ["Nuplane:Loading:ActiveStoreRoot"] = activeStoreRoot,
                ["Nuplane:Loading:SharedAssemblies:0:Name"] = "Nuplane.Abstractions",
                ["Nuplane:Loading:SharedAssemblies:0:PublicKeyToken"] = "31bf3856ad364e35",
                ["Nuplane:Loading:SharedAssemblies:0:MajorVersion"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
        {
            nuplane.AutoloadPackages(configuration.GetSection("Nuplane"), load => load.Enable());
        });

        using var provider = services.BuildServiceProvider();

        var loading = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;

        Assert.True(loading.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(20), loading.DeactivationTimeout);
        Assert.Equal(activeStoreRoot, loading.ActiveStoreRoot);

        var sharedAssembly = Assert.Single(loading.SharedAssemblies);
        Assert.Equal("Nuplane.Abstractions", sharedAssembly.Name);
        Assert.Equal("31bf3856ad364e35", sharedAssembly.PublicKeyToken);
        Assert.Equal(1, sharedAssembly.MajorVersion);
    }

    [Fact]
    public async Task StoreRegistry_OnFirstAccess_LogsDefaultPathActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-activation-log", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var logMessages = new List<string>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logMessages)));
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var storeRegistry = provider.GetRequiredService<IStoreRegistry>();
            await storeRegistry.GetStateAsync(CancellationToken.None);

            Assert.Contains(logMessages, m => m.Contains("Store persistence activated with default path"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public async Task StoreRegistry_OnFirstAccess_LogsInMemoryModeActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-inmemory-log", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:UseInMemoryStore"] = "true",
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var logMessages = new List<string>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logMessages)));
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var storeRegistry = provider.GetRequiredService<IStoreRegistry>();
            await storeRegistry.GetStateAsync(CancellationToken.None);

            Assert.Contains(logMessages, m => m.Contains("Store persistence is disabled by configuration"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public async Task StoreRegistry_OnFirstAccess_LogsConfiguredPathActivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-configured-log", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        var stateFilePath = Path.Combine(root, "custom-state.json");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:StateFilePath"] = stateFilePath,
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var logMessages = new List<string>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logMessages)));
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var storeRegistry = provider.GetRequiredService<IStoreRegistry>();
            await storeRegistry.GetStateAsync(CancellationToken.None);

            Assert.Contains(logMessages, m => m.Contains("Store persistence activated with configured path"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_WithNoStateFilePath_ResolvesDefaultPersistenceMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-default-resolve", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var effectiveSettings = provider.GetRequiredService<EffectiveStorePersistenceSettings>();

            Assert.Equal(StorePersistenceMode.DefaultPath, effectiveSettings.Mode);
            Assert.NotNull(effectiveSettings.ResolvedStateFilePath);
            Assert.EndsWith(".nuplane/store-state.json",
                effectiveSettings.ResolvedStateFilePath!.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_WithExplicitPath_ResolvesConfiguredPersistenceMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-explicit-resolve", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        var stateFilePath = Path.Combine(root, "my-state.json");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:StateFilePath"] = stateFilePath,
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var effectiveSettings = provider.GetRequiredService<EffectiveStorePersistenceSettings>();

            Assert.Equal(StorePersistenceMode.ConfiguredPath, effectiveSettings.Mode);
            Assert.Equal(Path.GetFullPath(stateFilePath), effectiveSettings.ResolvedStateFilePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_SetupStateFilePathOverridesStoreRegistrySection()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-precedence", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        var setupPath = Path.Combine(root, "setup-state.json");
        var storeRegistryPath = Path.Combine(root, "store-registry-state.json");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:StateFilePath"] = setupPath,
                    ["Nuplane:StoreRegistry:StateFilePath"] = storeRegistryPath,
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var effectiveSettings = provider.GetRequiredService<EffectiveStorePersistenceSettings>();

            Assert.Equal(StorePersistenceMode.ConfiguredPath, effectiveSettings.Mode);
            Assert.Equal(Path.GetFullPath(setupPath), effectiveSettings.ResolvedStateFilePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_SetupUseInMemoryStore_ResolvesInMemoryMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-setup-inmemory", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:UseInMemoryStore"] = "true",
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var effectiveSettings = provider.GetRequiredService<EffectiveStorePersistenceSettings>();

            Assert.Equal(StorePersistenceMode.InMemory, effectiveSettings.Mode);
            Assert.Null(effectiveSettings.ResolvedStateFilePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_BuilderUseInMemoryStore_ResolvesInMemoryMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-builder-inmemory", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
            {
                nuplane.UseInMemoryStore();
            });

            using var provider = services.BuildServiceProvider();

            var effectiveSettings = provider.GetRequiredService<EffectiveStorePersistenceSettings>();

            Assert.Equal(StorePersistenceMode.InMemory, effectiveSettings.Mode);
            Assert.Null(effectiveSettings.ResolvedStateFilePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_BlankSetupStateFilePath_FailsOnStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-blank-path", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:StateFilePath"] = "  ",
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<NuplaneSetupOptions>>().Value);

            Assert.Contains("StateFilePath cannot be blank", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_UseInMemoryStoreWithStateFilePath_FailsOnStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-conflict-config", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:UseInMemoryStore"] = "true",
                    ["Nuplane:Setup:StateFilePath"] = "./state.json",
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<NuplaneSetupOptions>>().Value);

            Assert.Contains("UseInMemoryStore cannot be combined", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddNuplane_BlankStoreRegistryStateFilePath_FailsOnStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-blank-store-path", Guid.NewGuid().ToString("N"));
        var packagesPath = Path.Combine(root, "packages");
        Directory.CreateDirectory(packagesPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:StoreRegistry:StateFilePath"] = "  ",
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = packagesPath
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"));

            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<StoreRegistryOptions>>().Value);

            Assert.Contains("StateFilePath", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
