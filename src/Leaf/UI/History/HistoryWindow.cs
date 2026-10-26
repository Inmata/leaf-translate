using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Leaf
{
    public sealed class HistoryWindow
    {
        private readonly AppShell shell;
        public Window Window { get; private set; }
        public HistoryWindow(AppShell owner)
        {
            shell = owner; Window = Ui.Load("History");
            Ui.Get<TextBox>(Window, "HistorySearch").TextChanged += (s, e) => Refresh();
            Ui.Get<ListBox>(Window, "HistoryList").MouseDoubleClick += (s, e) => Open();
            Ui.Click(Window, "OpenHistory", Open);
            Ui.Click(Window, "DeleteHistory", DeleteSelectedAsync);
            Ui.Click(Window, "ClearHistory", ClearAllAsync);
            Refresh();
        }
        private async void DeleteSelectedAsync()
        {
            var record = Ui.Get<ListBox>(Window, "HistoryList").SelectedItem as TranslationRecord;
            if (record == null) return;
            try {
                await shell.Store.DeleteAsync(record.Id);
                shell.Forget(record.Id); Refresh();
            } catch (Exception error) {
                MessageBox.Show(Window, error is UserError ? error.Message : "无法更新本地历史。", "Leaf");
            }
        }
        private async void ClearAllAsync()
        {
            if (MessageBox.Show(Window, "清空全部本地历史？", "Leaf", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try {
                await shell.Store.ClearAsync();
                shell.Forget(null); Refresh();
            } catch (Exception error) {
                MessageBox.Show(Window, error is UserError ? error.Message : "无法更新本地历史。", "Leaf");
            }
        }
        public void Refresh()
        {
            var items = shell.Store.History(Ui.Get<TextBox>(Window, "HistorySearch").Text);
            Ui.Get<ListBox>(Window, "HistoryList").ItemsSource = items;
            if (items.Count > 0) Ui.Get<ListBox>(Window, "HistoryList").SelectedIndex = 0;
            Ui.Get<TextBlock>(Window, "HistoryCount").Text = "最近 " + items.Count + " 条";
            Ui.Visible(Ui.Get<TextBlock>(Window, "HistoryEmpty"), items.Count == 0);
            Ui.Get<Button>(Window, "OpenHistory").IsEnabled = items.Count > 0;
            Ui.Get<Button>(Window, "DeleteHistory").IsEnabled = items.Count > 0;
        }
        private void Open()
        {
            var record = Ui.Get<ListBox>(Window, "HistoryList").SelectedItem as TranslationRecord;
            if (record != null) { shell.OpenRecord(record); Window.Close(); }
        }
    }
}
