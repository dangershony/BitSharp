﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.ExtensionMethods;
using BitSharp.Core.Storage;
using LightningDB;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BitSharp.Lmdb
{
    internal class ChainStateCursor : IChainStateCursor
    {
        //TODO
        public static bool IndexOutputs { get; set; }

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly bool readOnly;
        private readonly string jetDatabase;
        private readonly LightningEnvironment jetInstance;
        private readonly LightningDatabase globalsTableId;
        private readonly LightningDatabase chainTableId;
        private readonly LightningDatabase unspentTxTableId;
        private readonly LightningDatabase blockSpentTxesTableId;
        private readonly LightningDatabase blockUnmintedTxesTableId;

        private readonly byte[] unspentTxCountKey = UTF8Encoding.ASCII.GetBytes("UnspentTxCount");
        private readonly byte[] unspentOutputCountKey = UTF8Encoding.ASCII.GetBytes("UnspentOutputCount");
        private readonly byte[] totalTxCountKey = UTF8Encoding.ASCII.GetBytes("TotalTxount");
        private readonly byte[] totalInputCountKey = UTF8Encoding.ASCII.GetBytes("TotalInputCount");
        private readonly byte[] totalOutputCountKey = UTF8Encoding.ASCII.GetBytes("TotalOutputCount");

        private LightningTransaction txn;

        public ChainStateCursor(bool readOnly, string jetDatabase, LightningEnvironment jetInstance, LightningDatabase globalsTableId, LightningDatabase chainTableId, LightningDatabase unspentTxTableId, LightningDatabase blockSpentTxesTableId, LightningDatabase blockUnmintedTxesTableId)
        {
            this.readOnly = readOnly;
            this.jetDatabase = jetDatabase;
            this.jetInstance = jetInstance;
            this.globalsTableId = globalsTableId;
            this.chainTableId = chainTableId;
            this.unspentTxTableId = unspentTxTableId;
            this.blockSpentTxesTableId = blockSpentTxesTableId;
            this.blockUnmintedTxesTableId = blockUnmintedTxesTableId;
        }

        public void Dispose()
        {
            if (this.txn != null)
            {
                this.txn.Dispose();
                this.txn = null;
            }
        }

        public bool InTransaction
        {
            get { return this.txn != null; }
        }

        public IEnumerable<ChainedHeader> ReadChain()
        {
            CheckTransaction();

            using (var cursor = this.txn.CreateCursor(chainTableId))
            {
                var kvPair = cursor.MoveToFirst();
                while (kvPair != null)
                {
                    var chainedHeader = DataEncoder.DecodeChainedHeader(kvPair.Value.Value);
                    yield return chainedHeader;

                    kvPair = cursor.MoveNext();
                }
            }
        }

        public ChainedHeader GetChainTip()
        {
            CheckTransaction();

            using (var cursor = this.txn.CreateCursor(chainTableId))
            {
                var kvPair = cursor.MoveToLast();
                if (kvPair != null)
                {
                    var chainedHeader = DataEncoder.DecodeChainedHeader(kvPair.Value.Value);
                    return chainedHeader;
                }
                else
                {
                    return null;
                }
            }
        }

        public void AddChainedHeader(ChainedHeader chainedHeader)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeInt32(chainedHeader.Height);
            if (!this.txn.ContainsKey(chainTableId, key))
                this.txn.Put(chainTableId, key, DataEncoder.EncodeChainedHeader(chainedHeader));
            else
                throw new InvalidOperationException();
        }

        public void RemoveChainedHeader(ChainedHeader chainedHeader)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeInt32(chainedHeader.Height);

            if (this.txn.ContainsKey(chainTableId, key))
                this.txn.Delete(chainTableId, key);
            else
                throw new InvalidOperationException();
        }

        public int UnspentTxCount
        {
            get
            {
                CheckTransaction();

                byte[] value;
                if (this.txn.TryGet(globalsTableId, unspentTxCountKey, out value))
                    return Bits.ToInt32(value);
                else
                    return 0;
            }
            set
            {
                CheckTransaction();

                this.txn.Put(globalsTableId, unspentTxCountKey, Bits.GetBytes(value));
            }
        }

        public int UnspentOutputCount
        {
            get
            {
                CheckTransaction();

                byte[] value;
                if (this.txn.TryGet(globalsTableId, unspentOutputCountKey, out value))
                    return Bits.ToInt32(value);
                else
                    return 0;
            }
            set
            {
                CheckTransaction();

                this.txn.Put(globalsTableId, unspentOutputCountKey, Bits.GetBytes(value));
            }
        }

        public int TotalTxCount
        {
            get
            {
                CheckTransaction();

                byte[] value;
                if (this.txn.TryGet(globalsTableId, totalTxCountKey, out value))
                    return Bits.ToInt32(value);
                else
                    return 0;
            }
            set
            {
                CheckTransaction();

                this.txn.Put(globalsTableId, totalTxCountKey, Bits.GetBytes(value));
            }
        }

        public int TotalInputCount
        {
            get
            {
                CheckTransaction();

                byte[] value;
                if (this.txn.TryGet(globalsTableId, totalInputCountKey, out value))
                    return Bits.ToInt32(value);
                else
                    return 0;
            }
            set
            {
                CheckTransaction();

                this.txn.Put(globalsTableId, totalInputCountKey, Bits.GetBytes(value));
            }
        }

        public int TotalOutputCount
        {
            get
            {
                CheckTransaction();

                byte[] value;
                if (this.txn.TryGet(globalsTableId, totalOutputCountKey, out value))
                    return Bits.ToInt32(value);
                else
                    return 0;
            }
            set
            {
                CheckTransaction();

                this.txn.Put(globalsTableId, totalOutputCountKey, Bits.GetBytes(value));
            }
        }

        public bool ContainsUnspentTx(UInt256 txHash)
        {
            CheckTransaction();

            return this.txn.ContainsKey(unspentTxTableId, DbEncoder.EncodeUInt256(txHash));
        }

        public bool TryGetUnspentTx(UInt256 txHash, out UnspentTx unspentTx)
        {
            CheckTransaction();

            byte[] unspentTxBytes;
            if (this.txn.TryGet(unspentTxTableId, DbEncoder.EncodeUInt256(txHash), out unspentTxBytes))
            {
                unspentTx = DataEncoder.DecodeUnspentTx(unspentTxBytes);
                return true;
            }
            else
            {
                unspentTx = default(UnspentTx);
                return false;
            }
        }

        public bool TryAddUnspentTx(UnspentTx unspentTx)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeUInt256(unspentTx.TxHash);
            var value = DataEncoder.EncodeUnspentTx(unspentTx);

            if (!this.txn.ContainsKey(unspentTxTableId, key))
            {
                this.txn.Put(unspentTxTableId, key, value);

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool TryRemoveUnspentTx(UInt256 txHash)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeUInt256(txHash);

            if (this.txn.ContainsKey(unspentTxTableId, key))
            {
                this.txn.Delete(unspentTxTableId, key);
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool TryUpdateUnspentTx(UnspentTx unspentTx)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeUInt256(unspentTx.TxHash);

            if (this.txn.ContainsKey(unspentTxTableId, key))
            {
                var value = DataEncoder.EncodeUnspentTx(unspentTx);

                this.txn.Put(unspentTxTableId, key, value);
                return true;
            }
            else
            {
                return false;
            }
        }

        public IEnumerable<UnspentTx> ReadUnspentTransactions()
        {
            CheckTransaction();

            using (var cursor = this.txn.CreateCursor(unspentTxTableId))
            {
                var kvPair = cursor.MoveToFirst();
                while (kvPair != null)
                {
                    var unspentTx = DataEncoder.DecodeUnspentTx(kvPair.Value.Value);
                    yield return unspentTx;

                    kvPair = cursor.MoveNext();
                }
            }
        }

        public bool ContainsBlockSpentTxes(int blockIndex)
        {
            CheckTransaction();

            return this.txn.ContainsKey(blockSpentTxesTableId, DbEncoder.EncodeInt32(blockIndex));
        }

        public bool TryGetBlockSpentTxes(int blockIndex, out IImmutableList<UInt256> spentTxes)
        {
            CheckTransaction();

            byte[] spentTxesBytes;
            if (this.txn.TryGet(blockSpentTxesTableId, DbEncoder.EncodeInt32(blockIndex), out spentTxesBytes))
            {
                using (var stream = new MemoryStream(spentTxesBytes))
                using (var reader = new BinaryReader(stream))
                {
                    spentTxes = ImmutableList.CreateRange(reader.ReadList(() => DataEncoder.DecodeUInt256(reader)));
                }

                return true;
            }
            else
            {
                spentTxes = null;
                return false;
            }
        }

        public bool TryAddBlockSpentTxes(int blockIndex, IImmutableList<UInt256> spentTxes)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeInt32(blockIndex);
            if (!this.txn.ContainsKey(blockSpentTxesTableId, key))
            {
                byte[] spentTxesBytes;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.WriteList(spentTxes.ToImmutableArray(), spentTx => DataEncoder.EncodeUInt256(writer, spentTx));
                    spentTxesBytes = stream.ToArray();
                }

                this.txn.Put(blockSpentTxesTableId, key, spentTxesBytes);

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool TryRemoveBlockSpentTxes(int blockIndex)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeInt32(blockIndex);

            if (this.txn.ContainsKey(blockSpentTxesTableId, key))
            {
                this.txn.Delete(blockSpentTxesTableId, key);
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool ContainsBlockUnmintedTxes(UInt256 blockHash)
        {
            CheckTransaction();

            return this.txn.ContainsKey(blockUnmintedTxesTableId, DbEncoder.EncodeUInt256(blockHash));
        }

        public bool TryGetBlockUnmintedTxes(UInt256 blockHash, out IImmutableList<UnmintedTx> unmintedTxes)
        {
            CheckTransaction();

            byte[] unmintedTxesBytes;
            if (this.txn.TryGet(blockUnmintedTxesTableId, DbEncoder.EncodeUInt256(blockHash), out unmintedTxesBytes))
            {
                using (var stream = new MemoryStream(unmintedTxesBytes))
                using (var reader = new BinaryReader(stream))
                {
                    unmintedTxes = ImmutableList.CreateRange(reader.ReadList(() => DataEncoder.DecodeUnmintedTx(reader)));
                }

                return true;
            }
            else
            {
                unmintedTxes = null;
                return false;
            }
        }

        public bool TryAddBlockUnmintedTxes(UInt256 blockHash, IImmutableList<UnmintedTx> unmintedTxes)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeUInt256(blockHash);
            if (!this.txn.ContainsKey(blockUnmintedTxesTableId, key))
            {
                byte[] unmintedTxesBytes;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.WriteList(unmintedTxes.ToImmutableArray(), unmintedTx => DataEncoder.EncodeUnmintedTx(writer, unmintedTx));
                    unmintedTxesBytes = stream.ToArray();
                }

                this.txn.Put(blockUnmintedTxesTableId, key, unmintedTxesBytes);

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool TryRemoveBlockUnmintedTxes(UInt256 blockHash)
        {
            CheckTransaction();

            var key = DbEncoder.EncodeUInt256(blockHash);

            if (this.txn.ContainsKey(blockUnmintedTxesTableId, key))
            {
                this.txn.Delete(blockUnmintedTxesTableId, key);
                return true;
            }
            else
            {
                return false;
            }
        }

        public void BeginTransaction(bool readOnly)
        {
            if (this.txn != null)
                throw new InvalidOperationException();

            this.txn = this.jetInstance.BeginTransaction(readOnly || this.readOnly ? TransactionBeginFlags.ReadOnly : TransactionBeginFlags.None);
        }

        public void CommitTransaction()
        {
            CheckTransaction();

            this.txn.Commit();
            this.txn.Dispose();
            this.txn = null;
        }

        public void RollbackTransaction()
        {
            CheckTransaction();

            this.txn.Abort();
            this.txn.Dispose();
            this.txn = null;
        }

        public void Flush()
        {
            this.jetInstance.Flush(force: true);
        }

        public void Defragment()
        {
            this.logger.Info("ChainState database: {0:#,##0} MB".Format2(this.jetInstance.UsedSize / 1.MILLION()));
        }

        private void CheckTransaction()
        {
            if (this.txn == null)
                throw new InvalidOperationException();
        }
    }
}