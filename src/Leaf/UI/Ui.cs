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
    public static class Ui
    {
        public static void InitializeTheme()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views.Theme.xaml"))
                Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }
        public static Window Load(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views." + name + ".xaml")) {
                var window = (Window)XamlReader.Load(stream);
                // The theme's Window style names the paper face with a StaticResource, which is
                // resolved once while the theme is parsed and never follows a later change. A
                // window opened after the user picked another face would keep the embedded
                // default, so every freshly loaded window takes the family the shell is using
                // now. ApplyPaperFont covers windows already open; this covers the history and
                // settings windows opened afterwards from the tray.
                window.FontFamily = (FontFamily)Application.Current.Resources["PaperFontFamily"];
                using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Assets.Leaf.ico"))
                    window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                double limit = SystemParameters.WorkArea.Height - 70;
                window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, limit));
                return window;
            }
        }
        // Panel fragments load separately: each XamlReader root owns its own NameScope,
        // so lookups inside a panel must use the panel, not the hosting Window.
        public static FrameworkElement LoadPanel(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views." + name + ".xaml"))
                return (FrameworkElement)XamlReader.Load(stream);
        }
        public static T Get<T>(Window window, string name) where T : class { return (T)window.FindName(name); }
        public static T Get<T>(FrameworkElement scope, string name) where T : class { return (T)scope.FindName(name); }
        public static void Visible(UIElement control, bool visible) { control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
        public static Brush Brush(string resource) { return (Brush)Application.Current.Resources[resource]; }
        public static void Click(Window window, string name, Action action) { Get<Button>(window, name).Click += (s, e) => action(); }
        public static void Click(FrameworkElement scope, string name, Action action) { Get<Button>(scope, name).Click += (s, e) => action(); }
        public static void Render(Window window, string path) { Render(window, path, 96); }
        // Layout stays in device-independent units; only the output pixels follow the DPI.
        public static void Render(Window window, string path, double dpi)
        {
            if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) throw new ArgumentOutOfRangeException("dpi");
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(window.Width, window.Height)); element.Arrange(new Rect(0, 0, window.Width, window.Height)); element.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96), (int)Math.Ceiling(window.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
            image.Render(background);
            image.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
        }

        // ---- The paper's typeface ----------------------------------------------------
        // The embedded Source Han Sans SC pack family is the default and the fallback. A
        // settings value names an installed text font; when that name resolves to a real
        // face the reading page switches to it, otherwise the embedded default is kept and
        // the caller is told so it can offer a light, non-blocking notice.
        private static FontFamily embeddedPaperFont;
        public static FontFamily EmbeddedPaperFont
        {
            get {
                if (embeddedPaperFont == null) embeddedPaperFont = (FontFamily)Application.Current.Resources["PaperFontFamily"];
                return embeddedPaperFont;
            }
        }
        // The installed families a user may pick as a text font, sorted, with the icon and
        // symbol-only faces left out so a choice never turns the reading page into glyph boxes.
        private static readonly HashSet<string> SymbolFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Segoe MDL2 Assets", "Segoe Fluent Icons", "Segoe UI Symbol", "Symbol", "Marlett",
            "Webdings", "Wingdings", "Wingdings 2", "Wingdings 3", "Bookshelf Symbol 7"
        };
        public static List<string> InstalledTextFonts()
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in Fonts.SystemFontFamilies) {
                string name = family.Source;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.StartsWith("Global ", StringComparison.OrdinalIgnoreCase)) continue;
                if (SymbolFamilies.Contains(name)) continue;
                if (seen.Add(name)) names.Add(name);
            }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }
        // Applies a stored font choice everywhere at once. A window's FontFamily cascades to
        // its chrome and text, but a FlowDocument carries its own face, so every open document
        // is written too: switching the resource alone would leave documents built earlier on
        // the old face.
        public static void ApplyPaperFont(string name, out bool missing)
        {
            missing = false;
            FontFamily family = EmbeddedPaperFont;
            if (!string.IsNullOrWhiteSpace(name)) {
                var candidate = new FontFamily(name);
                if (HasTextFace(candidate)) family = candidate; else missing = true;
            }
            Application.Current.Resources["PaperFontFamily"] = family;
            foreach (Window window in Application.Current.Windows) {
                window.FontFamily = family;
                ApplyPaperFontToTree(window, family, new HashSet<DependencyObject>());
            }
        }
        private static bool HasTextFace(FontFamily family)
        {
            try {
                foreach (var typeface in family.GetTypefaces()) {
                    GlyphTypeface glyph;
                    if (typeface.TryGetGlyphTypeface(out glyph) && glyph.CharacterToGlyphMap.ContainsKey('A')) return true;
                }
            } catch { }
            return false;
        }
        // Documents carry their own face, so every open RichTextBox's flow is written directly.
        // Both trees are walked because a document hosted inside a not-yet-template-applied
        // panel is reachable through the logical tree but not yet through the visual one.
        private static void ApplyPaperFontToTree(DependencyObject node, FontFamily family, HashSet<DependencyObject> seen)
        {
            if (node == null || !seen.Add(node)) return;
            var rich = node as RichTextBox;
            if (rich != null && rich.Document != null) rich.Document.FontFamily = family;
            if (node is Visual) {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) ApplyPaperFontToTree(VisualTreeHelper.GetChild(node, i), family, seen);
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node)) {
                var dependency = child as DependencyObject;
                if (dependency != null) ApplyPaperFontToTree(dependency, family, seen);
            }
        }
    }
}
