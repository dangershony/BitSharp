﻿using BitSharp.Common;
using System;
using System.Numerics;

namespace BitSharp.Core.Domain
{
    public class BlockHeader
    {
        private readonly int hashCode;

        public BlockHeader(UInt32 version, UInt256 previousBlock, UInt256 merkleRoot, UInt32 time, UInt32 bits, UInt32 nonce, UInt256 hash = null)
        {
            Version = version;
            PreviousBlock = previousBlock;
            MerkleRoot = merkleRoot;
            Time = time;
            Bits = bits;
            Nonce = nonce;

            Hash = hash ?? DataCalculator.CalculateBlockHash(version, previousBlock, merkleRoot, time, bits, nonce);

            this.hashCode = this.Hash.GetHashCode();
        }

        public UInt32 Version { get; }

        public UInt256 PreviousBlock { get; }

        public UInt256 MerkleRoot { get; }

        public UInt32 Time { get; }

        public UInt32 Bits { get; }

        public UInt32 Nonce { get; }

        public UInt256 Hash { get; }

        public BlockHeader With(UInt32? Version = null, UInt256 PreviousBlock = null, UInt256 MerkleRoot = null, UInt32? Time = null, UInt32? Bits = null, UInt32? Nonce = null)
        {
            return new BlockHeader
            (
                Version ?? this.Version,
                PreviousBlock ?? this.PreviousBlock,
                MerkleRoot ?? this.MerkleRoot,
                Time ?? this.Time,
                Bits ?? this.Bits,
                Nonce ?? this.Nonce
            );
        }

        public BigInteger CalculateWork()
        {
            return DataCalculator.CalculateWork(this);
        }

        public UInt256 CalculateTarget()
        {
            return DataCalculator.BitsToTarget(this.Bits);
        }

        public override bool Equals(object obj)
        {
            if (!(obj is BlockHeader))
                return false;

            return (BlockHeader)obj == this;
        }

        public override int GetHashCode()
        {
            return this.hashCode;
        }

        public static bool operator ==(BlockHeader left, BlockHeader right)
        {
            return object.ReferenceEquals(left, right) || (!object.ReferenceEquals(left, null) && !object.ReferenceEquals(right, null) && left.Hash == right.Hash && left.Version == right.Version && left.PreviousBlock == right.PreviousBlock && left.MerkleRoot == right.MerkleRoot && left.Time == right.Time && left.Bits == right.Bits && left.Nonce == right.Nonce);
        }

        public static bool operator !=(BlockHeader left, BlockHeader right)
        {
            return !(left == right);
        }
    }
}
