# SSO 対応版 BasisVR — 仕様書 (v0.1 / 確定)

最終更新: 2026-07-21

## 1. 目的とスコープ

特定組織の閉じた運用を前提に、**BasisVR デスクトップ/PCVR クライアントの起動時に
OIDC(Okta 含む) による SSO ログインを必須化**する。ログインは **クライアント内で完結**し、
既存のサーバー(DID/パスワード)側プロトコルは変更しない。

- **スコープ内**: Desktop / PCVR クライアント。
- **スコープ外(今回)**: サーバー側の SSO 検証、ヘッドレス/負荷試験クライアント、
  Android スタンドアロン VR(Quest 等)、モバイル、中央 Basis アカウントバックエンド。

## 2. 前提となる既存アーキテクチャ

- サーバー接続: `IP:Port` + 任意 join パスワード。`BasisServerConfiguration.UseAuth` /
  `UseAuthIdentity` で認証要求を切替。
- プレイヤー identity: **DID (分散型ID)**。クライアントがローカルで Ed25519 鍵ペアを生成
  (`BasisDIDAuthIdentityClient`, PlayerPrefs 保存) → 公開鍵から DID を生成 → 接続時に
  サーバーのチャレンジへ署名して所有を証明。中央アカウントは無い。
- identity は差し替え可能: `IPlayerIdentityProvider` / `BasisPlayerIdentityRegistry`
  (default `"did"`)。SSO は新プロバイダーとして差し込める。
- 表示名: ローカル保存のフリーテキスト (`CachedUserName.BAS`)。接続時に
  `BasisConnectionService.ConnectAsync` → `BasisLocalPlayer.DisplayName` に反映。

## 3. 全体フロー

```
アプリ起動
  └─ SSO ゲート (新規: 起動ブートストラップに挿入)
       ├─ 保存済みセッションあり & 有効/更新可
       │     └─ サイレント更新 → ログイン済みとして続行
       │           (IdP 到達不可でも、保存トークンが有効期間内なら続行 = オフライン起動許可)
       └─ セッションなし/失効
             └─ ログイン画面表示 (本編へ進行不可)
                   └─ [サインイン] → システムブラウザ起動
                         (OIDC Authorization Code + PKCE, loopback redirect)
                         └─ 認可コード受領 → トークン交換 → クレーム取得
                               ├─ アクセス制限チェック (許可グループ/クレーム。空なら全員可)
                               │     └─ 不許可 → ブロック(理由表示、再サインイン導線)
                               └─ 許可 → セッション保存(端末固有鍵で暗号化) → 続行
                                     └─ SSO sub に紐付いた DID を選択/生成、
                                        表示名クレームを初期値化
本編(アバター選択/サーバー一覧)へ
```

## 4. 機能要件

### 4.1 OIDC ログイン
- **フロー**: Authorization Code Flow with **PKCE**(ネイティブアプリ想定、client secret なし)。
- **リダイレクト**: `http://127.0.0.1:<ランダム空きポート>/callback` (loopback)。ローカル
  HTTP リスナーで認可コードを受領し、完了ページを表示。
- **ブラウザ**: OS の **システムブラウザ**(埋め込み WebView は使わない)。
- **エンドポイント**: issuer の `/.well-known/openid-configuration` (Discovery) から自動解決。
- **scope**: `openid profile email` + 設定で追加可(例: `groups`)。
- **セキュリティ**: `state`(CSRF) / `nonce`(リプレイ) 必須。id token の署名(JWKS)と
  クレーム(`iss`,`aud`,`exp`,`nonce`) を検証。
- **失敗/キャンセル**: 本編に進ませず、ログイン画面に留めてエラー表示＋再試行。

### 4.2 セッション永続化とトークン保存
- **保存内容**: refresh / id / access token(＋有効期限、issuer、sub、キャッシュしたクレーム)。
- **保存方式**: **端末固有鍵で暗号化したファイル**を `Application.persistentDataPath` に保存
  (例 `SsoSession.BAS`)。端末固有鍵は `SystemInfo.deviceUniqueIdentifier` 等 + アプリ固定
  ソルトから導出。平文保存はしない。
- **起動時**: 保存セッションがあれば refresh token でサイレント更新を試行。
- **オフライン起動**: IdP 到達不可でも、保存トークンが有効期間内なら続行を許可。有効期限切れ
  かつ更新不可なら再ログイン要求。

