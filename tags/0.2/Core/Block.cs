/*
 * Copyright 2011 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BitCoinSharp.Common;
using BitCoinSharp.IO;
using log4net;
using Org.BouncyCastle.Math;

namespace BitCoinSharp
{
    /// <summary>
    /// A block is the foundation of the BitCoin system. It records a set of <see cref="Transaction">Transaction</see>s together with
    /// some data that links it into a place in the global block chain, and proves that a difficult calculation was done
    /// over its contents. See the BitCoin technical paper for more detail on blocks.
    /// </summary>
    /// <remarks>
    /// To get a block, you can either build one from the raw bytes you can get from another implementation,
    /// or request one specifically using <see cref="Peer.GetBlock(byte[])">Peer.GetBlock(byte[])</see>, or grab one from a downloaded
    /// <see cref="BlockChain">BlockChain</see>.
    /// </remarks>
    [Serializable]
    public class Block : Message
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof (Block));

        /// <summary>
        /// How many bytes are required to represent a block header.
        /// </summary>
        public const int HeaderSize = 80;

        private const uint _allowedTimeDrift = 2*60*60; // Same value as official client.

        /// <summary>
        /// A value for difficultyTarget (nBits) that allows half of all possible hash solutions. Used in unit testing.
        /// </summary>
        internal const uint EasiestDifficultyTarget = 0x207FFFFF;

        // For unit testing. If not zero, use this instead of the current time.
        internal static ulong FakeClock;
        private uint _version;
        private byte[] _prevBlockHash;
        private byte[] _merkleRoot;
        private uint _time;
        private uint _difficultyTarget; // "nBits"

        private uint _nonce;

        /// <summary>
        /// If null, it means this object holds only the headers.
        /// </summary>
        internal IList<Transaction> Transactions { get; private set; }

        /// <summary>
        /// Stores the hash of the block. If null, getHash() will recalculate it.
        /// </summary>
        [NonSerialized] private byte[] _hash;

        /// <summary>
        /// Special case constructor, used for the genesis node and unit tests.
        /// </summary>
        internal Block(NetworkParameters @params)
            : base(@params)
        {
            // Set up a few basic things. We are not complete after this though.
            _version = 1;
            _difficultyTarget = 0x1d07fff8;
            _time = (uint) UnixTime.ToUnixTime(DateTime.UtcNow);
            _prevBlockHash = new byte[32]; // All zeros.
        }

        /// <summary>
        /// Constructs a block object from the BitCoin wire format.
        /// </summary>
        /// <exception cref="BitCoinSharp.ProtocolException" />
        public Block(NetworkParameters @params, byte[] payloadBytes)
            : base(@params, payloadBytes, 0)
        {
        }

        /// <exception cref="BitCoinSharp.ProtocolException" />
        protected override void Parse()
        {
            _version = ReadUint32();
            _prevBlockHash = ReadHash();
            _merkleRoot = ReadHash();
            _time = ReadUint32();
            _difficultyTarget = ReadUint32();
            _nonce = ReadUint32();

            _hash = Utils.ReverseBytes(Utils.DoubleDigest(Bytes, 0, Cursor));

            if (Cursor == Bytes.Length)
            {
                // This message is just a header, it has no transactions.
                return;
            }

            var numTransactions = (int) ReadVarInt();
            Transactions = new List<Transaction>(numTransactions);
            for (var i = 0; i < numTransactions; i++)
            {
                var tx = new Transaction(Params, Bytes, Cursor);
                Transactions.Add(tx);
                Cursor += tx.MessageSize;
            }
        }

        /// <exception cref="System.IO.IOException" />
        private void WriteHeader(Stream stream)
        {
            Utils.Uint32ToByteStreamLe(_version, stream);
            stream.Write(Utils.ReverseBytes(_prevBlockHash));
            stream.Write(Utils.ReverseBytes(MerkleRoot));
            Utils.Uint32ToByteStreamLe(_time, stream);
            Utils.Uint32ToByteStreamLe(_difficultyTarget, stream);
            Utils.Uint32ToByteStreamLe(_nonce, stream);
        }

        /// <exception cref="System.IO.IOException" />
        public override void BitcoinSerializeToStream(Stream stream)
        {
            WriteHeader(stream);
            // We may only have enough data to write the header.
            if (Transactions == null) return;
            stream.Write(new VarInt((ulong) Transactions.Count).Encode());
            foreach (var tx in Transactions)
            {
                tx.BitcoinSerializeToStream(stream);
            }
        }

        /// <summary>
        /// Calculates the block hash by serializing the block and hashing the resulting bytes.
        /// </summary>
        private byte[] CalculateHash()
        {
            using (var bos = new MemoryStream())
            {
                WriteHeader(bos);
                return Utils.ReverseBytes(Utils.DoubleDigest(bos.ToArray()));
            }
        }

        /// <summary>
        /// Returns the hash of the block (which for a valid, solved block should be below the target) in the form seen
        /// on the block explorer. If you call this on block 1 in the production chain, you will get
        /// "00000000839a8e6886ab5951d76f411475428afc90947ee320161bbf18eb6048".
        /// </summary>
        public string HashAsString
        {
            get { return Utils.BytesToHexString(Hash); }
        }

        /// <summary>
        /// Returns the hash of the block (which for a valid, solved block should be below the target). Big endian.
        /// </summary>
        public byte[] Hash
        {
            get { return _hash ?? (_hash = CalculateHash()); }
        }

        /// <summary>
        /// The number that is one greater than the largest representable SHA-256 hash.
        /// </summary>
        private static readonly BigInteger _largestHash = BigInteger.One.ShiftLeft(256);

        /// <summary>
        /// Returns the work represented by this block.
        /// </summary>
        /// <remarks>
        /// Work is defined as the number of tries needed to solve a block in the average case. Consider a difficulty
        /// target that covers 5% of all possible hash values. Then the work of the block will be 20. As the target gets
        /// lower, the amount of work goes up.
        /// </remarks>
        /// <exception cref="BitCoinSharp.VerificationException" />
        public BigInteger GetWork()
        {
            var target = GetDifficultyTargetAsInteger();
            return _largestHash.Divide(target.Add(BigInteger.One));
        }

        /// <summary>
        /// Returns a copy of the block, but without any transactions.
        /// </summary>
        public Block CloneAsHeader()
        {
            return new Block(Params, BitcoinSerialize()) {Transactions = null};
        }

        /// <summary>
        /// Returns a multi-line string containing a description of the contents of the block. Use for debugging purposes
        /// only.
        /// </summary>
        public override string ToString()
        {
            var s = new StringBuilder();
            s.AppendFormat("v{0} block:", _version).AppendLine();
            s.AppendFormat("   previous block: {0}", Utils.BytesToHexString(_prevBlockHash)).AppendLine();
            s.AppendFormat("   merkle root: {0}", Utils.BytesToHexString(MerkleRoot)).AppendLine();
            s.AppendFormat("   time: [{0}] {1}", _time, new DateTime(_time*1000)).AppendLine();
            s.AppendFormat("   difficulty target (nBits): {0}", _difficultyTarget).AppendLine();
            s.AppendFormat("   nonce: {0}", _nonce).AppendLine();
            if (Transactions != null && Transactions.Count > 0)
            {
                s.AppendFormat("   with {0} transaction(s):", Transactions.Count).AppendLine();
                foreach (var tx in Transactions)
                {
                    s.Append(tx.ToString());
                }
            }
            return s.ToString();
        }

        /// <summary>
        /// Finds a value of nonce that makes the blocks hash lower than the difficulty target. This is called mining,
        /// but solve() is far too slow to do real mining with. It exists only for unit testing purposes and is not a part
        /// of the public API.
        /// </summary>
        /// <remarks>
        /// This can loop forever if a solution cannot be found solely by incrementing nonce. It doesn't change extraNonce.
        /// </remarks>
        internal void Solve()
        {
            while (true)
            {
                // Is our proof of work valid yet?
                if (CheckProofOfWork(false)) return;
                // No, so increment the nonce and try again.
                Nonce++;
            }
        }

        /// <summary>
        /// Returns the difficulty target as a 256 bit value that can be compared to a SHA-256 hash. Inside a block the
        /// target is represented using a compact form. If this form decodes to a value that is out of bounds,
        /// an exception is thrown.
        /// </summary>
        /// <exception cref="BitCoinSharp.VerificationException" />
        public BigInteger GetDifficultyTargetAsInteger()
        {
            var target = Utils.DecodeCompactBits(_difficultyTarget);
            if (target.CompareTo(BigInteger.Zero) <= 0 || target.CompareTo(Params.ProofOfWorkLimit) > 0)
                throw new VerificationException("Difficulty target is bad: " + target);
            return target;
        }

        /// <summary>
        /// Returns true if the hash of the block is OK (lower than difficulty target).
        /// </summary>
        /// <exception cref="BitCoinSharp.VerificationException" />
        private bool CheckProofOfWork(bool throwException)
        {
            // This part is key - it is what proves the block was as difficult to make as it claims
            // to be. Note however that in the context of this function, the block can claim to be
            // as difficult as it wants to be .... if somebody was able to take control of our network
            // connection and fork us onto a different chain, they could send us valid blocks with
            // ridiculously easy difficulty and this function would accept them.
            //
            // To prevent this attack from being possible, elsewhere we check that the difficultyTarget
            // field is of the right value. This requires us to have the preceding blocks.
            var target = GetDifficultyTargetAsInteger();

            var h = new BigInteger(1, Hash);
            if (h.CompareTo(target) > 0)
            {
                // Proof of work check failed!
                if (throwException)
                    throw new VerificationException("Hash is higher than target: " + HashAsString + " vs " +
                                                    target.ToString(16));
                return false;
            }
            return true;
        }

        /// <exception cref="BitCoinSharp.VerificationException" />
        private void CheckTimestamp()
        {
            // Allow injection of a fake clock to allow unit testing.
            var currentTime = FakeClock != 0 ? FakeClock : UnixTime.ToUnixTime(DateTime.UtcNow);
            if (_time > currentTime + _allowedTimeDrift)
                throw new VerificationException("Block too far in future");
        }

        /// <exception cref="BitCoinSharp.VerificationException" />
        private void CheckMerkleHash()
        {
            var tree = BuildMerkleTree();
            var calculatedRoot = tree[tree.Count - 1];
            if (!calculatedRoot.SequenceEqual(_merkleRoot))
            {
                _log.Error("Merkle tree did not verify: ");
                foreach (var b in tree) _log.Error(Utils.BytesToHexString(b));

                throw new VerificationException("Merkle hashes do not match: " +
                                                Utils.BytesToHexString(calculatedRoot) + " vs " + Utils.BytesToHexString(_merkleRoot));
            }
        }

        private byte[] CalculateMerkleRoot()
        {
            var tree = BuildMerkleTree();
            return tree[tree.Count - 1];
        }

        private IList<byte[]> BuildMerkleTree()
        {
            // The Merkle root is based on a tree of hashes calculated from the transactions:
            //
            //          root
            //             /\
            //            /  \
            //          A      B
            //         / \    / \
            //       t1 t2  t3 t4
            //
            // The tree is represented as a list: t1,t2,t3,t4,A,B,root where each entry is a hash.
            //
            // The hashing algorithm is double SHA-256. The leaves are a hash of the serialized contents of the
            // transaction. The interior nodes are hashes of the concentration of the two child hashes.
            //
            // This structure allows the creation of proof that a transaction was included into a block without having to
            // provide the full block contents. Instead, you can provide only a Merkle branch. For example to prove tx2 was
            // in a block you can just provide tx2, the hash(tx1) and B. Now the other party has everything they need to
            // derive the root, which can be checked against the block header. These proofs aren't used right now but
            // will be helpful later when we want to download partial block contents.
            //
            // Note that if the number of transactions is not a power of two the last tx is repeated to make it so (see
            // tx3 above). A tree with 5 transactions would look like this:
            //
            //                    root
            //                  /      \
            //                /          \
            //              1             6
            //            /  \          /   \
            //          2     3       4      5
            //         / \   / \    /  \   /  \
            //       t1 t2  t3 t4  t5 t5  t5 t5
            var tree = new List<byte[]>();
            // Start by adding all the hashes of the transactions as leaves of the tree.
            foreach (var t in Transactions)
            {
                tree.Add(t.Hash.Hash);
            }
            var levelOffset = 0; // Offset in the list where the currently processed level starts.
            // Step through each level, stopping when we reach the root (levelSize == 1).
            for (var levelSize = Transactions.Count; levelSize > 1; levelSize = (levelSize + 1)/2)
            {
                // For each pair of nodes on that level:
                for (var left = 0; left < levelSize; left += 2)
                {
                    // The right hand node can be the same as the left hand, in the case where we don't have enough
                    // transactions to be a power of two.
                    var right = Math.Min(left + 1, levelSize - 1);
                    var leftBytes = Utils.ReverseBytes(tree[levelOffset + left]);
                    var rightBytes = Utils.ReverseBytes(tree[levelOffset + right]);
                    tree.Add(Utils.ReverseBytes(Utils.DoubleDigestTwoBuffers(leftBytes, 0, 32, rightBytes, 0, 32)));
                }
                // Move to the next level.
                levelOffset += levelSize;
            }
            return tree;
        }

        /// <exception cref="BitCoinSharp.VerificationException" />
        private void CheckTransactions()
        {
            // The first transaction in a block must always be a coinbase transaction.
            if (!Transactions[0].IsCoinBase)
                throw new VerificationException("First tx is not coinbase");
            // The rest must not be.
            for (var i = 1; i < Transactions.Count; i++)
            {
                if (Transactions[i].IsCoinBase)
                    throw new VerificationException("TX " + i + " is coinbase when it should not be.");
            }
        }

        /// <summary>
        /// Checks the block data to ensure it follows the rules laid out in the network parameters. Specifically, throws
        /// an exception if the proof of work is invalid, if the timestamp is too far from what it should be, or if the
        /// transactions don't hash to the value in the merkle root field. This is <b>not</b> everything that is required
        /// for a block to be valid, only what is checkable independent of the chain.
        /// </summary>
        /// <exception cref="BitCoinSharp.VerificationException" />
        public void Verify()
        {
            // Prove that this block is OK. It might seem that we can just ignore most of these checks given that the
            // network is also verifying the blocks, but we cannot as it'd open us to a variety of obscure attacks.
            //
            // Firstly we need to ensure this block does in fact represent real work done. If the difficulty is high
            // enough, it's probably been done by the network.
            CheckProofOfWork(true);
            CheckTimestamp();
            // Now we need to check that the body of the block actually matches the headers. The network won't generate
            // an invalid block, but if we didn't validate this then an untrusted man-in-the-middle could obtain the next
            // valid block from the network and simply replace the transactions in it with their own fictional
            // transactions that reference spent or non-existant inputs.
            if (Transactions != null)
            {
                Debug.Assert(Transactions.Count > 0);
                CheckTransactions();
                CheckMerkleHash();
            }
        }

        public override bool Equals(object o)
        {
            if (!(o is Block)) return false;
            var other = (Block) o;
            return Hash.SequenceEqual(other.Hash);
        }

        public override int GetHashCode()
        {
            var hash = Hash;
            return hash != null ? hash.Aggregate(1, (current, element) => 31*current + element) : 0;
        }

        /// <summary>
        /// Returns the merkle root in big endian form, calculating it from transactions if necessary.
        /// </summary>
        public byte[] MerkleRoot
        {
            get { return _merkleRoot ?? (_merkleRoot = CalculateMerkleRoot()); }
            internal set // Exists only for unit testing.
            {
                _merkleRoot = value;
                _hash = null;
            }
        }

        /// <summary>
        /// Adds a transaction to this block.
        /// </summary>
        internal void AddTransaction(Transaction t)
        {
            if (Transactions == null)
            {
                Transactions = new List<Transaction>();
            }
            Transactions.Add(t);
            // Force a recalculation next time the values are needed.
            _merkleRoot = null;
            _hash = null;
        }

        /// <summary>
        /// Returns the version of the block data structure as defined by the BitCoin protocol.
        /// </summary>
        public long Version
        {
            get { return _version; }
        }

        /// <summary>
        /// Returns the hash of the previous block in the chain, as defined by the block header.
        /// </summary>
        public byte[] PrevBlockHash
        {
            get { return _prevBlockHash; }
            internal set
            {
                _prevBlockHash = value;
                _hash = null;
            }
        }

        /// <summary>
        /// Returns the time at which the block was solved and broadcast, according to the clock of the solving node.
        /// </summary>
        public uint Time
        {
            get { return _time; }
            set
            {
                _time = value;
                _hash = null;
            }
        }

        /// <summary>
        /// Returns the difficulty of the proof of work that this block should meet encoded in compact form. The
        /// <see cref="BlockChain">BlockChain</see> verifies that this is not too easy by looking at the length of the chain when the block is
        /// added. To find the actual value the hash should be compared against, use getDifficultyTargetBI.
        /// </summary>
        public uint DifficultyTarget
        {
            get { return _difficultyTarget; }
            internal set
            {
                _difficultyTarget = value;
                _hash = null;
            }
        }

        /// <summary>
        /// Returns the nonce, an arbitrary value that exists only to make the hash of the block header fall below the
        /// difficulty target.
        /// </summary>
        public uint Nonce
        {
            get { return _nonce; }
            internal set
            {
                _nonce = value;
                _hash = null;
            }
        }

        /////////////////////////////////////////////////////////////////////////////////////////////////
        // Unit testing related methods.

        private static int _coinbaseCounter;

        /// <summary>
        /// Adds a coinbase transaction to the block. This exists for unit tests.
        /// </summary>
        internal void AddCoinbaseTransaction(Address to)
        {
            Transactions = new List<Transaction>();
            var coinbase = new Transaction(Params);
            // A real coinbase transaction has some stuff in the scriptSig like the extraNonce and difficulty. The
            // transactions are distinguished by every TX output going to a different key.
            //
            // Here we will do things a bit differently so a new address isn't needed every time. We'll put a simple
            // counter in the scriptSig so every transaction has a different hash. The output is also different.
            // Real coinbase transactions use <pubkey> OP_CHECKSIG rather than a send to an address though there's
            // nothing in the system that enforces that and both are just as valid.
            coinbase.AddInput(new TransactionInput(Params, coinbase, new[] {(byte) _coinbaseCounter++}));
            coinbase.AddOutput(new TransactionOutput(Params, coinbase, Utils.ToNanoCoins(50, 0), to));
            Transactions.Add(coinbase);
        }

        /// <summary>
        /// Returns a solved block that builds on top of this one. This exists for unit tests.
        /// </summary>
        internal Block CreateNextBlock(Address to, uint time)
        {
            var b = new Block(Params);
            b.DifficultyTarget = _difficultyTarget;
            b.AddCoinbaseTransaction(to);
            b.PrevBlockHash = Hash;
            b.Time = time;
            b.Solve();
            b.Verify();
            return b;
        }

        internal Block CreateNextBlock(Address to)
        {
            return CreateNextBlock(to, (uint) UnixTime.ToUnixTime(DateTime.UtcNow));
        }
    }
}