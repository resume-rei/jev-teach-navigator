using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace JevTeachWindows {
  public static class NativeSkillTests {
    static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Reject(Action body, string reason) {
      try { body(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (IOException) { return; }
      throw new Exception("Expected rejection: " + reason);
    }
    static NativeSkillStep Click() {
      return new NativeSkillStep { App = "requests", Target = "新しい申請", Context = "申請", AutomationId = "new-request",
        Description = "申請フォームを開く", Precondition = "申請一覧", PreconditionAutomationId = "list-title", Result = "申請フォーム", ResultAutomationId = "form-title" };
    }
    static NativeSkillDocument Lesson() {
      var skill = NativeTeachingCompiler.Compile("備品を申請する", "備品を申請する", new[] { Click() });
      skill.Reviewed = true; skill.Requests.Add("備品を申請したい"); return skill;
    }
    static NativeSkillStep Input() {
      return new NativeSkillStep { App = "requests", Target = "申請理由", Role = "Edit", Context = "申請フォーム", AutomationId = "reason",
        Kind = "set-value", InputParameter = "reason", Description = "申請理由の全文を確認して入力する", Precondition = "申請フォーム" };
    }
    static void InTemporary(Action<string> body) {
      var dir = Path.Combine(Path.GetTempPath(), "jev-native-skill-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir); try { body(dir); } finally { Directory.Delete(dir, true); }
    }
    sealed class LessonDesktop : IDesktopAdapter {
      public string Foreground = "navigator", Value = "";
      public int Stage, Calls;
      public bool Complete;
      static DesktopNode Node(string name, string role, string id) {
        return new DesktopNode { Name = name, Role = role, AutomationId = id, Context = "申請", Runtime = id,
          Visible = true, Enabled = true, Invokable = role == "Button", Rect = new Rectangle(120, 160, 100, 30) };
      }
      DesktopView View(string app) {
        var v = new DesktopView { App = app, Title = app, Identity = "fake:" + app, Foreground = Foreground, Exists = true, Rect = new Rectangle(0, 0, 640, 480) };
        if (app == "requests") {
          v.Nodes.Add(Node(Stage == 0 ? "申請一覧" : "申請フォーム", "Text", Stage == 0 ? "list-title" : "form-title"));
          if (Stage == 0) v.Nodes.Add(Node("新しい申請", "Button", "new-request"));
          else { var edit = Node("申請理由", "Edit", "reason"); edit.Context = "申請フォーム"; edit.Settable = true; edit.ValueReadable = true; edit.Value = Value; v.Nodes.Add(edit); }
        } else {
          v.Nodes.Add(Node(Complete ? "今回の作業は完了です" : "資料棚", "Text", Complete ? "completed" : "shelf"));
          if (!Complete) v.Nodes.Add(Node("作業を完了", "Button", "finish"));
        }
        return v;
      }
      public Task<DesktopView> ObserveAsync(string app, CancellationToken cancel) { cancel.ThrowIfCancellationRequested(); return Task.FromResult(View(app)); }
      public Task ExecuteAsync(PreparedAction action, CancellationToken cancel) {
        cancel.ThrowIfCancellationRequested();
        if (action.ViewStamp != View(action.App).Stamp) throw new ScreenChangedException("fake screen changed");
        Calls++;
        if (action.Kind == "activate") Foreground = action.App;
        else if (action.Kind == "set-value") Value = action.InputText;
        else if (action.App == "requests") Stage++;
        else Complete = true;
        return Task.FromResult(0);
      }
    }
    public static int Run() {
      int count = 0;
      Action<string, Action> test = (name, body) => { body(); count++; Console.WriteLine("PASS " + name); };
      test("native teaching compiler requires a separate whole-lesson review", () => {
        var step = Click(); var s = NativeTeachingCompiler.Compile("備品を申請する", "備品を申請する", new[] { step });
        Assert(!s.Ready && s.Version == 0, "not executable before explicit review");
        Reject(() => s.ToDesktopTask(null), "unreviewed"); step.Target = "mutated";
        Assert(s.Steps[0].Target == "新しい申請", "compiler copies observed meanings");
        s.Reviewed = true; Assert(s.Ready && s.ToDesktopTask(null).Steps.Count == 1, "reviewed semantic task");
      });
      test("compiler fills descriptions and variable keys without copying values", () => {
        var step = Input(); step.Description = ""; step.InputParameter = "";
        var s = NativeTeachingCompiler.Compile("入力の練習", "入力する", new[] { step });
        Assert(s.Steps[0].InputParameter == "p1" && s.Steps[0].Description.Contains("入力案"), "local structured defaults");
        Assert(step.InputParameter == "" && step.Description == "", "input evidence object unchanged");
      });
      test("saved schema has no coordinates handles process IDs or runtime input", () => {
        var s = Lesson(); s.Steps.Add(Input()); var json = NativeSkillStore.Serialize(s);
        var roundTrip = NativeSkillStore.Parse(json); var task = roundTrip.ToDesktopTask(new Dictionary<string, string> { { "reason", "架空の今回だけの入力内容" } });
        Assert(task.Steps[1].InputText == "架空の今回だけの入力内容", "bound only for current execution");
        Assert(!NativeSkillStore.Serialize(roundTrip).Contains("架空の今回だけの入力内容"), "no persisted runtime value");
        foreach (var key in new[] { "\"x\":", "\"rect\":", "\"pid\":", "\"handle\":", "\"runtime\":", "\"inputText\":" })
          Assert(!json.Contains(key), "no live field " + key);
        Assert(task.Steps[0].AutomationId == "new-request" && task.Steps[0].PreconditionAutomationId == "list-title" && task.Steps[0].ResultAutomationId == "form-title", "semantic selectors mapped");
        task.Steps[0].Target = "modified runtime task"; Assert(roundTrip.Steps[0].Target == "新しい申請", "task does not alias saved meanings");
      });
      test("input parameters distinguish absent null and explicitly empty", () => {
        var s = NativeTeachingCompiler.Compile("入力の練習", "入力する", new[] { Input() }); s.Reviewed = true;
        Reject(() => s.ToDesktopTask(null), "missing parameter");
        Reject(() => s.ToDesktopTask(new Dictionary<string, string> { { "reason", null } }), "null parameter");
        Assert(s.ToDesktopTask(new Dictionary<string, string> { { "reason", "" } }).Steps[0].InputText == "", "explicit clear");
        Reject(() => s.ToDesktopTask(new Dictionary<string, string> { { "reason", "ok" }, { "other", "unknown" } }), "unknown parameter");
        Reject(() => s.ToDesktopTask(new Dictionary<string, string> { { "reason", new string('x', 16001) } }), "long value");
      });
      test("exact semantic selectors survive whitespace and serialization", () => {
        var s = Lesson(); s.Steps[0].Target = " 新しい申請 "; s.Steps[0].Context = "申請  "; s.Steps[0].AutomationId = " new-request ";
        var restored = NativeSkillStore.Parse(NativeSkillStore.Serialize(s));
        Assert(restored.Steps[0].Target == s.Steps[0].Target && restored.Steps[0].Context == s.Steps[0].Context && restored.Steps[0].AutomationId == s.Steps[0].AutomationId, "never silently normalize a UIA predicate");
      });
      test("review JSON stays readable and preserves the executable meaning exactly", () => {
        var s = Lesson(); s.Steps[0].Description = "段階 \"2\": {表示} を確認 \\ 次へ\n成功条件も確認";
        var compact = NativeSkillStore.Serialize(s); var review = NativeSkillStore.SerializeForReview(s);
        Assert(review.Contains("\n  \"schemaVersion\": 2") && review.Contains("\n    {"), "indented editor view");
        Assert(NativeSkillStore.Serialize(NativeSkillStore.Parse(review)) == compact, "formatting never changes selectors or approval");
      });
      test("strict schema rejects hidden fields and unsupported actions", () => {
        var json = NativeSkillStore.Serialize(Lesson());
        foreach (var extra in new[] { "\"pid\":14,", "\"inputText\":\"real value\",", "\"coordinates\":[1,2],", "\"apiKey\":\"sample\"," })
          Reject(() => NativeSkillStore.Parse("{" + extra + json.Substring(1)), "unknown root field");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"app\":\"requests\"", "\"app\":\"requests\",\"value\":\"recorded\"")), "unknown step value");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"kind\":\"invoke\"", "\"kind\":\"send-input\"")), "unsupported combined action");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"schemaVersion\":2", "\"schemaVersion\":1")), "no silent schema promotion");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"version\":0", "\"version\":0.0")), "integer type");
      });
      test("duplicate and escaped duplicate JSON keys cannot hide permissions", () => {
        var json = NativeSkillStore.Serialize(Lesson());
        Reject(() => NativeSkillStore.Parse(json.Replace("\"reviewed\":true", "\"reviewed\":false,\"reviewed\":true")), "duplicate permission");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"kind\":\"invoke\"", "\"kind\":\"invoke\",\"k\\u0069nd\":\"invoke\"")), "escaped duplicate action");
        Reject(() => NativeSkillStore.Parse(json.Replace("\"requests\"", "\"bad\nname\"")), "raw newline in JSON");
      });
      test("secrets are rejected in metadata at save and again at execution", () => {
        foreach (var secret in new[] { "password=secret", "Bearer sample-token", "api_key: abcdef", "暗証番号は1234", "sk-thisisafictionalkey123" }) {
          var s = Lesson(); s.Steps[0].Context = secret;
          Reject(() => NativeSkillStore.Serialize(s), "secret in selector");
          Reject(() => s.ToDesktopTask(null), "mutable object is revalidated");
        }
        var longName = Lesson(); longName.Name = new string('a', 121); Reject(() => longName.ToDesktopTask(null), "name length");
        var control = Lesson(); control.Goal = "goal\0tail"; Reject(() => NativeSkillStore.Serialize(control), "control character");
      });
      test("missing cues and free-text conditions remain unfinished", () => {
        var s = Lesson(); s.Steps[0].Result = ""; Assert(!s.Ready, "no click success cue");
        s = Lesson(); s.Steps[0].Precondition = ""; Assert(!s.Ready, "no before-screen cue");
        var step = Click();
        foreach (var conditional in new[] { "承認された場合だけ申請する", "承認済みなら進む", "この条件で申請する" }) {
          step.Description = conditional;
          s = NativeTeachingCompiler.Compile("条件付き申請", "備品を申請する", new[] { step }); s.Reviewed = true;
          Assert(!s.Ready && s.Unresolved.Count == 1, "no guessed branching");
        }
        s.Unresolved.Clear(); s.Steps[0].Description = "申請フォームを開く"; Assert(s.Ready, "explicit edit resolves it");
      });
      test("before-screen cue or target cannot be reused as click completion", () => {
        var s = Lesson(); s.Steps[0].Result = s.Steps[0].Precondition; s.Steps[0].ResultAutomationId = "";
        Reject(() => NativeSkillStore.Serialize(s), "unchanged precondition is not new evidence");
        s = Lesson(); s.Steps[0].Result = s.Steps[0].Target; s.Steps[0].ResultRole = "Button"; s.Steps[0].ResultAutomationId = "";
        Reject(() => NativeSkillStore.Serialize(s), "target is not result");
      });
      test("application aliases and cross-app results remain semantic", () => {
        var s = Lesson(); s.Steps[0].ResultApp = "archive"; s.Steps[0].AllowResume = true;
        var task = s.ToDesktopTask(null);
        Assert(s.UsedApps.SequenceEqual(new[] { "requests", "archive" }) && task.Steps[0].ResultApp == "archive" && task.Steps[0].AllowResume, "all runtime bindings required");
        s.Steps[0].App = "../other"; Reject(() => NativeSkillStore.Serialize(s), "alias cannot become a path");
      });
      test("native app aliases match registration syntax and reserve scope sentinels", () => {
        foreach (var alias in new[] { "navigator", "outside", "Navigator", "OUTSIDE", "日本語", "1app", new string('a', 65) }) {
          var s = Lesson(); s.Steps[0].App = alias; Reject(() => NativeSkillStore.Serialize(s), "invalid or reserved app alias");
          s = Lesson(); s.Steps[0].ResultApp = alias; Reject(() => s.ToDesktopTask(null), "invalid result app alias");
        }
      });
      test("same label with a distinct result role is a valid transition", () => {
        var s = Lesson(); s.Steps[0].Result = s.Steps[0].Target;
        Assert(s.Ready && s.ToDesktopTask(null).Steps[0].ResultRole == "Text", "button and heading are distinct meanings");
      });
      test("natural phrase selection is deterministic and refuses ambiguity", () => {
        var a = Lesson(); var b = Lesson(); b.Name = "別の備品申請";
        Assert(NativeSkillSelector.Select("備品を申請したい", new[] { a }).State == "selected", "known request phrase");
        var ambiguous = NativeSkillSelector.Select("備品を申請したい", new[] { a, b });
        Assert(ambiguous.State == "ambiguous" && ambiguous.Skill == null && ambiguous.Candidates.Count == 2, "no arbitrary winner");
        Assert(NativeSkillSelector.Select("税務資料を印刷したい", new[] { a }).State == "none", "unrelated request");
        a.Reviewed = false; Assert(NativeSkillSelector.Select("備品を申請したい", new[] { a }).State == "none", "draft excluded");
      });
      test("negative or cancelling requests cannot route to a positive skill", () => {
        foreach (var query in new[] { "備品を申請しない", "備品申請をキャンセル", "do not 備品を申請する", "申請を止める" })
          Assert(NativeSkillSelector.Select(query, new[] { Lesson() }).State == "none", "negative request");
      });
      test("immutable revisions survive edits and stale editor conflicts", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var draft = Lesson(); var v1 = store.Save(draft);
        Assert(draft.Version == 0 && v1.Version == 1, "save returns new object");
        var file = Path.Combine(dir, v1.Id, "v1.json"); var original = File.ReadAllText(file);
        var edit = store.Load(v1.Id); edit.Name = "変更した名前"; var v2 = store.Save(edit);
        Assert(v2.Version == 2 && store.Load(v1.Id).Name == "変更した名前", "latest revision selected");
        Assert(File.ReadAllText(file) == original && store.Load(v1.Id, 1).Name == v1.Name, "history retained");
        Assert(store.ListVersions(v1.Id).SequenceEqual(new[] { 1, 2 }), "ordered history");
        Reject(() => store.Save(v1), "optimistic conflict"); Assert(store.List().Single().Version == 2, "no conflict overwrite");
      }));
      test("launch review binds the entire displayed document to the selected saved version", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var saved = store.Save(Lesson());
        var shown = NativeSkillStore.SerializeForReview(saved);
        Assert(NativeSkillLaunchReview.RequireCurrent(store, saved.Id, shown, false).Id == saved.Id, "the displayed saved revision is launchable");
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, saved.Id, shown, true), "uncompiled or unsaved teaching blocks another skill");
        var draft = Lesson();
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, saved.Id, NativeSkillStore.SerializeForReview(draft), false), "new unsaved draft cannot launch stale selected row");
        var other = store.Save(Lesson());
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, saved.Id, NativeSkillStore.SerializeForReview(other), false), "different displayed saved skill");
        var edited = store.Load(saved.Id); edited.Name = "別の操作内容";
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, saved.Id, NativeSkillStore.SerializeForReview(edited), false), "same ID/version but different full content");
      }));
      test("historical snapshots and a concurrently updated current version require fresh review", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var v1 = store.Save(Lesson()); var v1Shown = NativeSkillStore.SerializeForReview(v1);
        var edit = store.Load(v1.Id); edit.Steps[0].Description = "新しい版で変更した操作の意味"; var v2 = store.Save(edit);
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, v1.Id, v1Shown, false), "a later writer invalidates the reviewed version");
        var historical = store.Load(v1.Id, 1); historical.Version = v2.Version;
        Reject(() => NativeSkillLaunchReview.RequireCurrent(store, v1.Id, NativeSkillStore.SerializeForReview(historical), false), "renumbered historical content cannot launch the current skill");
        Assert(NativeSkillLaunchReview.RequireCurrent(store, v2.Id, NativeSkillStore.SerializeForReview(v2), false).Version == 2, "reloading and showing v2 restores launch");
      }));
      test("teaching save selection and cross-app sequential guide connect without replay", () => InTemporary(dir => {
        var s = Lesson(); s.Steps.Add(Input());
        s.Steps.Add(new NativeSkillStep { App = "archive", Target = "作業を完了", AutomationId = "finish", Description = "資料棚の作業を完了する", Precondition = "資料棚", Result = "今回の作業は完了です" });
        var store = new NativeSkillStore(dir); store.Save(s);
        var selected = NativeSkillSelector.Select("備品を申請したい", store.List()); Assert(selected.State == "selected", "reload and select");
        var task = selected.Skill.ToDesktopTask(new Dictionary<string, string> { { "reason", "今回だけの架空入力" } });
        var adapter = new LessonDesktop(); var runner = new DesktopRunner(adapter) { ResultTimeoutMs = 50, ResultPollMs = 1 };
        runner.StartAsync(task).GetAwaiter().GetResult();
        for (int i = 0; i < 5; i++) {
          int before = adapter.Calls; var proposal = runner.TakeApproval(); Assert(proposal != null, "fresh presented action");
          runner.ExecuteApprovedAsync(proposal).GetAwaiter().GetResult();
          Assert(adapter.Calls == before + 1, "each approval dispatches one operation");
        }
        Assert(runner.State == "done" && runner.StepIndex == 3 && adapter.Calls == 5 && adapter.Value == "今回だけの架空入力", "three semantic steps plus two separately approved activations");
        Assert(runner.TakeApproval() == null && !File.ReadAllText(Path.Combine(dir, s.Id, "v1.json")).Contains("今回だけの架空入力"), "no extra action and no input persistence");
      }));
      test("independent writers never publish two copies of the same revision", () => InTemporary(dir => {
        var draft = Lesson(); int won = 0, held = 0;
        using (var start = new ManualResetEventSlim(false)) {
          var jobs = Enumerable.Range(0, 2).Select(i => Task.Run(() => {
            start.Wait(); try { new NativeSkillStore(dir).Save(draft); Interlocked.Increment(ref won); }
            catch (IOException) { Interlocked.Increment(ref held); } catch (InvalidOperationException) { Interlocked.Increment(ref held); }
          })).ToArray(); start.Set(); Task.WaitAll(jobs);
        }
        Assert(won == 1 && held == 1 && new NativeSkillStore(dir).Load(draft.Id).Version == 1, "exactly one publication");
      }));
      test("held writer lease refuses saves without modifying revisions", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var v1 = store.Save(Lesson());
        using (var lease = new FileStream(Path.Combine(dir, v1.Id, ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
          Reject(() => new NativeSkillStore(dir).Save(v1), "another active writer");
        Assert(store.Load(v1.Id).Version == 1, "unchanged after held save");
      }));
      test("corrupt newest revision is preserved and never silently rolled back", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var v1 = store.Save(Lesson()); var v2 = store.Save(v1);
        var file = Path.Combine(dir, v1.Id, "v2.json"); File.WriteAllText(file, "{damaged newest revision");
        Reject(() => store.Load(v1.Id), "do not select old version automatically"); Reject(() => store.List(), "list surfaces corruption");
        Reject(() => store.Save(v2), "do not overwrite corrupt newest");
        Assert(File.ReadAllText(file) == "{damaged newest revision" && !File.Exists(Path.Combine(dir, v1.Id, "v3.json")), "corruption preserved");
        Assert(store.Load(v1.Id, 1).Version == 1, "explicit historical inspection allowed");
      }));
      test("file identity mismatches and missing historical revisions block publication", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var v1 = store.Save(Lesson()); var file = Path.Combine(dir, v1.Id, "v1.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"version\":1", "\"version\":2"));
        Reject(() => store.Load(v1.Id), "file version mismatch");
        File.Move(file, Path.Combine(dir, v1.Id, "v2.json")); Reject(() => store.List(), "history gap");
        Reject(() => store.Load("../../escape"), "path traversal");
      }));
      test("interrupted temporary files never become a published revision", () => InTemporary(dir => {
        var store = new NativeSkillStore(dir); var v1 = store.Save(Lesson());
        var temporary = Path.Combine(dir, v1.Id, ".interrupted.tmp"); File.WriteAllText(temporary, "partial json");
        Assert(store.Load(v1.Id).Version == 1, "last complete revision");
        Assert(store.Save(v1).Version == 2 && File.ReadAllText(temporary) == "partial json", "old interruption preserved");
      }));
      test("legacy native evidence import cannot become executable automatically", () => {
        var json = "{\"schemaVersion\":1,\"status\":\"evidence-only-not-executable\",\"events\":[{\"pid\":42,\"window\":5,\"point\":{\"x\":100,\"y\":200}}]}";
        Reject(() => NativeSkillStore.Parse(json), "normal parser refuses legacy evidence");
        var draft = NativeSkillStore.ImportLegacyForReview(json); draft.Reviewed = true;
        Assert(!draft.Ready && draft.Steps.Count == 0 && draft.Unresolved.Count > 0, "re-teaching required");
        var converted = NativeSkillStore.Serialize(draft); Assert(!converted.Contains("\"pid\"") && !converted.Contains("\"point\""), "runtime evidence not copied");
      });
      test("legacy Web meanings import as a fresh review-only native lesson", () => {
        var source = new { schemaVersion = 1, id = Guid.NewGuid().ToString("D"), version = 7, app = "fictional-requests", name = "旧備品申請", purpose = "備品を申請する", examples = new[] { "備品を申請したい" },
          targets = new[] { new { key = "button|申請する|申請", label = "申請する", meaning = "新しい申請を開始する" } },
          steps = new[] { new { target = "button|申請する|申請", action = "click", before = "list", after = "form", parameter = (string)null } } };
        var json = new JavaScriptSerializer().Serialize(source); var draft = NativeSkillStore.ImportLegacyForReview(json);
        Assert(draft.Id != source.id && draft.Version == 0 && !draft.Ready && draft.Steps[0].Result == "" && !draft.Steps[0].AllowResume, "no DOM state promoted");
        draft.Reviewed = true; draft.Unresolved.Clear(); Assert(!draft.Ready, "before/after must be re-taught even after generic review");
        Assert(new JavaScriptSerializer().Serialize(source) == json, "source remains unchanged");
      });
      Console.WriteLine("PASS native semantic skills: " + count + " tests (local contract/storage tests; no Windows UI or APIs).");
      return count;
    }
  }
}
