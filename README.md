# MarkdownTrellis

[English](README.en.md) | 日本語

MarkdownTrellis は、Notepad++ で Markdown を編集しやすくするプラグインです。書式ボタンとリアルタイムプレビューを追加し、文書を原文のまま、または Teams などに貼り付けられる書式付きでコピーできます。ChatGPT などが出力した Markdown の確認や手直しにも使えます。

## 主な機能

- プレビュー: 入力に合わせて更新します。Notepad++ のドッキングパネルに表示します。
- 書式ボタン: 太字、斜体、取り消し線、インラインコード、見出し、箇条書き、番号付きリスト、チェックリスト、引用、コードブロック、表、リンク、画像、水平線
- 原文コピー: 文書全体を書かれているとおりコピーします（未保存の内容を含む）。
- Teams用コピー: プレビューの内容を書式付きでコピーします。Teams やメールなどに貼り付けると、見出し、表、太字、リンクが保たれます。
- 基本の構文: GFM（既定）と CommonMark を切り替えられます。Frontmatter はプレビューに表示しません。
- 追加構文: アラート、脚注、数式（文字のまま表示）、コンテナ、ハイライト、Wikiリンク、定義リストをタブごとに有効にできます。
- 全体を囲むコードフェンスの検出: 文書全体が ```` ```markdown ```` で囲まれているときに通知します。外側の2行だけを解除することもできます（Undo 可）。
- 表示言語: 英語と日本語（既定は Windows の表示言語）

編集、保存、Undo は Notepad++ 本体の機能を使います。プレビューは表示専用です。プレビュー内での編集、数式の描画、Mermaid の描画、Word や PDF への出力には対応していません。

## プライバシーと安全

- 文書の内容を外部へ送信しません。自動更新や更新確認の通信もありません。
- プレビューではスクリプトを実行せず、許可したタグと属性だけを表示します。許可していないタグは文字で表示します。
- 外部の画像は読み込みません。ローカルの画像は文書のフォルダ配下のものだけを表示します。
- リンクは、クリックしたときだけ既定のブラウザーで開きます。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- Notepad++ 64 ビット版（インストーラー版、ポータブル版）。確認済みの版: 8.9.8.1
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- .NET のインストールは不要です。

### WebView2 を起動できない PC

WebView2 を起動できない場合は、IE エンジンによる代替表示へ自動で切り替えます。代替表示には次の違いがあります。

- リンクと画像を文字で表示します。
- パネルのボタンは Windows 標準のものになります。書式、挿入、コピー、基本の構文、追加構文は使えます。
- 全体を囲むコードフェンスなどの通知、構文の統一（正規化）、言語の切替はパネルから使えません。外側のコードフェンスの解除はメニューから使えます。
- IE エンジンは WebView2 より保護が弱く、将来 Windows から廃止される可能性があります。

代替表示に切り替わる PC では、起動のたびに WebView2 を試して失敗するまで待ちが発生します。［プラグイン］→［MarkdownTrellis］→［IEエンジンで表示する］にチェックを付けると、WebView2 を試さずに代替表示を使います（Notepad++ の再起動後に反映）。

## インストール

配布物は [Releases](https://github.com/ja1bus/MarkdownTrellis/releases/latest) の `MarkdownTrellis-<版>-x64.zip` です。

DLL に電子署名はしていません。そのため、次の点に注意してください。

- セキュリティ製品や組織のポリシーによって、読み込みが止められる場合があります。
- ダウンロードしたファイルが Release のものと同じか確認するときは、次のコマンドの結果を `.sha256` の値と比べてください。

```powershell
Get-FileHash .\MarkdownTrellis-<版>-x64.zip -Algorithm SHA256
```

- 展開する前に ZIP のブロックを解除してください。ZIP を右クリックして［プロパティ］を開き、［許可する］にチェックを付けて［OK］を押します。次のコマンドでも解除できます。

```powershell
Unblock-File .\MarkdownTrellis-<版>-x64.zip
```

### ポータブル版の Notepad++

管理者権限は不要です。

1. Notepad++ を閉じます。
2. `notepad++.exe` のあるフォルダの `plugins` の中に `MarkdownTrellis` フォルダを作ります。
3. ZIP の中身をすべて、そのフォルダに展開します。

```text
<notepad++.exe のフォルダ>\plugins\MarkdownTrellis\MarkdownTrellis.dll
```

### インストーラー版の Notepad++

`Program Files` へのコピーにだけ管理者権限が必要です。インストール後の利用には必要ありません。

1. Notepad++ を閉じます。
2. ZIP を、書き込みできる場所に展開します。
3. エクスプローラーで `C:\Program Files\Notepad++\plugins` を開き、`MarkdownTrellis` フォルダを作ります（確認画面が出たら許可します）。
4. 展開したファイルをすべて、そのフォルダにコピーします。

```text
C:\Program Files\Notepad++\plugins\MarkdownTrellis\MarkdownTrellis.dll
```

### 更新と以前の版に戻す方法

Notepad++ を閉じて、使いたい版の ZIP の中身を同じフォルダに上書きします。自動更新はありません。導入している版は、パネルの見出しまたは［プラグイン］→［MarkdownTrellis］→［MarkdownTrellisについて］で確認できます。

### 削除

Notepad++ を閉じて、`plugins\MarkdownTrellis` フォルダを削除します。設定と WebView2 のデータも消す場合は、`%LOCALAPPDATA%\MarkdownTrellis` を削除します。

## 基本的な使い方

1. Notepad++ を起動します。ツールバーのアイコン（緑の四角に白い M）か、［プラグイン］→［MarkdownTrellis］からパネルを表示します。
2. `.md` などの Markdown ファイルか無題の文書では、プレビューが自動で表示されます。
3. 文字を選択してパネルのボタンを押すと、書式を適用できます。編集支援とコピーはメニューからも使えます。
4. ［原文コピー］または［Teams用コピー］でコピーします。

プラグインのコマンドには既定のショートカットを割り当てていません。必要な場合は Notepad++ の Shortcut Mapper で割り当てられます。

### 設定

設定は `%LOCALAPPDATA%\MarkdownTrellis\settings.json` に保存します。エラーの記録は同じフォルダの `error.log` です。

| 項目 | 内容 |
|---|---|
| `PreviewEngine` | `auto`（既定）または `ie`（最初から代替表示を使う）。メニューの［IEエンジンで表示する］でも切り替えられます |
| `ExtraHtmlTags` | プレビューで追加で許可する HTML タグ。安全のために禁止しているタグは許可できません |

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)（`global.json` で 10.0.403 以降の 10.0 系を指定）
- DLL の発行（`dotnet publish`）にだけ [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) の C++ によるデスクトップ開発ワークロードが必要です（[Native AOT の前提条件](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)）。`dotnet build` と `dotnet test` には必要ありません。
- NuGet パッケージは初回のビルド時に nuget.org から取得します。

### コマンド

リポジトリのルートで実行します。

```powershell
dotnet build MarkdownTrellis.csproj -c Release
dotnet test Tests/MarkdownTrellis.Tests.csproj -c Release

