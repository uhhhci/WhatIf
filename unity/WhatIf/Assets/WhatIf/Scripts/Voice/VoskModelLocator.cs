using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using WhatIf.Api;

namespace WhatIf.Voice
{
    /// <summary>
    /// Finds the Vosk model on disk. In the Editor it is read straight from StreamingAssets.
    /// On Android (Magic Leap 2) StreamingAssets live inside the APK and can't be opened as files,
    /// so the model is copied to persistent storage once, using the files.txt list next to it.
    /// </summary>
    public static class VoskModelLocator
    {
        public static async Task<string> GetModelPath(string folder)
        {
            var source = Path.Combine(Application.streamingAssetsPath, folder);
#if UNITY_ANDROID && !UNITY_EDITOR
            var target = Path.Combine(Application.persistentDataPath, folder);
            var marker = Path.Combine(target, ".complete");
            if (File.Exists(marker)) return target;

            var list = await Download(source + "/files.txt");
            foreach (var line in System.Text.Encoding.UTF8.GetString(list).Split('\n'))
            {
                var rel = line.Trim();
                if (rel.Length == 0) continue;
                var bytes = await Download(source + "/" + rel);
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.WriteAllBytes(dst, bytes);
            }
            File.WriteAllText(marker, "ok");
            return target;
#else
            await Task.CompletedTask;
            return source;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static async Task<byte[]> Download(string url)
        {
            using var r = UnityWebRequest.Get(url);
            await r.SendAsync();
            if (r.result != UnityWebRequest.Result.Success)
                throw new IOException($"cannot read {url}: {r.error}");
            return r.downloadHandler.data;
        }
#endif
    }
}
