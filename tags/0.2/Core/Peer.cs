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
using System.Threading;
using BitCoinSharp.Threading;
using log4net;

namespace BitCoinSharp
{
    /// <summary>
    /// A Peer handles the high level communication with a BitCoin node. It requires a NetworkConnection to be set up for
    /// it. After that it takes ownership of the connection, creates and manages its own thread used for communication
    /// with the network. All these threads synchronize on the block chain.
    /// </summary>
    public class Peer
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof (Peer));

        private readonly NetworkConnection _conn;
        private readonly NetworkParameters _params;
        private Thread _thread;
        // Whether the peer thread is supposed to be running or not. Set to false during shutdown so the peer thread
        // knows to quit when the socket goes away.
        private bool _running;
        private readonly BlockChain _blockChain;

        // Used to notify clients when the initial block chain download is finished.
        private CountDownLatch _chainCompletionLatch;
        // When we want to download a block or transaction from a peer, the InventoryItem is put here whilst waiting for
        // the response. Synchronized on itself.
        private readonly IList<GetDataFuture<Block>> _pendingGetBlockFutures;

        /// <summary>
        /// Construct a peer that handles the given network connection and reads/writes from the given block chain. Note that
        /// communication won't occur until you call start().
        /// </summary>
        public Peer(NetworkParameters @params, NetworkConnection conn, BlockChain blockChain)
        {
            _conn = conn;
            _params = @params;
            _blockChain = blockChain;
            _pendingGetBlockFutures = new List<GetDataFuture<Block>>();
        }

        /// <summary>
        /// Starts the background thread that processes messages.
        /// </summary>
        public void Start()
        {
            _thread = new Thread(Run);
            lock (this)
            {
                _running = true;
            }
            _thread.Name = "BitCoin peer thread: " + _conn;
            _thread.Start();
        }

        /// <summary>
        /// Runs in the peers network thread and manages communication with the peer.
        /// </summary>
        private void Run()
        {
            Debug.Assert(Thread.CurrentThread == _thread);
            try
            {
                while (true)
                {
                    var m = _conn.ReadMessage();
                    if (m is InventoryMessage)
                    {
                        ProcessInv((InventoryMessage) m);
                    }
                    else if (m is Block)
                    {
                        ProcessBlock((Block) m);
                    }
                    else if (m is AddressMessage)
                    {
                        // We don't care about addresses of the network right now. But in future,
                        // we should save them in the wallet so we don't put too much load on the seed nodes and can
                        // properly explore the network.
                    }
                    else
                    {
                        // TODO: Handle the other messages we can receive.
                        _log.WarnFormat("Received unhandled message: {0}", m);
                    }
                }
            }
            catch (Exception e)
            {
                if (e is IOException && !_running)
                {
                    // This exception was expected because we are tearing down the socket as part of quitting.
                    _log.Info("Shutting down peer thread");
                }
                else
                {
                    // We caught an unexpected exception.
                    Console.Error.WriteLine(e);
                }
            }
            lock (this)
            {
                _running = false;
            }
        }

        /// <exception cref="System.IO.IOException" />
        private void ProcessBlock(Block m)
        {
            Debug.Assert(Thread.CurrentThread == _thread);
            try
            {
                // Was this block requested by getblock?
                lock (_pendingGetBlockFutures)
                {
                    for (var i = 0; i < _pendingGetBlockFutures.Count; i++)
                    {
                        var f = _pendingGetBlockFutures[i];
                        if (f.Item.Hash.SequenceEqual(m.Hash))
                        {
                            // Yes, it was. So pass it through the future.
                            f.SetResult(m);
                            // Blocks explicitly requested don't get sent to the block chain.
                            _pendingGetBlockFutures.RemoveAt(i);
                            return;
                        }
                    }
                }
                // Otherwise it's a block sent to us because the peer thought we needed it, so add it to the block chain.
                // This call will synchronize on blockChain.
                if (_blockChain.Add(m))
                {
                    // The block was successfully linked into the chain. Notify the user of our progress.
                    if (_chainCompletionLatch != null)
                    {
                        _chainCompletionLatch.CountDown();
                        if (_chainCompletionLatch.Count == 0)
                        {
                            // All blocks fetched, so we don't need this anymore.
                            _chainCompletionLatch = null;
                        }
                    }
                }
                else
                {
                    // This block is unconnected - we don't know how to get from it back to the genesis block yet. That
                    // must mean that there are blocks we are missing, so do another getblocks with a new block locator
                    // to ask the peer to send them to us. This can happen during the initial block chain download where
                    // the peer will only send us 500 at a time and then sends us the head block expecting us to request
                    // the others.

                    // TODO: Should actually request root of orphan chain here.
                    BlockChainDownload(m.Hash);
                }
            }
            catch (VerificationException e)
            {
                // We don't want verification failures to kill the thread.
                _log.Warn("block verification failed", e);
            }
            catch (ScriptException e)
            {
                // We don't want script failures to kill the thread.
                _log.Warn("script exception", e);
            }
        }

        /// <exception cref="System.IO.IOException" />
        private void ProcessInv(InventoryMessage inv)
        {
            Debug.Assert(Thread.CurrentThread == _thread);
            // The peer told us about some blocks or transactions they have. For now we only care about blocks.
            // Note that as we don't actually want to store the entire block chain or even the headers of the block
            // chain, we may end up requesting blocks we already requested before. This shouldn't (in theory) happen
            // enough to be a problem.
            var topBlock = _blockChain.UnconnectedBlock;
            var topHash = (topBlock != null ? topBlock.Hash : null);
            var items = inv.Items;
            if (items.Count == 1 && items[0].Type == InventoryItem.ItemType.Block && topHash != null &&
                items[0].Hash.SequenceEqual(topHash))
            {
                // An inv with a single hash containing our most recent unconnected block is a special inv,
                // it's kind of like a tickle from the peer telling us that it's time to download more blocks to catch up to
                // the block chain. We could just ignore this and treat it as a regular inv but then we'd download the head
                // block over and over again after each batch of 500 blocks, which is wasteful.
                BlockChainDownload(topHash);
                return;
            }
            var getdata = new GetDataMessage(_params);
            var dirty = false;
            foreach (var item in items)
            {
                if (item.Type != InventoryItem.ItemType.Block) continue;
                getdata.AddItem(item);
                dirty = true;
            }
            // No blocks to download. This probably contained transactions instead, but right now we can't prove they are
            // valid so we don't bother downloading transactions that aren't in blocks yet.
            if (!dirty)
                return;
            // This will cause us to receive a bunch of block messages.
            _conn.WriteMessage(getdata);
        }

        /// <summary>
        /// Asks the connected peer for the block of the given hash, and returns a Future representing the answer.
        /// If you want the block right away and don't mind waiting for it, just call .get() on the result. Your thread
        /// will block until the peer answers. You can also use the Future object to wait with a timeout, or just check
        /// whether it's done later.
        /// </summary>
        /// <param name="blockHash">Hash of the block you were requesting.</param>
        /// <exception cref="System.IO.IOException" />
        public GetDataFuture<Block> GetBlock(byte[] blockHash)
        {
            var getdata = new InventoryMessage(_params);
            var inventoryItem = new InventoryItem(InventoryItem.ItemType.Block, blockHash);
            getdata.AddItem(inventoryItem);
            var future = new GetDataFuture<Block>(this, inventoryItem);
            // Add to the list of things we're waiting for. It's important this come before the network send to avoid
            // race conditions.
            lock (_pendingGetBlockFutures)
            {
                _pendingGetBlockFutures.Add(future);
            }
            _conn.WriteMessage(getdata);
            return future;
        }

        // A GetDataFuture wraps the result of a getblock or (in future) getTransaction so the owner of the object can
        // decide whether to wait forever, wait for a short while or check later after doing other work.
        public class GetDataFuture<T>
        {
            private readonly Peer _enclosing;
            private bool _cancelled;
            private readonly InventoryItem _item;
            private readonly CountDownLatch _latch;
            private T _result;

            internal GetDataFuture(Peer enclosing, InventoryItem item)
            {
                _enclosing = enclosing;
                _item = item;
                _latch = new CountDownLatch(1);
            }

            public bool Cancel()
            {
                // Cannot cancel a getdata - once sent, it's sent.
                _cancelled = true;
                return false;
            }

            public bool IsCancelled
            {
                get { return _cancelled; }
            }

            public bool IsDone
            {
                get { return !Equals(_result, default(T)) || _cancelled; }
            }

            public T Get()
            {
                _latch.Await();
                Debug.Assert(!Equals(_result, default(T)));
                return _result;
            }

            /// <exception cref="System.TimeoutException" />
            public T Get(TimeSpan timeout)
            {
                if (!_latch.Await(timeout))
                    throw new TimeoutException();
                Debug.Assert(!Equals(_result, default(T)));
                return _result;
            }

            internal InventoryItem Item
            {
                get { return _item; }
            }

            /// <summary>
            /// Called by the Peer when the result has arrived. Completes the task.
            /// </summary>
            internal void SetResult(T result)
            {
                Debug.Assert(Thread.CurrentThread == _enclosing._thread); // Called from peer thread.
                _result = result;
                // Now release the thread that is waiting. We don't need to synchronize here as the latch establishes
                // a memory barrier.
                _latch.CountDown();
            }
        }

        /// <summary>
        /// Send the given Transaction, ie, make a payment with BitCoins. To create a transaction you can broadcast, use
        /// a <see cref="Wallet">Wallet</see>. After the broadcast completes, confirm the send using the wallet confirmSend() method.
        /// </summary>
        /// <exception cref="System.IO.IOException" />
        internal void BroadcastTransaction(Transaction tx)
        {
            _conn.WriteMessage(tx);
        }

        /// <exception cref="System.IO.IOException" />
        private void BlockChainDownload(byte[] toHash)
        {
            // This may run in ANY thread.

            // The block chain download process is a bit complicated. Basically, we start with zero or more blocks in a
            // chain that we have from a previous session. We want to catch up to the head of the chain BUT we don't know
            // where that chain is up to or even if the top block we have is even still in the chain - we
            // might have got ourselves onto a fork that was later resolved by the network.
            //
            // To solve this, we send the peer a block locator which is just a list of block hashes. It contains the
            // blocks we know about, but not all of them, just enough of them so the peer can figure out if we did end up
            // on a fork and if so, what the earliest still valid block we know about is likely to be.
            //
            // Once it has decided which blocks we need, it will send us an inv with up to 500 block messages. We may
            // have some of them already if we already have a block chain and just need to catch up. Once we request the
            // last block, if there are still more to come it sends us an "inv" containing only the hash of the head
            // block.
            //
            // That causes us to download the head block but then we find (in processBlock) that we can't connect
            // it to the chain yet because we don't have the intermediate blocks. So we rerun this function building a
            // new block locator describing where we're up to.
            //
            // The getblocks with the new locator gets us another inv with another bunch of blocks. We download them once
            // again. This time when the peer sends us an inv with the head block, we already have it so we won't download
            // it again - but we recognize this case as special and call back into blockChainDownload to continue the
            // process.
            //
            // So this is a complicated process but it has the advantage that we can download a chain of enormous length
            // in a relatively stateless manner and with constant/bounded memory usage.
            _log.InfoFormat("blockChainDownload({0})", Utils.BytesToHexString(toHash));

            // TODO: Block locators should be abstracted out rather than special cased here.
            var blockLocator = new LinkedList<byte[]>();
            // We don't do the exponential thinning here, so if we get onto a fork of the chain we will end up
            // re-downloading the whole thing again.
            blockLocator.AddLast(_params.GenesisBlock.Hash);
            var topBlock = _blockChain.ChainHead.Header;
            if (!topBlock.Equals(_params.GenesisBlock))
                blockLocator.AddFirst(topBlock.Hash);
            var message = new GetBlocksMessage(_params, blockLocator.ToList(), toHash);
            _conn.WriteMessage(message);
        }

        /// <summary>
        /// Starts an asynchronous download of the block chain. The chain download is deemed to be complete once we've
        /// downloaded the same number of blocks that the peer advertised having in its version handshake message.
        /// </summary>
        /// <returns>
        /// A <see cref="BitCoinSharp.Threading.CountDownLatch">BitCoinSharp.Threading.CountDownLatch</see> that can be used to track progress and wait for completion.
        /// </returns>
        /// <exception cref="System.IO.IOException" />
        public CountDownLatch StartBlockChainDownload()
        {
            // Chain will overflow signed int blocks in ~41,000 years.
            var chainHeight = _conn.VersionMessage.BestHeight;
            if (chainHeight == 0)
            {
                // This should not happen because we shouldn't have given the user a Peer that is to another client-mode
                // node. If that happens it means the user overrode us somewhere.
                throw new Exception("Peer does not have block chain");
            }
            var blocksToGet = (int) (chainHeight - _blockChain.ChainHead.Height);
            _chainCompletionLatch = new CountDownLatch(blocksToGet);
            if (blocksToGet > 0)
            {
                // When we just want as many blocks as possible, we can set the target hash to zero.
                BlockChainDownload(new byte[32]);
            }
            return _chainCompletionLatch;
        }

        /// <summary>
        /// Terminates the network connection and stops the background thread.
        /// </summary>
        public void Disconnect()
        {
            lock (this)
            {
                _running = false;
            }
            try
            {
                // This will cause the background thread to die, but it's really ugly. We must do a better job of this.
                _conn.Shutdown();
            }
            catch (IOException)
            {
                // Don't care about this.
            }
        }
    }
}