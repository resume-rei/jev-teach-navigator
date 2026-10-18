using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JevTeachWindows {
  // Exercises worker scheduling/cancellation only, using synthetic functions. These
  // tests do not initialize UIA, a desktop, a keyboard hook or a COM provider.
  static class UiaWorkerTests {
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Canceled(Task task) {
      try { await task; throw new Exception("expected cancellation"); }
      catch (OperationCanceledException) { }
    }
    static async Task Rejected(Task task) {
      try { await task; throw new Exception("expected worker rejection"); }
      catch (InvalidOperationException) { }
    }
    static async Task Eventually(Func<bool> condition, string message) {
      for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(5); }
      Assert(condition(), message);
    }
    public static async Task<int> Run() {
      int count = 0;
      Func<string, Func<Task>, Task> test = async (name, body) => {
        await Eventually(() => UiaThread.LiveWorkers == 0, "prior worker did not exit");
        await body(); await Eventually(() => UiaThread.LiveWorkers == 0, "worker leaked after test");
        Assert(UiaThread.ActiveMutations == 0, "mutation lease leaked");
        count++; Console.WriteLine("PASS " + name);
      };
      await test("worker success cleanup precedes an inline next-observation continuation", async () => {
        using (var worker = new UiaThread()) {
          var first = worker.Run(() => 1, CancellationToken.None);
          var next = first.ContinueWith(done => worker.Run(() => done.Result + 1, CancellationToken.None),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
          Assert(await next == 2, "a completed task must free the worker before notifying its caller");
        }
      });
      await test("worker failure cleanup also permits immediate recovery observation", async () => {
        using (var worker = new UiaThread()) {
          var first = worker.Run<int>(() => { throw new InvalidOperationException("synthetic provider fault"); }, CancellationToken.None);
          var next = first.ContinueWith(done => { var observed = done.Exception; return worker.Run(() => 7, CancellationToken.None); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
          Assert(await next == 7, "exception cleanup must release occupancy");
        }
      });
      await test("a pre-canceled queued mutation executes no function", async () => {
        using (var worker = new UiaThread()) using (var stop = new CancellationTokenSource()) {
          int calls = 0; stop.Cancel(); var task = worker.Run(() => { calls++; return 1; }, stop.Token, true);
          await Canceled(task); Assert(calls == 0 && UiaThread.ActiveMutations == 0, "no mutation lease or work for canceled queue");
        }
      });
      await test("busy workers reject extra jobs rather than accumulating a queue", async () => {
        using (var worker = new UiaThread()) using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
          int extraCalls = 0;
          var first = worker.Run(() => { entered.Set(); release.Wait(); return 3; }, CancellationToken.None);
          try {
            Assert(entered.Wait(1000), "provider did not enter");
            for (int i = 0; i < 20; i++) await Rejected(worker.Run(() => { extraCalls++; return 0; }, CancellationToken.None));
            Assert(extraCalls == 0, "extra work was queued or executed");
          } finally { release.Set(); }
          Assert(await first == 3, "first work remains intact");
        }
      });
      await test("canceling active observation quarantines it without claiming provider completion", async () => {
        using (var worker = new UiaThread()) using (var stop = new CancellationTokenSource())
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
          var first = worker.Run(() => { entered.Set(); release.Wait(); return 3; }, stop.Token);
          try {
            Assert(entered.Wait(1000), "provider did not enter"); stop.Cancel();
            Assert(!first.IsCompleted && worker.Quarantined, "cancellation is not provider completion");
            await Rejected(worker.Run(() => 1, CancellationToken.None));
          } finally { release.Set(); }
          await Canceled(first); Assert(worker.Quarantined, "a poisoned instance is not silently reused");
        }
      });
      await test("pending mutation fences other workers until the function actually returns", async () => {
        using (var firstWorker = new UiaThread()) using (var secondWorker = new UiaThread()) using (var stop = new CancellationTokenSource())
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
          var first = firstWorker.Run(() => { entered.Set(); release.Wait(); return 3; }, stop.Token, true);
          try {
            Assert(entered.Wait(1000), "mutation did not enter"); stop.Cancel(); firstWorker.Dispose();
            Assert(!first.IsCompleted && UiaThread.ActiveMutations == 1, "unknown mutation remains in flight after dispose");
            await Rejected(secondWorker.Run(() => 1, CancellationToken.None));
            try { await secondWorker.Run(() => 1, CancellationToken.None, true); throw new Exception("overlapping mutation accepted"); }
            catch (ActionNotExecutedException) { }
          } finally { release.Set(); }
          await Canceled(first);
          Assert(await secondWorker.Run(() => 8, CancellationToken.None) == 8 && UiaThread.ActiveMutations == 0, "release lease only after provider returns");
        }
      });
      await test("disposal cancels active work but keeps its task pending until exit", async () => {
        var worker = new UiaThread();
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
          var work = worker.Run(() => { entered.Set(); release.Wait(); return 1; }, CancellationToken.None);
          try {
            Assert(entered.Wait(1000), "work did not enter"); worker.Dispose();
            Assert(worker.Quarantined && !work.IsCompleted, "lifetime cancellation quarantines active work");
            await Rejected(worker.Run(() => 2, CancellationToken.None));
          } finally { release.Set(); worker.Dispose(); }
          await Canceled(work);
        }
      });
      await test("hung observation workers have a fixed process-wide resource ceiling", async () => {
        var workers = new List<UiaThread>(); var tasks = new List<Task>(); var signals = new List<ManualResetEventSlim>();
        using (var release = new ManualResetEventSlim()) {
          try {
            for (int i = 0; i < 4; i++) {
              var worker = new UiaThread(); var entered = new ManualResetEventSlim(); workers.Add(worker); signals.Add(entered);
              tasks.Add(worker.Run(() => { entered.Set(); release.Wait(); return 1; }, CancellationToken.None));
              Assert(entered.Wait(1000), "synthetic hung worker did not enter");
            }
            Assert(UiaThread.LiveWorkers == 4, "expected four provider workers");
            try { using (var extra = new UiaThread()) { } throw new Exception("unbounded replacement worker created"); }
            catch (InvalidOperationException) { }
            foreach (var worker in workers) worker.Dispose();
            Assert(UiaThread.LiveWorkers == 4, "disposed hung threads cannot be counted as stopped");
            try { using (var extra = new UiaThread()) { } throw new Exception("abandoned-worker limit bypassed"); }
            catch (InvalidOperationException) { }
          } finally { release.Set(); foreach (var worker in workers) worker.Dispose(); }
          foreach (var task in tasks) await Canceled(task);
          foreach (var entered in signals) entered.Dispose();
        }
        await Eventually(() => UiaThread.LiveWorkers == 0, "released workers did not exit");
        using (var recovered = new UiaThread()) Assert(await recovered.Run(() => 9, CancellationToken.None) == 9, "resource slot returns after actual exit");
      });
      Console.WriteLine("PASS: " + count + " pure UIA-worker scheduling tests. No UIA providers or desktop input."); return count;
    }
  }
}
