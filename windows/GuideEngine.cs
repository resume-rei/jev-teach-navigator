using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JevTeachWindows {
  // A length-prefixed representation prevents separator characters in app text from
  // producing the same observation as a different collection of nodes.
  static class DesktopStamp {
    public static string Of(params object[] parts) {
      return String.Concat(parts.Select(p => { string value = p == null ? "" : p.ToString(); return value.Length + ":" + value; }));
    }
    public static string Hash(string value) {
      using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
    }
  }
  // Semantic task data never contains recorded coordinates or live element handles.
  // InputText is bound only for the current run; persistent skills store a parameter key.
  public sealed class TaskStep {
    public string App, Target, Role = "Button", Context = "", AutomationId = "", Description;
    public string Kind = "invoke", InputText;
    public string Result, ResultApp, ResultRole = "Text", ResultContext = "", ResultAutomationId = "";
    public string Precondition, PreconditionRole = "Text", PreconditionContext = "", PreconditionAutomationId = "";
    public bool AllowResume;
    internal TaskStep Copy() { return (TaskStep)MemberwiseClone(); }
  }
  public sealed class DesktopTask {
    public string Name;
    public List<TaskStep> Steps = new List<TaskStep>();
    public void Validate() {
      if (String.IsNullOrWhiteSpace(Name) || Steps == null || Steps.Count == 0 || Steps.Count > 80)
        throw new ArgumentException("目的と1〜80個の手順が必要です。");
      foreach (var s in Steps) {
        if (s == null || String.IsNullOrWhiteSpace(s.App) || String.IsNullOrWhiteSpace(s.Target) || String.IsNullOrWhiteSpace(s.Role) ||
            (s.Kind != "invoke" && s.Kind != "set-value"))
          throw new ArgumentException("対象アプリ・意味・役割と、invoke / set-value の操作が必要です。");
        if (new[] { s.App, s.ResultApp }.Any(app => String.Equals(app, "navigator", StringComparison.OrdinalIgnoreCase) || String.Equals(app, "outside", StringComparison.OrdinalIgnoreCase)))
          throw new ArgumentException("navigator / outside は案内の状態を表す予約名のため、対象アプリ名には使えません。");
        if (s.Kind == "set-value" && (s.InputText == null || s.InputText.Length > 20000))
          throw new ArgumentException("入力操作には確認する全文（最大20000文字）が必要です。");
        if (s.Kind == "invoke" && String.IsNullOrWhiteSpace(s.Result))
          throw new ArgumentException("対象の意味と、その操作で変わる結果が必要です。");
        if (!String.IsNullOrEmpty(s.Result) && (String.IsNullOrWhiteSpace(s.ResultRole) ||
            (s.Kind == "invoke" && s.Target == s.Result && s.Role == s.ResultRole &&
              (String.IsNullOrEmpty(s.ResultApp) || s.ResultApp == s.App) &&
              (String.IsNullOrEmpty(s.Context) || String.IsNullOrEmpty(s.ResultContext) || s.Context == s.ResultContext) &&
              (String.IsNullOrEmpty(s.AutomationId) || String.IsNullOrEmpty(s.ResultAutomationId) || s.AutomationId == s.ResultAutomationId))))
          throw new ArgumentException("操作対象とは別の、役割を指定した結果が必要です。");
        if (!String.IsNullOrEmpty(s.Precondition) && String.IsNullOrWhiteSpace(s.PreconditionRole))
          throw new ArgumentException("事前条件には対象の役割が必要です。");
      }
      // Reusing a checkpoint cannot establish which step has already completed.
      var checkpoints = Steps.Where(s => s.AllowResume).Select(s => s.Kind == "set-value" ?
        DesktopStamp.Of("value", s.App, s.Target, s.Role, s.Context, s.AutomationId, DesktopStamp.Hash(s.InputText), s.Result, s.ResultApp) :
        DesktopStamp.Of("result", s.ResultApp ?? s.App, s.Result, s.ResultRole, s.ResultContext, s.ResultAutomationId)).ToList();
      if (checkpoints.Distinct().Count() != checkpoints.Count)
        throw new ArgumentException("再開に使う結果条件は、各手順を一意に区別する必要があります。");
    }
    internal DesktopTask Copy() { return new DesktopTask { Name = Name, Steps = Steps.Select(s => s.Copy()).ToList() }; }
  }
  public sealed class DesktopNode {
    public string Name, Role, Context, Runtime, AutomationId = "";
    public Rectangle Rect;
    public bool Enabled, Visible, Invokable, Settable, ValueReadable;
    // The Windows adapter reads non-password values for live guide verification only.
    public string Value;
    public string Stamp { get { return DesktopStamp.Of(Name, Role, Context, Runtime, AutomationId, Rect, Enabled, Visible, Invokable,
      Settable, ValueReadable, ValueReadable ? DesktopStamp.Hash(Value) : ""); } }
    internal DesktopNode Copy() { return (DesktopNode)MemberwiseClone(); }
  }
  public sealed class DesktopView {
    public string App, Title, Identity, Foreground;
    public Rectangle Rect;
    public bool Exists, Minimized;
    public List<DesktopNode> Nodes = new List<DesktopNode>();
    public bool InScope { get { return !String.IsNullOrEmpty(Foreground) && Foreground != "outside"; } }
    public string Stamp { get { return DesktopStamp.Of(App, Title, Identity, Exists, Rect, Minimized,
      Foreground == App || Foreground == "navigator" ? "work-area" : Foreground,
      DesktopStamp.Of(Nodes.Select(n => (object)n.Stamp).OrderBy(s => s).ToArray())); } }
    public bool HasResult(string name) { return HasResult(name, "Text", "", ""); }
    public bool HasResult(string name, string role, string context, string automationId) {
      return Exists && !Minimized && !String.IsNullOrEmpty(name) &&
        Nodes.Count(n => Matches(n, name, role, context, automationId) && n.Visible) == 1;
    }
    internal static bool Matches(DesktopNode n, string name, string role, string context, string automationId) {
      return n.Name == name && n.Role == role && (String.IsNullOrEmpty(context) || n.Context == context) &&
        (String.IsNullOrEmpty(automationId) || n.AutomationId == automationId);
    }
  }
  public sealed class PreparedAction {
    public string Id, Kind, App, Description, ViewStamp, Identity, InputText;
    public int Step;
    public DesktopNode Target;
    public Rectangle Rect;
    public long Created;
    internal PreparedAction Copy() { var p = (PreparedAction)MemberwiseClone(); p.Target = Target == null ? null : Target.Copy(); return p; }
    internal string Proof { get { return DesktopStamp.Of(Id, Kind, App, Description, ViewStamp, Identity, InputText, Step,
      Target == null ? "" : Target.Stamp, Rect, Created); } }
  }
  public sealed class ActionTiming {
    public int Step;
    public string Kind, Outcome;
    public long ObserveMs, ExecuteMs, VerifyMs, NextReadyMs, ApprovalToReadyMs;
  }
  public class ScreenChangedException : Exception {
    public ScreenChangedException(string message) : base(message) { }
  }
  // Only an adapter that has performed NO mutating call may use this exception.
  // An ordinary ScreenChangedException after dispatch does not prove non-execution.
  public sealed class ActionNotExecutedException : ScreenChangedException {
    public ActionNotExecutedException(string message) : base(message) { }
  }
  public interface IDesktopAdapter {
    Task<DesktopView> ObserveAsync(string app, CancellationToken cancel);
    Task ExecuteAsync(PreparedAction action, CancellationToken cancel);
  }
  // Used by the real keyboard hook and the tests. A held key is never a second approval.
  public sealed class EnterLatch {
    bool held, captured;
    public bool Process(bool isDown, bool injected, bool capture, bool ready, out bool approve) {
      approve = false;
      // An injected release cannot re-arm a physically held key. An injected
      // press cannot provide human approval or change the physical-key latch.
      if (injected) return captured || capture;
      return isDown ? Down(capture, ready, out approve) : Up();
    }
    public bool Down(bool capture, bool ready, out bool approve) {
      approve = false;
      if (held) return captured;
      held = true; captured = capture; approve = capture && ready; return captured;
    }
    public bool Up() { bool result = captured; held = false; captured = false; return result; }
    public bool Held { get { return held; } }
    public void Reset() { held = false; captured = false; }
  }
  // Pure classification shared with the Windows hooks. Call KeyboardInterrupts
  // only after consuming a captured Enter/Esc through their dedicated handlers.
  public static class GuideInputPolicy {
    public static bool KeyboardInterrupts(bool down, bool injected, int key, bool navigatorForeground, bool approvalPending) {
      if (!down || injected) return false;
      // Enter passed through by the latch belongs to the target app or its IME.
      if (key == 13) return !navigatorForeground;
      // Alt/Tab/Windows-key navigation starts while our own form is foreground.
      // Catch it even before the approved operation's queued callback has begun.
      return !navigatorForeground || approvalPending;
    }
    public static bool PointerInterrupts(int message, bool injected, bool navigatorWindow) {
      return !injected && !navigatorWindow && (message == 0x201 || message == 0x204 || message == 0x207 ||
        message == 0x20b || message == 0x20a || message == 0x20e);
    }
  }
  public sealed class DesktopRunner {
    readonly IDesktopAdapter adapter;
    readonly Func<long> clock;
    readonly object sync = new object();
    CancellationTokenSource cancellation;
    int generation;
    bool monitoring, flowActive, dispatchStarted;
    System.Threading.Tasks.Task unsettledDispatch;
    PreparedAction pending, approved;
    string pendingProof, approvedProof, activeApp;
    long lastObserveMs, approvedAt;
    DesktopTask plan;
    public DesktopTask Task { get { lock (sync) return plan == null ? null : plan.Copy(); } }
    public PreparedAction Pending { get { lock (sync) return pending; } }
    public int StepIndex { get; private set; }
    public string State { get; private set; }
    public string Message { get; private set; }
    public int ExecutedCount { get; private set; }
    public bool ActionInFlight { get { lock (sync) return flowActive || (unsettledDispatch != null && !unsettledDispatch.IsCompleted); } }
    public int ProposalLifetimeMs = 15000, ResultTimeoutMs = 4000, ResultPollMs = 90, ObservationTimeoutMs = 3500, ExecutionTimeoutMs = 3500;
    public readonly List<ActionTiming> Timings = new List<ActionTiming>();
    public event Action Changed;
    public DesktopRunner(IDesktopAdapter adapter, Func<long> clock = null) {
      if (adapter == null) throw new ArgumentNullException("adapter");
      this.adapter = adapter;
      this.clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
      State = "stopped"; Message = "案内を開始すると、次の1操作を表示します。";
    }
    public bool Active { get { return State == "preparing" || State == "ready" || State == "checking" || State == "executing" || State == "verifying" || State == "waiting" || State == "suspended"; } }
    void Notify() { var h = Changed; if (h != null) h(); }
    bool CurrentLocked(int g) { return g == generation && cancellation != null && !cancellation.IsCancellationRequested; }
    bool Current(int g) { lock (sync) return CurrentLocked(g); }
    bool SetCurrent(int g, string state, string message) {
      lock (sync) { if (!CurrentLocked(g)) return false; State = state; Message = message; }
      Notify(); return Current(g); // The UI callback may synchronously cancel or approve.
    }
    void ClearProposal() { pending = null; pendingProof = null; approved = null; approvedProof = null; }
    void CancelSession() {
      generation++; ClearProposal();
      if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); cancellation = null; }
    }
    public async Task StartAsync(DesktopTask task) {
      if (task == null) throw new ArgumentNullException("task");
      task.Validate(); var snapshot = task.Copy(); snapshot.Validate(); int g;
      lock (sync) {
        if (ActionInFlight) throw new InvalidOperationException("前の操作がまだ終了していません。結果を確認するまで新しい案内は開始できません。");
        CancelSession(); plan = snapshot; StepIndex = 0; ExecutedCount = 0; activeApp = null; Timings.Clear();
        dispatchStarted = false; unsettledDispatch = null;
        cancellation = new CancellationTokenSource(); g = ++generation;
      }
      await PrepareAsync(g, null, true);
    }
    public void Stop() {
      lock (sync) { CancelSession(); State = "stopped"; Message = "案内を停止しました。Enterは通常の操作に戻ります。"; }
      Notify();
    }
    public void Pause(string reason) {
      lock (sync) {
        bool uncertain = dispatchStarted || State == "unknown" || State == "verifying";
        CancelSession(); State = uncertain ? "unknown" : "paused";
        Message = uncertain ? reason + " 操作の結果は未確認です。自動再実行しません。" : reason;
      }
      Notify();
    }
    public async Task ResumeAsync() {
      int g;
      lock (sync) {
        if (ActionInFlight || (State != "paused" && State != "waiting")) return;
        if (cancellation == null) cancellation = new CancellationTokenSource();
        g = ++generation;
      }
      await PrepareAsync(g, null, true);
    }
    void Suspend(int g) {
      lock (sync) { if (!CurrentLocked(g)) return; ClearProposal(); }
      SetCurrent(g, "suspended", "案内の対象アプリへ戻ると、自動で探し直します。いまは操作しません。");
    }
    static void ObserveFault(System.Threading.Tasks.Task job) {
      job.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    async Task<DesktopView> Observe(string app, int g) {
      CancellationToken token;
      lock (sync) { if (!CurrentLocked(g)) throw new OperationCanceledException(); token = cancellation.Token; }
      using (var operation = CancellationTokenSource.CreateLinkedTokenSource(token)) {
        long begin = clock(); var job = adapter.ObserveAsync(app, operation.Token);
        var winner = await System.Threading.Tasks.Task.WhenAny(job, System.Threading.Tasks.Task.Delay(ObservationTimeoutMs, token));
        if (winner != job) {
          operation.Cancel(); ObserveFault(job); token.ThrowIfCancellationRequested();
          throw new TimeoutException("Windowsの応答がありません。案内を保留しました。");
        }
        var view = await job;
        if (!Current(g)) throw new OperationCanceledException();
        if (view == null || view.App != app) throw new ScreenChangedException("観察したアプリを確認できません。");
        lastObserveMs = Math.Max(0, clock() - begin); return view;
      }
    }
    public static DesktopNode Resolve(TaskStep step, DesktopView view) {
      var matches = view.Nodes.Where(n => DesktopView.Matches(n, step.Target, step.Role, step.Context, step.AutomationId) &&
        n.Visible && n.Enabled && n.Rect.Width > 0 && n.Rect.Height > 0 &&
        (step.Kind == "set-value" ? n.Settable && n.ValueReadable : n.Invokable)).ToList();
      if (matches.Count != 1) throw new ScreenChangedException(matches.Count == 0 ?
        "「" + step.Target + "」が見つからないか、操作できません。" : "「" + step.Target + "」が複数あります。対象を一つに絞ってください。");
      return matches[0];
    }
    static bool PreconditionMet(TaskStep step, DesktopView view) {
      return String.IsNullOrEmpty(step.Precondition) ||
        view.HasResult(step.Precondition, step.PreconditionRole, step.PreconditionContext, step.PreconditionAutomationId);
    }
    static string ResultApp(TaskStep step) { return String.IsNullOrEmpty(step.ResultApp) ? step.App : step.ResultApp; }
    static bool ReadbackMet(TaskStep step, DesktopView view) {
      var matches = view.Nodes.Where(n => n.Visible && DesktopView.Matches(n, step.Target, step.Role, step.Context, step.AutomationId)).ToList();
      return view.Exists && !view.Minimized && matches.Count == 1 && matches[0].ValueReadable && matches[0].Value == step.InputText;
    }
    static bool ResultMet(TaskStep step, DesktopView targetView, DesktopView resultView) {
      if (step.Kind == "set-value" && !ReadbackMet(step, targetView)) return false;
      return String.IsNullOrEmpty(step.Result) ? step.Kind == "set-value" :
        resultView.HasResult(step.Result, step.ResultRole, step.ResultContext, step.ResultAutomationId);
    }
    async Task<DesktopView> ResultView(TaskStep step, DesktopView target, int g) {
      return ResultApp(step) == step.App ? target : await Observe(ResultApp(step), g);
    }
    // A skill author must explicitly declare a result as a resume checkpoint.
    // A single unique checkpoint can establish a later stage; conflicting visible
    // checkpoints are held for a person, rather than guessed from sequence/absence.
    async Task<bool> ReconcileAsync(int g, DesktopView supplied) {
      var views = new Dictionary<string, DesktopView>();
      if (supplied != null) views[supplied.App] = supplied;
      var completed = new List<int>();
      for (int i = StepIndex; i < plan.Steps.Count; i++) {
        var step = plan.Steps[i]; if (!step.AllowResume) continue;
        DesktopView target;
        if (!views.TryGetValue(step.App, out target)) { target = await Observe(step.App, g); views[step.App] = target; }
        if (!target.InScope) { Suspend(g); return false; }
        DesktopView result;
        if (!views.TryGetValue(ResultApp(step), out result)) { result = await Observe(ResultApp(step), g); views[ResultApp(step)] = result; }
        if (!result.InScope) { Suspend(g); return false; }
        if (ResultMet(step, target, result)) completed.Add(i);
      }
      if (!Current(g)) return false;
      if (completed.Count > 1) {
        SetCurrent(g, "waiting", "複数の再開条件が同時に見えています。今回の作業の状態を一つに特定できるまで保留します。"); return false;
      }
      if (completed.Count == 1) StepIndex = completed[0] + 1;
      return true;
    }
    async Task PrepareAsync(int g, DesktopView supplied, bool reconcile = false) {
      lock (sync) { if (!CurrentLocked(g)) return; pending = null; pendingProof = null; }
      if (!SetCurrent(g, "preparing", "現在の画面から、次の操作を探しています…")) return;
      try {
        if (reconcile && !await ReconcileAsync(g, supplied)) return;
        if (!Current(g)) return;
        if (StepIndex >= plan.Steps.Count) { SetCurrent(g, "done", "完了を画面で確認しました。すべての操作が終わりました。"); return; }
        var step = plan.Steps[StepIndex]; var v = supplied != null && supplied.App == step.App ? supplied : await Observe(step.App, g);
        if (!Current(g)) return;
        if (!v.InScope) { Suspend(g); return; }
        if (!v.Exists) { SetCurrent(g, "waiting", "登録した対象アプリを確認できません。アプリを開き、必要なら今回のウィンドウを登録し直してください。"); return; }
        PreparedAction proposal;
        // Activation itself is a separately displayed and approved operation.
        if ((v.Foreground != step.App && !(v.Foreground == "navigator" && activeApp == step.App)) || v.Minimized) {
          proposal = NewAction("activate", v, null, "「" + v.Title + "」へ切り替える", null);
        } else {
          activeApp = step.App;
          var result = await ResultView(step, v, g); if (!Current(g)) return;
          if (!result.InScope) { Suspend(g); return; }
          if (ResultMet(step, v, result)) {
            if (step.AllowResume) { StepIndex++; await PrepareAsync(g, null, true); return; }
            SetCurrent(g, "waiting", "結果条件が操作前から見えています。未実行か完了済みかを判断できないため、操作を保留しました。"); return;
          }
          if (!PreconditionMet(step, v)) { SetCurrent(g, "waiting", "教えた事前条件を一意に確認できません。画面を確認してください。"); return; }
          var target = Resolve(step, v);
          proposal = NewAction(step.Kind, v, target, step.Description ?? (step.Kind == "set-value" ? "「" + step.Target + "」へ表示した全文を入力する" : "「" + step.Target + "」を押す"), step.InputText);
        }
        lock (sync) { if (!CurrentLocked(g)) return; pending = proposal; pendingProof = proposal.Proof; }
        SetCurrent(g, "ready", "矢印と操作内容を確認して、Enterでこの1操作を実行します。");
      } catch (OperationCanceledException) { }
      catch (ScreenChangedException ex) { SetCurrent(g, "waiting", ex.Message + " 画面が変わると自動で探し直します。"); }
      catch (Exception ex) { SetCurrent(g, "paused", ex.Message); }
    }
    PreparedAction NewAction(string kind, DesktopView view, DesktopNode node, string text, string input) {
      return new PreparedAction { Id = Guid.NewGuid().ToString("N"), Kind = kind, App = view.App,
        Description = text, InputText = kind == "set-value" ? input : null, Step = StepIndex, Target = node == null ? null : node.Copy(), Rect = node == null ?
          (view.Minimized ? Rectangle.Empty : new Rectangle(view.Rect.X + 12, view.Rect.Y + 6, Math.Max(80, view.Rect.Width - 24), 28)) : node.Rect,
        ViewStamp = view.Stamp, Identity = view.Identity, Created = clock() };
    }
    // Called synchronously at key-down. Nothing is queued for a future proposal.
    public PreparedAction TakeApproval() {
      PreparedAction p;
      lock (sync) {
        if (State != "ready" || pending == null || flowActive || cancellation == null) return null;
        if (pending.Proof != pendingProof) {
          ClearProposal(); State = "paused"; Message = "提示内容が変更されたため、承認を無効にしました。再観察してください。"; p = null;
        } else {
          p = pending; pending = null; pendingProof = null; approved = p; approvedProof = p.Proof; approvedAt = clock(); ++generation;
          State = "executing"; Message = "承認した1操作を確認しています…";
        }
      }
      Notify(); return p;
    }
    async Task SettleLateDispatch(System.Threading.Tasks.Task job) {
      try { await job; } catch { }
      lock (sync) { if (Object.ReferenceEquals(unsettledDispatch, job)) { unsettledDispatch = null; dispatchStarted = false; } }
      Notify();
    }
    public async Task ExecuteApprovedAsync(PreparedAction requested) {
      PreparedAction p; int g; CancellationToken cancel;
      lock (sync) {
        if (requested == null || !Object.ReferenceEquals(approved, requested) || State != "executing" || cancellation == null || flowActive) return;
        if (requested.Step != StepIndex || requested.Proof != approvedProof) {
          ClearProposal(); State = "paused"; Message = "承認後に内容が変更されました。操作していません。"; p = null; g = generation; cancel = CancellationToken.None;
        } else {
          p = requested.Copy(); approved = null; approvedProof = null; flowActive = true; dispatchStarted = false; g = generation; cancel = cancellation.Token;
        }
      }
      if (p == null) { Notify(); return; }
      bool dispatched = false, reprepare = false, adapterRejected = false;
      var timing = new ActionTiming { Step = p.Step + 1, Kind = p.Kind, ObserveMs = lastObserveMs, Outcome = "not-executed" };
      System.Threading.Tasks.Task execution = null; long executedAt = 0;
      try {
        if (!SetCurrent(g, "checking", "実行直前の画面を確認しています…")) return;
        var step = plan.Steps[p.Step];
        var v = await Observe(p.App, g); if (!Current(g)) return;
        if (!v.InScope) { Suspend(g); return; }
        if (!v.Exists || clock() - p.Created > ProposalLifetimeMs || v.Stamp != p.ViewStamp || v.Identity != p.Identity) {
          await PrepareAsync(g, v, true); return;
        }
        DesktopView resultBefore = null;
        if (p.Kind != "activate") {
          var freshTarget = Resolve(step, v);
          if (freshTarget.Stamp != p.Target.Stamp || !PreconditionMet(step, v)) { await PrepareAsync(g, v, true); return; }
          resultBefore = await ResultView(step, v, g); if (!Current(g)) return;
          if (!resultBefore.InScope) { Suspend(g); return; }
          // An external result app must already be observed and bound to a live
          // identity. A newly launched unknown process cannot attest completion.
          if (!resultBefore.Exists || String.IsNullOrEmpty(resultBefore.Identity)) {
            SetCurrent(g, "waiting", "結果を確認する登録アプリが見つからないため、操作していません。"); return;
          }
          if (ResultMet(step, v, resultBefore)) { await PrepareAsync(g, v, true); return; }
        }
        if (!SetCurrent(g, "executing", p.Description + "…")) return;
        using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
          long begin = clock(); bool expired = false;
          lock (sync) {
            if (!CurrentLocked(g)) return;
            cancel.ThrowIfCancellationRequested();
            if (clock() - p.Created > ProposalLifetimeMs) expired = true;
            else {
              dispatched = true; dispatchStarted = true;
              try { execution = adapter.ExecuteAsync(p, operation.Token); unsettledDispatch = execution; }
              catch (ActionNotExecutedException) { adapterRejected = true; throw; }
            }
          }
          if (expired) { await PrepareAsync(g, null, true); return; }
          try {
            var winner = await System.Threading.Tasks.Task.WhenAny(execution, System.Threading.Tasks.Task.Delay(ExecutionTimeoutMs, cancel));
            if (winner != execution) {
              operation.Cancel(); ObserveFault(execution); cancel.ThrowIfCancellationRequested();
              throw new TimeoutException("操作の応答を確認できません。");
            }
            try { await execution; } catch (ActionNotExecutedException) { adapterRejected = true; throw; }
          } finally { if (!execution.IsCompleted) operation.Cancel(); }
          if (!Current(g)) return;
          timing.ExecuteMs = Math.Max(0, clock() - begin); executedAt = clock(); ExecutedCount++;
        }
        if (!SetCurrent(g, "verifying", "操作後の画面を確認しています…")) return;
        var watch = Stopwatch.StartNew(); bool verified = false;
        do {
          v = await Observe(p.App, g); if (!Current(g)) return;
          if (!v.InScope || !v.Exists || v.Identity != p.Identity) throw new InvalidOperationException("操作後に対象アプリまたは前面の状態が変わり、結果を確認できません。");
          if (p.Kind == "activate") verified = !v.Minimized && v.Foreground == p.App;
          else {
            var resultAfter = await ResultView(step, v, g); if (!Current(g)) return;
            if (!resultAfter.InScope || !resultAfter.Exists || resultAfter.Identity != resultBefore.Identity)
              throw new InvalidOperationException("結果を確認するアプリが変わりました。");
            verified = ResultMet(step, v, resultAfter);
          }
          if (verified) break;
          await System.Threading.Tasks.Task.Delay(ResultPollMs, cancel);
        } while (watch.ElapsedMilliseconds < ResultTimeoutMs);
        timing.VerifyMs = Math.Max(0, clock() - executedAt);
        if (!verified) throw new InvalidOperationException("期待した画面の変化を確認できませんでした。");
        timing.Outcome = "verified";
        lock (sync) {
          if (!CurrentLocked(g)) return;
          activeApp = p.App; dispatchStarted = false;
          if (p.Kind != "activate") StepIndex++;
        }
        // The next arrow is automatic; its approval is always a future key-down.
        await PrepareAsync(g, StepIndex < plan.Steps.Count && plan.Steps[StepIndex].App == p.App ? v : null);
        timing.NextReadyMs = Math.Max(0, clock() - executedAt);
        timing.ApprovalToReadyMs = Math.Max(0, clock() - approvedAt);
      } catch (OperationCanceledException) {
        timing.Outcome = dispatched ? "cancelled-after-dispatch" : "cancelled";
        if (Current(g)) SetCurrent(g, dispatched ? "unknown" : "paused", dispatched ? "操作の結果は未確認です。自動で再実行しません。" : "操作前に中断しました。");
      } catch (ScreenChangedException ex) {
        if (!dispatched || adapterRejected) { dispatched = false; reprepare = true; timing.Outcome = "not-executed"; }
        else { timing.Outcome = "unknown"; SetCurrent(g, "unknown", ex.Message + " 操作の結果は未確認です。自動で再実行しません。"); }
      } catch (Exception ex) {
        timing.Outcome = dispatched ? "unknown" : "not-executed";
        SetCurrent(g, dispatched ? "unknown" : "paused", ex.Message + (dispatched ? " 自動で再実行しません。" : " 操作していません。"));
      } finally {
        bool pendingExecution = execution != null && !execution.IsCompleted;
        lock (sync) {
          if (timing.Outcome == "not-executed" && dispatched && !CurrentLocked(g)) timing.Outcome = "cancelled-after-dispatch";
          Timings.Add(timing); flowActive = false;
          if (!pendingExecution) { if (Object.ReferenceEquals(unsettledDispatch, execution)) unsettledDispatch = null; dispatchStarted = false; }
        }
        Notify();
        if (pendingExecution) { var settlement = SettleLateDispatch(execution); ObserveFault(settlement); }
      }
      if (reprepare && Current(g)) await PrepareAsync(g, null, true);
    }
    // Passive observations can invalidate a proposal, but never execute anything.
    public async Task RefreshAsync() {
      int g; PreparedAction prior;
      lock (sync) {
        if (monitoring || flowActive || plan == null || StepIndex >= plan.Steps.Count || (State != "ready" && State != "waiting" && State != "suspended")) return;
        monitoring = true; g = generation; prior = pending;
      }
      try {
        var v = await Observe(plan.Steps[StepIndex].App, g); if (!Current(g)) return;
        if (!v.InScope) { Suspend(g); return; }
        if (prior == null || prior.ViewStamp != v.Stamp || clock() - prior.Created > ProposalLifetimeMs ||
            prior.Proof != pendingProof || (prior.Kind == "activate" && v.Foreground == prior.App))
          await PrepareAsync(g, v, true);
      } catch (OperationCanceledException) { }
      catch (Exception ex) {
        lock (sync) { if (CurrentLocked(g)) { pending = null; pendingProof = null; } }
        SetCurrent(g, "paused", ex.Message);
      } finally { lock (sync) monitoring = false; }
    }
  }
}
