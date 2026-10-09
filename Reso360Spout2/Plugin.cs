using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using HarmonyLib;
using System;
using System.IO.MemoryMappedFiles;
using System.Linq;

namespace Reso360Spout2;

/// <summary>
/// ホストプロセス (Renderite.Host.exe) で動作するプラグイン。
/// FrooxEngine の Harmony パッチでカメラスロット位置を取得し、
/// 共有メモリ経由でレンダラープロセスへ転送する。
/// UnityEngine は一切参照しない。
/// </summary>
[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Reso360Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    // ---- ホスト設定 -----------------------------------------------------------
    public static ConfigEntry<string> CAMERA_SLOT_NAME = null!;

    // ---- レンダラー設定 (BepisModSettings 経由でゲーム内から変更可能) ----------
    public static ConfigEntry<bool>           R_SPOUT_ENABLE     = null!;
    public static ConfigEntry<RendererProjectionType> R_PROJECTION_TYPE  = null!;
    public static ConfigEntry<RendererCubeMapSize>    R_CUBEMAP_SIZE     = null!;
    public static ConfigEntry<int>            R_OUTPUT_WIDTH     = null!;
    public static ConfigEntry<int>            R_OUTPUT_HEIGHT    = null!;
    public static ConfigEntry<bool>           R_RENDER_IN_STEREO = null!;
    public static ConfigEntry<float>          R_NEAR_CLIP        = null!;
    public static ConfigEntry<float>          R_FAR_CLIP         = null!;
    public static ConfigEntry<bool>           R_HIDE_LOCAL       = null!;
    public static ConfigEntry<float>          R_STEREO_SEPARATION = null!;

    // Renderer 側の enum と値が一致するよう同じ数値を使う
    public enum RendererProjectionType { Equirectangular_360 = 0, Equirectangular_180 = 1, FishEye_Circumference = 2, FishEye_Diagonal = 3 }
    // Unity のキューブマップ RenderTexture は 2 の冪でないと作成に失敗し、出力が真っ黒になる。
    // Auto = 0 は Renderer 側が出力解像度から必要な面解像度を計算する。
    // Renderer 側の RendererPlugin.CubeMapSize と数値を一致させること。
    public enum RendererCubeMapSize    { Auto = 0, Low = 512, Mid = 1024, High = 2048, Ultra = 4096 }

    // ---- 共有メモリ -----------------------------------------------------------
    // カメラ状態 (40 bytes) + コンフィグチャンネル (44 bytes) = 84 bytes
    //
    // [0-11]  position (x, y, z)           ← float × 3
    // [12-27] rotation (x, y, z, w)        ← float × 4
    // [28-39] scale    (x, y, z)           ← float × 3
    // [40-43] config version (int)         ← インクリメントで Renderer に変更を通知
    // [44-47] SPOUT_ENABLE      (int 0/1)
    // [48-51] PROJECTION_TYPE   (int)
    // [52-55] CUBEMAP_SIZE      (int)
    // [56-59] OUTPUT_WIDTH      (int)
    // [60-63] OUTPUT_HEIGHT     (int)
    // [64-67] RENDER_IN_STEREO  (int 0/1)
    // [68-71] NEAR_CLIP         (float)
    // [72-75] FAR_CLIP          (float)
    // [76-79] HIDE_LOCAL        (int 0/1)
    // [80-83] STEREO_SEPARATION (float)
    public const string SHARED_MEM_NAME = "Reso360Spout2_Camera";
    private const int   SHARED_MEM_SIZE = 84;

    private static MemoryMappedFile?         _sharedMem;
    private static MemoryMappedViewAccessor? _sharedMemView;

    public override void Load()
    {
        Log = base.Log;

        // 共有メモリ作成（レンダラーより先に起動するため CreateOrOpen）
        //
        // 名前付き MemoryMappedFile は Windows 専用 (CA1416)。Spout 自体が DirectX 前提で
        // Windows でしか動かないので、この MOD も Windows 専用。
        // Linux では例外になるが、握りつぶしてログを出すだけにして本体の起動は妨げない。
        try
        {
            _sharedMem     = MemoryMappedFile.CreateOrOpen(SHARED_MEM_NAME, SHARED_MEM_SIZE);
            _sharedMemView = _sharedMem.CreateViewAccessor();
            Log.LogInfo("[Reso360Spout2] Shared memory created.");
        }
        catch (Exception e)
        {
            Log.LogError("[Reso360Spout2] Failed to create shared memory (this MOD is Windows-only): " + e);
        }

        const string gs = "General";
        CAMERA_SLOT_NAME = Config.Bind(gs, "CAMERA_SLOT_NAME", "#Camera",
            "カメラ位置として参照するワールドスロット名");

        const string rs = "Renderer";
        R_SPOUT_ENABLE     = Config.Bind(rs, "SPOUT_ENABLE",     true,                                    "Spout 出力を有効にする");
        R_PROJECTION_TYPE  = Config.Bind(rs, "PROJECTION_TYPE",  RendererProjectionType.Equirectangular_180, "投影方式");
        R_CUBEMAP_SIZE     = Config.Bind(rs, "CUBEMAP_SIZE",     RendererCubeMapSize.Auto,                "キューブマップサイズ (Auto = 出力解像度に合わせる)");
        R_OUTPUT_WIDTH     = Config.Bind(rs, "OUTPUT_WIDTH",     6144,                                    "出力幅 (px)");
        R_OUTPUT_HEIGHT    = Config.Bind(rs, "OUTPUT_HEIGHT",    3072,                                    "出力高 (px)");
        R_RENDER_IN_STEREO = Config.Bind(rs, "RENDER_IN_STEREO", true,                                    "ステレオレンダリング");
        R_NEAR_CLIP        = Config.Bind(rs, "NEAR_CLIP",        0.01f,                                   "ニアクリップ");
        R_FAR_CLIP         = Config.Bind(rs, "FAR_CLIP",         3000f,                                   "ファークリップ");
        R_HIDE_LOCAL       = Config.Bind(rs, "HIDE_LOCAL",       true,                                    "ローカルユーザーを非表示");
        R_STEREO_SEPARATION = Config.Bind(rs, "STEREO_SEPARATION", 0.065f,                                 "ステレオ間距離 (IPD, m)");

        // 設定変更時に共有メモリへ書き出す
        R_SPOUT_ENABLE    .SettingChanged += (_, _) => WriteRendererConfig();
        R_PROJECTION_TYPE .SettingChanged += (_, _) => WriteRendererConfig();
        R_CUBEMAP_SIZE    .SettingChanged += (_, _) => WriteRendererConfig();
        R_OUTPUT_WIDTH    .SettingChanged += (_, _) => WriteRendererConfig();
        R_OUTPUT_HEIGHT   .SettingChanged += (_, _) => WriteRendererConfig();
        R_RENDER_IN_STEREO.SettingChanged += (_, _) => WriteRendererConfig();
        R_NEAR_CLIP       .SettingChanged += (_, _) => WriteRendererConfig();
        R_FAR_CLIP        .SettingChanged += (_, _) => WriteRendererConfig();
        R_HIDE_LOCAL       .SettingChanged += (_, _) => WriteRendererConfig();
        R_STEREO_SEPARATION.SettingChanged += (_, _) => WriteRendererConfig();

        // 初期値を共有メモリに書き込む（Renderer 起動前でも問題ない）
        WriteRendererConfig();

        var harmony = new Harmony("dev.kokoa.Reso360Spout2");
        harmony.PatchAll();

        ResoniteHooks.OnEngineReady += OnEngineReady;
        Log.LogInfo($"Plugin {PluginMetadata.GUID} loaded.");
    }

    private void OnEngineReady()
    {
        Log.LogInfo("[Reso360Spout2] Engine ready.");
    }

    /// <summary>共有メモリにカメラ状態を書き込む（Unity スレッドから読まれる）</summary>
    private static void WriteCameraState(
        float px, float py, float pz,
        float rx, float ry, float rz, float rw,
        float sx, float sy, float sz)
    {
        if (_sharedMemView == null) return;
        _sharedMemView.Write( 0, px); _sharedMemView.Write( 4, py); _sharedMemView.Write( 8, pz);
        _sharedMemView.Write(12, rx); _sharedMemView.Write(16, ry); _sharedMemView.Write(20, rz); _sharedMemView.Write(24, rw);
        _sharedMemView.Write(28, sx); _sharedMemView.Write(32, sy); _sharedMemView.Write(36, sz);
    }

    /// <summary>レンダラー設定を共有メモリに書き込み、バージョンをインクリメントして Renderer に通知する</summary>
    internal static void WriteRendererConfig()
    {
        if (_sharedMemView == null) return;

        _sharedMemView.Write(44, R_SPOUT_ENABLE    .Value ? 1 : 0);
        _sharedMemView.Write(48, (int)R_PROJECTION_TYPE.Value);
        _sharedMemView.Write(52, (int)R_CUBEMAP_SIZE   .Value);
        _sharedMemView.Write(56, R_OUTPUT_WIDTH    .Value);
        _sharedMemView.Write(60, R_OUTPUT_HEIGHT   .Value);
        _sharedMemView.Write(64, R_RENDER_IN_STEREO.Value ? 1 : 0);
        _sharedMemView.Write(68, R_NEAR_CLIP       .Value);
        _sharedMemView.Write(72, R_FAR_CLIP        .Value);
        _sharedMemView.Write(76, R_HIDE_LOCAL       .Value ? 1 : 0);
        _sharedMemView.Write(80, R_STEREO_SEPARATION.Value);

        // バージョンをインクリメント（Renderer がポーリングで変化を検出する）
        int version = _sharedMemView.ReadInt32(40);
        _sharedMemView.Write(40, version + 1);

        Log.LogDebug($"[Reso360Spout2] Renderer config written (version={version + 1})");
    }

    // ---- Harmony パッチ -------------------------------------------------------
    [HarmonyPatch(typeof(FrooxEngine.Engine), "RunUpdateLoop")]
    class Patch
    {
        // FindChildInHierarchy は階層全走査なので毎フレーム呼ばずにキャッシュする
        private static FrooxEngine.Slot?  _cachedSlot;
        private static FrooxEngine.World? _cachedWorld;
        private static string?            _cachedName;
        private static int                _searchCooldown;
        private static bool               _loggedMissing;

        static void Postfix(FrooxEngine.Engine __instance)
        {
            var world = __instance.WorldManager.FocusedWorld;
            if (world == null) return;

            world.RunSynchronously(() =>
            {
                var slot = ResolveCameraSlot(world);
                if (slot == null) return;

                WriteCameraState(
                    slot.GlobalPosition.X, slot.GlobalPosition.Y, slot.GlobalPosition.Z,
                    slot.GlobalRotation.X, slot.GlobalRotation.Y, slot.GlobalRotation.Z, slot.GlobalRotation.W,
                    slot.GlobalScale.X,    slot.GlobalScale.Y,    slot.GlobalScale.Z);
            });
        }

        /// <summary>カメラスロットを解決する。見つかった参照はワールド / 設定名が変わるまで使い回す。</summary>
        private static FrooxEngine.Slot? ResolveCameraSlot(FrooxEngine.World world)
        {
            string name = CAMERA_SLOT_NAME.Value;

            // キャッシュが有効ならそのまま使う
            if (_cachedSlot != null && !_cachedSlot.IsDestroyed &&
                _cachedWorld == world && _cachedName == name)
                return _cachedSlot;

            if (_cachedWorld != world || _cachedName != name)
            {
                _cachedWorld    = world;
                _cachedName     = name;
                _searchCooldown = 0;
                _loggedMissing  = false;
            }
            _cachedSlot = null;

            // 見つからない間、毎フレーム階層全走査しないよう間引く
            if (_searchCooldown > 0) { _searchCooldown--; return null; }

            var slot = world.RootSlot.FindChildInHierarchy(name);
            if (slot != null)
            {
                _cachedSlot    = slot;
                _loggedMissing = false;
                Log.LogInfo($"[Reso360Spout2] Camera slot '{name}' found in '{world.RawName}'.");
                return slot;
            }

            _searchCooldown = 60;   // 約 1 秒おきに再探索
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                Log.LogInfo($"[Reso360Spout2] Camera slot '{name}' not found in '{world.RawName}'. " +
                            $"Root children: {string.Join(", ", world.RootSlot.Children.Select(c => c.Name))}");
            }
            return null;
        }
    }
}
