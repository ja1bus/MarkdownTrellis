using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarkdownTrellis.Core;
using Xunit;

namespace MarkdownTrellis.Tests;

public class RendererTests
{
    private static string Preview(string text, SyntaxFeature features = SyntaxFeature.None, string? host = null) =>
        MarkdownRenderer.RenderPreview(new RenderRequest(text, features, ImageHost: host)).Html;

    [Fact]
    public void Empty_document_renders_empty() => Assert.Equal(string.Empty, Preview(string.Empty));

    [Fact]
    public void Gfm_elements_are_rendered()
    {
        var html = Preview("# T\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\n- [x] done\n- [ ] todo\n\n~~old~~ **b** *i* `c`\n\nhttps://example.com/\n\n---\n");
        Assert.Contains("<h1", html);
        Assert.Contains("<table", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<strong>b</strong>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("href=\"https://example.com/\"", html);
        Assert.Contains("<hr", html);
    }

    [Fact]
    public void Frontmatter_is_not_rendered()
    {
        var html = Preview("---\ntitle: \"x\"\n---\n\n# Body\n");
        Assert.DoesNotContain("title", html);
        Assert.Contains("Body", html);
    }

    [Fact]
    public void Old_default_mode_setting_is_ignored()
    {
        var copy = AppSettings.FromJson("{\"DefaultMode\": \"CommonMark\", \"Language\": \"en\"}");
        Assert.Equal("en", copy.Language);
        Assert.Contains("<del>", Preview("~~old~~"));
    }

    [Fact]
    public void Features_are_opt_in()
    {
        Assert.DoesNotContain("<mark>", Preview("==x=="));
        Assert.Contains("<mark>x</mark>", Preview("==x==", features: SyntaxFeature.Highlight));
        Assert.DoesNotContain("footnote", Preview("a[^1]\n\n[^1]: b\n"));
        Assert.Contains("footnote", Preview("a[^1]\n\n[^1]: b\n", features: SyntaxFeature.Footnotes));
        Assert.Contains("markdown-alert", Preview("> [!NOTE]\n> x\n", features: SyntaxFeature.Alerts));
        Assert.Contains("href=\"a%20b\"", Preview("[[a b|c]]", features: SyntaxFeature.WikiLinks));
    }

    [Theory]
    [InlineData(SyntaxFeature.Alerts, "> [!WARNING]\n> a\n")]
    [InlineData(SyntaxFeature.Footnotes, "a[^1]\n\n[^1]: b\n")]
    [InlineData(SyntaxFeature.Math, "$$\nx\n$$\n\n$y$\n")]
    [InlineData(SyntaxFeature.Containers, ":::note\na\n:::\n")]
    [InlineData(SyntaxFeature.Highlight, "==a==\n")]
    [InlineData(SyntaxFeature.WikiLinks, "[[a|b]]\n")]
    [InlineData(SyntaxFeature.DefinitionLists, "a\n: b\n")]
    public void Generated_html_of_each_feature_passes_the_sanitizer(SyntaxFeature feature, string text)
    {
        var result = MarkdownRenderer.RenderPreview(new RenderRequest(text, Features: feature));
        Assert.Empty(result.RejectedTags);
        Assert.DoesNotContain("&lt;", result.Html);
    }

    [Fact]
    public void Alert_shows_a_readable_title()
    {
        var html = MarkdownRenderer.RenderPreview(new RenderRequest("> [!WARNING]\n> a\n", Features: SyntaxFeature.Alerts)).Html;
        Assert.Contains("<p class=\"markdown-alert-title\">Warning</p>", html);
        Assert.DoesNotContain("svg", html);
    }

    [Fact]
    public void Dollar_prices_are_untouched_without_math() => Assert.Contains("$5 and $6", Preview("$5 and $6"));

    [Fact]
    public void Outer_fence_can_be_excluded_from_preview_only()
    {
        const string Text = "```markdown\n# Title\n\n**b**\n```\n";
        Assert.Contains("<code", MarkdownRenderer.RenderPreview(new RenderRequest(Text)).Html);
        var inner = MarkdownRenderer.RenderPreview(new RenderRequest(Text, StripOuterFence: true)).Html;
        Assert.Contains("<h1", inner);
        Assert.Contains("data-line=\"1\"", inner);
    }

    [Fact]
    public void Line_numbers_are_added_for_scroll_sync() => Assert.Contains("data-line=\"2\"", Preview("a\n\nb\n"));

    [Theory]
    [InlineData("# 日本語😀\r\n\r\n段落の**強調**\r\n続く行\r\n", "p", 2)]
    [InlineData("先頭\n\n## 見出し\n", "h2", 2)]
    [InlineData("先頭\n\n- 親\n  - 子\n", "li", 3)]
    [InlineData("先頭\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n", "table", 2)]
    [InlineData("先頭\n\n```csharp\nvar x = 1;\n```\n", "code", 2)]
    [InlineData("---\ntitle: x\n---\n\n# 本文\n", "h1", 4)]
    public void Preview_blocks_point_to_the_source_start_line(string source, string tag, int line)
    {
        Assert.Matches($"<{tag}\\b[^>]*\\bdata-line=\"{line}\"", Preview(source));
    }

    [Fact]
    public void Excluded_outer_fence_preserves_original_line_numbers_with_frontmatter()
    {
        const string source = "```markdown\r\n---\r\ntitle: x\r\n---\r\n\r\n# 日本語😀\r\n\r\n本文\r\n```\r\n";
        var html = MarkdownRenderer.RenderPreview(new RenderRequest(source, StripOuterFence: true)).Html;
        Assert.Matches("<h1\\b[^>]*\\bdata-line=\"5\"", html);
        Assert.Matches("<p\\b[^>]*\\bdata-line=\"7\"", html);
    }
}

public class SanitizerTests
{
    private static SanitizeResult Run(string html, IReadOnlyCollection<string>? extra = null) => HtmlSanitizer.Sanitize(html, new SanitizeOptions(extra));

    [Theory]
    [InlineData("<script>alert(1)</script>", "script")]
    [InlineData("<style>x{}</style>", "style")]
    [InlineData("<iframe src=x></iframe>", "iframe")]
    [InlineData("<form action=x>", "form")]
    [InlineData("<svg onload=alert(1)>", "svg")]
    [InlineData("<SCRIPT>a</SCRIPT>", "script")]
    public void Forbidden_tags_become_text(string html, string name)
    {
        var result = Run(html);
        Assert.DoesNotContain("<" + name, result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", result.Html);
        Assert.True(result.RejectedTags[name] > 0);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"jav&#x61;script:alert(1)\">x</a>")]
    [InlineData("<a href=\" java\nscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html,x\">x</a>")]
    [InlineData("<a href=\"vbscript:x\">x</a>")]
    [InlineData("<a href=\"file:///c:/x\">x</a>")]
    public void Dangerous_urls_are_removed(string html)
    {
        var output = Run(html).Html;
        Assert.DoesNotContain("href", output);
        Assert.Contains("<a>x</a>", output);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:a@example.com")]
    [InlineData("docs/a.md")]
    [InlineData("#top")]
    public void Allowed_urls_are_kept(string url) => Assert.Contains("href=\"" + url + "\"", Run($"<a href=\"{url}\">x</a>").Html);

    [Fact]
    public void Event_handlers_and_style_are_removed()
    {
        var html = Run("<div onclick=\"a()\" style=\"position:fixed\" class=\"c\">x</div>").Html;
        Assert.Equal("<div class=\"c\">x</div>", html);
    }

    [Fact]
    public void Details_is_kept_with_open_attribute() =>
        Assert.Equal("<details open><summary>s</summary>b</details>", Run("<details open><summary>s</summary>b</details>").Html);

    [Fact]
    public void Table_alignment_only_allows_generated_values()
    {
        Assert.Contains("style=\"text-align: right;\"", Run("<td style=\"text-align: right;\">x</td>").Html);
        Assert.DoesNotContain("style", Run("<td style=\"background:url(x)\">x</td>").Html);
    }

    [Fact]
    public void Text_inputs_are_not_allowed_and_checkboxes_are_disabled_unless_interactive()
    {
        Assert.Contains("&lt;input", Run("<input type=\"text\">").Html);
        Assert.Contains("disabled", Run("<input type=\"checkbox\">").Html);

        // 通常表示のプレビューだけは操作できる（クリックはパネル側で取り消す）。text は許可しないまま
        var interactive = new SanitizeOptions(InteractiveCheckboxes: true);
        Assert.DoesNotContain("disabled", HtmlSanitizer.Sanitize("<input type=\"checkbox\" disabled checked>", interactive).Html);
        Assert.Contains("checked", HtmlSanitizer.Sanitize("<input type=\"checkbox\" disabled checked>", interactive).Html);
        Assert.Contains("&lt;input", HtmlSanitizer.Sanitize("<input type=\"text\">", interactive).Html);
    }

    [Fact]
    public void Extra_tags_can_be_added_but_not_forbidden_ones()
    {
        Assert.Equal("<nav>x</nav>", Run("<nav>x</nav>", ["nav"]).Html);
        Assert.Contains("&lt;script", Run("<script>", ["script"]).Html);
    }

    [Fact]
    public void Broken_markup_does_not_produce_tags()
    {
        foreach (var html in new[] { "<a href=\"x", "<", "<<script>", "</", "<!doctype html>", "<?php ?>", "<div =x>", "<img src=x onerror=alert(1)//" })
        {
            var output = Run(html).Html;
            Assert.DoesNotContain("onerror", output.Replace("&lt;", string.Empty).Contains('<') ? output : string.Empty);
            Assert.DoesNotContain("<script", output);
        }
    }

    [Fact]
    public void Attribute_values_are_reencoded() =>
        Assert.Equal("<a title=\"&quot;&gt;&lt;script&gt;\">x</a>", Run("<a title='&quot;>&lt;script>'>x</a>").Html);

    [Fact]
    public void Comments_are_hidden() => Assert.Equal("ab", Run("a<!-- <script> -->b").Html);

    [Fact]
    public void Preview_blocks_external_and_outside_images()
    {
        var options = new SanitizeOptions(null, ImageMode.Preview, "doc.example");
        foreach (var src in new[] { "https://example.com/a.png", "../secret.png", "a/../../b.png", "c:/x.png", "/x.png", "%2e%2e/x.png" })
        {
            var result = HtmlSanitizer.Sanitize($"<img src=\"{src}\" alt=\"t\">", options);
            Assert.Equal(1, result.BlockedImages);
            Assert.DoesNotContain("<img", result.Html);
            Assert.Contains("t</span>", result.Html);
        }
    }

    [Fact]
    public void Preview_maps_relative_images_to_host()
    {
        var html = HtmlSanitizer.Sanitize("<img src=\"./img/a b.png\" alt=\"\">", new SanitizeOptions(null, ImageMode.Preview, "doc.example")).Html;
        Assert.Contains("src=\"https://doc.example/img/a%20b.png\"", html);
    }

    [Fact]
    public void Preview_blocks_images_when_document_has_no_folder() =>
        Assert.Equal(1, HtmlSanitizer.Sanitize("<img src=\"a.png\">", new SanitizeOptions(null, ImageMode.Preview, null)).BlockedImages);

    [Theory]
    [InlineData("https://doc.example/img/a%20b.png", "img\\a b.png")]
    [InlineData("https://DOC.example/a.JPG?x=1", "a.JPG")]
    public void Image_requests_resolve_inside_the_folder(string uri, string expected) =>
        Assert.Equal("C:\\docs\\" + expected, HtmlSanitizer.ResolveImagePath("C:\\docs", "doc.example", uri));

    [Theory]
    [InlineData("https://doc.example/..%5Csecret.png")]
    [InlineData("https://doc.example/C:%5Csecret.png")]
    [InlineData("https://doc.example/a.txt")]
    [InlineData("https://doc.example/settings.json")]
    [InlineData("https://other.example/a.png")]
    [InlineData("http://doc.example/a.png")]
    [InlineData("not a uri")]
    public void Image_requests_outside_are_refused(string uri) =>
        Assert.Null(HtmlSanitizer.ResolveImagePath("C:\\docs", "doc.example", uri));

    [Theory]
    [InlineData("https://doc.example/../secret.png")]
    [InlineData("https://doc.example/%2e%2e/secret.png")]
    [InlineData("https://doc.example/a/../../../secret.png")]
    public void Dot_segments_cannot_leave_the_folder(string uri) =>
        Assert.Equal("C:\\docs\\secret.png", HtmlSanitizer.ResolveImagePath("C:\\docs", "doc.example", uri));

    [Fact]
    public void Script_in_markdown_is_not_executable()
    {
        var html = MarkdownRenderer.RenderPreview(new RenderRequest("<script>alert(1)</script>\n\n[x](javascript:alert(1))\n\n<details><summary>s</summary>\n\nb\n\n</details>\n")).Html;
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("<details>", html);
    }
}

public class CopyTests
{
    private static RichCopyResult Copy(string text) => MarkdownRenderer.RenderForCopy(new RenderRequest(text));

