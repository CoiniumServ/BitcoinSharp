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
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using BitCoinSharp.IO;

namespace BitCoinSharp
{
    /// <summary>
    /// Keeps <see cref="StoredBlock">StoredBlock</see>s in memory. Used primarily for unit testing.
    /// </summary>
    public class MemoryBlockStore : IBlockStore
    {
        // We use a ByteBuffer to hold hashes here because the Java array equals()/hashcode() methods do not operate on
        // the contents of the array but just inherit the default Object behavior. ByteBuffer provides the functionality
        // needed to act as a key in a map.
        //
        // The StoredBlocks are also stored as serialized objects to ensure we don't have assumptions that would make
        // things harder for disk based implementations.
        private IDictionary<ByteBuffer, byte[]> _blockMap;
        private StoredBlock _chainHead;

        public MemoryBlockStore(NetworkParameters @params)
        {
            _blockMap = new Dictionary<ByteBuffer, byte[]>();
            // Insert the genesis block.
            var genesisHeader = @params.GenesisBlock.CloneAsHeader();
            var storedGenesis = new StoredBlock(genesisHeader, genesisHeader.GetWork(), 0);
            Put(storedGenesis);
            SetChainHead(storedGenesis);
        }

        /// <exception cref="BitCoinSharp.BlockStoreException" />
        public void Put(StoredBlock block)
        {
            lock (this)
            {
                var hash = block.Header.Hash;
                using (var bos = new MemoryStream())
                {
                    var oos = new BinaryFormatter();
                    oos.Serialize(bos, block);
                    _blockMap[ByteBuffer.Wrap(hash)] = bos.ToArray();
                }
            }
        }

        /// <exception cref="BitCoinSharp.BlockStoreException" />
        public StoredBlock Get(byte[] hash)
        {
            lock (this)
            {
                try
                {
                    byte[] serializedBlock;
                    using (var key = ByteBuffer.Wrap(hash))
                    {
                        if (!_blockMap.TryGetValue(key, out serializedBlock))
                            return null;
                    }
                    using (var stream = new MemoryStream(serializedBlock))
                    {
                        var ois = new BinaryFormatter();
                        var storedBlock = (StoredBlock) ois.Deserialize(stream);
                        return storedBlock;
                    }
                }
                catch (IOException e)
                {
                    throw new BlockStoreException(e);
                }
                catch (TypeLoadException e)
                {
                    throw new BlockStoreException(e);
                }
            }
        }

        public StoredBlock GetChainHead()
        {
            return _chainHead;
        }

        /// <exception cref="BitCoinSharp.BlockStoreException" />
        public void SetChainHead(StoredBlock chainHead)
        {
            _chainHead = chainHead;
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (_blockMap != null)
            {
                foreach (var key in _blockMap.Keys)
                {
                    key.Dispose();
                }
                _blockMap = null;
            }
        }

        #endregion
    }
}