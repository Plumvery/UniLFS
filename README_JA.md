# UniLFS

Unityの大容量アセットを、Git LFSの代わりに**自分の外部ストレージ**（Cloudflare R2 / S3互換サービス / Google Drive）に保存するエディタ拡張です。

English version: [README.md](README.md)

Git LFSの無料枠は小さく（GitHubはストレージ1GB・帯域1GB/月）、Unityプロジェクトではすぐ溢れます。UniLFSは大きいバイナリをgitから完全に切り離します。gitにはコンテンツハッシュを記録した小さなマニフェストだけをコミットし、実ファイルは自分で管理するストレージに置きます。たとえばR2の無料枠は10GB、**下り転送は無料**です。

- **git-lfs不要・CLIツール不要・サーバー不要** — 純粋なUnityエディタパッケージ
- **ストレージは自分で選ぶ** — Cloudflare R2 / Amazon S3 / MinIO / Wasabi（S3 API）、または Google Drive
- **`.meta`はgitに残る** — GUIDや参照が壊れない
- **コンテンツアドレス方式＋検証付き** — ブロブはSHA-256名で保存し、ダウンロードは必ずハッシュ検証
- **マージしやすいマニフェスト** — 1ファイル1行・ソート済みなのでPRがレビューしやすい
- **CI対応** — バッチモード用エントリポイントと環境変数での認証

## 仕組み

```
your-project/
├── unilfs.manifest.json     ← gitにコミット（パス + sha256 + サイズだけの小さいファイル）
├── .gitignore               ← UniLFSが管理ブロックを自動維持
├── Assets/
│   ├── Big/model.fbx        ← gitignoreされ、UniLFSが復元する
│   └── Big/model.fbx.meta   ← 普通にgitへコミット
└── UserSettings/UniLFS.json ← 認証情報（コミットされない）

リモートストレージ:
└── unilfs/objects/ab/abcdef1234...   ← SHA-256名のブロブ
```

1. **Track** — 大きいファイルを選ぶと、UniLFSがSHA-256を`unilfs.manifest.json`に記録し、`.gitignore`の管理ブロックへ追加します。
2. **Push** — リモートに無いブロブだけをアップロードします。ブロブのアップロード確認が取れてからマニフェストへ新ハッシュを書くので、コミットされたマニフェストが「存在しないブロブ」を指すことはありません。
3. **Pull** — チームメイト（やCI）は、マニフェストにあってローカルに無いファイルをダウンロードします。プロジェクトに書き込む前に必ずハッシュ検証します。

追跡中ファイルの更新は「編集 → **Push** → マニフェストの差分をコミット」。ブランチ切り替えは「checkout → **Pull**」だけです。

## 動作要件

- Unity **2021.3** 以降
- gitクライアント（UPMのgit URLインストールに必要）
- S3互換バケット（Cloudflare R2推奨）または Googleアカウント

## インストール

**Package Manager UI**: `Window > Package Manager` → `+` → *Add package from git URL*:

```
https://github.com/Plumvery/UniLFS.git
```

**または `Packages/manifest.json`**:

```json
{
  "dependencies": {
    "com.plumvery.unilfs": "https://github.com/Plumvery/UniLFS.git"
  }
}
```

タグでバージョン固定: `https://github.com/Plumvery/UniLFS.git#v0.1.0`

## クイックスタート（Cloudflare R2）

1. R2バケットと *Object Read & Write* 権限のAPIトークンを作成 — [手順ガイド](Documentation~/setup-r2.md)
2. Unityで `Edit > Project Settings > UniLFS` を開く:
   - Provider: **S3 compatible**
   - Endpoint: `https://<アカウントID>.r2.cloudflarestorage.com`
   - Bucket: バケット名、Region: `auto`
   - Access Key ID / Secret Access Key（ユーザーごとに保存、コミットされない）
3. **Test Connection** を押す
4. Projectウィンドウで大きいアセットを選択 → 右クリック → `UniLFS > Track Selected`
5. `Window > UniLFS` を開いて **Push**
6. `unilfs.manifest.json`・`.gitignore`・`ProjectSettings/UniLFSSettings.json`・アセットの`.meta`をコミット。
   すでにgitにコミット済みだったファイルは、Consoleに表示される `git rm --cached` コマンドを実行してください。

