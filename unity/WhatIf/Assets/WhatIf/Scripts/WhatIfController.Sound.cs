using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using WhatIf.Api;
using WhatIf.Content;

namespace WhatIf
{
    /// <summary>
    /// Generated sounds: the scene's ambience plays quietly all around, an object's sound comes from
    /// the object (3D audio, at most a few copies so it doesn't become noise). Sounds arrive after the
    /// objects; a scene works without them (sounds off on the server, licence not accepted).
    /// </summary>
    public partial class WhatIfController
    {
        AudioSource _ambience;

        async Task PlaySounds(PlanResponse response, Dictionary<string, List<GeneratedObject>> byItem, int round)
        {
            if (!settings.sounds || response.Sounds == null || response.Sounds.Count == 0) return;
            var waits = new List<Task>();
            foreach (var kv in response.Sounds)
                waits.Add(PlayOne(kv.Key, kv.Value, byItem, round));
            await Task.WhenAll(waits);
        }

        async Task PlayOne(string key, SoundInfo info, Dictionary<string, List<GeneratedObject>> byItem, int round)
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            while (info.Status == "queued" && Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(1500);
                if (round != _round) return;
                try { info = await _client.GetSound(info.Id); }
                catch (Exception e) { Debug.LogWarning($"[WhatIf] sound {key}: {e.Message}"); }
            }
            if (info.Status != "done" || string.IsNullOrEmpty(info.Url)) return;
            AudioClip clip;
            try { clip = await _client.DownloadAudio(info.Url); }
            catch (Exception e) { Debug.LogWarning($"[WhatIf] sound {key}: {e.Message}"); return; }
            if (round != _round || clip == null) return;

            if (key == "ambience")
            {
                if (_ambience == null) _ambience = gameObject.AddComponent<AudioSource>();
                _ambience.clip = clip;
                _ambience.loop = true;
                _ambience.spatialBlend = 0f;
                _ambience.volume = settings.ambienceVolume;
                _ambience.Play();
                Debug.Log($"[WhatIf] ambience: {info.Prompt}");
                return;
            }
            if (!byItem.TryGetValue(key, out var objs)) return;
            // the nearest few copies carry the sound
            objs = new List<GeneratedObject>(objs);
            objs.RemoveAll(o => o == null);
            objs.Sort((a, b) => (a.transform.position - head.position).sqrMagnitude
                .CompareTo((b.transform.position - head.position).sqrMagnitude));
            for (int i = 0; i < objs.Count && i < 2; i++)
            {
                var src = objs[i].gameObject.AddComponent<AudioSource>();
                src.clip = clip;
                src.loop = info.Loop;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 0.5f;
                src.maxDistance = 8f;
                src.volume = settings.objectSoundVolume;
                src.time = UnityEngine.Random.Range(0f, Mathf.Max(0f, clip.length - 0.1f)); // copies not in sync
                src.Play();
            }
            Debug.Log($"[WhatIf] sound for {key}: {info.Prompt}");
        }

        void StopAmbience()
        {
            if (_ambience != null) _ambience.Stop();
        }
    }
}
