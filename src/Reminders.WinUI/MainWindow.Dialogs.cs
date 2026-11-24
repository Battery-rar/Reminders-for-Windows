using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Reminders.Windows.Services;
using System.Globalization;
using System.Text.Json;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private static readonly (string Name, string Glyph)[] ListIcons =
    [ (L10n.Get("List"), "\uE8FD"), (L10n.Get("Inbox"), "\uE715"), (L10n.Get("Work"), "\uE821"), (L10n.Get("Home"), "\uE80F"),
      (L10n.Get("School"), "\uE7BE"), (L10n.Get("Shopping"), "\uE7BF"), (L10n.Get("Travel"), "\uE709"), (L10n.Get("Heart"), "\uEB51"), (L10n.Get("Star"), "\uE734") ];
    private static readonly (string Name, string Hex)[] ListColors =
    [ (L10n.Get("Blue"), "#0078D4"), (L10n.Get("Red"), "#D13438"), (L10n.Get("Orange"), "#CA5010"), (L10n.Get("Green"), "#107C10"),
      (L10n.Get("Purple"), "#8764B8"), (L10n.Get("Pink"), "#C239B3"), (L10n.Get("Gray"), "#767676") ];

    private void RestoreNavigationSelection()
    {
        _restoringNavigation = true;
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == $"{_view.Kind.ToString().ToLowerInvariant()}:{_view.Key}"
                || (_view.Kind == NavKind.Smart && item.Tag?.ToString() == $"smart:{_view.Key}"));
        _restoringNavigation = false;
    }

    private async void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.IsSettingsInvoked) { RestoreNavigationSelection(); await ShowSettingsAsync(); }
            else if (args.InvokedItemContainer?.Tag?.ToString() == "create-list")
            {
                RestoreNavigationSelection();
                await ShowNewListAsync();
            }
            else if (args.InvokedItemContainer?.Tag?.ToString() == "sync")
            {
                RestoreNavigationSelection();
                if (_demo) ShowInfo(L10n.Get("DemoMode"), L10n.Get("DemoSyncUnavailable"), InfoBarSeverity.Informational);
                else await RequestAutoSyncAsync(manual: true);
            }
            else if (args.InvokedItemContainer?.Tag?.ToString() == "conflicts")
            {
                RestoreNavigationSelection();
                if (await ConfirmLeaveDetailAsync()) await ResolveConflictsAsync();
            }
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntCompleteAction"), L10n.Error(error), InfoBarSeverity.Error); }
    }

    private string SidebarSort => _uiPreferences.ListSort is SidebarPresentation.NameAscending or SidebarPresentation.NameDescending
        ? _uiPreferences.ListSort : SidebarPresentation.Manual;

    private ComboBox CreateSidebarSortPicker()
    {
        var order = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var choices = new[] { SidebarPresentation.Manual, SidebarPresentation.NameAscending, SidebarPresentation.NameDescending };
        foreach (var (mode, label) in choices.Zip(new[] { "SidebarSortManual", "SidebarSortAscending", "SidebarSortDescending" }))
            order.Items.Add(new ComboBoxItem { Content = L10n.Get(label), Tag = mode });
        order.SelectedIndex = Array.IndexOf(choices, SidebarSort);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(order, L10n.Get("ListOrder"));
        return order;
    }

    private List<ReminderList> OrderedLists()
    {
        var saved = _settings.Property("list_order");
        var order = saved.ValueKind == JsonValueKind.Array && saved.GetArrayLength() > 0
            ? saved.EnumerateArray().Select(v => v.GetString() ?? "").ToList() : _uiPreferences.ListOrder;
        return SidebarPresentation.Order(_lists.Where(list => !list.IsGroup), SidebarSort, order, list => list.Id, list => list.Title).ToList();
    }

    private static Brush ColorBrush(string? hex)
    {
        if (hex is not null && hex.StartsWith('#') && hex.Length == 7 && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    }

    private static FontIcon MakeSidebarIcon(string? glyph, string? color)
    {
        var icon = SidebarPresentation.ResolveIcon(glyph);
        if (icon.IsEmoji)
            return new FontIcon { Glyph = icon.Glyph, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 18 };
        var result = new FontIcon { Glyph = icon.Glyph, FontSize = 18 };
        if (!new AccessibilitySettings().HighContrast) result.Foreground = ColorBrush(color);
        return result;
    }

    private void RebuildListNavigation(string? focusListId = null)
    {
        ClearSidebarDropTarget();
        _restoringNavigation = true;
        while (Navigation.MenuItems.Count > 7) Navigation.MenuItems.RemoveAt(7);
        var ordered = OrderedLists();
        NavigationViewItem? focusItem = null;
        foreach (var list in ordered)
        {
            _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
            var icon = MakeSidebarIcon(appearance?.Glyph ?? list.BadgeEmblem, appearance?.Color ?? list.ColorHex);
            var item = CreateNavigationItem(list.Title, icon, $"list:{list.Id}");
            AttachReminderDropTarget(item);
            var menu = new MenuFlyout();
            var customize = new MenuFlyoutItem { Text = L10n.Get("ChangeIconAndColor") };
            customize.Click += async (_, _) => await CustomizeListAsync(list);
            menu.Items.Add(customize);
            if (SidebarSort == SidebarPresentation.Manual)
            {
                var index = ordered.IndexOf(list);
                var up = new MenuFlyoutItem { Text = L10n.Get("MoveUp"), IsEnabled = index > 0 };
                up.Click += async (_, _) => await MoveListAsync(list.Id, -1);
                var down = new MenuFlyoutItem { Text = L10n.Get("MoveDown"), IsEnabled = index < ordered.Count - 1 };
                down.Click += async (_, _) => await MoveListAsync(list.Id, 1);
                menu.Items.Add(up); menu.Items.Add(down);
            }
            item.ContextFlyout = menu;
            Navigation.MenuItems.Add(item);
            if (list.Id == focusListId) focusItem = item;
        }
        if (_tags.Count > 0)
        {
            Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = L10n.Get("Tags") });
            foreach (var tag in _tags) Navigation.MenuItems.Add(CreateNavigationItem("#" + tag, new FontIcon { Glyph = "\uE8EC" }, $"tag:{tag}"));
        }
        _restoringNavigation = false;
        RestoreNavigationSelection();
        if (focusItem is not null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_windowClosed && Navigation.MenuItems.Contains(focusItem)) focusItem.Focus(FocusState.Programmatic);
            });
    }

    private void SaveUiPreferences()
    {
        if (_demo) return;
        _uiPreferences.NavigationPaneOpen = _panePinnedOpen;
        UiPreferences.Save(_uiPreferences, localDemo: _sync.IsLocalDemo);
    }

    private async Task MoveListAsync(string id, int direction)
    {
        if (!CanReorderSidebar) return;
        var order = OrderedLists().Select(list => list.Id).ToList();
        var index = order.IndexOf(id); var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        await SaveListOrderAsync(id, order);
    }

    private async Task<bool> SaveListOrderAsync(string id, List<string> order)
    {
        if (!CanReorderSidebar) return false;
        _sidebarOrderBusy = true;
        var accountEpoch = _accountEpoch;
        try
        {
            if (!_demo)
            {
                var settings = await _sync.CallAsync("set_settings", new { list_order = order });
                if (_windowClosed || accountEpoch != _accountEpoch) return false;
                _settings = settings;
            }
            _uiPreferences.ListOrder = order; SaveUiPreferences(); RebuildListNavigation(id);
            return true;
        }
        catch (Exception error)
        {
            if (!_windowClosed && accountEpoch == _accountEpoch)
                ShowInfo(L10n.Get("CouldntSaveListOrder"), L10n.Error(error), InfoBarSeverity.Error);
            return false;
        }
        finally { _sidebarOrderBusy = false; }
    }

    private async Task CustomizeListAsync(ReminderList list)
    {
        if (_dialogOpen || _savingDetail) return;
        _dialogOpen = true;
        try
        {
            _uiPreferences.Lists.TryGetValue(list.Id, out var appearance);
            const string custom = "custom";
            var icons = new ComboBox { Header = L10n.Get("Icon"), HorizontalAlignment = HorizontalAlignment.Stretch };
            icons.Items.Add(new ComboBoxItem { Content = L10n.Get("UseICloudListIcon"), Tag = "" });
            foreach (var icon in ListIcons)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon { Glyph = icon.Glyph, FontSize = 18 }); row.Children.Add(new TextBlock { Text = icon.Name });
                icons.Items.Add(new ComboBoxItem { Content = row, Tag = icon.Glyph });
            }
            icons.Items.Add(new ComboBoxItem { Content = L10n.Get("CustomListSymbol"), Tag = custom });
            var preset = Array.FindIndex(ListIcons, icon => icon.Glyph == appearance?.Glyph);
            icons.SelectedIndex = appearance?.Glyph is null ? 0 : preset >= 0 ? preset + 1 : icons.Items.Count - 1;
            var customIcon = new TextBox { Header = L10n.Get("CustomListSymbol"), PlaceholderText = L10n.Get("ListSymbolPlaceholder"),
                MaxLength = 64, Text = icons.SelectedIndex == icons.Items.Count - 1 ? appearance?.Glyph ?? "" : "" };
            var iconPreview = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center };
            var fallback = new TextBlock { Text = L10n.Get("ListSymbolFallback"), TextWrapping = TextWrapping.Wrap };
            var colors = new ComboBox { Header = L10n.Get("Color"), HorizontalAlignment = HorizontalAlignment.Stretch };
            colors.Items.Add(new ComboBoxItem { Content = L10n.Get("UseICloudListColor"), Tag = "" });
            foreach (var color in ListColors)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon { Glyph = "\uEA3B", Foreground = ColorBrush(color.Hex), FontSize = 18 }); row.Children.Add(new TextBlock { Text = color.Name });
                colors.Items.Add(new ComboBoxItem { Content = row, Tag = color.Hex });
            }
            colors.SelectedIndex = Array.FindIndex(ListColors, color => color.Hex == appearance?.Color) + 1;
            var panel = new StackPanel { Spacing = 16, MinWidth = 320 };
            panel.Children.Add(iconPreview); panel.Children.Add(icons); panel.Children.Add(customIcon); panel.Children.Add(fallback); panel.Children.Add(colors);
            panel.Children.Add(new TextBlock { Text = L10n.Get("ListAppearanceStorageDescription"), TextWrapping = TextWrapping.Wrap });
            string? SelectedGlyph() => (icons.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                custom => customIcon.Text.Trim(),
                "" => null,
                var selected => selected
            };
            string? SelectedColor() => (colors.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } color ? color : null;
            void UpdatePreview()
            {
                var customSelected = (icons.SelectedItem as ComboBoxItem)?.Tag?.ToString() == custom;
                var glyph = SelectedGlyph();
                customIcon.Visibility = customSelected ? Visibility.Visible : Visibility.Collapsed;
                fallback.Visibility = customSelected && glyph is { Length: > 0 } && SidebarPresentation.ResolveIcon(glyph).IsFallback
                    ? Visibility.Visible : Visibility.Collapsed;
                iconPreview.Content = MakeSidebarIcon(glyph ?? list.BadgeEmblem, SelectedColor() ?? list.ColorHex);
            }
            icons.SelectionChanged += (_, _) => UpdatePreview();
            colors.SelectionChanged += (_, _) => UpdatePreview();
            customIcon.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();
            var dialog = MakeDialog(list.Title, panel, L10n.Get("Save"));
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var glyph = SelectedGlyph();
            var pickedColor = SelectedColor();
            if (string.IsNullOrWhiteSpace(glyph)) glyph = null;
            if (glyph is null && pickedColor is null) _uiPreferences.Lists.Remove(list.Id);
            else _uiPreferences.Lists[list.Id] = new() { Glyph = glyph, Color = pickedColor };
            SaveUiPreferences(); RebuildListNavigation(list.Id);
        }
        catch (Exception error) { ShowInfo(L10n.Get("CouldntCustomizeList"), L10n.Error(error), InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private ContentDialog MakeDialog(string title, StackPanel panel, string action) => new()
    {
        XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = title,
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 },
        PrimaryButtonText = action, CloseButtonText = L10n.Get("Cancel"), DefaultButton = ContentDialogButton.Primary
    };

    private async Task AnimateCompletionAsync(ReminderItem reminder)
    {
        if (!_uiPreferences.CompletionAnimation || !new UISettings().AnimationsEnabled) return;
        if (IncludesCompleted || _view.Kind == NavKind.Smart && _view.Key == "completed") { await Task.Delay(600); return; }
        var container = ReminderList.ContainerFromItem(reminder) as UIElement;
        if (container is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(container);
        try
        {
            // Leave time to see the check mark before gently fading the row.
            await Task.Delay(180);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 1); fade.InsertKeyFrame(1, 0.35f); fade.Duration = TimeSpan.FromMilliseconds(420);
            visual.StartAnimation("Opacity", fade);
            await Task.Delay(420);
        }
        finally { visual.StopAnimation("Opacity"); visual.Opacity = 1; }
    }

    private void LoadDemoRows(bool fromSync = false, bool preserveDetail = false)
    {
        foreach (var row in _demoRows) row.ContextOnly = false;
        IEnumerable<ReminderItem> rows = _demoRows.Where(row => !row.Deleted);
        if (_view.Kind == NavKind.List) rows = rows.Where(row => row.ListId == _view.Key);
        else if (_view.Kind == NavKind.Smart)
        {
            if (_view.Key == "today") rows = rows.Where(row => DateTimeOffset.TryParse(row.DueDate, out var due) && (row.AllDay ? due.Date : due.LocalDateTime.Date) <= DateTime.Today);
            else if (_view.Key == "upcoming") rows = rows.Where(row => row.DueDate is not null);
            else if (_view.Key == "completed") rows = rows.Where(row => row.Completed);
            else if (_view.Key == "deleted") rows = _demoRows.Where(row => row.Deleted);
        }
        if (!IncludesCompleted && !(_view.Kind == NavKind.Smart && _view.Key is "completed" or "deleted"))
            rows = rows.Where(row => !row.Completed);
        rows = IncludeDemoFamily(rows.ToList());
        if (_view.Kind == NavKind.Tag) rows = rows.Where(row => row.Tags.Contains(_view.Key));
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) rows = rows.Where(row => (row.Title + " " + row.Description).Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        rows = rows.ToList();
        if (_view.Kind == NavKind.Tag || !string.IsNullOrWhiteSpace(SearchBox.Text))
            foreach (var row in rows) row.ContextOnly = false;
        rows = _sort switch { "title" => rows.OrderBy(row => row.Title), "priority" => rows.OrderBy(row => row.Priority == 0 ? 10 : row.Priority), _ => rows.OrderBy(row => row.DueDate is null).ThenBy(row => row.DueDate) };
        SetReminderRows(IncludeDemoFamily(rows.ToList()), fromSync, preserveDetail);
        SetBadge("smart:today", _demoRows.Count(row => !row.Completed && !row.Deleted && DateTimeOffset.TryParse(row.DueDate, out var due) && (row.AllDay ? due.Date : due.LocalDateTime.Date) <= DateTime.Today));
    }
}
