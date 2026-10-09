using BepInEx.Logging;

namespace Reso360Spout2Renderer
{
    /// <summary>
    /// レンダラー側のログ出力。
    ///
    /// <para>
    /// <c>UnityEngine.Debug.Log</c> は Unity の <c>Player.log</c> にしか出ず、
    /// <c>Renderer/BepInEx/LogOutput.log</c> には流れてこない。
    /// BepInEx 5 の <c>UnityLogListening</c> は true でも、Resonite が使う
    /// Unity 2019.4 ビルドではログコールバックのフックに失敗して何も転送されない。
    /// </para>
    /// <para>
    /// README のトラブルシューティングが参照するのは LogOutput.log なので、
    /// BepInEx の <see cref="ManualLogSource"/> を直接使って確実にそちらへ出す。
    /// (BepInEx はコンソール出力も行うため Player.log にも残る)
    /// </para>
    /// </summary>
    internal static class Log
    {
        private static readonly ManualLogSource Source =
            Logger.CreateLogSource("Reso360Spout2");

        internal static void Info(string message)    => Source.LogInfo(message);
        internal static void Warning(string message) => Source.LogWarning(message);
        internal static void Error(string message)   => Source.LogError(message);
    }
}
