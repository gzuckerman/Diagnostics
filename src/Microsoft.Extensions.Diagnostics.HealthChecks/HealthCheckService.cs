// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Diagnostics.HealthChecks
{
    internal class HealthCheckService : IHealthCheckService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HealthCheckService> _logger;

        public HealthCheckService(IServiceScopeFactory scopeFactory, ILogger<HealthCheckService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // We're specifically going out of our way to do this at startup time. We want to make sure you
            // get any kind of health-check related error as early as possible. Waiting until someone
            // actually tries to **run** health checks would be real baaaaad.
            using (var scope = _scopeFactory.CreateScope())
            {
                var healthChecks = scope.ServiceProvider.GetRequiredService<IEnumerable<IHealthCheck>>();
                EnsureNoDuplicates(healthChecks);
            }
        }

        public Task<CompositeHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            CheckHealthAsync(predicate: null, cancellationToken);

        public async Task<CompositeHealthCheckResult> CheckHealthAsync(
            Func<IHealthCheck, bool> predicate,
            CancellationToken cancellationToken = default)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var healthChecks = scope.ServiceProvider.GetRequiredService<IEnumerable<IHealthCheck>>();

                if (predicate != null)
                {
                    healthChecks = healthChecks.Where(predicate);
                }

                var results = new Dictionary<string, HealthCheckResult>(StringComparer.OrdinalIgnoreCase);
                foreach (var healthCheck in healthChecks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // If the health check does things like make Database queries using EF or backend HTTP calls,
                    // it may be valuable to know that logs it generates are part of a health check. So we start a scope.
                    using (_logger.BeginScope(new HealthCheckLogScope(healthCheck.Name)))
                    {
                        HealthCheckResult result;
                        try
                        {
                            _logger.LogTrace("Running health check: {healthCheckName}", healthCheck.Name);
                            result = await healthCheck.CheckHealthAsync(cancellationToken);
                            _logger.LogTrace("Health check '{healthCheckName}' completed with status '{healthCheckStatus}'", healthCheck.Name, result.Status);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Health check '{healthCheckName}' threw an unexpected exception", healthCheck.Name);
                            result = new HealthCheckResult(HealthCheckStatus.Failed, ex, ex.Message, data: null);
                        }

                        // This can only happen if the result is default(HealthCheckResult)
                        if (result.Status == HealthCheckStatus.Unknown)
                        {
                            // This is different from the case above. We throw here because a health check is doing something specifically incorrect.
                            var exception = new InvalidOperationException($"Health check '{healthCheck.Name}' returned a result with a status of Unknown");
                            _logger.LogError(exception, "Health check '{healthCheckName}' returned a result with a status of Unknown", healthCheck.Name);
                            throw exception;
                        }

                        results[healthCheck.Name] = result;
                    }
                }

                return new CompositeHealthCheckResult(results);
            }
        }

        private static void EnsureNoDuplicates(IEnumerable<IHealthCheck> healthChecks)
        {
            // Scan the list for duplicate names to provide a better error if there are duplicates.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            foreach (var check in healthChecks)
            {
                if (!names.Add(check.Name))
                {
                    duplicates.Add(check.Name);
                }
            }

            if (duplicates.Count > 0)
            {
                throw new ArgumentException($"Duplicate health checks were registered with the name(s): {string.Join(", ", duplicates)}", nameof(healthChecks));
            }
        }
    }
}
