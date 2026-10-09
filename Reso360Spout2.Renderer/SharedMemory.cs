using System;
using System.IO.MemoryMappedFiles;
using UnityEngine;

namespace Reso360Spout2Renderer
{
    /// <summary>
    /// ホストプロセスが書き込んだカメラ状態とレンダラー設定を共有メモリから読み取る。
    ///
    /// 構造: 84 bytes (Plugin.cs の SHARED_MEM_SIZE と必ず一致させること)
    ///   [0-11]  position (x, y, z)           float × 3
    ///   [12-27] rotation (x, y, z, w)        float × 4
    ///   [28-39] scale    (x, y, z)           float × 3
    ///   [40-43] config version (int)
    ///   [44-47] SPOUT_ENABLE     (int 0/1)
    ///   [48-51] PROJECTION_TYPE  (int)
    ///   [52-55] CUBEMAP_SIZE     (int)
    ///   [56-59] OUTPUT_WIDTH     (int)
    ///   [60-63] OUTPUT_HEIGHT    (int)
    ///   [64-67] RENDER_IN_STEREO (int 0/1)
    ///   [68-71] NEAR_CLIP         (float)
    ///   [72-75] FAR_CLIP          (float)
    ///   [76-79] HIDE_LOCAL        (int 0/1)
    ///   [80-83] STEREO_SEPARATION (float)
    /// </summary>
    public class SharedMemoryReader : IDisposable
    {
        public const string MAP_NAME = "Reso360Spout2_Camera";
        private const int   MAP_SIZE = 84;

        private MemoryMappedFile?         _mmf;
        private MemoryMappedViewAccessor? _view;
        private bool _disposed;

        public bool IsOpen => _view != null;

        public bool TryOpen()
        {
            if (IsOpen) return true;
            try
            {
                _mmf  = MemoryMappedFile.CreateOrOpen(MAP_NAME, MAP_SIZE);
                _view = _mmf.CreateViewAccessor(0, MAP_SIZE, MemoryMappedFileAccess.ReadWrite);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- カメラ状態 -------------------------------------------------------

        public Vector3 ReadPosition()
        {
            if (_view == null) return Vector3.zero;
            return new Vector3(
                _view.ReadSingle(0),
                _view.ReadSingle(4),
                _view.ReadSingle(8));
        }

        public Quaternion ReadRotation()
        {
            if (_view == null) return Quaternion.identity;
            var q = new Quaternion(
                _view.ReadSingle(12),
                _view.ReadSingle(16),
                _view.ReadSingle(20),
                _view.ReadSingle(24));
            // 未初期化(全0)の場合は identity を返す
            return (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)
                ? Quaternion.identity : q;
        }

        public Vector3 ReadScale()
        {
            if (_view == null) return Vector3.one;
            var s = new Vector3(
                _view.ReadSingle(28),
                _view.ReadSingle(32),
                _view.ReadSingle(36));
            // 未初期化(全0)の場合は Vector3.one を返す
            return s.sqrMagnitude < 1e-6f ? Vector3.one : s;
        }

        // ---- レンダラー設定 ---------------------------------------------------

        public int ReadConfigVersion() => _view?.ReadInt32(40) ?? 0;

        public RendererConfig ReadConfig()
        {
            if (_view == null) return RendererConfig.Default;
            return new RendererConfig
            {
                SpoutEnable    = _view.ReadInt32(44) != 0,
                ProjectionType = (ProjectionType)_view.ReadInt32(48),
                CubemapSize    = _view.ReadInt32(52),
                OutputWidth    = _view.ReadInt32(56),
                OutputHeight   = _view.ReadInt32(60),
                RenderInStereo = _view.ReadInt32(64) != 0,
                NearClip       = _view.ReadSingle(68),
                FarClip        = _view.ReadSingle(72),
                HideLocal        = _view.ReadInt32(76) != 0,
                StereoSeparation = _view.ReadSingle(80),
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view?.Dispose();
            _mmf?.Dispose();
        }
    }

    /// <summary>共有メモリから読み取ったレンダラー設定のスナップショット</summary>
    public struct RendererConfig
    {
        public bool          SpoutEnable;
        public ProjectionType ProjectionType;
        public int           CubemapSize;
        public int           OutputWidth;
        public int           OutputHeight;
        public bool          RenderInStereo;
        public float         NearClip;
        public float         FarClip;
        public bool          HideLocal;
        public float         StereoSeparation;

        public static readonly RendererConfig Default = new RendererConfig
        {
            SpoutEnable      = true,
            ProjectionType   = ProjectionType.Equirectangular_180,
            CubemapSize      = CubemapToOtherProjection.AutoCubemapSize,
            OutputWidth      = 6144,
            OutputHeight     = 3072,
            RenderInStereo   = true,
            NearClip         = 0.01f,
            FarClip          = 3000f,
            HideLocal        = true,
            StereoSeparation = 0.065f,
        };
    }
}