    [Fact]
    public void Styles_are_inline_and_internal_attributes_removed()
    {
        var html = Copy("| a |\n| --- |\n| 1 |\n").Html;
        Assert.Contains("<table style=", html);
        Assert.Contains("border:1px solid", html);
        Assert.DoesNotContain("data-line", html);
        Assert.DoesNotContain("<style", html);
    }

    [Fact]
    public void Details_is_expanded()
    {
        var result = Copy("<details><summary>Sum</summary>\n\nBody\n\n</details>\n");
        Assert.DoesNotContain("<details", result.Html);
        Assert.Contains("<p><strong>Sum</strong></p>", result.Html);
        Assert.Contains("Body", result.Html);
    }

    [Fact]
    public void Checkboxes_become_symbols()
    {
        var result = Copy("- [x] a\n- [ ] b\n");
        Assert.Contains("☑", result.Html);
        Assert.Contains("☐", result.Html);
        Assert.DoesNotContain("<input", result.Html);
    }

    [Fact]
    public void Local_images_are_replaced_and_counted()
    {
        var result = Copy("![alt](a.png) ![web](https://example.com/b.png)");
        Assert.Equal(1, result.LocalImages);
        Assert.DoesNotContain("a.png", result.Html);
        Assert.Contains("alt", result.Html);
        Assert.Contains("https://example.com/b.png", result.Html);
    }

    [Fact]
    public void Frontmatter_is_excluded() => Assert.DoesNotContain("title", Copy("---\ntitle: x\n---\n\nBody\n").PlainText);

    [Fact]
    public void Plain_text_has_list_numbers_and_tabs()
    {
        var text = Copy("# T\n\n1. a\n2. b\n\n- c\n\n| x | y |\n| --- | --- |\n| 1 | 2 |\n").PlainText;
        Assert.Contains("T\n", text);
        Assert.Contains("1. a\n2. b", text);
        Assert.Contains("- c", text);
        Assert.Contains("x\ty", text);
        Assert.Contains("1\t2", text);
    }

    [Fact]
    public void Relative_links_keep_only_the_text()
    {
        var html = Copy("[a](docs/a.md) [b](https://example.com/)").Html;
        Assert.DoesNotContain("docs/a.md", html);
        Assert.Contains("href=\"https://example.com/\"", html);
    }

    [Fact]
    public void Clipboard_offsets_point_to_the_fragment_in_utf8_bytes()
    {
        var bytes = RichHtmlConverter.BuildClipboardHtml("<p>日本語</p>");
        var text = Encoding.UTF8.GetString(bytes);
        int Value(string key)
        {
            var at = text.IndexOf(key, StringComparison.Ordinal) + key.Length;
            return int.Parse(text.Substring(at, 10), CultureInfo.InvariantCulture);
        }

        var fragment = Encoding.UTF8.GetString(bytes, Value("StartFragment:"), Value("EndFragment:") - Value("StartFragment:"));
        Assert.Equal("<p>日本語</p>", fragment);
        Assert.Equal(bytes.Length, Value("EndHTML:"));
        Assert.StartsWith("<!DOCTYPE", Encoding.UTF8.GetString(bytes, Value("StartHTML:"), 9));
    }
}

public class FenceTests
{
    [Fact]
    public void Detects_markdown_fence_and_unwraps_only_outer_lines()
    {
        const string Text = "```markdown\n# T\n\n```py\nx\n```\n\nend\n```\n";
        var fence = FenceDetector.Detect(Text);
        Assert.NotNull(fence);
        Assert.False(fence.IsAmbiguous);
        Assert.Equal("# T\n\n```py\nx\n```\n\nend\n\n", FenceDetector.Unwrap(Text, fence).Replace("end\n", "end\n\n"));
    }

    [Theory]
    [InlineData("# not fenced\n")]
    [InlineData("```py\nx\n```\n")]
    [InlineData("```markdown\nunclosed\n")]
    [InlineData("text\n```markdown\nx\n```\n")]
    [InlineData("")]
    public void Ignores_other_documents(string text) => Assert.Null(FenceDetector.Detect(text));

    [Fact]
    public void No_language_is_ambiguous() => Assert.True(FenceDetector.Detect("```\n# T\n```\n")!.IsAmbiguous);

    [Fact]
    public void Odd_inner_fence_is_ambiguous() => Assert.True(FenceDetector.Detect("```markdown\na\n```\nb\n```\n")!.IsAmbiguous);

    [Fact]
    public void Keeps_crlf_and_tilde_fence()
    {
        const string Text = "~~~md\r\n# T\r\n~~~\r\n";
        Assert.Equal("# T\r\n", FenceDetector.Unwrap(Text, FenceDetector.Detect(Text)!));
    }
}

public class DetectorTests
{
    [Fact]
    public void Reports_counts_and_skips_code()
    {
        const string Text = "> [!NOTE]\n> a\n\nb[^1] c[^2]\n\n[[w]]\n\n```\n[[not]] [^3]\n```\n\n\\[\nx\n\\]\n\n\\(y\\)\n\n:::note\n\n==z==\n";
        var detected = SyntaxDetector.Detect(Text).ToDictionary(d => d.Feature, d => d.Count);
        Assert.Equal(1, detected[SyntaxFeature.Alerts]);
        Assert.Equal(2, detected[SyntaxFeature.Footnotes]);
        Assert.Equal(1, detected[SyntaxFeature.WikiLinks]);
        Assert.Equal(2, detected[SyntaxFeature.Math]);
        Assert.Equal(1, detected[SyntaxFeature.Containers]);
        Assert.Equal(1, detected[SyntaxFeature.Highlight]);
    }

    [Fact]
    public void Active_features_are_not_reported() => Assert.Empty(SyntaxDetector.Detect("a[^1]", SyntaxFeature.Footnotes));

