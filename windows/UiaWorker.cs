using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace JevTeachWindows {
  // One bounded job at a time on a background MTA thread. We never abort a COM
  // provider or claim it stopped merely because its caller stopped waiting.
  sealed class UiaThread : IDisposable {
    const int MaximumWorkers = 4;
    static readonly object global = new object();
    static int liveWorkers, activeMutations;
    readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>(1);
    readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    readonly Thread thread;
    int occupied, disposed, poisoned;
    internal static int LiveWorkers { get { lock (global) return liveWorkers; } }
    internal static int ActiveMutations { get { lock (global) return activeMutations; } }
    internal bool Quarantined { get { return Volatile.Read(ref poisoned) != 0; } }
    public UiaThread() {
      lock (global) {
        if (liveWorkers >= MaximumWorkers) {
          jobs.Dispose(); lifetime.Dispose();
          throw new InvalidOperationException("応答しないUIA処理が残っています。新しい処理を増やさず、対象アプリとナビを確認して再起動してください。");
        }
        liveWorkers++;
      }
      try {
        thread = new Thread(() => {
          try { foreach (var job in jobs.GetConsumingEnumerable()) job(); }
          finally {
            jobs.Dispose(); lifetime.Dispose();
            lock (global) liveWorkers--;
          }
        });
        thread.IsBackground = true; thread.Name = "Jev-UIA"; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
      } catch {
        lock (global) liveWorkers--;
        jobs.Dispose(); lifetime.Dispose(); throw;
      }
    }
    static Exception Unavailable(bool mutation, string message) {
      return mutation ? (Exception)new ActionNotExecutedException(message) : new InvalidOperationException(message);
    }
    public Task<T> Run<T>(Func<T> work, CancellationToken cancel, bool mutation = false) {
      if (work == null) throw new ArgumentNullException("work");
      var result = new TaskCompletionSource<T>();
      lock (global) {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref poisoned) != 0 || activeMutations != 0) {
          result.SetException(Unavailable(mutation, "UIAの処理が停止・未完了です。対象アプリとナビの状態を確認してください。")); return result.Task;
        }
      }
      if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0) {
        result.SetException(Unavailable(mutation, "UIAは前の処理を確認中です。操作を保留しました。")); return result.Task;
      }
      CancellationTokenSource operation;
      try { operation = CancellationTokenSource.CreateLinkedTokenSource(cancel, lifetime.Token); }
      catch (ObjectDisposedException) {
        Interlocked.Exchange(ref occupied, 0); result.SetCanceled(); return result.Task;
      }
      var token = operation.Token;
      var state = new object(); bool running = false, finished = false, canceled = false;
      var registration = token.Register(() => {
        bool cancelQueued = false;
        lock (state) {
          if (finished) return;
          canceled = true;
          if (running) Interlocked.Exchange(ref poisoned, 1);
          else cancelQueued = true;
        }
        // Running work owns the task until it actually returns. Completing this
        // task early would hide an in-flight mutation from the guide runner.
        if (cancelQueued) result.TrySetCanceled();
      });
      Action job = () => {
        T value = default(T); Exception failure = null; bool started = false, mutationLease = false;
        try {
          lock (global) {
            lock (state) {
              if (canceled || token.IsCancellationRequested || Volatile.Read(ref disposed) != 0) canceled = true;
              else if (Volatile.Read(ref poisoned) != 0 || activeMutations != 0)
                failure = Unavailable(mutation, "前のUIA操作がまだ終了していません。");
              else {
                running = true; started = true;
                if (mutation) { activeMutations++; mutationLease = true; }
              }
            }
          }
          if (started) {
            token.ThrowIfCancellationRequested();
            value = work();
            token.ThrowIfCancellationRequested();
          }
        } catch (OperationCanceledException ex) { failure = ex; }
        catch (Exception ex) { failure = ex; }
        finally {
          lock (global) {
            lock (state) {
              running = false; finished = true;
              canceled = canceled || token.IsCancellationRequested;
              if (mutationLease) activeMutations--;
            }
          }
          // Dispose registrations without holding state: an executing cancellation
          // callback may be waiting for that lock.
          registration.Dispose(); operation.Dispose(); Interlocked.Exchange(ref occupied, 0);
        }
        // All ordinary completion happens AFTER cleanup. An inline continuation is
        // allowed to immediately submit the next result observation on this worker.
        if (canceled || failure is OperationCanceledException) result.TrySetCanceled();
        else if (failure != null) result.TrySetException(failure);
        else result.TrySetResult(value);
      };
      try {
        if (!jobs.TryAdd(job)) throw new InvalidOperationException("UIA処理は保留中です。");
      } catch (ObjectDisposedException) {
        lock (state) finished = true;
        registration.Dispose(); operation.Dispose(); Interlocked.Exchange(ref occupied, 0); result.TrySetCanceled();
      } catch (InvalidOperationException ex) {
        lock (state) finished = true;
        registration.Dispose(); operation.Dispose(); Interlocked.Exchange(ref occupied, 0);
        result.TrySetException(Unavailable(mutation, ex.Message));
      }
      return result.Task;
    }
    public void Dispose() {
      if (Interlocked.Exchange(ref disposed, 1) == 0) {
        // Lifetime disposal cancels queued work; already running provider work is
        // quarantined until it returns. No Thread.Abort and no replacement loop.
        try { lifetime.Cancel(); jobs.CompleteAdding(); }
        catch (ObjectDisposedException) { }
      }
    }
  }
}
