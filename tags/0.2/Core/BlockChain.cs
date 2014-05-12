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
using System.Linq;
using log4net;
using Org.BouncyCastle.Math;

namespace BitCoinSharp
{
    /// <summary>
    /// A BlockChain holds a series of <see cref="Block">Block</see> objects, links them together, and knows how to verify that the
    /// chain follows the rules of the <see cref="NetworkParameters">NetworkParameters</see> for this chain.
    /// </summary>
    /// <remarks>
    /// A BlockChain requires a <see cref="Wallet">Wallet</see> to receive transactions that it finds during the initial download. However,
    /// if you don't care about this, you can just pass in an empty wallet and nothing bad will happen.<p />
    /// A newly constructed BlockChain is empty. To fill it up, use a <see cref="Peer">Peer</see> object to download the chain from the
    /// network.<p />
    /// <b>Notes</b><p />
    /// The 'chain' can actually be a tree although in normal operation it can be thought of as a simple list. In such a
    /// situation there are multiple stories of the economy competing to become the one true consensus. This can happen
    /// naturally when two miners solve a block within a few seconds of each other, or it can happen when the chain is
    /// under attack.<p />
    /// A reference to the head block of every chain is stored. If you can reach the genesis block by repeatedly walking
    /// through the prevBlock pointers, then we say this is a full chain. If you cannot reach the genesis block we say it is
    /// an orphan chain.<p />
    /// Orphan chains can occur when blocks are solved and received during the initial block chain download,
    /// or if we connect to a peer that doesn't send us blocks in order.
    /// </remarks>
    public class BlockChain
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof (BlockChain));

        /// <summary>
        /// Keeps a map of block hashes to StoredBlocks.
        /// </summary>
        private readonly IBlockStore _blockStore;

        /// <summary>
        /// Tracks the top of the best known chain.
        /// </summary>
        /// <remarks>
        /// Following this one down to the genesis block produces the story of the economy from the creation of BitCoin
        /// until the present day. The chain head can change if a new set of blocks is received that results in a chain of
        /// greater work than the one obtained by following this one down. In that case a reorganize is triggered,
        /// potentially invalidating transactions in our wallet.
        /// </remarks>
        private StoredBlock _chainHead;

        private readonly NetworkParameters _params;
        private readonly Wallet _wallet;

        // Holds blocks that we have received but can't plug into the chain yet, eg because they were created whilst we
        // were downloading the block chain.
        private readonly IList<Block> _unconnectedBlocks = new List<Block>();

        /// <summary>
        /// Constructs a BlockChain connected to the given wallet and store. To obtain a <see cref="Wallet">Wallet</see> you can construct
        /// one from scratch, or you can deserialize a saved wallet from disk using <see cref="Wallet.LoadFromFile(System.IO.FileInfo)">Wallet.LoadFromFile(System.IO.FileInfo)</see><p />
        /// </summary>
        /// <remarks>
        /// For the store you can use a <see cref="MemoryBlockStore">MemoryBlockStore</see> if you don't care about saving the downloaded data, or a
        /// <see cref="BoundedOverheadBlockStore">BoundedOverheadBlockStore</see> if you'd like to ensure fast start-up the next time you run the program.
        /// </remarks>
        public BlockChain(NetworkParameters @params, Wallet wallet, IBlockStore blockStore)
        {
            _blockStore = blockStore;
            _chainHead = blockStore.GetChainHead();
            _log.InfoFormat("chain head is:{0}{1}", Environment.NewLine, _chainHead.Header);
            _params = @params;
            _wallet = wallet;
        }

        /// <summary>
        /// Processes a received block and tries to add it to the chain. If there's something wrong with the block an
        /// exception is thrown. If the block is OK but cannot be connected to the chain at this time, returns false.
        /// If the block can be connected to the chain, returns true.
        /// </summary>
        /// <exception cref="BitCoinSharp.VerificationException" />
        /// <exception cref="BitCoinSharp.ScriptException" />
        public bool Add(Block block)
        {
            lock (this)
            {
                return Add(block, true);
            }
        }

        // Stat counters.
        private int _statsLastTime = Environment.TickCount;
        private long _statsBlocksAdded;

        /// <exception cref="BitCoinSharp.BlockStoreException" />
        /// <exception cref="BitCoinSharp.VerificationException" />
        /// <exception cref="BitCoinSharp.ScriptException" />
        private bool Add(Block block, bool tryConnecting)
        {
            lock (this)
            {
                if (Environment.TickCount - _statsLastTime > 1000)
                {
                    // More than a second passed since last stats logging.
                    _log.InfoFormat("{0} blocks per second", _statsBlocksAdded);
                    _statsLastTime = Environment.TickCount;
                    _statsBlocksAdded = 0;
                }
                // We check only the chain head for double adds here to avoid potentially expensive block chain misses.
                if (block.Equals(_chainHead.Header))
                {
                    // Duplicate add of the block at the top of the chain, can be a natural artifact of the download process.
                    return true;
                }

                // Prove the block is internally valid: hash is lower than target, merkle root is correct and so on.
                try
                {
                    block.Verify();
                }
                catch (VerificationException e)
                {
                    _log.Error("Failed to verify block:", e);
                    _log.Error(block.ToString());
                    throw;
                }

                // Try linking it to a place in the currently known blocks.
                var storedPrev = _blockStore.Get(block.PrevBlockHash);

                if (storedPrev == null)
                {
                    // We can't find the previous block. Probably we are still in the process of downloading the chain and a
                    // block was solved whilst we were doing it. We put it to one side and try to connect it later when we
                    // have more blocks.
                    _log.WarnFormat("Block does not connect: {0}", block.HashAsString);
                    _unconnectedBlocks.Add(block);
                    return false;
                }
                // It connects to somewhere on the chain. Not necessarily the top of the best known chain.
                //
                // Create a new StoredBlock from this block. It will throw away the transaction data so when block goes
                // out of scope we will reclaim the used memory.
                var newStoredBlock = storedPrev.Build(block);
                CheckDifficultyTransitions(storedPrev, newStoredBlock);
                _blockStore.Put(newStoredBlock);
                // block.transactions may be null here if we received only a header and not a full block. This does not
                // happen currently but might in future if GetHeaders is implemented.
                ConnectBlock(newStoredBlock, storedPrev, block.Transactions);

                if (tryConnecting)
                    TryConnectingUnconnected();

                _statsBlocksAdded++;
                return true;
            }
        }

        /// <exception cref="BitCoinSharp.BlockStoreException" />
        /// <exception cref="BitCoinSharp.VerificationException" />
        private void ConnectBlock(StoredBlock newStoredBlock, StoredBlock storedPrev, IEnumerable<Transaction> newTransactions)
        {
            if (storedPrev.Equals(_chainHead))
            {
                // This block connects to the best known block, it is a normal continuation of the system.
                ChainHead = newStoredBlock;
                _log.DebugFormat("Chain is now {0} blocks high", _chainHead.Height);
                if (newTransactions != null)
                    SendTransactionsToWallet(newStoredBlock, NewBlockType.BestChain, newTransactions);
            }
            else
            {
                // This block connects to somewhere other than the top of the best known chain. We treat these differently.
                //
                // Note that we send the transactions to the wallet FIRST, even if we're about to re-organize this block
                // to become the new best chain head. This simplifies handling of the re-org in the Wallet class.
                var haveNewBestChain = newStoredBlock.MoreWorkThan(_chainHead);
                if (haveNewBestChain)
                {
                    _log.Info("Block is causing a re-organize");
                }
                else
                {
                    var splitPoint = FindSplit(newStoredBlock, _chainHead);
                    var splitPointHash = splitPoint != null ? splitPoint.Header.HashAsString : "?";
                    _log.InfoFormat("Block forks the chain at {0}, but it did not cause a reorganize:{1}{2}",
                                    splitPointHash, Environment.NewLine, newStoredBlock);
                }

                // We may not have any transactions if we received only a header. That never happens today but will in
                // future when GetHeaders is used as an optimization.
                if (newTransactions != null)
                {
                    SendTransactionsToWallet(newStoredBlock, NewBlockType.SideChain, newTransactions);
                }

                if (haveNewBestChain)
                    HandleNewBestChain(newStoredBlock);
            }
        }

        /// <summary>
        /// Called as part of connecting a block when the new block results in a different chain having higher total work.
        /// </summary>
        /// <exception cref="BitCoinSharp.BlockStoreException" />
        /// <exception cref="BitCoinSharp.VerificationException" />
        private void HandleNewBestChain(StoredBlock newChainHead)
        {
            // This chain has overtaken the one we currently believe is best. Reorganize is required.
            //
            // Firstly, calculate the block at which the chain diverged. We only need to examine the
            // chain from beyond this block to find differences.
            var splitPoint = FindSplit(newChainHead, _chainHead);
            _log.InfoFormat("Re-organize after split at height {0}", splitPoint.Height);
            _log.InfoFormat("Old chain head: {0}", _chainHead.Header.HashAsString);
            _log.InfoFormat("New chain head: {0}", newChainHead.Header.HashAsString);
            _log.InfoFormat("Split at block: {0}", splitPoint.Header.HashAsString);
            // Then build a list of all blocks in the old part of the chain and the new part.
            var oldBlocks = GetPartialChain(_chainHead, splitPoint);
            var newBlocks = GetPartialChain(newChainHead, splitPoint);
            // Now inform the wallet. This is necessary so the set of currently active transactions (that we can spend)
            // can be updated to take into account the re-organize. We might also have received new coins we didn't have
            // before and our previous spends might have been undone.
            _wallet.Reorganize(oldBlocks, newBlocks);
            // Update the pointer to the best known block.
            ChainHead = newChainHead;
        }

        /// <summary>
        /// Returns the set of contiguous blocks between 'higher' and 'lower'. Higher is included, lower is not.
        /// </summary>
        /// <exception cref="BitCoinSharp.BlockStoreException" />
        private IList<StoredBlock> GetPartialChain(StoredBlock higher, StoredBlock lower)
        {
            Debug.Assert(higher.Height > lower.Height);
            var results = new LinkedList<StoredBlock>();
            var cursor = higher;
            while (true)
            {
                results.AddLast(cursor);
                cursor = cursor.GetPrev(_blockStore);
                Debug.Assert(cursor != null, "Ran off the end of the chain");
                if (cursor.Equals(lower)) break;
            }
            return results.ToList();
        }

        /// <summary>
        /// Locates the point in the chain at which newStoredBlock and chainHead diverge. Returns null if no split point was
        /// found (ie they are part of the same chain).
        /// </summary>
        /// <exception cref="BitCoinSharp.BlockStoreException" />
        private StoredBlock FindSplit(StoredBlock newChainHead, StoredBlock chainHead)
        {
            var currentChainCursor = chainHead;
            var newChainCursor = newChainHead;
            // Loop until we find the block both chains have in common. Example:
            //
            //    A -> B -> C -> D
            //         \--> E -> F -> G
            //
            // findSplit will return block B. chainHead = D and newChainHead = G.
            while (!currentChainCursor.Equals(newChainCursor))
            {
                if (currentChainCursor.Height > newChainCursor.Height)
                {
                    currentChainCursor = currentChainCursor.GetPrev(_blockStore);
                    Debug.Assert(newChainCursor != null, "Attempt to follow an orphan chain");
                }
                else
                {
                    newChainCursor = newChainCursor.GetPrev(_blockStore);
                    Debug.Assert(currentChainCursor != null, "Attempt to follow an orphan chain");
                }
            }
            return currentChainCursor;
        }

        internal enum NewBlockType
        {
            BestChain,
            SideChain
        }

        /// <exception cref="BitCoinSharp.VerificationException" />
        private void SendTransactionsToWallet(StoredBlock block, NewBlockType blockType, IEnumerable<Transaction> newTransactions)
        {
            // Scan the transactions to find out if any mention addresses we own.
            foreach (var tx in newTransactions)
            {
                try
                {
                    ScanTransaction(block, tx, blockType);
                }
                catch (ScriptException e)
                {
                    // We don't want scripts we don't understand to break the block chain,
                    // so just note that this tx was not scanned here and continue.
                    _log.WarnFormat("Failed to parse a script: {0}", e);
                }
            }
        }

        /// <summary>
        /// For each block in unconnectedBlocks, see if we can now fit it on top of the chain and if so, do so.
        /// </summary>
        /// <exception cref="BitCoinSharp.VerificationException" />
        /// <exception cref="BitCoinSharp.ScriptException" />
        /// <exception cref="BitCoinSharp.BlockStoreException" />
        private void TryConnectingUnconnected()
        {
            // For each block in our unconnected list, try and fit it onto the head of the chain. If we succeed remove it
            // from the list and keep going. If we changed the head of the list at the end of the round try again until
            // we can't fit anything else on the top.
            int blocksConnectedThisRound;
            do
            {
                blocksConnectedThisRound = 0;
                foreach (var block in _unconnectedBlocks.ToList())
                {
                    var prev = _blockStore.Get(block.PrevBlockHash);
                    if (prev == null)
                    {
                        // This is still an unconnected/orphan block.
                        continue;
                    }
                    // Otherwise we can connect it now.
                    // False here ensures we don't recurse infinitely downwards when connecting huge chains.
                    Add(block, false);
                    _unconnectedBlocks.Remove(block);
                    blocksConnectedThisRound++;
                }
                if (blocksConnectedThisRound > 0)
                {
                    _log.InfoFormat("Connected {0} floating blocks.", blocksConnectedThisRound);
                }
            } while (blocksConnectedThisRound > 0);
        }

        /// <summary>
        /// Throws an exception if the blocks difficulty is not correct.
        /// </summary>
        /// <exception cref="BitCoinSharp.BlockStoreException" />
        /// <exception cref="BitCoinSharp.VerificationException" />
        private void CheckDifficultyTransitions(StoredBlock storedPrev, StoredBlock storedNext)
        {
            var prev = storedPrev.Header;
            var next = storedNext.Header;
            // Is this supposed to be a difficulty transition point?
            if ((storedPrev.Height + 1)%_params.Interval != 0)
            {
                // No ... so check the difficulty didn't actually change.
                if (next.DifficultyTarget != prev.DifficultyTarget)
                    throw new VerificationException("Unexpected change in difficulty at height " + storedPrev.Height +
                                                    ": " + next.DifficultyTarget.ToString("x") + " vs " +
                                                    prev.DifficultyTarget.ToString("x"));
                return;
            }

            // We need to find a block far back in the chain. It's OK that this is expensive because it only occurs every
            // two weeks after the initial block chain download.
            var now = Environment.TickCount;
            var cursor = _blockStore.Get(prev.Hash);
            for (var i = 0; i < _params.Interval - 1; i++)
            {
                if (cursor == null)
                {
                    // This should never happen. If it does, it means we are following an incorrect or busted chain.
                    throw new VerificationException(
                        "Difficulty transition point but we did not find a way back to the genesis block.");
                }
                cursor = _blockStore.Get(cursor.Header.PrevBlockHash);
            }
            _log.InfoFormat("Difficulty transition traversal took {0}ms", Environment.TickCount - now);

            var blockIntervalAgo = cursor.Header;
            var timespan = (int) (prev.Time - blockIntervalAgo.Time);
            // Limit the adjustment step.
            if (timespan < _params.TargetTimespan/4)
                timespan = _params.TargetTimespan/4;
            if (timespan > _params.TargetTimespan*4)
                timespan = _params.TargetTimespan*4;

            var newDifficulty = Utils.DecodeCompactBits(blockIntervalAgo.DifficultyTarget);
            newDifficulty = newDifficulty.Multiply(BigInteger.ValueOf(timespan));
            newDifficulty = newDifficulty.Divide(BigInteger.ValueOf(_params.TargetTimespan));

            if (newDifficulty.CompareTo(_params.ProofOfWorkLimit) > 0)
            {
                _log.WarnFormat("Difficulty hit proof of work limit: {0}", newDifficulty.ToString(16));
                newDifficulty = _params.ProofOfWorkLimit;
            }

            var accuracyBytes = (int) (next.DifficultyTarget >> 24) - 3;
            var receivedDifficulty = next.GetDifficultyTargetAsInteger();

            // The calculated difficulty is to a higher precision than received, so reduce here.
            var mask = BigInteger.ValueOf(0xFFFFFF).ShiftLeft(accuracyBytes*8);
            newDifficulty = newDifficulty.And(mask);

            if (newDifficulty.CompareTo(receivedDifficulty) != 0)
                throw new VerificationException("Network provided difficulty bits do not match what was calculated: " +
                                                receivedDifficulty.ToString(16) + " vs " + newDifficulty.ToString(16));
        }

        /// <exception cref="BitCoinSharp.ScriptException" />
        /// <exception cref="BitCoinSharp.VerificationException" />
        private void ScanTransaction(StoredBlock block, Transaction tx, NewBlockType blockType)
        {
            var shouldReceive = false;
            foreach (var output in tx.Outputs)
            {
                // TODO: Handle more types of outputs, not just regular to address outputs.
                if (output.ScriptPubKey.IsSentToIp) return;
                // This is not thread safe as a key could be removed between the call to isMine and receive.
                if (output.IsMine(_wallet))
                {
                    shouldReceive = true;
                }
            }

            // Coinbase transactions don't have anything useful in their inputs (as they create coins out of thin air).
            if (!tx.IsCoinBase)
            {
                foreach (var i in tx.Inputs)
                {
                    var pubkey = i.ScriptSig.PubKey;
                    // This is not thread safe as a key could be removed between the call to isPubKeyMine and receive.
                    if (_wallet.IsPubKeyMine(pubkey))
                    {
                        shouldReceive = true;
                    }
                }
            }

            if (shouldReceive)
                _wallet.Receive(tx, block, blockType);
        }

        /// <summary>
        /// Returns the block at the head of the current best chain. This is the block which represents the greatest
        /// amount of cumulative work done.
        /// </summary>
        public StoredBlock ChainHead
        {
            get
            {
                lock (this)
                {
                    return _chainHead;
                }
            }
            private set
            {
                _chainHead = value;
                _blockStore.SetChainHead(value);
            }
        }

        /// <summary>
        /// Returns the most recent unconnected block or null if there are none. This will all have to change.
        /// </summary>
        public Block UnconnectedBlock
        {
            get
            {
                lock (this)
                {
                    return _unconnectedBlocks.LastOrDefault();
                }
            }
        }
    }
}