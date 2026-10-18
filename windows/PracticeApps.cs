using System;
using System.Drawing;
using System.Windows.Forms;

namespace JevTeachWindows {
  static class PracticeTask {
    public static DesktopTask Create() {
      var t = new DesktopTask { Name = "資料棚で準備し、別の申請アプリで登録して戻る" };
      t.Steps.Add(new TaskStep { App = "shelf", Target = "申請の資料を用意", Result = "資料の準備ができました", Description = "資料棚で、申請の資料を用意する" });
      t.Steps.Add(new TaskStep { App = "request", Target = "新しい申請", Result = "申請内容を入力しました", Description = "申請アプリで、新しい申請を開く" });
      t.Steps.Add(new TaskStep { App = "request", Target = "内容を確認", Result = "確認画面です", Description = "申請内容を確認画面で見る" });
      t.Steps.Add(new TaskStep { App = "request", Target = "練習として登録", Result = "練習の登録が完了しました", Description = "架空の申請を、練習として登録する" });
      t.Steps.Add(new TaskStep { App = "shelf", Target = "作業を完了", Result = "今回の作業は完了です", Description = "資料棚へ戻り、作業を完了する" });
      return t;
    }
  }
  sealed class PracticeApp : Form {
    readonly bool shelf;
    readonly Panel body = new Panel();
    readonly CheckBox moved = new CheckBox(), duplicate = new CheckBox(), hidden = new CheckBox(), fail = new CheckBox(), delayed = new CheckBox();
    int stage;
    bool pending;
    public PracticeApp(bool shelf) {
      this.shelf = shelf;
      Text = shelf ? "Jev 練習・資料棚" : "Jev 練習・申請アプリ";
      AccessibleName = Text; Font = new Font("Yu Gothic UI", 11); AutoScaleMode = AutoScaleMode.Dpi;
      StartPosition = FormStartPosition.Manual; Size = new Size(710, 560); MinimumSize = new Size(540, 480);
      var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Left + (shelf ? 25 : 60), area.Top + (shelf ? 50 : 85));
      BackColor = Color.FromArgb(247, 248, 244);
      var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 105, Padding = new Padding(18, 8, 10, 8), WrapContents = true };
      var label = new Label { Text = "画面の変化を試す", AutoSize = true, Width = 180 };
      controls.Controls.Add(label);
      Configure(moved, "配置を変える", controls); Configure(duplicate, "同名を増やす", controls);
      Configure(hidden, "対象を隠す", controls); Configure(fail, "遷移しない", controls); Configure(delayed, "1秒遅らせる", controls);
      Controls.Add(body); Controls.Add(controls); body.Dock = DockStyle.Fill; body.Padding = new Padding(28);
      moved.CheckedChanged += (s,e) => Render(); duplicate.CheckedChanged += (s,e) => Render(); hidden.CheckedChanged += (s,e) => Render();
      Resize += (s,e) => Render(); Render();
    }
    void Configure(CheckBox box, string text, Control parent) { box.Text = text; box.AccessibleName = text; box.AutoSize = true; box.Margin = new Padding(8); parent.Controls.Add(box); }
    void TextAt(string text, int top, int height, float size) {
      body.Controls.Add(new Label { Text = text, AccessibleName = text, Left = 28, Top = top, Width = Math.Max(400, body.ClientSize.Width - 56), Height = height, Font = new Font(Font.FontFamily, size) });
    }
    void Render() {
      body.Controls.Clear();
      TextAt(shelf ? "資料棚" : "備品の申請", 28, 48, 22);
      TextAt("練習専用 · 実データ・外部送信はありません", 86, 40, 10);
      string state = shelf ? (stage == 0 ? "申請に使う資料を準備します" : stage == 1 ? "資料の準備ができました" : "今回の作業は完了です") :
        (stage == 0 ? "新しい申請を始めます" : stage == 1 ? "申請内容を入力しました" : stage == 2 ? "確認画面です" : "練習の登録が完了しました");
      TextAt(state, 145, 50, 15);
      if (!shelf && stage > 0) TextAt("件名: 練習用の備品購入 / 数量: 1", 200, 40, 11);
      string target = shelf ? (stage == 0 ? "申請の資料を用意" : stage == 1 ? "作業を完了" : null) :
        (stage == 0 ? "新しい申請" : stage == 1 ? "内容を確認" : stage == 2 ? "練習として登録" : null);
      if (target == null || hidden.Checked) return;
      int left = moved.Checked ? 35 : Math.Max(280, body.ClientSize.Width - 280);
      AddAction(target, left, 272);
      if (duplicate.Checked) AddAction(target, left, 328);
    }
    void AddAction(string text, int left, int top) {
      var b = new Button { Text = text, AccessibleName = text, Name = "action-" + stage, Left = left, Top = top, Width = 225, Height = 46,
        BackColor = Color.FromArgb(30, 92, 88), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Enabled = !pending };
      b.Click += async (s,e) => {
        if (pending || fail.Checked) return;
        pending = true; Render();
        if (delayed.Checked) await System.Threading.Tasks.Task.Delay(1000);
        if (IsDisposed) return; stage++; pending = false; Render();
      };
      body.Controls.Add(b);
    }
  }
}