    [Fact]
    public void Prices_are_not_math() => Assert.Empty(SyntaxDetector.Detect("cost $5 or $6"));
}

public class NormalizerTests
{
    [Fact]
    public void Math_delimiters_are_unified_outside_code()
    {
        const string Text = "\\[\nx\n\\]\r\n\\(y\\) `\\(z\\)`\n\n```\n\\(k\\)\n```\n";
        var result = Normalizer.Apply(Text, SyntaxFeature.Math);
        Assert.Equal("$$\nx\n$$\r\n$y$ `\\(z\\)`\n\n```\n\\(k\\)\n```\n", result.Text);
        Assert.Equal(3, result.Changes.Count);
    }

    [Fact]
    public void Wiki_links_become_markdown_links()
    {
        var result = Normalizer.Apply("[[a b|c]] [[d]]\n", SyntaxFeature.WikiLinks);
        Assert.Equal("[c](a%20b) [d](d)\n", result.Text);
        Assert.False(result.MayLoseMeaning);
    }

    [Fact]
    public void Containers_become_quotes_with_warning()
    {
        var result = Normalizer.Apply(":::note\nhello\n\nworld\n:::\nafter\n", SyntaxFeature.Containers);
        Assert.Equal("> **note**\n> hello\n>\n> world\n\nafter\n", result.Text);
        Assert.True(result.MayLoseMeaning);
    }

    [Fact]
    public void Unchanged_text_has_no_changes()
    {
        var result = Normalizer.Apply("plain\n", SyntaxFeature.Math);
        Assert.Equal("plain\n", result.Text);
        Assert.Empty(result.Changes);
    }
}

public class FormatterTests
{
    private static string Apply(string text, TextEdit edit) => text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    private static (string Text, string Selected) Run(string text, int start, int end, Func<string, int, int, TextEdit> action)
    {
        var edit = action(text, start, end);
        var result = Apply(text, edit);
        return (result, result[edit.SelectionStart..edit.SelectionEnd]);
    }

    [Fact]
    public void Bold_wraps_selection_and_unwraps_on_second_press()
    {
        var (text, selected) = Run("重要です", 0, 2, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Bold));
        Assert.Equal("**重要**です", text);
        Assert.Equal("重要", selected);
        var (back, _) = Run(text, 2, 4, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Bold));
        Assert.Equal("重要です", back);
    }

    [Fact]
    public void Selection_including_markers_is_unwrapped()
    {
        var (text, _) = Run("**a**", 0, 5, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Bold));
        Assert.Equal("a", text);
    }

    [Fact]
    public void Italic_and_bold_are_distinguished()
    {
        Assert.Equal("***a***", Run("**a**", 2, 3, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Italic)).Text);
        Assert.Equal("**a**", Run("***a***", 3, 4, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Italic)).Text);
        Assert.Equal("a", Run("*a*", 1, 2, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Italic)).Text);
    }

    [Fact]
    public void Empty_selection_inserts_markers_and_caret_between()
    {
        var edit = TextFormatter.ToggleInline("ab", 1, 1, InlineStyle.Bold);
        Assert.Equal("a****b", Apply("ab", edit));
        Assert.Equal(3, edit.SelectionStart);
        var undo = TextFormatter.ToggleInline("a****b", 3, 3, InlineStyle.Bold);
        Assert.Equal("ab", Apply("a****b", undo));
    }

    [Fact]
    public void Surrounding_spaces_stay_outside() =>
        Assert.Equal("a **b** c", Run("a b c", 1, 4, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Bold)).Text);

    [Fact]
    public void Multiple_lines_are_wrapped_per_line_keeping_eol()
    {
        Assert.Equal("**a**\r\n\r\n  **b**", Run("a\r\n\r\n  b", 0, 8, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Bold)).Text);
    }

    [Fact]
    public void Inline_code_with_backtick_uses_longer_fence() =>
        Assert.Equal("`` a`b ``", Run("a`b", 0, 3, (t, s, e) => TextFormatter.ToggleInline(t, s, e, InlineStyle.Code)).Text);

    [Fact]
    public void Ineffective_emphasis_is_reported()
    {
        Assert.True(TextFormatter.ToggleInline("「重要」です", 0, 4, InlineStyle.Bold).EmphasisIneffective);
        Assert.False(TextFormatter.ToggleInline("重要です", 0, 2, InlineStyle.Bold).EmphasisIneffective);
    }

    [Fact]
    public void Heading_is_set_replaced_and_removed()
    {
        Assert.Equal("## 作業結果", Run("作業結果", 2, 2, (t, s, e) => TextFormatter.SetHeading(t, s, e, 2)).Text);
        Assert.Equal("### a", Run("## a", 3, 3, (t, s, e) => TextFormatter.SetHeading(t, s, e, 3)).Text);
        Assert.Equal("a", Run("## a", 3, 3, (t, s, e) => TextFormatter.SetHeading(t, s, e, 2)).Text);
        Assert.Equal("a", Run("## a", 3, 3, (t, s, e) => TextFormatter.SetHeading(t, s, e, 0)).Text);
    }

    [Fact]
    public void Caret_stays_on_the_same_text_after_line_change()
    {
        var edit = TextFormatter.SetHeading("abc", 1, 1, 2);
        Assert.Equal(4, edit.SelectionStart);
    }

    [Fact]
    public void Lists_toggle_on_selected_lines()
    {
        const string Text = "a\nb\n\nc";
        Assert.Equal("- a\n- b\n\n- c", Run(Text, 0, Text.Length, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.BulletList)).Text);
        Assert.Equal("1. a\n2. b\n\n3. c", Run(Text, 0, Text.Length, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.NumberedList)).Text);
        Assert.Equal("- [ ] a\n- [ ] b\n\n- [ ] c", Run(Text, 0, Text.Length, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.TaskList)).Text);
        Assert.Equal("a\nb", Run("- a\n- b", 0, 7, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.BulletList)).Text);
    }

    [Fact]
    public void List_types_replace_each_other_and_keep_indent()
    {
        Assert.Equal("  1. a", Run("  - a", 0, 5, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.NumberedList)).Text);
        Assert.Equal("- a", Run("- [ ] a", 0, 7, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.BulletList)).Text);
    }

    [Theory]
    [InlineData("1. a\n", "1. a\n2. ")]
    [InlineData("1. a\n2. b\n", "1. a\n2. b\n3. ")]
    [InlineData("  3) a\n  ", "  3) a\n  4. ")]
    [InlineData("1. a\n\n", "1. a\n\n1. ")]
    [InlineData("  1. a\n", "  1. a\n1. ")]
    [InlineData("text\n", "text\n1. ")]
    public void Numbered_list_continues_from_the_line_above(string text, string expected)
    {
        var edit = TextFormatter.ToggleLineStyle(text, text.Length, text.Length, LineStyle.NumberedList);
        Assert.Equal(expected, Apply(text, edit));
        Assert.Equal(expected.Length, edit.SelectionStart);
    }

    [Fact]
    public void Numbered_selection_continues_and_can_still_be_removed()
    {
        const string Text = "1. a\nb\nc";
        Assert.Equal("1. a\n2. b\n3. c", Run(Text, 5, 8, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.NumberedList)).Text);
        Assert.Equal("1. a\nb", Run("1. a\n2. b", 5, 9, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.NumberedList)).Text);
    }

    [Fact]
    public void Code_fence_is_closed_on_a_blank_line_or_after_the_current_line()
    {
        const string Blank = "```py\nx\n";
        var edit = TextFormatter.ToggleCodeFence(Blank, Blank.Length, Blank.Length, "\n")!;
        Assert.Equal("```py\nx\n```", Apply(Blank, edit));
        Assert.Equal(Blank.Length + 3, edit.SelectionStart);

        const string Middle = "````\nx\ny";
        Assert.Equal("````\nx\n````\ny", Apply(Middle, TextFormatter.ToggleCodeFence(Middle, 6, 6, "\n")!));
        Assert.Equal("```\r\n```", Apply("```", TextFormatter.ToggleCodeFence("```", 3, 3, "\r\n")!));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(11)]
    public void Closed_code_fence_is_removed_keeping_the_content(int caret)
    {
        const string Text = "```py\nabc\n```\nafter";
        var edit = TextFormatter.ToggleCodeFence(Text, caret, caret, "\n")!;
        Assert.Equal("abc\nafter", Apply(Text, edit));
        Assert.Equal("abc\n", Apply(Text, edit)[edit.SelectionStart..edit.SelectionEnd]);
    }

    [Fact]
    public void Code_fence_toggle_is_null_outside_a_block()
    {
        Assert.Null(TextFormatter.ToggleCodeFence("plain", 2, 2, "\n"));
        Assert.Null(TextFormatter.ToggleCodeFence("```\na\n```\nafter", 13, 13, "\n"));
    }

    [Fact]
    public void Line_after_selection_end_at_column_zero_is_not_changed() =>
        Assert.Equal("- a\nb", Run("a\nb", 0, 2, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.BulletList)).Text);

    [Fact]
    public void Quote_toggles_including_blank_lines()
    {
        Assert.Equal("> a\n>\n> b", Run("a\n\nb", 0, 4, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.Quote)).Text);
        Assert.Equal("a", Run("> a", 0, 3, (t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, LineStyle.Quote)).Text);
    }

    [Fact]
    public void Table_matches_the_specified_format()
    {
        var edit = TextFormatter.InsertTable(string.Empty, 0, 0, 3, 2, "列{0}", "\n");
        Assert.Equal("| 列1 | 列2 | 列3 |\n| --- | --- | --- |\n|     |     |     |\n|     |     |     |\n", edit.Replacement);
        Assert.Equal("列1", edit.Replacement[edit.SelectionStart..edit.SelectionEnd]);
    }

    [Fact]
    public void Table_is_separated_from_surrounding_text_with_document_eol()
    {
        var result = Apply("ab", TextFormatter.InsertTable("ab", 1, 1, 1, 0, "c{0}", "\r\n"));
        Assert.Equal("a\r\n\r\n| c1 |\r\n| --- |\r\n\r\nb", result);
    }

    [Fact]
    public void Link_and_image_are_inserted()
    {
        Assert.Equal("[a\\]](https://e.example/)", Apply(string.Empty, TextFormatter.InsertLink(string.Empty, 0, 0, "a]", "https://e.example/", false)));
        Assert.Equal("![a](<b c.png>)", Apply(string.Empty, TextFormatter.InsertLink(string.Empty, 0, 0, "a", "b c.png", true)));
    }

    [Fact]
    public void Code_block_wraps_selection_with_a_longer_fence_when_needed()
    {
        var edit = TextFormatter.InsertCodeBlock("x", 0, 1, "py", "\n");
        Assert.Equal("```py\nx\n```\n", Apply("x", edit));
        Assert.Equal("````\n```\n````\n", Apply("```", TextFormatter.InsertCodeBlock("```", 0, 3, string.Empty, "\n")));
    }

    [Fact]
    public void Eol_is_taken_from_the_document_mode()
    {
        Assert.Equal("\r\n", TextFormatter.FindEol(0));
        Assert.Equal("\r", TextFormatter.FindEol(1));
        Assert.Equal("\n", TextFormatter.FindEol(2));
    }
}

