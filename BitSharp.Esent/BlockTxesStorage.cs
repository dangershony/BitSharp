﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using Microsoft.Isam.Esent.Interop;
using Microsoft.Isam.Esent.Interop.Server2003;
using Microsoft.Isam.Esent.Interop.Windows7;
using Microsoft.Isam.Esent.Interop.Windows8;
using Microsoft.Isam.Esent.Interop.Windows81;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using Transaction = BitSharp.Core.Domain.Transaction;

namespace BitSharp.Esent
{
    public class BlockTxesStorage : IBlockTxesStorage
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly string jetDirectory;
        private readonly string jetDatabase;
        private readonly Instance jetInstance;

        private readonly DisposableCache<BlockTxesCursor> cursorCache;

        private bool isDisposed;

        public BlockTxesStorage(string baseDirectory, int? index = null)
        {
            this.jetDirectory = Path.Combine(baseDirectory, "BlockTxes");
            if (index.HasValue)
                this.jetDirectory = Path.Combine(jetDirectory, index.Value.ToString());
            this.jetDatabase = Path.Combine(this.jetDirectory, "BlockTxes.edb");

            this.cursorCache = new DisposableCache<BlockTxesCursor>(1024,
                createFunc: () => new BlockTxesCursor(this.jetDatabase, this.jetInstance));

            this.jetInstance = new Instance(Guid.NewGuid().ToString());
            var success = false;
            try
            {
                EsentStorageManager.InitInstanceParameters(jetInstance, jetDirectory);
                this.jetInstance.Init();
                this.CreateOrOpenDatabase();
                success = true;
            }
            finally
            {
                if (!success)
                    this.jetInstance.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed && disposing)
            {
                this.cursorCache.Dispose();
                this.jetInstance.Dispose();

                isDisposed = true;
            }
        }

        internal Instance JetInstance { get { return this.jetInstance; } }

