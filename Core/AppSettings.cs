using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownTrellis.Core;

public sealed class AppSettings
{
    /// <summary>system、en、ja のいずれか。</summary>
    public string Language { get; set; } = "system";

    public bool PanelVisible { get; set; }

    /// <summary>手で編集した設定の誤り（未知の名前）で、他の設定まで既定に戻さないよう寛容に読む。</summary>
    [JsonConverter(typeof(LenientFeatureConverter))]
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

/// <summary>
/// 追加構文の名前を <c>"Alerts, Footnotes"</c> の形の文字列、または文字列の配列で読む。
/// 既知の名前（大文字小文字を区別しない）だけを使い、未知の名前・数値・他の型は無視する。書き出しは従来と同じ形。
/// </summary>
public sealed class LenientFeatureConverter : JsonConverter<SyntaxFeature>
{
    public override SyntaxFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = SyntaxFeature.None;
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                result = Parse(reader.GetString());
                break;
            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        result |= Parse(reader.GetString());
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                break;
            default:
                reader.Skip();
                break;
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, SyntaxFeature value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

    private static SyntaxFeature Parse(string? text)
    {
        var result = SyntaxFeature.None;
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var feature in Enum.GetValues<SyntaxFeature>())
            {
                if (feature.ToString().Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    result |= feature;
                }
            }
        }

        return result;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