public class ReviewRegressionTests
{
    private static string Apply(string text, TextEdit edit) => text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    // R01: 正規化と検出は、プレビューがコードやFrontmatterとして扱う範囲を変更しない
    [Theory]
    [InlineData("    [[example]]\n")]
    [InlineData("> ```\n> [[example]]\n> ```\n")]
    [InlineData("``[[example]]``\n")]
    [InlineData("---\ntitle: [[example]]\n---\n\nbody\n")]
    [InlineData("- item\n\n      [[example]]\n")]
    [InlineData("| a |\n| --- |\n| `[[example]]` |\n")]
    [InlineData("```\n[[example]]\n")]
    public void Protected_ranges_are_not_normalized_or_detected(string text)
    {
        Assert.Equal(text, Normalizer.Apply(text, SyntaxFeature.WikiLinks).Text);
        Assert.DoesNotContain(SyntaxDetector.Detect(text), d => d.Feature == SyntaxFeature.WikiLinks);
    }

    [Fact]
    public void Text_outside_protected_ranges_is_still_normalized()
    {
        const string Text = "`[[a]]` [[b]] ``[[c]]`` [[d]]\r\n";
        Assert.Equal("`[[a]]` [b](b) ``[[c]]`` [d](d)\r\n", Normalizer.Apply(Text, SyntaxFeature.WikiLinks).Text);
    }

    [Fact]
    public void Math_inside_protected_ranges_is_unchanged() =>
        Assert.Equal("    \\(x\\)\n", Normalizer.Apply("    \\(x\\)\n", SyntaxFeature.Math).Text);

    [Fact]
    public void Container_quotes_code_inside_but_does_not_open_inside_code()
    {
        var quoted = Normalizer.Apply(":::note\n```\nx\n```\n:::\n", SyntaxFeature.Containers).Text;
        Assert.Equal("> **note**\n> ```\n> x\n> ```\n\n", quoted);
        const string InCode = "```\n:::note\n```\n";
        Assert.Equal(InCode, Normalizer.Apply(InCode, SyntaxFeature.Containers).Text);
    }

    // R03: 次の行頭まで選択しても、改行の種類によらず選択した行すべてに適用する
    [Theory]
    [InlineData("a\r\nb\r\nc", 6, "- a\r\n- b\r\nc")]
    [InlineData("a\nb\nc", 4, "- a\n- b\nc")]
    [InlineData("a\rb\rc", 4, "- a\r- b\rc")]
    [InlineData("a\r\nb\r\n", 6, "- a\r\n- b\r\n")]
    [InlineData("a\r\nb", 4, "- a\r\n- b")]
    public void Selection_ending_at_a_line_start_covers_the_selected_lines(string text, int end, string expected) =>
        Assert.Equal(expected, Apply(text, TextFormatter.ToggleLineStyle(text, 0, end, LineStyle.BulletList)));

    [Fact]
    public void Heading_also_handles_crlf_selection() =>
        Assert.Equal("# a\r\n# b\r\nc", Apply("a\r\nb\r\nc", TextFormatter.SetHeading("a\r\nb\r\nc", 0, 6, 1)));

    // R04: 行単位の操作は、同じ行の複数選択を1つにまとめる
    [Fact]
    public void Selections_on_the_same_line_are_merged_for_line_operations()
    {
        var kept = TextFormatter.OnePerLine(
        [
            new SelectionSpan(0, 3, 0, 0),
            new SelectionSpan(4, 7, 0, 0),
            new SelectionSpan(8, 9, 1, 1),
            new SelectionSpan(10, 20, 2, 4),
            new SelectionSpan(21, 22, 4, 4),
        ]);
        Assert.Equal([21, 8, 4], kept.Select(s => s.Start));
    }

    // R08: コードボタンの2回押しで元に戻る
    [Theory]
    [InlineData("a`b")]
    [InlineData("a``b")]
    [InlineData("x")]
    public void Inline_code_round_trips_with_the_returned_selection(string original)
    {
        var first = TextFormatter.ToggleInline(original, 0, original.Length, InlineStyle.Code);
        var wrapped = Apply(original, first);
        Assert.Equal(original, Apply(wrapped, TextFormatter.ToggleInline(wrapped, first.SelectionStart, first.SelectionEnd, InlineStyle.Code)));
    }

    [Fact]
    public void Inline_code_with_long_delimiters_unwraps_when_they_are_selected()
    {
        const string Text = "`` a`b ``";
        Assert.Equal("a`b", Apply(Text, TextFormatter.ToggleInline(Text, 0, Text.Length, InlineStyle.Code)));
    }

    // R10: プレーンテキストの番号はHTMLの開始番号と一致する
    [Fact]
    public void Plain_text_keeps_the_list_start_number()
    {
        var text = MarkdownRenderer.RenderForCopy(new RenderRequest("3. three\n4. four\n\n- b\n\n1. one\n\n   5. nested\n")).PlainText;
        Assert.Contains("3. three\n4. four", text);
        Assert.Contains("- b", text);
        Assert.Contains("1. one", text);
        Assert.Contains("  5. nested", text);
    }

    [Fact]
    public void Plain_text_keeps_text_next_to_the_marker_in_loose_lists()
    {
        var text = MarkdownRenderer.RenderForCopy(new RenderRequest("1. one\n\n2. two\n\n- x\n\n  y\n")).PlainText;
        Assert.Contains("1. one\n2. two", text);
        Assert.Contains("- x\ny", text);
    }

    // 複数行でも効かない強調を警告する
    [Fact]
    public void Multiline_emphasis_reports_ineffective_lines()
    {
        Assert.True(TextFormatter.ToggleInline("通常\n「重要」です", 0, 7, InlineStyle.Bold).EmphasisIneffective);
        Assert.False(TextFormatter.ToggleInline("通常\n重要", 0, 5, InlineStyle.Bold).EmphasisIneffective);
    }

    [Fact]
    public void Setting_names_are_case_insensitive() =>
        Assert.Equal(["nav"], AppSettings.FromJson("{\"extraHtmlTags\":[\"nav\"]}").ExtraHtmlTags);
}

public class IconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Icon_has_transparent_corners_and_a_white_mark_on_green(int size)
    {
        foreach (var dark in new[] { false, true })
        {
            var pixels = IconPainter.Render(size, dark);
            Assert.Equal(size * size * 4, pixels.Length);
            Assert.Equal(0, pixels[3]);

            byte[] At(int x, int y) => pixels.AsSpan(((y * size) + x) * 4, 4).ToArray();
            var edge = At(size / 2, size / 16);
            Assert.True(edge[1] > edge[0] && edge[1] > edge[2], "background is green");
            Assert.Equal(255, edge[3]);
            var stem = At(size / 4, size / 2);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, stem);
        }
    }
}

public class SettingsTests
{
    [Fact]
    public void Language_resolves_from_setting_or_windows()
    {
        Assert.Equal("ja", AppSettings.ResolveLanguage("system", new CultureInfo("ja-JP")));
        Assert.Equal("en", AppSettings.ResolveLanguage("system", new CultureInfo("fr-FR")));
        Assert.Equal("en", AppSettings.ResolveLanguage("en", new CultureInfo("ja-JP")));
        Assert.Equal("ja", AppSettings.ResolveLanguage("ja", new CultureInfo("en-US")));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new AppSettings { Language = "ja", FollowCaret = false, DefaultFeatures = SyntaxFeature.Alerts | SyntaxFeature.Footnotes, ExtraHtmlTags = ["nav"], PanelVisible = true };
        var copy = AppSettings.FromJson(AppSettings.ToJson(settings));
        Assert.Equal("ja", copy.Language);
        Assert.False(copy.FollowCaret);
        Assert.Equal(SyntaxFeature.Alerts | SyntaxFeature.Footnotes, copy.DefaultFeatures);
        Assert.Equal(["nav"], copy.ExtraHtmlTags);
        Assert.True(copy.PanelVisible);
    }

    [Fact]
    public void Preview_engine_defaults_to_auto_and_round_trips()
    {
        Assert.Equal("auto", new AppSettings().PreviewEngine);
        Assert.Equal("ie", AppSettings.FromJson("{\"PreviewEngine\":\"ie\"}").PreviewEngine);
        Assert.Equal("auto", AppSettings.FromJson("{\"WebView2DataFolder\":\"C:\\\\Temp\"}").PreviewEngine);
    }

