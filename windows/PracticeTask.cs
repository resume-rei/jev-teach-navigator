using System;

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
}
