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

using NUnit.Framework;

namespace BitCoinSharp.Test
{
    [TestFixture]
    public class WalletTest
    {
        private static readonly NetworkParameters _params = NetworkParameters.UnitTests();

        private Address _myAddress;
        private Wallet _wallet;
        private IBlockStore _blockStore;

        [SetUp]
        public void SetUp()
        {
            var myKey = new EcKey();
            _myAddress = myKey.ToAddress(_params);
            _wallet = new Wallet(_params);
            _wallet.AddKey(myKey);
            _blockStore = new MemoryBlockStore(_params);
        }

        [TearDown]
        public void TearDown()
        {
            _blockStore.Dispose();
        }

        private static Transaction CreateFakeTx(ulong nanocoins, Address to)
        {
            var t = new Transaction(_params);
            var o1 = new TransactionOutput(_params, t, nanocoins, to);
            t.AddOutput(o1);
            // Make a previous tx simply to send us sufficient coins. This previous tx is not really valid but it doesn't
            // matter for our purposes.
            var prevTx = new Transaction(_params);
            var prevOut = new TransactionOutput(_params, prevTx, nanocoins, to);
            prevTx.AddOutput(prevOut);
            // Connect it.
            t.AddInput(prevOut);
            return t;
        }

        private class BlockPair
        {
            public StoredBlock StoredBlock { get; set; }
            public Block Block { get; set; }
        }

        // Emulates receiving a valid block that builds on top of the chain.
        private BlockPair CreateFakeBlock(params Transaction[] transactions)
        {
            var b = _blockStore.GetChainHead().Header.CreateNextBlock(new EcKey().ToAddress(_params));
            foreach (var tx in transactions)
                b.AddTransaction(tx);
            b.Solve();
            var pair = new BlockPair();
            pair.Block = b;
            pair.StoredBlock = _blockStore.GetChainHead().Build(b);
            _blockStore.Put(pair.StoredBlock);
            _blockStore.SetChainHead(pair.StoredBlock);
            return pair;
        }

        [Test]
        public void TestBasicSpending()
        {
            // We'll set up a wallet that receives a coin, then sends a coin of lesser value and keeps the change.
            var v1 = Utils.ToNanoCoins(1, 0);
            var t1 = CreateFakeTx(v1, _myAddress);

            _wallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(v1, _wallet.GetBalance());

            var k2 = new EcKey();
            var v2 = Utils.ToNanoCoins(0, 50);
            var t2 = _wallet.CreateSend(k2.ToAddress(_params), v2);

            // Do some basic sanity checks.
            Assert.AreEqual(1, t2.Inputs.Count);
            Assert.AreEqual(_myAddress, t2.Inputs[0].ScriptSig.FromAddress);

            // We have NOT proven that the signature is correct!
        }

        [Test]
        public void TestSideChain()
        {
            // The wallet receives a coin on the main chain, then on a side chain. Only main chain counts towards balance.
            var v1 = Utils.ToNanoCoins(1, 0);
            var t1 = CreateFakeTx(v1, _myAddress);

            _wallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(v1, _wallet.GetBalance());

            var v2 = Utils.ToNanoCoins(0, 50);
            var t2 = CreateFakeTx(v2, _myAddress);
            _wallet.Receive(t2, null, BlockChain.NewBlockType.SideChain);

            Assert.AreEqual(v1, _wallet.GetBalance());
        }

        [Test]
        public void TestListener()
        {
            var fakeTx = CreateFakeTx(Utils.ToNanoCoins(1, 0), _myAddress);
            var didRun = false;
            _wallet.CoinsReceived +=
                (sender, e) =>
                {
                    Assert.True(e.PrevBalance.Equals(0));
                    Assert.True(e.NewBalance.Equals(Utils.ToNanoCoins(1, 0)));
                    Assert.AreEqual(e.Tx, fakeTx);
                    Assert.AreEqual(sender, _wallet);
                    didRun = true;
                };
            _wallet.Receive(fakeTx, null, BlockChain.NewBlockType.BestChain);
            Assert.True(didRun);
        }

