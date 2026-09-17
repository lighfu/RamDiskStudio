# Changelog

このファイルには、利用者に関係する変更を記録します。
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) の分類と日付形式を使い、バージョン番号は [Semantic Versioning](https://semver.org/spec/v2.0.0.html) に従います。0.x は初期開発段階です。

## [Unreleased]

## [0.3.1] - 2026-09-17

初回の GitHub Release。公開前に実装した機能を、この版の追加機能としてまとめています。

### Added

- 日本語の Windows デスクトップ UI による、複数の RAM ディスク設定の作成・編集・保存。
- ImDisk を使ったディスクの作成・解除、容量とドライブ文字の指定、NTFS / exFAT / FAT32 の選択。
- 標準メモリ方式と、awealloc による物理メモリ固定方式。ドライバーの導入状況と空き RAM の確認。
- アプリ起動時のディスク自動作成と、作成時の Temp フォルダー生成。
- デバイスとボリュームの識別情報を照合する解除処理、強制解除の追加確認、設定ファイルのバックアップ。
- ETW によるドライブ別の読み書き監視。速度、量、回数、IOPS、平均アクセスサイズ、使用容量・使用率を表示。
- SQLite に保存する秒・分・時・日単位の履歴と、アプリの再起動後も保持する累計。
- マウスによる時間軸・縦軸の拡縮、移動、期間選択。線形・0 対応の対数・変化を強調する縦軸の切り替え。
- 時間軸とカーソルが連動する比較グラフ、系列の表示切り替え、グラフの高さ調整、ライブ追従。
- ライト / ダークの切り替えと選択の保存。グラフ、設定画面、確認ダイアログにも適用。
- .NET ランタイムを同梱した Windows x64 向け配布 ZIP、第三者ライセンス表示、SHA-256 チェックサム。

導入方法、データ消失条件、測定の意味と既知の制約は [リリースノート](docs/releases/v0.3.1.md) を参照してください。

[Unreleased]: https://github.com/lighfu/RamDiskStudio/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/lighfu/RamDiskStudio/releases/tag/v0.3.1
