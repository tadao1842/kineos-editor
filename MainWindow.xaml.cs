using System;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;

namespace KineosEditor
{
    public partial class MainWindow : Window
    {
        private const string FileFilter = "すべてのファイル (*.*)|*.*|Kineos (*.kns)|*.kns|テキスト (*.txt)|*.txt|Markdown (*.md)|*.md";
        private static readonly Regex ReplacementTokens = new Regex(@"\$\$|\$(?=\\[0-9])|(\\+)([0-9]+)");
        private readonly SearchPanel searchPanel;
        private readonly IHighlightingDefinition kineosHighlighting;
        private readonly IHighlightingDefinition markdownHighlighting;
        private string currentFilePath;

        public MainWindow()
        {
            InitializeComponent();

            kineosHighlighting = LoadHighlighting("Kineos");
            markdownHighlighting = LoadHighlighting("Markdown");

            var fontName = ConfigurationManager.AppSettings["FontFamily"];
            textEditor.FontFamily = string.IsNullOrWhiteSpace(fontName) ||
                                    string.Equals(fontName.Trim(), "NOTONOTO35", StringComparison.OrdinalIgnoreCase)
                ? new FontFamily(new Uri("pack://application:,,,/"), "/KineosEditor;component/Fonts/#NOTONOTO35")
                : new FontFamily(fontName);
            textEditor.Options.HighlightCurrentLine = true;

            var textArea = textEditor.TextArea;
            textArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(211, 190, 250));
            textArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromRgb(43, 35, 53));
            textArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(103, 81, 126));
            textArea.SelectionForeground = Brushes.White;
            searchPanel = SearchPanel.Install(textEditor);
            searchPanel.Style = (Style)FindResource("EditorSearchPanel");
            searchPanel.Localization = new SearchLocalization();
            searchPanel.MarkerBrush = new SolidColorBrush(Color.FromRgb(113, 85, 38));
            searchPanel.MarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(189, 149, 105)), 1);
            searchPanel.SearchOptionsChanged += (sender, e) => searchPanel.Tag = null;

            textArea.Caret.PositionChanged += (sender, e) => UpdateCaretStatus();
            textEditor.Document.UndoStack.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == "IsOriginalFile") UpdateDocumentStatus();
            };
            Loaded += (sender, e) => textEditor.Focus();
            UpdateCaretStatus();
            UpdateDocumentStatus();
        }

        public void LoadFile(string path)
        {
            try
            {
                textEditor.Load(path);
                SetCurrentFile(path);
                UpdateCaretStatus();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowMessage("ファイルを開けませんでした", ex.Message);
            }
        }

        private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = FileFilter };
            if (dialog.ShowDialog(this) == true && ConfirmDiscardChanges())
            {
                LoadFile(dialog.FileName);
                textEditor.Focus();
            }
        }

        private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveFile();
        }

        private bool SaveFile()
        {
            var path = currentFilePath;
            if (string.IsNullOrEmpty(path))
            {
                var dialog = new SaveFileDialog { Filter = FileFilter };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }

            try
            {
                textEditor.Save(path);
                SetCurrentFile(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowMessage("ファイルを保存できませんでした", ex.Message);
                return false;
            }
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            ApplicationCommands.Find.Execute(null, textEditor.TextArea);
        }

        private void OpenReplace_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (searchPanel.IsClosed)
                ApplicationCommands.Find.Execute(null, textEditor.TextArea);
            else if (string.IsNullOrEmpty(searchPanel.SearchPattern))
                searchPanel.Reactivate();

            // AvalonEdit schedules focus for the search field at Input priority.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (searchPanel.IsClosed || string.IsNullOrEmpty(searchPanel.SearchPattern)) return;
                var field = GetReplacementField();
                field.Focus();
                field.SelectAll();
            }));
        }

        private TextBox GetReplacementField()
        {
            searchPanel.ApplyTemplate();
            return (TextBox)searchPanel.Template.FindName("ReplacementTextBox", searchPanel);
        }

        private void ReplacementTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            ReplaceMatches(((TextBox)sender).Text, replaceAll: false);
            e.Handled = true;
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            ReplaceMatches(GetReplacementField().Text, replaceAll: false);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            ReplaceMatches(GetReplacementField().Text, replaceAll: true);
        }

        private void ReplaceMatches(string replacement, bool replaceAll)
        {
            var pattern = searchPanel.SearchPattern;
            if (string.IsNullOrEmpty(pattern))
            {
                searchPanel.Tag = "検索文字列を入力してください";
                return;
            }

            var document = textEditor.Document;
            try
            {
                var mode = searchPanel.UseRegex ? SearchMode.RegEx : SearchMode.Normal;
                var strategy = SearchStrategyFactory.Create(pattern, !searchPanel.MatchCase, searchPanel.WholeWords, mode);
                var matches = strategy.FindAll(document, 0, document.TextLength).ToList();
                if (matches.Count == 0)
                {
                    searchPanel.Tag = "一致する文字列がありません";
                    return;
                }

                if (!replaceAll)
                {
                    var start = textEditor.SelectionStart;
                    var length = textEditor.SelectionLength;
                    var match = matches.FirstOrDefault(item => item.Offset == start && item.Length == length)
                                ?? matches.FirstOrDefault(item => item.Offset >= textEditor.CaretOffset)
                                ?? matches[0];
                    matches.Clear();
                    matches.Add(match);
                }

                // Calculate replacements before editing, then apply from the end to preserve offsets.
                if (mode == SearchMode.RegEx)
                    replacement = ReplacementTokens.Replace(replacement, NormalizeReplacementToken);

                var edits = matches.Select(match => new
                {
                    Match = match,
                    Text = mode == SearchMode.RegEx ? match.ReplaceWith(replacement) : replacement
                }).Reverse().ToList();

                using (document.RunUpdate())
                {
                    foreach (var edit in edits)
                        document.Replace(edit.Match.Offset, edit.Match.Length, edit.Text);
                }

                if (!replaceAll)
                {
                    var edit = edits[0];
                    var nextOffset = edit.Match.Offset + edit.Text.Length + (edit.Match.Length == 0 ? 1 : 0);
                    var remaining = strategy.FindAll(document, 0, document.TextLength).ToList();
                    var next = remaining.FirstOrDefault(item => item.Offset >= nextOffset) ?? remaining.FirstOrDefault();
                    if (next != null)
                    {
                        textEditor.Select(next.Offset, next.Length);
                        textEditor.CaretOffset = next.Offset;
                        textEditor.TextArea.Caret.BringCaretToView();
                    }
                }

                searchPanel.Tag = $"{edits.Count} 件置換しました";
            }
            catch (SearchPatternException)
            {
                searchPanel.Tag = "正規表現を確認してください";
            }
            catch (ArgumentException) when (searchPanel.UseRegex)
            {
                searchPanel.Tag = "置換文字列を確認してください";
            }
        }

        private static string NormalizeReplacementToken(Match token)
        {
            // Keep .NET's $$ escape intact and prevent a literal $ from swallowing a following group.
            if (!token.Groups[1].Success) return "$$";

            var slashes = token.Groups[1].Length;
            var number = token.Groups[2].Value;
            var prefix = new string('\\', slashes / 2);
            return prefix + (slashes % 2 == 0 ? number : "${" + number + "}");
        }

        private sealed class SearchLocalization : ICSharpCode.AvalonEdit.Search.Localization
        {
            public override string ErrorText => "検索エラー";
            public override string NoMatchesFoundText => "一致する文字列がありません";
        }

        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize_Click(sender, e);
            }
            else if (WindowState == WindowState.Normal)
            {
                DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.MinimizeWindow(this);
        }

        private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.CloseWindow(this);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ((HwndSource)PresentationSource.FromVisual(this)).AddHook(KeepMaximizedWithinWorkArea);
        }

        private static IntPtr KeepMaximizedWithinWorkArea(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int getMinMaxInfo = 0x0024;
            const uint nearestMonitor = 2;
            if (message != getMinMaxInfo) return IntPtr.Zero;

            var monitor = MonitorFromWindow(hwnd, nearestMonitor);
            var bounds = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref bounds)) return IntPtr.Zero;

            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxPosition.X = bounds.Work.Left - bounds.Monitor.Left;
            limits.MaxPosition.Y = bounds.Work.Top - bounds.Monitor.Top;
            limits.MaxSize.X = bounds.Work.Right - bounds.Work.Left;
            limits.MaxSize.Y = bounds.Work.Bottom - bounds.Work.Top;
            Marshal.StructureToPtr(limits, lParam, false);
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = !ConfirmDiscardChanges();
        }

        private bool ConfirmDiscardChanges()
        {
            if (textEditor.Document.UndoStack.IsOriginalFile) return true;

            var choice = ShowMessage("保存の確認", "編集中の変更を保存しますか？", confirmSave: true);
            return choice == MessageBoxResult.No || (choice == MessageBoxResult.Yes && SaveFile());
        }

        private MessageBoxResult ShowMessage(string title, string message, bool confirmSave = false)
        {
            var dialog = (Window)FindResource("MessageWindow");
            dialog.Title = title;
            dialog.DataContext = message;
            dialog.Tag = confirmSave;
            dialog.Topmost = Topmost;
            dialog.WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            if (IsVisible) dialog.Owner = this;
            dialog.ContentRendered += (sender, e) => dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            dialog.ShowDialog();
            return dialog.Tag is MessageBoxResult choice ? choice : MessageBoxResult.Cancel;
        }

        private void MessageChoice_Click(object sender, RoutedEventArgs e)
        {
            var button = (FrameworkElement)sender;
            var dialog = Window.GetWindow(button);
            dialog.Tag = button.Tag;
            dialog.Close();
        }

        private void UpdateDocumentStatus()
        {
            var fileName = Path.GetFileName(currentFilePath);
            var modified = !textEditor.Document.UndoStack.IsOriginalFile;
            documentName.Text = (fileName ?? "無題") + (modified ? "  ●" : "");
            Title = (fileName ?? "Kineos Editor") + (modified ? " *" : "");
            documentStatus.Text = modified ? "未保存の変更" : currentFilePath ?? "新規ドキュメント";
            documentStatus.ToolTip = currentFilePath;
            encodingStatus.Text = textEditor.Encoding?.WebName.ToUpperInvariant() ?? "UTF-8";
        }

        private static IHighlightingDefinition LoadHighlighting(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream($"KineosEditor.{name}.xshd"))
            using (var reader = XmlReader.Create(stream))
            {
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        private void SetCurrentFile(string path)
        {
            currentFilePath = path;
            var extension = Path.GetExtension(path);
            switch (extension.ToLowerInvariant())
            {
                case ".kns":
                    textEditor.SyntaxHighlighting = kineosHighlighting;
                    break;
                case ".md":
                    textEditor.SyntaxHighlighting = markdownHighlighting;
                    break;
                default:
                    textEditor.SyntaxHighlighting = null;
                    break;
            }

            fileType.Text = extension.Length == 0 ? "未定" : extension.Substring(1).ToUpperInvariant();
            UpdateDocumentStatus();
        }

        private void UpdateCaretStatus()
        {
            var caret = textEditor.TextArea.Caret;
            caretStatus.Text = $"行 {caret.Line}  列 {caret.Column}";
        }
    }
}
