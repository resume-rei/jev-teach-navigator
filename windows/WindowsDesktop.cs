using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace JevTeachWindows {
  static partial class Native {
    [StructLayout(LayoutKind.Sequential)] public struct WinRect { public int Left, Top, Right, Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
    [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct GuiThreadInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public WinRect CaretRect; }
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
      if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return false;
      var name = new StringBuilder(256); GetClassName(info.Focus, name, name.Capacity);
      if (name.ToString().IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
      var context = ImmGetContext(info.Focus); if (context == IntPtr.Zero) return false;
      try { return ImmGetCompositionStringW(context, 8, IntPtr.Zero, 0) > 0; }
      finally { ImmReleaseContext(info.Focus, context); }
    }
    public static bool ModifierPressed() { return new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); }
  }
  // Every application in this milestone is explicitly launched by this session.
  // The task engine does not depend on fixture controls or their internal state.
  sealed class RegisteredApp {
    public string Key, Title;
    public Process Process;
    public int Pid;
    public long StartTicks;
    public IntPtr Handle;
    public bool Refresh() {
      try {
        Process.Refresh(); if (Process.HasExited || Process.StartTime.ToUniversalTime().Ticks != StartTicks) return false;
        Handle = Process.MainWindowHandle; int pid;
        return Handle != IntPtr.Zero && Native.IsWindow(Handle) && Native.GetWindowThreadProcessId(Handle, out pid) != 0 && pid == Pid;
      } catch { return false; }
    }
    public string Identity { get { return Pid + ":" + StartTicks + ":" + Handle.ToInt64(); } }
  }
  sealed class PracticeSession : IDisposable {
    readonly Dictionary<string, RegisteredApp> apps = new Dictionary<string, RegisteredApp>();
    readonly IntPtr navigator;
    public PracticeSession(IntPtr navigator) { this.navigator = navigator; }
    public void Launch() {
      Add("shelf", "練習・資料棚", "--practice-shelf");
      Add("request", "練習・申請アプリ", "--practice-request");
    }
    void Add(string key, string title, string argument) {
      var p = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, argument) { UseShellExecute = false });
      if (p == null) throw new InvalidOperationException("練習アプリを起動できませんでした。");
      var a = new RegisteredApp { Key = key, Title = title, Process = p, Pid = p.Id, StartTicks = p.StartTime.ToUniversalTime().Ticks };
      apps.Add(key, a);
      var watch = Stopwatch.StartNew();
      while (!a.Refresh() && watch.ElapsedMilliseconds < 5000) Thread.Sleep(40);
      if (!a.Refresh()) throw new InvalidOperationException("練習アプリの起動が完了しませんでした。");
    }
    public RegisteredApp Get(string key) { RegisteredApp a; return apps.TryGetValue(key, out a) && a.Refresh() ? a : null; }
    public string ForegroundKey() {
      var h = Native.GetForegroundWindow(); if (h == navigator) return "navigator";
      foreach (var pair in apps) {
        var a = pair.Value; int pid;
        if (a.Handle == h && Native.IsWindow(h) && Native.GetWindowThreadProcessId(h, out pid) != 0 && pid == a.Pid) return pair.Key;
      }
      return "outside";
    }
    public bool ForegroundInScope() { return ForegroundKey() != "outside"; }
    public void Dispose() {
      foreach (var a in apps.Values) {
        // Never kill a process, never close any pre-existing window.
        try { if (a.Refresh()) a.Process.CloseMainWindow(); } catch { }
        a.Process.Dispose();
      }
      apps.Clear();
    }
  }
  // UIA always runs on one background MTA thread, never on the forms thread.
  sealed class UiaThread : IDisposable {
    readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
    readonly Thread thread;
    public UiaThread() {
      thread = new Thread(() => { foreach (var job in jobs.GetConsumingEnumerable()) job(); });
      thread.IsBackground = true; thread.Name = "Jev-UIA"; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    public Task<T> Run<T>(Func<T> work, CancellationToken cancel) {
      var result = new TaskCompletionSource<T>();
      try { jobs.Add(() => { try { cancel.ThrowIfCancellationRequested(); result.TrySetResult(work()); } catch (OperationCanceledException) { result.TrySetCanceled(); } catch (Exception ex) { result.TrySetException(ex); } }); }
      catch (InvalidOperationException) { result.TrySetCanceled(); }
      return result.Task;
    }
    public void Dispose() { jobs.CompleteAdding(); }
  }
  sealed class WindowsDesktop : IDesktopAdapter, IDisposable {
    readonly PracticeSession session;
    readonly UiaThread thread = new UiaThread();
    public WindowsDesktop(PracticeSession session) { this.session = session; }
    public Task<DesktopView> ObserveAsync(string app, CancellationToken cancel) { return thread.Run(() => Observe(app), cancel); }
    DesktopView Observe(string app) {
      var v = new DesktopView { App = app, Foreground = session.ForegroundKey() };
      var a = session.Get(app); if (a == null) return v;
      v.Exists = true; v.Identity = a.Identity; v.Title = a.Title; v.Minimized = Native.IsIconic(a.Handle);
      Native.WinRect wr; if (!Native.GetWindowRect(a.Handle, out wr)) throw new ScreenChangedException("ウィンドウを確認できません。");
      v.Rect = wr.Rectangle; if (v.Minimized) return v;
      var root = AutomationElement.FromHandle(a.Handle);
      var nodes = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsControlElementProperty, true));
      if (nodes.Count > 2000) throw new InvalidOperationException("候補が多すぎるため保留しました。");
      foreach (AutomationElement e in nodes) {
        var c = e.Current;
        if (c.ProcessId != a.Process.Id || c.IsPassword || c.IsOffscreen || c.BoundingRectangle.IsEmpty) continue;
        if (c.ControlType != ControlType.Button && c.ControlType != ControlType.Text) continue;
        string name; try { name = Uia.Clean(c.Name); } catch { continue; }
        var parent = TreeWalker.ControlViewWalker.GetParent(e);
        string context = ""; try { if (parent != null) context = Uia.Clean(parent.Current.Name); } catch { continue; }
        object pattern; var r = c.BoundingRectangle;
        v.Nodes.Add(new DesktopNode { Name = name, Role = c.ControlType == ControlType.Button ? "Button" : "Text", Context = context,
          Runtime = String.Join(".", e.GetRuntimeId()), Rect = Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)),
          Enabled = c.IsEnabled, Visible = !c.IsOffscreen, Invokable = e.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) });
      }
      return v;
    }
    public async Task ExecuteAsync(PreparedAction action, CancellationToken cancel) {
      await thread.Run(() => {
        var fresh = Observe(action.App);
        if (!fresh.InScope || fresh.Identity != action.Identity || fresh.Stamp != action.ViewStamp)
          throw new ScreenChangedException("操作直前に対象が変わりました。");
        var a = session.Get(action.App); if (a == null) throw new ScreenChangedException("対象アプリが閉じられました。");
        cancel.ThrowIfCancellationRequested();
        if (action.Kind == "activate") {
          if (Native.IsIconic(a.Handle)) Native.ShowWindowAsync(a.Handle, 9);
          if (!ActivateApprovedWindow(a.Handle, cancel)) throw new InvalidOperationException("Windowsが前面への切替を許可しませんでした。対象アプリの状態を確認してください。");
          return true;
        }
        if (action.Kind != "invoke" || action.Target == null || (fresh.Foreground != action.App && fresh.Foreground != "navigator"))
          throw new ScreenChangedException("操作対象が前面にありません。");
        var match = fresh.Nodes.SingleOrDefault(n => n.Runtime == action.Target.Runtime && n.Stamp == action.Target.Stamp);
        if (match == null || !match.Enabled || !match.Invokable) throw new ScreenChangedException("対象を再確認できません。");
        var center = new Native.Point { X = match.Rect.Left + match.Rect.Width / 2, Y = match.Rect.Top + match.Rect.Height / 2 };
        if (Native.GetAncestor(Native.WindowFromPoint(center), 2) != a.Handle)
          throw new ScreenChangedException("対象が別ウィンドウに隠れています。");
        var root = AutomationElement.FromHandle(a.Handle);
        var nodes = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        foreach (AutomationElement e in nodes) {
          if (String.Join(".", e.GetRuntimeId()) != match.Runtime) continue;
          var c = e.Current; object invoke;
          var foreground = session.ForegroundKey();
          if (!c.IsEnabled || c.IsOffscreen || c.IsPassword || c.Name != match.Name || (foreground != action.App && foreground != "navigator"))
            throw new ScreenChangedException("実行直前に対象が変わりました。");
          if (!e.TryGetCurrentPattern(InvokePattern.Pattern, out invoke)) throw new ScreenChangedException("Invokeに対応していません。");
          cancel.ThrowIfCancellationRequested(); ((InvokePattern)invoke).Invoke(); return true;
        }
        throw new ScreenChangedException("対象が消えました。");
      }, cancel);
    }
    bool ActivateApprovedWindow(IntPtr target, CancellationToken cancel) {
      // Only reached for an explicitly approved activation in this session.
      // A manual click in a child app can revoke this process's foreground right.
      if (Native.GetForegroundWindow() == target || Native.SetForegroundWindow(target)) return true;
      if (!session.ForegroundInScope()) throw new ScreenChangedException("対象外のアプリへ移動しました。");
      cancel.ThrowIfCancellationRequested();
      // UIA can ask the window's provider to establish keyboard focus. The runner
      // still verifies the actual foreground HWND before offering a button action.
      AutomationElement.FromHandle(target).SetFocus();
      return true;
    }
    public void Dispose() { thread.Dispose(); }
  }
}
