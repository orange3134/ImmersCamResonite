using BepInEx;
using BepInEx.Configuration;
using System;
using System.IO;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Reso360Spout2Renderer
{
    /// <summary>
    /// レンダラープロセス (Renderite.Renderer) で動作する BepInEx 5 プラグイン。
    /// 共有メモリからカメラ状態を受け取り、360度/VR180映像をSpout送信する。
    /// </summary>
    // バージョンは Directory.Build.props の $(Version) と揃えること (属性なので定数が必要)
    [BepInPlugin("dev.kokoa.Reso360Spout2.Renderer", "Reso360Spout2 Renderer", "1.0.1")]
    public class RendererPlugin : BaseUnityPlugin
    {
        public static RendererPlugin? Instance { get; private set; }

        // ---- 設定 -------------------------------------------------------------
        public static ConfigEntry<bool>          SPOUT_ENABLE     = null!;
        public static ConfigEntry<ProjectionType> PROJECTION_TYPE  = null!;
        public static ConfigEntry<CubeMapSize>   CUBEMAP_SIZE     = null!;
        public static ConfigEntry<int>           OUTPUT_WIDTH     = null!;
        public static ConfigEntry<int>           OUTPUT_HEIGHT    = null!;
        public static ConfigEntry<bool>          RENDER_IN_STEREO = null!;
        public static ConfigEntry<float>         NEAR_CLIP        = null!;
        public static ConfigEntry<float>         FAR_CLIP         = null!;
        public static ConfigEntry<bool>          HIDE_LOCAL        = null!;
        public static ConfigEntry<float>         STEREO_SEPARATION = null!;

        // Unity のキューブマップ RenderTexture は 2 の冪でなければ作成に失敗する
        // (以前 Ultra = 3072 になっており、選ぶと出力が真っ黒になっていた)。
        // Auto = 0 は出力解像度から必要な面解像度を計算する (CubemapToOtherProjection 参照)。
        // Host 側の Reso360Plugin.RendererCubeMapSize と数値を一致させること。
        public enum CubeMapSize { Auto = 0, Low = 512, Mid = 1024, High = 2048, Ultra = 4096 }

        private string _pluginDir = "";
        private UnityEntry? _unityEntry;
        private GameObject? _entryGo;

        // ---- Awake: BepInEx 5 初期化 ------------------------------------------
        void Awake()
        {
            Instance = this;
            _pluginDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? "";

            // 設定バインド
            const string s = "General";
            SPOUT_ENABLE     = Config.Bind(s, "SPOUT_ENABLE",     true,                        "Spout 出力を有効にする");
            PROJECTION_TYPE  = Config.Bind(s, "PROJECTION_TYPE",  ProjectionType.Equirectangular_180, "投影方式");
            CUBEMAP_SIZE     = Config.Bind(s, "CUBEMAP_SIZE",     CubeMapSize.Auto,            "キューブマップサイズ (Auto = 出力解像度に合わせる)");
            OUTPUT_WIDTH     = Config.Bind(s, "OUTPUT_WIDTH",     6144,                        "出力幅 (px)");
            OUTPUT_HEIGHT    = Config.Bind(s, "OUTPUT_HEIGHT",    3072,                        "出力高 (px)");
            RENDER_IN_STEREO = Config.Bind(s, "RENDER_IN_STEREO", true,                        "ステレオレンダリング");
            NEAR_CLIP        = Config.Bind(s, "NEAR_CLIP",        0.01f,                       "ニアクリップ");
            FAR_CLIP         = Config.Bind(s, "FAR_CLIP",         3000f,                       "ファークリップ");
            HIDE_LOCAL        = Config.Bind(s, "HIDE_LOCAL",        true,   "ローカルユーザーを非表示");
            STEREO_SEPARATION = Config.Bind(s, "STEREO_SEPARATION", 0.065f, "ステレオ間距離 (IPD, m)");

            // UnityEntry を持つ GameObject を生成
            _entryGo = new GameObject("___Reso360Spout2");
            DontDestroyOnLoad(_entryGo);
            _unityEntry = _entryGo.AddComponent<UnityEntry>();
            _unityEntry.PluginDir = _pluginDir;

            // 設定変更ハンドラ
            HIDE_LOCAL.SettingChanged       += (_, _2) => _unityEntry?.ApplyHideLocal();
            NEAR_CLIP.SettingChanged        += (_, _2) => { if (_unityEntry?.CameraComponent != null) _unityEntry.CameraComponent.nearClipPlane = NEAR_CLIP.Value; };
            FAR_CLIP.SettingChanged         += (_, _2) => { if (_unityEntry?.CameraComponent != null) _unityEntry.CameraComponent.farClipPlane  = FAR_CLIP.Value; };
            CUBEMAP_SIZE.SettingChanged     += (_, _2) => { if (_unityEntry?.cubeComponent   != null) _unityEntry.cubeComponent.SetCubemapSize((int)CUBEMAP_SIZE.Value); };
            PROJECTION_TYPE.SettingChanged  += (_, _2) => { if (_unityEntry?.cubeComponent   != null) _unityEntry.cubeComponent.ProjectionType  = PROJECTION_TYPE.Value; };
            RENDER_IN_STEREO .SettingChanged += (_, _2) => { if (_unityEntry?.cubeComponent   != null) _unityEntry.cubeComponent.RenderInStereo  = RENDER_IN_STEREO.Value; };
            STEREO_SEPARATION.SettingChanged += (_, _2) => { if (_unityEntry?.cubeComponent   != null) _unityEntry.cubeComponent.StereoSeparation = STEREO_SEPARATION.Value; };
            // Spout センダーを作り直す必要がある設定
            SPOUT_ENABLE     .SettingChanged += (_, _2) => _unityEntry?.UpdateSpoutState();
            OUTPUT_WIDTH     .SettingChanged += (_, _2) => _unityEntry?.UpdateSpoutState();
            OUTPUT_HEIGHT    .SettingChanged += (_, _2) => _unityEntry?.UpdateSpoutState();

            Logger.LogInfo("Reso360Spout2 Renderer plugin loaded.");
        }
    }

    // ---- MonoBehaviour: Unity レンダリング担当 ----------------------------------
    public class UnityEntry : MonoBehaviour
    {
        public Camera? CameraComponent;
        public CubemapToOtherProjection? cubeComponent;
        public IntPtr Plugin = IntPtr.Zero;
        public RenderTexture? SourceTexture;
        public Texture2D? SharedTexture;
        public string PluginDir = "";

        private SharedMemoryReader _sharedMem = new SharedMemoryReader();
        private GameObject? _cameraRoot;
        // Spout 初期化を遅延させる: D3D デバイスが完全に準備されてから呼ぶ
        private int _initDelayFrames = 1000;
        // ホストからのコンフィグ変更を検出するためのバージョン追跡
        private int _lastConfigVersion = -1;
        // 設定変更による Spout 再構築の予約 (1 フレームに 1 回だけ実行する)
        private bool _spoutRebuildRequested;
        // D3D 準備完了後のみ Spout を触ってよい
        private bool _spoutReady;
        // 共有テクスチャ取得の連続失敗回数 (最初の数フレームは正常に失敗する)
        private int _sharedTextureRetries;
        private const int SharedTextureWarnAfter = 120;

        // 出力解像度の上限は D3D11 のテクスチャ上限 (16384)。
        // これを超えると CreateSender も RenderTexture も黙って失敗する。
        private const int MinOutputSize = 16;
        private const int MaxOutputSize = 16384;

        // センダー作成後に 1 回だけ実測する送信レート
        private bool  _ratePending;
        private int   _rateFrames;
        private float _rateStartTime;
        private const float RateWindowSeconds = 10f;

        void Start()
        {
            // 共有メモリを開く
            if (!_sharedMem.TryOpen())
                Log.Warning("Shared memory not yet available; will retry.");

            // 先にカメラ作成 (cubeComponent が作られる)、その後シェーダーをセット
            CreateCamera();
            LoadShaders();  // cubeComponent にシェーダーをセット

            // 初期設定を適用 (SettingChanged を経由しない直接適用 — D3D 未準備のため Spout 系は後回し)
            ApplyInitialConfig();

            StartCoroutine(PostRenderLoop());

            // Spout の InitSpout() は LateUpdate() で遅延呼び出しする
            // (Start() 時点では D3D デバイスが Spout に未登録のため NULL device クラッシュが発生する)
        }

        void LateUpdate()
        {
            try
            {
                // 共有メモリ再試行
                if (!_sharedMem.IsOpen) _sharedMem.TryOpen();

                // Spout 初期化を数フレーム遅延 (D3D デバイス準備待ち)
                if (_initDelayFrames > 0)
                {
                    _initDelayFrames--;
                    if (_initDelayFrames == 0)
                    {
                        Log.Info("Initializing Spout sender (deferred)...");
                        _spoutReady = true;
                        RebuildSpout();
                        _spoutRebuildRequested = false;
                    }
                    return;
                }

                // ---- D3D 準備完了後: ホストからのコンフィグ変更を検出して適用 ----
                if (_sharedMem.IsOpen)
                {
                    int ver = _sharedMem.ReadConfigVersion();
                    if (ver != _lastConfigVersion && ver != 0)
                    {
                        _lastConfigVersion = ver;
                        ApplyConfigFromHost(_sharedMem.ReadConfig());
                    }
                }

                // 設定変更で予約された Spout 再構築をここでまとめて 1 回だけ行う
                if (_spoutRebuildRequested)
                {
                    _spoutRebuildRequested = false;
                    RebuildSpout();
                }

                // ホストプロセスからカメラ状態を受け取る
                transform.position   = _sharedMem.ReadPosition();
                transform.rotation   = _sharedMem.ReadRotation();
                transform.localScale = _sharedMem.ReadScale();

            }
            catch (Exception e)
            {
                Log.Error("LateUpdate failed: " + e);
            }
        }

        void OnDestroy()
        {
            DestroySpout();
            _sharedMem.Dispose();
        }

        /// <summary>
        /// 起動時の直接適用: SettingChanged を経由せずコンポーネントに直接セットする。
        /// D3D デバイス未準備のため Spout 系 (CreateSender) は呼ばない。
        /// 共有メモリに有効なコンフィグがあればそちらを優先する。
        /// </summary>
        private void ApplyInitialConfig()
        {
            RendererConfig cfg = RendererConfig.Default;
            if (_sharedMem.IsOpen)
            {
                int ver = _sharedMem.ReadConfigVersion();
                if (ver != 0)
                {
                    _lastConfigVersion = ver;
                    cfg = _sharedMem.ReadConfig();
                    Log.Info($"Initial config from host (version={ver}): Projection={cfg.ProjectionType}, CubemapSize={cfg.CubemapSize}");
                }
            }

            // ConfigEntry も更新しておくことで BepInEx config ファイルに反映される
            RendererPlugin.SPOUT_ENABLE    .Value = cfg.SpoutEnable;
            RendererPlugin.PROJECTION_TYPE .Value = cfg.ProjectionType;
            RendererPlugin.CUBEMAP_SIZE    .Value = (RendererPlugin.CubeMapSize)cfg.CubemapSize;
            RendererPlugin.OUTPUT_WIDTH    .Value = cfg.OutputWidth;
            RendererPlugin.OUTPUT_HEIGHT   .Value = cfg.OutputHeight;
            RendererPlugin.RENDER_IN_STEREO.Value = cfg.RenderInStereo;
            RendererPlugin.NEAR_CLIP       .Value = cfg.NearClip;
            RendererPlugin.FAR_CLIP        .Value = cfg.FarClip;
            RendererPlugin.HIDE_LOCAL       .Value = cfg.HideLocal;
            RendererPlugin.STEREO_SEPARATION.Value = cfg.StereoSeparation;

            // Spout 系を除くコンポーネントへ直接適用 (SettingChanged は既に上で発火しているが念のため)
            cubeComponent!.SetCubemapSize(cfg.CubemapSize);
            cubeComponent.ProjectionType    = cfg.ProjectionType;
            cubeComponent.RenderInStereo    = cfg.RenderInStereo;
            cubeComponent.StereoSeparation  = cfg.StereoSeparation;
            CameraComponent!.nearClipPlane  = cfg.NearClip;
            CameraComponent.farClipPlane    = cfg.FarClip;
            ApplyHideLocal(cfg.HideLocal);
        }

        /// <summary>
        /// D3D 準備完了後: ConfigEntry.Value 経由でセット → SettingChanged ハンドラが各コンポーネントに適用する。
        /// SPOUT_ENABLE 変更時は UpdateSpoutState() が呼ばれる (D3D 準備済みなので安全)。
        /// </summary>
        private void ApplyConfigFromHost(RendererConfig cfg)
        {
            Log.Info($"Config updated from host: SpoutEnable={cfg.SpoutEnable}, Projection={cfg.ProjectionType}, CubemapSize={cfg.CubemapSize}, {cfg.OutputWidth}x{cfg.OutputHeight}");

            RendererPlugin.SPOUT_ENABLE    .Value = cfg.SpoutEnable;
            RendererPlugin.PROJECTION_TYPE .Value = cfg.ProjectionType;
            RendererPlugin.CUBEMAP_SIZE    .Value = (RendererPlugin.CubeMapSize)cfg.CubemapSize;
            RendererPlugin.OUTPUT_WIDTH    .Value = cfg.OutputWidth;
            RendererPlugin.OUTPUT_HEIGHT   .Value = cfg.OutputHeight;
            RendererPlugin.RENDER_IN_STEREO.Value = cfg.RenderInStereo;
            RendererPlugin.NEAR_CLIP       .Value = cfg.NearClip;
            RendererPlugin.FAR_CLIP        .Value = cfg.FarClip;
            RendererPlugin.HIDE_LOCAL       .Value = cfg.HideLocal;
            RendererPlugin.STEREO_SEPARATION.Value = cfg.StereoSeparation;
        }

        IEnumerator PostRenderLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame(); // 全てのカメラの描画が終わるのを待つ

                if (SourceTexture != null && Plugin != IntPtr.Zero)
                {
                    cubeComponent!.Rendering();
                    SendToSpout();
                }
            }
        }

        private void LoadShaders()
        {
            var bundlePath = Path.Combine(PluginDir, "cubeto360");
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null) { Log.Warning($"AssetBundle not found: {bundlePath}"); return; }

            Shader? cubemapShader = null, cubemapRendererShader = null;
            foreach (var shader in bundle.LoadAllAssets<Shader>())
            {
                if (shader.name == "Unlit/CubemapToOtherProjection") cubemapShader = shader;
                else if (shader.name == "Unlit/CubemapRenderer")      cubemapRendererShader = shader;
            }
            // CubemapToOtherProjection に渡す（Start より前に設定が必要なため直接セット）
            if (cubeComponent != null)
            {
                cubeComponent.CubemapShader         = cubemapShader;
                cubeComponent.CubemapRendererShader = cubemapRendererShader;
            }
            Log.Info("Shaders loaded.");
        }

        private void CreateCamera()
        {
            _cameraRoot = new GameObject("CameraRoot");
            _cameraRoot.transform.SetParent(transform, false);

            CameraComponent = _cameraRoot.AddComponent<Camera>();
            CameraComponent.depth            = -128;
            CameraComponent.fieldOfView      = 90f;
            CameraComponent.stereoTargetEye  = StereoTargetEyeMask.None;
            CameraComponent.stereoSeparation = 0.065f;
            // 描画は CubemapRenderer が camera.Render() で明示的に行う。
            // 有効のままだと Unity が毎フレーム画面へもシーン全体を余分に描いてしまう
            // (Resonite のカメラに上書きされるので見えず、GPU 時間だけ食う)。
            CameraComponent.enabled = false;

            cubeComponent = _cameraRoot.AddComponent<CubemapToOtherProjection>();
            cubeComponent.FlipVertically = true;
            Log.Info("Camera created.");
        }

        private void SendToSpout()
        {
            if (Plugin == IntPtr.Zero) return;

            // ResoniteSpout と同様: まず Update イベントを発行してから SharedTexture を取得する
            SpoutUtil.IssueSenderPluginEvent(PluginEntry.Event.Update, Plugin);

            if (SharedTexture == null)
            {
                // Update イベントはレンダースレッドで処理されるため、
                // 共有テクスチャが用意されるまで数フレームかかる (最初の数回の失敗は正常)
                var ptr = PluginEntry.GetTexturePointer(Plugin);
                if (ptr == IntPtr.Zero)
                {
                    if (++_sharedTextureRetries == SharedTextureWarnAfter)
                        Log.Warning("Spout shared texture is still unavailable after " +
                                         $"{SharedTextureWarnAfter} frames. Is KlakSpout_send.dll present in " +
                                         "Renderite.Renderer_Data/Plugins/x86_64?");
                    return;
                }

                SharedTexture = Texture2D.CreateExternalTexture(
                    PluginEntry.GetTextureWidth(Plugin),
                    PluginEntry.GetTextureHeight(Plugin),
                    TextureFormat.ARGB32, false, false, ptr);
                SharedTexture.hideFlags = HideFlags.DontSave;
                _sharedTextureRetries = 0;
                Log.Info($"Spout sender ready: {SharedTexture.width}x{SharedTexture.height}");
            }

            // 上下反転は投影パス (CubemapToOtherProjection.FlipVertically) で済んでいるので、
            // そのままコピーするだけでよい
            Graphics.CopyTexture(SourceTexture, SharedTexture);

            MeasureSendRate();
        }

        /// <summary>
        /// センダー作成後の最初の数秒だけ送信レートを測り、1 回だけログに出す。
        /// 高解像度が自分の GPU で実用になるかどうかは、この数字でしか分からない。
        /// </summary>
        private void MeasureSendRate()
        {
            if (!_ratePending) return;

            _rateFrames++;
            float elapsed = Time.realtimeSinceStartup - _rateStartTime;
            if (elapsed < RateWindowSeconds) return;

            _ratePending = false;
            float fps = _rateFrames / elapsed;
            string note = fps < 10f
                ? " — lower OUTPUT_WIDTH/HEIGHT or CUBEMAP_SIZE if this is too slow"
                : "";
            Log.Info($"Spout output running at {fps:0.0} fps" +
                     $" ({SharedTexture!.width}x{SharedTexture.height}, " +
                     $"cubemap {cubeComponent!.CubemapSize}px/face, rendered at {cubeComponent.RenderSize}px){note}");
        }

        public void ApplyHideLocal() => ApplyHideLocal(RendererPlugin.HIDE_LOCAL.Value);

        public void ApplyHideLocal(bool hideLocal)
        {
            if (CameraComponent == null) return;
            if (hideLocal)
            {
                CameraComponent.cullingMask &= ~((1 << 29) | (1 << 30) | (1 << 31));
                CameraComponent.cullingMask &= ~(1 << 28);
            }
            else
            {
                CameraComponent.cullingMask |= (1 << 29) | (1 << 30) | (1 << 31);
                CameraComponent.cullingMask &= ~(1 << 28);
            }
        }

        /// <summary>
        /// Spout 送信の作り直しを予約する。SPOUT_ENABLE / OUTPUT_WIDTH / OUTPUT_HEIGHT が
        /// 変わるたびに呼ばれるが、実際の作り直しは LateUpdate で 1 フレーム 1 回にまとめる。
        /// </summary>
        public void UpdateSpoutState() => _spoutRebuildRequested = true;

        private void RebuildSpout()
        {
            // D3D デバイスが Spout に登録される前に CreateSender を呼ぶとクラッシュする
            if (!_spoutReady) return;

            DestroySpout();
            if (!RendererPlugin.SPOUT_ENABLE.Value) return;

            int requestedW = RendererPlugin.OUTPUT_WIDTH.Value;
            int requestedH = RendererPlugin.OUTPUT_HEIGHT.Value;
            int w = Mathf.Clamp(requestedW, MinOutputSize, MaxOutputSize);
            int h = Mathf.Clamp(requestedH, MinOutputSize, MaxOutputSize);
            if (w != requestedW || h != requestedH)
                Log.Warning($"Output size {requestedW}x{requestedH} is out of range " +
                            $"({MinOutputSize}..{MaxOutputSize}); using {w}x{h}.");

            Plugin = PluginEntry.CreateSender("VRCam", w, h);
            if (Plugin == IntPtr.Zero)
            {
                Log.Error("CreateSender failed. KlakSpout_send.dll is probably not loaded by " +
                               "Unity (it must live in Renderite.Renderer_Data/Plugins/x86_64; " +
                               "Reso360Spout2.Patcher puts it there at startup).");
                return;
            }

            // 投影パスの Blit 先でしかないので深度バッファは不要
            SourceTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            // RenderTarget をセットすると CUBEMAP_SIZE = Auto のキューブマップ解像度も再計算される
            cubeComponent!.RenderTarget = SourceTexture;
            Log.Info($"Spout sender created: {w}x{h} (cubemap {cubeComponent.CubemapSize}px/face, rendered at {cubeComponent.RenderSize}px)");

            // 解像度を上げたときに実際に何 fps 出るのかは、やってみないと分からない。
            // 作り直しのたびに 1 回だけ実測して出す。
            _rateFrames = 0;
            _rateStartTime = Time.realtimeSinceStartup;
            _ratePending = true;
        }

        private void DestroySpout()
        {
            // 旧センダーの共有テクスチャを掴んだままにしない。
            // ネイティブ側を破棄する前に、それを参照する外部テクスチャを先に捨てる (KlakSpout v1 と同じ順序)
            if (SharedTexture != null) { Destroy(SharedTexture); SharedTexture = null; }
            if (Plugin != IntPtr.Zero)
            {
                // レンダーイベントでは破棄できない (KlakSpout v1。PluginEntry 参照)
                PluginEntry.DestroySharedObject(Plugin);
                Plugin = IntPtr.Zero;
            }
            if (SourceTexture != null) { SourceTexture.Release(); Destroy(SourceTexture); SourceTexture = null; }
            if (cubeComponent != null) cubeComponent.RenderTarget = null;
            _sharedTextureRetries = 0;
        }
    }
}
