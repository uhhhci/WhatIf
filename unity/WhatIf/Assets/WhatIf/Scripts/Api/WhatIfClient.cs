using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace WhatIf.Api
{
    /// <summary>Thin async wrapper around the generation server's HTTP API.</summary>
    public class WhatIfClient
    {
        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        readonly string _baseUrl;
        readonly int _timeoutS;

        public WhatIfClient(string baseUrl, int timeoutS = 120)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _timeoutS = timeoutS;
        }

        public string Absolute(string path) => path.StartsWith("http") ? path : _baseUrl + path;

        public string BaseUrl => _baseUrl;

        /// <summary>Quick status check (short timeout, so an unreachable server is noticed fast).</summary>
        public async Task<HealthInfo> Health()
        {
            using var r = UnityWebRequest.Get(_baseUrl + "/health");
            r.timeout = 4;
            await r.SendAsync();
            Check(r);
            return JsonConvert.DeserializeObject<HealthInfo>(r.downloadHandler.text, Json);
        }

        public Task<PlanResponse> Plan(PlanRequest req) => Post<PlanResponse>("/plan", req);

        /// <summary>
        /// /plan/stream: `onEvent` gets each object as soon as the planner has written it, then the whole
        /// answer (on the main thread). False if the server has no streaming (older server): use Plan.
        /// </summary>
        public async Task<bool> PlanStream(PlanRequest req, Action<PlanStreamEvent> onEvent)
        {
            var bytes = await Task.Run(() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(req, Json)));
            var lines = new LineHandler(line =>
            {
                PlanStreamEvent ev;
                try { ev = JsonConvert.DeserializeObject<PlanStreamEvent>(line, Json); }
                catch (Exception e) { Debug.LogWarning("[WhatIf] plan stream: unreadable line: " + e.Message); return; }
                if (ev == null) return;
                try { onEvent(ev); } catch (Exception e) { Debug.LogException(e); } // one bad object must not end the stream
            });
            using var r = new UnityWebRequest(_baseUrl + "/plan/stream", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = lines,
                timeout = _timeoutS,
            };
            r.SetRequestHeader("Content-Type", "application/json");
            lines.Owner = r;
            await r.SendAsync();
            if (r.responseCode == 404 || r.responseCode == 405) return false;
            if (r.result != UnityWebRequest.Result.Success)
                throw new Exception($"POST {r.url} failed: {r.responseCode} {r.error}");
            lines.Flush();
            return true;
        }

        /// <summary>Hands on each complete line of a streamed answer as it arrives.</summary>
        sealed class LineHandler : DownloadHandlerScript
        {
            readonly Action<string> _onLine;
            readonly System.Collections.Generic.List<byte> _buf = new System.Collections.Generic.List<byte>();
            public UnityWebRequest Owner;

            public LineHandler(Action<string> onLine) : base(new byte[16 * 1024]) { _onLine = onLine; }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                // an error page (404 of an older server) is not a stream of events
                if (Owner != null && Owner.responseCode >= 400) return true;
                for (int i = 0; i < dataLength; i++)
                {
                    if (data[i] != (byte)'\n') { _buf.Add(data[i]); continue; }
                    Emit();
                }
                return true;
            }

            public void Flush() { if (_buf.Count > 0) Emit(); }

            void Emit()
            {
                var line = Encoding.UTF8.GetString(_buf.ToArray()).Trim();
                _buf.Clear();
                if (line.Length > 0) _onLine(line);
            }
        }

        public Task<AssetInfo> GetAsset(string id) => Get<AssetInfo>("/assets/" + id);

        /// <summary>Speech-to-text on the server (local Whisper). Body: 16-bit PCM WAV.</summary>
        public Task<EditResponse> Edit(EditRequest req) => Post<EditResponse>("/edit", req);

        public async Task<string> Transcribe(byte[] wav, string language = null) =>
            (await TranscribeFull(wav, language)).Text ?? "";

        /// <summary>Speech-to-text with the server's verdict whether it was speech at all.</summary>
        /// <summary>Words of the current scene (scenario, object names) that speech recognition should
        /// expect ("Goethe" instead of "Güter").</summary>
        public Func<string> TranscribeContext;

        public async Task<TranscribeResult> TranscribeFull(byte[] wav, string language = null)
        {
            var url = _baseUrl + "/transcribe" + (string.IsNullOrEmpty(language) ? "" : "?language=" + language);
            using var r = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(wav),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutS,
            };
            r.SetRequestHeader("Content-Type", "audio/wav");
            var context = TranscribeContext?.Invoke();
            if (!string.IsNullOrWhiteSpace(context))
                r.SetRequestHeader("X-WhatIf-Context", Uri.EscapeDataString(context.Length > 300 ? context.Substring(0, 300) : context));
            await r.SendAsync();
            Check(r);
            return JsonConvert.DeserializeObject<TranscribeResult>(r.downloadHandler.text, Json) ?? new TranscribeResult { Text = "" };
        }



        public Task<SoundInfo> GetSound(string id) => Get<SoundInfo>("/sounds/" + id);

        public async Task<AudioClip> DownloadAudio(string url)
        {
            using var r = UnityWebRequestMultimedia.GetAudioClip(Absolute(url), AudioType.WAV);
            r.timeout = _timeoutS;
            await r.SendAsync();
            Check(r);
            return DownloadHandlerAudioClip.GetContent(r);
        }

        public async Task<Texture2D> DownloadTexture(string url)
        {
            using var r = UnityWebRequestTexture.GetTexture(Absolute(url), nonReadable: true);
            r.timeout = _timeoutS;
            await r.SendAsync();
            Check(r);
            var tex = DownloadHandlerTexture.GetContent(r);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        async Task<T> Get<T>(string path)
        {
            using var r = UnityWebRequest.Get(_baseUrl + path);
            r.timeout = _timeoutS;
            await r.SendAsync();
            Check(r);
            var text = r.downloadHandler.text;
            return await Task.Run(() => JsonConvert.DeserializeObject<T>(text, Json));
        }

        async Task<T> Post<T>(string path, object body)
        {
            // building and reading the JSON (with a camera picture inside) on a worker thread: on the
            // main thread it was part of the hiccup after every sentence
            var bytes = await Task.Run(() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body, Json)));
            using var r = new UnityWebRequest(_baseUrl + path, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutS,
            };
            r.SetRequestHeader("Content-Type", "application/json");
            await r.SendAsync();
            Check(r);
            var answer = r.downloadHandler.text;
            return await Task.Run(() => JsonConvert.DeserializeObject<T>(answer, Json));
        }

        static void Check(UnityWebRequest r)
        {
            if (r.result == UnityWebRequest.Result.Success) return;
            var detail = r.downloadHandler != null && !(r.downloadHandler is DownloadHandlerTexture)
                ? r.downloadHandler.text : "";
            throw new Exception($"{r.method} {r.url} failed: {r.responseCode} {r.error} {detail}");
        }
    }

    static class UnityWebRequestExtensions
    {
        /// <summary>Awaitable SendWebRequest; continuation runs on the main thread.</summary>
        public static Task SendAsync(this UnityWebRequest r)
        {
            var tcs = new TaskCompletionSource<bool>();
            r.SendWebRequest().completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
