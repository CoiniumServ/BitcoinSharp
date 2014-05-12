// Original source: http://springnet.cvs.sourceforge.net/viewvc/springnet/Spring.Net/sandbox/src/Spring/Spring.Threading/Threading/Helpers/CountDownLatch.cs

using System;
using System.Threading;

namespace BitCoinSharp.Threading
{
    /// <summary>
    /// A synchronization aid that allows one or more threads to wait until
    /// a set of operations being performed in other threads completes.
    /// </summary>
    /// <remarks>
    /// A <see cref="BitCoinSharp.Threading.CountDownLatch"/> is initialized with a given
    /// <b>count</b>. The <see cref="BitCoinSharp.Threading.CountDownLatch.Await()"/> and <see cref="BitCoinSharp.Threading.CountDownLatch.Await(TimeSpan)"/>
    /// methods block until the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/>
    /// reaches zero due to invocations of the
    /// <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown()"/> method, after which all waiting threads are
    /// released and any subsequent invocations of <see cref="BitCoinSharp.Threading.CountDownLatch.Await()"/> and <see cref="BitCoinSharp.Threading.CountDownLatch.Await(TimeSpan)"/> return
    /// immediately. This is a one-shot phenomenon -- the count cannot be
    /// reset.
    ///
    /// <p/>
    /// A <see cref="BitCoinSharp.Threading.CountDownLatch"/> is a versatile synchronization tool
    /// and can be used for a number of purposes. A
    /// <see cref="BitCoinSharp.Threading.CountDownLatch"/> initialized with a count of one serves as a
    /// simple on/off latch, or gate: all threads invoking <see cref="BitCoinSharp.Threading.CountDownLatch.Await()"/> and <see cref="BitCoinSharp.Threading.CountDownLatch.Await(TimeSpan)"/>
    /// wait at the gate until it is opened by a thread invoking <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown()"/>.
    /// A <see cref="BitCoinSharp.Threading.CountDownLatch"/> initialized to <i>N</i>
    /// can be used to make one thread wait until <i>N</i> threads have
    /// completed some action, or some action has been completed <i>N</i> times.
    ///
    /// <p/>
    /// A useful property of a <see cref="BitCoinSharp.Threading.CountDownLatch"/> is that it
    /// doesn't require that threads calling <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown()"/> wait for
    /// the count to reach zero before proceeding, it simply prevents any
    /// thread from proceeding past an <see cref="BitCoinSharp.Threading.CountDownLatch.Await()"/> and <see cref="BitCoinSharp.Threading.CountDownLatch.Await(TimeSpan)"/> until all
    /// threads could pass.
    ///
    /// <p/>
    /// <b>Sample usage:</b>
    /// <br/>
    /// Here is a pair of classes in which a group
    /// of worker threads use two countdown latches:
    /// <ul>
    /// <li>The first is a start signal that prevents any worker from proceeding
    /// until the driver is ready for them to proceed.</li>
    /// <li>The second is a completion signal that allows the driver to wait
    /// until all workers have completed.</li>
    /// </ul>
    ///
    /// <code>
    /// public class Driver { // ...
    ///             void Main() {
    ///                     CountDownLatch startSignal = new CountDownLatch(1);
    ///                     CountDownLatch doneSignal = new CountDownLatch(N);
    ///
    ///                     for (int i = 0; i &lt; N; ++i)
    ///                             new Thread(new ThreadStart(new Worker(startSignal, doneSignal).Run).Start();
    ///
    ///             doSomethingElse();            // don't let run yet
    ///                     startSignal.CountDown();      // let all threads proceed
    ///             doSomethingElse();
    ///             doneSignal.Await();           // wait for all to finish
    ///     }
    /// }
    ///
    /// public class Worker : IRunnable {
    ///             private CountDownLatch startSignal;
    ///     private CountDownLatch doneSignal;
    ///     Worker(CountDownLatch startSignal, CountDownLatch doneSignal) {
    ///             this.startSignal = startSignal;
    ///             this.doneSignal = doneSignal;
    ///     }
    ///     public void Run() {
    ///             try {
    ///                     startSignal.Await();
    ///                     doWork();
    ///                     doneSignal.CountDown();
    ///             } catch (ThreadInterruptedException ex) {} // return;
    ///     }
    ///
    ///     void doWork() { ... }
    /// }
    ///
    /// </code>
    /// </remarks>
    /// <author>Doug Lea</author>
    /// <author>Griffin Caprio (.NET)</author>
    public class CountDownLatch
    {
        private int _count;

        /// <summary>
        /// Returns the current count.
        /// </summary>
        /// <remarks>
        /// This method is typically used for debugging and testing purposes.
        /// </remarks>
        /// <returns>The current count.</returns>
        public long Count
        {
            get { return _count; }
        }

