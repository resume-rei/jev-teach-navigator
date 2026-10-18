using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace JevTeachWindows {
  static partial class Native {
    [StructLayout(LayoutKind.Sequential)] public struct WinRect { public int Left, Top, Right, Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
    [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct GuiThreadInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public WinRect CaretRect; }
    public delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
    public delegate void WinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out WinRect rect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr h);
    [DllImport("imm32.dll")] public static extern int ImmGetCompositionStringW(IntPtr context, int index, IntPtr buffer, int count);
    [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr h, IntPtr context);
    public static bool EditingOrComposing() {
      var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
      if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return true;
      var name = new StringBuilder(256); GetClassName(info.Focus, name, name.Capacity);
      if (name.ToString().IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      var context = ImmGetContext(info.Focus); if (context == IntPtr.Zero) return false;
      try { return ImmGetCompositionStringW(context, 8, IntPtr.Zero, 0) > 0; }
      finally { ImmReleaseContext(info.Focus, context); }
    }
    public static bool ModifierPressed() { return new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); }
  }

  // Registration is explicit, session-scoped and bound to this exact window lifetime.
  // MainWindowHandle is never followed after registration, including a dialog/relaunch.
  sealed class RegisteredApp {
    public string Key, Title;
    public Process Process;
    public int Pid;
    public long StartTicks;
    public IntPtr Handle;
    public bool OwnedPractice;
    int destroyed;
    public void Invalidate() { Interlocked.Exchange(ref destroyed, 1); }
    readonly string registration = Guid.NewGuid().ToString("N");
    public bool Refresh() {
      try {
        Process.Refresh(); int pid;
        return Volatile.Read(ref destroyed) == 0 && !Process.HasExited && Process.StartTime.ToUniversalTime().Ticks == StartTicks &&
          Handle != IntPtr.Zero && Native.IsWindow(Handle) && Native.GetWindowThreadProcessId(Handle, out pid) != 0 && pid == Pid;
      } catch { return false; }
    }
    public string Identity { get { return registration + ":" + Pid + ":" + StartTicks + ":" + Handle.ToInt64(); } }
    public override string ToString() { return Key + " · " + Title + (Refresh() ? "" : "（失効・再登録が必要）"); }
  }

  class DesktopSession : IDisposable {
    readonly Dictionary<string, RegisteredApp> apps = new Dictionary<string, RegisteredApp>();
    readonly HashSet<IntPtr> navigatorWindows = new HashSet<IntPtr>();
    readonly object sync = new object();
    readonly Native.WinEvent destroyCallback;
    IntPtr destroyHook;
    public DesktopSession(IntPtr navigator) {
      navigatorWindows.Add(navigator);
      destroyCallback = (hook, kind, window, objectId, childId, thread, time) => {
        if (objectId != 0 || childId != 0) return;
        lock (sync) foreach (var app in apps.Values) if (app.Handle == window) app.Invalidate();
      };
      destroyHook = Native.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, destroyCallback, 0, 0, 2);
      if (destroyHook == IntPtr.Zero) throw new InvalidOperationException("ウィンドウの終了監視を開始できません。登録を保留します。");
    }
    public void AddNavigator(IntPtr handle) { lock (sync) navigatorWindows.Add(handle); }
    public void RemoveNavigator(IntPtr handle) { lock (sync) navigatorWindows.Remove(handle); }
    public bool IsNavigator(IntPtr handle) { lock (sync) return navigatorWindows.Contains(handle); }
    public RegisteredApp[] Apps { get { lock (sync) return apps.Values.ToArray(); } }
    public bool HasExternalApps { get { return Apps.Any(a => !a.OwnedPractice); } }
    public static List<WindowChoice> AvailableWindows() {
      var choices = new List<WindowChoice>(); int ownPid = Process.GetCurrentProcess().Id;
      Native.EnumWindow callback = (h, p) => {
        int pid; Native.GetWindowThreadProcessId(h, out pid);
        if (pid == ownPid || !Native.IsWindowVisible(h)) return true;
        var title = new StringBuilder(512); Native.GetWindowText(h, title, title.Capacity);
        if (title.Length > 0) choices.Add(new WindowChoice { Handle = h, Pid = pid, Name = title.ToString() });
        return true;
      };
      Native.EnumWindows(callback, IntPtr.Zero); return choices;
    }
    public RegisteredApp Register(string key, WindowChoice choice) {
      if (choice == null || !Regex.IsMatch(key ?? "", @"^[A-Za-z][A-Za-z0-9_-]{0,63}$") || String.Equals(key, "navigator", StringComparison.OrdinalIgnoreCase) || String.Equals(key, "outside", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("アプリ名は英字で始まる64文字以内の英数字・_・-にしてください。");
      lock (sync) {
        if (apps.ContainsKey(key)) throw new InvalidOperationException("同じアプリ名が登録済みです。解除してから登録し直してください。");
        if (apps.Values.Any(a => a.Handle == choice.Handle)) throw new InvalidOperationException("このウィンドウは登録済みです。");
        var process = Process.GetProcessById(choice.Pid);
        var app = new RegisteredApp { Key = key, Title = choice.Name, Process = process, Pid = choice.Pid, Handle = choice.Handle,
          StartTicks = process.StartTime.ToUniversalTime().Ticks };
        if (!app.Refresh() || process.Id == Process.GetCurrentProcess().Id) { process.Dispose(); throw new InvalidOperationException("選んだウィンドウが失効しました。一覧を更新してください。"); }
        apps.Add(key, app); return app;
      }
    }
    protected void RegisterPractice(string key, string title, string argument) {
      var process = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, argument) { UseShellExecute = false });
      if (process == null) throw new InvalidOperationException("練習アプリを起動できませんでした。");
      var watch = Stopwatch.StartNew(); IntPtr handle = IntPtr.Zero;
      while (watch.ElapsedMilliseconds < 5000) { process.Refresh(); handle = process.MainWindowHandle; if (handle != IntPtr.Zero || process.HasExited) break; Thread.Sleep(40); }
      if (handle == IntPtr.Zero) { process.Dispose(); throw new InvalidOperationException("練習アプリの起動が完了しませんでした。"); }
      var app = new RegisteredApp { Key = key, Title = title, Process = process, Pid = process.Id, Handle = handle,
        StartTicks = process.StartTime.ToUniversalTime().Ticks, OwnedPractice = true };
      lock (sync) apps.Add(key, app);
    }
    public void Remove(string key) {
      RegisteredApp app; lock (sync) { if (!apps.TryGetValue(key, out app)) return; apps.Remove(key); }
      CloseOwned(app);
    }
    public RegisteredApp Get(string key) { lock (sync) { RegisteredApp a; return apps.TryGetValue(key, out a) && a.Refresh() ? a : null; } }
    public string ForegroundKey() {
      var h = Native.GetForegroundWindow(); if (IsNavigator(h)) return "navigator";
      foreach (var a in Apps) if (a.Handle == h && a.Refresh()) return a.Key;
      return "outside";
    }
    public bool ForegroundInScope() { return ForegroundKey() != "outside"; }
    static void CloseOwned(RegisteredApp app) {
      // Never close, kill or minimize a window the user registered.
      try { if (app.OwnedPractice && app.Refresh()) app.Process.CloseMainWindow(); } catch { }
      app.Process.Dispose();
    }
    public void Dispose() { if (destroyHook != IntPtr.Zero) { Native.UnhookWinEvent(destroyHook); destroyHook = IntPtr.Zero; } RegisteredApp[] old; lock (sync) { old = apps.Values.ToArray(); apps.Clear(); } foreach (var app in old) CloseOwned(app); }
  }
  sealed class PracticeSession : DesktopSession {
    public PracticeSession(IntPtr navigator) : base(navigator) { }
    public void Launch() { RegisterPractice("shelf", "練習・資料棚", "--practice-shelf"); RegisterPractice("request", "練習・申請アプリ", "--practice-request"); }
  }

  sealed class WindowsDesktop : IDesktopAdapter, IDisposable {
    readonly DesktopSession session;
    readonly UiaThread thread = new UiaThread();
    readonly bool readValues;
    public WindowsDesktop(DesktopSession session, bool readValues = true) { this.session = session; this.readValues = readValues; }
    public Task<DesktopView> ObserveAsync(string app, CancellationToken cancel) { return thread.Run(() => Observe(app, cancel), cancel); }
    static string Role(AutomationElement e) { return e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
    static Rectangle Bounds(System.Windows.Rect r) {
      if (r.IsEmpty || Double.IsNaN(r.X) || Double.IsInfinity(r.X) || Double.IsNaN(r.Y) || Double.IsInfinity(r.Y) ||
          Double.IsNaN(r.Width) || Double.IsInfinity(r.Width) || Double.IsNaN(r.Height) || Double.IsInfinity(r.Height) ||
          r.Width <= 0 || r.Height <= 0 || r.Left < Int32.MinValue || r.Top < Int32.MinValue || r.Right > Int32.MaxValue || r.Bottom > Int32.MaxValue)
        return Rectangle.Empty;
      return Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
    }
    static bool MayContainInputText(AutomationElement e) {
      var current = e.Current; object value, text;
      if (current.IsPassword || current.ControlType == ControlType.Edit || e.TryGetCurrentPattern(ValuePattern.Pattern, out value)) return true;
      if (!e.TryGetCurrentPattern(TextPattern.Pattern, out text)) return false;
      // Rich editors can expose entered text through TextPattern descendants
      // without ValuePattern. Only an explicit read-only result permits traversal.
      var readOnly = ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
      return !(readOnly is bool && (bool)readOnly);
    }
    static string IndependentLabel(AutomationElement label, AutomationElement target, RegisteredApp app) {
      if (label == null || String.Join(".", label.GetRuntimeId()) == String.Join(".", target.GetRuntimeId())) return "";
      string rootId = String.Join(".", AutomationElement.FromHandle(app.Handle).GetRuntimeId());
      var current = label;
      for (int depth = 0; current != null && depth < 64; depth++) {
        if (current.Current.ProcessId != app.Pid || MayContainInputText(current)) return "";
        if (String.Join(".", current.GetRuntimeId()) == rootId) return Uia.Clean(label.Current.Name);
        current = TreeWalker.ControlViewWalker.GetParent(current);
      }
      // A label in another window, or underneath an editor, is not an independent cue.
      return "";
    }
    DesktopNode ReadNode(AutomationElement e, RegisteredApp app) {
      var c = e.Current;
      if (c.ProcessId != app.Pid || c.IsPassword || c.IsOffscreen) return null;
      var rect = Bounds(c.BoundingRectangle); if (rect.IsEmpty) return null;
      object invoke, value;
      bool invokable = e.TryGetCurrentPattern(InvokePattern.Pattern, out invoke);
      bool hasValue = e.TryGetCurrentPattern(ValuePattern.Pattern, out value);
      var type = c.ControlType;
      bool inputText = hasValue || type == ControlType.Edit || MayContainInputText(e);
      if (inputText && !hasValue && !invokable) return null;
      // UIA patterns determine capability; there is no button-only or coordinate fallback.
      if (!invokable && !hasValue && type != ControlType.Text && type != ControlType.Window && type != ControlType.Pane && type != ControlType.Document) return null;
      var parent = TreeWalker.ControlViewWalker.GetParent(e);
      string automationId = Uia.Clean(c.AutomationId);
      string name;
      if (inputText) {
        // Custom editors sometimes publish the user's value as Name. Use an
        // independent label or stable AutomationId, never that editable Name.
        name = IndependentLabel(c.LabeledBy, e, app);
        if (String.IsNullOrWhiteSpace(name)) {
          if (String.IsNullOrWhiteSpace(automationId)) return null;
          name = "入力欄 [" + automationId + "]";
        }
      } else name = Uia.Clean(c.Name);
      string context = "";
      if (parent != null && !MayContainInputText(parent)) context = Uia.Clean(parent.Current.Name);
      // Values remain ephemeral and are not requested at all by the teaching adapter.
      string currentValue = null; bool valueReadable = false;
      bool settable = hasValue && !((ValuePattern)value).Current.IsReadOnly;
      if (hasValue && readValues) { currentValue = ((ValuePattern)value).Current.Value; valueReadable = currentValue != null && currentValue.Length <= 20000; if (!valueReadable) currentValue = null; }
      return new DesktopNode { Name = name, Role = Role(e), AutomationId = automationId, Context = context,
        Runtime = String.Join(".", e.GetRuntimeId()), Rect = rect, Enabled = c.IsEnabled, Visible = !c.IsOffscreen,
        Invokable = invokable, Settable = settable, ValueReadable = valueReadable, Value = currentValue };
    }
    DesktopView Observe(string app, CancellationToken cancel) {
      cancel.ThrowIfCancellationRequested();
      var v = new DesktopView { App = app, Foreground = session.ForegroundKey() };
      var a = session.Get(app); if (a == null) return v;
      v.Exists = true; v.Identity = a.Identity;
      var title = new StringBuilder(512); Native.GetWindowText(a.Handle, title, title.Capacity); v.Title = title.ToString();
      v.Minimized = Native.IsIconic(a.Handle);
      Native.WinRect wr; if (!Native.GetWindowRect(a.Handle, out wr)) throw new ScreenChangedException("ウィンドウを確認できません。");
      v.Rect = wr.Rectangle;
      var root = AutomationElement.FromHandle(a.Handle);
      v.Identity += ":" + String.Join(".", root.GetRuntimeId());
      if (v.Minimized) return v;
      // A bounded traversal stops before collecting an unbounded descendants array.
      var queue = new Queue<AutomationElement>(); queue.Enqueue(root); int visited = 0;
      while (queue.Count > 0) {
        cancel.ThrowIfCancellationRequested();
        var e = queue.Dequeue(); if (++visited > 2000) throw new InvalidOperationException("候補が多すぎるため保留しました。対象ウィンドウを絞ってください。");
        if (e.Current.ProcessId != a.Pid || e.Current.IsPassword) continue;
        DesktopNode node; try { node = ReadNode(e, a); } catch (SensitiveMetadataException) { node = null; }
        if (node != null) v.Nodes.Add(node);
        // Editors may expose their actual text as child Text.Name. Those children
        // are never teaching/result cues. No value-bearing subtree is traversed.
        if (MayContainInputText(e)) continue;
        var child = TreeWalker.ControlViewWalker.GetFirstChild(e);
        while (child != null) {
          cancel.ThrowIfCancellationRequested();
          if (queue.Count + visited >= 2000) throw new InvalidOperationException("候補が多すぎるため保留しました。");
          queue.Enqueue(child); child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
      }
      if (!a.Refresh()) throw new ScreenChangedException("対象アプリが閉じられました。");
      v.Foreground = session.ForegroundKey(); return v;
    }
    public Task<DesktopNode> AtAsync(string app, Native.Point point, CancellationToken cancel) {
      return thread.Run(() => {
        var a = session.Get(app);
        if (a == null || Native.GetForegroundWindow() != a.Handle || Native.GetAncestor(Native.WindowFromPoint(point), 2) != a.Handle)
          throw new ScreenChangedException("教示するウィンドウが変わりました。");
        var e = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        string rootId = String.Join(".", AutomationElement.FromHandle(a.Handle).GetRuntimeId());
        DesktopNode candidate = null;
        for (int depth = 0; e != null && depth < 64; depth++) {
          cancel.ThrowIfCancellationRequested(); if (e.Current.ProcessId != a.Pid || e.Current.IsPassword) return null;
          // FromPoint may return a child Text containing the editor's value.
          // Validate the complete ancestry and replace such children with the editor.
          if (MayContainInputText(e)) candidate = ReadNode(e, a);
          else if (candidate == null) candidate = ReadNode(e, a);
          if (String.Join(".", e.GetRuntimeId()) == rootId) return candidate;
          e = TreeWalker.ControlViewWalker.GetParent(e);
        }
        return null;
      }, cancel);
    }
    public async Task ExecuteAsync(PreparedAction action, CancellationToken cancel) {
      await thread.Run(() => {
        var fresh = Observe(action.App, cancel);
        if (!fresh.InScope || fresh.Identity != action.Identity || fresh.Stamp != action.ViewStamp)
          throw new ActionNotExecutedException("操作直前に対象が変わりました。");
        var a = session.Get(action.App); if (a == null) throw new ActionNotExecutedException("対象アプリが閉じられました。");
        cancel.ThrowIfCancellationRequested();
        if (action.Kind == "activate") {
          if (!Native.IsWindowEnabled(a.Handle)) throw new ActionNotExecutedException("ダイアログなどで元のウィンドウが操作できません。対象ダイアログを明示登録してください。");
          if (!a.Refresh() || !session.ForegroundInScope()) throw new ActionNotExecutedException("切替直前に登録範囲から移りました。");
          cancel.ThrowIfCancellationRequested();
          if (Native.IsIconic(a.Handle)) Native.ShowWindowAsync(a.Handle, 9);
          if (!ActivateApprovedWindow(a, cancel)) throw new InvalidOperationException("Windowsが前面への切替を許可しませんでした。対象アプリの状態を確認してください。");
          return true;
        }
        if ((action.Kind != "invoke" && action.Kind != "set-value") || action.Target == null ||
            (fresh.Foreground != action.App && fresh.Foreground != "navigator") || !Native.IsWindowEnabled(a.Handle))
          throw new ActionNotExecutedException("操作対象が前面にないか、ダイアログが開いています。");
        var match = fresh.Nodes.SingleOrDefault(n => n.Runtime == action.Target.Runtime && n.Stamp == action.Target.Stamp);
        if (match == null || !match.Enabled || (action.Kind == "invoke" ? !match.Invokable : !match.Settable))
          throw new ActionNotExecutedException("対象を再確認できません。");
        var center = new Native.Point { X = match.Rect.Left + match.Rect.Width / 2, Y = match.Rect.Top + match.Rect.Height / 2 };
        if (Native.GetAncestor(Native.WindowFromPoint(center), 2) != a.Handle)
          throw new ActionNotExecutedException("対象が別ウィンドウに隠れています。ナビを対象に重ならない位置へ移してください。");
        // FromPoint supplies a live element, but only the approved runtime/semantic match may dispatch.
        var element = AutomationElement.FromPoint(new System.Windows.Point(center.X, center.Y));
        for (int depth = 0; element != null && depth < 8; depth++, element = TreeWalker.ControlViewWalker.GetParent(element)) {
          cancel.ThrowIfCancellationRequested();
          if (String.Join(".", element.GetRuntimeId()) != match.Runtime) continue;
          var now = ReadNode(element, a);
          if (now == null || now.Stamp != match.Stamp || !a.Refresh() || !session.ForegroundInScope() ||
              (session.ForegroundKey() != action.App && session.ForegroundKey() != "navigator"))
            throw new ActionNotExecutedException("実行直前に対象が変わりました。");
          object pattern;
          if (action.Kind == "invoke") {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) throw new ActionNotExecutedException("Invokeに対応していません。");
            CheckDispatchScope(a, action.App, center, cancel); ((InvokePattern)pattern).Invoke();
          } else {
            if (action.InputText == null || !element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
              throw new ActionNotExecutedException("値を一度で設定できる入力欄ではありません。");
            CheckDispatchScope(a, action.App, center, cancel); ((ValuePattern)pattern).SetValue(action.InputText);
          }
          return true;
        }
        throw new ActionNotExecutedException("対象が消えました。");
      }, cancel, true);
    }
    void CheckDispatchScope(RegisteredApp app, string key, Native.Point center, CancellationToken cancel) {
      // Pattern/property retrieval can block. Repeat native scope checks after it,
      // immediately before the first mutation, without another provider call.
      cancel.ThrowIfCancellationRequested(); string foreground = session.ForegroundKey();
      if (!app.Refresh() || !Native.IsWindowEnabled(app.Handle) || (foreground != key && foreground != "navigator") ||
          Native.GetAncestor(Native.WindowFromPoint(center), 2) != app.Handle)
        throw new ActionNotExecutedException("操作直前に対象の表示またはフォーカスが変わりました。");
    }
    bool ActivateApprovedWindow(RegisteredApp app, CancellationToken cancel) {
      cancel.ThrowIfCancellationRequested();
      if (!app.Refresh() || !Native.IsWindowEnabled(app.Handle) || !session.ForegroundInScope()) return false;
      if (Native.GetForegroundWindow() == app.Handle) return true;
      if (!Native.SetForegroundWindow(app.Handle)) {
        var root = AutomationElement.FromHandle(app.Handle);
        cancel.ThrowIfCancellationRequested();
        if (!app.Refresh() || !Native.IsWindowEnabled(app.Handle) || !session.ForegroundInScope()) return false;
        root.SetFocus();
      }
      cancel.ThrowIfCancellationRequested();
      return app.Refresh() && Native.GetForegroundWindow() == app.Handle;
    }
    public void Dispose() { thread.Dispose(); }
  }
}
