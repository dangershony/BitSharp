﻿using BitSharp.Common;
using BitSharp.Data;
using BitSharp.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Blockchain.Test
{
    public class UnitTestRules : MainnetRules
    {
        public static readonly UInt256 Target0 = UInt256.Parse("F000000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public static readonly UInt256 Target1 = UInt256.Parse("0F00000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public static readonly UInt256 Target2 = UInt256.Parse("00F0000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public static readonly UInt256 Target3 = UInt256.Parse("000F000000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public static readonly UInt256 Target4 = UInt256.Parse("0000F00000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

        private UInt256 _highestTarget;
        private Block _genesisBlock;
        private ChainedBlock _genesisChainedBlock;

        public UnitTestRules(BlockHeaderCache blockHeaderCache)
            : base(blockHeaderCache)
        {
            this._highestTarget = Target0;
        }

        public override UInt256 HighestTarget { get { return this._highestTarget; } }

        public override Block GenesisBlock { get { return this._genesisBlock; } }

        public override ChainedBlock GenesisChainedBlock { get { return this._genesisChainedBlock; } }

        public void SetGenesisBlock(Block genesisBlock)
        {
            this._genesisBlock = genesisBlock;
            this._genesisChainedBlock = ChainedBlock.CreateForGenesisBlock(this._genesisBlock.Header);
        }

        public void SetHighestTarget(UInt256 highestTarget)
        {
            this._highestTarget = highestTarget;
        }
    }
}
