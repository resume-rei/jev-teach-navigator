using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JevTeachWindows {
  static class DesktopEngineTests {
    sealed class FakeDesktop : IDesktopAdapter {
      public string Foreground = "navigator", Identity = "pid:start:window", Runtime = "instance-1";
      public int Left = 350, Calls, ShelfStage, RequestStage, ResultDelay;
      public bool Duplicate, Missing, Fail, Closed, DuplicateResult;
      public TaskCompletionSource<bool> ObservationGate, ExecutionGate;
      public async Task<DesktopView> ObserveAsync(string app, CancellationToken cancel) {
        if (ObservationGate != null) await ObservationGate.Task;
        cancel.ThrowIfCancellationRequested(); return View(app);
      }
      public DesktopView View(string app) {
        var v = new DesktopView { App = app, Title = app, Identity = Identity + app, Foreground = Foreground, Exists = !Closed, Rect = new Rectangle(30, 30, 650, 500) };
        int stage = app == "shelf" ? ShelfStage : RequestStage;
        string[] targets = app == "shelf" ? new[] { "申請の資料を用意", "作業を完了" } : new[] { "新しい申請", "内容を確認", "練習として登録" };
        string[] results = app == "shelf" ? new[] { "資料の準備ができました", "今回の作業は完了です" } : new[] { "申請内容を入力しました", "確認画面です", "練習の登録が完了しました" };
        if (!Missing && stage < targets.Length) {
          v.Nodes.Add(new DesktopNode { Name = targets[stage], Role = "Button", Context = "body", Runtime = Runtime + stage, Rect = new Rectangle(Left, 220, 180, 45), Enabled = true, Visible = true, Invokable = true });
          if (Duplicate) v.Nodes.Add(new DesktopNode { Name = targets[stage], Role = "Button", Context = "body", Runtime = "duplicate", Rect = new Rectangle(Left, 320, 180, 45), Enabled = true, Visible = true, Invokable = true });
        }
        if (stage > 0) {
          v.Nodes.Add(new DesktopNode { Name = results[stage - 1], Role = "Text", Runtime = "result", Visible = true, Enabled = true });
          if (DuplicateResult) v.Nodes.Add(new DesktopNode { Name = results[stage - 1], Role = "Text", Runtime = "second-result", Visible = true });
        }
        return v;
      }
      public async Task ExecuteAsync(PreparedAction p, CancellationToken cancel) {
        if (ExecutionGate != null) await ExecutionGate.Task;
        cancel.ThrowIfCancellationRequested();
        if (View(p.App).Stamp != p.ViewStamp) throw new ScreenChangedException("changed");
        Calls++;
        if (p.Kind == "activate") { Foreground = p.App; return; }
        if (Fail) return;
        if (ResultDelay > 0) await Task.Delay(ResultDelay, cancel);
        if (p.App == "shelf") ShelfStage++; else RequestStage++;
      }
    }
    static void Assert(bool condition, string text) { if (!condition) throw new Exception(text); }
    static async Task Approve(DesktopRunner r) { var p = r.TakeApproval(); Assert(p != null, "proposal required"); await r.ExecuteApprovedAsync(p); }
    static DesktopRunner Runner(FakeDesktop d, Func<long> clock = null) { return new DesktopRunner(d, clock) { ResultTimeoutMs = 35, ResultPollMs = 1 }; }
    public static async Task<int> Run() {
      int count = 0;
      Func<string, Func<Task>, Task> test = async (name, body) => { await body(); count++; Console.WriteLine("PASS " + name); };
      await test("cross-process task automatically proposes every next action", async () => {
        var d = new FakeDesktop(); var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        for (int i = 0; i < 8; i++) await Approve(r);
        Assert(r.State == "done" && r.StepIndex == 5 && d.Calls == 8, "five steps plus three switches");
        Assert(r.TakeApproval() == null && r.Timings.Count == 8 && r.Timings.All(t => t.Outcome == "verified"), "no extra execution");
        Assert(!r.ActionInFlight && r.Timings.All(t => t.ApprovalToReadyMs >= t.NextReadyMs), "settled complete timings");
      });
      await test("moved target is re-proposed before any invocation", async () => {
        var d = new FakeDesktop { Foreground = "shelf" }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        d.Left = -120; await Approve(r); Assert(d.Calls == 0 && r.Pending.Rect.X == -120, "must use fresh position"); await Approve(r); Assert(d.Calls == 1, "new Enter required");
      });
      await test("duplicate semantic target holds and automatically recovers", async () => {
        var d = new FakeDesktop { Foreground = "shelf", Duplicate = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        Assert(r.State == "waiting" && r.Pending == null, "ambiguous"); d.Duplicate = false; await r.RefreshAsync(); Assert(r.State == "ready" && d.Calls == 0, "fresh arrow without execution");
      });
      await test("absent target holds and reappearing target is found", async () => {
        var d = new FakeDesktop { Foreground = "shelf", Missing = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        Assert(r.Pending == null, "missing"); d.Missing = false; await r.RefreshAsync(); Assert(r.Pending != null && d.Calls == 0, "reappeared");
      });
      await test("unchanged result stops and never automatically retries", async () => {
        var d = new FakeDesktop { Foreground = "shelf", Fail = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); await Approve(r);
        Assert(r.State == "unknown" && r.StepIndex == 0 && d.Calls == 1, "outcome unknown"); await r.RefreshAsync(); await r.ResumeAsync(); Assert(r.TakeApproval() == null && d.Calls == 1, "no replay");
      });
      await test("delayed operation waits for its result", async () => {
        var d = new FakeDesktop { Foreground = "shelf", ResultDelay = 20 }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); await Approve(r);
        Assert(r.StepIndex == 1 && r.State == "ready", "next only after result");
      });
      await test("failure metrics are saved only after the failed action settles", async () => {
        var d = new FakeDesktop { Foreground = "shelf", Fail = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        bool sawPendingFailure = false;
        r.Changed += () => { if (r.State == "unknown" && r.ActionInFlight) sawPendingFailure = true; };
        await Approve(r);
        Assert(sawPendingFailure && !r.ActionInFlight && r.Timings.Count == 1 && r.Timings[0].Outcome == "unknown", "include final failed action");
      });
      await test("unrelated foreground suspends and reentry automatically re-observes", async () => {
        var d = new FakeDesktop(); var r = Runner(d); await r.StartAsync(PracticeTask.Create()); d.Foreground = "outside"; await r.RefreshAsync();
        Assert(r.State == "suspended" && r.Pending == null && d.Calls == 0, "outside");
        d.Foreground = "navigator"; await r.RefreshAsync(); Assert(r.State == "ready" && d.Calls == 0, "resume without automatic execution");
      });
      await test("reading the navigator does not add a redundant window switch", async () => {
        var d = new FakeDesktop(); var r = Runner(d); await r.StartAsync(PracticeTask.Create()); await Approve(r);
        d.Foreground = "navigator"; await r.RefreshAsync(); Assert(r.Pending.Kind == "invoke", "keep visible target"); await Approve(r);
        Assert(d.Calls == 2 && r.StepIndex == 1, "one activation and one invocation");
      });
      await test("expired approval re-observes and requires another Enter", async () => {
        long now = 0; var d = new FakeDesktop { Foreground = "shelf" }; var r = Runner(d, () => now); await r.StartAsync(PracticeTask.Create());
        now = 16000; await Approve(r); Assert(d.Calls == 0 && r.Pending.Created == now, "expiry");
      });
      await test("the same approved object cannot dispatch twice", async () => {
        var d = new FakeDesktop { Foreground = "shelf", ExecutionGate = new TaskCompletionSource<bool>() }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        var p = r.TakeApproval(); var first = r.ExecuteApprovedAsync(p); var second = r.ExecuteApprovedAsync(p); d.ExecutionGate.SetResult(true); await Task.WhenAll(first, second);
        Assert(d.Calls == 1, "duplicate dispatch");
      });
      await test("stopping during observation prevents late proposal", async () => {
        var d = new FakeDesktop { ObservationGate = new TaskCompletionSource<bool>() }; var r = Runner(d); var start = r.StartAsync(PracticeTask.Create());
        r.Stop(); d.ObservationGate.SetResult(true); await start; Assert(r.State == "stopped" && r.Pending == null && d.Calls == 0, "late observation");
      });
      await test("stopping a queued operation cancels before dispatch", async () => {
        var d = new FakeDesktop { Foreground = "shelf", ExecutionGate = new TaskCompletionSource<bool>() }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        var run = r.ExecuteApprovedAsync(r.TakeApproval()); r.Stop(); d.ExecutionGate.SetResult(true); await run; Assert(d.Calls == 0 && r.State == "stopped", "queued dispatch cancelled");
      });
      await test("pausing an in-flight action does not offer replay", async () => {
        var d = new FakeDesktop { Foreground = "shelf", ExecutionGate = new TaskCompletionSource<bool>() }; var r = Runner(d); await r.StartAsync(PracticeTask.Create());
        var run = r.ExecuteApprovedAsync(r.TakeApproval()); r.Pause("pause"); d.ExecutionGate.SetResult(true); await run; await r.ResumeAsync();
        Assert(d.Calls == 0 && r.Pending == null && r.State == "unknown", "uncertain operation cannot resume automatically");
      });
      await test("Enter during recognition is discarded, not banked", () => {
        var key = new EnterLatch(); bool approve; Assert(key.Down(true, false, out approve) && !approve, "busy");
        Assert(key.Down(true, true, out approve) && !approve, "repeat cannot approve fresh proposal"); key.Up(); key.Down(true, true, out approve); Assert(approve, "new press"); return Task.FromResult(0);
      });
      await test("holding Enter across proposals authorizes only one action", () => {
        var key = new EnterLatch(); bool approve; key.Down(true, true, out approve); Assert(approve, "first");
        for (int i = 0; i < 10; i++) { key.Down(true, true, out approve); Assert(!approve, "repeat"); }
        Assert(key.Up(), "suppress paired release"); return Task.FromResult(0);
      });
      await test("unscoped editing Enter is passed through", () => {
        var key = new EnterLatch(); bool approve; Assert(!key.Down(false, true, out approve) && !approve && !key.Up(), "normal input"); return Task.FromResult(0);
      });
      await test("element replacement invalidates an earlier proposal", async () => {
        var d = new FakeDesktop { Foreground = "shelf" }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); d.Runtime = "replacement"; await Approve(r);
        Assert(d.Calls == 0 && r.Pending.Target.Runtime.StartsWith("replacement"), "runtime changed");
      });
      await test("process identity changes cannot reuse approval", async () => {
        var d = new FakeDesktop { Foreground = "shelf" }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); d.Identity = "other-process"; await Approve(r); Assert(d.Calls == 0, "process identity");
      });
      await test("a closed app is held without any action", async () => {
        var d = new FakeDesktop { Foreground = "shelf", Closed = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); Assert(r.State == "waiting" && r.Pending == null, "closed app");
      });
      await test("duplicate result text is insufficient completion evidence", async () => {
        var d = new FakeDesktop { Foreground = "shelf", DuplicateResult = true }; var r = Runner(d); await r.StartAsync(PracticeTask.Create()); await Approve(r); Assert(r.State == "unknown" && r.StepIndex == 0, "ambiguous completion");
      });
      await test("a semantic step requires a distinct observed result", () => {
        var t = PracticeTask.Create(); t.Steps[0].Result = t.Steps[0].Target;
        try { t.Validate(); throw new Exception("accepted invalid task"); } catch (ArgumentException) { } return Task.FromResult(0);
      });
      Console.WriteLine("PASS: " + count + " desktop-runner tests. No desktop input, microphone, screenshots or API calls."); return count;
    }
  }
}
