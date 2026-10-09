using BepInEx.Logging;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Reso360Spout2Patcher
{
    /// <summary>
    /// BepInEx 5 プリローダーパッチャー。
    /// Unity がネイティブプラグインを読み込む前に KlakSpout_send.dll を
    /// Renderite.Renderer_Data/Plugins/x86_64/ にコピーする。
    /// これにより Unity が UnityPluginLoad を呼び出して D3D11 デバイスを登録する。
    ///
    /// 注意: BepInEx 5 はパッチャーを「public static な TargetDLLs プロパティ」と
    /// 「public static void Patch(ref AssemblyDefinition)」の両方を持つ型としてのみ認識する。
    /// Patch がないと型ごと無視され Initialize() すら呼ばれないため、
    /// 何もしない Patch を必ず残しておくこと。
    /// </summary>
    public static class Patcher
    {
        private static readonly ManualLogSource Log =
            Logger.CreateLogSource("Reso360Spout2.Patcher");

        // パッチ対象のアセンブリはない (ファイルコピーのみ)
        public static IEnumerable<string> TargetDLLs { get; } = Enumerable.Empty<string>();

        /// <summary>TargetDLLs が空なので呼ばれないが、BepInEx のパッチャー判定に必要。</summary>
        public static void Patch(ref AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            try
            {
                // パッチャー DLL と同じフォルダにある KlakSpout_send.dll
                var patcherDir = Path.GetDirectoryName(typeof(Patcher).Assembly.Location)!;
                var sourcePath = Path.Combine(patcherDir, "KlakSpout_send.dll");

                if (!File.Exists(sourcePath))
                {
                    Log.LogError($"KlakSpout_send.dll not found at: {sourcePath}");
                    return;
                }

                var destDir = FindUnityPluginDir();
                if (destDir == null)
                {
                    Log.LogError("Unity native plugin folder (Renderite.Renderer_Data/Plugins/x86_64) not found.");
                    return;
                }

                var destPath = Path.Combine(destDir, "KlakSpout_send.dll");

                // 同一ファイルなら不要なコピーをスキップ
                if (File.Exists(destPath) &&
                    new FileInfo(sourcePath).Length == new FileInfo(destPath).Length &&
                    File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destPath))
                {
                    Log.LogInfo($"KlakSpout_send.dll is already up to date: {destPath}");
                    return;
                }

                File.Copy(sourcePath, destPath, overwrite: true);
                Log.LogInfo($"Copied KlakSpout_send.dll to: {destPath}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to deploy KlakSpout_send.dll: {ex}");
            }
        }

        /// <summary>
        /// Renderer プロセスの Unity ネイティブプラグインフォルダを探す。
        /// BepInEx.Paths / 実行ファイルのディレクトリ / AppDomain のベースを順に試す。
        /// </summary>
        private static string? FindUnityPluginDir()
        {
            foreach (var root in EnumerateRootCandidates())
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = Path.Combine(root, "Renderite.Renderer_Data", "Plugins", "x86_64");
                if (Directory.Exists(dir)) return dir;
            }
            return null;
        }

        private static IEnumerable<string?> EnumerateRootCandidates()
        {
            yield return BepInEx.Paths.GameRootPath;
            yield return Path.GetDirectoryName(BepInEx.Paths.ExecutablePath);
            yield return AppDomain.CurrentDomain.BaseDirectory;
            // patchers フォルダから 3 階層上が Renderer ルート
            // (Renderer/BepInEx/patchers/Reso360Spout2.Patcher → Renderer)
            var patcherDir = Path.GetDirectoryName(typeof(Patcher).Assembly.Location);
            if (patcherDir != null)
                yield return Path.GetFullPath(Path.Combine(patcherDir, "..", "..", ".."));
        }

        public static void Finish() { }
    }
}
