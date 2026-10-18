using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JevTeachWindows {
  // The native authoring path deliberately uses reviewed, observed semantics. It does
  // not interpret free-form speech as an executable plan and never records input values.
  sealed class SkillWorkspace : Form {
    readonly DesktopSession session;
    readonly NativeSkillStore store;
    readonly WindowsDesktop desktop;
    readonly Label status = new Label(), selectedLabel = new Label();
    readonly ComboBox windows = new ComboBox(), before = new ComboBox(), after = new ComboBox(), resultApp = new ComboBox(), kind = new ComboBox();
    readonly TextBox alias = new TextBox(), skillName = new TextBox(), explanation = new TextBox(), parameter = new TextBox(), request = new TextBox(), editor = new TextBox();
    readonly ListBox registered = new ListBox(), lessons = new ListBox(), library = new ListBox();
    readonly CheckBox resume = new CheckBox(), reviewed = new CheckBox();
    readonly Button begin = new Button(), finish = new Button();
    readonly Overlay highlight = new Overlay();
    readonly System.Windows.Forms.Timer monitor = new System.Windows.Forms.Timer();
    readonly List<NativeSkillStep> recorded = new List<NativeSkillStep>();
    CancellationTokenSource recording;
    Native.Hook hook;
    IntPtr mouseHook;
    Native.Point down;
    string downApp, selectedApp;
    bool rightCaptured, busy, manualActivity, closed, closeAfterRelease, closeApproved, editorMustSave;
    string originalEditor = "";
    int generation;
    DesktopNode selected;
    DesktopView beforeView, afterView;
    string afterApp;
    NativeSkillDocument loaded;
    public DesktopTask SelectedTask { get; private set; }
    public string SelectedSkillId { get; private set; }
    bool Teaching { get { return recording != null && !recording.IsCancellationRequested; } }
    bool HasUnsavedWork { get { return recorded.Count > 0 || editorMustSave || editor.Text != originalEditor; } }
    sealed class Cue {
      public DesktopNode Node;
      public override string ToString() { return Node.Role + " · " + Node.Name + (String.IsNullOrEmpty(Node.AutomationId) ? "" : " [" + Node.AutomationId + "]") + (String.IsNullOrEmpty(Node.Context) ? "" : " / " + Node.Context); }
    }
    public SkillWorkspace(DesktopSession session, NativeSkillStore store) {
      this.session = session; this.store = store; desktop = new WindowsDesktop(session, false);
      Text = "アプリ登録・教える・保存スキル"; Width = 800; Height = 900; MinimumSize = new Size(620, 620);
      Font = new Font("Yu Gothic UI", 10); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
      status.Dock = DockStyle.Top; status.Height = 70; status.Padding = new Padding(12); status.BackColor = Color.FromArgb(235, 244, 239);
      var tabs = new TabControl { Dock = DockStyle.Fill }; Controls.Add(tabs); Controls.Add(status);
      var appPage = Page(tabs, "1 アプリを登録");
      Add(appPage, new Label { Text = "操作するウィンドウを選び、スキルで使うアプリ名を付けます。登録は今回の起動中だけ有効です。新しいダイアログは別に登録します。", Height = 56 });
      Add(appPage, Button("ウィンドウ一覧を更新", RefreshWindows));
      windows.DropDownStyle = ComboBoxStyle.DropDownList; Add(appPage, windows);
      Add(appPage, new Label { Text = "アプリ名（例: editor / calculator。次回も同じ名前で登録）", Height = 28 }); alias.Text = "app1"; Add(appPage, alias);
      Add(appPage, Button("選んだウィンドウだけを登録", () => { RequireStopped(); session.Register(alias.Text.Trim(), windows.SelectedItem as WindowChoice); UpdateApps(); SetStatus("操作対象を明示登録しました。"); }));
      registered.Height = 180; Add(appPage, registered);
      Add(appPage, Button("選んだ登録を解除", () => { RequireStopped(); var app = registered.SelectedItem as RegisteredApp; if (app != null) session.Remove(app.Key); UpdateApps(); }));
      Add(appPage, new Label { Text = "登録した一般アプリは、ナビ終了時も閉じません。アプリの再起動・ウィンドウの作り直し後は再登録してください。", Height = 50 });
      var teachPage = Page(tabs, "2 教える");
      Add(teachPage, new Label { Text = "開始 → 対象を右クリック → 操作と開始画面の目印を確認 → 普段どおり操作 → 結果の目印を観察 → 手順を追加。右クリック自体は実行しません。", Height = 58 });
      begin.Text = "教えるモードを開始"; begin.Height = 36; begin.Click += (s,e) => Guard(StartTeaching); Add(teachPage, begin);
      finish.Text = "記録を停止"; finish.Height = 36; finish.Enabled = false; finish.Click += (s,e) => StopTeaching("記録を停止しました。未保存の手順はこの画面に保持しています。"); Add(teachPage, finish);
      selectedLabel.Text = "対象未指定。画面の説明・目印は一覧から選べます。"; selectedLabel.Height = 46; Add(teachPage, selectedLabel);
      kind.DropDownStyle = ComboBoxStyle.DropDownList; kind.Items.AddRange(new object[] { "invoke", "set-value" }); kind.SelectedIndex = 0;
      Add(teachPage, new Label { Text = "操作（invoke = 1回実行、set-value = 入力欄の全文を設定）", Height = 26 }); Add(teachPage, kind);
      Add(teachPage, new Label { Text = "操作の意味（例: 新しい申請を開く）", Height = 26 }); Add(teachPage, explanation);
      Add(teachPage, new Label { Text = "開始画面の目印（クリック不要・現在のUIA候補から選択）", Height = 26 }); before.DropDownStyle = ComboBoxStyle.DropDownList; Add(teachPage, before);
      Add(teachPage, new Label { Text = "入力の場合のパラメータ名（入力する実際の文章は保存しません）", Height = 26 }); parameter.Text = "text"; Add(teachPage, parameter);
      Add(teachPage, new Label { Text = "操作後、結果が表示される登録アプリ", Height = 26 }); resultApp.DropDownStyle = ComboBoxStyle.DropDownList; Add(teachPage, resultApp);
      resultApp.SelectedIndexChanged += (s,e) => { afterView = null; afterApp = null; after.Items.Clear(); };
      Add(teachPage, AsyncButton("普段どおり操作した後、現在の結果候補を観察", ObserveResult));
      after.DropDownStyle = ComboBoxStyle.DropDownList; Add(teachPage, after);
      resume.Text = "この結果が既に見えれば完了として開始（入力は今回の全文との一致）"; resume.Height = 42; Add(teachPage, resume);
      Add(teachPage, Button("意味と結果を確認して1手順を追加", AddLesson));
      lessons.Height = 150; Add(teachPage, lessons);
      Add(teachPage, Button("最後の未保存手順を取り除く", () => { RequireStopped(); if (recorded.Count > 0) { recorded.RemoveAt(recorded.Count - 1); lessons.Items.RemoveAt(lessons.Items.Count - 1); } }));
      Add(teachPage, new Label { Text = "スキル名 / 目的", Height = 26 }); skillName.Text = "いつもの操作"; Add(teachPage, skillName);
      Add(teachPage, Button("記録を停止してスキルの下書きを作る", () => {
        StopTeaching("記録を停止しました。"); var draft = NativeTeachingCompiler.Compile(skillName.Text, skillName.Text, recorded);
        if (!ConfirmEditorReplacement(false)) return;
        ShowEditor(draft, true); library.ClearSelected(); tabs.SelectedIndex = 2;
        SetStatus("下書きです。全手順・アプリ名・最後の完了条件を確認して保存してください。");
      }));
      var libraryPage = Page(tabs, "3 保存・選択・案内");
      Add(libraryPage, Button("保存スキルを読み直す", RefreshLibrary)); library.Height = 130; Add(libraryPage, library);
      Add(libraryPage, Button("選択したスキルを閲覧・編集", () => {
        RequireStopped(); var selectedDoc = library.SelectedItem as NativeSkillDocument; if (selectedDoc == null) return;
        var doc = store.Load(selectedDoc.Id); if (!ConfirmEditorReplacement(true)) return; ShowEditor(doc, false);
      }));
      Add(libraryPage, Button("旧版を開く", () => {
        RequireStopped(); var selectedDoc = library.SelectedItem as NativeSkillDocument; if (selectedDoc == null) return;
        using (var dialog = new RevisionDialog(store.ListVersions(selectedDoc.Id))) {
          if (dialog.ShowDialog(this) != DialogResult.OK) return;
          var historical = store.Load(selectedDoc.Id, dialog.Version); historical.Version = store.Load(selectedDoc.Id).Version;
          if (!ConfirmEditorReplacement(true)) return;
          ShowEditor(historical, true); library.ClearSelected();
          SetStatus("旧版を編集用の下書きとして開きました。保存すると現在版の次に追加します。保存前は案内できません。");
        }
      }));
      editor.Multiline = true; editor.ScrollBars = ScrollBars.Both; editor.WordWrap = false; editor.Height = 240; editor.MaxLength = NativeSkillFormat.MaxJsonBytes; Add(libraryPage, editor);
      editor.TextChanged += (s,e) => reviewed.Checked = false;
      reviewed.Text = "全手順と入力パラメータ、最後の結果を完了条件とすることを確認した"; reviewed.Height = 44; Add(libraryPage, reviewed);
      Add(libraryPage, Button("新しい版として保存", SaveSkill));
      Add(libraryPage, Button("旧Webスキル・教示証拠を下書きとして取り込む", () => {
        RequireStopped(); using (var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true }) {
          if (dialog.ShowDialog(this) != DialogResult.OK) return;
          if (new FileInfo(dialog.FileName).Length > NativeSkillFormat.MaxJsonBytes) throw new InvalidOperationException("取り込むJSONが大きすぎます。");
          var draft = NativeSkillStore.ImportLegacyForReview(File.ReadAllText(dialog.FileName));
          if (!ConfirmEditorReplacement(true)) return;
          ShowEditor(draft, true); library.ClearSelected();
          SetStatus("元ファイルを変更せず下書きに取り込みました。Windowsの対象と前後画面を教え直すまで実行できません。");
        }
      }));
      Add(libraryPage, new Label { Text = "やりたいこと（保存した名前・依頼例の語句で選びます）", Height = 26 }); Add(libraryPage, request);
      Add(libraryPage, Button("依頼文からスキルを選ぶ", () => {
        RequireStopped(); var selection = NativeSkillSelector.Select(request.Text, store.List()); SetStatus(selection.Message);
        if (selection.State == "selected") {
          if (!ConfirmEditorReplacement(true)) return;
          ShowEditor(selection.Skill, false); library.SelectedItem = library.Items.Cast<NativeSkillDocument>().FirstOrDefault(d => d.Id == loaded.Id);
        } else library.ClearSelected();
      }));
      Add(libraryPage, Button("選択した保存スキルで案内を始める", SelectForGuidance));
      Add(libraryPage, new Label { Text = "入力する全文は実行時に別欄で指定し、案内パネルでも全文を表示します。入力と送信は別々のEnterです。画像API・音声認識はこの経路では使いません。", Height = 60 });
      Shown += (s,e) => { session.AddNavigator(Handle); Guard(RefreshWindows); Guard(RefreshLibrary); UpdateApps(); SetStatus("停止中です。まず操作するアプリを登録してください。"); };
      monitor.Interval = 400; monitor.Tick += (s,e) => { if (Teaching && !session.ForegroundInScope()) StopTeaching("未登録のアプリへ移ったため記録を停止しました。必要ならウィンドウを登録して再開してください。"); }; monitor.Start();
      FormClosing += (s,e) => {
        StopTeaching("記録を停止しました。");
        if (!closeApproved && HasUnsavedWork && SelectedTask == null && MessageBox.Show(this, "未保存の教示手順・下書き・編集があります。破棄してこの画面を閉じますか？", "未保存の内容", MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
        closeApproved = true;
        if (rightCaptured) { closeAfterRelease = true; e.Cancel = true; return; }
        UnhookMouse(); closed = true; monitor.Stop(); session.RemoveNavigator(Handle); desktop.Dispose(); highlight.Dispose();
      };
    }
    FlowLayoutPanel Page(TabControl tabs, string text) {
      var page = new TabPage(text); tabs.TabPages.Add(page);
      var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) }; page.Controls.Add(flow);
      flow.SizeChanged += (s,e) => { foreach (Control child in flow.Controls) child.Width = Math.Max(300, flow.ClientSize.Width - 42); };
      return flow;
    }
    void Add(FlowLayoutPanel flow, Control child) { child.Width = 710; if (child.Height < 26) child.Height = 28; child.Margin = new Padding(0, 4, 0, 4); flow.Controls.Add(child); }
    Button Button(string text, Action action) { var button = new Button { Text = text, Height = 36 }; button.Click += (s,e) => Guard(action); return button; }
    Button AsyncButton(string text, Func<Task> action) { var button = new Button { Text = text, Height = 36 }; button.Click += async (s,e) => { try { await action(); } catch (Exception ex) { SetStatus(ex.Message); } }; return button; }
    void Guard(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message); } }
    void SetStatus(string text) { if (!closed) status.Text = (Teaching ? "● 教示中" : "■ 記録停止中") + " · マイク停止 · 外部送信なし\r\n" + text; }
    void RequireStopped() { if (Teaching || busy) throw new InvalidOperationException("先に記録を停止し、観察の終了を待ってください。"); }
    void ShowEditor(NativeSkillDocument document, bool mustSave) {
      loaded = document; editor.Text = NativeSkillStore.SerializeForReview(document); originalEditor = editor.Text;
      editorMustSave = mustSave; reviewed.Checked = false;
    }
    bool ConfirmEditorReplacement(bool includeRecorded) {
      bool pending = editorMustSave || editor.Text != originalEditor || (includeRecorded && recorded.Count > 0);
      if (pending && MessageBox.Show(this, "未保存の内容を破棄して、指定したスキルを開きますか？", "未保存の内容", MessageBoxButtons.YesNo) != DialogResult.Yes) return false;
      if (includeRecorded) { recorded.Clear(); lessons.Items.Clear(); }
      return true;
    }
    void RefreshWindows() { RequireStopped(); windows.Items.Clear(); foreach (var window in DesktopSession.AvailableWindows()) windows.Items.Add(window); if (windows.Items.Count > 0) windows.SelectedIndex = 0; }
    void UpdateApps() { registered.Items.Clear(); resultApp.Items.Clear(); foreach (var app in session.Apps) { registered.Items.Add(app); resultApp.Items.Add(app.Key); } if (resultApp.Items.Count > 0) resultApp.SelectedIndex = 0; }
    void RefreshLibrary() { RequireStopped(); var docs = store.List(); library.Items.Clear(); foreach (var doc in docs) library.Items.Add(doc); }
    void StartTeaching() {
      RequireStopped(); if (rightCaptured) throw new InvalidOperationException("右ボタンを離してから開始してください。"); if (session.Apps.Length == 0) throw new InvalidOperationException("アプリを明示登録してください。");
      recording = new CancellationTokenSource(); generation++; selected = null; beforeView = null; afterView = null; manualActivity = false;
      hook = MouseHook; mouseHook = Native.SetWindowsHookEx(14, hook, Native.GetModuleHandle(null), 0);
      if (mouseHook == IntPtr.Zero) { StopTeaching("教示フックを開始できません。"); return; }
      begin.Enabled = false; finish.Enabled = true; SetStatus("登録したアプリで教える対象を右クリックしてください。左クリックは普段どおり通します。");
    }
    void StopTeaching(string text) {
      generation++; var old = recording; recording = null; if (old != null) { old.Cancel(); old.Dispose(); }
      // Suppressed right-down must consume its matching up; the hook retires then.
      if (!rightCaptured) UnhookMouse(); highlight.Hide(); begin.Enabled = true; finish.Enabled = false; SetStatus(text);
    }
    void UnhookMouse() { if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; } }
    async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancel) {
      using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
        var job = work(deadline.Token);
        var done = await Task.WhenAny(job, Task.Delay(3500, cancel));
        if (done != job) { deadline.Cancel(); cancel.ThrowIfCancellationRequested(); throw new TimeoutException("UIAの応答がありません。記録を停止し、対象とナビを確認してください。"); }
        return await job;
      }
    }
    IntPtr MouseHook(int code, IntPtr w, IntPtr l) {
      if (code < 0) return Native.CallNextHookEx(mouseHook, code, w, l);
      var data = (Native.Mouse)Marshal.PtrToStructure(l, typeof(Native.Mouse)); int message = w.ToInt32();
      // Synthetic pointer events are not evidence of human teaching.
      if ((data.Flags & 1) != 0) return Native.CallNextHookEx(mouseHook, code, w, l);
      if (message == 0x205 && rightCaptured) {
        rightCaptured = false; var from = down; string app = downApp; var to = data.P; int g = generation;
        if (!Teaching) { UnhookMouse(); if (closeAfterRelease) BeginInvoke((Action)Close); return new IntPtr(1); }
        if (session.ForegroundKey() == app) BeginInvoke((Action)(async () => await SelectTarget(app, from, to, g)));
        return new IntPtr(1);
      }
      if (!Teaching) return Native.CallNextHookEx(mouseHook, code, w, l);
      string foreground = session.ForegroundKey(); var current = session.Get(foreground);
      bool inside = current != null && Native.GetAncestor(Native.WindowFromPoint(data.P), 2) == current.Handle;
      if (message == 0x204 && inside) { rightCaptured = true; down = data.P; downApp = foreground; return new IntPtr(1); }
      if (message == 0x202 && inside && selected != null && foreground == selectedApp && selected.Rect.Contains(data.P.X, data.P.Y)) manualActivity = true;
      return Native.CallNextHookEx(mouseHook, code, w, l);
    }
    static bool SameSemantic(DesktopNode a, DesktopNode b) { return a.Name == b.Name && a.Role == b.Role && a.Context == b.Context && a.AutomationId == b.AutomationId; }
    static IEnumerable<Cue> Cues(DesktopView view) {
      return view.Nodes.Where(n => n.Visible && !String.IsNullOrWhiteSpace(n.Name) && n.Role != "Edit" && !n.Settable &&
        view.Nodes.Count(m => m.Visible && SameSemantic(m, n)) == 1).Select(n => new Cue { Node = n });
    }
    async Task SelectTarget(string app, Native.Point from, Native.Point to, int g) {
      if (!Teaching || busy || g != generation) return;
      if (Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) > 8) {
        selected = null; beforeView = null; afterView = null;
        highlight.Aim(Rectangle.FromLTRB(Math.Min(to.X, from.X), Math.Min(to.Y, from.Y), Math.Max(to.X, from.X), Math.Max(to.Y, from.Y)), false);
        selectedLabel.Text = "参考領域を指定しました。範囲は実行先に保存しません。操作対象は別途右クリックしてください。"; return;
      }
      busy = true;
      try {
        var cancel = recording.Token;
        var node = await Bounded(token => desktop.AtAsync(app, to, token), cancel);
        var view = await Bounded(token => desktop.ObserveAsync(app, token), cancel);
        if (!Teaching || g != generation) return;
        if (node == null || (!node.Invokable && !node.Settable)) throw new InvalidOperationException("対象にUIAのInvoke/Value操作がありません。画像での再特定が必要な対象は、この経路では保留します。");
        var matches = view.Nodes.Where(n => n.Runtime == node.Runtime && n.Stamp == node.Stamp).ToList();
        if (matches.Count != 1 || view.Nodes.Count(n => n.Visible && n.Enabled && SameSemantic(n, node)) != 1) throw new InvalidOperationException("対象が変わったか、同じ意味の対象が複数あります。");
        resume.Checked = false; selected = node; selectedApp = app; beforeView = view; afterView = null; manualActivity = false;
        before.Items.Clear(); after.Items.Clear(); foreach (var cue in Cues(view)) before.Items.Add(cue);
        if (before.Items.Count > 0) before.SelectedIndex = 0;
        resultApp.SelectedItem = app; kind.SelectedItem = node.Settable ? "set-value" : "invoke"; if (node.Settable) parameter.Text = "p" + (recorded.Count + 1); explanation.Text = "「" + node.Name + "」" + (node.Settable ? "に入力する" : "を実行する");
        selectedLabel.Text = app + " · " + node.Role + " · " + node.Name + "（実行していません）"; highlight.Aim(node.Rect, false);
        SetStatus("開始画面の目印を確認し、普段どおり操作してください。その後、結果候補を観察します。");
      } catch (OperationCanceledException) { }
      catch (Exception ex) { StopTeaching(ex.Message); }
      finally { busy = false; }
    }
    async Task ObserveResult() {
      if (!Teaching || busy || selected == null) throw new InvalidOperationException("教えるモードで操作対象を右クリックしてください。");
      if (!manualActivity) throw new InvalidOperationException("選んだ対象を普段どおり左クリックして操作してください。入力は欄をクリックしてから入力します。");
      string app = resultApp.SelectedItem as string; if (app == null) throw new InvalidOperationException("結果を見る登録アプリを選んでください。");
      int g = generation; busy = true;
      try {
        var view = await Bounded(token => desktop.ObserveAsync(app, token), recording.Token);
        if (!Teaching || g != generation) return;
        if (!view.Exists || view.Minimized) throw new InvalidOperationException("結果のウィンドウを表示してください。");
        afterView = view; afterApp = app; after.Items.Clear(); foreach (var cue in Cues(view)) after.Items.Add(cue);
        if (after.Items.Count > 0) after.SelectedIndex = 0;
        highlight.Hide(); SetStatus("操作後の候補を観察しました。成功を示す目印を選び、意味と結果を確認して追加してください。");
      } finally { busy = false; }
    }
    void AddLesson() {
      if (!Teaching || busy || selected == null || beforeView == null || afterView == null || !manualActivity)
        throw new InvalidOperationException("対象の指定・普段どおりの操作・結果の観察が必要です。");
      var prior = before.SelectedItem as Cue; var result = after.SelectedItem as Cue; string operation = kind.SelectedItem as string;
      if (prior == null || SameSemantic(prior.Node, selected)) throw new InvalidOperationException("開始画面の目印は操作対象と別の要素を選んでください。");
      if (operation == "invoke" && (result == null || (afterApp == selectedApp && beforeView.Nodes.Any(n => n.Visible && SameSemantic(n, result.Node)))))
        throw new InvalidOperationException("操作後に新しく現れた一意の結果を選んでください。操作前からある目印では成功と判定できません。");
      if (operation == "invoke" && !selected.Invokable || operation == "set-value" && !selected.Settable) throw new InvalidOperationException("対象がその操作に対応していません。");
      var step = new NativeSkillStep { App = selectedApp, Target = selected.Name, Role = selected.Role, Context = selected.Context, AutomationId = selected.AutomationId,
        Kind = operation, Description = Uia.Clean(explanation.Text), Precondition = prior.Node.Name, PreconditionRole = prior.Node.Role,
        PreconditionContext = prior.Node.Context, PreconditionAutomationId = prior.Node.AutomationId, AllowResume = resume.Checked };
      if (operation == "set-value") { step.InputParameter = parameter.Text.Trim(); }
      else { step.Result = result.Node.Name; step.ResultApp = afterApp; step.ResultRole = result.Node.Role; step.ResultContext = result.Node.Context; step.ResultAutomationId = result.Node.AutomationId; }
      // Validate the semantic projection before retaining it. No Rect/Runtime/Value passes this boundary.
      NativeTeachingCompiler.Compile("検証", "検証", new[] { step });
      recorded.Add(step); lessons.Items.Add(recorded.Count + ". " + step.Description + " → " + (operation == "set-value" ? "承認した全文と一致" : step.Result));
      selected = null; beforeView = null; afterView = null; manualActivity = false; highlight.Hide(); selectedLabel.Text = "次の対象を右クリックするか、停止して保存してください。";
      SetStatus("意味と結果の1手順を追加しました。入力実値は保存していません。");
    }
    void SaveSkill() {
      RequireStopped(); var doc = NativeSkillStore.Parse(editor.Text);
      doc.Reviewed = reviewed.Checked; var saved = store.Save(doc); loaded = saved;
      ShowEditor(saved, false); reviewed.Checked = saved.Reviewed;
      recorded.Clear(); lessons.Items.Clear(); RefreshLibrary();
      library.SelectedItem = library.Items.Cast<NativeSkillDocument>().FirstOrDefault(d => d.Id == saved.Id);
      SetStatus(saved.Ready ? "v" + saved.Version + "を保存しました。依頼文または一覧から選択して案内を開始できます。" : "下書きを保存しました: " + String.Join(" / ", saved.ReviewIssues));
    }
    void SelectForGuidance() {
      RequireStopped(); var selectedSkill = library.SelectedItem as NativeSkillDocument;
      if (selectedSkill == null) throw new InvalidOperationException("保存済みのスキルを選んでください。");
      var doc = NativeSkillLaunchReview.RequireCurrent(store, selectedSkill.Id, editor.Text, HasUnsavedWork);
      foreach (var app in doc.UsedApps) if (session.Get(app) == null) throw new InvalidOperationException("アプリ「" + app + "」の現在のウィンドウを登録してください。");
      var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
      if (doc.Parameters.Count > 0) using (var input = new SkillInputDialog(doc.Parameters)) { if (input.ShowDialog(this) != DialogResult.OK) return; inputs = input.Values; }
      SelectedTask = doc.ToDesktopTask(inputs); SelectedSkillId = doc.Id; DialogResult = DialogResult.OK; Close();
    }
  }

  sealed class SkillInputDialog : Form {
    readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
    readonly Dictionary<string, CheckBox> clearFields = new Dictionary<string, CheckBox>();
    public Dictionary<string, string> Values { get; private set; }
    public SkillInputDialog(IEnumerable<string> parameters) {
      Text = "今回入力する全文"; Width = 620; Height = 580; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
      var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) }; Controls.Add(flow);
      flow.Controls.Add(new Label { Text = "ここで指定する実値はスキルに保存しません。案内パネルでも全文を確認し、入力の1操作をEnterで承認します。", Width = 560, Height = 52 });
      foreach (var parameter in parameters) {
        flow.Controls.Add(new Label { Text = parameter, Width = 550, Height = 25 });
        var field = new TextBox { Width = 550, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 16000 };
        fields.Add(parameter, field); flow.Controls.Add(field);
        var clear = new CheckBox { Text = "空文字で欄を消去する（上の内容は使いません）", Width = 550, Height = 32 };
        clear.CheckedChanged += (s,e) => field.Enabled = !clear.Checked;
        clearFields.Add(parameter, clear); flow.Controls.Add(clear);
      }
      var button = new Button { Text = "この全文を案内の入力案に使う", Width = 550, Height = 42 }; flow.Controls.Add(button);
      button.Click += (s,e) => {
        if (fields.Any(p => p.Value.Text.Length == 0 && !clearFields[p.Key].Checked)) {
          MessageBox.Show(this, "入力内容を指定してください。空にする場合は、その欄の『空文字で欄を消去する』を明示選択してください。"); return;
        }
        // Whitespace and newlines are intentional input too. Only the explicit
        // clearing option produces an empty string; never trim or auto-clear.
        Values = fields.ToDictionary(p => p.Key, p => clearFields[p.Key].Checked ? "" : p.Value.Text, StringComparer.Ordinal);
        DialogResult = DialogResult.OK; Close();
      };
      // No AcceptButton: Enter in a field belongs to text/IME, never to approval.
    }
  }
  sealed class RevisionDialog : Form {
    readonly ListBox versions = new ListBox(); public int Version { get; private set; }
    public RevisionDialog(IEnumerable<int> values) {
      Text = "旧版を開く"; Width = 300; Height = 340; StartPosition = FormStartPosition.CenterParent;
      versions.Dock = DockStyle.Fill; foreach (int value in values) versions.Items.Add(value); Controls.Add(versions);
      var button = new Button { Dock = DockStyle.Bottom, Height = 42, Text = "この版を閲覧・編集する" }; Controls.Add(button);
      button.Click += (s,e) => { if (versions.SelectedItem == null) return; Version = (int)versions.SelectedItem; DialogResult = DialogResult.OK; Close(); };
    }
  }
}
