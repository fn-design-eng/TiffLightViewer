# Tiff Light Viewer

Windows向けの軽量TIFFビューアです。

DataClasysで暗号化されたTIFFファイルは、表示用にローカル一時領域へ復号して読み込みます。ペイントで開く場合は、復号済み一時ファイルではなく元ファイルのパスを渡します。

## 主な機能

- `.tif` / `.tiff` の表示
- マルチページTIFFのページ移動
- 同一フォルダ内の前後TIFF移動
- 拡大、縮小、実寸、ウィンドウに合わせる、回転表示
- DataClasys暗号化TIFFの表示用復号
- 元ファイルパスを指定したペイント起動
- ウィンドウへのTIFFファイルのドラッグ&ドロップ
- 重いTIFF向けのページ遅延読み込みと前後ページキャッシュ

## 開発

.NET 10 SDK が必要です。

```powershell
dotnet build
dotnet run -- "C:\path\to\sample.tif"
```

## 操作

- マウスホイール: 拡大 / 縮小
- 拡大時の左ドラッグ: 表示位置の移動
- 左右キー: 同じフォルダ内の前後TIFFへ移動
- PageUp / PageDown: マルチページTIFFのページ移動
- 下部中央ツールバー: ズーム、前後ファイル移動、フィット/実寸、ページ移動、左右回転、ペイント起動
- 表示テーマ: Windowsのアプリテーマ設定に追従

## 公開版の使い方

1. Windows x64 に [.NET Desktop Runtime 10 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) をインストールします。
2. 公開ZIPを展開し、そのフォルダで PowerShell から `.\Install-TiffDecoder.ps1` を一度実行します。初回のみインターネット接続が必要です。デコーダーは NuGet.org から取得し、配布時のハッシュと照合します。
3. 同じフォルダの `TiffLightViewer.exe` を起動します。

スクリプトの実行が既定で無効な場合は、配布元を確認したうえで、組織のポリシーで許可されていれば次の手順を使えます。管理者権限は不要で、実行ポリシーの指定は起動したプロセス限りです。

```powershell
Unblock-File -LiteralPath .\Install-TiffDecoder.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Install-TiffDecoder.ps1
```

DataClasysは公開ZIPに含まれません。復号には利用者のPCにインストール済みのDataClasysクライアントと、対象ファイルの復号権限が必要です。

## 公開版の作成

リポジトリ直下から PowerShell 7 で `.\scripts\New-PublicRelease.ps1` を実行すると、`dist/TiffLightViewer-win-x64.zip` が作られます。公開するバイナリはこのZIPです。アプリのライセンスは [MIT](LICENSE)、第三者コンポーネントは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## ローカル利用向けの発行

次の出力には外部ライブラリが同梱されます。公開ZIPの代わりには使用しないでください。

```powershell
dotnet publish -p:PublishProfile=SelfContained
dotnet publish -p:PublishProfile=FrameworkDependent
```

- .NET内包版: `bin\Release\net10.0-windows\win-x64\publish-self-contained\`
- .NET Desktop Runtime 10 必須版: `bin\Release\net10.0-windows\win-x64\publish-framework-dependent\`
