using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownTrellis.Core;

public sealed class AppSettings
{
    /// <summary>system、en、ja のいずれか。</summary>
    public string Language { get; set; } = "system";

    public bool PanelVisible { get; set; }

    public SyntaxFeature DefaultFeatures { get; set; } = SyntaxFeature.None;

    /// <summary>許可するHTMLタグの追加分。常に禁止のタグは追加できない。</summary>
    public List<string> ExtraHtmlTags { get; set; } = [];

    /// <summary>auto（WebView2を試し、失敗したらIEエンジン）または ie（最初からIEエンジン）。</summary>
    public string PreviewEngine { get; set; } = "auto";

    /// <summary>カーソル行に合わせてプレビューをスクロールする（通常表示のみ）。</summary>
    public bool FollowCaret { get; set; } = true;

    public static string ToJson(AppSettings settings) => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    public static AppSettings FromJson(string json) => JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();

    /// <summary>設定の言語とWindowsの表示言語から、実際の表示言語を決める。</summary>
    public static string ResolveLanguage(string setting, CultureInfo uiCulture) => setting switch
    {
        "ja" => "ja",
        "en" => "en",
        _ => uiCulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en",
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
