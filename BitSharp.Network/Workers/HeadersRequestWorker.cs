﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace BitSharp.Network.Workers
{
    internal class HeadersRequestWorker : Worker
    {
        private static readonly TimeSpan STALE_REQUEST_TIME = TimeSpan.FromSeconds(60);

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly LocalClient localClient;
        private readonly CoreDaemon coreDaemon;
        private readonly CoreStorage coreStorage;

        private readonly ConcurrentDictionary<Peer, DateTimeOffset> headersRequestsByPeer;

        private readonly WorkerMethod flushWorker;
        private readonly ConcurrentQueue<FlushHeaders> flushQueue;

        public HeadersRequestWorker(WorkerConfig workerConfig, LocalClient localClient, CoreDaemon coreDaemon)
            : base("HeadersRequestWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime)
        {
            this.localClient = localClient;
            this.coreDaemon = coreDaemon;
            this.coreStorage = coreDaemon.CoreStorage;

            this.headersRequestsByPeer = new ConcurrentDictionary<Peer, DateTimeOffset>();

            this.localClient.OnBlockHeaders += HandleBlockHeaders;
            this.coreDaemon.OnTargetChainChanged += HandleTargetChainChanged;

            this.flushWorker = new WorkerMethod("HeadersRequestWorker.FlushWorker", FlushWorkerMethod, initialNotify: true, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue);
            this.flushQueue = new ConcurrentQueue<FlushHeaders>();
        }

        protected override void SubDispose()
        {
            this.localClient.OnBlockHeaders -= HandleBlockHeaders;
            this.coreDaemon.OnTargetChainChanged -= HandleTargetChainChanged;

            this.flushWorker.Dispose();
        }

        protected override void SubStart()
        {
            this.flushWorker.Start();
        }

        protected override void SubStop()
        {
            this.flushWorker.Stop();
        }

        public Task SendGetHeaders(Peer peer)
        {
            if (!IsStarted)
                return Task.CompletedTask;

            var targetChainLocal = this.coreDaemon.TargetChain;
            if (targetChainLocal == null)
                return Task.CompletedTask;

            var blockLocatorHashes = CalculateBlockLocatorHashes(targetChainLocal.Blocks);

            // remove an existing request for this peer if it's stale
            var now = DateTimeOffset.Now;
            DateTimeOffset requestTime;
            if (this.headersRequestsByPeer.TryGetValue(peer, out requestTime)
                && (now - requestTime) > STALE_REQUEST_TIME)
            {
                this.headersRequestsByPeer.TryRemove(peer, out requestTime);
            }

            // determine if a new request can be made
            if (this.headersRequestsByPeer.TryAdd(peer, now))
                // send out the request for headers
                return peer.Sender.SendGetHeaders(blockLocatorHashes, hashStop: UInt256.Zero);
            else
                return Task.CompletedTask;
        }

        protected override Task WorkAction()
        {
            var now = DateTimeOffset.Now;
            var requestTasks = new List<Task>();

            var peerCount = this.localClient.ConnectedPeers.Count;
            var targetChainLocal = this.coreDaemon.TargetChain;

            if (peerCount == 0 || targetChainLocal == null)
                return Task.CompletedTask;

            var blockLocatorHashes = CalculateBlockLocatorHashes(targetChainLocal.Blocks);

            // remove any stale requests from the peer's list of requests
            this.headersRequestsByPeer.RemoveWhere(x => !x.Key.IsConnected || (now - x.Value) > STALE_REQUEST_TIME);

            // loop through each connected peer
            var requestCount = 0;
            var connectedPeers = this.localClient.ConnectedPeers.SafeToList();
            connectedPeers.Shuffle();
            foreach (var peer in connectedPeers)
            {
                // determine if a new request can be made
                if (this.headersRequestsByPeer.TryAdd(peer, now))
                {
                    // send out the request for headers
                    requestTasks.Add(peer.Sender.SendGetHeaders(blockLocatorHashes, hashStop: UInt256.Zero));

                    // only send out a few header requests at a time
                    requestCount++;
                    if (requestCount >= 2)
                        break;
                }
            }

            return Task.CompletedTask;
        }

        private Task FlushWorkerMethod(WorkerMethod instance)
        {
            FlushHeaders flushHeaders;
            while (this.flushQueue.TryDequeue(out flushHeaders))
            {
                // cooperative loop
                this.ThrowIfCancelled();

                var peer = flushHeaders.Peer;
                var blockHeaders = flushHeaders.Headers;

                // chain the downloaded headers
                this.coreStorage.ChainHeaders(blockHeaders);

                DateTimeOffset ignore;
                this.headersRequestsByPeer.TryRemove(peer, out ignore);
            }

            return Task.CompletedTask;
        }

        private void HandleBlockHeaders(Peer peer, IImmutableList<BlockHeader> blockHeaders)
        {
            if (blockHeaders.Count > 0)
            {
                this.flushQueue.Enqueue(new FlushHeaders(peer, blockHeaders));
                this.flushWorker.NotifyWork();
            }
            else
            {
                DateTimeOffset ignore;
                this.headersRequestsByPeer.TryRemove(peer, out ignore);
            }
        }

        private void HandleTargetChainChanged(object sender, EventArgs e)
        {
            this.NotifyWork();
        }

        private static ImmutableArray<UInt256> CalculateBlockLocatorHashes(IImmutableList<ChainedHeader> blockHashes)
        {
            var blockLocatorHashes = ImmutableArray.CreateBuilder<UInt256>();

            if (blockHashes.Count > 0)
            {
                var step = 1;
                var start = 0;
                for (var i = blockHashes.Count - 1; i > 0; i -= step, start++)
                {
                    if (start >= 10)
                        step *= 2;

                    blockLocatorHashes.Add(blockHashes[i].Hash);
                }
                blockLocatorHashes.Add(blockHashes[0].Hash);
            }

            return blockLocatorHashes.ToImmutable();
        }

        private sealed class FlushHeaders
        {
            public FlushHeaders(Peer peer, IImmutableList<BlockHeader> headers)
            {
                Peer = peer;
                Headers = headers;
            }

            public Peer Peer { get; }

            public IImmutableList<BlockHeader> Headers { get; }
        }
    }
}
