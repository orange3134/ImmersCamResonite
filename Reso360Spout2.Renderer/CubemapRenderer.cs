using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Reso360Spout2Renderer
{
    public enum GammaConvertType { None = 0, Linear_to_sRGB, Linear_to_BT709 }

    public class CubemapRenderer : IDisposable
    {
        private Shader? _shader;
        private Material? _material;
        private Mesh? _mesh;
        private readonly int _cubemapSize;
        // カメラが実際に描く 1 面の解像度。キューブマップ (2 の冪) より小さくてよく、
        // 面へ書き込むコマンドバッファの DrawMesh が拡大して貼る。
        private readonly int _renderSize;
        private RenderTexture? _cubemap;
        private RenderTexture? _tempRT;
        // BuiltinRenderTextureType.CameraTarget はレンダラープロセスの VR コンテキストで
        // VR 共有テクスチャに解決されクラッシュするため、_tempRT 確定後に再構築する
        private CommandBuffer[]? _commandBuffers;

        struct FaceInfo
        {
            public Quaternion Rotate;
            public CubemapFace Face;
            public Vector3 PositionShift;
            public FaceInfo(Quaternion r, CubemapFace f, Vector3 p) { Rotate = r; Face = f; PositionShift = p; }
        }

        private static readonly FaceInfo[] _faces =
        {
            new FaceInfo(Quaternion.Euler(  0f,  90f, 0f), CubemapFace.PositiveX, new Vector3( 0f,  1f, 0f)),
            new FaceInfo(Quaternion.Euler(  0f, -90f, 0f), CubemapFace.NegativeX, new Vector3( 0f, -1f, 0f)),
            new FaceInfo(Quaternion.Euler(-90f,   0f, 0f), CubemapFace.PositiveY, Vector3.zero),
            new FaceInfo(Quaternion.Euler( 90f,   0f, 0f), CubemapFace.NegativeY, Vector3.zero),
            new FaceInfo(Quaternion.Euler(  0f,   0f, 0f), CubemapFace.PositiveZ, new Vector3( 1f,  0f, 0f)),
            new FaceInfo(Quaternion.Euler(  0f, 180f, 0f), CubemapFace.NegativeZ, new Vector3(-1f,  0f, 0f)),
        };

        private static readonly Vector3[] _meshVertices =
        {
            new Vector3( 1f,  1f, 0f),
            new Vector3(-1f,  1f, 0f),
            new Vector3(-1f, -1f, 0f),
            new Vector3( 1f, -1f, 0f),
        };
        private static readonly int[] _meshIndices = { 0, 1, 2, 2, 3, 0 };

        public CubemapRenderer(int cubemapSize, int renderSize, Shader cubemapRendererShader)
        {
            _cubemapSize = cubemapSize;
            _renderSize  = Mathf.Clamp(renderSize, 1, cubemapSize);
            _shader   = cubemapRendererShader;
            _material = new Material(_shader);
            _mesh = new Mesh { vertices = _meshVertices, triangles = _meshIndices };

            _cubemap           = new RenderTexture(cubemapSize, cubemapSize, 0, RenderTextureFormat.ARGB32);
            _cubemap.dimension = TextureDimension.Cube;
            // Unity のキューブマップは 2 の冪でないと Create に失敗し、出力が無言で真っ黒になる
            if (!_cubemap.Create())
                Log.Error($"Failed to create a {cubemapSize}px cubemap render texture; output will be black.");
            // コマンドバッファは _tempRT 確定後 (RebuildCommandBuffers) に作成する
        }

        public void Dispose()
        {
            DisposeCommandBuffers();
            if (_material != null) { UnityEngine.Object.Destroy(_material); _material = null; }
            if (_mesh     != null) { UnityEngine.Object.Destroy(_mesh);     _mesh     = null; }
            if (_tempRT   != null) { UnityEngine.Object.Destroy(_tempRT);   _tempRT   = null; }
            if (_cubemap  != null) { UnityEngine.Object.Destroy(_cubemap);  _cubemap  = null; }
        }

        private void DisposeCommandBuffers()
        {
            if (_commandBuffers == null) return;
            foreach (var c in _commandBuffers) c.Dispose();
            _commandBuffers = null;
        }

        /// <summary>
        /// _tempRT を直接参照するコマンドバッファを構築する。
        /// BuiltinRenderTextureType.CameraTarget の代わりに使うことで VR クラッシュを回避する。
        /// </summary>
        private void RebuildCommandBuffers()
        {
            DisposeCommandBuffers();
            var tid = Shader.PropertyToID("_MainTex");
            _commandBuffers = new CommandBuffer[_faces.Length];
            for (int i = 0; i < _faces.Length; i++)
            {
                var cb = new CommandBuffer();
                // _tempRT を直接指定 (VR 共有テクスチャへの誤解決を防ぐ)
                cb.SetGlobalTexture(tid, _tempRT);
                cb.SetRenderTarget(_cubemap, 0, _faces[i].Face);
                cb.DrawMesh(_mesh!, Matrix4x4.identity, _material!, 0, 0);
                _commandBuffers[i] = cb;
            }
        }

        /// <summary>
        /// 前方半球 (+Z 側) だけで足りる投影 (VR180 / 魚眼) のとき、各面のうち描画が必要な範囲。
        /// 面カメラのビュー空間で +Z がどちら側に来るかで決まる (ビューポート座標、y は下が 0)。
        ///   +X 面 (yaw  90): +Z は左   → 左半分
        ///   -X 面 (yaw -90): +Z は右   → 右半分
        ///   +Y 面 (上向き) : +Z は下   → 下半分
        ///   -Y 面 (下向き) : +Z は上   → 上半分
        /// 残り半分は出力から参照されないので描かない。4 面が半分になるので画素数は 5 面 → 3 面分。
        /// 境界 (ちょうど 90 度) でバイリニア補間が隣の texel を拾うため少しだけ余分に描く。
        /// </summary>
        private Rect FrontHemisphereRect(CubemapFace face)
        {
            float m = Mathf.Min(0.5f, 4f / _renderSize);
            float h = 0.5f + m;
            switch (face)
            {
                case CubemapFace.PositiveX: return new Rect(0f,     0f,     h,  1f);
                case CubemapFace.NegativeX: return new Rect(1f - h, 0f,     h,  1f);
                case CubemapFace.PositiveY: return new Rect(0f,     0f,     1f, h);
                case CubemapFace.NegativeY: return new Rect(0f,     1f - h, 1f, h);
                default:                    return new Rect(0f,     0f,     1f, 1f);
            }
        }

        public void RenderCubemap(Camera camera, int faceMask, float ipdOffset, GammaConvertType gammaConvert, bool correctPosition,
                                  bool frontHemisphereOnly = false)
        {
            // _tempRT のフォーマットが変わった場合は再生成
            if (_tempRT != null)
            {
                var expected = camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
                if (_tempRT.format != expected) { UnityEngine.Object.Destroy(_tempRT); _tempRT = null; }
            }
            if (_tempRT == null)
            {
                _tempRT = new RenderTexture(_renderSize, _renderSize, 24,
                    camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                _tempRT.dimension = TextureDimension.Tex2D;
                _tempRT.Create();
                // _tempRT が新規作成/変更されたのでコマンドバッファを再構築
                RebuildCommandBuffers();
            }

            switch (gammaConvert)
            {
                case GammaConvertType.Linear_to_sRGB:  SetKeyword("LINEAR_TO_SRGB", true);  SetKeyword("LINEAR_TO_BT709", false); break;
                case GammaConvertType.Linear_to_BT709: SetKeyword("LINEAR_TO_SRGB", false); SetKeyword("LINEAR_TO_BT709", true);  break;
                default:                               SetKeyword("LINEAR_TO_SRGB", false); SetKeyword("LINEAR_TO_BT709", false); break;
            }

            var orgRot    = camera.transform.localRotation;
            var orgPos    = camera.transform.localPosition;
            var orgTarget = camera.targetTexture;
            var orgOrtho  = camera.orthographic;
            var orgAspect = camera.aspect;
            var orgFov    = camera.fieldOfView;

            camera.orthographic  = false;
            camera.aspect        = 1.0f;
            camera.fieldOfView   = 90.0f;
            camera.targetTexture = _tempRT;

            try
            {
                for (int i = 0; i < _commandBuffers!.Length; i++)
                {
                    if ((faceMask & (1 << (int)_faces[i].Face)) == 0) continue;
                    var rect = frontHemisphereOnly ? FrontHemisphereRect(_faces[i].Face) : new Rect(0f, 0f, 1f, 1f);
                    bool partial = rect.width < 1f || rect.height < 1f;
                    RenderTexture? partialRT = null;
                    try
                    {
                        camera.transform.localRotation = orgRot * _faces[i].Rotate;
                        camera.transform.localPosition = orgPos +
                            (correctPosition ? _faces[i].PositionShift * ipdOffset : Vector3.right * ipdOffset);
                        if (partial)
                        {
                            // camera.rect は使わない。Deferred の照明パスがビューポートを正しく扱えず、
                            // rect で絞った面ではライトで照らされる物が真っ黒になる
                            // (ローカルホームは無照明のグリッドだけなので気づかず、宿屋のワールドで判明)。
                            // Renderite 自身の CameraController と同じく、部分サイズの RT に全面で描いてから
                            // _tempRT の該当位置へコピーする。
                            int px = Mathf.RoundToInt(rect.x * _renderSize);
                            int py = Mathf.RoundToInt(rect.y * _renderSize);
                            int pw = Mathf.Clamp(Mathf.RoundToInt(rect.width  * _renderSize), 1, _renderSize - px);
                            int ph = Mathf.Clamp(Mathf.RoundToInt(rect.height * _renderSize), 1, _renderSize - py);
                            partialRT = RenderTexture.GetTemporary(pw, ph, 24, _tempRT.format);
                            camera.targetTexture = partialRT;

                            // 90 度の視錐台のうち実際に描く画素範囲だけを切り出した非対称視錐台。
                            // カリングもこの行列で行われるので、見えない側のオブジェクトも描かれない。
                            float n = camera.nearClipPlane;
                            float x0 = (float)px / _renderSize, x1 = (float)(px + pw) / _renderSize;
                            float y0 = (float)py / _renderSize, y1 = (float)(py + ph) / _renderSize;
                            camera.projectionMatrix = Matrix4x4.Frustum(
                                -n + 2f * n * x0, -n + 2f * n * x1,
                                -n + 2f * n * y0, -n + 2f * n * y1,
                                n, camera.farClipPlane);
                            camera.Render();
                            Graphics.CopyTexture(partialRT, 0, 0, 0, 0, pw, ph, _tempRT, 0, 0, px, py);
                        }
                        else
                        {
                            camera.targetTexture = _tempRT;
                            camera.Render();
                        }
                        // _tempRT をキューブマップの面へ書き込む
                        Graphics.ExecuteCommandBuffer(_commandBuffers[i]);
                    }
                    finally
                    {
                        if (partial) camera.ResetProjectionMatrix();
                        if (partialRT != null)
                        {
                            camera.targetTexture = _tempRT;
                            RenderTexture.ReleaseTemporary(partialRT);
                        }
                    }
                }
            }
            finally
            {
                camera.transform.localRotation = orgRot;
                camera.transform.localPosition = orgPos;
                camera.targetTexture           = orgTarget;
                camera.orthographic            = orgOrtho;
                camera.aspect                  = orgAspect;
                camera.fieldOfView             = orgFov;
            }
        }

        public RenderTexture? Cubemap => _cubemap;

        private void SetKeyword(string keyword, bool flag)
        {
            if (_material == null) return;
            if (flag) _material.EnableKeyword(keyword); else _material.DisableKeyword(keyword);
        }
    }
}
