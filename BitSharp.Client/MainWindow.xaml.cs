﻿using BitSharp.Client.Helper;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage.Memory;
using BitSharp.Esent;
using BitSharp.LevelDb;
using BitSharp.Node;
using BitSharp.Node.Storage.Memory;
using BitSharp.Node.Workers;
using BitSharp.Wallet;
using BitSharp.Wallet.Address;
using Ninject;
using Ninject.Modules;
using NLog;
using NLog.Config;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace BitSharp.Client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private Logger logger;

        private IKernel kernel;
        private CoreDaemon coreDaemon;
        private DummyMonitor dummyMonitor;
        private LocalClient localClient;
        private MainWindowViewModel viewModel;

        protected override void OnInitialized(EventArgs e)
        {
            try
            {
                //TODO
                //**************************************************************
                var useTestNet = false;
                var connectToPeers = true;

                var ignoreScripts = false;

                var pruningMode = PruningMode.TxIndex | PruningMode.BlockSpentIndex | PruningMode.BlockTxesDelete;
                var enableDummyWallet = true;

                var useLevelDb = false;
                var runInMemory = false;

                var cleanData = false;
                var cleanChainState = false;
                var cleanBlockTxes = false
                    // clean block txes if the chain state is being cleaned and block txes have been pruned, the new chain state will require intact blocks to validate
                    || (cleanChainState && (pruningMode.HasFlag(PruningMode.BlockTxesPreserveMerkle) || pruningMode.HasFlag(PruningMode.BlockTxesDestroyMerkle)));
                //NOTE: Running with a cleaned chained state against a pruned blockchain does not work.
                //      It will see the data is missing, but won't redownload the blocks.
                //**************************************************************

                long? cacheSizeMaxBytes = 512.MEBIBYTE();

                // directories
                var baseDirectory = Config.LocalStoragePath;
                //if (Debugger.IsAttached)
                //    baseDirectory = Path.Combine(baseDirectory, "Debugger");

                var chainType = useTestNet ? ChainType.TestNet3 : ChainType.MainNet;

                string[] blockTxesStorageLocations = null;

                // detect local dev machine - TODO proper configuration
                var isAzureVM = (Environment.MachineName == "BITSHARP");
                var isLocalDev = (Environment.MachineName == "SKIPPY");

                if (isAzureVM)
                {
                    cacheSizeMaxBytes = null;
                    BlockRequestWorker.SecondaryBlockFolder = @"E:\BitSharp.Blocks\RawBlocks";
                    PeerWorker.ConnectedMax = 15;

                    blockTxesStorageLocations = new[]
                    {
                        @"E:\Blocks1",
                        @"E:\Blocks2",
                        @"E:\Blocks3",
                        @"E:\Blocks4",
                    };
                }
                else if (isLocalDev)
                {
                    //Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

                    cacheSizeMaxBytes = (int)2.GIBIBYTE();

                    // location to store a copy of raw blocks to avoid redownload
                    BlockRequestWorker.SecondaryBlockFolder = Path.Combine(baseDirectory, "RawBlocks");

                    // split block txes storage across 2 dedicated SSDs, keep chain state on main SSD
                    //blockTxesStorageLocations = new[]
                    //{
                    //    @"Y:\BitSharp",
                    //    @"Z:\BitSharp",
                    //};
                }

                //TODO
                if (cleanData)
                {
                    try { Directory.Delete(Path.Combine(baseDirectory, "Data", chainType.ToString()), recursive: true); }
                    catch (IOException) { }
                }
                if (cleanChainState)
                {
                    try { Directory.Delete(Path.Combine(baseDirectory, "Data", chainType.ToString(), "ChainState"), recursive: true); }
                    catch (IOException) { }
                }
                if (cleanBlockTxes)
                {
                    try { Directory.Delete(Path.Combine(baseDirectory, "Data", chainType.ToString(), "BlockTxes"), recursive: true); }
                    catch (IOException) { }
                }

                // initialize kernel
                this.kernel = new StandardKernel();

                // add logging module
                this.kernel.Load(new LoggingModule(baseDirectory, LogLevel.Info));

                InitUILogging(LogLevel.Info);

                // log startup
                this.logger = LogManager.GetCurrentClassLogger();
                this.logger.Info($"Starting up: {DateTime.Now}");

                var modules = new List<INinjectModule>();

                // add storage module
                if (useLevelDb)
                {
                    ulong? blocksCacheSize = null;
                    ulong? blocksWriteCacheSize = null;

                    ulong? blockTxesCacheSize = null;
                    ulong? blockTxesWriteCacheSize = null;

                    ulong? chainStateCacheSize = null;
                    ulong? chainStateWriteCacheSize = null;

                    modules.Add(new LevelDbStorageModule(baseDirectory, chainType,
                        blocksCacheSize, blocksWriteCacheSize,
                        blockTxesCacheSize, blockTxesWriteCacheSize,
                        chainStateCacheSize, chainStateWriteCacheSize));

                    long? writeCacheSizeMaxBytes = 128.MEBIBYTE();

                }
                else if (runInMemory)
                {
                    modules.Add(new MemoryStorageModule());
                    modules.Add(new NodeMemoryStorageModule());
                }
                else
                {
                    modules.Add(new EsentStorageModule(baseDirectory, chainType, cacheSizeMaxBytes: cacheSizeMaxBytes, blockTxesStorageLocations: blockTxesStorageLocations));
                }

                // add rules module
                modules.Add(new RulesModule(chainType));

                // load modules
                this.kernel.Load(modules.ToArray());

                // initialize rules
                var rules = this.kernel.Get<ICoreRules>();
                rules.IgnoreScripts = ignoreScripts;

                // initialize the blockchain daemon
                this.coreDaemon = this.kernel.Get<CoreDaemon>();
                this.coreDaemon.PruningMode = pruningMode;
                this.kernel.Bind<CoreDaemon>().ToConstant(this.coreDaemon).InTransientScope();

                // initialize dummy wallet monitor
                this.dummyMonitor = new DummyMonitor(this.coreDaemon);
                if (enableDummyWallet)
                {
                    Task.Run(() => this.dummyMonitor.Start());
                }
                else
                {
                    // allow pruning to any height when not using the wallet
                    this.coreDaemon.PrunableHeight = int.MaxValue;
                }

                // initialize p2p client
                this.localClient = this.kernel.Get<LocalClient>();
                this.kernel.Bind<LocalClient>().ToConstant(this.localClient).InTransientScope();

                // setup view model
                this.viewModel = new MainWindowViewModel(this.kernel, this.dummyMonitor);

                // start the blockchain daemon
                Task.Run(() => this.coreDaemon.Start());

                // start p2p client
                Task.Run(() => this.localClient.Start(connectToPeers));

                this.DataContext = this.viewModel;

                base.OnInitialized(e);
            }
            catch (Exception ex)
            {
                if (this.logger != null)
                {
                    this.logger.Fatal(ex, "Application failed");
                    LogManager.Flush();
                }
                else
                {
                    Console.WriteLine(ex);
                }

                Environment.Exit(-1);
            }
        }

        public void Dispose()
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.Info("Shutting down");

            // shutdown
            this.viewModel.Dispose();
            this.localClient.Dispose();
            this.dummyMonitor.Dispose();
            this.coreDaemon.Dispose();
            this.kernel.Dispose();

            this.logger.Info($"Finished shutting down: {stopwatch.Elapsed.TotalSeconds:N2}s");
            LogManager.Flush();
        }

        public MainWindowViewModel ViewModel => this.viewModel;

        protected override void OnClosing(CancelEventArgs e)
        {
            this.DataContext = null;
            Dispose();

            base.OnClosing(e);
        }

        private void InitUILogging(LogLevel logLevel)
        {
            // log layout format
            var layout = "${message} ${exception:separator=\r\n:format=message,type,method,stackTrace:maxInnerExceptionLevel=10:innerExceptionSeparator=\r\n:innerFormat=message,type,method,stackTrace}";

            // create rich text box target
            var uiTarget = new WpfRichTextBoxTarget
            {
                Layout = layout,
                TargetRichTextBox = loggerTextBox,
                UseDefaultRowColoringRules = true,
                AutoScroll = true,
                MaxLines = 250,
            };

            var config = LogManager.Configuration ?? new LoggingConfiguration();

            config.AddTarget("UI", uiTarget);
            config.LoggingRules.Add(new LoggingRule("*", logLevel, uiTarget.WrapAsync()));

            LogManager.Configuration = config;
        }

        private sealed class DummyMonitor : WalletMonitor
        {
            public DummyMonitor(CoreDaemon coreDaemon)
                : base(coreDaemon, keepEntries: false)
            {
                this.AddAddress(new First10000Address());
                this.AddAddress(new Top10000Address());
            }
        }
    }
}
