﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using LightningDB;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BitSharp.Lmdb
{
    public class BlockTxesStorage : IBlockTxesStorage
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly string jetDirectory;
        private readonly LightningEnvironment jetInstance;
        private readonly LightningDatabase globalsTableId;
        private readonly LightningDatabase blocksTableId;

        private readonly byte[] blockCountKey = UTF8Encoding.ASCII.GetBytes("BlockCount");

        public BlockTxesStorage(string baseDirectory, long blockTxesSize)
        {
            this.jetDirectory = Path.Combine(baseDirectory, "BlockTxes");

            this.jetInstance = new LightningEnvironment(this.jetDirectory, EnvironmentOpenFlags.NoThreadLocalStorage | EnvironmentOpenFlags.NoSync)
            {
                MaxDatabases = 10,
                MapSize = blockTxesSize
            };
            this.jetInstance.Open();

            using (var txn = this.jetInstance.BeginTransaction())
            {
                globalsTableId = txn.OpenDatabase("Globals", new DatabaseOptions { Flags = DatabaseOpenFlags.Create });
                blocksTableId = txn.OpenDatabase("Blocks", new DatabaseOptions { Flags = DatabaseOpenFlags.Create });

                if (!txn.ContainsKey(globalsTableId, blockCountKey))
                    txn.Put(globalsTableId, blockCountKey, Bits.GetBytes(0));

                txn.Commit();
            }
        }

        public void Dispose()
        {
            this.jetInstance.Dispose();
        }

        public bool ContainsBlock(UInt256 blockHash)
        {
            using (var txn = this.jetInstance.BeginTransaction(TransactionBeginFlags.ReadOnly))
            {
                return txn.ContainsKey(blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, 0));
            }
        }

        public void PruneElements(IEnumerable<KeyValuePair<UInt256, IEnumerable<int>>> blockTxIndices)
        {
            using (var txn = this.jetInstance.BeginTransaction())
            using (var cursor = txn.CreateCursor(blocksTableId))
            {
                foreach (var keyPair in blockTxIndices)
                {
                    var blockHash = keyPair.Key;
                    var txIndices = keyPair.Value;

                    var pruningCursor = new MerkleTreePruningCursor(blockHash, txn, blocksTableId, cursor);

                    // prune the transactions
                    foreach (var index in txIndices)
                        MerkleTree.PruneNode(pruningCursor, index);
                }

                cursor.Dispose();
                txn.Commit();
            }
        }

        public void DeleteElements(IEnumerable<KeyValuePair<UInt256, IEnumerable<int>>> blockTxIndices)
        {
            using (var txn = this.jetInstance.BeginTransaction())
            {
                foreach (var keyPair in blockTxIndices)
                {
                    var blockHash = keyPair.Key;
                    var txIndices = keyPair.Value;

                    // prune the transactions
                    foreach (var index in txIndices)
                    {
                        var key = DbEncoder.EncodeBlockHashTxIndex(blockHash, index);
                        if (txn.ContainsKey(blocksTableId, key))
                            txn.Delete(blocksTableId, key);
                    }
                }

                txn.Commit();
            }
        }

        public bool TryReadBlockTransactions(UInt256 blockHash, out IEnumerable<BlockTx> blockTxes)
        {
            if (this.ContainsBlock(blockHash))
            {
                blockTxes = ReadBlockTransactions(blockHash);
                return true;
            }
            else
            {
                blockTxes = null;
                return false;
            }
        }

        private IEnumerable<BlockTx> ReadBlockTransactions(UInt256 blockHash)
        {
            using (var txn = this.jetInstance.BeginTransaction(TransactionBeginFlags.ReadOnly))
            using (var cursor = txn.CreateCursor(blocksTableId))
            {
                var kvPair = cursor.MoveToFirstAfter(DbEncoder.EncodeBlockHashTxIndex(blockHash, 0));

                if (kvPair == null)
                    yield break;

                do
                {
                    UInt256 recordBlockHash; int txIndex;
                    DbEncoder.DecodeBlockHashTxIndex(kvPair.Value.Key, out recordBlockHash, out txIndex);
                    if (blockHash != recordBlockHash)
                        yield break;

                    yield return DataEncoder.DecodeBlockTx(kvPair.Value.Value);
                }
                while ((kvPair = cursor.MoveNext()) != null);
            }
        }

        public bool TryGetTransaction(UInt256 blockHash, int txIndex, out Transaction transaction)
        {
            using (var txn = this.jetInstance.BeginTransaction(TransactionBeginFlags.ReadOnly))
            {
                byte[] blockTxBytes;
                if (txn.TryGet(blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, txIndex), out blockTxBytes))
                {
                    var blockTx = DataEncoder.DecodeBlockTx(blockTxBytes);

                    transaction = blockTx.Transaction;
                    return transaction != null;
                }
                else
                {
                    transaction = default(Transaction);
                    return false;
                }
            }
        }

        public int BlockCount
        {
            get
            {
                using (var txn = this.jetInstance.BeginTransaction(TransactionBeginFlags.ReadOnly))
                {
                    return Bits.ToInt32(txn.Get(globalsTableId, blockCountKey));
                }
            }
        }

        public string Name
        {
            get { return "Blocks"; }
        }

        public IEnumerable<UInt256> TryAddBlockTransactions(IEnumerable<KeyValuePair<UInt256, IEnumerable<Transaction>>> blockTransactions)
        {
            try
            {
                using (var txn = this.jetInstance.BeginTransaction())
                {
                    var addedBlocks = new List<UInt256>();
                    foreach (var keyPair in blockTransactions)
                    {
                        var blockHash = keyPair.Key;
                        var blockTxes = keyPair.Value;

                        if (this.ContainsBlock(blockHash))
                            continue;

                        var txIndex = 0;
                        foreach (var tx in blockTxes)
                        {
                            var blockTx = new BlockTx(txIndex, 0, tx.Hash, false, tx);

                            var key = DbEncoder.EncodeBlockHashTxIndex(blockHash, txIndex);
                            var value = DataEncoder.EncodeBlockTx(blockTx);

                            txn.Put(blocksTableId, key, value);
                            txIndex++;
                        }

                        // increase block count
                        txn.Put(globalsTableId, blockCountKey,
                            Bits.ToInt32(txn.Get(globalsTableId, blockCountKey)) + 1);

                        addedBlocks.Add(blockHash);
                    }

                    txn.Commit();
                    return addedBlocks;
                }
            }
            catch (Exception)
            {
                return Enumerable.Empty<UInt256>();
            }
        }

        public bool TryRemoveBlockTransactions(UInt256 blockHash)
        {
            using (var txn = this.jetInstance.BeginTransaction())
            using (var cursor = txn.CreateCursor(blocksTableId))
            {
                // remove transactions
                var kvPair = cursor.MoveToFirstAfter(DbEncoder.EncodeBlockHashTxIndex(blockHash, 0));

                var removed = false;
                if (kvPair != null)
                {
                    do
                    {
                        UInt256 recordBlockHash; int txIndex;
                        DbEncoder.DecodeBlockHashTxIndex(kvPair.Value.Key, out recordBlockHash, out txIndex);
                        if (blockHash == recordBlockHash)
                        {
                            cursor.Delete();
                            removed = true;
                        }
                        else
                        {
                            break;
                        }
                    }
                    while ((kvPair = cursor.MoveNext()) != null);
                }

                if (removed)
                {
                    // decrease block count
                    txn.Put(globalsTableId, blockCountKey,
                        Bits.ToInt32(txn.Get(globalsTableId, blockCountKey)) - 1);

                    cursor.Dispose();
                    txn.Commit();
                }

                return removed;
            }
        }

        public void Flush()
        {
            this.jetInstance.Flush(force: true);
        }

        public void Defragment()
        {
            this.logger.Info("BlockTxes database: {0:#,##0} MB".Format2(this.jetInstance.UsedSize / 1.MILLION()));
        }
    }
}