    [Fact]
    public void Legacy_preview_has_no_links_scripts_or_loadable_images()
    {
        const string Text = "[web](https://example.com/) [bad](javascript:alert(1)) ![a](a.png) ![b](https://example.com/b.png)\n\n<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n# T\n";
        var html = MarkdownRenderer.RenderLegacyPreview(new RenderRequest(Text));
        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("href", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("onerror=", html.Replace("&lt;img src=x onerror=alert(1)&gt;", string.Empty));
        Assert.Contains("<span class=\"link\"", html);
        Assert.Contains("web</span>", html);
        Assert.Contains("<h1", html);
    }

    [Fact]
    public void Every_string_has_both_languages() => Assert.Equal(Strings.All("en").Count(), Strings.All("ja").Count());

    [Fact]
    public void Feature_help_exists_for_every_feature()
    {
        foreach (var feature in Enum.GetValues<SyntaxFeature>().Where(f => f != SyntaxFeature.None))
        {
            var key = Strings.FeatureKey(feature) + "Help";
            Assert.NotEqual(key, Strings.Get("ja", key));
        }
    }

    [Fact]
    public void Follow_caret_is_on_by_default_and_for_old_settings()
    {
        Assert.True(new AppSettings().FollowCaret);
        Assert.True(AppSettings.FromJson("{\"Language\": \"ja\"}").FollowCaret);
    }

    [Fact]
    public void Selection_copy_detects_definitions_outside_the_selection()
    {
        const string Document = "See [the docs][Docs] and a note[^1].\n\n[docs]: https://example.com/\n[^1]: Note text.\n";
        Assert.True(FragmentCheck.UsesOutsideDefinitions(Document, "See [the docs][Docs].", SyntaxFeature.None));
        Assert.True(FragmentCheck.UsesOutsideDefinitions(Document, "a note[^1].", SyntaxFeature.Footnotes));

        // 脚注を解釈しない設定では、脚注の定義の有無を知らせない
        Assert.False(FragmentCheck.UsesOutsideDefinitions(Document, "a note[^1].", SyntaxFeature.None));

        // 定義も選択に含まれていれば、知らせない
        Assert.False(FragmentCheck.UsesOutsideDefinitions(Document, Document, SyntaxFeature.Footnotes));
        Assert.False(FragmentCheck.UsesOutsideDefinitions(Document, "Plain text only.", SyntaxFeature.Footnotes));
    }

    [Fact]
    public void Selection_copy_renders_only_the_fragment()
    {
        const string Fragment = "| a | b |\n| --- | --- |\n| 1 | ~~2~~ |\n";
        var result = MarkdownRenderer.RenderForCopy(new RenderRequest(Fragment, StripOuterFence: false));
        Assert.Contains("<table", result.Html);
        Assert.Contains("<del>", result.Html);
    }
}

/// <summary>0.14.0の機能追加（F1〜F8）とレビュー指摘の修正（R1〜R7）。</summary>
public class Release014Tests
{
    private static string Apply(string text, TextEdit edit) => text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    private static string Preview(string text, SyntaxFeature features = SyntaxFeature.None, string? host = null) =>
        MarkdownRenderer.RenderPreview(new RenderRequest(text, features, ImageHost: host)).Html;

    // --- F2 チェックボックス ---

    [Theory]
    [InlineData("- [ ] a", "- [x] a")]
    [InlineData("* [x] b", "* [ ] b")]
    [InlineData("+ [X] c", "+ [ ] c")]
    [InlineData("1. [ ] d", "1. [x] d")]
    [InlineData("12) [x] e", "12) [ ] e")]
    [InlineData("  - [ ] f", "  - [x] f")]
    [InlineData("-\t[ ] g", "-\t[x] g")]
    [InlineData("123456789. [ ] h", "123456789. [x] h")]
    [InlineData("- [ ]", "- [x]")]
    public void Task_mark_toggles_one_character(string line, string expected)
    {
        var toggled = TextFormatter.ToggleTaskMark(line);
        Assert.Equal(expected, toggled);
        Assert.Equal(1, line.Zip(toggled!).Count(p => p.First != p.Second));
    }

    [Theory]
    [InlineData("1234567890. [ ] i")]
    [InlineData("> - [ ] j")]
    [InlineData("-[ ] k")]
    [InlineData("- [y] l")]
    [InlineData("本文 [ ] m")]
    public void Non_task_lines_are_not_toggled(string line) => Assert.Null(TextFormatter.ToggleTaskMark(line));

    [Fact]
    public void Preview_checkboxes_are_clickable_but_legacy_and_copy_are_disabled()
    {
        const string Text = "- [ ] a\n- [x] b\n";
        var preview = Preview(Text);
        Assert.Equal(2, Regex.Matches(preview, "type=\"checkbox\"").Count);
        Assert.DoesNotContain("disabled", preview);
        Assert.Matches("<li[^>]*data-line=\"0\"[^>]*>\\s*<input[^>]*type=\"checkbox\"", preview);
        Assert.Matches("<li[^>]*data-line=\"1\"[^>]*>\\s*<input[^>]*checked", preview);

        var legacy = MarkdownRenderer.RenderLegacyPreview(new RenderRequest(Text));
        Assert.Equal(2, Regex.Matches(legacy, "disabled").Count);
        Assert.DoesNotContain("<input", MarkdownRenderer.RenderForCopy(new RenderRequest(Text)).Html);
    }

    // --- F3 見出しのシフト ---

    private const string HeadingDocument = "---\n# yaml\n---\n# A\n## B\n###### F\n   ### S3\n    #### S4\n#tag\n####### G7\n> # Q\n- # L\nSetext\n===\n```\n# C\n```\n~~~\n## U\n";

    [Fact]
    public void Headings_are_promoted_outside_code_and_frontmatter()
    {
        var edit = TextFormatter.ShiftHeadings(HeadingDocument, 0, 0, -1)!;
        var result = Apply(HeadingDocument, edit);
        Assert.Equal(HeadingDocument.Replace("## B", "# B").Replace("###### F", "##### F").Replace("   ### S3", "   ## S3"), result);
    }

    [Fact]
    public void Headings_are_demoted_and_crlf_is_kept()
    {
        var crlf = HeadingDocument.Replace("\n", "\r\n");
        var result = Apply(crlf, TextFormatter.ShiftHeadings(crlf, 0, 0, 1)!);
        Assert.Equal(crlf.Replace("# A\r", "## A\r").Replace("## B", "### B").Replace("   ### S3", "   #### S3"), result);
    }

    [Fact]
    public void Heading_shift_without_change_is_null()
    {
        Assert.Null(TextFormatter.ShiftHeadings("# A\n", 0, 0, -1));
        Assert.Null(TextFormatter.ShiftHeadings("###### F\n", 0, 0, 1));
        Assert.Null(TextFormatter.ShiftHeadings("text\n", 0, 0, 1));

        // 1行目が --- でなければFrontmatterではない
        Assert.Equal("\n---\n## x\n---\n", Apply("\n---\n# x\n---\n", TextFormatter.ShiftHeadings("\n---\n# x\n---\n", 0, 0, 1)!));
    }

    [Fact]
    public void Heading_shift_on_selection_keeps_the_selection_on_the_same_characters()
    {
        const string Text = "# A\n## B\n### C\n#### D";
        var anchor = Text.IndexOf('B');
        var caret = Text.IndexOf('C') + 1;
        var edit = TextFormatter.ShiftHeadings(Text, anchor, caret, 1)!;
        var result = Apply(Text, edit);
        Assert.Equal("# A\n### B\n#### C\n#### D", result);
        Assert.Equal('B', result[edit.SelectionStart]);
        Assert.Equal('C', result[edit.SelectionEnd - 1]);
    }

    // --- F4 表の整形 ---

    private static string Format(string text, int caret, string eol = "\n")
    {
        var edit = TextFormatter.FormatTable(text, caret, caret, eol);
        Assert.NotNull(edit);
        return Apply(text, edit!);
    }

    [Fact]
    public void Table_columns_are_aligned_with_wide_characters_counted_as_two()
    {
        const string Text = "|名前|説明|\n|---|---|\n|りんご|赤い果物|\n|a|b|";
        var edit = TextFormatter.FormatTable(Text, Text.IndexOf("果物", StringComparison.Ordinal), Text.IndexOf("果物", StringComparison.Ordinal), "\n")!;
        var result = Apply(Text, edit);
        Assert.Equal("| 名前   | 説明     |\n| ------ | -------- |\n| りんご | 赤い果物 |\n| a      | b        |", result);
        Assert.Equal("赤い果物", result.Substring(edit.SelectionStart, 4));
        Assert.Equal("| 名前   | 説明     |\r\n| ------ | -------- |\r\n| りんご | 赤い果物 |\r\n| a      | b        |", Format(Text, 1, "\r\n"));
    }

    [Fact]
    public void Table_alignment_escapes_and_code_spans_are_kept()
    {
        Assert.Equal(
            "| 左       | 中央   | 右   | なし |\n| :------- | :----: | ---: | ---- |\n| あいうえ | x      | 1    | 2    |\n| abcd     | abcdef | abcd | abcd |",
            Format("|左|中央|右|なし|\n|:---|:---:|---:|---|\n|あいうえ|x|1|2|\n|abcd|abcdef|abcd|abcd|", 1));
        var code = Format("|列1|列2|\n|---|---|\n|a \\| b|`x|y`|", 1);
        Assert.EndsWith("| a \\| b | `x|y` |", code);
    }