        /// <summary>
        /// Constructs a <see cref="BitCoinSharp.Threading.CountDownLatch"/> initialized with the given
        /// <paramref name="count"/>.
        /// </summary>
        /// <param name="count">The number of times <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown"/> must be invoked
        /// before threads can pass through <see cref="BitCoinSharp.Threading.CountDownLatch.Await()"/>.
        /// </param>
        /// <exception cref="System.ArgumentException">If <paramref name="count"/> is less than 0.</exception>
        public CountDownLatch(int count)
        {
            if (count < 0)
                throw new ArgumentException("Count must be greater than 0.", "count");
            _count = count;
        }

        /// <summary>
        /// Causes the current thread to wait until the latch has counted down to
        /// zero, unless <see cref="System.Threading.Thread.Interrupt()"/> is called on the thread.
        /// </summary>
        /// <remarks>
        /// If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> is zero then this method
        /// returns immediately.
        /// <p/>If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> is greater than zero then
        /// the current thread becomes disabled for thread scheduling
        /// purposes and lies dormant until the count reaches zero due to invocations of the
        /// <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown()"/> method or
        /// some other thread calls <see cref="System.Threading.Thread.Interrupt()"/> on the current
        /// thread.
        /// <p/>
        /// A <see cref="System.Threading.ThreadInterruptedException"/> is thrown if the thread is interrupted.
        /// </remarks>
        /// <exception cref="System.Threading.ThreadInterruptedException">If the current thread is interrupted.</exception>
        public void Await()
        {
            lock (this)
            {
                while (_count > 0)
                    Monitor.Wait(this);
            }
        }

        /// <summary>
        /// Causes the current thread to wait until the latch has counted down to
        /// zero, unless <see cref="System.Threading.Thread.Interrupt()"/> is called on the thread or
        /// the specified <paramref name="duration"/> elapses.
        /// </summary>
        /// <remarks>
        /// <p/>
        /// If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> is zero then this method
        /// returns immediately.
        /// <p/>If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> is greater than zero then
        /// the current thread becomes disabled for thread scheduling
        /// purposes and lies dormant until the count reaches zero due to invocations of the
        /// <see cref="BitCoinSharp.Threading.CountDownLatch.CountDown()"/> method or
        /// some other thread calls <see cref="System.Threading.Thread.Interrupt()"/> on the current
        /// thread.
        /// <p/>
        /// A <see cref="System.Threading.ThreadInterruptedException"/> is thrown if the thread is interrupted.
        /// <p/>
        /// If the specified <paramref name="duration"/> elapses then the value <see lang="false"/>
        /// is returned. If the time is less than or equal to zero, the method will not wait at all.
        /// </remarks>
        /// <param name="duration">the maximum time to wait</param>
        /// <returns>
        /// <see lang="true"/> if the count reached zero and <see lang="false"/>
        /// if the waiting time elapsed before the count reached zero.
        /// </returns>
        /// <exception cref="System.Threading.ThreadInterruptedException">If the current thread is interrupted.</exception>
        public bool Await(TimeSpan duration)
        {
            var durationToWait = duration;
            lock (this)
            {
                if (_count <= 0)
                    return true;
                if (durationToWait.Ticks <= 0)
                    return false;
                var deadline = DateTime.Now.Add(durationToWait);
                for (;;)
                {
                    Monitor.Wait(this, durationToWait);
                    if (_count <= 0)
                        return true;
                    durationToWait = deadline.Subtract(DateTime.Now);
                    if (durationToWait.Ticks <= 0)
                        return false;
                }
            }
        }

        /// <summary>
        /// Decrements the count of the latch, releasing all waiting threads if
        /// the count reaches zero.
        /// </summary>
        /// <remarks>
        /// If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> is greater than zero then
        /// it is decremented. If the new count is zero then all waiting threads
        /// are re-enabled for thread scheduling purposes. If the current <see cref="BitCoinSharp.Threading.CountDownLatch.Count"/> equals zero then nothing
        /// happens.
        /// </remarks>
        public void CountDown()
        {
            lock (this)
            {
                if (_count == 0)
                    return;
                if (--_count == 0)
                    Monitor.PulseAll(this);
            }
        }

        /// <summary>
        /// Returns a string identifying this latch, as well as its state.
        /// </summary>
        /// <remarks>
        /// The state, in brackets, includes the String
        /// &quot;Count =&quot; followed by the current count.
        /// </remarks>
        /// <returns>A string identifying this latch, as well as its state</returns>
        public override String ToString()
        {
            return base.ToString() + "[Count = " + Count + "]";
        }
    }
}