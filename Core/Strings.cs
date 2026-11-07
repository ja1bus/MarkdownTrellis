namespace MarkdownTrellis.Core;

/// <summary>表示文字列（英語が既定、日本語を併記）。パネルとメニューで共通に使う。</summary>
public static class Strings
{
    private static readonly Dictionary<string, (string En, string Ja)> Table = new(StringComparer.Ordinal)
    {
        ["menu.panel"] = ("Show / Hide Panel", "パネルの表示/非表示"),
        ["bold"] = ("Bold", "太字"),
        ["italic"] = ("Italic", "斜体"),
        ["strike"] = ("Strikethrough", "取り消し線"),
        ["code"] = ("Inline Code", "インラインコード"),
        ["heading"] = ("Heading", "見出し"),
        ["headingNone"] = ("Normal text", "標準"),
        ["h1"] = ("Heading 1", "見出し 1"),
        ["h2"] = ("Heading 2", "見出し 2"),
        ["h3"] = ("Heading 3", "見出し 3"),
        ["h4"] = ("Heading 4", "見出し 4"),
        ["h5"] = ("Heading 5", "見出し 5"),
        ["h6"] = ("Heading 6", "見出し 6"),
        ["ul"] = ("Bulleted List", "箇条書き"),
        ["ol"] = ("Numbered List", "番号付きリスト"),
        ["task"] = ("Checklist", "チェックリスト"),
        ["quote"] = ("Quote", "引用"),
        ["codeblock"] = ("Code Block", "コードブロック"),
        ["table"] = ("Table...", "表..."),
        ["link"] = ("Link...", "リンク..."),
        ["image"] = ("Image...", "画像..."),
        ["hr"] = ("Horizontal Rule", "水平線"),
        ["copyMarkdown"] = ("Copy Source", "原文コピー"),
        ["copyRich"] = ("Copy Formatted", "書式付きコピー"),
        ["copyMarkdownHelp"] = ("Copies the whole document exactly as written in the editor, including unsaved changes.", "エディタの文書全体を、書かれているとおりコピーします（未保存の内容を含む）。"),
        ["copyRichHelp"] = ("Copies the preview with its formatting. Paste it into a Teams chat or channel, an email, and so on to keep headings, tables, bold text, and links.", "プレビューの内容を書式付きでコピーします。Teamsのチャットやチャネル、メールなどに貼り付けると、見出し・表・太字・リンクが保たれます。"),
        ["codeblockHelp"] = ("Inserts a code block. Inside an unclosed block, closes it. Inside a closed block, removes the fences.", "コードブロックを挿入します。閉じていないブロック内では閉じ、閉じたブロック内では囲みを解除します。"),
        ["featuresHelp"] = ("Checked syntax is interpreted in the preview and in Copy Formatted for this tab. The document text does not change. Click OK to apply.", "チェックした構文を、このタブのプレビューと書式付きコピーで解釈します。文書の本文は変更しません。［OK］で確定します。"),
        ["clearAll"] = ("Clear All", "すべてクリア"),
        ["unwrap"] = ("Remove Outer Code Fence", "外側のコードフェンスを解除"),
        ["about"] = ("About MarkdownTrellis", "MarkdownTrellisについて"),
        ["mode"] = ("Base syntax", "基本の構文"),
        ["modeGfm"] = ("GFM", "GFM"),
        ["modeCommonMark"] = ("CommonMark", "CommonMark"),
        ["features"] = ("Extra syntax", "追加構文"),
        ["presetGitHub"] = ("GitHub compatible", "GitHub互換"),
        ["featureAlerts"] = ("Alerts [!NOTE]", "アラート [!NOTE]"),
        ["featureFootnotes"] = ("Footnotes [^1]", "脚注 [^1]"),
        ["featureMath"] = ("Math $$ (as text)", "数式 $$（文字のまま表示）"),
        ["featureContainers"] = ("Containers :::", "コンテナ :::"),
        ["featureHighlight"] = ("Highlight ==text==", "ハイライト ==text=="),
        ["featureWikiLinks"] = ("Wiki links [[link]]", "Wikiリンク [[link]]"),
        ["featureDefinitionLists"] = ("Definition lists", "定義リスト"),
        ["language"] = ("Language", "言語"),
        ["languageSystem"] = ("Same as Windows", "Windowsに合わせる"),
        ["cols"] = ("Columns", "列数"),
        ["rows"] = ("Data rows", "データ行数"),
        ["linkText"] = ("Text", "表示文字列"),
        ["linkUrl"] = ("URL", "URL"),
        ["imageAlt"] = ("Alternative text", "代替テキスト"),
        ["imagePath"] = ("Path or URL", "パスまたはURL"),
        ["codeLanguage"] = ("Language (optional)", "言語（省略可）"),
        ["insert"] = ("Insert", "挿入"),
        ["cancel"] = ("Cancel", "キャンセル"),
        ["apply"] = ("Apply", "実行"),
        ["enable"] = ("Enable", "有効にする"),
        ["normalize"] = ("Convert...", "変換..."),
        ["normalizeTitle"] = ("Review the conversion", "変換内容の確認"),
        ["normalizeWarning"] = ("This conversion may change the meaning. Check the result; you can undo it.", "意味が変わる可能性があります。結果を確認してください。Undoで戻せます。"),
        ["normalizeStale"] = ("The document changed after the review, so nothing was converted. Check the conversion again.", "確認後に文書が変わったため変換していません。内容を確認し直してください。"),
        ["normalizeNone"] =("Nothing to convert.", "変換する箇所はありません。"),
        ["headerFormat"] = ("Column {0}", "列{0}"),
        ["noticeFence"] = ("The whole document may be wrapped in a code fence.", "文書全体がコードフェンスで囲まれている可能性があります。"),
        ["noticeFenceAmbiguous"] = ("This may be a code fence around the whole document, or an intended code block.", "文書全体を囲むコードフェンス、または意図したコードブロックの可能性があります。"),
        ["previewInner"] = ("Preview without it", "除外してプレビュー"),
        ["previewFull"] = ("Preview as written", "そのままプレビュー"),
        ["noticeDetected"] = ("{0}: {1} found", "{0}: {1}件"),
        ["noticeRejected"] = ("Shown as text (not allowed): {0}", "許可外のため文字列で表示: {0}"),
        ["noticeImages"] = ("Images not loaded (external or outside the document folder): {0}", "読み込まなかった画像（外部または文書フォルダ外）: {0}件"),
        ["notTarget"] = ("This file is not previewed automatically.", "このファイルは自動でプレビューしません。"),
        ["previewThis"] = ("Preview this file", "このファイルをプレビュー"),
        ["tooLarge"] = ("This document is large, so it is updated manually.", "文書が大きいため手動更新にしています。"),
        ["update"] = ("Update", "更新"),
        ["empty"] = ("The document is empty.", "文書は空です。"),
        ["renderFailed"] = ("Preview failed. You can keep editing and saving.", "プレビューに失敗しました。編集と保存は続けられます。"),
        ["copiedMarkdown"] = ("Copied the source text.", "原文をコピーしました。"),
        ["copiedRich"] = ("Copied with formatting. Paste it into a chat or email.", "書式付きでコピーしました。チャットやメールに貼り付けられます。"),
        ["copiedRichImages"] = ("Copied with formatting. Local images replaced with text: {0}", "書式付きでコピーしました。文字に置き換えたローカル画像: {0}件"),
        ["copyFailed"] = ("Could not access the clipboard. Try again.", "クリップボードを使用できませんでした。もう一度お試しください。"),
        ["warnEmphasis"] = ("The formatting may not apply here because of the surrounding punctuation. Add a space or change the selection.", "前後の記号の影響で書式が効かない可能性があります。空白を入れるか選択範囲を変えてください。"),
        ["warnReadOnly"] = ("The document is read-only.", "文書は読み取り専用です。"),
        ["gfmOnly"] = ("Available in GFM mode", "GFMモードで利用できます"),
        ["webViewMissing"] = ("Could not start the preview. See error.log in %LOCALAPPDATA%\\MarkdownTrellis for details. Editing commands and copying are still available from the Plugins menu.", "プレビューを起動できませんでした。詳細は %LOCALAPPDATA%\\MarkdownTrellis の error.log を見てください。書式設定とコピーは［プラグイン］メニューから利用できます。"),
        ["legacyNotice"] = ("Simple preview: WebView2 could not start. Links and images are shown as text. To skip the wait at startup, check Plugins > MarkdownTrellis > Use IE Engine.", "簡易表示: WebView2を起動できないため、IEエンジンで表示しています。リンクと画像は文字で表示します。起動時の待ちを省くには、［プラグイン］→［MarkdownTrellis］→［IEエンジンで表示する］にチェックを付けてください。"),
        ["legacyNoticeChosen"] = ("Simple preview: shown with the IE engine as set in Plugins > MarkdownTrellis > Use IE Engine. Links and images are shown as text.", "簡易表示: ［IEエンジンで表示する］の設定により、IEエンジンで表示しています。リンクと画像は文字で表示します。"),
        ["ieEngine"] = ("Use IE Engine", "IEエンジンで表示する"),
        ["ieEngineOn"] = ("The preview will use the IE engine without trying WebView2. Restart Notepad++ to apply.", "WebView2を試さず、IEエンジンで表示するようにしました。Notepad++の再起動後に反映されます。"),
        ["ieEngineOff"] = ("The preview will try WebView2 first and use the IE engine only if WebView2 cannot start. Restart Notepad++ to apply.", "WebView2を先に試し、起動できないときだけIEエンジンで表示するようにしました。Notepad++の再起動後に反映されます。"),
        ["more"] = ("More", "その他"),
    };

    public static string Get(string language, string key)
    {
        if (!Table.TryGetValue(key, out var value))
        {
            return key;
        }

        return language == "ja" ? value.Ja : value.En;
    }

    public static IEnumerable<KeyValuePair<string, string>> All(string language) =>
        Table.Select(p => new KeyValuePair<string, string>(p.Key, language == "ja" ? p.Value.Ja : p.Value.En));

    public static string FeatureKey(SyntaxFeature feature) => "feature" + feature;
}