        public bool ContainsBlock(UInt256 blockHash)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                using (var jetTx = cursor.jetSession.BeginTransaction())
                {
                    Api.JetSetCurrentIndex(cursor.jetSession, cursor.blocksTableId, "IX_BlockHashTxIndex");
                    Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, 0), MakeKeyGrbit.NewKey);

                    if (Api.TrySeek(cursor.jetSession, cursor.blocksTableId, SeekGrbit.SeekGE))
                    {
                        UInt256 recordBlockHash; int txIndex;
                        DbEncoder.DecodeBlockHashTxIndex(Api.RetrieveColumn(cursor.jetSession, cursor.blocksTableId, cursor.blockHashTxIndexColumnId),
                            out recordBlockHash, out txIndex);
                        return blockHash == recordBlockHash;
                    }
                    else
                        return false;
                }
            }
        }

        public void PruneElements(IEnumerable<KeyValuePair<UInt256, IEnumerable<int>>> blockTxIndices)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                foreach (var keyPair in blockTxIndices)
                {
                    var blockHash = keyPair.Key;
                    var txIndices = keyPair.Value;

                    using (var jetTx = cursor.jetSession.BeginTransaction())
                    {
                        var pruningCursor = new MerkleTreePruningCursor(blockHash, cursor);

                        // prune the transactions
                        foreach (var index in txIndices)
                        {
                            var cachedCursor = new CachedMerkleTreePruningCursor(pruningCursor);
                            MerkleTree.PruneNode(cachedCursor, index);
                        }

                        jetTx.CommitLazy();
                    }
                }
            }
        }

        public void DeleteElements(IEnumerable<KeyValuePair<UInt256, IEnumerable<int>>> blockTxIndices)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                foreach (var keyPair in blockTxIndices)
                {
                    var blockHash = keyPair.Key;
                    var txIndices = keyPair.Value;

                    using (var jetTx = cursor.jetSession.BeginTransaction())
                    {
                        // prune the transactions
                        foreach (var index in txIndices)
                        {
                            // remove transactions
                            Api.JetSetCurrentIndex(cursor.jetSession, cursor.blocksTableId, "IX_BlockHashTxIndex");
                            Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, index), MakeKeyGrbit.NewKey);

                            if (Api.TrySeek(cursor.jetSession, cursor.blocksTableId, SeekGrbit.SeekEQ))
                                Api.JetDelete(cursor.jetSession, cursor.blocksTableId);
                        }

                        jetTx.CommitLazy();
                    }
                }
            }
        }

        public bool TryReadBlockTransactions(UInt256 blockHash, out IEnumerator<BlockTx> blockTxes)
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

        private IEnumerator<BlockTx> ReadBlockTransactions(UInt256 blockHash)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                using (var jetTx = cursor.jetSession.BeginTransaction())
                {
                    Api.JetSetCurrentIndex(cursor.jetSession, cursor.blocksTableId, "IX_BlockHashTxIndex");

                    Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, 0), MakeKeyGrbit.NewKey);
                    if (!Api.TrySeek(cursor.jetSession, cursor.blocksTableId, SeekGrbit.SeekGE))
                        throw new MissingDataException(blockHash);

                    Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, int.MaxValue), MakeKeyGrbit.NewKey);
                    if (!Api.TrySetIndexRange(cursor.jetSession, cursor.blocksTableId, SetIndexRangeGrbit.RangeUpperLimit))
                        throw new MissingDataException(blockHash);

                    do
                    {
                        var blockHashTxIndexColumn = new BytesColumnValue { Columnid = cursor.blockHashTxIndexColumnId };
                        var blockDepthColumn = new Int32ColumnValue { Columnid = cursor.blockDepthColumnId };
                        var blockTxHashColumn = new BytesColumnValue { Columnid = cursor.blockTxHashColumnId };
                        var blockTxBytesColumn = new BytesColumnValue { Columnid = cursor.blockTxBytesColumnId };
                        Api.RetrieveColumns(cursor.jetSession, cursor.blocksTableId, blockHashTxIndexColumn, blockDepthColumn, blockTxHashColumn, blockTxBytesColumn);

                        UInt256 recordBlockHash; int txIndex;
                        DbEncoder.DecodeBlockHashTxIndex(blockHashTxIndexColumn.Value, out recordBlockHash, out txIndex);
                        var depth = blockDepthColumn.Value.Value;
                        var txHash = DbEncoder.DecodeUInt256(blockTxHashColumn.Value);
                        var txBytes = blockTxBytesColumn.Value;

                        // determine if transaction is pruned by its depth
                        var pruned = depth >= 0;
                        depth = Math.Max(0, depth);

                        var tx = !pruned ? DataEncoder.DecodeTransaction(txBytes, txHash) : null;

                        var blockTx = new BlockTx(txIndex, depth, txHash, pruned, tx);

                        yield return blockTx;
                    }
                    while (Api.TryMoveNext(cursor.jetSession, cursor.blocksTableId));
                }
            }
        }

        public bool TryGetTransaction(UInt256 blockHash, int txIndex, out Transaction transaction)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                using (var jetTx = cursor.jetSession.BeginTransaction())
                {
                    Api.JetSetCurrentIndex(cursor.jetSession, cursor.blocksTableId, "IX_BlockHashTxIndex");
                    Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, txIndex), MakeKeyGrbit.NewKey);
                    if (Api.TrySeek(cursor.jetSession, cursor.blocksTableId, SeekGrbit.SeekEQ))
                    {
                        var blockTxHashColumn = new BytesColumnValue { Columnid = cursor.blockTxHashColumnId };
                        var blockTxBytesColumn = new BytesColumnValue { Columnid = cursor.blockTxBytesColumnId };
                        Api.RetrieveColumns(cursor.jetSession, cursor.blocksTableId, blockTxHashColumn, blockTxBytesColumn);

                        var txBytes = blockTxBytesColumn.Value;
                        if (txBytes != null)
                        {
                            var txHash = DbEncoder.DecodeUInt256(blockTxHashColumn.Value);

                            transaction = DataEncoder.DecodeTransaction(txBytes, txHash);
                            return true;
                        }
                        else
                        {
                            transaction = default(Transaction);
                            return false;
                        }
                    }
                    else
                    {
                        transaction = default(Transaction);
                        return false;
                    }
                }
            }
        }

        private void CreateOrOpenDatabase()
        {
            try
            {
                OpenDatabase();
            }
            catch (Exception)
            {
                DeleteDatabase();
                CreateDatabase();
            }
        }

        private void CreateDatabase()
        {
            JET_DBID blockDbId;
            JET_TABLEID globalsTableId;
            JET_COLUMNID blockCountColumnId;
            JET_COLUMNID flushColumnId;
            JET_TABLEID blocksTableId;
            JET_COLUMNID blockHashTxIndexColumnId;
            JET_COLUMNID blockDepthColumnId;
            JET_COLUMNID blockTxHashColumnId;
            JET_COLUMNID blockTxBytesColumnId;

            using (var jetSession = new Session(this.jetInstance))
            {
                var createGrbit = CreateDatabaseGrbit.None;
                if (EsentVersion.SupportsWindows7Features)
                    createGrbit |= Windows7Grbits.EnableCreateDbBackgroundMaintenance;

                Api.JetCreateDatabase(jetSession, jetDatabase, "", out blockDbId, createGrbit);

                var defaultValue = BitConverter.GetBytes(0);
                Api.JetCreateTable(jetSession, blockDbId, "Globals", 0, 0, out globalsTableId);
                Api.JetAddColumn(jetSession, globalsTableId, "BlockCount", new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate }, defaultValue, defaultValue.Length, out blockCountColumnId);
                Api.JetAddColumn(jetSession, globalsTableId, "Flush", new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate }, defaultValue, defaultValue.Length, out flushColumnId);

                // initialize global data
                using (var jetUpdate = jetSession.BeginUpdate(globalsTableId, JET_prep.Insert))
                {
                    Api.SetColumn(jetSession, globalsTableId, blockCountColumnId, 0);
                    Api.SetColumn(jetSession, globalsTableId, flushColumnId, 0);

                    jetUpdate.Save();
                }

                Api.JetCloseTable(jetSession, globalsTableId);

                Api.JetCreateTable(jetSession, blockDbId, "Blocks", 0, 0, out blocksTableId);
                Api.JetAddColumn(jetSession, blocksTableId, "BlockHashTxIndex", new JET_COLUMNDEF { coltyp = JET_coltyp.Binary, cbMax = 36, grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed }, null, 0, out blockHashTxIndexColumnId);
                Api.JetAddColumn(jetSession, blocksTableId, "Depth", new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnNotNULL }, null, 0, out blockDepthColumnId);
                Api.JetAddColumn(jetSession, blocksTableId, "TxHash", new JET_COLUMNDEF { coltyp = JET_coltyp.Binary, cbMax = 32, grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed }, null, 0, out blockTxHashColumnId);
                Api.JetAddColumn(jetSession, blocksTableId, "TxBytes", new JET_COLUMNDEF { coltyp = JET_coltyp.LongBinary }, null, 0, out blockTxBytesColumnId);

                Api.JetCreateIndex2(jetSession, blocksTableId,
                    new JET_INDEXCREATE[]
                    {
                        new JET_INDEXCREATE
                        {
                            cbKeyMost = 255,
                            grbit = CreateIndexGrbit.IndexUnique | CreateIndexGrbit.IndexDisallowNull,
                            szIndexName = "IX_BlockHashTxIndex",
                            szKey = "+BlockHashTxIndex\0\0",
                            cbKey = "+BlockHashTxIndex\0\0".Length
                        }
                    }, 1);

                Api.JetCloseTable(jetSession, blocksTableId);
            }
        }

        private void DeleteDatabase()
        {
            try { Directory.Delete(this.jetDirectory, recursive: true); }
            catch (Exception) { }
            Directory.CreateDirectory(this.jetDirectory);
        }

        private void OpenDatabase()
        {
            var readOnly = false;

            using (var jetSession = new Session(this.jetInstance))
            {
                var attachGrbit = AttachDatabaseGrbit.None;
                if (readOnly)
                    attachGrbit |= AttachDatabaseGrbit.ReadOnly;
                if (EsentVersion.SupportsWindows7Features)
                    attachGrbit |= Windows7Grbits.EnableAttachDbBackgroundMaintenance;

                Api.JetAttachDatabase(jetSession, this.jetDatabase, attachGrbit);
                var success = false;
                try
                {
                    JET_DBID blockDbId;
                    Api.JetOpenDatabase(jetSession, this.jetDatabase, "", out blockDbId, readOnly ? OpenDatabaseGrbit.ReadOnly : OpenDatabaseGrbit.None);
                    try
                    {
                        using (var handle = this.cursorCache.TakeItem())
                        {
                            var cursor = handle.Item;

                            // reset flush column
                            using (var jetUpdate = cursor.jetSession.BeginUpdate(cursor.globalsTableId, JET_prep.Replace))
                            {
                                Api.SetColumn(cursor.jetSession, cursor.globalsTableId, cursor.flushColumnId, 0);

                                jetUpdate.Save();
                            }
                        }

                        success = true;
                    }
                    finally
                    {
                        if (!success)
                            Api.JetCloseDatabase(jetSession, blockDbId, CloseDatabaseGrbit.None);
                    }
                }
                finally
                {
                    if (!success)
                        Api.JetDetachDatabase(jetSession, this.jetDatabase);
                }
            }
        }

        public int BlockCount
        {
            get
            {
                using (var handle = this.cursorCache.TakeItem())
                {
                    var cursor = handle.Item;

                    return Api.RetrieveColumnAsInt32(cursor.jetSession, cursor.globalsTableId, cursor.blockCountColumnId).Value;
                }
            }
        }

        public string Name
        {
            get { return "Blocks"; }
        }

        public bool TryAddBlockTransactions(UInt256 blockHash, IEnumerable<Transaction> blockTxes)
        {
            if (this.ContainsBlock(blockHash))
                return false;

            try
            {
                using (var handle = this.cursorCache.TakeItem())
                {
                    var cursor = handle.Item;

                    using (var jetTx = cursor.jetSession.BeginTransaction())
                    {
                        var txIndex = 0;
                        foreach (var tx in blockTxes)
                        {
                            AddTransaction(blockHash, txIndex, tx.Hash, DataEncoder.EncodeTransaction(tx), cursor);
                            txIndex++;
                        }

                        // increase block count
                        Api.EscrowUpdate(cursor.jetSession, cursor.globalsTableId, cursor.blockCountColumnId, +1);

                        jetTx.CommitLazy();
                        return true;
                    }
                }
            }
            catch (EsentKeyDuplicateException)
            {
                return false;
            }
        }

        private void AddTransaction(UInt256 blockHash, int txIndex, UInt256 txHash, byte[] txBytes, BlockTxesCursor cursor)
        {
            using (var jetUpdate = cursor.jetSession.BeginUpdate(cursor.blocksTableId, JET_prep.Insert))
            {
                Api.SetColumns(cursor.jetSession, cursor.blocksTableId,
                    new BytesColumnValue { Columnid = cursor.blockHashTxIndexColumnId, Value = DbEncoder.EncodeBlockHashTxIndex(blockHash, txIndex) },
                    //TODO i'm using -1 depth to mean not pruned, this should be interpreted as depth 0
                    new Int32ColumnValue { Columnid = cursor.blockDepthColumnId, Value = -1 },
                    new BytesColumnValue { Columnid = cursor.blockTxHashColumnId, Value = DbEncoder.EncodeUInt256(txHash) },
                    new BytesColumnValue { Columnid = cursor.blockTxBytesColumnId, Value = txBytes });

                jetUpdate.Save();
            }
        }

        public bool TryRemoveBlockTransactions(UInt256 blockHash)
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                using (var jetTx = cursor.jetSession.BeginTransaction())
                {
                    // remove transactions
                    Api.JetSetCurrentIndex(cursor.jetSession, cursor.blocksTableId, "IX_BlockHashTxIndex");
                    Api.MakeKey(cursor.jetSession, cursor.blocksTableId, DbEncoder.EncodeBlockHashTxIndex(blockHash, 0), MakeKeyGrbit.NewKey);

                    var removed = false;
                    if (Api.TrySeek(cursor.jetSession, cursor.blocksTableId, SeekGrbit.SeekGE))
                    {
                        do
                        {
                            UInt256 recordBlockHash; int txIndex;
                            DbEncoder.DecodeBlockHashTxIndex(Api.RetrieveColumn(cursor.jetSession, cursor.blocksTableId, cursor.blockHashTxIndexColumnId),
                                out recordBlockHash, out txIndex);
                            if (blockHash == recordBlockHash)
                            {
                                Api.JetDelete(cursor.jetSession, cursor.blocksTableId);
                                removed = true;
                            }
                            else
                            {
                                break;
                            }
                        }
                        while (Api.TryMoveNext(cursor.jetSession, cursor.blocksTableId));
                    }

                    if (removed)
                    {
                        // decrease block count
                        Api.EscrowUpdate(cursor.jetSession, cursor.globalsTableId, cursor.blockCountColumnId, -1);

                        jetTx.CommitLazy();
                        return true;
                    }
                    else
                    {
                        // transactions are already removed
                        return false;
                    }
                }
            }
        }

        public void Flush()
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                if (EsentVersion.SupportsServer2003Features)
                {
                    Api.JetCommitTransaction(cursor.jetSession, Server2003Grbits.WaitAllLevel0Commit);
                }
                else
                {
                    using (var jetTx = cursor.jetSession.BeginTransaction())
                    {
                        Api.EscrowUpdate(cursor.jetSession, cursor.globalsTableId, cursor.flushColumnId, 1);
                        jetTx.Commit(CommitTransactionGrbit.None);
                    }
                }
            }
        }

        public void Defragment()
        {
            using (var handle = this.cursorCache.TakeItem())
            {
                var cursor = handle.Item;

                //int passes = int.MaxValue, seconds = int.MaxValue;
                //Api.JetDefragment(cursor.jetSession, cursor.blockDbId, "", ref passes, ref seconds, DefragGrbit.BatchStart);

                if (EsentVersion.SupportsWindows81Features)
                {
                    logger.Info("Begin shrinking block txes database");

                    int actualPages;
                    Windows8Api.JetResizeDatabase(cursor.jetSession, cursor.blockDbId, 0, out actualPages, Windows81Grbits.OnlyShrink);

                    logger.Info("Finished shrinking block txes database: {0:#,##0} MB".Format2((float)actualPages * SystemParameters.DatabasePageSize / 1.MILLION()));
                }
            }
        }
    }
}
