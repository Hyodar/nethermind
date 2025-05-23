// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(16L, 32L, DownloaderOptions.Process, 32, 32)]
    [TestCase(16L, 32L, DownloaderOptions.Process, 32, 29)]
    [TestCase(16L, 32L, DownloaderOptions.WithReceipts | DownloaderOptions.Insert, 0, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.WithReceipts | DownloaderOptions.Insert, 32, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.Process, 32, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.Process, 32, SyncBatchSize.Max * 8 - 16L)]
    public async Task Merge_Happy_path(long beaconPivot, long headNumber, int options, int threshold, long insertedBeaconBlocks)
    {
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = (downloaderOptions & DownloaderOptions.WithReceipts) != 0;
        int notSyncedTreeStartingBlockNumber = 3;

        InMemoryReceiptStorage? receiptStorage = withReceipts ? new InMemoryReceiptStorage() : null;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(notSyncedTreeStartingBlockNumber + 1, (int)headNumber + 1, receiptStorage: receiptStorage)
            .InsertBeaconPivot(beaconPivot)
            .InsertBeaconHeaders(notSyncedTreeStartingBlockNumber + 1, beaconPivot - 1)
            .InsertBeaconBlocks(beaconPivot + 1, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = "0"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None);

        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        SyncPeerMock syncPeer = new(syncedTree, withReceipts, responseOptions, 16000000, receiptStorage: receiptStorage);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await downloader.Dispatch(peerInfo, new BlocksRequest(downloaderOptions, threshold), CancellationToken.None);

        long expectedDownloadStart = notSyncedTreeStartingBlockNumber;
        long expectedDownloadEnd = insertedBeaconBlocks - threshold;

        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd));
        ctx.BlockTree.BestKnownNumber.Should().Be(Math.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd));

        int receiptCount = 0;
        for (long i = expectedDownloadStart; i < expectedDownloadEnd; i++)
        {
            if (i % 3 == 0)
            {
                receiptCount += 2;
            }
        }

        ctx.ReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
        ctx.BeaconPivot.ProcessDestination?.Number.Should().Be(Math.Max(insertedBeaconBlocks - threshold, beaconPivot));
    }

    [TestCase(32L, DownloaderOptions.Insert, 32, false)]
    [TestCase(32L, DownloaderOptions.Insert, 32, true)]
    public async Task Can_reach_terminal_block(long headNumber, int options, int threshold, bool withBeaconPivot)
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1, true, ttd)
            .InsertBeaconPivot(16)
            .InsertBeaconHeaders(4, 15)
            .InsertBeaconBlocks(17, headNumber, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        BlockDownloader downloader = ctx.BlockDownloader;

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await downloader.Dispatch(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        Assert.That(ctx.PosSwitcher.HasEverReachedTerminalBlock(), Is.True);
    }

    [TestCase(32L, DownloaderOptions.Insert, 16, false, 16)]
    [TestCase(32L, DownloaderOptions.Insert, 16, true, 3)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(long headNumber, int options, int ttdBlock, bool withBeaconPivot, int expectedBestKnownNumber)
    {
        UInt256 ttd = 10_000_000;
        int negativeTd = BlockHeaderBuilder.DefaultDifficulty.ToInt32(null);
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(
                4,
                (int)headNumber + 1,
                true,
                ttd,
                syncedSplitFrom: ttdBlock,
                syncedSplitVariant: negativeTd
            );
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        BlockDownloader downloader = ctx.BlockDownloader;

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await downloader.Dispatch(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        notSyncedTree.BestKnownNumber.Should().Be(expectedBestKnownNumber);
    }

    [TestCase(32L, 32L, 0, 32)]
    [TestCase(32L, 32L, 10, 22)]
    public async Task WillSkipBlocksToIgnore(long pivot, long headNumber, int blocksToIgnore, long expectedBestKnownNumber)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(pivot)
            .InsertBeaconHeaders(4, pivot - 1);

        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees);
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;

        SyncPeerMock syncPeer = new(syncedTree, false, responseOptions, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        BlocksRequest blocksRequest = new BlocksRequest(DownloaderOptions.Process, blocksToIgnore);
        await downloader.Dispatch(peerInfo, blocksRequest, CancellationToken.None);

        ctx.BlockTree.BestKnownNumber.Should().Be(Math.Max(0, expectedBestKnownNumber));
    }

    [Test]
    public async Task Recalculate_header_total_difficulty()
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(1, 4, true, ttd);

        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;

        blockTrees
            .InsertOtherChainToMain(notSyncedTree, 1, 3) // Need to have the header inserted to LRU which mean we need to move the head forward
            .InsertBeaconHeaders(1, 3, tdMode: BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns((info =>
        {
            BlockHeader header = (BlockHeader)info[0];
            // Simulate something calls find header on the header, causing the TD to get recalculated
            notSyncedTree.FindHeader(header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            return true;
        }));

        await using IContainer container = CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(notSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb)
                .AddSingleton<ISealValidator>(sealValidator);
        }, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        BlockHeader lastHeader = syncedTree.FindHeader(3, BlockTreeLookupOptions.None)!;
        // Because the FindHeader recalculated the TD.
        lastHeader.TotalDifficulty = 0;

        ctx.BeaconPivot.EnsurePivot(lastHeader);

        BlockDownloader downloader = ctx.BlockDownloader;

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        Block? lastBestSuggestedBlock = null;

        notSyncedTree.NewBestSuggestedBlock += (_, args) =>
        {
            lastBestSuggestedBlock = args.Block;
        };

        await downloader.Dispatch(peerInfo, new BlocksRequest(DownloaderOptions.Process | DownloaderOptions.WithReceipts), CancellationToken.None);

        lastBestSuggestedBlock!.Hash.Should().Be(lastHeader.Hash!);
        lastBestSuggestedBlock.TotalDifficulty.Should().NotBeEquivalentTo(UInt256.Zero);
    }

    [Test]
    public async Task Does_not_deadlock_on_replace_peer()
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(0, 4)
            .InsertBeaconPivot(3);

        ManualResetEventSlim chainLevelHelperBlocker = new ManualResetEventSlim(false);
        IChainLevelHelper chainLevelHelper = Substitute.For<IChainLevelHelper>();
        chainLevelHelper
            .When(clh => clh.GetNextHeaders(Arg.Any<int>(), Arg.Any<int>()))
            .Do(_ =>
            {
                chainLevelHelperBlocker.Wait();
            });

        await using IContainer container = CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IChainLevelHelper>(chainLevelHelper)

                .AddSingleton<IBlockTree>(blockTrees.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb);
        }, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{0}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(3, BlockTreeLookupOptions.None));

        IPeerAllocationStrategy peerAllocationStrategy = Substitute.For<IPeerAllocationStrategy>();

        // Setup a peer of any kind
        ISyncPeer syncPeer1 = Substitute.For<ISyncPeer>();
        syncPeer1.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 9999));

        // Setup so that first allocation goes to sync peer 1
        peerAllocationStrategy
            .Allocate(Arg.Any<PeerInfo?>(), Arg.Any<IEnumerable<PeerInfo>>(), Arg.Any<INodeStatsManager>(), Arg.Any<IBlockTree>())
            .Returns(new PeerInfo(syncPeer1));
        SyncPeerAllocation peerAllocation = new(peerAllocationStrategy, AllocationContexts.Blocks, null);
        peerAllocation.AllocateBestPeer(new List<PeerInfo>(), Substitute.For<INodeStatsManager>(), ctx.BlockTree);
        ctx.PeerPool
            .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>())
            .Returns(Task.FromResult(peerAllocation));

        // Need to be asleep at this time
        ctx.Feed.FallAsleep();

        CancellationTokenSource cts = new CancellationTokenSource();

        Task _ = ctx.Dispatcher.Start(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Feed should activate and allocate the first peer
        Task accidentalDeadlockTask = Task.Factory.StartNew(() => ctx.Feed.Activate(), TaskCreationOptions.LongRunning);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // At this point, chain level helper is block, we then trigger replaced.
        ISyncPeer syncPeer2 = Substitute.For<ISyncPeer>();
        syncPeer2.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.2", 9999));
        syncPeer2.HeadNumber.Returns(4);

        // It will now get replaced with syncPeer2
        peerAllocationStrategy.ClearSubstitute();
        peerAllocationStrategy
            .Allocate(Arg.Any<PeerInfo?>(), Arg.Any<IEnumerable<PeerInfo>>(), Arg.Any<INodeStatsManager>(), Arg.Any<IBlockTree>())
            .Returns(new PeerInfo(syncPeer2));
        peerAllocation.AllocateBestPeer(new List<PeerInfo>(), Substitute.For<INodeStatsManager>(), ctx.BlockTree);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Release it
        chainLevelHelperBlocker.Set();

        // Just making sure...
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.That(() => accidentalDeadlockTask.IsCompleted, Is.True.After(1000, 100));
        cts.Cancel();
        cts.Dispose();
    }

    [Test]
    public void No_old_bodies_and_receipts()
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, 129)
            .InsertBeaconPivot(64)
            .InsertBeaconHeaders(4, 128);
        BlockTree syncedTree = blockTrees.SyncedTree;

        using IContainer container = CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(blockTrees.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb);
        }, new SyncConfig()
        {
            FastSync = true,
            NonValidatorNode = true,
            DownloadBodiesInFastSync = false,
            DownloadReceiptsInFastSync = false
        });

        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(64, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 34000000);

        ctx.ConfigureBestPeer(syncPeer);

        SyncFeedComponent<BlocksRequest> fastSyncFeedComponent = ctx.FastSyncFeedComponent;
        fastSyncFeedComponent.Feed.Activate();

        CancellationTokenSource cts = new();
        Task _ = fastSyncFeedComponent.Dispatcher.Start(cts.Token);

        Assert.That(
            () => ctx.BlockTree.BestKnownNumber,
            Is.EqualTo(96).After(3000, 100)
        );

        cts.Cancel();
    }

    [TestCase(DownloaderOptions.WithReceipts)]
    [TestCase(DownloaderOptions.Insert)]
    [TestCase(DownloaderOptions.Process)]
    public async Task BlockDownloader_works_correctly_with_withdrawals(int options)
    {
        await using IContainer container = CreateMergeNode();
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.WithReceipts;
        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        int headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, withReceipts, responseOptions, true);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockHeaders(ci.ArgAt<long>(0), ci.ArgAt<int>(1), ci.ArgAt<int>(2), ci.ArgAt<CancellationToken>(3)));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockBodies(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await syncPeerInternal.GetReceipts(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));


        syncPeer.TotalDifficulty.Returns(_ => syncPeerInternal.TotalDifficulty);
        syncPeer.HeadHash.Returns(_ => syncPeerInternal.HeadHash);
        syncPeer.HeadNumber.Returns(_ => syncPeerInternal.HeadNumber);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        int threshold = 2;
        await downloader.Dispatch(peerInfo, new BlocksRequest(DownloaderOptions.Insert, threshold), CancellationToken.None);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, Math.Min(headNumber, headNumber - threshold)));

        syncPeerInternal.ExtendTree(chainLength * 2);
        Func<Task> action = async () => await downloader.Dispatch(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);

        await action.Should().NotThrowAsync();
    }

    [TestCase(2)]
    [TestCase(6)]
    [TestCase(34)]
    [TestCase(129)]
    [TestCase(1024)]
    public void BlockDownloader_does_not_stop_processing_when_main_chain_is_unknown(long pivot)
    {
        DownloaderOptions downloaderOptions = DownloaderOptions.Process;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
             .GoesLikeThis()
             .WithBlockTrees(1, (int)(pivot + 1), false, 0)
             .InsertBeaconPivot(pivot)
             .InsertBeaconHeaders(1, pivot)
             .InsertBeaconBlocks(pivot, pivot, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"0"
        });

        PostMergeContext ctx = container.Resolve<PostMergeContext>();
        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        SyncPeerMock syncPeer = new(blockTrees.SyncedTree, true, Response.AllCorrect | Response.WithTransactions, 0);
        Assert.DoesNotThrowAsync(() => ctx.BlockDownloader.Dispatch(new(syncPeer), new BlocksRequest(downloaderOptions), CancellationToken.None));
    }

    private IContainer CreateMergeNode(Action<ContainerBuilder>? configurer = null, params IConfig[] configs)
    {
        IConfigProvider configProvider = new ConfigProvider(configs);
        return CreateNode((builder) =>
        {
            builder
                .AddModule(new MergeModule(configProvider))
                .AddSingleton<PostMergeContext>();
            configurer?.Invoke(builder);
        }, configProvider);
    }

    private IContainer CreateMergeNode(BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder treeBuilder, params IConfig[] configs)
    {
        return CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(treeBuilder.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, treeBuilder.NotSyncedTreeBuilder.MetadataDb);
        }, configs);
    }

    private record PostMergeContext(
        IBeaconPivot BeaconPivot,
        IPoSSwitcher PosSwitcher,
        ResponseBuilder ResponseBuilder,
        [KeyFilter(nameof(FastSyncFeed))] SyncFeedComponent<BlocksRequest> FastSyncFeedComponent,
        [KeyFilter(nameof(FullSyncFeed))] SyncFeedComponent<BlocksRequest> FullSyncFeedComponent,
        IBlockTree BlockTree,
        InMemoryReceiptStorage ReceiptStorage,
        ISyncPeerPool PeerPool) : Context(
        ResponseBuilder,
        FastSyncFeedComponent,
        FullSyncFeedComponent,
        BlockTree,
        ReceiptStorage,
        PeerPool);
}
