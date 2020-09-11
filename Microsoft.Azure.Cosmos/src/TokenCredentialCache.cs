//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Azure;
    using global::Azure.Core;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Diagnostics;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Documents;

    internal sealed class TokenCredentialCache : IDisposable
    {
        private const string ScopeFormat = "https://{0}/.default";
        private readonly TokenRequestContext tokenRequestContext;
        private readonly TokenCredential tokenCredential;
        private readonly TimeSpan backgroundTokenCredentialRefreshInterval;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly CancellationToken cancellationToken;
        private readonly SemaphoreSlim semaphoreSlim;

        private AccessToken cachedAccessToken;
        private bool isDisposed;

        internal TokenCredentialCache(
            TokenCredential tokenCredential,
            string accountEndpointHost,
            TimeSpan backgroundTokenCredentialRefreshInterval)
        {
            this.tokenCredential = tokenCredential;
            this.tokenRequestContext = new TokenRequestContext(new string[]
            {
                string.Format(TokenCredentialCache.ScopeFormat, accountEndpointHost)
            });

            this.backgroundTokenCredentialRefreshInterval = backgroundTokenCredentialRefreshInterval;
            this.cancellationTokenSource = new CancellationTokenSource();
            this.cancellationToken = this.cancellationTokenSource.Token;
            this.semaphoreSlim = new SemaphoreSlim(1, 1);
            this.isDisposed = false;
            this.StartRefreshToken();
        }

        internal async ValueTask<string> GetTokenAsync(
            CosmosDiagnosticsContext diagnosticsContext)
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException("TokenCredentialCache");
            }

            if (this.cachedAccessToken.ExpiresOn <= DateTime.UtcNow)
            {
                await this.RefreshCachedTokenWithRetryHelperAsync(diagnosticsContext);
            }

            return this.cachedAccessToken.Token;
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
            this.isDisposed = true;
        }

        private async ValueTask<AccessToken> RefreshCachedTokenWithRetryHelperAsync(
            CosmosDiagnosticsContext diagnosticsContext)
        {
            await this.semaphoreSlim.WaitAsync(this.cancellationToken);
            try
            {
                List<Exception> exceptions = null;
                Exception lastException;
                int totalRetryCount = 3;
                for (int retry = 0; retry < totalRetryCount; retry++)
                {
                    if (this.cancellationToken.IsCancellationRequested)
                    {
                        DefaultTrace.TraceInformation(
                            "Stop RefreshTokenWithIndefiniteRetries because cancellation is requested");

                        break;
                    }

                    try
                    {
                        using (diagnosticsContext.CreateScope(nameof(this.RefreshCachedTokenWithRetryHelperAsync)))
                        {
                            this.cachedAccessToken = await this.tokenCredential.GetTokenAsync(
                                this.tokenRequestContext,
                                this.cancellationToken);
                            return this.cachedAccessToken;
                        }
                    }
                    catch (RequestFailedException requestFailedException) 
                    {
                        diagnosticsContext.AddDiagnosticsInternal(
                            new PointOperationStatistics(
                                activityId: Trace.CorrelationManager.ActivityId.ToString(),
                                statusCode: (HttpStatusCode)requestFailedException.Status,
                                subStatusCode: SubStatusCodes.Unknown,
                                responseTimeUtc: DateTime.UtcNow,
                                requestCharge: default,
                                errorMessage: requestFailedException.ToString(),
                                method: default,
                                requestUri: null,
                                requestSessionToken: default,
                                responseSessionToken: default));

                        // Don't retry on auth failures
                        if (requestFailedException.Status == 401 ||
                            requestFailedException.Status == 403)
                        {
                            throw;
                        }

                        lastException = requestFailedException;
                    }
                    catch (Exception exception)
                    {
                        diagnosticsContext.AddDiagnosticsInternal(
                            new PointOperationStatistics(
                                activityId: Trace.CorrelationManager.ActivityId.ToString(),
                                statusCode: HttpStatusCode.InternalServerError,
                                subStatusCode: SubStatusCodes.Unknown,
                                responseTimeUtc: DateTime.UtcNow,
                                requestCharge: default,
                                errorMessage: exception.ToString(),
                                method: default,
                                requestUri: default,
                                requestSessionToken: default,
                                responseSessionToken: default));

                        lastException = exception;
                    }

                    DefaultTrace.TraceError(
                        $"TokenCredential.GetToken() failed. scope = {string.Join(";", this.tokenRequestContext.Scopes)}, retry = {retry}, Exception = {lastException}");

                    try
                    {
                        //await Task.Delay(
                        //    TimeSpan.FromMilliseconds(100),
                        //    this.cancellationToken);

                        if (exceptions == null)
                        {
                            exceptions = new List<Exception>(totalRetryCount);
                        }

                        exceptions.Add(lastException);
                    }
                    catch (Exception e)
                    {
                        string m = e.ToString();
                        Console.WriteLine(m);
                        throw;
                    }
                }

                throw CosmosExceptionFactory.CreateUnauthorizedException(
                    ClientResources.FailedToGetAadToken,
                    (int)SubStatusCodes.FailedToGetAadToken,
                    exceptions.Count == 1 ? exceptions[0] : new AggregateException(exceptions));
            }
            finally
            {
                this.semaphoreSlim.Release();
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void StartRefreshToken()
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            while (!this.cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(this.backgroundTokenCredentialRefreshInterval, this.cancellationToken);

                    DefaultTrace.TraceInformation("StartRefreshToken() - Invoking refresh");

                    await this.RefreshCachedTokenWithRetryHelperAsync(EmptyCosmosDiagnosticsContext.Singleton);
                }
                catch (Exception ex)
                {
                    if (this.cancellationTokenSource.IsCancellationRequested &&
                        (ex is TaskCanceledException || ex is ObjectDisposedException))
                    {
                        return;
                    }

                    DefaultTrace.TraceCritical(
                        "StartRefreshToken() - Unable to refresh token credential cache. Exception: {0}",
                        ex.ToString());
                }
            }
        }
    }
}