チームメイトは「clone → Project Settingsで自分の認証情報を入力 → プロジェクトを開く」だけ。UniLFSが欠けているファイルを検知してPullを提案します（[自動Pull](#自動pull--gitフック不要)参照）。もちろん `Window > UniLFS` → **Pull** の手動操作も可能です。

Google Driveを使う場合は [Documentation~/setup-google-drive.md](Documentation~/setup-google-drive.md) へ。

## UniLFSウィンドウ（`Window > UniLFS`）

| ボタン | 動作 |
|--------|------|
| Refresh | 全追跡ファイルを再チェック（mtime+sizeでハッシュをキャッシュするので軽い） |
| Push | 新規/変更ブロブをアップロードし、マニフェストを更新 |
| Pull | ローカルに無いファイルをダウンロード |
| Restore Modified | ローカル変更をマニフェストの版で上書き（確認ダイアログあり） |
| Track / Untrack Selected | `Assets > UniLFS` の右クリックメニューと同じ |

状態表示: **up to date**（マニフェストと一致）/ **modified**（未Pushのローカル変更あり）/ **missing**（Pullが必要）。

## 自動Pull — gitフック不要

エディタの起動時・フォーカス復帰時（＝ターミナルやgitクライアントで`git pull`した直後がまさにこれ）に、UniLFSが軽量な存在チェックを実行します。マニフェストが変わっていて追跡ファイルが欠けている場合、**Auto Pull** 設定に応じて動作します:

| モード | 動作 |
|--------|------|
| **Ask**（デフォルト） | ダイアログでダウンロードするか確認 |
| **Automatic** | 即座にバックグラウンドでダウンロード（進捗はステータスバー） |
| **Off** | Consoleに警告を出すだけ |

設定は `Edit > Project Settings > UniLFS`。同じマニフェスト状態につきエディタセッション中1回しか反応しないので、ダイアログで「Later」を選んでもフォーカスのたびに聞かれることはありません。

## 設定と認証情報

| ファイル | コミット | 内容 |
|----------|---------|------|
| `unilfs.manifest.json` | ✅ | 追跡パス + SHA-256 + サイズ |
| `ProjectSettings/UniLFSSettings.json` | ✅ | プロバイダ、エンドポイント、バケット、フォルダIDなど |
| `.gitignore`（管理ブロック） | ✅ | 追跡ファイルのパス、認証ファイル |
| `UserSettings/UniLFS.json` | ❌（自動でgitignore） | アクセスキー、OAuthリフレッシュトークン |

環境変数が最優先です（CI向け）:
`UNILFS_S3_ACCESS_KEY_ID`, `UNILFS_S3_SECRET_ACCESS_KEY`, `UNILFS_DRIVE_CLIENT_ID`, `UNILFS_DRIVE_CLIENT_SECRET`, `UNILFS_DRIVE_REFRESH_TOKEN`

認証情報は `UserSettings/UniLFS.json` に平文で保存されます（`~/.aws/credentials`と同様の方式）。UniLFSはこのファイルを必ず`.gitignore`ブロックに含めますが、シークレットとして扱ってください。

## CI

```sh
Unity -batchmode -nographics -quit -projectPath . \
  -executeMethod UniLFS.Editor.UniLfsCli.Pull
```

`Pull` / `Push` / `Status` が使えます。エラーがあるとプロセスは非ゼロで終了します。GitHub Actionsの例は [Documentation~/ci.md](Documentation~/ci.md) へ。

## マージの挙動

マニフェストは1ファイル1行・ソート済みなので、**別々の**ファイルを追跡した2人の変更はきれいにマージされます。**同じ**ファイルを2人が変更した場合は1行のコンフリクトになるので、採用したいハッシュを選んで **Pull**（自分のローカル版を上書きするなら **Restore Modified**）してください。どちらの版のブロブもリモートに存在するので、データが失われることはありません。

## 制限事項（v0.1）

- ファイルロック機能なし（素のgitと同じく、バイナリを誰が編集するかはチームで調整）
- 古いブロブのGCは未実装（ストレージは安価。`prune`はロードマップにあり）
- アップロードは単一リクエスト: R2/S3ではオブジェクトあたり約5GBまで
- Google Driveは個人〜小規模チーム向き（レート制限・容量の注意はガイド参照）
- エディタ専用: ビルド前にPullが必要（そのためのCIエントリポイントです）

## ロードマップ

- ブロブのprune / GC
- パターン指定の自動追跡（フォルダ以下を全部追跡など）
- マルチパートアップロード
- OpenUPM掲載

PR・Issue歓迎です。

## ライセンス

[MIT](LICENSE.md)