    [Fact]
    public void Table_cells_are_filled_or_kept_and_indent_is_preserved()
    {
        Assert.Equal("| aaa | bbb | ccc |\n| --- | --- | --- |\n| 1   |     |     |\n| 1   | 2   | 3   | 4 |", Format("|aaa|bbb|ccc|\n|---|---|---|\n|1|\n|1|2|3|4|", 1));
        Assert.Equal("  | 項目 | 数値 |\n  | ---- | ---- |\n  | 1    | 23   |", Format("  |項目|数値|\n  |---|---|\n  |1|23|", 3));
        Assert.Equal("| name | value |\n| ---- | ----- |\n| x    | 10    |", Format("name | value\n--- | ---\nx | 10", 0));
    }

    [Fact]
    public void Formatted_table_returns_an_empty_edit()
    {
        const string Text = "| a   | b   |\n| --- | --- |\n| 1   | 2   |";
        var edit = TextFormatter.FormatTable(Text, 2, 2, "\n")!;
        Assert.Equal(0, edit.Length);
        Assert.Equal(string.Empty, edit.Replacement);
    }

    [Theory]
    [InlineData("|a|b|\n|---|---|---|\n|1|2|", 0)]
    [InlineData("|a|b|\n|1|2|", 7)]
    [InlineData("a|b\n|h|i|\n|---|---|\n|1|2|", 0)]
    [InlineData("```\n|a|b|\n|---|---|\n|1|2|\n```", 5)]
    [InlineData("---\n|a|b|\n|---|---|\n---", 5)]
    [InlineData("> |a|b|\n> |---|---|\n> |1|2|", 0)]
    [InlineData("|a|b|\n|---|---|\n\n本文", 18)]
    public void Non_tables_are_not_formatted(string text, int caret) => Assert.Null(TextFormatter.FormatTable(text, caret, caret, "\n"));

    [Fact]
    public void Display_width_counts_fullwidth_and_wide_as_two()
    {
        Assert.Equal(2, DisplayWidth.Of("Ａ"));
        Assert.Equal(2, DisplayWidth.Of("😀"));
        Assert.Equal(2, DisplayWidth.Of("한"));
        Assert.Equal(2, DisplayWidth.Of("e\u0301"));
        Assert.Equal(1, DisplayWidth.Of("①"));
        Assert.Equal(1, DisplayWidth.Of("ｱ"));
    }

    // --- F5 画像の貼り付け ---

    [Fact]
    public void Pasted_image_names_are_dated_unique_and_validated()
    {
        var now = new DateTime(2026, 11, 19, 9, 5, 3);
        Assert.Equal("メモ-20261119-090503.png", PastedImage.DefaultName("C:\\t\\doc\\メモ.md", now));
        Assert.Equal("my note-20261119-090503.png", PastedImage.DefaultName("C:\\t\\doc\\my note.markdown", now));

        HashSet<string> existing = [];
        Assert.Equal("x.png", PastedImage.MakeUnique("x.png", existing.Contains));
        existing.Add("x.png");
        Assert.Equal("x-2.png", PastedImage.MakeUnique("x.png", existing.Contains));
        existing.Add("x-2.png");
        Assert.Equal("x-3.png", PastedImage.MakeUnique("x.png", existing.Contains));

        Assert.Equal("photo.png", PastedImage.Validate("photo"));
        Assert.Equal("photo.png", PastedImage.Validate("photo.png"));
        Assert.Equal("photo.PNG", PastedImage.Validate("photo.PNG"));
        Assert.Equal("photo.jpg.png", PastedImage.Validate("photo.jpg"));
        Assert.Equal("写真 1.png", PastedImage.Validate("写真 1"));
        Assert.Equal("my.image.png", PastedImage.Validate("my.image"));
        foreach (var bad in new[] { "a/b", "a\\b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b", "a\tb", "CON", "nul", "COM1", "CON.png", "name ", "name.", ".", "..", "", "  " })
        {
            Assert.Null(PastedImage.Validate(bad));
        }

        Assert.Equal("images/x.png", PastedImage.RelativeUrl("x.png"));
        Assert.Equal("![代替](images/x.png)", TextFormatter.InsertLink(string.Empty, 0, 0, "代替", "images/x.png", true).Replacement);
        Assert.Equal("![代替](<images/写真 1.png>)", TextFormatter.InsertLink(string.Empty, 0, 0, "代替", "images/写真 1.png", true).Replacement);
    }

    // --- F6 相対リンクの .md ---

    [Theory]
    [InlineData("other.md", "C:\\t\\doc\\other.md")]
    [InlineData("other.md#節", "C:\\t\\doc\\other.md")]
    [InlineData("other.md?x=1#a", "C:\\t\\doc\\other.md")]
    [InlineData("sub/../other.md", "C:\\t\\doc\\other.md")]
    [InlineData("sub/third.markdown", "C:\\t\\doc\\sub\\third.markdown")]
    [InlineData("a.mdown", "C:\\t\\doc\\a.mdown")]
    [InlineData("a.mkd", "C:\\t\\doc\\a.mkd")]
    [InlineData("OTHER.MD", "C:\\t\\doc\\OTHER.MD")]
    [InlineData("other%20file.md", "C:\\t\\doc\\other file.md")]
    [InlineData("%E7%AF%80.md", "C:\\t\\doc\\節.md")]
    public void Relative_markdown_links_resolve_inside_the_folder(string href, string expected) =>
        Assert.Equal(expected, HtmlSanitizer.ResolveLinkPath("C:\\t\\doc", href));

    [Theory]
    [InlineData("../other.md")]
    [InlineData("sub/../../other.md")]
    [InlineData("%2E%2E/other.md")]
    [InlineData("%5C..%5Cother.md")]
    [InlineData("/other.md")]
    [InlineData("\\other.md")]
    [InlineData("C:/t/doc/other.md")]
    [InlineData("file:///C:/t/doc/other.md")]
    [InlineData("https://example.com/a.md")]
    [InlineData("other.txt")]
    [InlineData("other.md.exe")]
    [InlineData("#節")]
    [InlineData("")]
    public void Other_links_are_not_opened(string href) => Assert.Null(HtmlSanitizer.ResolveLinkPath("C:\\t\\doc", href));

    // --- F7 HTMLをコピー ---

    [Fact]
    public void Copy_html_text_is_the_inline_styled_fragment()
    {
        const string Text = "---\ntitle: t\n---\n# 見出し\n\n![ローカル](a.png) ![外部](https://example.com/c.png)\n\n- [ ] 未\n- [x] 済\n\n|a|b|\n|---|---|\n|1|2|\n";
        var result = MarkdownRenderer.RenderForCopy(new RenderRequest(Text));
        Assert.DoesNotContain("<style", result.Html);
        Assert.DoesNotContain("title: t", result.Html);
        Assert.DoesNotContain("<input", result.Html);
        Assert.Contains("☐", result.Html);
        Assert.Contains("☑", result.Html);
        Assert.Contains("<td style=", result.Html);
        Assert.Equal(1, result.LocalImages);
        Assert.Contains("https://example.com/c.png", result.Html);
    }

    // --- F8 文字数 ---

    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("a", 1, 1, 1)]
    [InlineData("a\n", 1, 2, 1)]
    [InlineData("a\r\nb", 2, 2, 2)]
    [InlineData("a\rb", 2, 2, 2)]
    [InlineData("\r\n", 0, 2, 0)]
    [InlineData("x\n\ny", 2, 3, 2)]
    [InlineData("hello world", 11, 1, 2)]
    [InlineData("don't", 5, 1, 2)]
    [InlineData("a-b c_d", 7, 1, 4)]
    [InlineData(" \t ", 3, 1, 0)]
    [InlineData("日本語です", 5, 1, 5)]
    [InlineData("Hello世界", 7, 1, 3)]
    [InlineData("東京2025年", 7, 1, 4)]
    [InlineData("한국어", 3, 1, 3)]
    [InlineData("ｱｲｳ", 3, 1, 3)]
    [InlineData("々〆〇", 3, 1, 3)]
    [InlineData("１２３", 3, 1, 1)]
    [InlineData("e\u0301te", 4, 1, 1)]
    [InlineData("😀 ok", 4, 1, 1)]
    public void Text_stats_count_characters_lines_and_words(string text, int characters, int lines, int words) =>
        Assert.Equal(new TextCounts(characters, lines, words), TextStats.Count(text));

    // --- R1 解析の共用 ---

    [Theory]
    [InlineData("a[^1]\n\n```\n[^2] ==x==\n```\n\n> [!NOTE]\n> n\n\n`[[w]]` [[w]] ==h==\n\n:::note\nx\n:::\n\n---\n\nTerm\n: def\n", SyntaxFeature.None)]
    [InlineData("---\ntitle: '[^1]'\n---\n\n$$\n:::note\n$$\n\n\\(x\\) [^1]\n", SyntaxFeature.None)]
    [InlineData("a[^1] ==h==\n\n~~~\n==x==\n", SyntaxFeature.Footnotes)]
    public void Shared_parse_detects_the_same_syntax(string text, SyntaxFeature active)
    {
        var result = MarkdownRenderer.RenderPreview(new RenderRequest(text, active));
        Assert.NotNull(result.Document);
        Assert.Equal(SyntaxDetector.Detect(text, active), SyntaxDetector.Detect(result.Document!, text, active));
    }

    [Fact]
    public void Shared_parse_with_math_ignores_syntax_inside_display_math()
    {
        const string Text = "$$\n:::note\n$$\n";
        var result = MarkdownRenderer.RenderPreview(new RenderRequest(Text, SyntaxFeature.Math));
        Assert.Empty(SyntaxDetector.Detect(result.Document!, Text, SyntaxFeature.Math));
    }

