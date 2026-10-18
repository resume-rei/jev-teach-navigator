using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JevTeachWindows {
  // The persisted schema contains meanings and parameter names only. Runtime input
  // text, coordinates, process identities and UIA element handles have no fields here.
  public sealed class NativeSkillStep {
    public string App = "", Target = "", Role = "Button", Context = "", AutomationId = "";
    public string Kind = "invoke", Description = "", InputParameter = "";
    public string Result = "", ResultApp = "", ResultRole = "Text", ResultContext = "", ResultAutomationId = "";
    public string Precondition = "", PreconditionRole = "Text", PreconditionContext = "", PreconditionAutomationId = "";
    public bool AllowResume;
  }

  public sealed class NativeSkillDocument {
    public string Id = Guid.NewGuid().ToString("D"), Name = "", Goal = "";
    public string Origin = "local-teaching", UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    public int Version;
    public bool Reviewed;
    public List<string> Requests = new List<string>();
    public List<NativeSkillStep> Steps = new List<NativeSkillStep>();
    public List<string> Unresolved = new List<string>();
    public List<string> Parameters { get { return Steps.Where(s => s.Kind == "set-value").Select(s => s.InputParameter).Distinct(StringComparer.Ordinal).ToList(); } }
    public List<string> UsedApps { get { return Steps.SelectMany(s => String.IsNullOrEmpty(s.ResultApp) ? new[] { s.App } : new[] { s.App, s.ResultApp }).Distinct(StringComparer.Ordinal).ToList(); } }
    public List<string> ReviewIssues { get { NativeSkillFormat.Validate(this); return NativeSkillFormat.Issues(this); } }
    public bool Ready { get { try { return ReviewIssues.Count == 0; } catch (ArgumentException) { return false; } } }
    public override string ToString() { return Name + " · v" + Version + (Ready ? "" : " · 未完成"); }

    public DesktopTask ToDesktopTask(IDictionary<string, string> inputs) {
      // Revalidate the mutable editor object, rather than trusting its earlier parse.
      NativeSkillFormat.Validate(this);
      var issues = NativeSkillFormat.Issues(this);
      if (issues.Count != 0) throw new InvalidOperationException("未完成のスキルは案内できません。" + String.Join(" ", issues));
      var values = inputs ?? new Dictionary<string, string>();
      if (values.Keys.Any(k => !Parameters.Contains(k))) throw new ArgumentException("このスキルにない入力パラメータがあります。");
      var task = new DesktopTask { Name = Name };
      foreach (var step in Steps) {
        string input = null;
        if (step.Kind == "set-value") {
          // An explicit empty string is a valid reviewed clearing operation. Missing
          // and null are different, and are never silently turned into an empty value.
          if (!values.TryGetValue(step.InputParameter, out input) || input == null)
            throw new ArgumentException("今回の入力案を指定してください: " + step.InputParameter);
          if (input.Length > 16000 || input.IndexOf('\0') >= 0)
            throw new ArgumentException("入力案は16000文字以内で、NULを含めないでください。");
        }
        task.Steps.Add(new TaskStep {
          App = step.App, Target = step.Target, Role = step.Role, Context = step.Context, AutomationId = step.AutomationId,
          Kind = step.Kind, Description = step.Description, InputText = input,
          Result = step.Result, ResultApp = String.IsNullOrEmpty(step.ResultApp) ? null : step.ResultApp,
          ResultRole = step.ResultRole, ResultContext = step.ResultContext, ResultAutomationId = step.ResultAutomationId,
          Precondition = step.Precondition, PreconditionRole = step.PreconditionRole,
          PreconditionContext = step.PreconditionContext, PreconditionAutomationId = step.PreconditionAutomationId,
          AllowResume = step.AllowResume
        });
      }
      task.Validate(); return task;
    }
  }

  public static class NativeTeachingCompiler {
    // This is a local deterministic compiler for explicit observed/reviewed steps,
    // not an LLM: free-form conditions are retained as unresolved instead of guessed.
    public static NativeSkillDocument Compile(string name, string goal, IEnumerable<NativeSkillStep> steps) {
      if (steps == null) throw new ArgumentException("教示した手順が必要です。");
      var skill = new NativeSkillDocument { Name = name, Goal = goal };
      skill.Requests.Add(goal);
      foreach (var original in steps) {
        if (original == null) throw new ArgumentException("空の手順は保存できません。");
        var step = NativeSkillFormat.CloneStep(original);
        if (String.IsNullOrWhiteSpace(step.Description)) step.Description = "「" + step.Target + "」" + (step.Kind == "set-value" ? "へ今回の入力案を入力する" : "を操作する");
        if (step.Kind == "set-value" && String.IsNullOrEmpty(step.InputParameter)) step.InputParameter = "p" + (skill.Steps.Count + 1);
        skill.Steps.Add(step);
        if (Regex.IsMatch(step.Description, @"場合|ときだけ|時だけ|なら|条件|もし|\b(?:only if|unless)\b", RegexOptions.IgnoreCase))
          skill.Unresolved.Add("自由文の条件を自動判定できません。手順 " + skill.Steps.Count + " の事前条件と実行範囲を確認してください。");
      }
      NativeSkillFormat.Validate(skill);
      // Completing the text entry alone never makes a lesson executable. The UI
      // separately presents the entire lesson and its completion condition for review.
      return skill;
    }
  }

  public sealed class NativeSkillSelection {
    public string State, Message;
    public NativeSkillDocument Skill;
    public List<NativeSkillDocument> Candidates = new List<NativeSkillDocument>();
  }

  public static class NativeSkillLaunchReview {
    // A displayed draft, imported lesson, historical snapshot, or stale editor must
    // never start whichever different saved row happens to remain selected in the UI.
    public static NativeSkillDocument RequireCurrent(NativeSkillStore store, string selectedId, string displayedJson, bool hasUnsavedWork) {
      if (hasUnsavedWork) throw new InvalidOperationException("未保存の教示・下書き・編集があります。保存するか、破棄を確認して保存済みスキルを開いてください。");
      if (store == null) throw new ArgumentException("保存先が必要です。");
      var displayed = NativeSkillStore.Parse(displayedJson);
      if (displayed.Version == 0 || displayed.Id != selectedId)
        throw new InvalidOperationException("表示中のスキルと選択した保存スキルが一致しません。保存済みスキルを閲覧してから開始してください。");
      var current = store.Load(selectedId);
      if (displayed.Version != current.Version || NativeSkillStore.Serialize(displayed) != NativeSkillStore.Serialize(current))
        throw new InvalidOperationException("表示した内容と保存済みの現在版が一致しません。最新版を読み直し、手順を確認してから開始してください。");
      if (!current.Ready) throw new InvalidOperationException("未完成のスキルは実行できません: " + System.String.Join(" / ", current.ReviewIssues));
      return current;
    }
  }

  public static class NativeSkillSelector {
    static string Normalize(string text) {
      var n = Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s\p{P}\p{S}]", "");
      foreach (var end in new[] { "をお願いします", "お願いします", "してください", "したいです", "がしたい", "したい", "する" })
        if (n.EndsWith(end, StringComparison.Ordinal) && n.Length > end.Length) { n = n.Substring(0, n.Length - end.Length); break; }
      return n;
    }
    static HashSet<string> Grams(string value) {
      var set = new HashSet<string>(StringComparer.Ordinal);
      for (int i = 0; i + 1 < value.Length; i++) set.Add(value.Substring(i, 2));
      return set;
    }
    static double Score(string request, string phrase) {
      var p = Normalize(phrase);
      if (request == p && p.Length > 0) return 1;
      if (request.Length < 3 || p.Length < 3) return 0;
      var a = Grams(request); var b = Grams(p);
      int shared = a.Count(x => b.Contains(x));
      return 2.0 * shared / Math.Max(1, a.Count + b.Count);
    }
    public static NativeSkillSelection Select(string request, IEnumerable<NativeSkillDocument> skills) {
      request = NativeSkillFormat.Text(request, 2000, true);
      var result = new NativeSkillSelection { State = "none", Message = "一致する完成済みスキルがありません。名前または保存した依頼例で指定してください。" };
      if (Regex.IsMatch(request, @"しない|しなく|やめ|止め|中止|取消|キャンセル|\b(?:not|never|cancel|stop|don't)\b", RegexOptions.IgnoreCase)) {
        result.Message = "否定・取消を含む依頼からはスキルを自動選択しません。"; return result;
      }
      var q = Normalize(request);
      var ranked = (skills ?? Enumerable.Empty<NativeSkillDocument>()).Where(s => s != null && s.Ready)
        .Select(s => new { Skill = s, Score = new[] { s.Name, s.Goal }.Concat(s.Requests).Max(p => Score(q, p)) })
        .OrderByDescending(s => s.Score).ThenBy(s => s.Skill.Id, StringComparer.Ordinal).ToList();
      if (ranked.Count == 0 || ranked[0].Score < .62) return result;
      result.Candidates = ranked.Where(s => s.Score >= .55 && ranked[0].Score - s.Score < .12).Select(s => s.Skill).ToList();
      if (result.Candidates.Count > 1) {
        result.State = "ambiguous"; result.Message = "複数のスキルが同程度に一致しました。名前や依頼例を具体化するか、一覧から明示選択してください。"; return result;
      }
      result.State = "selected"; result.Skill = ranked[0].Skill;
      result.Message = "ローカルの語句一致で選択しました。開始前に手順と対象アプリを確認してください。";
      return result;
    }
  }

  public sealed class NativeSkillStore {
    readonly string root;
    readonly object sync = new object();
    public NativeSkillStore(string directory) {
      if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("保存先が必要です。");
      root = Path.GetFullPath(directory);
    }
    public static NativeSkillDocument Parse(string json) { return NativeSkillFormat.Parse(json); }
    public static string Serialize(NativeSkillDocument skill) { return NativeSkillFormat.Serialize(skill); }
    public static string SerializeForReview(NativeSkillDocument skill) {
      var compact = Serialize(skill); var result = new StringBuilder();
      bool quoted = false, escaped = false; int depth = 0;
      Action line = () => { result.Append('\n'); result.Append(' ', depth * 2); };
      for (int i = 0; i < compact.Length; i++) {
        char c = compact[i];
        if (quoted) { result.Append(c); if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
        if (c == '"') { quoted = true; result.Append(c); }
        else if (c == '{' || c == '[') { result.Append(c); depth++; if (i + 1 < compact.Length && compact[i + 1] != '}' && compact[i + 1] != ']') line(); }
        else if (c == '}' || c == ']') { depth--; if (i > 0 && compact[i - 1] != '{' && compact[i - 1] != '[') line(); result.Append(c); }
        else if (c == ',') { result.Append(c); line(); }
        else if (c == ':') result.Append(": ");
        else result.Append(c);
      }
      if (Encoding.UTF8.GetByteCount(result.ToString()) > NativeSkillFormat.MaxJsonBytes) throw new ArgumentException("レビューするJSONが大きすぎます。手順や説明を分割してください。");
      return result.ToString();
    }
    static void NotLink(string path) {
      if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("リンク先への保存・読み込みは行いません。元の保存先を確認してください。");
    }
    string DirectoryFor(string id) { NativeSkillFormat.Id(id); return Path.Combine(root, id); }
    List<int> Revisions(string directory) {
      NotLink(root); NotLink(directory);
      if (!Directory.Exists(directory)) return new List<int>();
      var list = new List<int>();
      foreach (var file in Directory.GetFiles(directory)) {
        var name = Path.GetFileName(file); var match = Regex.Match(name, @"^v([1-9][0-9]*)\.json$");
        if (!match.Success) {
          if (name.StartsWith("v", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            throw new IOException("版ファイル名が不正です。既存ファイルを保全して確認してください。");
          continue;
        }
        int version;
        if (!Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out version))
          throw new IOException("版番号を読めません。既存ファイルを保全して確認してください。");
        list.Add(version);
      }
      list.Sort();
      for (int i = 0; i < list.Count; i++) if (list[i] != i + 1)
        throw new IOException("保存版の連続性が崩れています。既存ファイルを保全して確認してください。");
      return list;
    }
    public List<int> ListVersions(string id) { return Revisions(DirectoryFor(id)); }
    public NativeSkillDocument Load(string id, int version = 0) {
      var directory = DirectoryFor(id); var versions = Revisions(directory);
      if (versions.Count == 0) throw new FileNotFoundException("保存されたスキルがありません。");
      if (version == 0) version = versions[versions.Count - 1];
      if (!versions.Contains(version)) throw new FileNotFoundException("指定した版がありません。");
      var file = Path.Combine(directory, "v" + version.ToString(CultureInfo.InvariantCulture) + ".json"); NotLink(file);
      try {
        if (new FileInfo(file).Length > NativeSkillFormat.MaxJsonBytes) throw new ArgumentException("ファイルが大きすぎます。");
        var skill = Parse(File.ReadAllText(file, new UTF8Encoding(false, true)));
        if (skill.Id != id || skill.Version != version) throw new ArgumentException("スキルIDまたは版とファイル名が一致しません。");
        return skill;
      } catch (Exception ex) {
        if (!(ex is ArgumentException) && !(ex is InvalidOperationException) && !(ex is DecoderFallbackException)) throw;
        throw new IOException("保存済みスキル " + id + " の v" + version + " を読めません。古い版へ自動で戻さず、ファイルを保全してください。");
      }
    }
    public List<NativeSkillDocument> List() {
      NotLink(root);
      if (!Directory.Exists(root)) return new List<NativeSkillDocument>();
      var result = new List<NativeSkillDocument>();
      foreach (var directory in Directory.GetDirectories(root)) {
        var id = Path.GetFileName(directory);
        if (!NativeSkillFormat.IsId(id)) continue;
        if (Revisions(directory).Count > 0) result.Add(Load(id));
      }
      return result.OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }
    public NativeSkillDocument Save(NativeSkillDocument draft) {
      // Copy before touching disk, so concurrent editor mutation cannot add executable
      // fields after validation or change which revision the writer publishes.
      var skill = Parse(Serialize(draft));
      lock (sync) {
        NotLink(root); Directory.CreateDirectory(root);
        var directory = DirectoryFor(skill.Id); NotLink(directory); Directory.CreateDirectory(directory);
        var lockFile = Path.Combine(directory, ".write.lock"); NotLink(lockFile);
        FileStream lease;
        try { lease = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("別の画面が保存中です。現在版を読み直してから保存してください。"); }
        using (lease) {
          var revisions = Revisions(directory);
          int currentVersion = revisions.Count == 0 ? 0 : revisions[revisions.Count - 1];
          if (currentVersion > 0) Load(skill.Id); // A corrupt newest version blocks writes too.
          if (skill.Version != currentVersion) throw new InvalidOperationException("別の画面で更新されています。最新版を読み直してください。");
          if (currentVersion == Int32.MaxValue) throw new InvalidOperationException("版数の上限です。");
          skill.Version++; skill.UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
          var bytes = new UTF8Encoding(false).GetBytes(Serialize(skill) + "\n");
          if (bytes.Length > NativeSkillFormat.MaxJsonBytes) throw new ArgumentException("保存データが大きすぎます。");
          var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
          var file = Path.Combine(directory, "v" + skill.Version.ToString(CultureInfo.InvariantCulture) + ".json");
          try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
              stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            // Same-directory rename publishes a fully written file and refuses an
            // existing destination. Neither old revisions nor corrupt data are replaced.
            File.Move(temporary, file);
          } finally { if (File.Exists(temporary)) File.Delete(temporary); }
          return skill;
        }
      }
    }

    public static NativeSkillDocument ImportLegacyForReview(string json) {
      var data = NativeSkillFormat.JsonObject(json);
      if (NativeSkillFormat.Integer(data, "schemaVersion", true) != 1) throw new ArgumentException("移行対象は旧schemaVersion 1だけです。");
      var draft = new NativeSkillDocument { Origin = "legacy-review", Name = "旧Windows教示の確認候補", Goal = "旧記録を現在画面で確認して教え直す" };
      object status;
      if (data.TryGetValue("status", out status) && Object.Equals(status, "evidence-only-not-executable")) {
        // Lab records may contain old screen positions and process IDs. None of the
        // evidence object is copied into this executable schema or guessed into steps.
        draft.Unresolved.Add("旧教示は実行不能の証拠です。対象を現在画面で選び直し、操作前後と完了を教えてください。");
        return draft;
      }
      object app;
      if (!data.TryGetValue("app", out app) || !Object.Equals(app, "fictional-requests")) throw new ArgumentException("対応するWeb v1またはWindows教示証拠ではありません。");
      draft.Name = NativeSkillFormat.String(data, "name", 120, true);
      draft.Goal = NativeSkillFormat.String(data, "purpose", 2000, true);
      draft.Requests = NativeSkillFormat.Strings(data, "examples", 20, 2000);
      draft.Unresolved.Add("Web v1のDOM画面IDをWindowsのUIA成功条件に変換できません。各対象・アプリ・前後画面を現在画面で教え直してください。");
      // Preserve useful meanings, but never promote DOM screens or recorded clicks
      // into verified Windows results. Every imported invoke still lacks its cues.
      var targets = NativeSkillFormat.Array(data, "targets", 300).Cast<object>().Select(NativeSkillFormat.Object).ToList();
      foreach (var rawStep in NativeSkillFormat.Array(data, "steps", 80)) {
        var step = NativeSkillFormat.Object(rawStep);
        var key = NativeSkillFormat.String(step, "target", 100, true);
        var matches = targets.Where(t => NativeSkillFormat.String(t, "key", 100, true) == key).ToList();
        if (matches.Count != 1) throw new ArgumentException("旧スキルの対象参照が不正です。");
        var action = NativeSkillFormat.String(step, "action", 20, true);
        if (action != "click" && action != "type") throw new ArgumentException("旧スキルの操作が未対応です。");
        var target = matches[0];
        var imported = new NativeSkillStep { App = "imported-app", Target = NativeSkillFormat.String(target, "label", 200, true),
          Role = action == "type" ? "Edit" : "Button", Context = "", Kind = action == "type" ? "set-value" : "invoke",
          Description = NativeSkillFormat.String(target, "meaning", 2000, false) };
        if (String.IsNullOrEmpty(imported.Description)) imported.Description = "「" + imported.Target + "」を教え直す";
        if (action == "type") imported.InputParameter = NativeSkillFormat.String(step, "parameter", 64, true);
        draft.Steps.Add(imported);
      }
      NativeSkillFormat.Validate(draft); return draft;
    }
  }

  static class NativeSkillFormat {
    public const int MaxJsonBytes = 524288;
    static readonly string[] Roles = { "Button", "Calendar", "CheckBox", "ComboBox", "Custom", "DataGrid", "DataItem", "Document", "Edit", "Group", "Header", "HeaderItem", "Hyperlink", "Image", "List", "ListItem", "Menu", "MenuBar", "MenuItem", "Pane", "ProgressBar", "RadioButton", "ScrollBar", "Separator", "Slider", "Spinner", "SplitButton", "StatusBar", "Tab", "TabItem", "Table", "Text", "Thumb", "TitleBar", "ToolBar", "ToolTip", "Tree", "TreeItem", "Window" };
    static readonly Regex Secrets = new Regex(@"(?:password|passwd|api[ _-]?key|authorization|secret|パスワード|暗証番号|認証コード|秘密鍵|APIキー)\s*[=:：は]\s*\S+|\bbearer\s+\S+|\b(?:sk-[\w-]{10,}|gh[pousr]_[\w]{10,})\b|-----BEGIN .*PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static void Require(bool ok, string message) { if (!ok) throw new ArgumentException(message); }
    public static string Text(string value, int max, bool required) {
      Require(value != null && value.Length <= max, "文字列の形式または文字数を確認してください。");
      Require(!value.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'), "制御文字を意味記述へ保存できません。");
      Require(!Secrets.IsMatch(value), "認証情報らしい文字列は保存できません。実値を除いて説明してください。");
      var result = value.Trim(); Require(!required || result.Length > 0, "空の必須項目があります。");
      // UIA Name/Context/AutomationId are exact predicates: normalizing or trimming
      // them while saving could select a different control after reopening a lesson.
      return value;
    }
    public static bool IsId(string value) { Guid id; return value != null && Guid.TryParseExact(value, "D", out id) && value == id.ToString("D"); }
    public static void Id(string value) { Require(IsId(value), "スキルIDは正規のUUIDで指定してください。"); }
    static void Alias(string value) {
      Text(value, 64, true);
      Require(Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_-]{0,63}$"), "アプリ名は英字で始まる64文字以内の英数字・_・-で指定してください。");
      Require(!System.String.Equals(value, "navigator", StringComparison.OrdinalIgnoreCase) && !System.String.Equals(value, "outside", StringComparison.OrdinalIgnoreCase), "navigatorとoutsideは案内システムの予約名です。別のアプリ名を指定してください。");
    }
    static void Role(string value) { Require(Roles.Contains(value), "UIAの役割名が未対応です。"); }
    public static void Validate(NativeSkillDocument s) {
      Require(s != null, "スキルが必要です。"); Id(s.Id);
      Require(s.Version >= 0, "版番号が不正です。");
      Text(s.Name, 120, true); Text(s.Goal, 2000, true); Text(s.UpdatedAt, 64, true);
      DateTime stamp; Require(DateTime.TryParse(s.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp), "更新時刻が不正です。");
      Require(new[] { "local-teaching", "manual-editor", "legacy-review" }.Contains(s.Origin), "スキルの由来が未対応です。");
      Require(s.Requests != null && s.Requests.Count <= 20 && s.Unresolved != null && s.Unresolved.Count <= 100, "依頼例・未解決事項が多すぎます。");
      foreach (var q in s.Requests) Text(q, 2000, true);
      foreach (var issue in s.Unresolved) Text(issue, 2000, true);
      Require(s.Steps != null && s.Steps.Count <= 80, "手順は80個以内にしてください。");
      foreach (var step in s.Steps) {
        Require(step != null, "空の手順があります。"); Alias(step.App); Text(step.Target, 500, true); Role(step.Role);
        Text(step.Context, 500, false); Text(step.AutomationId, 500, false); Text(step.Description, 2000, true);
        Require(step.Kind == "invoke" || step.Kind == "set-value", "対応操作はinvokeとset-valueです。入力と送信を分けてください。");
        Text(step.InputParameter, 64, false);
        if (step.Kind == "invoke") Require(step.InputParameter.Length == 0, "クリックに入力値パラメータは指定できません。");
        else Require(Regex.IsMatch(step.InputParameter, @"^[A-Za-z][A-Za-z0-9_-]{0,63}$"), "入力パラメータは実値でなく英数字の識別名を指定してください。");
        Text(step.Result, 500, false); Text(step.ResultApp, 120, false); if (step.ResultApp.Length > 0) Alias(step.ResultApp);
        Role(step.ResultRole); Text(step.ResultContext, 500, false); Text(step.ResultAutomationId, 500, false);
        Text(step.Precondition, 500, false); Role(step.PreconditionRole); Text(step.PreconditionContext, 500, false); Text(step.PreconditionAutomationId, 500, false);
        if (step.Kind == "invoke" && step.Result.Length > 0 && (step.ResultApp.Length == 0 || step.ResultApp == step.App)) {
          Require(!Overlaps(step.Result, step.ResultRole, step.ResultContext, step.ResultAutomationId, step.Target, step.Role, step.Context, step.AutomationId), "操作対象そのものをクリックの結果条件にはできません。");
          Require(!Overlaps(step.Result, step.ResultRole, step.ResultContext, step.ResultAutomationId, step.Precondition, step.PreconditionRole, step.PreconditionContext, step.PreconditionAutomationId), "操作前に存在する同じ目印を、操作成功の証拠にはできません。");
        }
      }
    }
    static bool Overlaps(string name, string role, string context, string id, string otherName, string otherRole, string otherContext, string otherId) {
      return name == otherName && role == otherRole && (context.Length == 0 || otherContext.Length == 0 || context == otherContext) && (id.Length == 0 || otherId.Length == 0 || id == otherId);
    }
    public static List<string> Issues(NativeSkillDocument s) {
      var issues = new List<string>(s.Unresolved);
      if (!s.Reviewed) issues.Add("手順全体と最終結果を確認してから、案内を許可してください。");
      if (s.Steps.Count == 0) issues.Add("操作手順がまだありません。");
      for (int i = 0; i < s.Steps.Count; i++) {
        var step = s.Steps[i];
        if (System.String.IsNullOrEmpty(step.Precondition)) issues.Add("手順 " + (i + 1) + " の操作前の画面目印を指定してください。");
        if (step.Kind == "invoke" && System.String.IsNullOrEmpty(step.Result)) issues.Add("手順 " + (i + 1) + " の操作後の一意な目印を指定してください。");
      }
      return issues.Distinct(StringComparer.Ordinal).ToList();
    }
    public static Dictionary<string, object> Object(object value) {
      var result = value as Dictionary<string, object>; Require(result != null, "JSONオブジェクトが必要です。"); return result;
    }
    static void Exact(Dictionary<string, object> data, string keys) {
      var allowed = new HashSet<string>(keys.Split(','), StringComparer.Ordinal);
      Require(data.Keys.All(allowed.Contains), "未対応の項目があります。座標・実値・PID・ハンドル等は保存できません。");
    }
    public static string String(Dictionary<string, object> data, string key, int max, bool required) {
      object raw;
      if (!data.TryGetValue(key, out raw)) { Require(!required, "必須項目がありません: " + key); return ""; }
      Require(raw is string, "文字列が必要です: " + key); return Text((string)raw, max, required);
    }
    public static int Integer(Dictionary<string, object> data, string key, bool required) {
      object raw;
      if (!data.TryGetValue(key, out raw)) { Require(!required, "必須項目がありません: " + key); return 0; }
      Require(raw is int, "整数が必要です: " + key); return (int)raw;
    }
    static bool Boolean(Dictionary<string, object> data, string key) {
      object raw; if (!data.TryGetValue(key, out raw)) return false;
      Require(raw is bool, "真偽値が必要です: " + key); return (bool)raw;
    }
    public static object[] Array(Dictionary<string, object> data, string key, int max) {
      object raw; Require(data.TryGetValue(key, out raw), "配列が必要です: " + key);
      var array = raw as object[]; Require(array != null && array.Length <= max, "配列の形式または件数が不正です: " + key); return array;
    }
    public static List<string> Strings(Dictionary<string, object> data, string key, int max, int length) {
      return Array(data, key, max).Select(v => { Require(v is string, "配列の要素は文字列で指定してください。"); return Text((string)v, length, true); }).ToList();
    }
    static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes, RecursionLimit = 32 }; }
    public static Dictionary<string, object> JsonObject(string json) {
      Require(json != null && json.Length <= MaxJsonBytes && Encoding.UTF8.GetByteCount(json) <= MaxJsonBytes, "JSONが大きすぎるか未指定です。");
      // JavaScriptSerializer silently accepts duplicate property names. Check the
      // original token stream so an editor cannot hide a second action/permission.
      new UniqueJsonKeys(json).Check();
      return Object(Serializer().DeserializeObject(json));
    }
    public static NativeSkillDocument Parse(string json) {
      var d = JsonObject(json);
      Exact(d, "schemaVersion,platform,id,version,name,goal,requests,steps,unresolved,reviewed,origin,updatedAt");
      Require(Integer(d, "schemaVersion", true) == 2 && String(d, "platform", 40, true) == "windows-uia", "Windows意味スキルv2が必要です。旧版は明示的な移行確認を使ってください。");
      var s = new NativeSkillDocument { Id = String(d, "id", 36, true), Version = Integer(d, "version", true), Name = String(d, "name", 120, true), Goal = String(d, "goal", 2000, true),
        Reviewed = Boolean(d, "reviewed"), Origin = String(d, "origin", 40, true), UpdatedAt = String(d, "updatedAt", 64, true),
        Requests = Strings(d, "requests", 20, 2000), Unresolved = Strings(d, "unresolved", 100, 2000) };
      foreach (var item in Array(d, "steps", 80)) s.Steps.Add(ParseStep(Object(item)));
      Validate(s); return s;
    }
    static NativeSkillStep ParseStep(Dictionary<string, object> d) {
      Exact(d, "app,target,role,context,automationId,kind,description,inputParameter,result,resultApp,resultRole,resultContext,resultAutomationId,precondition,preconditionRole,preconditionContext,preconditionAutomationId,allowResume");
      return new NativeSkillStep { App = String(d, "app", 120, true), Target = String(d, "target", 500, true), Role = String(d, "role", 40, true), Context = String(d, "context", 500, false), AutomationId = String(d, "automationId", 500, false),
        Kind = String(d, "kind", 20, true), Description = String(d, "description", 2000, true), InputParameter = String(d, "inputParameter", 64, false),
        Result = String(d, "result", 500, false), ResultApp = String(d, "resultApp", 120, false), ResultRole = String(d, "resultRole", 40, true), ResultContext = String(d, "resultContext", 500, false), ResultAutomationId = String(d, "resultAutomationId", 500, false),
        Precondition = String(d, "precondition", 500, false), PreconditionRole = String(d, "preconditionRole", 40, true), PreconditionContext = String(d, "preconditionContext", 500, false), PreconditionAutomationId = String(d, "preconditionAutomationId", 500, false),
        AllowResume = Boolean(d, "allowResume") };
    }
    static object StepObject(NativeSkillStep s) {
      return new { app = s.App, target = s.Target, role = s.Role, context = s.Context, automationId = s.AutomationId, kind = s.Kind, description = s.Description, inputParameter = s.InputParameter,
        result = s.Result, resultApp = s.ResultApp, resultRole = s.ResultRole, resultContext = s.ResultContext, resultAutomationId = s.ResultAutomationId,
        precondition = s.Precondition, preconditionRole = s.PreconditionRole, preconditionContext = s.PreconditionContext, preconditionAutomationId = s.PreconditionAutomationId, allowResume = s.AllowResume };
    }
    public static NativeSkillStep CloneStep(NativeSkillStep s) {
      return new NativeSkillStep { App = s.App, Target = s.Target, Role = s.Role, Context = s.Context, AutomationId = s.AutomationId,
        Kind = s.Kind, Description = s.Description, InputParameter = s.InputParameter, Result = s.Result, ResultApp = s.ResultApp,
        ResultRole = s.ResultRole, ResultContext = s.ResultContext, ResultAutomationId = s.ResultAutomationId,
        Precondition = s.Precondition, PreconditionRole = s.PreconditionRole, PreconditionContext = s.PreconditionContext,
        PreconditionAutomationId = s.PreconditionAutomationId, AllowResume = s.AllowResume };
    }
    public static string Serialize(NativeSkillDocument s) {
      Validate(s);
      return Serializer().Serialize(new { schemaVersion = 2, platform = "windows-uia", id = s.Id, version = s.Version, name = s.Name, goal = s.Goal,
        requests = s.Requests, steps = s.Steps.Select(StepObject).ToArray(), unresolved = s.Unresolved, reviewed = s.Reviewed, origin = s.Origin, updatedAt = s.UpdatedAt });
    }

    sealed class UniqueJsonKeys {
      readonly string text;
      int position;
      public UniqueJsonKeys(string text) { this.text = text; }
      void White() { while (position < text.Length && Char.IsWhiteSpace(text[position])) position++; }
      void Need(bool condition) { Require(condition, "JSONの形式が不正です。"); }
      public void Check() { Value(0); White(); Need(position == text.Length); }
      string Quoted() {
        White(); Need(position < text.Length && text[position] == '"'); int start = position++;
        while (position < text.Length) {
          char c = text[position++];
          if (c == '"') return Serializer().Deserialize<string>(text.Substring(start, position - start));
          Need(c >= 32);
          if (c == '\\') {
            Need(position < text.Length); char escape = text[position++];
            Need("\"\\/bfnrtu".IndexOf(escape) >= 0);
            if (escape == 'u') { Need(position + 4 <= text.Length); for (int i = 0; i < 4; i++) Need(Uri.IsHexDigit(text[position++])); }
          }
        }
        throw new ArgumentException("JSON文字列が閉じられていません。");
      }
      void Value(int depth) {
        Need(depth < 32); White(); Need(position < text.Length); char c = text[position];
        if (c == '"') { Quoted(); return; }
        if (c == '{') {
          position++; var keys = new HashSet<string>(StringComparer.Ordinal); White();
          if (position < text.Length && text[position] == '}') { position++; return; }
          while (true) {
            string key = Quoted(); Require(keys.Add(key), "JSON項目が重複しています: " + key); White();
            Need(position < text.Length && text[position++] == ':'); Value(depth + 1); White(); Need(position < text.Length);
            char next = text[position++]; if (next == '}') return; Need(next == ',');
          }
        }
        if (c == '[') {
          position++; White(); if (position < text.Length && text[position] == ']') { position++; return; }
          while (true) { Value(depth + 1); White(); Need(position < text.Length); char next = text[position++]; if (next == ']') return; Need(next == ','); }
        }
        int start = position;
        while (position < text.Length && !Char.IsWhiteSpace(text[position]) && ",]}".IndexOf(text[position]) < 0) position++;
        Need(position > start);
        var token = text.Substring(start, position - start);
        Need(token == "true" || token == "false" || token == "null" || Regex.IsMatch(token, @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"));
      }
    }
  }
}
