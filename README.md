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

## 開発

```powershell
dotnet build
dotnet run -- "C:\path\to\sample.tif"
```

## 発行

```powershell
dotnet publish -c Release
```

発行物は `bin\Release\net8.0-windows\win-x64\publish\` に作成されます。