    [Fact]
    public void Stripped_outer_fence_does_not_share_the_parse()
    {
        var result = MarkdownRenderer.RenderPreview(new RenderRequest("```markdown\n# a\n```\n", StripOuterFence: true));
        Assert.Null(result.Document);
    }

    // --- R3 設定 ---

    [Theory]
    [InlineData("\"Alerts, Footnotes\"", SyntaxFeature.Alerts | SyntaxFeature.Footnotes)]
    [InlineData("\"alerts,FOOTNOTES\"", SyntaxFeature.Alerts | SyntaxFeature.Footnotes)]
    [InlineData("[\"Alerts\", \"Footnotes\"]", SyntaxFeature.Alerts | SyntaxFeature.Footnotes)]
    [InlineData("\"Alerts, Bogus\"", SyntaxFeature.Alerts)]
    [InlineData("[\"Footnotes\", 3, true, \"Bogus\"]", SyntaxFeature.Footnotes)]
    [InlineData("5", SyntaxFeature.None)]
    [InlineData("true", SyntaxFeature.None)]
    [InlineData("{}", SyntaxFeature.None)]
    [InlineData("null", SyntaxFeature.None)]
    [InlineData("\"\"", SyntaxFeature.None)]
    [InlineData("\"None\"", SyntaxFeature.None)]
    [InlineData("\"Bogus\"", SyntaxFeature.None)]
    public void Unknown_default_features_do_not_reset_other_settings(string value, SyntaxFeature expected)
    {
        var json = "{\"Language\": \"ja\", \"DefaultFeatures\": " + value + ", \"PreviewEngine\": \"ie\", \"PanelVisible\": true, \"FollowCaret\": false, \"ExtraHtmlTags\": [\"u\"]}";
        var settings = AppSettings.FromJson(json);
        Assert.Equal(expected, settings.DefaultFeatures);
        Assert.Equal("ja", settings.Language);
        Assert.Equal("ie", settings.PreviewEngine);
        Assert.True(settings.PanelVisible);
        Assert.False(settings.FollowCaret);
        Assert.Equal(["u"], settings.ExtraHtmlTags);
    }

    [Fact]
    public void Default_features_are_written_in_the_old_form()
    {
        Assert.Contains("\"DefaultFeatures\": \"Alerts, Footnotes\"", AppSettings.ToJson(new AppSettings { DefaultFeatures = SyntaxFeature.Alerts | SyntaxFeature.Footnotes }));
        Assert.Contains("\"DefaultFeatures\": \"None\"", AppSettings.ToJson(new AppSettings()));
    }

    // --- R5 脚注と data-line の順 ---