# DLL を publish\ に発行する
pwsh -File tools\publish-plugin.ps1

# DLL を発行して、指定した Notepad++ に配置する（Notepad++ は閉じておく）
pwsh -File tools\publish-plugin.ps1 -NppDir '<notepad++.exe のフォルダ>'

# Release 用の ZIP と SHA-256 を publish\release\ に作成する
pwsh -File tools\package-release.ps1
```

### 構成

| パス | 内容 |
|---|---|
| `MarkdownTrellis.csproj` | プラグイン本体（NativeAOT の共有ライブラリ） |
| `Plugin/` | Notepad++、Scintilla、WebView2、IE エンジン、クリップボードとの連携 |
| `Core/` | Notepad++ に依存しない処理（Markdown の変換、HTML の検査、書式の操作、表示文字列） |
| `Assets/shell.html` | パネルのツールバーとプレビュー枠（DLL に埋め込み） |
| `Tests/` | `Core` の単体テスト（xUnit） |
| `tools/` | DLL の発行と Release 用 ZIP の作成 |

ソースコード中のコメントは主に日本語です。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

Notepad++、Microsoft Teams は各権利者の製品です。このプラグインは、それらの開発元が提供または保証するものではありません。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの使用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## サポート

このプロジェクトでは、問い合わせや不具合報告の窓口を設けていません。

## 関連リンク

- Releases: https://github.com/ja1bus/MarkdownTrellis/releases
- Microsoft Edge WebView2: https://developer.microsoft.com/microsoft-edge/webview2/
