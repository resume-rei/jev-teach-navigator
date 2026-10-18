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
        if (View(p.App).Stamp != p.ViewStamp) throw new ActionNotExecutedException("changed before dispatch");
        Calls++;
        if (p.Kind == "activate") { Foreground = p.App; return; }
        if (Fail) return;
        if (ResultDelay > 0) await Task.Delay(ResultDelay, cancel);
        if (p.App == "shelf") ShelfStage++; else RequestStage++;
      }
    }
    // Synthetic adapter: no UIA, operating-system input, external data or APIs.
    sealed class SemanticDesktop : IDesktopAdapter {
      public readonly Dictionary<string, DesktopView> Views = new Dictionary<string, DesktopView>();
      public string Foreground = "editor";
      public int Calls, Observations;
      public Action<PreparedAction> OnExecute;
      public Action<string> OnObserve;
      public TaskCompletionSource<bool> ExecutionGate, ObservationGate;
      public bool IgnoreCancellation;
      public CancellationToken LastExecutionToken, LastObservationToken;
      public SemanticDesktop() { AddApp("editor"); AddApp("receipt"); }
      public DesktopView AddApp(string app) {
        var view = new DesktopView { App = app, Title = app, Identity = app + ":pid:start:hwnd", Exists = true,
          Rect = new Rectangle(10, 10, 640, 500) };
        Views[app] = view; return view;
      }
      public async Task<DesktopView> ObserveAsync(string app, CancellationToken cancel) {
        Observations++; LastObservationToken = cancel;
        if (ObservationGate != null) await ObservationGate.Task;
        cancel.ThrowIfCancellationRequested(); if (OnObserve != null) OnObserve(app);
        var view = Views[app];
        return new DesktopView { App = app, Title = view.Title, Identity = view.Identity, Foreground = Foreground,
          Exists = view.Exists, Minimized = view.Minimized, Rect = view.Rect, Nodes = view.Nodes.Select(n => n.Copy()).ToList() };
      }
      public async Task ExecuteAsync(PreparedAction action, CancellationToken cancel) {
        LastExecutionToken = cancel;
        if (ExecutionGate != null) await ExecutionGate.Task;
        if (!IgnoreCancellation) cancel.ThrowIfCancellationRequested();
        Calls++;
        if (action.Kind == "activate") Foreground = action.App;
        else if (OnExecute != null) OnExecute(action);
        else if (action.Kind == "set-value") Views[action.App].Nodes.Single(n => n.Runtime == action.Target.Runtime).Value = action.InputText;
        else Views[action.App].Nodes.Add(Node("完了", "Text", "result"));
      }
    }
    static DesktopNode Node(string name, string role = "Button", string runtime = "control", string context = "body") {
      return new DesktopNode { Name = name, Role = role, Runtime = runtime, Context = context, AutomationId = runtime,
        Rect = new Rectangle(30, 60, 150, 35), Enabled = true, Visible = true, Invokable = role == "Button",
        Settable = role == "Edit", ValueReadable = role == "Edit", Value = role == "Edit" ? "" : null };
    }
    static DesktopTask OneStep(string kind = "invoke") {
      var t = new DesktopTask { Name = "合成データの操作" };
      t.Steps.Add(new TaskStep { App = "editor", Kind = kind, Target = kind == "set-value" ? "件名" : "保存",
        Role = kind == "set-value" ? "Edit" : "Button", Context = "body", Result = kind == "set-value" ? null : "完了",
        InputText = kind == "set-value" ? "架空の入力\n全文です。" : null });
      return t;
    }
    static SemanticDesktop Semantic(string kind = "invoke") {
      var d = new SemanticDesktop(); d.Views["editor"].Nodes.Add(Node(kind == "set-value" ? "件名" : "保存", kind == "set-value" ? "Edit" : "Button")); return d;
    }
    static void Assert(bool condition, string text) { if (!condition) throw new Exception(text); }
    static async Task Approve(DesktopRunner r) { var p = r.TakeApproval(); Assert(p != null, "proposal required"); await r.ExecuteApprovedAsync(p); }
    static DesktopRunner Runner(IDesktopAdapter d, Func<long> clock = null) { return new DesktopRunner(d, clock) { ResultTimeoutMs = 35, ResultPollMs = 1 }; }
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
      await test("synthetic Enter-up cannot rearm a physically held key for the next proposal", () => {
        var key = new EnterLatch(); bool approve;
        Assert(key.Process(true, false, true, true, out approve) && approve, "first physical down");
        Assert(key.Process(false, true, true, false, out approve) && !approve && key.Held, "injected release leaves physical latch held");
        Assert(key.Process(true, false, true, true, out approve) && !approve, "physical repeat cannot approve fresh proposal");
        Assert(key.Process(false, false, true, true, out approve) && !approve && !key.Held, "physical release clears latch");
        Assert(key.Process(true, false, true, true, out approve) && approve, "new physical press can approve"); return Task.FromResult(0);
      });
      await test("injected Enter presses cannot approve or create a physical-key latch", () => {
        var key = new EnterLatch(); bool approve;
        Assert(key.Process(true, true, true, true, out approve) && !approve && !key.Held, "owned injected down is consumed");
        Assert(key.Process(false, true, true, true, out approve) && !approve && !key.Held, "owned injected release is consumed");
        Assert(!key.Process(true, true, false, true, out approve) && !approve && !key.Held, "unowned injection does not affect approval");
        return Task.FromResult(0);
      });
      await test("Alt-Tab started in the navigator interrupts a pending approval", () => {
        Assert(GuideInputPolicy.KeyboardInterrupts(true, false, 0x12, true, true), "Alt begins while navigator still owns foreground");
        Assert(GuideInputPolicy.KeyboardInterrupts(true, false, 0x09, true, true), "Tab before the switch is also an intervention");
        Assert(!GuideInputPolicy.KeyboardInterrupts(true, false, 0x09, true, false), "ordinary navigation in an idle panel is not a target operation");
        Assert(!GuideInputPolicy.KeyboardInterrupts(true, true, 0x12, true, true), "injection is not human input");
        return Task.FromResult(0);
      });
      await test("a switch shortcut before queued execution cancels the approval without dispatch", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep()); var p = r.TakeApproval();
        Assert(!r.ActionInFlight && r.State == "executing", "approved callback has not begun");
        if (GuideInputPolicy.KeyboardInterrupts(true, false, 0x12, true, r.ActionInFlight || r.State == "executing")) r.Pause("physical Alt");
        await r.ExecuteApprovedAsync(p); Assert(r.State == "paused" && d.Calls == 0 && r.Pending == null, "old queued callback cannot take focus or execute");
      });
      await test("outside Esc during result verification yields unknown and never resumes the next arrow", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep());
        r.Changed += () => {
          if (r.State == "verifying" && GuideInputPolicy.KeyboardInterrupts(true, false, 27, false, r.ActionInFlight)) r.Pause("target app Esc");
        };
        await Approve(r); await r.ResumeAsync(); await r.RefreshAsync();
        Assert(r.State == "unknown" && r.StepIndex == 0 && r.Pending == null && d.Calls == 1, "target Esc passes to the app but cancels guidance");
      });
      await test("own-foreground shortcut after dispatch yields unknown instead of auto-focus recovery", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep());
        r.Changed += () => {
          if (r.State == "verifying" && GuideInputPolicy.KeyboardInterrupts(true, false, 0x12, true, r.ActionInFlight)) r.Pause("Alt-Tab during execution");
        };
        await Approve(r); Assert(r.State == "unknown" && r.Pending == null && r.StepIndex == 0 && d.Calls == 1, "no next arrow after physical switch intent");
      });
      await test("extra mouse buttons and horizontal wheels interrupt general-app guidance", () => {
        Assert(GuideInputPolicy.PointerInterrupts(0x20b, false, false), "XBUTTONDOWN can navigate the app");
        Assert(GuideInputPolicy.PointerInterrupts(0x20e, false, false), "horizontal wheel changes the working view");
        Assert(!GuideInputPolicy.PointerInterrupts(0x20b, true, false), "synthetic pointer event is not human evidence");
        Assert(!GuideInputPolicy.PointerInterrupts(0x200, false, false), "moving the pointer alone does not change the task");
        Assert(!GuideInputPolicy.PointerInterrupts(0x201, false, true), "own panel clicks remain usable");
        return Task.FromResult(0);
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
        var t = PracticeTask.Create(); t.Steps[0].Result = t.Steps[0].Target; t.Steps[0].ResultRole = t.Steps[0].Role;
        try { t.Validate(); throw new Exception("accepted invalid task"); } catch (ArgumentException) { } return Task.FromResult(0);
      });
      await test("cancel from preparing notification prevents even the first observation", async () => {
        var d = Semantic(); var r = Runner(d);
        r.Changed += () => { if (r.State == "preparing") r.Stop(); };
        await r.StartAsync(OneStep()); Assert(r.State == "stopped" && d.Observations == 0 && d.Calls == 0, "reentrant start cancellation");
      });
      await test("cancel from checking notification never dereferences a disposed session", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep());
        r.Changed += () => { if (r.State == "checking") r.Pause("cancel during check"); };
        await Approve(r); Assert(r.State == "paused" && d.Calls == 0 && !r.ActionInFlight, "cancelled before dispatch");
      });
      await test("cancel between final check and dispatch sends no operation", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep());
        r.Changed += () => { if (r.State == "executing" && r.ActionInFlight) r.Pause("cancel at dispatch boundary"); };
        await Approve(r); Assert(r.State == "paused" && d.Calls == 0, "notification cannot authorize a late dispatch");
      });
      await test("ordinary screen-changed after mutation is unknown and cannot be replayed", async () => {
        var d = Semantic(); d.OnExecute = p => { d.Views["editor"].Nodes.Add(Node("完了", "Text", "result")); throw new ScreenChangedException("changed after effect"); };
        var r = Runner(d); await r.StartAsync(OneStep()); await Approve(r);
        Assert(r.State == "unknown" && r.StepIndex == 0 && d.Calls == 1, "exception cannot attest non-execution");
        r.Pause("pause again"); await r.ResumeAsync(); await r.RefreshAsync();
        Assert(r.State == "unknown" && r.TakeApproval() == null && d.Calls == 1, "unknown cannot become resumable paused");
      });
      await test("verification screen-changed cannot be treated as an unexecuted action", async () => {
        var d = Semantic(); d.OnObserve = app => { if (d.Calls > 0) throw new ScreenChangedException("provider lost result"); };
        var r = Runner(d); await r.StartAsync(OneStep()); await Approve(r);
        Assert(r.State == "unknown" && r.Pending == null && d.Calls == 1, "no reprepare after verification fault");
      });
      await test("adapter proof of no side effect requires a fresh approval", async () => {
        var d = Semantic(); d.OnExecute = p => { d.Calls--; throw new ActionNotExecutedException("pre-dispatch target disappeared"); };
        var r = Runner(d); await r.StartAsync(OneStep()); var first = r.Pending.Id; await Approve(r);
        Assert(r.State == "ready" && r.Pending.Id != first && d.Calls == 0 && r.Timings[0].Outcome == "not-executed", "safe new proposal only");
      });
      await test("a result present before the operation never proves a new success", async () => {
        var d = Semantic(); d.Views["editor"].Nodes.Add(Node("完了", "Text", "old-result")); var r = Runner(d);
        await r.StartAsync(OneStep()); Assert(r.State == "waiting" && r.Pending == null && d.Calls == 0, "preexisting success held");
      });
      await test("explicit preconditions require unique role and context evidence", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].Precondition = "編集画面"; t.Steps[0].PreconditionContext = "header";
        d.Views["editor"].Nodes.Add(Node("編集画面", "Text", "wrong-context", "footer"));
        var r = Runner(d); await r.StartAsync(t); Assert(r.State == "waiting", "wrong context");
        d.Views["editor"].Nodes.Add(Node("編集画面", "Text", "first-cue", "header"));
        d.Views["editor"].Nodes.Add(Node("編集画面", "Text", "second-cue", "header"));
        await r.RefreshAsync(); Assert(r.State == "waiting", "duplicate cue");
        d.Views["editor"].Nodes.RemoveAt(3); await r.RefreshAsync(); Assert(r.State == "ready" && d.Calls == 0, "one explicit cue");
      });
      await test("completion role and context must match the taught result", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].ResultRole = "Pane"; t.Steps[0].ResultContext = "status";
        d.OnExecute = p => d.Views["editor"].Nodes.Add(Node("完了", "Text", "wrong-role", "status"));
        var r = Runner(d); await r.StartAsync(t); await Approve(r); Assert(r.State == "unknown" && d.Calls == 1, "same text wrong role");
        var valid = Semantic(); valid.OnExecute = p => valid.Views["editor"].Nodes.Add(Node("完了", "Pane", "correct-role", "status"));
        var good = Runner(valid); await good.StartAsync(t); await Approve(good); Assert(good.State == "done", "typed semantic result");
      });
      await test("set-value shows full text and input never approves the following submit", async () => {
        var d = Semantic("set-value"); d.Views["editor"].Nodes.Add(Node("保存", "Button", "submit"));
        var t = OneStep("set-value"); t.Steps.Add(OneStep().Steps[0]); var r = Runner(d); await r.StartAsync(t);
        Assert(r.Pending.Kind == "set-value" && r.Pending.InputText == t.Steps[0].InputText, "full multiline proposal");
        await Approve(r); Assert(r.StepIndex == 1 && r.Pending.Kind == "invoke" && d.Calls == 1, "one input only");
        Assert(d.Views["editor"].Nodes[0].Value == t.Steps[0].InputText, "exact readback");
        d.OnExecute = p => d.Views["editor"].Nodes.Add(Node("完了", "Text", "result"));
        await Approve(r); Assert(r.State == "done" && d.Calls == 2, "submit is a new Enter");
      });
      await test("input normalization mismatch stops without retyping", async () => {
        var d = Semantic("set-value"); d.OnExecute = p => d.Views["editor"].Nodes[0].Value = p.InputText.TrimEnd('。');
        var r = Runner(d); await r.StartAsync(OneStep("set-value")); await Approve(r); await r.RefreshAsync(); await r.ResumeAsync();
        Assert(r.State == "unknown" && r.StepIndex == 0 && d.Calls == 1, "readback must equal the whole approved string");
      });
      await test("a readonly or unreadable input never receives a proposal", async () => {
        var d = Semantic("set-value"); d.Views["editor"].Nodes[0].ValueReadable = false;
        var r = Runner(d); await r.StartAsync(OneStep("set-value")); Assert(r.State == "waiting" && r.Pending == null, "no reliable result");
      });
      await test("empty text is an explicitly approved clear with exact readback", async () => {
        var d = Semantic("set-value"); d.Views["editor"].Nodes[0].Value = "架空の以前の値";
        var t = OneStep("set-value"); t.Steps[0].InputText = "";
        var r = Runner(d); await r.StartAsync(t); Assert(r.Pending.InputText == "", "empty is different from missing parameter");
        await Approve(r); Assert(r.State == "done" && d.Calls == 1, "clear verified");
      });
      await test("mutable caller task cannot replace the text after it is displayed", async () => {
        var d = Semantic("set-value"); var t = OneStep("set-value"); string shown = t.Steps[0].InputText;
        var r = Runner(d); await r.StartAsync(t); t.Steps[0].InputText = "変更された実行案"; r.Task.Steps[0].InputText = "別の変更";
        await Approve(r); Assert(r.State == "done" && d.Views["editor"].Nodes[0].Value == shown, "run owns immutable task snapshot");
      });
      await test("mutating a displayed proposal invalidates it before approval", async () => {
        var d = Semantic("set-value"); var r = Runner(d); await r.StartAsync(OneStep("set-value")); r.Pending.InputText = "別の全文";
        Assert(r.TakeApproval() == null && r.State == "paused" && d.Calls == 0, "unshown changed operation rejected");
      });
      await test("mutating an approved proposal cannot change the executed text", async () => {
        var d = Semantic("set-value"); var r = Runner(d); await r.StartAsync(OneStep("set-value")); var p = r.TakeApproval(); p.InputText = "承認後の別の全文";
        await r.ExecuteApprovedAsync(p); Assert(r.State == "paused" && d.Calls == 0 && !r.ActionInFlight, "tampered approval rejected");
      });
      await test("tampering an approved step invalidates it without leaving a stuck executing state", async () => {
        var d = Semantic(); var r = Runner(d); await r.StartAsync(OneStep()); var p = r.TakeApproval(); p.Step = 42;
        await r.ExecuteApprovedAsync(p); Assert(r.State == "paused" && d.Calls == 0 && !r.ActionInFlight, "corrupted step released safely");
      });
      await test("proposal mutation while checking cannot alter the private execution snapshot", async () => {
        var d = Semantic("set-value"); var t = OneStep("set-value"); var r = Runner(d); await r.StartAsync(t);
        d.ObservationGate = new TaskCompletionSource<bool>(); var p = r.TakeApproval(); var run = r.ExecuteApprovedAsync(p);
        p.InputText = "await中の別の全文"; d.ObservationGate.SetResult(true); await run;
        Assert(r.State == "done" && d.Views["editor"].Nodes[0].Value == t.Steps[0].InputText, "only originally approved copy dispatched");
      });
      await test("manual input edits invalidate a proposal even at unchanged coordinates", async () => {
        var d = Semantic("set-value"); var r = Runner(d); await r.StartAsync(OneStep("set-value")); string id = r.Pending.Id;
        d.Views["editor"].Nodes[0].Value = "人が入力した別の架空値"; await Approve(r);
        Assert(d.Calls == 0 && r.State == "ready" && r.Pending.Id != id, "readback digest binds current edit value");
      });
      await test("a uniquely observed explicit checkpoint resumes at a later step", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].AllowResume = true;
        t.Steps.Add(new TaskStep { App = "editor", Target = "次へ", Result = "全体完了", AllowResume = true });
        d.Views["editor"].Nodes.Clear(); d.Views["editor"].Nodes.Add(Node("完了", "Text", "checkpoint"));
        d.Views["editor"].Nodes.Add(Node("次へ", "Button", "next")); var r = Runner(d); await r.StartAsync(t);
        Assert(r.StepIndex == 1 && r.State == "ready" && r.Pending.Target.Name == "次へ" && d.Calls == 0, "no replay of missing old target");
      });
      await test("manual completion invalidates pending action and never consumes its approval for the next", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].AllowResume = true;
        t.Steps.Add(new TaskStep { App = "editor", Target = "次へ", Result = "全体完了" });
        d.Views["editor"].Nodes.Add(Node("次へ", "Button", "next")); var r = Runner(d); await r.StartAsync(t);
        d.Views["editor"].Nodes.Add(Node("完了", "Text", "checkpoint")); await Approve(r);
        Assert(r.StepIndex == 1 && r.State == "ready" && d.Calls == 0, "new step needs a new Enter");
      });
      await test("conflicting resume checkpoints are held instead of guessed", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].AllowResume = true;
        t.Steps.Add(new TaskStep { App = "editor", Target = "次へ", Result = "全体完了", AllowResume = true });
        d.Views["editor"].Nodes.Add(Node("完了", "Text", "checkpoint1")); d.Views["editor"].Nodes.Add(Node("全体完了", "Text", "checkpoint2"));
        var r = Runner(d); await r.StartAsync(t); Assert(r.State == "waiting" && r.StepIndex == 0 && r.Pending == null && d.Calls == 0, "ambiguous start");
      });
      await test("duplicate checkpoint definitions cannot identify progress", () => {
        var t = OneStep(); t.Steps[0].AllowResume = true; var second = OneStep().Steps[0]; second.AllowResume = true; second.Target = "次へ"; t.Steps.Add(second);
        try { t.Validate(); throw new Exception("duplicate resume evidence accepted"); } catch (ArgumentException) { } return Task.FromResult(0);
      });
      await test("app aliases cannot impersonate the navigator or outside scope sentinels", () => {
        var target = OneStep(); target.Steps[0].App = "navigator";
        try { target.Validate(); throw new Exception("reserved target alias accepted"); } catch (ArgumentException) { }
        var result = OneStep(); result.Steps[0].ResultApp = "OUTSIDE";
        try { result.Validate(); throw new Exception("reserved result alias accepted"); } catch (ArgumentException) { }
        return Task.FromResult(0);
      });
      await test("exact current input can be an explicitly declared resume checkpoint", async () => {
        var d = Semantic("set-value"); var t = OneStep("set-value"); t.Steps[0].AllowResume = true; d.Views["editor"].Nodes[0].Value = t.Steps[0].InputText;
        var r = Runner(d); await r.StartAsync(t); Assert(r.State == "done" && r.StepIndex == 1 && d.Calls == 0, "observed exact readback, no retype");
      });
      await test("a result in another registered app verifies that same live identity", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].ResultApp = "receipt"; t.Steps[0].ResultContext = "receipt-panel";
        d.OnExecute = p => d.Views["receipt"].Nodes.Add(Node("完了", "Text", "receipt-result", "receipt-panel"));
        var r = Runner(d); await r.StartAsync(t); await Approve(r); Assert(r.State == "done" && d.Calls == 1, "external typed result");
      });
      await test("approval expiry is checked again after result-app observation", async () => {
        long now = 0; var d = Semantic(); var t = OneStep(); t.Steps[0].ResultApp = "receipt";
        var r = Runner(d, () => now); await r.StartAsync(t); bool advanced = false;
        d.OnObserve = app => { if (app == "receipt" && !advanced) { now = 16000; advanced = true; } };
        await Approve(r); Assert(d.Calls == 0 && r.State == "ready" && r.Pending.Created == now, "late expiry requires fresh Enter");
      });
      await test("same label with distinct target and result roles can describe a real transition", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].Result = "保存";
        d.OnExecute = p => d.Views["editor"].Nodes.Add(Node("保存", "Text", "result"));
        var r = Runner(d); await r.StartAsync(t); await Approve(r); Assert(r.State == "done" && d.Calls == 1, "role distinguishes button from result text");
      });
      await test("replacement result app cannot attest a previously approved operation", async () => {
        var d = Semantic(); var t = OneStep(); t.Steps[0].ResultApp = "receipt";
        d.OnExecute = p => { d.Views["receipt"].Identity = "replacement-process"; d.Views["receipt"].Nodes.Add(Node("完了", "Text", "receipt-result")); };
        var r = Runner(d); await r.StartAsync(t); await Approve(r); Assert(r.State == "unknown" && r.StepIndex == 0 && d.Calls == 1, "result process replacement");
      });
      await test("unregistered foreground during verification prevents a success claim", async () => {
        var d = Semantic(); d.OnExecute = p => { d.Views["editor"].Nodes.Add(Node("完了", "Text", "result")); d.Foreground = "outside"; };
        var r = Runner(d); await r.StartAsync(OneStep()); await Approve(r);
        Assert(r.State == "unknown" && r.StepIndex == 0 && d.Calls == 1, "scope changed after dispatch");
      });
      await test("execution timeout cancels queued work and blocks overlapping starts until it settles", async () => {
        var d = Semantic(); d.ExecutionGate = new TaskCompletionSource<bool>(); var r = Runner(d); r.ExecutionTimeoutMs = 15;
        await r.StartAsync(OneStep()); await Approve(r);
        Assert(r.State == "unknown" && r.ActionInFlight && d.LastExecutionToken.IsCancellationRequested && d.Calls == 0, "cancel timeout token");
        try { await r.StartAsync(OneStep()); throw new Exception("overlapping start accepted"); } catch (InvalidOperationException) { }
        d.ExecutionGate.SetResult(true); await Task.Delay(20);
        Assert(!r.ActionInFlight && r.State == "unknown" && d.Calls == 0 && r.Pending == null, "late canceled queued action never executes");
      });
      await test("late effects from a cancellation-ignoring adapter never resume guidance", async () => {
        var d = Semantic(); d.ExecutionGate = new TaskCompletionSource<bool>(); d.IgnoreCancellation = true;
        var r = Runner(d); r.ExecutionTimeoutMs = 15; await r.StartAsync(OneStep()); await Approve(r);
        Assert(r.State == "unknown" && r.ActionInFlight, "timeout is not completion");
        d.ExecutionGate.SetResult(true); await Task.Delay(20); await r.RefreshAsync(); await r.ResumeAsync();
        Assert(r.State == "unknown" && r.StepIndex == 0 && r.Pending == null && d.Calls == 1, "late completion is never a fresh authorization");
      });
      await test("observation timeout cancels its queued request and discards late data", async () => {
        var d = Semantic(); d.ObservationGate = new TaskCompletionSource<bool>(); var r = Runner(d); r.ObservationTimeoutMs = 15;
        await r.StartAsync(OneStep()); Assert(r.State == "paused" && d.LastObservationToken.IsCancellationRequested && d.Calls == 0, "observation timeout cancellation");
        d.ObservationGate.SetResult(true); await Task.Delay(20); Assert(r.State == "paused" && r.Pending == null, "late observation is not a proposal");
      });
      await test("concurrent calls cannot consume the same key approval twice", async () => {
        var d = Semantic(); d.ExecutionGate = new TaskCompletionSource<bool>(); var r = Runner(d); await r.StartAsync(OneStep());
        var approved = await Task.WhenAll(Task.Run(() => r.TakeApproval()), Task.Run(() => r.TakeApproval()));
        Assert(approved.Count(candidate => candidate != null) == 1, "approval atomically consumed");
        var p = approved.Single(x => x != null); var runs = new[] { Task.Run(() => r.ExecuteApprovedAsync(p)), Task.Run(() => r.ExecuteApprovedAsync(p)) };
        d.ExecutionGate.SetResult(true); await Task.WhenAll(runs); Assert(d.Calls == 1 && r.State == "done", "one dispatch across callers");
      });
      Console.WriteLine("PASS: " + count + " desktop-runner tests. No desktop input, microphone, screenshots or API calls."); return count;
    }
  }
}