    [Fact]
    public void Footnote_definitions_in_the_middle_are_rendered_at_the_end()
    {
        // 前提の文書化: 脚注の定義（2行目）は末尾の div.footnotes に出るため、data-line は文書順に増えない。
        // shell.html の scrollToLine は打ち切らずに全体から選び、.footnotes を除く。
        var html = Preview("本文1[^1]\n\n[^1]: 定義\n\n段落4\n", SyntaxFeature.Footnotes);
        var lines = Regex.Matches(html, "data-line=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal([0, 4, 2], lines);
        Assert.True(html.IndexOf("class=\"footnotes\"", StringComparison.Ordinal) < html.LastIndexOf("data-line=\"2\"", StringComparison.Ordinal));
    }

    // --- R6 生HTMLの data-line ---

    [Fact]
    public void Raw_html_data_line_attributes_are_removed()
    {
        const string Text = "# 本物\n\n<div data-line=\"30\">偽ブロック</div>\n\n<h2 data-line=\"0\">偽見出し</h2>\n\n本文 <span data-line=\"30\">偽インライン</span>\n\n<DIV DATA-LINE='30'>大文字</DIV>\n\n<div\ndata-line=\"7\" class=\"x\">改行</div>\n\n`<div data-line=\"1\">`\n\n```\n<div data-line=\"2\">\n```\n";
        var html = Preview(Text);
        Assert.Matches("<h1[^>]*data-line=\"0\"", html);
        Assert.DoesNotContain("data-line=\"30\"", html);
        Assert.DoesNotContain("data-line=\"7\"", html);
        Assert.DoesNotMatch("<h2[^>]*data-line", html);
        Assert.Contains("偽ブロック", html);
        Assert.Contains("class=\"x\"", html);
        Assert.Contains("&lt;div data-line=&quot;1&quot;&gt;", html);
        Assert.Contains("&lt;div data-line=&quot;2&quot;&gt;", html);
    }

    [Fact]
    public void Text_outside_tags_is_not_changed_when_removing_data_line() =>
        Assert.Equal("<p class=\"a\">use data-line=\"1\" here</p>", MarkdownRenderer.StripLineAttributes("<p class=\"a\" data-line=\"3\">use data-line=\"1\" here</p>"));

    // --- 0.14.0 コードレビュー（code-review-20261119.md）の回帰確認 ---

    [Theory]
    [InlineData(new byte[] { 0x61, 0x20, 0xF0, 0x9F, 0x98, 0x20, 0x62 }, 65001, false)] // 4バイト文字の途中で終わる（置き換え文字と同じ3バイト）
    [InlineData(new byte[] { 0x0A, 0xF0, 0x9F, 0x98 }, 65001, false)]
    [InlineData(new byte[] { 0xC0, 0xAF }, 65001, false)] // 過剰な長さの符号
    [InlineData(new byte[] { 0xEF, 0xBF, 0xBD }, 65001, true)] // 正当なU+FFFD
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80, 0x61 }, 65001, true)] // 絵文字
    [InlineData(new byte[] { 0x81, 0x30, 0x61 }, 932, false)] // CP932の不正な2バイト目
    [InlineData(new byte[] { 0x95, 0x5C, 0x8E, 0xA6 }, 932, true)] // CP932の「表示」
    [InlineData(new byte[] { 0x82 }, 932, false)] // 2バイト文字の途中で終わる
    public void R1_encoding_check_rejects_invalid_bytes_with_the_same_length(byte[] bytes, int codePage, bool expected)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = codePage == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(codePage);
        Assert.Equal(expected, EncodingCheck.RoundTrips(bytes, encoding));
    }

    private static byte[] Dib(int width, int height, int bitCount, int compression = 0, int headerSize = 40, int colorsUsed = 0, byte[]? extra = null, int? pixelBytes = null)
    {
        var stride = ((width * bitCount) + 31) / 32 * 4;
        var colors = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : colorsUsed) : colorsUsed;
        var masks = compression == DibLayout.BiBitFields && headerSize == 40 ? 12 : 0;
        var header = new byte[headerSize];
        BitConverter.GetBytes(headerSize).CopyTo(header, 0);
        BitConverter.GetBytes(width).CopyTo(header, 4);
        BitConverter.GetBytes(height).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 12);
        BitConverter.GetBytes((ushort)bitCount).CopyTo(header, 14);
        BitConverter.GetBytes(compression).CopyTo(header, 16);
        BitConverter.GetBytes(colorsUsed).CopyTo(header, 32);
        extra?.CopyTo(header, 40);
        return [.. header, .. new byte[masks + (colors * 4)], .. new byte[pixelBytes ?? stride * Math.Abs(height)]];
    }

    [Fact]
    public void R2_dib_layout_accepts_complete_bitmaps()
    {
        var bottomUp = DibLayout.Parse(Dib(3, 2, 24))!;
        Assert.Equal(40, bottomUp.BitsOffset);
        Assert.Equal(12, bottomUp.Stride);
        Assert.False(bottomUp.TopDown);
        Assert.True(DibLayout.Parse(Dib(3, -2, 24))!.TopDown);

        var indexed = DibLayout.Parse(Dib(5, 3, 8, colorsUsed: 4))!;
        Assert.Equal(40 + 16, indexed.BitsOffset);
        Assert.Equal(40 + 1024, DibLayout.Parse(Dib(5, 3, 8))!.BitsOffset);
        Assert.Equal(40 + 12, DibLayout.Parse(Dib(2, 2, 32, DibLayout.BiBitFields))!.BitsOffset);

        var masks = new byte[16];
        BitConverter.GetBytes(0x00FF0000u).CopyTo(masks, 0);
        BitConverter.GetBytes(0x0000FF00u).CopyTo(masks, 4);
        BitConverter.GetBytes(0x000000FFu).CopyTo(masks, 8);
        BitConverter.GetBytes(0xFF000000u).CopyTo(masks, 12);
        var v5 = DibLayout.Parse(Dib(1, 2, 32, DibLayout.BiBitFields, 124, extra: masks))!;
        Assert.Equal(124, v5.BitsOffset);
        Assert.True(v5.HasAlpha);
        Assert.False(DibLayout.Parse(Dib(1, 2, 32))!.HasAlpha);
    }

    [Fact]
    public void R2_dib_layout_rejects_truncated_or_unsupported_bitmaps()
    {
        var full = Dib(3, 2, 24);
        Assert.Null(DibLayout.Parse(full.AsSpan(0, full.Length - 1))); // 最終走査行の一部が欠けた
        var indexed = Dib(5, 3, 8);
        Assert.Null(DibLayout.Parse(indexed.AsSpan(0, 40 + 1000))); // 色表が切れた
        Assert.Null(DibLayout.Parse(Dib(2, 2, 32, DibLayout.BiBitFields).AsSpan(0, 46))); // マスクが切れた
        Assert.Null(DibLayout.Parse(Dib(1, 1, 24).AsSpan(0, 39)));
        var core = Dib(1, 1, 24);
        BitConverter.GetBytes(12).CopyTo(core, 0); // BITMAPCOREHEADER は扱わない
        Assert.Null(DibLayout.Parse(core));
        Assert.Null(DibLayout.Parse(Dib(1, 1, 8, compression: 1))); // RLE
        Assert.Null(DibLayout.Parse(Dib(1, 1, 24, compression: DibLayout.BiBitFields)));
        Assert.Null(DibLayout.Parse(Dib(1, 1, 8, colorsUsed: 257)));
        Assert.Null(DibLayout.Parse(Dib(0, 1, 24)));
        Assert.Null(DibLayout.Parse(Dib(1, 0, 24)));
        Assert.Null(DibLayout.Parse(Dib(1, 1, 12, pixelBytes: 4)));

        // 過大な大きさ（ヘッダーだけ）。桁あふれせずに拒否する
        var huge = Dib(1, 1, 32);
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 4);
        BitConverter.GetBytes(int.MinValue).CopyTo(huge, 8);
        Assert.Null(DibLayout.Parse(huge));
        BitConverter.GetBytes(30000).CopyTo(huge, 4);
        BitConverter.GetBytes(30000).CopyTo(huge, 8);
        Assert.Null(DibLayout.Parse(huge));
        var planes = Dib(1, 1, 24);
        BitConverter.GetBytes((ushort)2).CopyTo(planes, 12);
        Assert.Null(DibLayout.Parse(planes));
    }

    [Theory]
    [InlineData("shot#1.png")]
    [InlineData("shot%20one.png")]
    [InlineData("shot).png")]
    [InlineData("shot(2).png")]
    [InlineData("a&amp;b.png")]
    [InlineData("q?.png")]
    [InlineData("写真 1.png")]
    [InlineData("photo.PNG")]
    [InlineData("メモ#2-20261119-090503.png")]
    public void R7_inserted_image_link_resolves_to_the_saved_file(string name)
    {
        const string Folder = "C:\\t\\doc";
        const string Host = "img.markdowntrellis.invalid";
        var markdown = TextFormatter.InsertLink(string.Empty, 0, 0, "代替", PastedImage.RelativeUrl(name), true).Replacement;
        var html = Preview(markdown + "\n", host: Host);
        var src = Regex.Match(html, "<img[^>]*src=\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith("https://" + Host + "/", src);
        Assert.Equal(Path.Combine(Folder, "images", name), HtmlSanitizer.ResolveImagePath(Folder, Host, System.Net.WebUtility.HtmlDecode(src)));
        Assert.DoesNotContain(".png)", Regex.Replace(html, "<[^>]+>", string.Empty));
    }

    [Theory]
    [InlineData("COM¹")]
    [InlineData("com².png")]
    [InlineData("LPT³")]
    [InlineData("COM0")]
    [InlineData("lpt0.png")]
    public void R7_superscript_and_zero_device_names_are_reserved(string name) => Assert.Null(PastedImage.Validate(name));

    [Theory]
    [InlineData("<h2 title=\"a>b\" data-line=\"4\">raw heading</h2>", "<h2 title=\"a>b\">raw heading</h2>")]
    [InlineData("<abbr title=\"literal data-line=7 value\">abbr</abbr>", "<abbr title=\"literal data-line=7 value\">abbr</abbr>")]
    [InlineData("<div title='x data-line=\"1\"' DATA-LINE=2>t</div>", "<div title='x data-line=\"1\"'>t</div>")]
    [InlineData("<div\ndata-line = \"3\"\nclass=x>t</div>", "<div\nclass=x>t</div>")]
    [InlineData("<input data-line checked>", "<input checked>")]
    [InlineData("<br data-line=1/>", "<br>")] // 引用符なしの値は / を含む（HTMLの規則）
    [InlineData("<br data-line=\"1\"/>", "<br/>")]
    [InlineData("<!-- <p data-line=\"1\"> --><p>x</p>", "<!-- <p data-line=\"1\"> --><p>x</p>")]
    [InlineData("a < b data-line=\"1\"", "a < b data-line=\"1\"")]
    [InlineData("<span data-lines=\"1\" data-line-x=2>t</span>", "<span data-lines=\"1\" data-line-x=2>t</span>")]
    public void R8_data_line_is_removed_by_attribute_not_by_text(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.StripLineAttributes(input));

    [Fact]
    public void R8_raw_heading_with_quoted_angle_bracket_is_not_a_line_target()
    {
        var html = Preview("# 本物\n\n<h2 title=\"a>b\" data-line=\"4\">raw heading</h2>\n\n本文 <abbr title=\"literal data-line=7 value\">abbr</abbr>\n");
        Assert.DoesNotMatch("<h2[^>]*data-line", html);
        Assert.Contains("title=\"literal data-line=7 value\"", html);
    }

    [Fact]
    public void R10_headings_inside_lists_quotes_html_and_indented_code_are_not_shifted()
    {
        const string Text = "- item\n  ## list heading\n\n## ordinary\n\n<div>\n# in html\n</div>\n\n- x\n\n  ```\n  # in fence\n  ```\n\n> ## quoted\n";
        var result = Apply(Text, TextFormatter.ShiftHeadings(Text, 0, 0, 1)!);
        Assert.Equal(Text.Replace("\n## ordinary", "\n### ordinary"), result);
    }

    [Theory]
    [InlineData("    | h | j |\n    |---|---|\n    | a | b |", 6)]
    [InlineData("text\n\n\t| h | j |\n\t|---|---|\n\t| a | b |", 8)]
    [InlineData("- x\n\n  ```\n  |a|b|\n  |---|---|\n  ```", 13)]
    public void R10_table_like_text_in_code_is_not_formatted(string text, int caret) =>
        Assert.Null(TextFormatter.FormatTable(text, caret, caret, "\n"));

    [Fact]
    public void R10_table_next_to_code_is_still_formatted() =>
        Assert.Equal("    code\n\n| a   | b   |\n| --- | --- |\n| 1   | 2   |", Format("    code\n\n|a|b|\n|---|---|\n|1|2|", 12));

    private static string ApplyAll(string text, IEnumerable<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            text = Apply(text, edit);
        }

        return text;
    }

    [Fact]
    public void R11_empty_cursor_on_the_same_line_does_not_hide_the_selected_headings()
    {
        const string Text = "## A\r\n## B xyz\r\n## C\r\n";
        var selection = (Text.IndexOf('#'), Text.IndexOf('B') + 1);
        var cursor = Text.IndexOf("xyz", StringComparison.Ordinal) + 3;
        var edits = TextFormatter.ShiftHeadingEdits(Text, [selection, (cursor, cursor)], 1);
        Assert.Equal("### A\r\n### B xyz\r\n## C\r\n", ApplyAll(Text, edits));
        Assert.Equal(2, edits.Count);
    }

    [Fact]
    public void R11_overlapping_selections_change_each_heading_once_and_empty_cursors_mean_whole_document()
    {
        const string Text = "# A\n# B\n# C\n# D\n";
        var lineB = Text.IndexOf('B');
        var edits = TextFormatter.ShiftHeadingEdits(Text, [(0, lineB), (lineB - 2, lineB + 1), (Text.IndexOf('D'), Text.IndexOf('D') + 1)], 1);
        Assert.Equal("## A\n## B\n# C\n## D\n", ApplyAll(Text, edits));
        Assert.Equal("## A\n## B\n## C\n## D\n", ApplyAll(Text, TextFormatter.ShiftHeadingEdits(Text, [(1, 1), (5, 5)], 1)));
        Assert.Empty(TextFormatter.ShiftHeadingEdits("# A\n", [(0, 0)], -1));
    }

    // --- 工程G 14G-2(c) 下線形式の見出しの行 ---

    [Fact]
    public void Setext_heading_line_is_the_text_line_not_the_underline()
    {
        var html = Preview("見出し下線\n===\n\n段落\n\n副見出し\n---\n\n本文\n\n複数行の\n見出し\n===\n");
        Assert.Matches("<h1[^>]*data-line=\"0\"[^>]*>見出し下線", html);
        Assert.Matches("<h2[^>]*data-line=\"5\"[^>]*>副見出し", html);
        Assert.Matches("<h1[^>]*data-line=\"10\"[^>]*>複数行の", html);
        Assert.Matches("<p[^>]*data-line=\"3\"[^>]*>段落", html);

        var crlf = Preview("a\r\n\r\n見出し\r\n---\r\n");
        Assert.Matches("<h2[^>]*data-line=\"2\"", crlf);
        var fenced = MarkdownRenderer.RenderPreview(new RenderRequest("```markdown\n本文\n\n見出し\n===\n```\n", StripOuterFence: true)).Html;
        Assert.Matches("<h1[^>]*data-line=\"3\"", fenced);
    }

    // --- 文言 ---

    [Fact]
    public void New_strings_exist_in_both_languages()
    {
        foreach (var key in new[] { "toc", "headingUp", "headingDown", "headingNoChange", "formatTable", "tableNotFound", "pasteImage", "pasteSaveFirst", "pasteNoImage", "pasteFileName", "pasteBadName", "pasteFailed", "copyHtml", "copiedHtml", "copiedHtmlImages", "linkNotFound", "warnEncoding", "statsDocument", "statsSelection", "headingUpHelp", "headingDownHelp", "formatTableHelp", "pasteImageHelp", "copyHtmlHelp" })
        {
            Assert.NotEqual(key, Strings.Get("ja", key));
            Assert.NotEqual(Strings.Get("en", key), Strings.Get("ja", key));
        }
    }
}
