using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Reminders.Windows.Services;
using Windows.Graphics.Printing;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private PrintListJob? _printJob;

    private async void PrintList_Click(object sender, RoutedEventArgs e) => await ShowPrintListAsync();

    private async void PrintListAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanPrintList) return;
        args.Handled = true;
        await ShowPrintListAsync();
    }

    private bool CanPrintList => !_windowClosed && !_accountBusy && !_dialogOpen
        && AuthGate.Visibility == Visibility.Collapsed && SetupGuide.Visibility == Visibility.Collapsed;

    private async Task ShowPrintListAsync()
    {
        if (!CanPrintList || _printJob is not null) return;

        PrintListJob? printJob = null;
        try
        {
            if (!PrintManager.IsSupported())
            {
                ShowPrintListError("PrintListUnavailable");
                return;
            }

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            printJob = new PrintListJob(PrintManagerInterop.GetForWindow(handle), PrintCanvas,
                CapturePrintList(), DispatcherQueue, FinishPrintList, ShowPrintListError);
            _printJob = printJob;
            PrintListButton.IsEnabled = false;
            printJob.Register();
            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(handle))
                FinishPrintList(printJob, "PrintListUnavailable");
        }
        catch (Exception)
        {
            if (printJob is null) ShowPrintListError("PrintListUnavailable");
            else FinishPrintList(printJob, "PrintListUnavailable");
        }
    }

    private PrintListSnapshot CapturePrintList()
    {
        // Use the rendered query while a newly requested view or search is loading.
        var search = _renderedQuery?.Search.Trim();
        PrintListSection[] sections = _usingGroupedReminderSource
            ? _reminderSections.Where(section => section.Count > 0)
                .Select(section => new PrintListSection(section.Title, section.Select(CapturePrintRow).ToArray())).ToArray()
            : _visibleReminders.Count > 0 ? [new(null, _visibleReminders.Select(CapturePrintRow).ToArray())] : [];
        return new(_renderedReminderTitle ?? _view.Label, string.IsNullOrEmpty(search) ? null : L10n.Format("PrintListSearch", search),
            L10n.Get(string.IsNullOrEmpty(search) ? "NoRemindersHere" : "NoMatchingReminders"), sections);
    }

    private static PrintListRow CapturePrintRow(ReminderItem row)
    {
        var due = DateTimeOffset.TryParse(row.DueDate, out var value)
            ? L10n.Format("PrintListDue", row.AllDay ? value.Date.ToString("d") : value.LocalDateTime.ToString("g"))
            : null;
        return new(row.DisplayTitle, row.Description, due, row.Completed, row.HierarchyIndent.Left / 2);
    }

    private void FinishPrintList(PrintListJob job, string? errorKey)
    {
        if (!ReferenceEquals(_printJob, job)) return;
        job.Dispose();
        _printJob = null;
        if (_windowClosed) return;
        PrintListButton.IsEnabled = true;
        if (errorKey is not null) ShowPrintListError(errorKey);
    }

    private void ShowPrintListError(string errorKey)
    {
        if (!_windowClosed)
            ShowInfo(L10n.Get("PrintListFailed"), L10n.Get(errorKey), InfoBarSeverity.Error);
    }

    private sealed record PrintListSnapshot(string Title, string? Search, string EmptyMessage, PrintListSection[] Sections);
    private sealed record PrintListSection(string? Title, PrintListRow[] Rows);
    private sealed record PrintListRow(string Title, string Notes, string? Due, bool Completed, double Indent);

    private sealed class PrintListJob : IDisposable
    {
        private readonly PrintManager _manager;
        private readonly Canvas _canvas;
        private readonly PrintListSnapshot _snapshot;
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<PrintListJob, string?> _completed;
        private readonly Action<string> _showError;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _source;
        private readonly List<Border> _pages = [];
        private readonly object _taskLock = new();
        private PrintTask? _task;
        private bool _disposed;

        internal PrintListJob(PrintManager manager, Canvas canvas, PrintListSnapshot snapshot, DispatcherQueue dispatcher,
            Action<PrintListJob, string?> completed, Action<string> showError)
        {
            _manager = manager;
            _canvas = canvas;
            _snapshot = snapshot;
            _dispatcher = dispatcher;
            _completed = completed;
            _showError = showError;
            _source = _document.DocumentSource;
        }

        internal void Register()
        {
            _document.Paginate += Paginate;
            _document.GetPreviewPage += GetPreviewPage;
            _document.AddPages += AddPages;
            _manager.PrintTaskRequested += PrintTaskRequested;
        }

        private void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            lock (_taskLock)
            {
                if (_disposed || _task is not null) return;
                _task = args.Request.CreatePrintTask(_snapshot.Title, request => request.SetSource(_source));
                _task.Completed += PrintTaskCompleted;
            }
        }

        private void PrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        {
            var errorKey = args.Completion == PrintTaskCompletion.Failed ? "PrintListJobFailed" : null;
            _dispatcher.TryEnqueue(() => _completed(this, errorKey));
        }

        private void Paginate(object sender, PaginateEventArgs args)
        {
            ClearPages();
            try
            {
                BuildPages(((PrintTaskOptions)args.PrintTaskOptions).GetPageDescription(0));
            }
            catch (Exception)
            {
                ClearPages();
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_disposed) _showError("PrintListJobFailed");
                });
            }
            _document.SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final);
        }

        private void GetPreviewPage(object sender, GetPreviewPageEventArgs args)
        {
            if (args.PageNumber >= 1 && args.PageNumber <= _pages.Count)
                _document.SetPreviewPage(args.PageNumber, _pages[args.PageNumber - 1]);
        }

        private void AddPages(object sender, AddPagesEventArgs args)
        {
            foreach (var page in _pages) _document.AddPage(page);
            _document.AddPagesComplete();
        }

        private void BuildPages(PrintPageDescription description)
        {
            var size = description.PageSize;
            var imageable = description.ImageableRect;
            var horizontalMargin = Math.Min(48, imageable.Width / 10);
            var verticalMargin = Math.Min(48, imageable.Height / 10);
            var padding = new Thickness(
                Math.Max(horizontalMargin, imageable.Left), Math.Max(verticalMargin, imageable.Top),
                Math.Max(horizontalMargin, size.Width - imageable.Right), Math.Max(verticalMargin, size.Height - imageable.Bottom));
            var width = size.Width - padding.Left - padding.Right;
            var height = size.Height - padding.Top - padding.Bottom;
            if (width < 96 || height < 64)
                throw new InvalidOperationException("The printer's imageable area is too small.");

            var first = new RichTextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                TextWrapping = TextWrapping.Wrap,
                IsTextScaleFactorEnabled = false
            };
            first.Blocks.Add(CreateParagraph(_snapshot.Title, 26, bold: true, bottom: 8));
            if (_snapshot.Search is { } search) first.Blocks.Add(CreateParagraph(search, 14, bottom: 8));
            if (_snapshot.Sections.Length == 0) first.Blocks.Add(CreateParagraph(_snapshot.EmptyMessage, 14));
            foreach (var section in _snapshot.Sections)
            {
                if (section.Title is { } title)
                    first.Blocks.Add(CreateParagraph(title, 16, bold: true, top: 10, bottom: 4));

                foreach (var row in section.Rows)
                {
                    var indent = Math.Min(row.Indent, width / 4);
                    first.Blocks.Add(CreateParagraph((row.Completed ? "☑ " : "☐ ") + row.Title, 14, bold: true, left: indent, top: 6));
                    if (row.Due is { } due) first.Blocks.Add(CreateParagraph(due, 12, left: indent + 18));
                    if (!string.IsNullOrWhiteSpace(row.Notes))
                        first.Blocks.Add(CreateParagraph(row.Notes, 13, left: indent + 18));
                }
            }

            AddPage(first);
            RichTextBlockOverflow? lastPage = null;
            while (lastPage?.HasOverflowContent ?? first.HasOverflowContent)
            {
                var nextPage = new RichTextBlockOverflow();
                if (lastPage is null) first.OverflowContentTarget = nextPage;
                else lastPage.OverflowContentTarget = nextPage;
                AddPage(nextPage);
                if (nextPage.HasOverflowContent && (nextPage.ContentStart is not { } start
                    || nextPage.ContentEnd is not { } end || end.Offset <= start.Offset))
                    throw new InvalidOperationException("Printing could not fit a line on the page.");
                lastPage = nextPage;
            }

            void AddPage(FrameworkElement content)
            {
                content.Width = width;
                content.Height = height;
                var page = new Border
                {
                    Width = size.Width,
                    Height = size.Height,
                    Padding = padding,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Child = content
                };
                _canvas.Children.Add(page);
                _canvas.UpdateLayout();
                _pages.Add(page);
            }
        }

        private static Paragraph CreateParagraph(string text, double fontSize, bool bold = false,
            double left = 0, double top = 0, double bottom = 0)
        {
            var paragraph = new Paragraph
            {
                FontSize = fontSize,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(left, top, 0, bottom)
            };
            paragraph.Inlines.Add(new Run { Text = text });
            return paragraph;
        }

        private void ClearPages()
        {
            _canvas.Children.Clear();
            _pages.Clear();
        }

        public void Dispose()
        {
            lock (_taskLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _manager.PrintTaskRequested -= PrintTaskRequested;
            if (_task is not null) _task.Completed -= PrintTaskCompleted;
            _document.Paginate -= Paginate;
            _document.GetPreviewPage -= GetPreviewPage;
            _document.AddPages -= AddPages;
            ClearPages();
        }
    }
}
