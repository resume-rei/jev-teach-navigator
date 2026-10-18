using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace JevTeachWindows {
  sealed class DesktopArrow : Form {
    Rectangle target;
    bool fromRight;
    public DesktopArrow() {
      Text = "Jev 対象の矢印"; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
      ShowInTaskbar = false; TopMost = true; BackColor = Color.Fuchsia; TransparencyKey = Color.Fuchsia; DoubleBuffered = true; Enabled = false;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x20 | 0x80000 | 0x8000000 | 0x80; return p; } }
    public void Aim(Rectangle rect) {
      if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) { Hide(); return; }
      var area = Screen.FromRectangle(rect).Bounds;
      fromRight = rect.Left - 58 < area.Left;
      target = new Rectangle(fromRight ? 5 : 58, 6, rect.Width, rect.Height);
      // Show/Bounds on a topmost managed Form can activate it during relocation.
      // Explicit NOACTIVATE is required on every position/show operation.
      Native.SetWindowPos(Handle, new IntPtr(-1), fromRight ? rect.Left - 5 : rect.Left - 58,
        rect.Top - 6, rect.Width + 68, rect.Height + 13, 0x10 | 0x40);
      Invalidate();
    }
    public new void Hide() { if (IsHandleCreated) Native.ShowWindow(Handle, 0); }
    protected override void OnPaint(PaintEventArgs e) {
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      using (var outline = new Pen(Color.White, 7)) e.Graphics.DrawRectangle(outline, target);
      using (var pen = new Pen(Color.FromArgb(187, 43, 50), 3)) {
        e.Graphics.DrawRectangle(pen, target); pen.Width = 6; pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;
        int y = target.Top + target.Height / 2;
        e.Graphics.DrawLine(pen, fromRight ? target.Right + 51 : 3, y, fromRight ? target.Right + 8 : 49, y);
      }
    }
  }
  sealed class DesktopNavigator : Form {
    readonly Label status = new Label(), action = new Label(), message = new Label(), progress = new Label(), latency = new Label();
    readonly ListBox steps = new ListBox();
    readonly Button start = new Button(), pause = new Button(), stop = new Button(), skills = new Button(), approval = new Button();
    readonly TextBox inputPreview = new TextBox();
    string presentedId;
    bool practiceRun, returnApprovalFocus, manualIntervention;
    readonly DesktopArrow arrow = new DesktopArrow();
    readonly System.Windows.Forms.Timer monitor = new System.Windows.Forms.Timer();
    readonly EnterLatch enter = new EnterLatch();
    DesktopSession session;
    WindowsDesktop desktop;
    DesktopRunner runner;
    Native.Hook keyCallback, interventionCallback;
    IntPtr keyHook, interventionHook;
    bool starting, closing, saved;
    public DesktopNavigator() {
      Text = "Jev Teach Navigator · Windows案内"; AccessibleName = "Windows案内";
      AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Yu Gothic UI", 10); BackColor = Color.FromArgb(247, 248, 244);
      Width = 470; Height = 850; MinimumSize = new Size(420, 680); StartPosition = FormStartPosition.Manual; TopMost = true;
      var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - Width - 18, area.Top + 22);
      var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 15, AutoScroll = true };
      Controls.Add(root);
      Add(root, new Label { Text = "Windowsを、Enterで進める", Font = new Font(Font.FontFamily, 17, FontStyle.Bold) }, 44);
      Add(root, new Label { Text = "登録アプリで教える → 保存 → 選択 → 連続案内\nローカル実行 · 記録停止 · マイク停止", ForeColor = Color.FromArgb(66, 83, 80) }, 52);
      start.Text = "練習アプリを開いて案内を開始"; start.BackColor = Color.FromArgb(27, 95, 87); start.ForeColor = Color.White;
      start.FlatStyle = FlatStyle.Flat; start.Click += async (s,e) => await StartPractice(); Add(root, start, 48);
      skills.Text = "アプリ登録・教える・保存スキル"; skills.Click += async (s,e) => await OpenWorkspace(); Add(root, skills, 44);
      status.Text = "■ 停止中"; status.Font = new Font(Font.FontFamily, 12, FontStyle.Bold); Add(root, status, 38);
      progress.Text = "保存スキル、または練習の5手順を選べます。"; Add(root, progress, 30);
      action.Text = "開始すると、次の操作と赤い矢印が出ます。"; action.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
      action.BackColor = Color.White; action.Padding = new Padding(10); Add(root, action, 85);
      inputPreview.Multiline = true; inputPreview.ReadOnly = true; inputPreview.ScrollBars = ScrollBars.Vertical; inputPreview.ImeMode = ImeMode.Disable; inputPreview.Text = "入力操作では、ここに今回入力する全文を表示します。"; Add(root, inputPreview, 95);
      approval.Text = "内容を確認して、ここでEnter"; approval.AccessibleName = "提示済みの1操作をEnterで承認"; approval.ImeMode = ImeMode.Disable; Add(root, approval, 40);
      message.Text = "矢印を見てEnter。操作後は次の矢印が自動で出ます。\nEscで一時停止。連打を先の操作へ持ち越しません。"; Add(root, message, 70);
      steps.BorderStyle = BorderStyle.None; steps.BackColor = BackColor; steps.TabStop = false; steps.ItemHeight = 25; Add(root, steps, 128);
      latency.ForeColor = Color.FromArgb(66, 83, 80); latency.Text = "APIを使わずに試せます。"; Add(root, latency, 30);
      var buttons = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill };
      buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      pause.Text = "一時停止"; pause.Enabled = false; pause.Dock = DockStyle.Fill;
      pause.Click += async (s,e) => { if (runner == null) return; if (runner.State == "paused" || runner.State == "waiting") { approval.Focus(); await runner.ResumeAsync(); } else runner.Pause("一時停止中です。準備ができたら再開できます。"); };
      stop.Text = "案内を終了"; stop.Enabled = false; stop.Dock = DockStyle.Fill;
      stop.Click += (s,e) => { if (runner != null) runner.Stop(); ReleaseHookWhenKeyUp(); SaveMetrics(); };
      buttons.Controls.Add(pause, 0, 0); buttons.Controls.Add(stop, 1, 0); Add(root, buttons, 42);
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      Deactivate += (s,e) => {
        if (!practiceRun && runner != null && runner.State == "ready") {
          returnApprovalFocus = false; runner.Pause("利用者の操作に戻りました。ナビの承認欄へ戻り、案内を再開してください。");
        }
      };
      monitor.Interval = 450; monitor.Tick += async (s,e) => { if (runner != null && !closing) await runner.RefreshAsync(); }; monitor.Start();
      FormClosing += (s,e) => { closing = true; if (runner != null) runner.Stop(); Unhook(); SaveMetrics(); monitor.Stop(); arrow.Dispose(); if (desktop != null) desktop.Dispose(); if (!starting && session != null) session.Dispose(); };
    }
    void Add(TableLayoutPanel parent, Control c, int height) {
      c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 4, 0, 4);
      int row = parent.Controls.Count; parent.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); parent.Controls.Add(c, 0, row);
    }
    async Task StartPractice() {
      if (enter.Held) { message.Text = "Enterを離してから新しい案内を開始してください。"; return; }
      if (starting || (runner != null && runner.ActionInFlight)) return; starting = true; start.Enabled = false;
      try {
        if (runner != null) runner.Stop(); Unhook(); SaveMetrics();
        if (runner != null) runner.Changed -= Render;
        runner = null;
        if (desktop != null) desktop.Dispose(); if (session != null) session.Dispose();
        status.Text = "練習アプリを起動中…"; saved = false;
        var practice = new PracticeSession(Handle); session = practice; await Task.Run(() => practice.Launch()); practiceRun = true;
        if (closing) { session.Dispose(); return; }
        desktop = new WindowsDesktop(session); runner = new DesktopRunner(desktop); runner.Changed += Render;
        Hook(); await runner.StartAsync(PracticeTask.Create());
      } catch (Exception ex) { Unhook(); if (session != null) session.Dispose(); if (!closing) { status.Text = "開始できませんでした"; message.Text = ex.Message; } }
      finally { starting = false; Render(); }
    }
    async Task OpenWorkspace() {
      if (enter.Held) { message.Text = "Enterを離してから保存スキルを開いてください。"; return; }
      if (starting || (runner != null && (runner.Active || runner.ActionInFlight))) return;
      starting = true; skills.Enabled = false;
      try {
        if (runner != null) runner.Stop(); ReleaseHookWhenKeyUp(); SaveMetrics();
        if (session == null) session = new DesktopSession(Handle);
        var dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "data", "windows", "skills"));
        using (var workspace = new SkillWorkspace(session, new NativeSkillStore(dir))) {
          if (workspace.ShowDialog(this) != DialogResult.OK || workspace.SelectedTask == null) return;
          if (runner != null) runner.Changed -= Render;
          if (desktop != null) desktop.Dispose();
          desktop = new WindowsDesktop(session); runner = new DesktopRunner(desktop); runner.Changed += Render;
          practiceRun = false; saved = false; presentedId = null; returnApprovalFocus = false; manualIntervention = false;
          Activate(); approval.Focus(); Unhook(); Hook(); await runner.StartAsync(workspace.SelectedTask);
        }
      } catch (Exception ex) { Unhook(); status.Text = "案内を開始できませんでした"; message.Text = ex.Message; }
      finally { starting = false; Render(); }
    }
    void Render() {
      if (closing || IsDisposed) return;
      if (runner == null) { start.Enabled = !starting; skills.Enabled = !starting; return; }
      var r = runner; var p = r.Pending;
      string label = r.State == "ready" ? "● Enterで実行できます" : r.State == "done" ? "✓ 完了" : r.State == "suspended" ? "対象アプリに戻るのを待っています" :
        r.State == "paused" ? "Ⅱ 一時停止" : r.State == "waiting" ? "対象を探しています" :
        r.State == "unknown" ? "結果を確認できません" : r.State == "stopped" ? "■ 停止中" : "処理中…";
      status.Text = label; action.Text = p == null ? (r.State == "done" ? "保存した完了条件を確認しました。" : r.State == "unknown" ? "画面を確認してください。自動で再実行しません。" : r.Message) : p.Description;
      progress.Text = r.Task == null ? "" : Math.Min(r.StepIndex + 1, r.Task.Steps.Count) + " / " + r.Task.Steps.Count + " 手順 · " + r.ExecutedCount + " 操作済み";
      message.Text = r.Message + (r.State == "ready" ? "\nEscで一時停止。" : "");
      steps.BeginUpdate(); steps.Items.Clear();
      if (r.Task != null) for (int i = 0; i < r.Task.Steps.Count; i++) steps.Items.Add((i < r.StepIndex ? "✓ " : i == r.StepIndex ? "→ " : "   ") + r.Task.Steps[i].Description);
      steps.EndUpdate();
      if (p != null && r.State == "ready") {
        if (presentedId != p.Id) {
          inputPreview.Text = p.Kind == "set-value" ? (p.InputText ?? "") : "この操作では文字を入力しません。";
          // General-app approvals stay in our own non-editing surface. Returning
          // our UI focus is not an additional action on the target application.
          // Custom editors/TSF IMEs are never intercepted in a general app.
          if (!practiceRun && returnApprovalFocus && !manualIntervention && session != null && session.ForegroundInScope()) {
            returnApprovalFocus = false; Activate(); approval.Focus();
            if (Native.GetForegroundWindow() != Handle || !approval.Focused) { r.Pause("ナビの承認欄を選び、案内を再開してください。"); return; }
          }
          if (!practiceRun && (Native.GetForegroundWindow() != Handle || !approval.Focused)) {
            r.Pause("ナビの承認欄を選び、案内を再開してください。"); return;
          }
          arrow.Aim(p.Rect); action.Update(); inputPreview.Update(); approval.Update(); arrow.Update();
          presentedId = p.Id;
        } else arrow.Aim(p.Rect);
      } else { presentedId = null; arrow.Hide(); if (r.State == "done" || r.State == "stopped") inputPreview.Clear(); }
      start.Enabled = !starting && !r.Active && !r.ActionInFlight; skills.Enabled = start.Enabled; start.Text = r.Task == null ? "練習アプリを開いて案内を開始" : "練習を最初からやり直す";
      pause.Enabled = r.Active || r.State == "paused"; pause.Text = r.State == "paused" || r.State == "waiting" ? "案内を再開" : "一時停止";
      stop.Enabled = r.Active || r.State == "paused" || r.State == "unknown";
      var last = r.Timings.LastOrDefault(t => t.Outcome == "verified");
      latency.Text = last == null ? "APIを使わずに試せます。" : "Enter → 次の提示: " + last.ApprovalToReadyMs + " ms（前回）";
      if (r.State == "done") { ReleaseHookWhenKeyUp(); SaveMetrics(); }
      if (r.State == "unknown") SaveMetrics();
    }
    void Hook() {
      enter.Reset(); if ((Native.GetAsyncKeyState(13) & 0x8000) != 0) { bool ignored; enter.Down(false, false, out ignored); }
      keyCallback = KeyboardHook; keyHook = Native.SetWindowsHookEx(13, keyCallback, Native.GetModuleHandle(null), 0);
      if (keyHook == IntPtr.Zero) throw new InvalidOperationException("Enterの受付を開始できません。");
      interventionCallback = InterventionHook; interventionHook = Native.SetWindowsHookEx(14, interventionCallback, Native.GetModuleHandle(null), 0);
      if (interventionHook == IntPtr.Zero) { Unhook(); throw new InvalidOperationException("利用者の割り込み操作を監視できません。"); }
    }
    void Unhook() {
      if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
      if (interventionHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(interventionHook); interventionHook = IntPtr.Zero; }
    }
    void ReleaseHookWhenKeyUp() { if (!enter.Held) Unhook(); }
    void HumanIntervention() {
      if (practiceRun || runner == null || !runner.Active) return;
      manualIntervention = true; returnApprovalFocus = false;
      runner.Pause("利用者の入力・操作を検知しました。現在画面を確認してから案内を再開してください。");
    }
    IntPtr InterventionHook(int code, IntPtr w, IntPtr l) {
      if (code >= 0 && !practiceRun && runner != null && runner.Active) {
        var mouse = (Native.Mouse)Marshal.PtrToStructure(l, typeof(Native.Mouse));
        int message = w.ToInt32();
        if (GuideInputPolicy.PointerInterrupts(message, (mouse.Flags & 1) != 0, false)) {
          var window = Native.GetAncestor(Native.WindowFromPoint(mouse.P), 2);
          if (window != Handle) HumanIntervention();
        }
      }
      return Native.CallNextHookEx(interventionHook, code, w, l);
    }
    IntPtr KeyboardHook(int code, IntPtr w, IntPtr l) {
      if (code >= 0) {
        var key = (Native.Keyboard)Marshal.PtrToStructure(l, typeof(Native.Keyboard)); int kind = w.ToInt32();
        bool down = kind == 0x100 || kind == 0x104, up = kind == 0x101 || kind == 0x105;
        bool injected = (key.Flags & 0x10) != 0;
        bool scope = session != null && session.ForegroundInScope();
        bool ownApproval = Native.GetForegroundWindow() == Handle && approval.Focused;
        bool capture = runner != null && (runner.Active || runner.State == "unknown") && scope &&
          (practiceRun || ownApproval) && !Native.ModifierPressed() && !Native.EditingOrComposing();
        if (key.Key == 13 && (down || up)) {
          bool approve;
          bool ready = runner != null && runner.State == "ready" && runner.Pending != null && presentedId == runner.Pending.Id;
          bool suppress = enter.Process(down, injected, capture, ready, out approve);
          if (!injected && up && (runner == null || runner.State == "done" || runner.State == "stopped")) Unhook();
          if (approve) {
            var approvedRunner = runner; var p = approvedRunner.TakeApproval();
            if (p != null) {
              manualIntervention = false; returnApprovalFocus = true;
              BeginInvoke((Action)(async () => await approvedRunner.ExecuteApprovedAsync(p)));
            }
          }
          if (suppress) return new IntPtr(1);
        }
        if (!injected && key.Key == 27 && down && capture) {
          if (runner.State == "unknown") { runner.Stop(); ReleaseHookWhenKeyUp(); }
          else runner.Pause("Escで一時停止しました。");
          return new IntPtr(1);
        }
        bool approvalPending = runner != null && (runner.ActionInFlight || runner.State == "executing" || runner.State == "checking" || runner.State == "verifying");
        if (GuideInputPolicy.KeyboardInterrupts(down, injected, (int)key.Key, Native.GetForegroundWindow() == Handle, approvalPending)) HumanIntervention();
      }
      return Native.CallNextHookEx(keyHook, code, w, l);
    }
    void SaveMetrics() {
      if (saved || runner == null || runner.ActionInFlight || runner.Timings.Count == 0) return;
      try {
        var dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "data", "windows", "runs")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        var result = new { kind = practiceRun ? "local-practice-timing" : "local-registered-app-timing", state = runner.State, stepsCompleted = runner.StepIndex, actions = runner.ExecutedCount, timings = runner.Timings.ToArray(), apiCalls = 0, captures = 0 };
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream)) writer.Write(new JavaScriptSerializer().Serialize(result)); saved = true;
      } catch { latency.Text = "時間計測の保存に失敗しました。案内の状態は保持しています。"; }
    }
  }
}
