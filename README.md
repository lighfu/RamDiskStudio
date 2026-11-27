# RAM Disk Studio

ImDisk を使う Windows 向けの RAM ディスク管理アプリです。アプリは Rust、画面は m3e SDK 1.0.1（Material 3 Expressive / GPUI）で実装しています。

## 使い方

1. [ImDisk Virtual Disk Driver](https://www.ltr-data.se/opencode.html/#ImDisk) をインストールします。物理メモリ固定を使う場合は awealloc も必要です。
2. [Releases](https://github.com/lighfu/RamDiskStudio/releases/latest) から Windows x64 向け ZIP を取得して展開し、`RamDiskStudio.exe` を起動します。ディスクの作成・解除には管理者権限が必要です。
3. 「ディスクを追加」で容量、ドライブ文字、ファイルシステム、メモリ方式を設定し、「作成」を押します。

解除・再起動・電源断で RAM ディスク上のファイルは消えます。

### 自動起動

ディスクの「起動時に作成」を有効にし、アプリの「自動起動」から登録します。初回の登録には管理者確認が必要です。登録するとログオン時にマウントされます。稼働中の所有ディスクを作り直すことはありません。

旧版から切り替えるときは、旧アプリを閉じて Rust 版を起動し、「自動起動」→「登録を更新」を選びます。登録先の EXE とスタートメニューが新版に切り替わります。設定・テーマ・履歴は従来どおり %LOCALAPPDATA%\RamDiskStudio に保存します。

### モニター

読み書き速度・量・回数・IOPS・平均サイズ、使用容量・使用率を秒、分、時、日単位で表示します。累計と履歴は SQLite に保存します。管理者権限で Windows ETW のファイル I/O 要求を記録します。管理者権限がない場合は容量を記録します。ファイル名・内容は保存しません。

ホイールで拡縮、ドラッグで移動、Shift＋ドラッグで範囲を選択します。Ctrl を押すと縦軸を操作できます。縦軸は線形・対数・変化を強調する表示を選べます。「ライブ」で最新時刻に戻ります。

### ファイルシステムとメモリ

NTFS / exFAT / FAT32、64 MiB～1 TiB に対応します。FAT32 は 256 MiB～32 GiB です。標準メモリは ImDisk VM、物理メモリ固定は awealloc を使います。awealloc の入手先は [Arsenal Image Mounter のドライバーパッケージ](https://github.com/ArsenalRecon/Arsenal-Image-Mounter/tree/master/DriverSetup) です。

## ビルドと検証

Windows x64、Rust 1.98.1 以降、Visual Studio MSVC ビルドツール、Windows SDK が必要です。ソースからのビルドには、別の非公開リポジトリで管理している m3e SDK へのアクセスも必要です。配布 ZIP の実行に SDK は不要です。

リリースで使用した SDK はバージョン 1.0.1 のコミット `f0bad16b6dfb1d9be3215c0e81cf77c47178f57c` です。アクセス権がある場合は、RAM Disk Studio のリポジトリ内から次のコマンドで SDK を隣のフォルダーに配置できます。

~~~powershell
git clone https://github.com/lighfu/m3e-sdk.git ../m3e-sdk
git -C ../m3e-sdk checkout --detach f0bad16b6dfb1d9be3215c0e81cf77c47178f57c
~~~

~~~powershell
pwsh -NoProfile -File scripts/build.ps1 -SmokeTest
pwsh -NoProfile -File scripts/package-release.ps1
~~~

実行ファイルは target/x86_64-pc-windows-msvc/release/RamDiskStudio.exe です。起動テストは一時設定を使い、実ディスクの作成や自動起動の登録を行いません。配布に .NET ランタイムは不要です。ImDisk と awealloc は別途導入してください。

[変更履歴](CHANGELOG.md) · [検証記録](docs/validation.md) · [リリースノート](docs/releases/v0.4.0.md)
