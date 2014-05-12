To get started, ensure you have the latest .net SDK installed and run "msbuild" from the src folder.

Now ensure you're running a BitCoin node locally and run the example app:

   cd bin/Debug
   BitCoinSharp.Examples PingService

It will download the block chain and eventually print a BitCoin address. If you send coins to it,
you should get them back a few minutes later when a block is solved.

Note that if you connect to a node that is itself downloading the block chain, you will see very slow progress (1
block per second or less). Find a node that isn't heavily loaded to connect to.

If you get a SocketException, the node you've connected to has its max send buffer set to low
(unfortunately the default is too low). Connect to a node that has a bigger send buffer,
settable by passing -maxsendbuffer=25600 to the Bitcoin C++ software.