BitcoinSharp
============

Fork of abandoned BitcoinSharp project - https://code.google.com/p/bitcoinsharp

============

BitCoinSharp is a direct port of the excellent BitCoinJ library. It implements the native BitCoin P2P protocol in C# for use in .net 3.5 and above. This library can be used to maintain a wallet and send/receive transactions without installing the official client. It comes with full documentation and includes an example application that demonstrates some of the more common uses.

This project aims to be easier to understand than the C++ implementation and more suitable for use on constrained devices such as mobile phones once Silverlight support has been added.

BitCoinSharp implements the "simplified payment verification" mode of Satoshi's paper. It does not store a full copy of the block chain but rather it stores what it needs in order to verify transactions with the aid of an untrusted peer node.

Download the library and then read:

* [The Getting Started guide](http://code.google.com/p/bitcoinj/wiki/GettingStarted)
* [The online API documentation](http://bitcoinj.googlecode.com/svn/trunk/docs/index.html)

Got questions? Comments? Patches? Please use the BitCoinJ discussion group for the time being but keep in mind this forum is focused on the original Java implementation from which this library was ported.

Be warned: This software is still in an early state of development. It is not safe to use with serious quantities of money or on production networks. Doing so risks losing coins or creating spends that will never be confirmed.
