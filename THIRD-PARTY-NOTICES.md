# Third-party software

The public Windows x64 archive contains Tiff Light Viewer and the .NET app
launcher. The application source is available under the MIT license. The
.NET app launcher is provided by Microsoft under the MIT license; see
`DOTNET-APPHOST-LICENSE.txt` and `DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt`
in the archive.

- [.NET Desktop Runtime 10 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)
  is installed separately from Microsoft. See the
  [Windows distribution licensing information](https://github.com/dotnet/core/blob/main/license-information-windows.md).
- [Magick.NET](https://github.com/dlemstra/Magick.NET) is licensed under
  [Apache-2.0](https://github.com/dlemstra/Magick.NET/blob/main/License.txt).
  The setup script retrieves the required files directly from NuGet.org.
  These files include [ImageMagick](https://imagemagick.org/license/) and
  native components with their own licenses.
- [DataClasys](https://www.dataclasys.co.jp/) is an independent product.
  The application may invoke an existing DataClasys installation to decrypt a
  local working copy using the user's DataClasys permissions. No DataClasys
  software is included.
