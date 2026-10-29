using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log
    {
        internal static void Info(string message) { }
        internal static void Error(string message, Exception error) { }
    }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static void Wait(Func<bool> done, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.ElapsedMilliseconds < 3000) Thread.Sleep(5);
        Check(done(), message);
    }

    private static void BlockedIoAndSnapshots()
    {
        var entered = new ManualResetEventSlim(); var releaseIo = new ManualResetEventSlim();
        var callerReturned = new ManualResetEventSlim(); var releaseCaller = new ManualResetEventSlim();
        var calls = new List<OrderCdrLinkQueue.Snapshot>();
        int active = 0, maximum = 0, callerThread = 0, workerThread = 0;
        using (var queue = new OrderCdrLinkQueue(value => {
            int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            lock (calls) calls.Add(value);
            if (value.CdrPath == "old.cdr")
            {
                workerThread = Thread.CurrentThread.ManagedThreadId; entered.Set();
                if (!releaseIo.Wait(2500)) throw new TimeoutException("Fake NAS was not released");
            }
            Interlocked.Decrement(ref active);
            return value.CdrPath != "old.cdr";
        }, retryDelay: TimeSpan.FromMilliseconds(20)))
        {
            Task caller = Task.Run(() => {
                callerThread = Thread.CurrentThread.ManagedThreadId;
                queue.Enqueue(" SERVER ", 1, "old.cdr", "year-original");
                callerReturned.Set(); releaseCaller.Wait(2500);
            });
            try
            {
                Check(callerReturned.Wait(1000), "Enqueue returns while NAS is blocked");
                Check(entered.Wait(1000), "Single worker entered fake NAS");
                Check(workerThread != callerThread, "File work never runs on the caller thread");
                for (int i = 0; i < 1000; i++) queue.Enqueue("server", 1, "old.cdr", "year-original");
                string path = "second.cdr", year = "year-second";
                queue.Enqueue("server", 2, path, year, false);
                path = "changed-after-enqueue.cdr"; year = "changed-after-enqueue";
                queue.Enqueue("server", 3, "third.cdr", "year-third");
                queue.Enqueue("server", 1, "new.cdr", "year-new");
                lock (calls) Check(calls.Count == 1, "Coalescing creates no parallel calls behind blocked NAS");
                releaseIo.Set(); releaseCaller.Set(); caller.Wait(1000);
                Wait(() => { lock (calls) return calls.Count == 4 && Volatile.Read(ref active) == 0; }, "All different orders and latest path drain");
                Thread.Sleep(50);
                lock (calls)
                {
                    Check(calls.Count == 4, "Superseded failed path cannot retry and overwrite the new path");
                    Check(calls.Count(value => value.CdrPath == "old.cdr") == 1, "Identical repeated snapshots coalesce");
                    Check(calls.Any(value => value.OrderId == 2 && value.CdrPath == "second.cdr" && value.YearFolder == "year-second" && !value.ResolveDocumentYear),
                        "Captured path, year and resolution policy do not change with caller state");
                    Check(calls.Any(value => value.OrderId == 3), "Changing documents does not discard another order");
                    Check(calls.Last().OrderId == 1 && calls.Last().CdrPath == "new.cdr", "Latest path wins for one order");
                }
                Check(maximum == 1, "Only one file operation runs at a time");
                queue.Enqueue("SERVER", 1, "NEW.CDR", "YEAR-NEW");
                Thread.Sleep(50);
                lock (calls) Check(calls.Count == 4, "Completed identical Windows paths are deduplicated");
            }
            finally { releaseIo.Set(); releaseCaller.Set(); }
        }
    }

    private static void FailuresAndRetry()
    {
        int attempts = 0, errors = 0;
        using (var queue = new OrderCdrLinkQueue(value => {
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1) throw new IOException("fake unavailable share");
            return attempt >= 3;
        }, error => Interlocked.Increment(ref errors), retryDelay: TimeSpan.FromMilliseconds(15)))
        {
            queue.Enqueue("server", 10, "retry.cdr", "year");
            Wait(() => Volatile.Read(ref attempts) == 3, "Failure and exception receive bounded automatic retries");
            Thread.Sleep(40);
            Check(errors == 1, "Exception is observed once without crashing the worker");
            Check(attempts == 3, "Success stops automatic retries");
            queue.Enqueue("server", 10, "retry.cdr", "year"); Thread.Sleep(40);
            Check(attempts == 3, "Success stays deduplicated");
        }

        int failures = 0;
        using (var queue = new OrderCdrLinkQueue(value => Interlocked.Increment(ref failures) >= 3,
            retryDelay: TimeSpan.FromMilliseconds(15), failureCooldown: TimeSpan.FromMilliseconds(130), maxAttempts: 2))
        {
            queue.Enqueue("server", 11, "offline.cdr", "year");
            Wait(() => Volatile.Read(ref failures) == 2, "Max attempts reached");
            Thread.Sleep(30);
            for (int i = 0; i < 100; i++) queue.Enqueue("server", 11, "offline.cdr", "year");
            Thread.Sleep(40);
            Check(failures == 2, "Repeated idle notifications do not create a retry storm");
            Wait(() => Volatile.Read(ref failures) == 3, "Closed document keeps a pending link and retries after cooldown");
            Thread.Sleep(160);
            Check(failures == 3, "Successful recovery stops the slow retry timer");
        }
    }

    private static void NewOrdersDoNotWaitForRetryDelay()
    {
        var order = new List<int>(); int first = 0;
        using (var queue = new OrderCdrLinkQueue(value => {
            lock (order) order.Add(value.OrderId);
            return value.OrderId != 1 || Interlocked.Increment(ref first) > 1;
        }, retryDelay: TimeSpan.FromMilliseconds(180)))
        {
            queue.Enqueue("server", 1, "one.cdr", "year");
            Wait(() => Volatile.Read(ref first) == 1, "First order failed once");
            queue.Enqueue("server", 2, "two.cdr", "year");
            Wait(() => { lock (order) return order.Count == 3; }, "New order and delayed retry finish");
            lock (order) Check(order.SequenceEqual(new[] { 1, 2, 1 }), "Another order runs before a failed order's retry delay");
        }
    }

    private static void BoundedHistoryAndNonBlockingDispose()
    {
        int count = 0;
        using (var queue = new OrderCdrLinkQueue(value => { Interlocked.Increment(ref count); return true; }, historyLimit: 2))
        {
            for (int id = 1; id <= 3; id++) { queue.Enqueue("server", id, "a.cdr", "year"); int expected = id; Wait(() => Volatile.Read(ref count) == expected, "History job completes"); }
            Thread.Sleep(20);
            queue.Enqueue("server", 1, "a.cdr", "year");
            Wait(() => Volatile.Read(ref count) == 4, "Completed dedupe history has a finite limit");
        }
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var blocked = new OrderCdrLinkQueue(value => { entered.Set(); release.Wait(2000); return true; });
        blocked.Enqueue("server", 1, "a.cdr", "year");
        Check(entered.Wait(1000), "Dispose test enters fake NAS");
        var elapsed = Stopwatch.StartNew(); blocked.Dispose();
        Check(elapsed.ElapsedMilliseconds < 300, "Dispose never waits for SMB on caller thread");
        blocked.Enqueue("server", 2, "b.cdr", "year"); release.Set();
    }

    private static int Main()
    {
        try
        {
            BlockedIoAndSnapshots(); FailuresAndRetry(); NewOrdersDoNotWaitForRetryDelay(); BoundedHistoryAndNonBlockingDispose();
            Console.WriteLine("Passed " + _checks + " order-link background checks."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
