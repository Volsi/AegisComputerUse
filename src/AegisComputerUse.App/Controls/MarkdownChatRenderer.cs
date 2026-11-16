using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AegisComputerUse.App.Controls;

public static partial class MarkdownChatRenderer
{
    private static readonly FontFamily ProseFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Cascadia Code, Consolas, Courier New");

    private static readonly Brush PrimaryTextBrush = FreezeBrush(234, 238, 244);
    private static readonly Brush HeadingBrush = FreezeBrush(248, 250, 252);
    private static readonly Brush SubHeadingBrush = FreezeBrush(56, 189, 248);
    private static readonly Brush QuoteTextBrush = FreezeBrush(156, 163, 175);
    private static readonly Brush QuoteBorderBrush = FreezeBrush(56, 189, 248);
    private static readonly Brush InlineCodeBg = FreezeBrush(28, 34, 46);
    private static readonly Brush InlineCodeFg = FreezeBrush(125, 211, 252);

    private static readonly Brush CodeCardBg = FreezeBrush(10, 12, 17);
    private static readonly Brush CodeHeaderBg = FreezeBrush(20, 24, 33);
    private static readonly Brush CodeBorder = FreezeBrush(38, 43, 54);
    private static readonly Brush CodeKeyword = FreezeBrush(192, 132, 252);
    private static readonly Brush CodeString = FreezeBrush(165, 214, 255);
    private static readonly Brush CodeComment = FreezeBrush(110, 118, 129);
    private static readonly Brush CodeNumber = FreezeBrush(121, 192, 255);
    private static readonly Brush CodeType = FreezeBrush(110, 231, 183);
    private static readonly Brush CodeNormal = FreezeBrush(230, 237, 243);
    private static readonly Brush SelectionHighlight = FreezeBrush(37, 99, 235);
    private static readonly Brush CopyBtnBg = FreezeBrush(31, 36, 46);
    private static readonly Brush CopyBtnHoverBg = FreezeBrush(43, 50, 64);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "namespace", "public", "private", "protected", "internal", "static", "readonly", "sealed",
        "class", "struct", "record", "interface", "enum", "void", "async", "await", "return", "var", "const",
        "let", "function", "def", "import", "from", "export", "default", "if", "else", "elif", "switch",
        "case", "break", "continue", "for", "foreach", "while", "do", "try", "catch", "finally", "throw",
        "new", "true", "false", "null", "None", "True", "False", "in", "out", "ref", "is", "as", "not",
        "and", "or", "SELECT", "FROM", "WHERE", "INSERT", "INTO", "UPDATE", "DELETE", "CREATE", "TABLE",
        "string", "int", "bool", "double", "float", "byte", "char", "object", "Task", "List", "Dictionary"
    };

    [GeneratedRegex(@"```([a-zA-Z0-9_+#\-\.]*)[ \t]*\r?\n([\s\S]*?)```", RegexOptions.CultureInvariant)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"(\*\*[^*\n]+\*\*|`[^`\n]+`|\*[^*\n]+\*)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarkdownRegex();

    [GeneratedRegex(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"(//[^\n]*|#[^\n]*|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\b\d+(?:\.\d+)?\b|\b[A-Za-z_][A-Za-z0-9_]*\b)", RegexOptions.CultureInvariant)]
    private static partial Regex CodeTokenRegex();

    public static UIElement BuildUserMessage(string promptText)
    {
        string text = promptText ?? string.Empty;
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = ProseFont,
            FontSize = 13.5,
            Foreground = PrimaryTextBrush,
            LineHeight = 20.5
        };
        return tb;
    }

    public static UIElement BuildRichMessage(string markdown)
    {
        string text = (markdown ?? string.Empty).Replace("\r\n", "\n");
        var root = new StackPanel();

        int cursor = 0;
        foreach (Match match in FencedCodeRegex().Matches(text))
        {
            if (match.Index > cursor)
            {
                string prose = text[cursor..match.Index].Trim('\n');
                if (!string.IsNullOrWhiteSpace(prose))
                    root.Children.Add(BuildProseBlock(prose));
            }

            string lang = match.Groups[1].Value.Trim();
            string code = match.Groups[2].Value.TrimEnd('\n');
            root.Children.Add(BuildCodeCard(lang, code));
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            string tail = text[cursor..].Trim('\n');
            if (!string.IsNullOrWhiteSpace(tail))
            {
                // Handle an unclosed trailing code fence gracefully if model truncated output
                int openFence = tail.IndexOf("```", StringComparison.Ordinal);
                if (openFence >= 0)
                {
                    string beforeFence = tail[..openFence].Trim('\n');
                    if (!string.IsNullOrWhiteSpace(beforeFence))
                        root.Children.Add(BuildProseBlock(beforeFence));

                    string remainder = tail[(openFence + 3)..];
                    int newline = remainder.IndexOf('\n');
                    string lang = newline >= 0 ? remainder[..newline].Trim() : string.Empty;
                    string code = newline >= 0 ? remainder[(newline + 1)..].TrimEnd('\n') : remainder;
                    root.Children.Add(BuildCodeCard(lang, code));
                }
                else
                {
                    root.Children.Add(BuildProseBlock(tail));
                }
            }
        }

        if (root.Children.Count == 0)
        {
            root.Children.Add(BuildProseBlock(string.Empty));
        }

        return root;
    }

    private static UIElement BuildProseBlock(string prose)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = ProseFont,
            FontSize = 13.5,
            Foreground = PrimaryTextBrush
        };

        foreach (string rawLine in prose.Split('\n'))
        {
            string line = rawLine;
            string trimmed = line.Trim();

            if (trimmed is "---" or "***" or "___")
            {
                doc.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = CodeBorder,
                    Margin = new Thickness(0, 6, 0, 8)
                }));
                continue;
            }

            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 5), LineHeight = 20.5 };

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                p.FontSize = 14;
                p.FontWeight = FontWeights.SemiBold;
                p.Foreground = SubHeadingBrush;
                p.Margin = new Thickness(0, 7, 0, 4);
                AppendInlineRuns(p.Inlines, line[4..]);
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                p.FontSize = 15.5;
                p.FontWeight = FontWeights.Bold;
                p.Foreground = HeadingBrush;
                p.Margin = new Thickness(0, 9, 0, 5);
                AppendInlineRuns(p.Inlines, line[3..]);
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                p.FontSize = 17;
                p.FontWeight = FontWeights.Bold;
                p.Foreground = HeadingBrush;
                p.Margin = new Thickness(0, 9, 0, 6);
                AppendInlineRuns(p.Inlines, line[2..]);
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                p.Margin = new Thickness(6, 3, 0, 5);
                p.Padding = new Thickness(10, 2, 0, 2);
                p.BorderBrush = QuoteBorderBrush;
                p.BorderThickness = new Thickness(2.5, 0, 0, 0);
                p.Foreground = QuoteTextBrush;
                AppendInlineRuns(p.Inlines, line[2..]);
            }
            else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
            {
                string trimmedStart = line.TrimStart();
                int indent = Math.Min(24, (line.Length - trimmedStart.Length) * 6);
                p.Margin = new Thickness(12 + indent, 0, 0, 4);
                p.Inlines.Add(new Run("•  ") { Foreground = SubHeadingBrush, FontWeight = FontWeights.Bold });
                AppendInlineRuns(p.Inlines, trimmedStart[2..]);
            }
            else
            {
                Match ord = OrderedListRegex().Match(line);
                if (ord.Success)
                {
                    p.Margin = new Thickness(12, 0, 0, 4);
                    p.Inlines.Add(new Run(ord.Groups[1].Value + ".  ") { Foreground = SubHeadingBrush, FontWeight = FontWeights.SemiBold });
                    AppendInlineRuns(p.Inlines, ord.Groups[2].Value);
                }
                else
                {
                    AppendInlineRuns(p.Inlines, line);
                }
            }

            doc.Blocks.Add(p);
        }

        var rtb = CreateSelectableReadOnlyRichTextBox(doc, horizontalScroll: false);
        rtb.Margin = new Thickness(0, 0, 0, 2);
        return rtb;
    }

    private static void AppendInlineRuns(InlineCollection inlines, string text)
    {
        int idx = 0;
        foreach (Match m in InlineMarkdownRegex().Matches(text))
        {
            if (m.Index > idx)
                inlines.Add(new Run(text[idx..m.Index]));

            string token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                inlines.Add(new Bold(new Run(token[2..^2])));
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                var run = new Run(" " + token[1..^1] + " ")
                {
                    FontFamily = CodeFont,
                    FontSize = 12.2,
                    Background = InlineCodeBg,
                    Foreground = InlineCodeFg
                };
                inlines.Add(run);
            }
            else if (token.StartsWith('*') && token.EndsWith('*'))
            {
                inlines.Add(new Italic(new Run(token[1..^1])));
            }
            else
            {
                inlines.Add(new Run(token));
            }

            idx = m.Index + m.Length;
        }

        if (idx < text.Length)
            inlines.Add(new Run(text[idx..]));
    }

    private static UIElement BuildCodeCard(string language, string code)
    {
        string displayLang = string.IsNullOrWhiteSpace(language) ? "TEXT" : language.ToUpperInvariant();
        int lineCount = string.IsNullOrEmpty(code) ? 0 : code.Split('\n').Length;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 12, 14, 13),
            PageWidth = 2600,
            FontFamily = CodeFont,
            FontSize = 12.5,
            Foreground = CodeNormal
        };

        var p = new Paragraph { Margin = new Thickness(0), LineHeight = 19.5 };
        AppendSyntaxHighlightedRuns(p.Inlines, code, language);
        doc.Blocks.Add(p);

        var rtb = CreateSelectableReadOnlyRichTextBox(doc, horizontalScroll: true);

        var headerGrid = new Grid { Margin = new Thickness(12, 7, 10, 7) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new Border
        {
            Background = FreezeBrush(24, 31, 45),
            BorderBrush = FreezeBrush(44, 55, 76),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = displayLang,
                FontFamily = CodeFont,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = SubHeadingBrush
            }
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{lineCount} стр.",
            FontFamily = CodeFont,
            FontSize = 10.5,
            Foreground = FreezeBrush(110, 118, 134),
            VerticalAlignment = VerticalAlignment.Center
        });
        headerGrid.Children.Add(titleStack);

        var copyBtn = CreateRoundedCopyButton();
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copyBtn.Content = "✓ Скопировано";
                copyBtn.Foreground = CodeType;
                await Task.Delay(1500);
                copyBtn.Content = "Копировать";
                copyBtn.Foreground = FreezeBrush(226, 232, 240);
            }
            catch { }
        };
        Grid.SetColumn(copyBtn, 1);
        headerGrid.Children.Add(copyBtn);

        var headerBorder = new Border
        {
            Background = CodeHeaderBg,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Child = headerGrid
        };

        var stack = new StackPanel();
        stack.Children.Add(headerBorder);
        stack.Children.Add(rtb);

        return new Border
        {
            Background = CodeCardBg,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 6, 0, 10),
            Child = stack
        };
    }

    private static Button CreateRoundedCopyButton()
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border))
        {
            Name = "CopyBd"
        };
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, CopyBtnHoverBg, "CopyBd"));
        template.Triggers.Add(hoverTrigger);

        return new Button
        {
            Content = "Копировать",
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
            Background = CopyBtnBg,
            Foreground = FreezeBrush(226, 232, 240),
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            Template = template
        };
    }

    private static void AppendSyntaxHighlightedRuns(InlineCollection inlines, string code, string lang)
    {
        bool hashComment = lang.Equals("python", StringComparison.OrdinalIgnoreCase) ||
                           lang.Equals("py", StringComparison.OrdinalIgnoreCase) ||
                           lang.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                           lang.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                           lang.Equals("yaml", StringComparison.OrdinalIgnoreCase) ||
                           lang.Equals("yml", StringComparison.OrdinalIgnoreCase);

        int idx = 0;
        foreach (Match m in CodeTokenRegex().Matches(code))
        {
            if (m.Index > idx)
                inlines.Add(new Run(code[idx..m.Index]) { Foreground = CodeNormal });

            string tok = m.Value;
            if (tok.StartsWith("//", StringComparison.Ordinal) || (hashComment && tok.StartsWith('#')))
            {
                inlines.Add(new Run(tok) { Foreground = CodeComment, FontStyle = FontStyles.Italic });
            }
            else if (tok.StartsWith('"') || tok.StartsWith('\''))
            {
                inlines.Add(new Run(tok) { Foreground = CodeString });
            }
            else if (char.IsDigit(tok[0]))
            {
                inlines.Add(new Run(tok) { Foreground = CodeNumber });
            }
            else if (Keywords.Contains(tok))
            {
                inlines.Add(new Run(tok) { Foreground = CodeKeyword, FontWeight = FontWeights.SemiBold });
            }
            else if (char.IsUpper(tok[0]) && tok.Length > 1 && tok.Any(char.IsLower))
            {
                inlines.Add(new Run(tok) { Foreground = CodeType });
            }
            else
            {
                inlines.Add(new Run(tok) { Foreground = CodeNormal });
            }

            idx = m.Index + m.Length;
        }

        if (idx < code.Length)
            inlines.Add(new Run(code[idx..]) { Foreground = CodeNormal });
    }

    private static RichTextBox CreateSelectableReadOnlyRichTextBox(FlowDocument doc, bool horizontalScroll)
    {
        doc.PagePadding = new Thickness(0);
        doc.ColumnWidth = double.PositiveInfinity;

        var rtb = new RichTextBox(doc)
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsDocumentEnabled = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionBrush = SelectionHighlight,
            Cursor = Cursors.IBeam,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = horizontalScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };

        var copyItem = new MenuItem { Header = "Копировать", Command = ApplicationCommands.Copy };
        var selectAllItem = new MenuItem { Header = "Выделить всё", Command = ApplicationCommands.SelectAll };
        rtb.ContextMenu = new ContextMenu
        {
            Background = CodeHeaderBg,
            Foreground = PrimaryTextBrush,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            Items = { copyItem, selectAllItem }
        };

        // Bubble vertical mouse wheel smoothly to ancestor ScrollViewer
        rtb.PreviewMouseWheel += (s, e) =>
        {
            if (e.Handled || s is not DependencyObject start) return;
            e.Handled = true;
            var routed = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = s
            };

            DependencyObject? cur = VisualTreeHelper.GetParent(start);
            while (cur is not null)
            {
                if (cur is ScrollViewer sv)
                {
                    sv.RaiseEvent(routed);
                    return;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
        };

        return rtb;
    }

    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
