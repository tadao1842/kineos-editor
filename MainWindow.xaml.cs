using System.Configuration;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Win32;

namespace KineosEditor
{
    public partial class MainWindow : Window
    {
        private string currentFilePath;

        public MainWindow()
        {
            InitializeComponent();

            var assembly = Assembly.GetExecutingAssembly();

            using (var stream = assembly.GetManifestResourceStream("KineosEditor.Kineos.xshd"))
            using (var reader = new XmlTextReader(stream))
            {
                textEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }

            textEditor.FontFamily = new FontFamily(ConfigurationManager.AppSettings["FontFamily"]);
        }

        private void TextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (
                (Keyboard.Modifiers == ModifierKeys.Control) &&
                (e.Key == Key.S)
            )
            {
                SaveFile();
                e.Handled = true;
            }
        }

        public void LoadFile(string path)
        {
            currentFilePath = path;
            textEditor.Load(path);
            Title = Path.GetFileName(currentFilePath);
        }

        private void SaveFile()
        {
            if (string.IsNullOrEmpty(currentFilePath))
            {
                var dlg = new SaveFileDialog { DefaultExt = "kns" };
                if (dlg.ShowDialog() != true) return;
                currentFilePath = dlg.FileName;
            }

            textEditor.Save(currentFilePath);
        }
    }
}
