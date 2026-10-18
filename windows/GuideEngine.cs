using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JevTeachWindows {
  // Semantic task data never contains recorded coordinates or live element handles.
  public sealed class TaskStep {
    public string App, Target, Role = "Button", Context = "", Result, Description;
  }
  public sealed class DesktopTask {
    public string Name;
    public List<TaskStep> Steps = new List<TaskStep>();
    public void Validate() {
      if (String.IsNullOrWhiteSpace(Name) || Steps.Count == 0 || Steps.Count > 80)
        throw new ArgumentException("目的と1〜80個の手順が必要です。");
      foreach (var s in Steps)
        if (String.IsNullOrWhiteSpace(s.App) || String.IsNullOrWhiteSpace(s.Target) ||
            String.IsNullOrWhiteSpace(s.Result) || s.Target == s.Result)
          throw new ArgumentException("対象の意味と、その操作で変わる結果が必要です。");
    }
  }
  public sealed class DesktopNode {
    public string Name, Role, Context, Runtime;
    public Rectangle Rect;
    public bool Enabled, Visible, Invokable;
    public string Stamp { get { return Name + "|" + Role + "|" + Context + "|" + Runtime + "|" + Rect + "|" + Enabled + "|" + Visible + "|" + Invokable; } }
  }
  public sealed class DesktopView {
    public string App, Title, Identity, Foreground;
    public Rectangle Rect;
    public bool Exists, Minimized;
    public List<DesktopNode> Nodes = new List<DesktopNode>();
    public bool InScope { get { return Foreground != "outside"; } }
    public string Stamp { get { return Identity + "|" + Rect + "|" + Minimized + "|" + (Foreground == App || Foreground == "navigator" ? "work-area" : Foreground) + "\n" + String.Join("\n", Nodes.Select(n => n.Stamp).OrderBy(s => s)); } }
    public bool HasResult(string name) { return Nodes.Count(n => n.Visible && n.Name == name && n.Role == "Text") == 1; }
  }
  public sealed class PreparedAction {
    public string Id, Kind, App, Description, ViewStamp, Identity;
    public int Step;
    public DesktopNode Target;
    public Rectangle Rect;
    public long Created;
  }
  public sealed class ActionTiming {
    public int Step;
    public string Kind, Outcome;
    public long ObserveMs, ExecuteMs, VerifyMs, NextReadyMs, ApprovalToReadyMs;
  }
  public sealed class ScreenChangedException : Exception {
    public ScreenChangedException(string message) : base(message) { }
  }
  public interface IDesktopAdapter {
    Task<DesktopView> ObserveAsync(string app, CancellationToken cancel);
    Task ExecuteAsync(PreparedAction action, CancellationToken cancel);
  }
  // Used by the real keyboard hook and the tests. A held key is never a second approval.
  public sealed class EnterLatch {
    bool held, captured;
    public bool Down(bool capture, bool ready, out bool approve) {
      approve = false;
      if (held) return captured;
      held = true; captured = capture; approve = capture && ready; return captured;
    }
    public bool Up() { bool result = captured; held = false; captured = false; return result; }
    public bool Held { get { return held; } }
    public void Reset() { held = false; captured = false; }
  }
  public sealed class DesktopRunner {
    readonly IDesktopAdapter adapter;
    readonly Func<long> clock;
    CancellationTokenSource cancellation;
    int generation;
    bool monitoring;
    PreparedAction approved;
    string activeApp;
    long lastObserveMs, approvedAt;
    public DesktopTask Task { get; private set; }
    public PreparedAction Pending { get; private set; }
    public int StepIndex { get; private set; }
    public string State { get; private set; }
    public string Message { get; private set; }
    public int ExecutedCount { get; private set; }
    public bool ActionInFlight { get; private set; }
    public int ProposalLifetimeMs = 15000, ResultTimeoutMs = 4000, ResultPollMs = 90;
    public readonly List<ActionTiming> Timings = new List<ActionTiming>();
    public event Action Changed;
    public DesktopRunner(IDesktopAdapter adapter, Func<long> clock = null) {
      this.adapter = adapter;
      this.clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
      State = "stopped"; Message = "案内を開始すると、次の1操作を表示します。";
    }
    public bool Active { get { return State == "preparing" || State == "ready" || State == "checking" || State == "executing" || State == "verifying" || State == "waiting" || State == "suspended"; } }
    void Set(string state, string message) { State = state; Message = message; var h = Changed; if (h != null) h(); }
    bool Current(int g) { return g == generation && cancellation != null && !cancellation.IsCancellationRequested; }
    public async Task StartAsync(DesktopTask task) {
      task.Validate(); Stop(); Task = task; StepIndex = 0; ExecutedCount = 0; activeApp = null; Timings.Clear();
      cancellation = new CancellationTokenSource(); int g = ++generation;
      await PrepareAsync(g, null);
    }
    public void Stop() {
      generation++; Pending = null; approved = null;
      if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); cancellation = null; }
      Set("stopped", "案内を停止しました。Enterは通常の操作に戻ります。");
    }
    public void Pause(string reason) {
      bool mayHaveExecuted = State == "executing" || State == "verifying";
      generation++; Pending = null; approved = null;
      if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); cancellation = null; }
      Set(mayHaveExecuted ? "unknown" : "paused", mayHaveExecuted ? reason + " 操作の結果は未確認です。自動再実行しません。" : reason);
    }
    public async Task ResumeAsync() {
      if (State != "paused" && State != "waiting") return;
      if (cancellation == null) cancellation = new CancellationTokenSource();
      int g = ++generation; await PrepareAsync(g, null);
    }
    void Suspend() { Pending = null; approved = null; Set("suspended", "案内の対象アプリへ戻ると、自動で探し直します。いまは操作しません。"); }
    async Task<DesktopView> Observe(string app, int g) {
      var cancel = cancellation.Token; long begin = clock();
      var job = adapter.ObserveAsync(app, cancel);
      var winner = await System.Threading.Tasks.Task.WhenAny(job, System.Threading.Tasks.Task.Delay(3500, cancel));
      if (winner != job) { cancel.ThrowIfCancellationRequested(); throw new TimeoutException("Windowsの応答がありません。案内を保留しました。"); }
      var view = await job; lastObserveMs = Math.Max(0, clock() - begin);
      if (!Current(g)) throw new OperationCanceledException(); return view;
    }
    public static DesktopNode Resolve(TaskStep step, DesktopView view) {
      var matches = view.Nodes.Where(n => n.Name == step.Target && n.Role == step.Role &&
        (String.IsNullOrEmpty(step.Context) || n.Context == step.Context) && n.Visible && n.Enabled && n.Invokable).ToList();
      if (matches.Count != 1) throw new ScreenChangedException(matches.Count == 0 ?
        "「" + step.Target + "」が見つからないか、操作できません。" : "「" + step.Target + "」が複数あります。対象を一つに絞ってください。");
      return matches[0];
    }
    async Task PrepareAsync(int g, DesktopView supplied) {
      if (!Current(g)) return;
      Pending = null;
      if (StepIndex >= Task.Steps.Count) { Set("done", "完了を画面で確認しました。すべての操作が終わりました。"); return; }
      Set("preparing", "現在の画面から、次の操作を探しています…");
      try {
        var step = Task.Steps[StepIndex]; var v = supplied ?? await Observe(step.App, g);
        if (!Current(g)) return;
        if (!v.InScope) { Suspend(); return; }
        if (!v.Exists) { Set("waiting", "対象アプリが閉じられています。練習をやり直してください。"); return; }
        // Window activation is a separately displayed, separately approved operation.
        if ((v.Foreground != step.App && !(v.Foreground == "navigator" && activeApp == step.App)) || v.Minimized) {
          Pending = NewAction("activate", v, null, "「" + v.Title + "」へ切り替える");
        } else {
          activeApp = step.App;
          var target = Resolve(step, v);
          Pending = NewAction("invoke", v, target, step.Description ?? "「" + step.Target + "」を押す");
        }
        Set("ready", "矢印と操作内容を確認して、Enterでこの1操作を実行します。");
      } catch (OperationCanceledException) { }
      catch (ScreenChangedException ex) { if (Current(g)) Set("waiting", ex.Message + " 画面が変わると自動で探し直します。"); }
      catch (Exception ex) { if (Current(g)) Set("paused", ex.Message); }
    }
    PreparedAction NewAction(string kind, DesktopView view, DesktopNode node, string text) {
      return new PreparedAction { Id = Guid.NewGuid().ToString("N"), Kind = kind, App = view.App,
        Description = text, Step = StepIndex, Target = node, Rect = node == null ?
          (view.Minimized ? Rectangle.Empty : new Rectangle(view.Rect.X + 12, view.Rect.Y + 6, Math.Max(80, view.Rect.Width - 24), 28)) : node.Rect,
        ViewStamp = view.Stamp, Identity = view.Identity, Created = clock() };
    }
    // Called synchronously at key-down. Nothing is queued for a future proposal.
    public PreparedAction TakeApproval() {
      if (State != "ready" || Pending == null) return null;
      var p = Pending; Pending = null; approved = p; approvedAt = clock(); ++generation;
      Set("executing", "承認した1操作を確認しています…"); return p;
    }
    public async Task ExecuteApprovedAsync(PreparedAction p) {
      if (p == null || !Object.ReferenceEquals(approved, p) || State != "executing" || p.Step != StepIndex || cancellation == null) return;
      approved = null; ActionInFlight = true;
      // Even a duplicate caller holding this object cannot dispatch twice.
      Set("checking", "実行直前の画面を確認しています…");
      int g = generation; bool dispatched = false, reprepare = false; var cancel = cancellation.Token;
      var timing = new ActionTiming { Step = StepIndex + 1, Kind = p.Kind, ObserveMs = lastObserveMs, Outcome = "not-executed" };
      long executedAt = 0;
      try {
        var v = await Observe(p.App, g); if (!Current(g)) return;
        if (!v.InScope) { Suspend(); return; }
        if (clock() - p.Created > ProposalLifetimeMs || v.Stamp != p.ViewStamp) {
          await PrepareAsync(g, v); return;
        }
        long begin = clock(); dispatched = true;
        Set("executing", p.Description + "…");
        var job = adapter.ExecuteAsync(p, cancel);
        var winner = await System.Threading.Tasks.Task.WhenAny(job, System.Threading.Tasks.Task.Delay(3500, cancel));
        if (winner != job) { cancel.ThrowIfCancellationRequested(); throw new TimeoutException("操作の応答を確認できません。"); }
        await job; if (!Current(g)) return;
        timing.ExecuteMs = Math.Max(0, clock() - begin); executedAt = clock(); ExecutedCount++;
        Set("verifying", "操作後の画面を確認しています…");
        var watch = Stopwatch.StartNew(); bool verified = false;
        do {
          v = await Observe(p.App, g); if (!Current(g)) return;
          verified = p.Kind == "activate" ? v.Exists && !v.Minimized && v.Foreground == p.App : v.HasResult(Task.Steps[StepIndex].Result);
          if (verified) break;
          await System.Threading.Tasks.Task.Delay(ResultPollMs, cancel);
        } while (watch.ElapsedMilliseconds < ResultTimeoutMs);
        timing.VerifyMs = Math.Max(0, clock() - executedAt);
        if (!verified) throw new InvalidOperationException("期待した画面の変化を確認できませんでした。");
        timing.Outcome = "verified";
        activeApp = p.App;
        if (p.Kind == "invoke") StepIndex++;
        // This is the automatic next-arrow path. No new approval is generated here.
        await PrepareAsync(g, StepIndex < Task.Steps.Count && Task.Steps[StepIndex].App == p.App ? v : null);
        timing.NextReadyMs = Math.Max(0, clock() - executedAt);
        timing.ApprovalToReadyMs = Math.Max(0, clock() - approvedAt);
      } catch (OperationCanceledException) { timing.Outcome = dispatched ? "cancelled-after-dispatch" : "cancelled"; }
      catch (ScreenChangedException) { dispatched = false; reprepare = true; }
      catch (Exception ex) {
        timing.Outcome = dispatched ? "unknown" : "not-executed";
        if (Current(g)) { Pending = null; Set(dispatched ? "unknown" : "paused", ex.Message + (dispatched ? " 自動で再実行しません。" : " 操作していません。")); }
      } finally {
        Timings.Add(timing); ActionInFlight = false; var h = Changed; if (h != null) h();
      }
      if (reprepare && Current(g)) await PrepareAsync(g, null);
    }
    // Passive observations can invalidate a proposal, but never execute anything.
    public async Task RefreshAsync() {
      if (monitoring || (State != "ready" && State != "waiting" && State != "suspended")) return;
      monitoring = true; int g = generation; var prior = Pending;
      try {
        var v = await Observe(Task.Steps[StepIndex].App, g); if (!Current(g)) return;
        if (!v.InScope) { if (State != "suspended") Suspend(); return; }
        if (prior == null || prior.ViewStamp != v.Stamp || clock() - prior.Created > ProposalLifetimeMs || (prior.Kind == "activate" && v.Foreground == prior.App))
          await PrepareAsync(g, v);
      } catch (OperationCanceledException) { }
      catch (Exception ex) { if (Current(g)) { Pending = null; Set("paused", ex.Message); } }
      finally { monitoring = false; }
    }
  }
}
