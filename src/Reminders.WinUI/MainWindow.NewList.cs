using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private const int ListNameByteLimit = 255;

    private async Task ShowNewListAsync()
    {
        if (_dialogOpen || _accountBusy || _savingDetail || AuthGate.Visibility != Visibility.Collapsed
            || SetupGuide.Visibility == Visibility.Visible || !await ConfirmLeaveDetailAsync()) return;

        var accountEpoch = _accountEpoch;
        _dialogOpen = true;
        ReminderList? created = null;
        try
        {
            var name = new TextBox { Header = L10n.Get("NewListName"), MaxLength = ListNameByteLimit };
            var picker = new ListAppearancePicker(L10n.Get("NewList"));
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(name);
            panel.Children.Add(picker.View);
            panel.Children.Add(error);
            var dialog = MakeDialog(L10n.Get("NewList"), panel, L10n.Get("CreateList"));
            var requestId = Guid.NewGuid().ToString("D");
            var creating = false;

            void UpdateForm()
            {
                var title = name.Text.Trim();
                var tooLong = Encoding.UTF8.GetByteCount(title) > ListNameByteLimit;
                dialog.IsPrimaryButtonEnabled = !creating && title.Length > 0 && !tooLong && picker.IsValid;
                picker.Title = title.Length == 0 ? L10n.Get("NewList") : title;
                if (!creating)
                {
                    error.Message = tooLong ? L10n.Get("NewListNameTooLong") : "";
                    error.IsOpen = tooLong;
                    error.Visibility = tooLong ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            name.TextChanged += (_, _) => UpdateForm();
            picker.Changed += (_, _) => UpdateForm();
            dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
            dialog.Closing += (_, args) => { if (creating) args.Cancel = true; };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                if (creating || !dialog.IsPrimaryButtonEnabled) { args.Cancel = true; return; }
                var deferral = args.GetDeferral();
                creating = true;
                name.IsEnabled = picker.View.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    var title = name.Text.Trim();
                    var color = picker.Color;
                    var badge = picker.Badge;
                    created = _demo
                        ? new ReminderList { Id = "preview-list/" + requestId, Title = title, ColorHex = color, BadgeEmblem = badge }
                        : await _sync.CallAsync<ReminderList>("create_list", new
                        {
                            title, color_hex = color, badge_emblem = badge, request_id = requestId
                        });
                }
                catch (Exception failure)
                {
                    args.Cancel = true;
                    error.Message = L10n.Error(failure);
                    error.IsOpen = true;
                    error.Visibility = Visibility.Visible;
                }
                finally
                {
                    creating = false;
                    name.IsEnabled = picker.View.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };
            UpdateForm();
            await dialog.ShowAsync();
        }
        catch (Exception failure)
        {
            ShowInfo(L10n.Get("CouldntCreateList"), L10n.Error(failure), InfoBarSeverity.Error);
        }
        finally { _dialogOpen = false; }

        if (created is null || _windowClosed || _accountBusy || accountEpoch != _accountEpoch
            || AuthGate.Visibility != Visibility.Collapsed) return;
        _navigationLoads.Invalidate();
        _reminderLoads.Invalidate();
        _lists.RemoveAll(list => list.Id == created.Id);
        _lists.Add(created);
        _view = new(created.Title, "", NavKind.List, created.Id);
        ViewTitle.Text = created.Title;
        ViewSubtitle.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        ShowDetail(null);
        RebuildListNavigation(created.Id);
        await LoadRemindersAsync();
    }
}
