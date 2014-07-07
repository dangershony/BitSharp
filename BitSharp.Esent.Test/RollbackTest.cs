﻿using BitSharp.Common;
using BitSharp.Core.Builders;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Test;
using BitSharp.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Esent.Test
{
    [TestClass]
    public class RollbackTest
    {
        [TestMethod]
        public void TestRollback()
        {
            var logger = LogManager.CreateNullLogger();
            var baseDirectory = EsentTests.PrepareBaseDirectory();

            var blockProvider = new MainnetBlockProvider();
            var blocks = Enumerable.Range(0, 1000).Select(x => blockProvider.GetBlock(x)).ToList();

            var genesisBlock = blocks[0];
            var genesisHeader = new ChainedHeader(genesisBlock.Header, height: 0, totalWork: 0);
            var genesisChain = Chain.CreateForGenesisBlock(genesisHeader);
            var genesisUtxo = Utxo.CreateForGenesisBlock(genesisBlock.Hash);
            var chainBuilder = genesisChain.ToBuilder();

            var rules = new MainnetRules(logger, null);

            using (var blockStorage = new BlockStorageNew(baseDirectory))
            using (var chainStateBuilderStorage = new ChainStateBuilderStorage(baseDirectory, genesisUtxo.Storage, logger))
            using (var chainStateBuilder = new ChainStateBuilder(chainBuilder, chainStateBuilderStorage, logger, rules, blockStorage, null, null))
            {
                foreach (var block in blocks)
                    blockStorage.AddBlock(block);

                var expectedUtxos = new List<List<KeyValuePair<UInt256, UnspentTx>>>();

                using (var chainState = chainStateBuilder.ToImmutable())
                {
                    expectedUtxos.Add(chainState.Utxo.GetUnspentTransactions().ToList());
                }

                for (var blockIndex = 1; blockIndex < blocks.Count; blockIndex++)
                {
                    var block = blocks[blockIndex];
                    var chainedHeader = new ChainedHeader(block.Header, blockIndex, 0);
                    var blockTxes = block.Transactions.Select((x, i) => new BlockTx(i, 0, x.Hash, x));

                    chainStateBuilder.AddBlock(chainedHeader, blockTxes);

                    using (var chainState = chainStateBuilder.ToImmutable())
                    {
                        expectedUtxos.Add(chainState.Utxo.GetUnspentTransactions().ToList());
                    }
                }

                expectedUtxos.RemoveAt(expectedUtxos.Count - 1);

                for (var blockIndex = blocks.Count - 1; blockIndex >= 1; blockIndex--)
                {
                    var block = blocks[blockIndex];
                    var chainedHeader = new ChainedHeader(block.Header, blockIndex, 0);
                    var blockTxes = block.Transactions.Select((x, i) => new BlockTx(i, 0, x.Hash, x));

                    chainStateBuilder.RollbackBlock(chainedHeader, blockTxes);

                    var expectedUtxo = expectedUtxos.Last();
                    expectedUtxos.RemoveAt(expectedUtxos.Count - 1);

                    List<KeyValuePair<UInt256, UnspentTx>> actualUtxo;
                    using (var chainState = chainStateBuilder.ToImmutable())
                    {
                        actualUtxo = chainState.Utxo.GetUnspentTransactions().ToList();
                    }

                    CollectionAssert.AreEqual(expectedUtxo, actualUtxo, new UtxoComparer());
                }
            }
        }

        private class UtxoComparer : IComparer, IComparer<KeyValuePair<UInt256, UnspentTx>>
        {
            public int Compare(KeyValuePair<UInt256, UnspentTx> x, KeyValuePair<UInt256, UnspentTx> y)
            {
                if (x.Key == y.Key && x.Value.Equals(y.Value))
                    return 0;
                else
                    return -1;
            }

            int IComparer.Compare(object x, object y)
            {
                return this.Compare((KeyValuePair<UInt256, UnspentTx>)x, (KeyValuePair<UInt256, UnspentTx>)y);
            }
        }
    }
}