// Windows extension lab. Independent source; no Hermes/cua-driver dependency.
// Teaching captures UIA metadata only. No screen capture, key logger or raw audio files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace JevTeachWindows {
  static partial class Native {
    public delegate IntPtr Hook(int n,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Mouse { public Point P;public uint Data,Flags,Time;public IntPtr Extra; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out int pid);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id,Hook hook,IntPtr module,uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h,int n,IntPtr w,IntPtr l);
    [DllImport("kernel32.dll",CharSet=CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  }
  public class Candidate {
    public string Label,Role,AutomationId,Context,Runtime;
    public int ProcessId;
    public Rectangle Bounds;
    public bool Enabled,CanInvoke;
    public string Semantic {get{return Role+"|"+Label+"|"+AutomationId+"|"+Context;}}
    public override string ToString(){return Role+" · "+Label+" / "+Context;}
  }
  sealed class SensitiveMetadataException : InvalidOperationException { public SensitiveMetadataException(string message) : base(message) { } }
  static class Uia {
    public static string Clean(string s) {s=s??"";if(Regex.IsMatch(s,@"password|passwd|api.?key|secret|パスワード|暗証番号|認証コード|秘密鍵|sk-[\w-]{10,}|gh[pousr]_[\w]{10,}",RegexOptions.IgnoreCase))throw new SensitiveMetadataException("認証情報らしい文字列は記録しません。");if(s.Length>500)throw new InvalidOperationException("UIAの意味記述が長すぎるため保留します。短縮して別対象と同一視しません。");return s;}
    public static Candidate Read(AutomationElement e,int pid) {
      var c=e.Current;if(c.ProcessId!=pid||c.IsPassword||c.IsOffscreen||c.BoundingRectangle.IsEmpty)return null;
      if(c.ControlType!=ControlType.Button&&c.ControlType!=ControlType.Edit&&c.ControlType!=ControlType.CheckBox&&c.ControlType!=ControlType.ComboBox)return null;
      var parent=TreeWalker.ControlViewWalker.GetParent(e);var r=c.BoundingRectangle;object pattern;
      return new Candidate{Label=Clean(c.Name),Role=c.ControlType.ProgrammaticName,AutomationId=Clean(c.AutomationId),Context=parent==null?"":Clean(parent.Current.Name),ProcessId=pid,Runtime=String.Join(".",e.GetRuntimeId()),Bounds=new Rectangle((int)Math.Round(r.X),(int)Math.Round(r.Y),(int)Math.Round(r.Width),(int)Math.Round(r.Height)),Enabled=c.IsEnabled,CanInvoke=e.TryGetCurrentPattern(InvokePattern.Pattern,out pattern)};
    }
    public static List<Candidate> Observe(IntPtr handle,int pid) {
      int current;Native.GetWindowThreadProcessId(handle,out current);if(!Native.IsWindow(handle)||current!=pid)throw new InvalidOperationException("対象ウィンドウが失効しました。");
      var root=AutomationElement.FromHandle(handle);var all=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsControlElementProperty,true));
      if(all.Count>1500)throw new InvalidOperationException("候補が多すぎるため観察を保留しました。");
      var rows=new List<Candidate>();foreach(AutomationElement e in all){var c=Read(e,pid);if(c!=null)rows.Add(c);}return rows;
    }
    public static Candidate At(Native.Point point,int pid) {
      var e=AutomationElement.FromPoint(new System.Windows.Point(point.X,point.Y));
      for(int i=0;e!=null&&i<5;i++){if(e.Current.IsPassword)throw new InvalidOperationException("機密入力欄は記録しません。");var c=Read(e,pid);if(c!=null)return c;e=TreeWalker.ControlViewWalker.GetParent(e);}return null;
    }
    public static string Fingerprint(List<Candidate> rows) {return String.Join("\n",rows.Select(c=>c.Semantic+"|"+c.Runtime+"|"+c.Bounds.ToString()+"|"+c.Enabled));}
    public static Candidate Resolve(Candidate target,List<Candidate> rows) {
      var found=rows.Where(c=>c.Enabled&&c.Semantic==target.Semantic).ToList();if(found.Count!=1)throw new InvalidOperationException(found.Count==0?"対象が見つかりません。":"同じ意味の対象が複数あります。");return found[0];
    }
    public static void Invoke(IntPtr handle,int pid,Candidate target,string fingerprint,IntPtr foreground) {
      var current=Observe(handle,pid);if(Fingerprint(current)!=fingerprint)throw new InvalidOperationException("実行直前に画面が変わりました。");var match=Resolve(target,current);
      var root=AutomationElement.FromHandle(handle);var all=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
      foreach(AutomationElement e in all){if(String.Join(".",e.GetRuntimeId())!=match.Runtime)continue;if(e.Current.IsPassword||!e.Current.IsEnabled||e.Current.IsOffscreen||Native.GetForegroundWindow()!=foreground)break;object p;if(e.TryGetCurrentPattern(InvokePattern.Pattern,out p)){((InvokePattern)p).Invoke();return;}}
      throw new InvalidOperationException("InvokePatternを利用できません。座標クリックには切り替えません。");
    }
  }
  class Overlay : Form {
    Rectangle highlight; bool arrow;
    public Overlay(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Color.Fuchsia;TransparencyKey=Color.Fuchsia;DoubleBuffered=true;Enabled=false;}
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x20|0x80000|0x8000000|0x80;return p;}}
    public void Aim(Rectangle r,bool isArrow){highlight=new Rectangle(55,12,r.Width,r.Height);arrow=isArrow;Native.SetWindowPos(Handle,new IntPtr(-1),r.Left-55,r.Top-12,r.Width+72,r.Height+25,0x10|0x40);Invalidate();}
    public new void Hide(){if(IsHandleCreated)Native.ShowWindow(Handle,0);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var pen=new Pen(arrow?Color.Firebrick:Color.Goldenrod,3)){e.Graphics.DrawRectangle(pen,highlight);if(arrow){pen.EndCap=System.Drawing.Drawing2D.LineCap.ArrowAnchor;pen.Width=6;e.Graphics.DrawLine(pen,5,highlight.Top+highlight.Height/2,50,highlight.Top+highlight.Height/2);}else e.Graphics.DrawString("✦",new Font("Segoe UI",14),Brushes.Goldenrod,highlight.Right-5,0);}}
  }
  class WindowChoice {public IntPtr Handle;public int Pid;public string Name;public override string ToString(){return Name+" ["+Pid+"]";}}
  class Pending {public Candidate Target;public string Fingerprint;public IntPtr Foreground;public DateTime Created;public bool Live;}
  class Lab : Form {
    readonly ComboBox windows=new ComboBox();readonly ListBox candidates=new ListBox();readonly TextBox explanation=new TextBox();readonly TextBox approval=new TextBox();readonly Label status=new Label();readonly CheckBox screenExplanation=new CheckBox();readonly CheckBox live=new CheckBox();readonly ListBox timeline=new ListBox();readonly Overlay overlay=new Overlay();
    readonly JavaScriptSerializer json=new JavaScriptSerializer();readonly List<object> events=new List<object>();readonly string output;
    readonly System.Windows.Forms.Timer monitor=new System.Windows.Forms.Timer();
    IntPtr targetHandle,hookHandle;int targetPid,generation;bool teaching,busy,suppressRight;Native.Point down;Native.Hook hook;Candidate selected;Pending pending;SpeechRecognitionEngine speech;string lastFingerprint="";
    Button start,stop,mic,observe,propose,save; WindowChoice fixture;
    public Lab(string outputDir){
      output=outputDir;Text="Jev Teach Navigator · Windows拡張検証";Width=660;Height=860;MinimumSize=new Size(530,650);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Yu Gothic UI",10);BackColor=Color.FromArgb(246,248,243);
      var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(20)};Controls.Add(flow);
      Action<Control> add=c=>{c.Width=590;c.Margin=new Padding(0,4,0,4);flow.Controls.Add(c);};
      add(new Label{Text="Windows UIA / 透明オーバーレイ / ローカル音声の検証",Height=30});
      status.Text="■ 停止中 · 録画なし · マイク停止";status.Height=45;add(status);
      add(new Label{Text="別ウィンドウの架空アプリで検証してください。外部APIは使いません。",Height=35});
      add(Button("架空のWindows画面を開く",()=>{var exe=Application.ExecutablePath;var p=Process.Start(exe,"--fixture");p.WaitForInputIdle(5000);p.Refresh();fixture=new WindowChoice{Handle=p.MainWindowHandle,Pid=p.Id,Name="架空申請アプリ"};windows.Items.Add(fixture);windows.SelectedItem=fixture;BindWindow();}));
      add(Button("対象ウィンドウ一覧を更新",()=>{if(teaching)throw new InvalidOperationException("先に停止してください。");windows.Items.Clear();foreach(var p in Process.GetProcesses()){try{if(p.Id!=Process.GetCurrentProcess().Id&&p.MainWindowHandle!=IntPtr.Zero)windows.Items.Add(new WindowChoice{Handle=p.MainWindowHandle,Pid=p.Id,Name=p.ProcessName});}catch{}}}));
      windows.DropDownStyle=ComboBoxStyle.DropDownList;windows.SelectedIndexChanged+=(s,e)=>{if(!teaching)BindWindow();};add(windows);
      start=Button("教えるモードを開始",StartTeaching);stop=Button("記録を停止（マイクとフックを解除）",StopTeaching);add(start);add(stop);
      explanation.Multiline=true;explanation.Height=65;add(explanation);screenExplanation.Text="現在の画面全体を説明（クリック不要）";screenExplanation.Height=30;add(screenExplanation);
      add(Button("説明を記録",()=>{if(!teaching)throw new InvalidOperationException("教えるモードを開始してください。");var text=Uia.Clean(explanation.Text);if(String.IsNullOrWhiteSpace(text))return;Record("explanation",new{target=screenExplanation.Checked?null:selected,text=text,screenFingerprint=lastFingerprint});explanation.Clear();}));
      mic=Button("音声入力を開始 / 停止（教える間だけ）",ToggleMic);add(mic);
      timeline.Height=110;add(timeline);save=Button("停止済みの教示記録を保存",Save);add(save);
      add(new Label{Text="以下は1対象の再特定・矢印・Enterの独立検証。保存スキルとの接続は次段階。",Height=45});
      observe=Button("現在のUIA候補を観察",async()=>await Observe());add(observe);candidates.Height=100;add(candidates);propose=Button("選んだ対象を探し直して矢印を表示",async()=>await Propose());add(propose);
      live.Text="この起動中に開いた架空画面への1クリックを許可する";live.Height=35;live.CheckedChanged+=(s,e)=>Cancel("実行許可を変更しました。再提案してください。");add(live);
      approval.ReadOnly=true;approval.Multiline=true;approval.Height=65;approval.ImeMode=ImeMode.Disable;approval.Text="既定はdry-run。Enterはここにフォーカスがある場合だけ有効。";add(approval);
      approval.KeyDown+=async(s,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;if(e.KeyValue==13&&!enterHeld){enterHeld=true;await Approve();}}if(e.KeyCode==Keys.Escape)Cancel("提案を取消しました。");};approval.KeyUp+=(s,e)=>{if(e.KeyCode==Keys.Enter)enterHeld=false;};
      Deactivate+=(s,e)=>{enterHeld=false;if(pending!=null)Cancel("フォーカスが移ったため再提案が必要です。");};
      monitor.Interval=400;monitor.Tick+=async(s,e)=>{if(busy||targetHandle==IntPtr.Zero)return;if(pending!=null&&DateTime.UtcNow-pending.Created>TimeSpan.FromSeconds(20))Cancel("提案の有効期限が切れました。");if(teaching&&Native.GetForegroundWindow()!=targetHandle&&Native.GetForegroundWindow()!=Handle){StopTeaching();Status("別のアプリへ移ったため記録を停止しました。");}if(teaching)await ObserveTeaching();};monitor.Start();
      FormClosing+=(s,e)=>{if(events.Count>0){StopTeaching();try{Save();}catch{e.Cancel=true;Status("保存に失敗しました。記録を保全するため画面を閉じません。");}}};FormClosed+=(s,e)=>{StopTeaching();monitor.Stop();overlay.Dispose();};UpdateButtons();
    }
    bool enterHeld;
    Button Button(string text,Action action){var b=new Button{Text=text,Height=36};b.Click+=(s,e)=>{try{action();}catch(Exception ex){Status(ex.Message);}};return b;}
    void Status(string text){status.Text=(teaching?"● 教示中":"■ 停止中")+" · "+(speech==null?"マイク停止":"マイク使用中")+"\n"+text;}
    void UpdateButtons(){start.Enabled=!teaching;stop.Enabled=teaching;windows.Enabled=!teaching;mic.Enabled=teaching;observe.Enabled=!teaching;propose.Enabled=!teaching;save.Enabled=!teaching&&events.Count>0;}
    void BindWindow(){generation++;pending=null;overlay.Hide();var w=windows.SelectedItem as WindowChoice;if(w==null)return;targetHandle=w.Handle;targetPid=w.Pid;candidates.Items.Clear();Status("対象を設定しました。まだ記録していません。");}
    void Record(string kind,object details){if(!teaching)return;events.Add(new{kind=kind,time=DateTime.UtcNow.ToString("o"),window=targetHandle.ToInt64(),pid=targetPid,details=details});timeline.Items.Add(kind+" · "+DateTime.Now.ToLongTimeString());timeline.TopIndex=Math.Max(0,timeline.Items.Count-1);}
    void StartTeaching(){if(targetHandle==IntPtr.Zero||!Native.IsWindow(targetHandle))throw new InvalidOperationException("対象を選んでください。");if(events.Count>0)throw new InvalidOperationException("先に停止済み記録を保存してください。");teaching=true;generation++;lastFingerprint="";selected=null;pending=null;hook=MouseHook;hookHandle=Native.SetWindowsHookEx(14,hook,Native.GetModuleHandle(null),0);if(hookHandle==IntPtr.Zero){teaching=false;throw new InvalidOperationException("マウスフックを開始できません。");}UpdateButtons();Status("対象のUIAラベルと説明のみ記録。値・画像・音声ファイルは保存しません。");}
    void StopTeaching(){teaching=false;generation++;suppressRight=false;selected=null;try{StopMic();}finally{if(hookHandle!=IntPtr.Zero){Native.UnhookWindowsHookEx(hookHandle);hookHandle=IntPtr.Zero;}overlay.Hide();UpdateButtons();Status("記録を停止しました。");}}
    async Task ObserveTeaching(){if(!teaching||busy)return;int g=generation;busy=true;try{var rows=await Task.Run(()=>Uia.Observe(targetHandle,targetPid));if(!teaching||g!=generation)return;var f=Uia.Fingerprint(rows);if(f!=lastFingerprint){Record("screen-change",new{before=lastFingerprint,after=f});lastFingerprint=f;selected=null;}}catch(Exception ex){StopTeaching();Status(ex.Message);}finally{busy=false;}}
    IntPtr MouseHook(int n,IntPtr w,IntPtr l){
      if(n>=0&&teaching){var code=w.ToInt32();var info=(Native.Mouse)Marshal.PtrToStructure(l,typeof(Native.Mouse));bool inside=Native.GetForegroundWindow()==targetHandle&&Native.GetAncestor(Native.WindowFromPoint(info.P),2)==targetHandle;
        if(code==0x204&&inside){suppressRight=true;down=info.P;return new IntPtr(1);}
        if(code==0x205&&suppressRight){suppressRight=false;var end=info.P;int g=generation;BeginInvoke((Action)(async()=>{if(!teaching||g!=generation)return;if(Math.Abs(end.X-down.X)+Math.Abs(end.Y-down.Y)>8){var rect=Rectangle.FromLTRB(Math.Min(end.X,down.X),Math.Min(end.Y,down.Y),Math.Max(end.X,down.X),Math.Max(end.Y,down.Y));selected=null;overlay.Aim(rect,false);Record("reference-region",rect);return;}try{var c=await Task.Run(()=>Uia.At(end,targetPid));if(!teaching||g!=generation)return;if(c==null){Status("対象を特定できません。画面として説明できます。");return;}selected=c;overlay.Aim(c.Bounds,false);Record("selection",c);Status("対象: "+c.Label+"（実行していません）");}catch(Exception ex){Status(ex.Message);}}));return new IntPtr(1);}
        if(code==0x202&&inside){int g=generation;BeginInvoke((Action)(async()=>{if(!teaching||g!=generation)return;Record("left-click-observed",new{before=lastFingerprint,point=info.P,note="利用者の操作。成功や完了とは判定しない。"});await Task.Delay(250);if(teaching&&g==generation)await ObserveTeaching();}));}
      }return Native.CallNextHookEx(hookHandle,n,w,l);
    }
    void ToggleMic(){if(speech!=null){StopMic();Status("音声入力を停止しました。");return;}if(!teaching)throw new InvalidOperationException("教えるモード中のみ音声を使えます。");var recognizer=SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r=>r.Culture.Name=="ja-JP");if(recognizer==null)throw new InvalidOperationException("日本語のローカル認識エンジンがありません。文字入力を使ってください。");speech=new SpeechRecognitionEngine(recognizer);speech.LoadGrammar(new DictationGrammar());int g=generation;var engine=speech;speech.SpeechRecognized+=(s,e)=>{if(!teaching||generation!=g||e.Result.Confidence<.7f)return;BeginInvoke((Action)(()=>{if(teaching&&generation==g&&speech==engine)explanation.Text=e.Result.Text;}));};try{speech.SetInputToDefaultAudioDevice();speech.RecognizeAsync(RecognizeMode.Multiple);Status("認識結果は説明欄に表示します。「説明を記録」で確定してください。");}catch{StopMic();throw;}}
    void StopMic(){var old=speech;speech=null;if(old==null)return;try{old.RecognizeAsyncCancel();old.SetInputToNull();}finally{old.Dispose();}}
    void Save(){if(teaching)throw new InvalidOperationException("先に記録を停止してください。");if(events.Count==0)return;Directory.CreateDirectory(output);var file=Path.Combine(output,"teaching-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(stream)){writer.Write(json.Serialize(new{schemaVersion=1,status="evidence-only-not-executable",recording="UIA metadata and confirmed explanations only",events=events}));}events.Clear();timeline.Items.Clear();UpdateButtons();Status("教示記録を保存しました: "+Path.GetFileName(file));}
    async Task Observe(){if(teaching||busy)return;busy=true;int g=generation;var h=targetHandle;var pid=targetPid;try{var rows=await Task.Run(()=>Uia.Observe(h,pid));if(g!=generation)return;candidates.Items.Clear();foreach(var c in rows)candidates.Items.Add(c);Status(rows.Count+"候補。保存も操作もしていません。");}catch(Exception ex){Status(ex.Message);}finally{busy=false;}}
    async Task Propose(){if(teaching||busy)return;var chosen=candidates.SelectedItem as Candidate;if(chosen==null){Status("候補を選んでください。");return;}busy=true;int g=generation;var h=targetHandle;var pid=targetPid;try{var rows=await Task.Run(()=>Uia.Observe(h,pid));if(g!=generation)return;var fresh=Uia.Resolve(chosen,rows);if(!fresh.CanInvoke)throw new InvalidOperationException("この検証版はInvoke可能なボタンだけを提案します。");approval.Focus();pending=new Pending{Target=fresh,Fingerprint=Uia.Fingerprint(rows),Foreground=Native.GetForegroundWindow(),Created=DateTime.UtcNow,Live=live.Checked};overlay.Aim(fresh.Bounds,true);approval.Text=(live.Checked?"実行候補: ":"dry-run候補: ")+fresh.Label+"\r\nEnterでこの1操作を確認。Escで取消。";}catch(Exception ex){Cancel(ex.Message);}finally{busy=false;}}
    void Cancel(string reason){pending=null;overlay.Hide();Status(reason);}
    async Task Approve(){if(busy||pending==null)return;var p=pending;pending=null;overlay.Hide();busy=true;int g=generation;var h=targetHandle;var pid=targetPid;windows.Enabled=false;start.Enabled=false;live.Enabled=false;try{if(DateTime.UtcNow-p.Created>TimeSpan.FromSeconds(20)||Native.GetForegroundWindow()!=p.Foreground||live.Checked!=p.Live)throw new InvalidOperationException("提案の期限・許可・フォーカスが変わりました。");var rows=await Task.Run(()=>Uia.Observe(h,pid));if(g!=generation||Uia.Fingerprint(rows)!=p.Fingerprint)throw new InvalidOperationException("画面が変わりました。再提案してください。");if(!p.Live){Status("dry-run: 再特定と承認を確認しました。実入力は0回です。");return;}if(fixture==null||fixture.Handle!=h||fixture.Pid!=pid)throw new InvalidOperationException("実行はこの起動中に開いた架空アプリに限定しています。");await Task.Run(()=>Uia.Invoke(h,pid,p.Target,p.Fingerprint,p.Foreground));var after=await Task.Run(()=>Uia.Observe(h,pid));Status(Uia.Fingerprint(after)==p.Fingerprint?"結果不明。自動再実行しません。":"1操作を実行して画面の変化を観察しました。スキル完了判定は未接続です。");}catch(Exception ex){Status(ex.Message+" 自動再実行しません。");}finally{busy=false;live.Enabled=true;UpdateButtons();}}
  }
  class Fixture : Form {
    int stage;
    public Fixture(){Text="Jev 架空申請アプリ";Width=520;Height=350;Font=new Font("Yu Gothic UI",12);Render();}
    void Render(){Controls.Clear();var title=new Label{Text=stage==0?"備品申請":stage==1?"確認画面":"申請完了",Left=30,Top=30,Width=430,Height=50};Controls.Add(title);if(stage<2){var b=new Button{Text=stage==0?"申請する":"申請を送信",AccessibleName=stage==0?"申請する":"申請を送信",Name="fixture-action",Left=200,Top=160,Width=200,Height=50};b.Click+=(s,e)=>{stage++;Render();};Controls.Add(b);}}
  }
  static class Program {
    [STAThread] static int Main(string[] args){
      if(args.Contains("--self-test")){var a=new Candidate{Label="申請",Role="Button",AutomationId="apply",Context="備品",Enabled=true,Bounds=new Rectangle(-100,30,60,40)};if(Uia.Resolve(a,new List<Candidate>{a}).Bounds.X!=-100)return 1;try{Uia.Resolve(a,new List<Candidate>{a,a});return 2;}catch(InvalidOperationException){}try{Uia.Clean("password=secret");return 3;}catch(InvalidOperationException){}Console.WriteLine("PASS: semantic match, ambiguous hold, negative coordinates, credential rejection.");try{DesktopEngineTests.Run().GetAwaiter().GetResult();NativeSkillTests.Run();UiaWorkerTests.Run().GetAwaiter().GetResult();}catch(Exception ex){Console.Error.WriteLine(ex);return 4;}return 0;}
      try{Native.SetProcessDpiAwarenessContext(new IntPtr(-4));}catch(EntryPointNotFoundException){}
      Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
      if(args.Contains("--fixture"))Application.Run(new Fixture());
      else if(args.Contains("--practice-shelf"))Application.Run(new PracticeApp(true));
      else if(args.Contains("--practice-request"))Application.Run(new PracticeApp(false));
      else if(args.Contains("--teaching-lab")){var output=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..","data","windows"));Application.Run(new Lab(output));}
      else Application.Run(new DesktopNavigator());return 0;
    }
  }
}
