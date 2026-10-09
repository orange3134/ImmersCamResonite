using System.Collections;
using UnityEngine;

namespace Reso360Spout2Renderer
{
    public enum ProjectionType
    {
        Equirectangular_360 = 0,
        Equirectangular_180,
        FishEye_Circumference,
        FishEye_Diagonal
    }

    public enum FishEyeType { Equidistance, EquisolidAngle, Orthogonal }

    [RequireComponent(typeof(Camera))]
    public class CubemapToOtherProjection : MonoBehaviour
    {
        // 出力先 / 投影方式 / ステレオ配置が変わると、必要なキューブマップ解像度も変わる。
        // CUBEMAP_SIZE = Auto のときに再計算できるよう、プロパティにして変更を捕まえる。
        private RenderTexture? _renderTarget;
        private ProjectionType _projectionType = ProjectionType.Equirectangular_360;
        private bool _renderInStereo;

        public RenderTexture? RenderTarget
        {
            get => _renderTarget;
            set { if (_renderTarget != value) { _renderTarget = value; ApplyCubemapSizing(); } }
        }

        public ProjectionType ProjectionType
        {
            get => _projectionType;
            set { if (_projectionType != value) { _projectionType = value; ApplyCubemapSizing(); } }
        }

        public bool RenderInStereo
        {
            get => _renderInStereo;
            set { if (_renderInStereo != value) { _renderInStereo = value; ApplyCubemapSizing(); } }
        }

        /// <summary>実際に使用しているキューブマップ 1 面の解像度 (Auto の場合は計算後の値)。</summary>
        public int CubemapSize { get; private set; } = 1024;

        /// <summary>
        /// カメラが実際に描く 1 面の解像度。2 の冪である必要があるのはキューブマップだけなので、
        /// Auto では必要量ぎりぎりで描いてキューブマップへ拡大して貼る。明示指定時は CubemapSize と同じ。
        /// </summary>
        public int RenderSize { get; private set; } = 1024;

        public FishEyeType FishEyeType = FishEyeType.Equidistance;
        public bool UseUnityInternalCubemapRenderer = false;
        public GammaConvertType GammaConvertType = GammaConvertType.None;
        public float StereoSeparation = 0.065f;
        public bool CorrectCameraPositionInStereoRendering = false;
        public bool FlipVertically = false;

        // シェーダーはレンダラープラグインから設定される
        public Shader? CubemapShader;
        public Shader? CubemapRendererShader;

        private Camera _camera = null!;
        private Material? _material;
        private RenderTexture? _cubemap;
        private CubemapRenderer? _cubemapRenderer;
        private bool _started;
        // 現在の CubemapSize / RenderSize でキューブマップを作成済みか
        private bool _built;

        void Start()
        {
            _camera   = GetComponent<Camera>();
            _material = new Material(CubemapShader);
            _started  = true;

            // Start より前に設定された値で実効サイズを確定させる。
            // Auto のときは RenderTarget が決まるまで作らない (決まった時点で呼び直される)。
            ApplyCubemapSizing();
        }

        /// <summary>
        /// Unity のキューブマップ RenderTexture は「2 の冪」かつ正方形でなければ
        /// <c>RenderTexture.Create</c> が失敗し、出力が真っ黒になる。
        /// 設定値がどこから来ても黙って壊れないよう、2 の冪へ丸めておく。
        /// </summary>
        private const int MinCubemapSize = 64;
        private const int MaxCubemapSize = 8192;

        /// <summary>CUBEMAP_SIZE = Auto を表す値 (Host 側の enum と共有メモリでも 0)。</summary>
        public const int AutoCubemapSize = 0;

        /// Auto が勝手に VRAM を食い尽くさないための上限。
        /// 4096 の 6 面 ARGB32 でおよそ 400MB。8K 出力まではこれで足りる。
        private const int AutoMaxCubemapSize = 4096;
        private const int AutoMinCubemapSize = 512;

        /// <summary>
        /// キューブ 1 面は 90 度を N ピクセルで受け持つが、密度は面の中央が最も粗く
        /// <c>N / 114.59</c> px/度 になる (p = (N/2)·tanθ の θ=0 における微分)。
        /// 出力に必要な px/度 を下回らない N を選ぶための係数。
        /// </summary>
        private const float CubeFaceDegreesPerPixel = 114.59f;

