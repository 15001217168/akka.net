using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Transport.Helios;

namespace Akka.Remote.Transport.Streaming
{
    public class StreamTransportSettings
    {
        public Config Config { get; }

        public int StreamWriteBufferSize { get; }

        public int StreamReadBufferSize { get; }

        public int MaximumFrameSize { get; }

        public TimeSpan FlushWaitTimeout { get; }

        public int ChunkedReadThreshold { get; }

        public int FrameSizeHardLimit { get; }

        public StreamTransportSettings(Config config)
        {
            Config = config;

            StreamWriteBufferSize = GetByteSize(config, "stream-write-buffer-size");
            StreamReadBufferSize = GetByteSize(config, "stream-read-buffer-size");
            MaximumFrameSize = GetByteSize(config, "maximum-frame-size", 32000);
            FlushWaitTimeout = config.GetTimeSpan("flush-wait-on-shutdown");
            ChunkedReadThreshold = GetByteSize(config, "chunked-read-threshold");
            FrameSizeHardLimit = GetByteSize(config, "frame-size-hard-limit", 32000);
        }

        internal StreamTransportSettings(HeliosTransportSettings heliosSettings)
        {
            Config = heliosSettings.Config;

            StreamWriteBufferSize = 4096;
            StreamReadBufferSize = 65536;
            MaximumFrameSize = heliosSettings.MaxFrameSize;
            FlushWaitTimeout = TimeSpan.FromSeconds(2);
            ChunkedReadThreshold = 4096;
            FrameSizeHardLimit = 67108864;
        }

        protected static int GetByteSize(Config config, string path, int minValue = 0, int maxValue = int.MaxValue)
        {
            long? option = config.GetByteSize(path);

            if (option == null)
                throw new ConfigurationException($"Setting '{path}' is missing.");

            long size = option.Value;
            if (size < minValue)
                throw new ConfigurationException($"Setting '{path}' must be at least '{minValue}'.");

            if (size > maxValue)
                throw new ConfigurationException($"Setting '{path}' must be smaller than '{maxValue}'.");

            return (int)size;
        }
    }

    public abstract class StreamTransport : Transport
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly HashSet<StreamAssociationHandle> _associations = new HashSet<StreamAssociationHandle>();

        protected StreamTransportSettings Settings { get; }

        protected Address InboundAddress { get; private set; }

        protected CancellationToken ShutdownToken => _cancellation.Token;

        public override long MaximumPayloadBytes => Settings.MaximumFrameSize;

        protected StreamTransport(ActorSystem system, StreamTransportSettings settings)
        {
            _cancellation = new CancellationTokenSource();

            System = system;
            Config = settings.Config;
            Settings = settings;
        }

        public sealed override Task<Tuple<Address, TaskCompletionSource<IAssociationEventListener>>> Listen()
        {
            TaskCompletionSource<IAssociationEventListener> completion = new TaskCompletionSource<IAssociationEventListener>();

            InboundAddress = Initialize();

            completion.Task.ContinueWith(task =>
            {
                StartAcceptingConnections(task.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            return Task.FromResult(Tuple.Create(InboundAddress, completion));
        }

        protected abstract Address Initialize();

        protected abstract void StartAcceptingConnections(IAssociationEventListener listener);

        public override bool IsResponsibleFor(Address remote)
        {
            return true;
        }

        public sealed override async Task<bool> Shutdown()
        {
            _cancellation.Cancel();
            bool gracefulShutdown = await ShutdownAssociations();
            Cleanup();
            return gracefulShutdown;
        }

        public void RegisterAssociation(StreamAssociationHandle association)
        {
            lock (_associations)
            {
                _associations.Add(association);
            }

            association.Stopped.ContinueWith(_ =>
            {
                lock (_associations)
                {
                    _associations.Remove(association);
                }
            }, ShutdownToken);
        }

        private async Task<bool> ShutdownAssociations()
        {
            StreamAssociationHandle[] associations;
            lock (_associations)
            {
                associations = _associations.ToArray();
                _associations.Clear();
            }

            var tasks = associations.Select(item => item.Stopped).ToArray();
            var results = await Task.WhenAll(tasks);

            return results.All(flushSucceeded => flushSucceeded);
        }

        protected abstract void Cleanup();
    }
}