### 4.3 identity モデル (DID を維持し SSO に紐付け)
- 既存の DID 鍵ペア方式 (Ed25519) を **維持**。サーバーには従来通り DID チャレンジ応答で接続。
- **紐付け**: DID 鍵ペアを **SSO の `sub` 単位で分離保存**。ログインユーザーごとに固有の DID を
  持ち/生成(同一端末を複数ユーザーで共有しても混ざらない)。
  - 現行の PlayerPrefs グローバルキー(`PrivateKeyDID` 等)を **`sub` で名前空間化したキー**へ拡張。
- サーバー側は変更なし。SSO とのバインドは **クライアントローカルの対応付け**に留める。

### 4.4 表示名・識別子
- **安定 ID**: OIDC `sub`(内部識別・DID 紐付けキー)。
- **表示名**: `name`(無ければ `preferred_username` → `email` の順) を **初期値**として設定。
  ユーザーは従来通り編集可能。初回ログイン時のみクレームで初期化し、以後の編集値を優先。

### 4.5 アクセス制限 (設定で任意)
- 設定に許可条件を持つ:
  - 許可グループ(`groups` クレーム等のいずれかに合致)
  - 許可クレーム条件(`key = value` の許可リスト)
- **空なら全員許可**。不許可時は本編に進ませず、理由を表示して別アカウント再サインイン導線を出す。

### 4.6 サインアウト / アカウント切替
- 設定パネルに「サインアウト」を追加。
- サインアウト時: ローカルセッション破棄(暗号化ファイル削除) → 必要なら OIDC RP-Initiated
  Logout(`end_session_endpoint`) → ログイン画面へ。
- **アカウント切替**: サインアウト後、別アカウントで再ログイン可能。切替後は該当 `sub` の
  DID/表示名に切り替わる。

### 4.7 設定配布 (実行時設定ファイル)
- ビルド同梱の **実行時 JSON 設定ファイル**で OIDC 接続情報を提供(再ビルド不要で管理者が変更可)。
- 探索順: `Application.streamingAssetsPath`(同梱既定) → `Application.persistentDataPath`(上書き)。

## 5. 設定ファイルスキーマ (案)

```json
{
  "issuer": "https://<org>.okta.com/oauth2/default",
  "clientId": "0oaXXXXXXXXXXXX",
  "scopes": ["openid", "profile", "email", "groups"],
  "redirect": { "mode": "loopback", "path": "/callback" },
  "displayNameClaims": ["name", "preferred_username", "email"],
  "access": {
    "allowedGroups": [],
    "allowedClaims": []
  },
  "enforcement": {
    "allowOfflineWithinTokenValidity": true
  }
}
```

## 6. 変更・追加コンポーネント (想定)

- **新規パッケージ** `com.basis.integration.sso`
  - `BasisOidcConfig` — 設定ファイルの読込/モデル
  - `BasisOidcLoginService` — Discovery / PKCE / loopback リスナー / トークン交換・更新 / クレーム取得
  - `BasisSsoSessionStore` — 端末固有鍵での暗号化保存・読込・破棄
  - `BasisSsoGate` — 起動ブートストラップ挿入。ログイン UI 表示制御・本編進行のブロック
  - `BasisSsoIdentityBinding` — `sub` ↔ DID の名前空間化紐付け
  - ログイン画面 UI(既存 Panel 部品を利用)
- **改修**
  - 起動ブートストラップ: SSO ゲートを本編前に挿入。特に `BasisConnectionService` の起動時
    オートコネクト/`--connection` は **ログイン成立まで抑止**。
  - `BasisDIDAuthIdentityClient`: DID 鍵の保存キーを `sub` 名前空間対応に拡張。
  - `SettingsProvider`: サインアウト UI 追加。表示名初期化フックの接続。

## 7. セキュリティ考慮

- トークンは平文保存しない(端末固有鍵暗号化)。ただし端末固有鍵はローカル鍵であり
  OS セキュアストレージ相当ではない点は明記(今回の合意方針)。
- PKCE / state / nonce 必須。id token 署名(JWKS)とクレーム検証。
- **クライアントゲートは改変クライアントで回避可能**(サーバー側検証は今回スコープ外)。

## 8. 非スコープ (今回やらないこと)

- サーバー/ヘッドレス側の OIDC 検証・join パスワード置換
- Quest/Android のデバイスコードフロー
- 中央 Basis アカウントバックエンドの新設

## 9. 運用前提 (org 側で用意が必要)

- Okta アプリ種別: **OIDC Native** (Authorization Code + PKCE, loopback redirect 許可)。
- アクセス制限を使う場合: `groups` クレームを token/UserInfo に含める Okta 設定。