        internal static int SanitizeCubemapSize(int size)
        {
            if (size < MinCubemapSize) size = MinCubemapSize;
            if (size > MaxCubemapSize) size = MaxCubemapSize;

            // 最も近い 2 の冪に丸める (下側の冪と上側の冪の近い方)
            int lower = MinCubemapSize;
            while (lower * 2 <= size) lower *= 2;
            if (lower == size) return size;
            int upper = lower * 2;
            if (upper > MaxCubemapSize) return lower;
            return (size - lower) < (upper - size) ? lower : upper;
        }

        // 設定された生の値。0 (Auto) なら出力解像度から毎回計算する。
        private int _requestedCubemapSize = 2048;

        /// <summary>
        /// キューブマップサイズを変更する。Start 前なら値を控えるだけで、
        /// Start 後ならキューブマップと CubemapRenderer を作り直す。
        /// (CubemapSize に代入するだけでは Start 済みのリソースには反映されない)
        /// </summary>
        public void SetCubemapSize(int size)
        {
            _requestedCubemapSize = size;
            ApplyCubemapSizing();
        }

        /// <summary>
        /// 実効キューブマップ解像度を決めて、変わっていれば作り直す。
        /// 出力解像度 / 投影方式 / ステレオ配置のいずれかが変わったときにも呼ばれる。
        /// </summary>
        private void ApplyCubemapSizing()
        {
            int effective, render;
            if (_requestedCubemapSize == AutoCubemapSize)
            {
                // 出力解像度が分からないうちは何も作らない。
                // Spout センダーの作り直し中は RenderTarget が一瞬 null になるので、
                // ここで作り直すと毎回 512px の無駄なキューブマップができてしまう。
                if (_renderTarget == null) return;
                render = ComputeAutoRenderSize();
                effective = AutoMinCubemapSize;
                while (effective < render) effective *= 2;
            }
            else
            {
                effective = SanitizeCubemapSize(_requestedCubemapSize);
                if (effective != _requestedCubemapSize)
                    Log.Warning($"CUBEMAP_SIZE {_requestedCubemapSize} is not a power of two; using {effective} instead.");
                render = effective;
            }

            if (effective == CubemapSize && render == RenderSize && _built) return;
            CubemapSize = effective;
            RenderSize  = render;
            if (_started) RebuildCubemap();
        }

        /// <summary>
        /// 出力 1 枚に必要なキューブマップ 1 面の解像度を求める。
        ///
        /// ステレオでは 1 枚のフレームに両目が入る (180 系は左右、360 は上下) ので、
        /// 片目あたりの画素数と、その目が受け持つ画角から必要な px/度 を出し、
        /// キューブ面の最も粗い部分 (面の中央) がそれを下回らないサイズを選ぶ。
        ///
        /// 既定の 6144x3072 VR180 では 2048 (= High) になり、これまでの既定と一致する。
        /// 8192x4096 では 2816 (キューブマップ自体は 4096)。
        /// </summary>
        private int ComputeAutoRenderSize()
        {
            float w = _renderTarget!.width;
            float h = _renderTarget.height;

            if (RenderInStereo)
            {
                if (ProjectionType == ProjectionType.Equirectangular_360) h *= 0.5f;  // 上下配置
                else                                                      w *= 0.5f;  // 左右配置
            }

            float pxPerDeg;
            switch (ProjectionType)
            {
                case ProjectionType.Equirectangular_360:
                    pxPerDeg = Mathf.Max(w / 360f, h / 180f);
                    break;
                case ProjectionType.FishEye_Diagonal:
                    // 180 度の円が対角に収まるので、対角の長さが 180 度分になる
                    pxPerDeg = Mathf.Sqrt(w * w + h * h) / 180f;
                    break;
                default: // Equirectangular_180 / FishEye_Circumference
                    pxPerDeg = Mathf.Max(w / 180f, h / 180f);
                    break;
            }

            int needed = Mathf.CeilToInt(pxPerDeg * CubeFaceDegreesPerPixel);

            // 描画解像度は必要量を AutoRenderStep 単位で切り上げるだけにする。
            // 以前は 2 の冪まで切り上げており、8192x4096 では必要 2608 に対して 4096 を
            // 描いていた (画素数 2.5 倍)。負荷はほぼこの描画なので fps に直結する。
            int size = (needed + AutoRenderStep - 1) / AutoRenderStep * AutoRenderStep;
            return Mathf.Clamp(size, AutoMinCubemapSize, AutoMaxCubemapSize);
        }

