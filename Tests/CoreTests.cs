using System.Globalization;
using System.Text;
using MarkdownTrellis.Core;
using Xunit;

namespace MarkdownTrellis.Tests;

public class RendererTests
{
    private static string Preview(string text, BaseMode mode = BaseMode.Gfm, SyntaxFeature features = SyntaxFeature.None, string? host = null) =>
        MarkdownRenderer.RenderPreview(new RenderRequest(text, mode, features, ImageHost: host)).Html;

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
    public void CommonMark_does_not_render_gfm_only_syntax()
    {
        var html = Preview("~~old~~\n\n| a |\n| --- |\n", BaseMode.CommonMark);
        Assert.DoesNotContain("<del>", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public void Frontmatter_is_not_rendered_in_either_mode()
    {
        const string Text = "---\ntitle: \"x\"\n---\n\n# Body\n";
        foreach (var mode in new[] { BaseMode.Gfm, BaseMode.CommonMark })
        {
            var html = Preview(Text, mode);
            Assert.DoesNotContain("title", html);
            Assert.Contains("Body", html);
        }
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
    public void Text_inputs_are_not_allowed_and_checkboxes_are_disabled()
    {
        Assert.Contains("&lt;input", Run("<input type=\"text\">").Html);
        Assert.Contains("disabled", Run("<input type=\"checkbox\">").Html);
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
        var settings = new AppSettings { Language = "ja", DefaultMode = BaseMode.CommonMark, DefaultFeatures = SyntaxFeature.Alerts | SyntaxFeature.Footnotes, ExtraHtmlTags = ["nav"], PanelVisible = true };
        var copy = AppSettings.FromJson(AppSettings.ToJson(settings));
        Assert.Equal("ja", copy.Language);
        Assert.Equal(BaseMode.CommonMark, copy.DefaultMode);
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
}
