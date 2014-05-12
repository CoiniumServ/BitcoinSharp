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
using System.Diagnostics;
using System.Linq;

// TODO: Switch all code/interfaces to using this class.

namespace BitCoinSharp
{
    /// <summary>
    /// A Sha256Hash just wraps a byte[] so that equals and hashcode work correctly, allowing it to be used as keys in a
    /// map. It also checks that the length is correct and provides a bit more type safety.
    /// </summary>
    [Serializable]
    public class Sha256Hash
    {
        public byte[] Hash { get; private set; }

        public Sha256Hash(byte[] hash)
        {
            Debug.Assert(hash.Length == 32);
            Hash = hash;
        }

        /// <summary>
        /// Returns true if the hashes are equal.
        /// </summary>
        public override bool Equals(object other)
        {
            if (!(other is Sha256Hash)) return false;
            return Hash.SequenceEqual(((Sha256Hash) other).Hash);
        }

        /// <summary>
        /// Hash code of the byte array as calculated by <see cref="object.GetHashCode()">object.GetHashCode()</see>. Note the difference between a SHA256
        /// secure hash and the type of quick/dirty hash used by the Java hashCode method which is designed for use in
        /// hash tables.
        /// </summary>
        public override int GetHashCode()
        {
            return Hash != null ? Hash.Aggregate(1, (current, element) => 31*current + element) : 0;
        }

        public override string ToString()
        {
            return Utils.BytesToHexString(Hash);
        }
    }
}