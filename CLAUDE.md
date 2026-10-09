# Reso360Spout2 開発知見メモ

このファイルは後続のエージェントや開発者のための技術的知見のまとめです。

---

## ビルド環境 (Resonite の場所の解決)

`Directory.Build.props` が Resonite のインストール先を自動検出する。優先順位:

1. `-p:ResonitePath=...` / 環境変数 `RESONITE_PATH`
2. Steam のレジストリ (`HKCU\Software\Valve\Steam\SteamPath`) + `libraryfolders.vdf` 走査
   （C ドライブ以外の Steam ライブラリにも対応）
3. 既定のインストールパス (Windows / Linux)

解決結果は以下のプロパティとして 3 プロジェクトに共有される:
`GamePath` / `ModDeployDir` / `HostBepInExDir` / `RendererBepInExDir` / `RendererManagedDir` / `UnityNativePluginDir`。

デプロイ先は既定で Gale の `360Spout` プロファイル (`%APPDATA%\com.kesomannen.gale\resonite\profiles\360Spout\`)。
そのプロファイルが無いマシン (Mod Manager を使わずゲーム本体に直接入れている環境) ではゲーム本体フォルダ。
別の場所へ入れたい場合は:

```
dotnet build -c Release -p:ModDeployDir="%APPDATA%\com.kesomannen.gale\resonite\profiles\OtherProfile\"
```

**csproj にゲームパスをハードコードしないこと。** 以前は `C:\Program Files (x86)\Steam\...` が
直書きされており、Steam ライブラリが別ドライブのマシンでは参照解決すらできなかった。

### デプロイ先と参照元を混ぜないこと

`ModDeployDir` は **コピー先だけ**。参照アセンブリ (FrooxEngine / UnityEngine / BepInEx 5 コア) は
必ずゲーム本体 (`GamePath` / `RendererCoreDir`) から引く。
以前は Renderer / Patcher が `$(RendererBepInExDir)core\BepInEx.dll` を参照していたため、
`-p:ModDeployDir=` で別フォルダを指定した瞬間に BepInEx の型が全部見つからなくなっていた。

### コマンドラインの `-p:` はグローバルプロパティ

`-p:Foo=...` で渡した値は **プロジェクト側から代入しても黙って無視される**。

```xml
<!-- NG: -p:ModDeployDir=... で渡されたときだけ末尾区切りが付かない -->
<ModDeployDir>$([MSBuild]::EnsureTrailingSlash('$(ModDeployDir)'))</ModDeployDir>

<!-- OK: 加工結果は別プロパティへ -->
<ModDeployRoot>$([MSBuild]::EnsureTrailingSlash('$(ModDeployDir)'))</ModDeployRoot>
```

これを踏んで `...\profileRenderer\BepInEx\` のような隣接フォルダへコピーしていた。

### パッケージング

```
dotnet build -c Release -t:PackTS                    # build/ に Thunderstore zip を出力
dotnet build -c Release -t:PackTS -p:PublishTS=true  # Thunderstore へ公開
dotnet tcli build                                    # tcli を直接叩く場合
```

`Directory.Build.targets` の `PackTS` は `tcli build --package-version $(Version)` を呼ぶので、
**`$(Version)` と `thunderstore.toml` の `versionNumber` がズレていると
コマンドによって別バージョンの zip ができる。**

### リリース手順

バージョンを上げるときは次の 4 箇所を必ず揃える:

1. `Directory.Build.props` の `<Version>` （3 プロジェクト共通、`PackTS` が使う）
2. `thunderstore.toml` の `versionNumber` （素の `tcli build` が使う）
3. `manifest.json` の `version_number`
4. `Reso360Spout2.Renderer/RendererPlugin.cs` の `[BepInPlugin]` 第 3 引数
   （属性なので定数が必要。ログに出るバージョンはこれ）

加えて `CHANGELOG.md` を更新する（Thunderstore の Changelog タブに出る）。

---

## プロジェクト構成

Resoniteは2プロセス構成（Renderite）で動作する。

| プロセス | 実行ファイル | BepInEx | .NET | 役割 |
|---|---|---|---|---|
| Host | `Resonite.exe` | BepInEx 6 (net10.0) | .NET Core | FrooxEngineへのアクセス、共有メモリ書き込み |
| Renderer | `Renderer/Renderite.Renderer.exe` | BepInEx 5 (net472) | Mono (Unity 2019.4) | 360度レンダリング、Spout2送信 |

### ソリューション構成

```
Reso360Spout2.sln
├── Reso360Spout2/            ← Host プラグイン (BepInEx 6 / net10.0)
├── Reso360Spout2.Renderer/   ← Renderer プラグイン (BepInEx 5 / net472)
└── Reso360Spout2.Patcher/    ← Renderer プリローダーパッチャー (net472)
```

---

## KlakSpout DLL の扱いに関する重要知見

### 問題の本質

KlakSpoutのネイティブDLLは Unity の `UnityPluginLoad(IUnityInterfaces*)` が呼ばれることで D3D11 デバイスを取得する。このコールバックが呼ばれない場合、デバイスポインタが NULL となり `CreateSharedDX11Texture` が失敗する。

### NG: `LoadLibraryExW` での手動ロード

```csharp
// これは動かない
var handle = LoadLibraryExW(fullPath, IntPtr.Zero, 0);
[DllImport("KlakSpout_send")] static extern IntPtr CreateSender(...);
```

`LoadLibraryExW` でロードしても Unity は `UnityPluginLoad` を呼び出さない。
結果: `KlakSpout.dll` の場合は `CreateSender` が `IntPtr.Zero` を返す。`KlakSpout_send.dll` の場合は `CreateSharedDX11Texture NULL device` でクラッシュ。

### 解決策: Unity の Plugins フォルダへの配置

Unity は `Renderite.Renderer_Data/Plugins/x86_64/` 内の DLL に対してのみ起動時に `UnityPluginLoad` を呼び出す。

**実装**: BepInEx プリローダーパッチャー（`Reso360Spout2.Patcher`）が Unity 初期化より前に `KlakSpout_send.dll` をこのフォルダへコピーする。

```
起動順序:
1. Doorstop (winhttp.dll) が BepInEx プリローダーを起動
2. Patcher.Initialize() → KlakSpout_send.dll を Plugins/x86_64/ にコピー
3. Unity 初期化 → Plugins/x86_64/ の DLL をロード → UnityPluginLoad 呼び出し → D3D11 デバイス登録
4. BepInEx プラグインが起動 → [DllImport("KlakSpout_send")] → 既ロード済みモジュールを使用 → 動作
```

### 最重要の落とし穴: パッチャーは `Patch` がないと読み込まれない

BepInEx 5 (`AssemblyPatcher.ToPatcherPlugin`) は、パッチャーを

* `public static IEnumerable<string> TargetDLLs { get; }` プロパティ **と**
* `public static void Patch(ref AssemblyDefinition)` メソッド

の **両方** を持つ型としてのみ認識する。片方でも欠けると型ごと黙って無視され、
`Initialize()` すら呼ばれない。ログには何のエラーも出ず、
`N patcher plugins loaded` の数が増えないだけなので気づきにくい。

`TargetDLLs` が空でも `Patch` は必ず用意すること（中身は空で良い）。

**これが v1.0.1 が配布先で動かなかった原因。** パッチャーが無視されて
`KlakSpout_send.dll` が `Plugins/x86_64/` に配置されず、`CreateSender` が失敗していた。
開発機だけ動いていたのは、csproj のポストビルドが同じ DLL をビルドのたびに
ゲームフォルダへコピーしていたため。**その理由からポストビルドコピーは削除した**
（開発機と配布先で挙動が変わると、この種のバグが表に出なくなる）。

動作確認方法 — `Renderer/BepInEx/LogOutput.log` (または Unity の `Player.log`) に:

```
[Info   :   BepInEx] Loaded 1 patcher method from [Reso360Spout2.Patcher 1.0.0.0]
[Info   :   BepInEx] 2 patcher plugins loaded
[Info   :Reso360Spout2.Patcher] Copied KlakSpout_send.dll to: ...\Plugins\x86_64\KlakSpout_send.dll
```

タイミングは問題ない。パッチャーは Unity がネイティブプラグインを読み込むより前に走るので、
**インストール直後の初回起動から** Spout センダーが作られる。

### DLL の役割分担

| DLL | サイズ | 用途 | 備考 |
|---|---|---|---|
| `KlakSpout_send.dll` | 220KB | Spout 送信専用 | `CreateSender`, `GetRenderEventFunc` など |
| `KlakSpout.dll` | 1MB | Spout 受信 + ユーティリティ | `CreateReceiver`, `ScanSharedObjects` など。`D3D11CreateDevice` を内包 |

`KlakSpout.dll` は自前で D3D11 デバイスを作れるが、`CreateSender` は Unity のデバイスが必要なため、`KlakSpout_send.dll` と役割分担する。

### `[DllImport]` の書き方

```csharp
// Sender: KlakSpout_send.dll から
[DllImport("KlakSpout_send", EntryPoint = "GetRenderEventFunc")]
internal static extern IntPtr Sender_GetRenderEventFunc();

[DllImport("KlakSpout_send", EntryPoint = "CreateSender")]
internal static extern IntPtr CreateSender(string name, int width, int height);

// Receiver / Utility: KlakSpout.dll から
[DllImport("KlakSpout")] internal static extern IntPtr CreateReceiver(string name);
[DllImport("KlakSpout")] internal static extern int ScanSharedObjects();
```

---

## CubemapRenderer のクラッシュ修正

### 症状

`D3DKMTOpenResource` でアクセス違反クラッシュ（`d3d11.dll` 内）。

### 原因

VR シングルパスステレオモードでは `BuiltinRenderTextureType.CameraTarget` がカメラの `targetTexture` ではなく VR 共有アイテクスチャに解決される。これをコマンドバッファのコンストラクタ内で使用するとクラッシュする。

### 修正

コマンドバッファを `_tempRT` 生成後に遅延構築し、`BuiltinRenderTextureType.CameraTarget` の代わりに `_tempRT` を直接参照する。

```csharp
// NG: コンストラクタでの BuiltinRenderTextureType.CameraTarget 使用
cb.SetGlobalTexture(tid, BuiltinRenderTextureType.CameraTarget);

// OK: _tempRT 生成後に RebuildCommandBuffers() を呼び、_tempRT を直接参照
cb.SetGlobalTexture(tid, _tempRT);
```

---

## Spout 送信フロー

```csharp
// 1. Sender を作成 (InitSpout 内、Start() からは呼ばない)
Plugin = PluginEntry.CreateSender("VRCam", width, height);

// 2. 毎フレーム Update イベントを発行 (render thread で共有テクスチャを初期化)
SpoutUtil.IssueSenderPluginEvent(PluginEntry.Event.Update, Plugin);

// 3. 共有テクスチャポインタ取得 (Update 発行後でないと NULL)
if (SharedTexture == null) {
    var ptr = PluginEntry.GetTexturePointer(Plugin);
    SharedTexture = Texture2D.CreateExternalTexture(w, h, TextureFormat.ARGB32, false, false, ptr);
}

// 4. 上下反転しつつコピー (ResoniteSpout 方式)
var tempRt = RenderTexture.GetTemporary(SharedTexture.width, SharedTexture.height, 0, RenderTextureFormat.ARGB32);
Graphics.Blit(SourceTexture, tempRt, new Vector2(1f, -1f), new Vector2(0f, 1f));
Graphics.CopyTexture(tempRt, SharedTexture);
RenderTexture.ReleaseTemporary(tempRt);
```

**注意**: `Start()` から直接 Sender を作ると D3D デバイスが未初期化でクラッシュすることがある。`LateUpdate()` で数フレーム待ってから呼ぶこと（`_initDelayFrames` → `_spoutReady`）。

`Update` イベントはレンダースレッドで処理されるため、`GetTexturePointer` が有効な値を返すまで
数フレームかかる。最初の数回が `IntPtr.Zero` を返すのは**正常**なので警告を出さないこと
（`_sharedTextureRetries` が `SharedTextureWarnAfter` に達して初めて警告する）。

### Sender の作り直し

`SPOUT_ENABLE` / `OUTPUT_WIDTH` / `OUTPUT_HEIGHT` が変わったら Sender を作り直す必要がある。
`UpdateSpoutState()` はフラグを立てるだけで、実際の再構築は `LateUpdate` で 1 フレーム 1 回。
（`ApplyConfigFromHost` は ConfigEntry を連続で代入するため、素直に再構築すると 1 フレームに何度も走る）

作り直す際は `SharedTexture` を必ず null に戻すこと。破棄済みセンダーの共有テクスチャを
掴んだままだと出力が壊れる。

### センダーの破棄は `DestroySharedObject` で行うこと

`KlakSpout_send.dll` は **KlakSpout v1 系** (エクスポート: `CreateSender` / `DestroySharedObject` /
`GetRenderEventFunc` ...)。v1 のレンダーイベントはイベント ID もデータも見ないので、
v2 流に `IssuePluginEventAndData(Dispose, ptr)` を発行しても**何も起きない**。
以前はこれで破棄したつもりになっており、

- 解像度を変えるたびに共有テクスチャがリーク (8K で 128MB ずつ。6 往復で +1.2GB を実測)
- `SPOUT_ENABLE = false` でも `SpoutSenderNames` に `VRCam` が残り、OBS には止まった映像が出続ける

となっていた。`Texture2D` (外部テクスチャ) を先に `Destroy` してから `DestroySharedObject(ptr)` を呼ぶ。
確認方法: GPU カウンタ `\GPU Process Memory(pid_<Renderer>*)\Dedicated Usage` が作り直しで増え続けないこと、
無効化で `SpoutSenderNames` から `VRCam` が消えること。

### 出力解像度とキューブマップ解像度の関係 (CUBEMAP_SIZE = Auto)

**出力解像度を上げてもキューブマップが小さいままなら、ディテールは増えない。**
最終フレームはキューブマップからリサンプルされるだけなので、
8K 出力に 2048 面を組み合わせると「中身 4K の 8K ファイル」になる。

必要な面解像度は次のように決まる。キューブ 1 面は 90 度を N ピクセルで受け持ち、
面中央の点は `p = (N/2)·tanθ` なので、密度は面の中央が最も粗く

```
θ=0 での密度 = (N/2)·(π/180) = N / 114.59  [px/度]
```

出力側が要求する px/度 を下回らない N を選べばよい。
ステレオでは 1 枚に両目が入る (180 系は左右、360 は上下) ので、片目あたりで計算する。

| 出力 | 片目 | 必要 px/度 | 必要 N | 描画 (256 刻み) | キューブマップ (2 の冪) |
|---|---|---|---|---|---|
| 6144x3072 VR180 | 3072x3072 | 17.1 | 1957 | 2048 | 2048 |
| 8192x4096 VR180 | 4096x4096 | 22.8 | 2608 | 2816 | 4096 |
| 8192x8192 360   | 8192x4096 | 22.8 | 2608 | 2816 | 4096 |

既定の 6144x3072 で 2048 になるのは、従来の既定 (High) と一致する。

**2 の冪が必要なのはキューブマップだけ。** カメラの描画先 (`CubemapRenderer._tempRT`) は
任意サイズでよく、面へ書き込む DrawMesh が拡大して貼る。以前は描画も 2 の冪まで切り上げて
8K で 4096 を描いており (必要量の 2.5 倍の画素)、fps が半分以下になっていた。
Auto は `RenderSize` (描画) と `CubemapSize` (テクスチャ) を分けて持つ。明示指定時は両者同じ。

`CubemapToOtherProjection.ComputeAutoRenderSize()` がこれを実装している。
`RenderTarget` / `ProjectionType` / `RenderInStereo` はプロパティにしてあり、
どれかが変わると `ApplyCubemapSizing()` が走って必要なら作り直す。

Auto の上限は 4096 (6 面 ARGB32 で約 400MB)。8K までならこれで足りる。
それ以上を狙うなら `CUBEMAP_SIZE` を明示的に指定する。

**注意**: Spout センダーを作り直す間 `RenderTarget` は一瞬 null になる。
Auto でここを再計算すると毎回小さいキューブマップを作ってしまうので、
`RenderTarget == null` のときは何もしない。

### 高解像度が実用かどうかは実測でしか分からない

センダー作成から 10 秒間の送信レートを 1 回だけログに出している
(`Spout output running at 59.9 fps (8192x4096, cubemap 4096px/face, rendered at 2816px)`)。
解像度を変えた直後にこれを見るのが、設定が実用になるかを知る唯一の方法。

RTX 3060 / ローカルホーム (軽いシーン) での実測 (描画サイズ):
6144x3072+2048 で約 88fps、8192x4096+2816 で約 60fps、8192x8192(360)+2816 で約 48fps。
負荷はほぼキューブマップ面のレンダリング (画素数) が占めるので、
出力解像度だけ上げても fps はあまり落ちない。速くしたければ描く画素を減らす。

### 描く画素を減らすための工夫 (壊さないこと)

- **前方半球のみ**: VR180 / 魚眼は -Z 面を描かず、±X / ±Y 面は +Z 側の半分だけ描く
  (`CubemapRenderer.FrontHemisphereRect` + 非対称の `Matrix4x4.Frustum`)。
  残り半分には前の面の描画が残っているが出力からは参照されない。境界のバイリニア用に数 texel 余分に描く。
  **`camera.rect` で絞ってはいけない。** Deferred の照明パスがビューポートを正しく扱えず、
  ライトで照らされる物が側面だけ真っ黒になる (無照明のグリッドしかないローカルホームでは再現しない。
  The Snowside Inn の壁で判明)。部分サイズの一時 RT に全面で描いて `CopyTexture` で `_tempRT` の
  該当位置へ置く (Renderite 自身の `CameraController` と同じ方式)。面への書き込みは
  `Graphics.ExecuteCommandBuffer` で描画直後に行う。
  検証は 360 (全面描画) の前方 180 度と VR180 をモノラルで撮り比べるのが確実。
- **カメラは `enabled = false`**: 描画は `camera.Render()` で明示的に行う。有効のままだと
  毎フレーム画面へもシーン全体を余計に描く。
- **上下反転は投影パスで**: Spout 用の反転に出力解像度の一時 RT を使わない。
  `_PositionScaleOffset` の縦スケールを負にすると四角形が裏返ってカリングされ、
  **出力が無言で真っ黒になる**。中身は `_UVScaleOffset` で v を反転し、
  四角形側は目の配置 (oy) の符号だけ入れ替える。

ローカルホームのグリッド線は時間で色が変わる。キャプチャ同士で色味が違っても
チャンネル入れ替えを疑う前に、同じ条件で数回撮って比べること。

### キューブマップサイズの変更

`CubemapToOtherProjection.CubemapSize` に代入するだけでは効かない。`_cubemap` と
`CubemapRenderer` は `Start()` で生成されるため、変更時は `SetCubemapSize()` を呼んで
作り直すこと。

**キューブマップの RenderTexture は 2 の冪でなければならない。**
そうでないと `RenderTexture.Create()` が失敗し、Unity の `Player.log` に

```
RenderTexture.Create failed: cube maps must be power of two and width must match height
```

とだけ出て、**出力が無言で真っ黒になる**。`CubeMapSize.Ultra` が 3072 だったため
この選択肢は最初から壊れていた (現在は 4096)。`SetCubemapSize()` が 2 の冪へ丸め、
`Create()` の失敗もログに出すようにしてあるので、新しい値を足すときも壊れ方は分かる。

---

## レンダラー側のログは `Debug.Log` を使わないこと

`UnityEngine.Debug.Log` は Unity の `Player.log` にしか出ず、
`Renderer/BepInEx/LogOutput.log` には流れてこない。
BepInEx 5 の `UnityLogListening = true` でも、Resonite が使う Unity 2019.4 ビルドでは
ログコールバックのフックに失敗して何も転送されない。

`Reso360Spout2.Renderer/Log.cs` の `Log.Info/Warning/Error` (BepInEx の `ManualLogSource`) を使う。
README のトラブルシューティングが `LogOutput.log` を見ろと書いている以上、そこに出ないと意味がない。

---

## Thunderstore パッケージ構成

```toml
# パッチャー (Renderer/BepInEx/patchers/ に配置)
[[build.copy]]
source = "./Reso360Spout2.Patcher/bin/Release/net472/Reso360Spout2.Patcher.dll"
target = "Renderer/BepInEx/patchers/Reso360Spout2.Patcher/"

[[build.copy]]
source = "./Reso360Spout2/KlakSpout_send.dll"
target = "Renderer/BepInEx/patchers/Reso360Spout2.Patcher/"

# Renderer プラグイン (Renderer/BepInEx/plugins/ に配置)
[[build.copy]]
source = "./Reso360Spout2.Renderer/bin/Release/net472/Reso360Spout2.Renderer.dll"
target = "Renderer/BepInEx/plugins/Reso360Spout2.Renderer/"
```

---

## 依存パッケージ (Thunderstore)

`thunderstore.toml` / `manifest.json` の両方を揃えて更新すること。

| パッケージ | 役割 | 無いとどうなるか |
|---|---|---|
| `ResoniteModding-BepisLoader` | Host 側の BepInEx 6 | 何も読み込まれない |
| `ResoniteModding-BepInExResoniteShim` | `[ResonitePlugin]` 属性の解決 | **全プラグインが `0 plugins to load` になる** |
| `ResoniteModding-BepisResoniteWrapper` | `OnEngineReady` | Host プラグインが読み込めない |
| `ResoniteModding-RenderiteHook` | Renderer へ Doorstop を注入 | Renderer 側が一切動かない |
| `ResoniteModding-BepInExRenderer` | Renderer 側の BepInEx 5 本体 | 同上 |
| `ResoniteModding-BepisModSettings` | ゲーム内設定 UI | ダッシュから設定できない |
| `ResoniteModding-BepisLocaleLoader` | BepisModSettings 1.6+ の必須依存 | BepisModSettings が読み込み拒否 |

デバッグ時の注意: BepInEx は `BepInEx/cache/chainloader_typeloader.dat` に型の判定結果を
キャッシュする。依存 DLL を後から足しても判定が古いままになるので、
**プラグイン構成を変えたらキャッシュを消してから起動すること。**

---

## ゲーム内設定 UI (BepisModSettings) との連携

### 前提: 2プロセス問題

BepisModSettings は **Host プロセス** (net10.0) で動く。Renderer プラグインは **Renderer プロセス** (net472 / Unity) で動く。別プロセスなので、BepisModSettings は Renderer の BepInEx ConfigFile に直接アクセスできない。

### 解決策: 共有メモリ経由のコンフィグチャンネル

カメラ状態用の共有メモリ末尾にコンフィグチャンネルを追加する。

```
共有メモリ "Reso360Spout2_Camera" のレイアウト (現在 84 bytes):

[0-39]  カメラ状態 (position / rotation / scale)
[40-43] config version (int) ← Host がインクリメント、Renderer がポーリング
[44-47] SPOUT_ENABLE      (int 0/1)
[48-51] PROJECTION_TYPE   (int)
[52-55] CUBEMAP_SIZE      (int)
[56-59] OUTPUT_WIDTH      (int)
[60-63] OUTPUT_HEIGHT     (int)
[64-67] RENDER_IN_STEREO  (int 0/1)
[68-71] NEAR_CLIP         (float)
[72-75] FAR_CLIP          (float)
[76-79] HIDE_LOCAL        (int 0/1)
[80-83] STEREO_SEPARATION (float)
```

### Host 側 (Plugin.cs) の実装

- Renderer 設定と同名の `ConfigEntry` を `"Renderer"` セクションに追加 → BepisModSettings が自動で UI に表示する
- 各 `SettingChanged` で `WriteRendererConfig()` を呼び出し、値を共有メモリに書いてからバージョンをインクリメント

```csharp
R_NEAR_CLIP.SettingChanged += (_, _) => WriteRendererConfig();

internal static void WriteRendererConfig()
{
    _sharedMemView.Write(68, R_NEAR_CLIP.Value);
    // ... 他の値 ...
    int version = _sharedMemView.ReadInt32(40);
    _sharedMemView.Write(40, version + 1);  // Renderer に変更を通知
}
```

### Renderer 側 (RendererPlugin.cs / SharedMemory.cs) の実装

- `SharedMemoryReader.ReadConfigVersion()` でバージョンを読み、前回値と異なれば `ReadConfig()` で設定を取得
- `LateUpdate()` 内でポーリング（カメラ状態の読み取りと同じタイミング）
- D3D デバイス準備完了後（`_initDelayFrames` カウントダウン後）のみ反映する。それ以前は `ApplyInitialConfig()` で直接適用

```csharp
// LateUpdate 内 (D3D 準備完了後のみ)
int ver = _sharedMem.ReadConfigVersion();
if (ver != _lastConfigVersion && ver != 0)
{
    _lastConfigVersion = ver;
    ApplyConfigFromHost(_sharedMem.ReadConfig());  // ConfigEntry.Value 経由 → SettingChanged 発火
}
```

### 設定項目の追加手順

1. `Plugin.cs`: `SHARED_MEM_SIZE` を +4、コメントのレイアウト図を更新、`ConfigEntry` 追加、`SettingChanged` 登録、`WriteRendererConfig()` に書き込み行を追加
2. `SharedMemory.cs`: `MAP_SIZE` を +4、コメントのレイアウト図を更新、`RendererConfig` 構造体にフィールド追加、`ReadConfig()` に読み取り行を追加、`Default` を更新
3. `RendererPlugin.cs`: `ConfigEntry` 追加、`SettingChanged` でコンポーネントへの適用を登録、`ApplyInitialConfig()` と `ApplyConfigFromHost()` に代入行を追加

### enum の扱い

Host 側の enum（`RendererProjectionType`, `RendererCubeMapSize`）と Renderer 側の enum（`ProjectionType`, `CubeMapSize`）は数値が一致している必要がある。共有メモリでは `(int)` にキャストして int として送受信する。

---

## ResoniteSpout を参考にする際の注意

`../ResoniteSpout/` に参照実装がある。ただし以下の点に注意:

- Thunderstore 公開版は `KlakSpout.dll` のみ含み `KlakSpout_send.dll` は含まない
- Sender 機能（`[DllImport("KlakSpout_send.dll")]` を使う部分）は開発中で、公開版では `KlakSpout_send.dll` がなく動作しない可能性が高い
- `KlakSpout_send.dll` を BepInEx プラグインフォルダに置くだけでは動かない（上記「解決策」参照）

---

## 動作確認はローカルホーム以外のワールドでも行うこと

ローカルホームは無照明のグリッドが中心で、照明・フォグ・ライトプローブ絡みの不具合が出ない。
照明のある室内 (The Snowside Inn)、屋外 (The Luminous Glade)、明るいワールド (Let's Go to the Moon) などで
`CAMERA_SLOT_NAME` を `SpawnArea` にすると多くのワールドでそのまま撮れる (注目ワールド 13 個で全て存在した)。
ワールドブラウザから開いたセッションはオンライン状態だと「誰でも」になるので、
事前に状態を「非表示」にするか、開いた直後にセッションタブでプライベートにすること。

`SpawnArea` で撮るときの見え方の癖 (MOD の不具合ではない):
- `SpawnArea` は床から数 cm の高さにあるので、各面のニアクリップ面が床を切り、
  下半分に面ごとの扇形の境界が出る (NEAR_CLIP を上げると広がる。360 でも同じ)。
- 自分のアバターがスポーン地点に立っているとカメラが体の中に入る。W キーで数歩歩かせてから撮る。
- 見た目の確認には `CAMERA_SLOT_NAME = Head Proxy` (自分の頭の位置) + `NEAR_CLIP = 0.3` が向く。
  目の高さから撮れ、自分の頭の内側がニアクリップで消える。下端には自分の肩が写る。
  頭は待機モーションで常に揺れるので、同じ設定で 4 秒空けて撮っても差が 50 前後出る。
  一致度の比較は `SpawnArea` (静止) で行い、`Head Proxy` は目視確認用にする。
- 1 回の起動で 20 ワールド前後を開くと、Renderite のテクスチャ転送用共有メモリが開けなくなり
  (`SetTexture2DData ... Could not open file`) レンダラーごと落ちる。MOD とは無関係。途中で再起動すること。
- 検証は 360 (全面描画) の正面 180 度と VR180 をモノラルで比べる。一致度 (平均差 /765) が
  1 前後なら正常。アニメーションする空やアバターがあると 5〜15 程度になる。

既知の制限:
- `HIDE_LOCAL` は自分のアバターの頭 (Hidden レイヤー) しか消せない。体は Renderite 上で Default レイヤーにある。

### 重いワールドでは描画回数そのものが fps を決める

オブジェクトの多いワールド (Warden's Crater) では Spout 無効 49fps → 有効 6fps まで落ちた。
解像度を 1024x512 まで下げても 8fps で、影を切っても変わらない。モノラル (描画 5 回) で 10.7fps、
ステレオ (10 回) で 6.2fps なので、`camera.Render()` 1 回あたり約 14ms の固定費 (カリングと描画コール) が
支配的。画素を減らす工夫は軽いワールドでしか効かない。改善するなら描画回数を減らすか、
Spout の更新レートをゲームより下げる設定を足す必要がある。

## 動作確認のやり方 (GUI 操作なし)

ゲーム内に入らなくても、ここまでは自動で確認できる。

**1. Spout センダーが出ているか** — 名前付き共有メモリ `SpoutSenderNames` を読む:

```powershell
# OpenFileMappingW("SpoutSenderNames") を 2560 バイト読むと
# 256 バイト区切りでセンダー名が並んでいる。"VRCam" があれば送信中。
```

**2. 実際の映像を確認する** — センダー情報マップ `VRCam` の先頭 16 バイトが
`{ shareHandle(uint32), width, height, DXGI format }`。
`ID3D11Device::OpenSharedResource` で開いてステージングテクスチャにコピーすれば PNG に落とせる。
SharpDX は `Renderer/Renderite.Renderer_Data/Managed/` にあるものを参照すればよい。

**3. 設定を動かす** — 共有メモリ `Reso360Spout2_Camera` のコンフィグチャンネルに直接書いて
バージョン (offset 40) をインクリメントすれば、Renderer 側が拾う。
BepisModSettings の UI を触らずに投影方式・解像度・IPD などを試せる。

**4. カメラ追従** — `#Camera` スロットが無いワールドでも、`CAMERA_SLOT_NAME` を
ワールドに実在するスロット名（例: ローカルホームの `SpawnPoint`）に変えれば
Host → 共有メモリ → Renderer の経路を検証できる。Host のログが
探索結果とルート直下のスロット名一覧を出す。

## 開発環境

- ゲームパスは自動検出される（上記「ビルド環境」参照）。ハードコードしないこと
- Unity ネイティブプラグインフォルダ: `[ゲームパス]Renderer\Renderite.Renderer_Data\Plugins\x86_64\`
- 動作確認した Resonite バージョン: `2026.9.18.82` (Unity 2019.4.19f1 / Mono / net472)
- ログの場所:
  - Host: `[ゲームパス]BepInEx\LogOutput.log`
  - Renderer: `[ゲームパス]Renderer\BepInEx\LogOutput.log`
  - Unity: `%USERPROFILE%\AppData\LocalLow\Yellow Dog Man Studios\Renderite.Renderer\Player.log`
    （`Debug.Log` はこちらにしか出ない）