        [Test]
        public void TestBalance()
        {
            // Receive 5 coins then half a coin.
            var v1 = Utils.ToNanoCoins(5, 0);
            var v2 = Utils.ToNanoCoins(0, 50);
            var t1 = CreateFakeTx(v1, _myAddress);
            var t2 = CreateFakeTx(v2, _myAddress);
            var b1 = CreateFakeBlock(t1).StoredBlock;
            var b2 = CreateFakeBlock(t2).StoredBlock;
            var expected = Utils.ToNanoCoins(5, 50);
            _wallet.Receive(t1, b1, BlockChain.NewBlockType.BestChain);
            _wallet.Receive(t2, b2, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(expected, _wallet.GetBalance());

            // Now spend one coin.
            var v3 = Utils.ToNanoCoins(1, 0);
            var spend = _wallet.CreateSend(new EcKey().ToAddress(_params), v3);
            _wallet.ConfirmSend(spend);

            // Available and estimated balances should not be the same. We don't check the exact available balance here
            // because it depends on the coin selection algorithm.
            Assert.AreEqual(Utils.ToNanoCoins(4, 50), _wallet.GetBalance(Wallet.BalanceType.Estimated));
            Assert.False(_wallet.GetBalance(Wallet.BalanceType.Available).Equals(
                _wallet.GetBalance(Wallet.BalanceType.Estimated)));

            // Now confirm the transaction by including it into a block.
            var b3 = CreateFakeBlock(spend).StoredBlock;
            _wallet.Receive(spend, b3, BlockChain.NewBlockType.BestChain);

            // Change is confirmed. We started with 5.50 so we should have 4.50 left.
            var v4 = Utils.ToNanoCoins(4, 50);
            Assert.AreEqual(v4, _wallet.GetBalance(Wallet.BalanceType.Available));
        }

        // Intuitively you'd expect to be able to create a transaction with identical inputs and outputs and get an
        // identical result to the official client. However the signatures are not deterministic - signing the same data
        // with the same key twice gives two different outputs. So we cannot prove bit-for-bit compatibility in this test
        // suite.
        [Test]
        public void TestBlockChainCatchup()
        {
            var tx1 = CreateFakeTx(Utils.ToNanoCoins(1, 0), _myAddress);
            var b1 = CreateFakeBlock(tx1).StoredBlock;
            _wallet.Receive(tx1, b1, BlockChain.NewBlockType.BestChain);
            // Send 0.10 to somebody else.
            var send1 = _wallet.CreateSend(new EcKey().ToAddress(_params), Utils.ToNanoCoins(0, 10), _myAddress);
            // Pretend it makes it into the block chain, our wallet state is cleared but we still have the keys, and we
            // want to get back to our previous state. We can do this by just not confirming the transaction as
            // createSend is stateless.
            var b2 = CreateFakeBlock(send1).StoredBlock;
            _wallet.Receive(send1, b2, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(Utils.BitcoinValueToFriendlyString(_wallet.GetBalance()), "0.90");
            // And we do it again after the catch-up.
            var send2 = _wallet.CreateSend(new EcKey().ToAddress(_params), Utils.ToNanoCoins(0, 10), _myAddress);
            // What we'd really like to do is prove the official client would accept it .... no such luck unfortunately.
            _wallet.ConfirmSend(send2);
            var b3 = CreateFakeBlock(send2).StoredBlock;
            _wallet.Receive(send2, b3, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(Utils.BitcoinValueToFriendlyString(_wallet.GetBalance()), "0.80");
        }

        [Test]
        public void TestBalances()
        {
            var nanos = Utils.ToNanoCoins(1, 0);
            var tx1 = CreateFakeTx(nanos, _myAddress);
            _wallet.Receive(tx1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(nanos, tx1.GetValueSentToMe(_wallet, true));
            // Send 0.10 to somebody else.
            var send1 = _wallet.CreateSend(new EcKey().ToAddress(_params), Utils.ToNanoCoins(0, 10), _myAddress);
            // Re-serialize.
            var send2 = new Transaction(_params, send1.BitcoinSerialize());
            Assert.AreEqual(nanos, send2.GetValueSentFromMe(_wallet));
        }

        [Test]
        public void TestFinneyAttack()
        {
            // A Finney attack is where a miner includes a transaction spending coins to themselves but does not
            // broadcast it. When they find a solved block, they hold it back temporarily whilst they buy something with
            // those same coins. After purchasing, they broadcast the block thus reversing the transaction. It can be
            // done by any miner for products that can be bought at a chosen time and very quickly (as every second you
            // withhold your block means somebody else might find it first, invalidating your work).
            //
            // Test that we handle ourselves performing the attack correctly: a double spend on the chain moves
            // transactions from pending to dead.
            //
            // Note that the other way around, where a pending transaction sending us coins becomes dead,
            // isn't tested because today BitCoinJ only learns about such transactions when they appear in the chain.
            Transaction eventDead = null;
            Transaction eventReplacement = null;
            _wallet.DeadTransaction +=
                (sender, e) =>
                {
                    eventDead = e.DeadTx;
                    eventReplacement = e.ReplacementTx;
                };

            // Receive 1 BTC.
            var nanos = Utils.ToNanoCoins(1, 0);
            var t1 = CreateFakeTx(nanos, _myAddress);
            _wallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            // Create a send to a merchant.
            var send1 = _wallet.CreateSend(new EcKey().ToAddress(_params), Utils.ToNanoCoins(0, 50));
            // Create a double spend.
            var send2 = _wallet.CreateSend(new EcKey().ToAddress(_params), Utils.ToNanoCoins(0, 50));
            // Broadcast send1.
            _wallet.ConfirmSend(send1);
            // Receive a block that overrides it.
            _wallet.Receive(send2, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(send1, eventDead);
            Assert.AreEqual(send2, eventReplacement);
        }
    }
}