        private const int AutoRenderStep = 256;

        private void RebuildCubemap()
        {
            if (_cubemap != null) { Destroy(_cubemap); _cubemap = null; }
            _cubemapRenderer?.Dispose();
            _cubemapRenderer = null;
            _built = true;

            if (UseUnityInternalCubemapRenderer)
            {
                // RenderToCubemap 用。CubemapRenderer は自前のキューブマップを持つので、
                // そちらを使うときは作らない (4096 だと深度込みで約 800MB を無駄に確保してしまう)。
                _cubemap           = new RenderTexture(CubemapSize, CubemapSize, 24, RenderTextureFormat.ARGB32);
                _cubemap.dimension = UnityEngine.Rendering.TextureDimension.Cube;
                if (!_cubemap.Create())
                {
                    Log.Error($"Failed to create a {CubemapSize}px cubemap render texture; output would be black.");
                    Destroy(_cubemap);
                    _cubemap = null;
                    return;
                }
            }
            else if (CubemapRendererShader != null)
            {
                _cubemapRenderer = new CubemapRenderer(CubemapSize, RenderSize, CubemapRendererShader);
            }

            string how = _requestedCubemapSize == AutoCubemapSize ? " (auto)" : "";
            string render = RenderSize != CubemapSize ? $", faces rendered at {RenderSize}px" : "";
            Log.Info($"Cubemap rebuilt at {CubemapSize}px{how}{render}.");
        }

        public void Rendering()
        {
            if (RenderTarget == null) return;
            if (UseUnityInternalCubemapRenderer ? _cubemap == null : _cubemapRenderer == null) return;

            switch (ProjectionType)
            {
                case ProjectionType.Equirectangular_360:
                    SetUV(Mathf.PI * 2f, Mathf.PI, 0f, 0f);
                    SetKeyword("PROJ_FISHEYE", false); SetKeyword("ANGLEFUNC_EQUISOLIDANGLE", false); SetKeyword("ANGLEFUNC_ORTHGONAL", false);
                    break;
                case ProjectionType.Equirectangular_180:
                    SetUV(Mathf.PI, Mathf.PI, Mathf.PI * 0.5f, 0f);
                    SetKeyword("PROJ_FISHEYE", false); SetKeyword("ANGLEFUNC_EQUISOLIDANGLE", false); SetKeyword("ANGLEFUNC_ORTHGONAL", false);
                    break;
                case ProjectionType.FishEye_Circumference:
                    SetUV(2f, 2f, -1f, -1f); SetKeyword("PROJ_FISHEYE", true); SetFishEyeScale(1.0f);
                    break;
                case ProjectionType.FishEye_Diagonal:
                    SetUV(2f, 2f, -1f, -1f); SetKeyword("PROJ_FISHEYE", true); SetFishEyeScale(1f / Mathf.Sqrt(2));
                    break;
            }

            switch (FishEyeType)
            {
                case FishEyeType.Equidistance:   SetKeyword("ANGLEFUNC_EQUISOLIDANGLE", false); SetKeyword("ANGLEFUNC_ORTHGONAL", false); break;
                case FishEyeType.EquisolidAngle: SetKeyword("ANGLEFUNC_EQUISOLIDANGLE", true);  SetKeyword("ANGLEFUNC_ORTHGONAL", false); break;
                case FishEyeType.Orthogonal:     SetKeyword("ANGLEFUNC_EQUISOLIDANGLE", false); SetKeyword("ANGLEFUNC_ORTHGONAL", true);  break;
            }

            if (RenderInStereo)
            {
                var tmpSep = _camera.stereoSeparation;
                var tmpEye = _camera.stereoTargetEye;
                _camera.stereoSeparation = StereoSeparation;
                _camera.stereoTargetEye  = StereoTargetEyeMask.None;

                if (ProjectionType == ProjectionType.Equirectangular_360)
                {
                    Render(Camera.MonoOrStereoscopicEye.Left,  1f, 0.5f,  0f, -0.5f);
                    Render(Camera.MonoOrStereoscopicEye.Right, 1f, 0.5f,  0f,  0.5f);
                }
                else
                {
                    Render(Camera.MonoOrStereoscopicEye.Left,  0.5f, 1f, -0.5f, 0f);
                    Render(Camera.MonoOrStereoscopicEye.Right, 0.5f, 1f,  0.5f, 0f);
                }

                _camera.stereoSeparation = tmpSep;
                _camera.stereoTargetEye  = tmpEye;
            }
            else
            {
                Render(Camera.MonoOrStereoscopicEye.Mono, 1f, 1f, 0f, 0f);
            }
        }

