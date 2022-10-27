// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game.Online.API;

namespace osu.Game.Online
{
    public abstract class SocketClientConnector : Component
    {
        /// <summary>
        /// Whether this is connected to the hub, use <see cref="CurrentConnection"/> to access the connection, if this is <c>true</c>.
        /// </summary>
        public IBindable<bool> IsConnected => isConnected;

        /// <summary>
        /// The current connection opened by this connector.
        /// </summary>
        public SocketClient? CurrentConnection { get; private set; }

        private readonly Bindable<bool> isConnected = new Bindable<bool>();
        private readonly SemaphoreSlim connectionLock = new SemaphoreSlim(1);
        private CancellationTokenSource connectCancelSource = new CancellationTokenSource();

        private readonly IBindable<APIState> apiState = new Bindable<APIState>();

        /// <summary>
        /// Constructs a new <see cref="HubClientConnector"/>.
        /// </summary>
        /// <param name="api"> An API provider used to react to connection state changes.</param>
        protected SocketClientConnector(IAPIProvider api)
        {
            apiState.BindTo(api.State);
            apiState.BindValueChanged(_ => Task.Run(connectIfPossible), true);
        }

        public Task Reconnect()
        {
            Logger.Log($"{ClientName} reconnecting...", LoggingTarget.Network);
            return Task.Run(connectIfPossible);
        }

        private async Task connectIfPossible()
        {
            switch (apiState.Value)
            {
                case APIState.Failing:
                case APIState.Offline:
                    await disconnect(true);
                    break;

                case APIState.Online:
                    await connect();
                    break;
            }
        }

        private async Task connect()
        {
            cancelExistingConnect();

            if (!await connectionLock.WaitAsync(10000).ConfigureAwait(false))
                throw new TimeoutException("Could not obtain a lock to connect. A previous attempt is likely stuck.");

            try
            {
                while (apiState.Value == APIState.Online)
                {
                    // ensure any previous connection was disposed.
                    // this will also create a new cancellation token source.
                    await disconnect(false).ConfigureAwait(false);

                    // this token will be valid for the scope of this connection.
                    // if cancelled, we can be sure that a disconnect or reconnect is handled elsewhere.
                    var cancellationToken = connectCancelSource.Token;

                    cancellationToken.ThrowIfCancellationRequested();

                    Logger.Log($"{ClientName} connecting...", LoggingTarget.Network);

                    try
                    {
                        // importantly, rebuild the connection each attempt to get an updated access token.
                        CurrentConnection = await BuildConnectionAsync(cancellationToken).ConfigureAwait(false);
                        CurrentConnection.Closed += ex => onConnectionClosed(ex, cancellationToken);

                        cancellationToken.ThrowIfCancellationRequested();

                        await CurrentConnection.StartAsync(cancellationToken).ConfigureAwait(false);

                        Logger.Log($"{ClientName} connected!", LoggingTarget.Network);
                        isConnected.Value = true;
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        //connection process was cancelled.
                        throw;
                    }
                    catch (Exception e)
                    {
                        await handleErrorAndDelay(e, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                connectionLock.Release();
            }
        }

        /// <summary>
        /// Handles an exception and delays an async flow.
        /// </summary>
        private async Task handleErrorAndDelay(Exception exception, CancellationToken cancellationToken)
        {
            Logger.Log($"{ClientName} connect attempt failed: {exception.Message}", LoggingTarget.Network);
            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        }

        protected abstract Task<SocketClient> BuildConnectionAsync(CancellationToken cancellationToken);

        private async Task onConnectionClosed(Exception? ex, CancellationToken cancellationToken)
        {
            isConnected.Value = false;

            if (ex != null)
                await handleErrorAndDelay(ex, cancellationToken).ConfigureAwait(false);
            else
                Logger.Log($"{ClientName} disconnected", LoggingTarget.Network);

            // make sure a disconnect wasn't triggered (and this is still the active connection).
            if (!cancellationToken.IsCancellationRequested)
                await Task.Run(connect, default).ConfigureAwait(false);
        }

        private async Task disconnect(bool takeLock)
        {
            cancelExistingConnect();

            if (takeLock)
            {
                if (!await connectionLock.WaitAsync(10000).ConfigureAwait(false))
                    throw new TimeoutException("Could not obtain a lock to disconnect. A previous attempt is likely stuck.");
            }

            try
            {
                if (CurrentConnection != null)
                    await CurrentConnection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                CurrentConnection = null;
                if (takeLock)
                    connectionLock.Release();
            }
        }

        private void cancelExistingConnect()
        {
            connectCancelSource.Cancel();
            connectCancelSource = new CancellationTokenSource();
        }

        protected virtual string ClientName => GetType().ReadableName();

        public override string ToString() => $"{ClientName} ({(IsConnected.Value ? "connected" : "not connected")})";

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            apiState.UnbindAll();
            cancelExistingConnect();
        }
    }
}
