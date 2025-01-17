﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.AspNetCore;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.ApplicationInsights.Extensibility.Implementation.ApplicationId;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.QuickPulse;
using Microsoft.ApplicationInsights.Extensibility.W3C;
using Microsoft.ApplicationInsights.SnapshotCollector;
using Microsoft.ApplicationInsights.WindowsServer;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    internal static class ApplicationInsightsServiceCollectionExtensions
    {
        public static IServiceCollection AddApplicationInsights(this IServiceCollection services, Action<ApplicationInsightsLoggerOptions> configure)
        {
            services.AddApplicationInsights();
            if (configure != null)
            {
                services.Configure<ApplicationInsightsLoggerOptions>(configure);
            }
            return services;
        }

        public static IServiceCollection AddApplicationInsights(this IServiceCollection services)
        {
            services.TryAddSingleton<ISdkVersionProvider, WebJobsSdkVersionProvider>();

            // Bind to the configuration section registered with 
            services.AddOptions<ApplicationInsightsLoggerOptions>()
                .Configure<ILoggerProviderConfiguration<ApplicationInsightsLoggerProvider>>((options, config) =>
                {
                    config.Configuration?.Bind(options);
                });

            services.AddSingleton<ITelemetryInitializer, HttpDependenciesParsingTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsRoleEnvironmentTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsSanitizingInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, MetricSdkVersionTelemetryInitializer>();
            services.AddSingleton<QuickPulseInitializationScheduler>();
            services.AddSingleton<QuickPulseTelemetryModule>();

            services.AddSingleton<ITelemetryModule>(provider =>
            {
                ApplicationInsightsLoggerOptions options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;
                if (options.EnableLiveMetrics)
                {
                    return provider.GetService<QuickPulseTelemetryModule>();
                }
                return NullTelemetryModule.Instance;
            });

            services.AddSingleton<ITelemetryModule>(provider =>
            {
                ApplicationInsightsLoggerOptions options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;
                if (options.EnablePerformanceCountersCollection)
                {
                    return new PerformanceCollectorModule
                    {
                        // Disabling this can improve cold start times
                        EnableIISExpressPerformanceCounters = false
                    };
                }

                return NullTelemetryModule.Instance;
            });

            services.AddSingleton<IApplicationIdProvider, ApplicationInsightsApplicationIdProvider>();

            services.AddSingleton<ITelemetryModule>(provider =>
            {
                var options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;

                if (options.EnableDependencyTracking)
                {
                    var dependencyCollector = new DependencyTrackingTelemetryModule();
                    var excludedDomains = dependencyCollector.ExcludeComponentCorrelationHttpHeadersOnDomains;
                    excludedDomains.Add("core.windows.net");
                    excludedDomains.Add("core.chinacloudapi.cn");
                    excludedDomains.Add("core.cloudapi.de");
                    excludedDomains.Add("core.usgovcloudapi.net");
                    excludedDomains.Add("localhost");
                    excludedDomains.Add("127.0.0.1");

                    var includedActivities = dependencyCollector.IncludeDiagnosticSourceActivities;
                    includedActivities.Add("Microsoft.Azure.ServiceBus");

                    dependencyCollector.EnableW3CHeadersInjection = options.HttpAutoCollectionOptions.EnableW3CDistributedTracing;
                    return dependencyCollector;
                }

                return NullTelemetryModule.Instance;
            });

            services.AddSingleton<ITelemetryModule>(provider =>
            {
                var options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;
                if (options.HttpAutoCollectionOptions.EnableHttpTriggerExtendedInfoCollection)
                {
                    var appIdProvider = provider.GetService<IApplicationIdProvider>();

                    return new RequestTrackingTelemetryModule(appIdProvider)
                    {
                        CollectionOptions = new RequestCollectionOptions
                        {
                            TrackExceptions = false, // webjobs/functions track exceptions themselves
                            EnableW3CDistributedTracing = options.HttpAutoCollectionOptions.EnableW3CDistributedTracing,
                            InjectResponseHeaders = options.HttpAutoCollectionOptions.EnableResponseHeaderInjection
                        }
                    };
                }

                return NullTelemetryModule.Instance;
            });

            services.AddSingleton<ITelemetryModule, AppServicesHeartbeatTelemetryModule>();

            services.AddSingleton<ITelemetryChannel, ServerTelemetryChannel>();
            services.AddSingleton<TelemetryConfiguration>(provider =>
            {
                ApplicationInsightsLoggerOptions options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;
                LoggerFilterOptions filterOptions = CreateFilterOptions(provider.GetService<IOptions<LoggerFilterOptions>>().Value);

                ITelemetryChannel channel = provider.GetService<ITelemetryChannel>();
                TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();

                IApplicationIdProvider appIdProvider = provider.GetService<IApplicationIdProvider>();
                ISdkVersionProvider sdkVersionProvider = provider.GetService<ISdkVersionProvider>();
                // Because of https://github.com/Microsoft/ApplicationInsights-dotnet-server/issues/943
                // we have to touch (and create) Active configuration before initializing telemetry modules
                // Active configuration is used to report AppInsights heartbeats
                // role environment telemetry initializer is needed to correlate heartbeats to particular host

                var activeConfig = TelemetryConfiguration.Active;
                if (!string.IsNullOrEmpty(options.InstrumentationKey) &&
                string.IsNullOrEmpty(activeConfig.InstrumentationKey))
                {
                    activeConfig.InstrumentationKey = options.InstrumentationKey;
                }

                if (!activeConfig.TelemetryInitializers.OfType<WebJobsRoleEnvironmentTelemetryInitializer>().Any())
                {
                    activeConfig.TelemetryInitializers.Add(new WebJobsRoleEnvironmentTelemetryInitializer());
                    activeConfig.TelemetryInitializers.Add(new WebJobsTelemetryInitializer(sdkVersionProvider));
                    if (options.HttpAutoCollectionOptions.EnableW3CDistributedTracing)
                    {
                        // W3C distributed tracing is enabled by the feature flag inside ApplicationInsights SDK
                        // W3COperationCorrelationTelemetryInitializer will go away once W3C is implemented
                        // in the DiagnosticSource (.NET)

                        TelemetryConfiguration.Active.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
                    }
                }

                SetupTelemetryConfiguration(
                    config,
                    options,
                    channel,
                    provider.GetServices<ITelemetryInitializer>(),
                    provider.GetServices<ITelemetryModule>(),
                    appIdProvider,
                    filterOptions,
                    provider.GetService<QuickPulseInitializationScheduler>());

                return config;
            });

            services.AddSingleton<TelemetryClient>(provider =>
            {
                TelemetryConfiguration configuration = provider.GetService<TelemetryConfiguration>();
                TelemetryClient client = new TelemetryClient(configuration);

                ISdkVersionProvider versionProvider = provider.GetService<ISdkVersionProvider>();
                client.Context.GetInternalContext().SdkVersion = versionProvider?.GetSdkVersion();

                return client;
            });

            services.AddSingleton<ILoggerProvider, ApplicationInsightsLoggerProvider>();

            return services;
        }

        internal static LoggerFilterOptions CreateFilterOptions(LoggerFilterOptions registeredOptions)
        {
            // We want our own copy of the rules, excluding the 'allow-all' rule that we added for this provider.
            LoggerFilterOptions customFilterOptions = new LoggerFilterOptions
            {
                MinLevel = registeredOptions.MinLevel
            };

            ApplicationInsightsLoggerFilterRule allowAllRule = registeredOptions.Rules.OfType<ApplicationInsightsLoggerFilterRule>().Single();

            // Copy all existing rules
            foreach (LoggerFilterRule rule in registeredOptions.Rules)
            {
                if (rule != allowAllRule)
                {
                    customFilterOptions.Rules.Add(rule);
                }
            }

            // Copy 'hidden' rules
            foreach (LoggerFilterRule rule in allowAllRule.ChildRules)
            {
                customFilterOptions.Rules.Add(rule);
            }

            return customFilterOptions;
        }

        private static void SetupTelemetryConfiguration(
            TelemetryConfiguration configuration,
            ApplicationInsightsLoggerOptions options,
            ITelemetryChannel channel,
            IEnumerable<ITelemetryInitializer> telemetryInitializers,
            IEnumerable<ITelemetryModule> telemetryModules,
            IApplicationIdProvider applicationIdProvider,
            LoggerFilterOptions filterOptions,
            QuickPulseInitializationScheduler delayer)
        {
            if (options.InstrumentationKey != null)
            {
                configuration.InstrumentationKey = options.InstrumentationKey;
            }

            if (options.HttpAutoCollectionOptions.EnableW3CDistributedTracing)
            {
                // W3C distributed tracing is enabled by the feature flag inside ApplicationInsights SDK
                // W3COperationCorrelationTelemetryInitializer will go away once W3C is implemented
                // in the DiagnosticSource (.NET)
                configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            }

            configuration.TelemetryChannel = channel;

            foreach (ITelemetryInitializer initializer in telemetryInitializers)
            {
                configuration.TelemetryInitializers.Add(initializer);
            }

            (channel as ServerTelemetryChannel)?.Initialize(configuration);

            QuickPulseTelemetryModule quickPulseModule = null;
            foreach (ITelemetryModule module in telemetryModules)
            {
                if (module is QuickPulseTelemetryModule telemetryModule)
                {
                    quickPulseModule = telemetryModule;
                    if (options.LiveMetricsAuthenticationApiKey != null)
                    {
                        quickPulseModule.AuthenticationApiKey = options.LiveMetricsAuthenticationApiKey;
                    }

                    // QuickPulse can have a startup performance hit, so delay its initialization.
                    delayer.ScheduleInitialization(() => module.Initialize(configuration), options.LiveMetricsInitializationDelay);
                }
                else if (module != null)
                {
                    module.Initialize(configuration);
                }
            }

            QuickPulseTelemetryProcessor quickPulseProcessor = null;
            configuration.TelemetryProcessorChainBuilder
                .Use((next) => new OperationFilteringTelemetryProcessor(next))
                .Use((next) =>
                {
                    quickPulseProcessor = new QuickPulseTelemetryProcessor(next);
                    return quickPulseProcessor;
                })
                .Use((next) => new FilteringTelemetryProcessor(filterOptions, next));

            if (options.SamplingSettings != null)
            {
                configuration.TelemetryProcessorChainBuilder.Use((next) =>
                {
                    var processor = new AdaptiveSamplingTelemetryProcessor(options.SamplingSettings, null, next);
                    if (options.SamplingExcludedTypes != null)
                    {
                        processor.ExcludedTypes = options.SamplingExcludedTypes;
                    }
                    if (options.SamplingIncludedTypes != null)
                    {
                        processor.IncludedTypes = options.SamplingIncludedTypes;
                    }
                    return processor;
                });
            }

            if (options.SnapshotConfiguration != null)
            {
                configuration.TelemetryProcessorChainBuilder.UseSnapshotCollector(options.SnapshotConfiguration);
            }

            configuration.TelemetryProcessorChainBuilder.Build();
            quickPulseModule?.RegisterTelemetryProcessor(quickPulseProcessor);

            foreach (ITelemetryProcessor processor in configuration.TelemetryProcessors)
            {
                if (processor is ITelemetryModule module)
                {
                    module.Initialize(configuration);
                }
            }

            configuration.ApplicationIdProvider = applicationIdProvider;
        }
    }
}