        void OnDestroy()
        {
            if (_material != null) { Destroy(_material); _material = null; }
            if (_cubemap  != null) { Destroy(_cubemap);  _cubemap  = null; }
            _cubemapRenderer?.Dispose(); _cubemapRenderer = null;
        }

        void Render(Camera.MonoOrStereoscopicEye eye, float sx, float sy, float ox, float oy)
        {
            if (UseUnityInternalCubemapRenderer)
            {
                ApplyGamma(); _camera.RenderToCubemap(_cubemap, 63, eye);
                SetPositionScale(sx, sy, ox, oy);
                _material!.SetMatrix("_Matrix", Matrix4x4.Rotate(Quaternion.identity));
                Graphics.Blit(_cubemap, RenderTarget, _material);
            }
            else
            {
                float ipd = eye == Camera.MonoOrStereoscopicEye.Left  ? -StereoSeparation / 2f
                          : eye == Camera.MonoOrStereoscopicEye.Right ?  StereoSeparation / 2f : 0f;
                // 360 以外 (VR180 / 魚眼) は前方半球しか使わないので、背面は描かず側面は半分だけ描く
                bool full = ProjectionType == ProjectionType.Equirectangular_360;
                _cubemapRenderer!.RenderCubemap(
                    _camera,
                    full ? 63 : 63 - (1 << (int)CubemapFace.NegativeZ),
                    ipd, GammaConvertType, CorrectCameraPositionInStereoRendering,
                    frontHemisphereOnly: !full);
                SetPositionScale(sx, sy, ox, oy);
                _material!.SetMatrix("_Matrix", Matrix4x4.identity);
                SetKeyword("LINEAR_TO_SRGB", false); SetKeyword("LINEAR_TO_BT709", false);
                Graphics.Blit(_cubemapRenderer.Cubemap, RenderTarget, _material);
            }
        }

        void ApplyGamma()
        {
            switch (GammaConvertType)
            {
                case GammaConvertType.Linear_to_sRGB:  SetKeyword("LINEAR_TO_SRGB", true);  SetKeyword("LINEAR_TO_BT709", false); break;
                case GammaConvertType.Linear_to_BT709: SetKeyword("LINEAR_TO_SRGB", false); SetKeyword("LINEAR_TO_BT709", true);  break;
                default:                               SetKeyword("LINEAR_TO_SRGB", false); SetKeyword("LINEAR_TO_BT709", false); break;
            }
        }

        // Spout 側は上下が逆なので、投影パスの時点で反転して書き込む
        // (以前は出力解像度の一時 RT へもう 1 回 Blit して反転しており、8K だとその分が重かった)。
        // 四角形の縦スケールを負にすると裏向きになってカリングされ真っ黒になるので、
        // 中身は UV→角度の対応 (v を 1-v に) で反転し、四角形は目の配置 (oy) だけ入れ替える。
        void SetUV(float sx, float sy, float ox, float oy)
            => _material?.SetVector("_UVScaleOffset",
                   FlipVertically ? new Vector4(sx, -sy, ox, oy + sy) : new Vector4(sx, sy, ox, oy));
        void SetPositionScale(float sx, float sy, float ox, float oy)
            => _material?.SetVector("_PositionScaleOffset",
                   new Vector4(sx, sy, ox, FlipVertically ? -oy : oy));
        void SetFishEyeScale(float s)
            => _material?.SetFloat("_FishEyeDiameterScale", s);
        void SetKeyword(string keyword, bool flag)
        {
            if (_material == null) return;
            if (flag) _material.EnableKeyword(keyword); else _material.DisableKeyword(keyword);
        }
    }
}
