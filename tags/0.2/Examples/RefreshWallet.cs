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
using System.IO;
using System.Net;

namespace BitCoinSharp.Examples
{
    /// <summary>
    /// RefreshWallet loads a wallet, then processes the block chain to update the transaction pools within it.
    /// </summary>
    public class RefreshWallet
    {
        public static void Run(string[] args)
        {
            var file = new FileInfo(args[0]);
            var wallet = Wallet.LoadFromFile(file);
            Console.WriteLine(wallet.ToString());

            // Set up the components and link them together.
            var @params = NetworkParameters.TestNet();
            var blockStore = new MemoryBlockStore(@params);
            var conn = new NetworkConnection(IPAddress.Loopback, @params,
                                             blockStore.GetChainHead().Height, 60000);
            var chain = new BlockChain(@params, wallet, blockStore);
            var peer = new Peer(@params, conn, chain);
            peer.Start();

            wallet.CoinsReceived +=
                (sender, e) =>
                {
                    Console.WriteLine();
                    Console.WriteLine("Received tx " + e.Tx.HashAsString);
                    Console.WriteLine(e.Tx.ToString());
                };

            // Now download and process the block chain.
            var progress = peer.StartBlockChainDownload();
            var max = progress.Count; // Racy but no big deal.
            if (max > 0)
            {
                Console.WriteLine("Downloading block chain. " + (max > 1000 ? "This may take a while." : ""));
                var current = max;
                while (current > 0)
                {
                    var pct = 100.0 - (100.0*(current/(double) max));
                    Console.WriteLine(string.Format("Chain download {0}% done", (int) pct));
                    progress.Await(TimeSpan.FromSeconds(1));
                    current = progress.Count;
                }
            }
            peer.Disconnect();
            wallet.SaveToFile(file);
            Console.WriteLine();
            Console.WriteLine("Done!");
            Console.WriteLine();
            Console.WriteLine(wallet.ToString());
        }
    }
}