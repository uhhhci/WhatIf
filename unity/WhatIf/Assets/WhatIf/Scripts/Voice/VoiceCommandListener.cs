using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using Vosk;

namespace WhatIf.Voice
{
    /// <summary>
    /// Hands-free prompts: "What if this place had more plants?"
    ///
    /// 1. The microphone runs continuously, but audio stays on the device: an offline Vosk recogniser
    ///    with a tiny grammar ("what if" vs. anything else) only listens for the keyword.
    /// 2. After the keyword, the sentence is recorded until a pause (keeping the audio just before
    ///    the keyword, so "What if" itself is included).
    /// 3. Only that recording is sent to the server, which transcribes it with Whisper (local PC).
    /// </summary>
    public class VoiceCommandListener : MonoBehaviour
    {
        public enum State { Starting, Unavailable, Waiting, Recording, Transcribing }

        const int Rate = 16000;
        // The recording starts this long before the spoken "what" (Vosk word timing). Detection
        // itself can come 1.5 s or more after the keyword: with a two-phrase grammar the recogniser
        // often confirms "what if" only after hearing the next words.
        const float LeadInS = 0.3f;
        const int KwsBatch = Rate / 10; // feed the spotter 0.1 s at a time; tiny chunks made it lag
        const float FrameS = 0.02f;    // VAD frame

        public WhatIfSettings settings;

        /// <summary>Raised on the main thread when a prompt was recorded and transcribed.</summary>
        public event Action<string> PromptHeard;
        /// <summary>Continuous mode: every spoken sentence (text, start, end in realtimeSinceStartup).</summary>
        public event Action<string, float, float> UtteranceHeard;
        public event Action<State, string> StateChanged;
        /// <summary>Speech began (a sentence is being recorded): the camera picture can be taken now,
        /// not only after the sentence has been transcribed.</summary>
        public event Action SpeechStarted;

        public State Current { get; private set; } = State.Starting;

        string _device;
        AudioClip _clip;
        int _readPos;
#if UNITY_ANDROID && !UNITY_EDITOR
        UnityEngine.XR.MagicLeap.MLAudioInput.BufferClip _mlClip;
        readonly ConcurrentQueue<float[]> _mlSamples = new ConcurrentQueue<float[]>(); // filled on the audio thread
#endif

        // Last seconds of audio, so the recording can start before the keyword was detected.
        readonly short[] _ring = new short[Rate * 10];
        long _written; // total samples written to the ring

        readonly List<short> _utterance = new List<short>();
        float _utteranceStart, _lastVoiceAt, _noiseFloor = 0.01f;

        // Keyword spotting runs on a worker thread; audio chunks are queued to it together with
        // the ring position of their last sample.
        readonly ConcurrentQueue<(short[] pcm, long end)> _kwsQueue = new ConcurrentQueue<(short[], long)>();
        Thread _kwsThread;
        volatile bool _running, _keywordHeard, _resetKws;
        long _keywordStart;       // ring position (samples) where the spoken keyword began
        volatile string _keywordLanguage = "en"; // which keyword was heard: the sentence's language

        /// <summary>One offline keyword spotter per language; each listens only for its own phrase.</summary>
        sealed class Spotter
        {
            public string Language;   // hint for the server's transcription
            public string Grammar;    // Vosk grammar: the keyword or anything else
            public string[] Words;    // the keyword as consecutive words ("*" = any word)
            public Model Model;
        }

        readonly List<Spotter> _spotters = new List<Spotter>();

        Func<byte[], string, System.Threading.Tasks.Task<Api.TranscribeResult>> _transcribe;

        // Continuous mode: speech detected by energy, no keyword spotting.
        bool Continuous => settings.continuousListening;
        int _voicedRun;                  // voiced VAD frames of the current speech start while waiting
        int _gapFrames;                  // unvoiced frames since the last voiced one
        long _speechStartSample;         // ring position where the current speech began
        volatile bool _speechTriggered;
        int _voicedFramesInUtterance;
        double _levelSum; int _levelFrames;  // loudness of the voiced frames of the current utterance
        float _wearerDb = float.NaN;         // the wearer's usual level, learnt from "What if" sentences
        float _utteranceSpeechStart;     // realtime the speech began (for gaze matching)

        public void Init(WhatIfSettings s, Func<byte[], string, System.Threading.Tasks.Task<Api.TranscribeResult>> transcribe)
        {
            settings = s;
            _transcribe = transcribe;
        }

        async void Start()
        {
            try
            {
                if (!await RequestMicrophone()) { SetState(State.Unavailable, "No microphone permission"); return; }

                if (Continuous)
                {
                    // Everything is transcribed (on the server): no keyword models needed.
                    if (!OpenMicrophone()) { SetState(State.Unavailable, "No microphone found"); return; }
                    _running = true;
                    SetState(State.Waiting, null);
                    return;
                }

                Vosk.Vosk.SetLogLevel(-1);
                await AddSpotter("en", settings.voskModelFolder, "[\"what if\", \"[unk]\"]", "what", "if");
                // "Was wäre wenn": the umlaut as a JSON escape, so no text encoding can break it on the
                // way in; the middle word matches anything, so none can break it on the way out.
                if (settings.germanKeyword)
                    await AddSpotter("de", settings.voskGermanModelFolder, "[\"was w\\u00e4re wenn\", \"[unk]\"]", "was", "*", "wenn");

                if (_spotters.Count == 0) { SetState(State.Unavailable, "No speech model found (run unity/setup_vosk.ps1)"); return; }

                if (!OpenMicrophone()) { SetState(State.Unavailable, "No microphone found"); return; }
                _running = true;
                _kwsThread = new Thread(KeywordLoop) { IsBackground = true, Name = "vosk-kws" };
                _kwsThread.Start();
                SetState(State.Waiting, null);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetState(State.Unavailable, "Voice input failed: " + e.Message);
            }
        }

        async System.Threading.Tasks.Task AddSpotter(string language, string folder, string grammar, params string[] words)
        {
            if (string.IsNullOrEmpty(folder)) return;
            var path = await VoskModelLocator.GetModelPath(folder);
            var model = await System.Threading.Tasks.Task.Run(() => new Model(path));
            _spotters.Add(new Spotter { Language = language, Grammar = grammar, Words = words, Model = model });
        }

        static async System.Threading.Tasks.Task<bool> RequestMicrophone()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string perm = UnityEngine.Android.Permission.Microphone;
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm)) return true;
            // WhatIfController requests all permissions together (Android shows one dialog at a
            // time; separate requests can swallow each other). Wait up to a minute for the answer.
            for (int i = 0; i < 600; i++)
            {
                await System.Threading.Tasks.Task.Delay(100);
                if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm)) return true;
            }
            return false;
#else
            await System.Threading.Tasks.Task.CompletedTask;
            return true;
#endif
        }

        void Update()
        {
            if (!_running) return;
            ReadMicrophone();

            if (Current == State.Waiting && Continuous && _speechTriggered)
            {
                _speechTriggered = false;
                // 2 s before the detected start: a softly spoken "Was wäre, wenn" before the louder rest
                // stays in (1 s cut it off again); Whisper ignores the silence
                BeginRecordingAt(_speechStartSample - (long)(2.0f * Rate));
            }
            else if (Current == State.Waiting && _keywordHeard)
            {
                _keywordHeard = false;
                BeginRecording();
            }
            else if (Current == State.Recording)
            {
                float now = Time.realtimeSinceStartup;
                bool pause = now - _lastVoiceAt > settings.voiceEndSilenceS && now - _utteranceStart > 1.2f;
                if (pause || now - _utteranceStart > settings.voiceMaxSeconds) FinishRecording();
            }
        }

        /// <summary>
        /// Magic Leap 2: the VoiceComm capture (the microphone array focused on the wearer's mouth),
        /// so other people's voices are much quieter; elsewhere (Editor) Unity's default microphone.
        /// </summary>
        bool OpenMicrophone()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                try
                {
                    var type = UnityEngine.XR.MagicLeap.MLAudioInput.MicCaptureType.VoiceComm;
                    int rate = UnityEngine.XR.MagicLeap.MLAudioInput.GetSampleRate(type);
                    if (rate == Rate)
                    {
                        _mlClip = new UnityEngine.XR.MagicLeap.MLAudioInput.BufferClip(type, 10, Rate);
                        _mlClip.OnReceivedSamples += samples => _mlSamples.Enqueue((float[])samples.Clone());
                        Debug.Log("[WhatIf] microphone: Magic Leap VoiceComm (focused on the wearer)");
                        return true;
                    }
                    Debug.LogWarning($"[WhatIf] microphone: VoiceComm runs at {rate} Hz, using the default microphone");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[WhatIf] microphone: VoiceComm failed (" + e.Message + "), using the default microphone");
                    _mlClip = null;
                }
            }
#endif
            if (Microphone.devices.Length == 0) return false;
            _device = Microphone.devices[0];
            _clip = Microphone.Start(_device, true, 10, Rate);
            Debug.Log("[WhatIf] microphone: " + _device);
            return true;
        }

        void ReadMicrophone()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_mlClip != null)
            {
                while (_mlSamples.TryDequeue(out var chunk)) ProcessSamples(chunk);
                return;
            }
#endif
            if (_clip == null) return;
            int pos = Microphone.GetPosition(_device);
            if (pos < 0 || pos == _readPos) return;
            int count = pos > _readPos ? pos - _readPos : _clip.samples - _readPos + pos;
            // GetData wraps around the looping clip by itself
            var tmp = new float[count];
            _clip.GetData(tmp, _readPos);
            _readPos = pos;
            ProcessSamples(tmp);
        }

        void ProcessSamples(float[] tmp)
        {
            int count = tmp.Length;
            if (count == 0) return;
            var pcm = new short[count];
            for (int i = 0; i < count; i++) pcm[i] = (short)Mathf.Clamp(tmp[i] * 32767f, -32768f, 32767f);

            for (int i = 0; i < count; i++) _ring[(_written + i) % _ring.Length] = pcm[i];
            _written += count;

            TrackVoice(tmp);
            if (Current == State.Waiting && !Continuous) _kwsQueue.Enqueue((pcm, _written));
            else if (Current == State.Recording) _utterance.AddRange(pcm);
        }

        /// <summary>Energy-based voice activity: louder than a few times the background noise.</summary>
        void TrackVoice(float[] samples)
        {
            int frame = (int)(Rate * FrameS);
            for (int start = 0; start + frame <= samples.Length; start += frame)
            {
                double sum = 0;
                for (int i = start; i < start + frame; i++) sum += samples[i] * samples[i];
                float rms = Mathf.Sqrt((float)(sum / frame));
                bool voiced = rms > Mathf.Max(0.010f, _noiseFloor * 2.5f);
                if (Current == State.Waiting && !voiced) // learn the background level slowly, follow drops quickly
                    _noiseFloor = rms < _noiseFloor ? Mathf.Lerp(_noiseFloor, rms, 0.2f) : Mathf.Lerp(_noiseFloor, rms, 0.002f);
                if (voiced) _lastVoiceAt = Time.realtimeSinceStartup;
                if (Current == State.Recording && voiced) { _voicedFramesInUtterance++; _levelSum += rms; _levelFrames++; }
                if (Continuous && Current == State.Waiting)
                {
                    // speech = 160 ms of voiced frames within a short window; gaps of up to 200 ms
                    // (soft consonants, pauses between words) don't restart it, so the start stays
                    // at the first word instead of moving to a later, louder one
                    if (voiced)
                    {
                        if (_voicedRun == 0) _speechStartSample = _written - samples.Length + start;
                        _gapFrames = 0;
                        if (++_voicedRun >= 8) { _speechTriggered = true; _voicedRun = 0; }
                    }
                    else if (_voicedRun > 0 && ++_gapFrames > 15) { _voicedRun = 0; _gapFrames = 0; }
                }
            }
        }

        void KeywordLoop()
        {
            // One recogniser per language with a tiny grammar ("keyword" vs. anything else): fast and
            // robust. Both hear the same audio; the first keyword found decides the language.
            var recs = new VoskRecognizer[_spotters.Count];
            long streamStart = -1; // ring position of the first sample these recognisers were fed
            var batch = new List<short>(KwsBatch * 2);
            try
            {
                while (_running)
                {
                    if (recs[0] == null || _resetKws)
                    {
                        // Fresh recognisers per round keep their word timestamps aligned with streamStart.
                        for (int i = 0; i < recs.Length; i++)
                        {
                            recs[i]?.Dispose();
                            recs[i] = new VoskRecognizer(_spotters[i].Model, Rate, _spotters[i].Grammar);
                            recs[i].SetWords(true);
                            recs[i].SetPartialWords(true);
                        }
                        streamStart = -1; batch.Clear(); _resetKws = false;
                        while (_kwsQueue.TryDequeue(out _)) { }
                    }
                    if (!_kwsQueue.TryDequeue(out var item)) { Thread.Sleep(10); continue; }
                    if (streamStart < 0) streamStart = item.end - item.pcm.Length;
                    batch.AddRange(item.pcm);
                    long batchEnd = item.end;
                    if (batch.Count < KwsBatch) continue;

                    var samples = batch.ToArray();
                    batch.Clear();
                    for (int i = 0; i < recs.Length; i++)
                    {
                        bool final = recs[i].AcceptWaveform(samples, samples.Length);
                        string json = final ? recs[i].Result() : recs[i].PartialResult();
                        var sp = _spotters[i];
                        // A two-phrase grammar squeezes unclear speech into the keyword (the German
                        // spotter "heard" was ... wäre ... wenn in English sentences, at confidence 1.0).
                        // Only the keyword's words spoken in a row count.
                        float? kwStart = KeywordStart(json, sp.Words);
                        if (kwStart == null) continue;
                        long start = streamStart + (long)((kwStart.Value - LeadInS) * Rate);
                        Interlocked.Exchange(ref _keywordStart, start);
                        _keywordLanguage = sp.Language;
                        _keywordHeard = true;
                        _resetKws = true; // re-arm with fresh recognisers for the next round
                        break;
                    }
                }
            }
            finally { foreach (var r in recs) r?.Dispose(); }
        }

        const float MaxWordGapS = 0.3f;   // between consecutive keyword words
        const float MaxKeywordS = 1.6f;   // whole keyword

        /// <summary>
        /// Start time of the keyword if its words appear consecutively in the result, close together;
        /// null otherwise. "*" matches any word except [unk].
        /// </summary>
        public static float? KeywordStart(string json, string[] keyword)
        {
            var words = Words(json);
            for (int i = 0; i + keyword.Length <= words.Count; i++)
            {
                bool ok = true;
                for (int k = 0; k < keyword.Length && ok; k++)
                {
                    var w = words[i + k].word;
                    ok = keyword[k] == "*" ? w != "[unk]" && w.Length > 0 : w == keyword[k];
                    if (ok && k > 0) ok = words[i + k].start - words[i + k - 1].end <= MaxWordGapS;
                }
                if (ok && words[i + keyword.Length - 1].end - words[i].start <= MaxKeywordS) return words[i].start;
            }
            return null;
        }

        /// <summary>Word objects of a Vosk result with word timings, in order.</summary>
        static List<(string word, float start, float end)> Words(string json)
        {
            var list = new List<(string, float, float)>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(json, @"\{[^{}]*\}"))
            {
                var obj = m.Value;
                var w = System.Text.RegularExpressions.Regex.Match(obj, "\"word\"\\s*:\\s*\"([^\"]*)\"");
                var st = System.Text.RegularExpressions.Regex.Match(obj, "\"start\"\\s*:\\s*([0-9.]+)");
                var en = System.Text.RegularExpressions.Regex.Match(obj, "\"end\"\\s*:\\s*([0-9.]+)");
                if (w.Success && st.Success && en.Success)
                    list.Add((w.Groups[1].Value, ParseF(st.Groups[1].Value), ParseF(en.Groups[1].Value)));
            }
            return list;
        }

        static float ParseF(string s) =>
            float.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Continuous mode: record from a ring position (just before the speech began).</summary>
        void BeginRecordingAt(long ringStart)
        {
            _utterance.Clear();
            long start = Math.Max(Math.Max(0, _written - _ring.Length + 1), ringStart);
            for (long i = start; i < _written; i++) _utterance.Add(_ring[i % _ring.Length]);
            _utteranceStart = _lastVoiceAt = Time.realtimeSinceStartup;
            _utteranceSpeechStart = _utteranceStart - (_written - start) / (float)Rate;
            _voicedFramesInUtterance = 8;
            _levelSum = 0; _levelFrames = 0;
            SetState(State.Recording, null);
            SpeechStarted?.Invoke();
        }

        void BeginRecording()
        {
            _utterance.Clear();
            // Copy everything from just before the spoken "what" up to now: "What if" is part of the
            // recording no matter how late the spotter confirmed it.
            long keywordStart = Interlocked.Read(ref _keywordStart);
            long start = Math.Max(Math.Max(0, _written - _ring.Length + 1), keywordStart);
            for (long i = start; i < _written; i++) _utterance.Add(_ring[i % _ring.Length]);
            Debug.Log($"[WhatIf] \"what if\" heard; recording starts {(_written - start) / (float)Rate:F2} s back");
            _utteranceStart = _lastVoiceAt = Time.realtimeSinceStartup;
            SetState(State.Recording, null);
            SpeechStarted?.Invoke();
        }

        System.Threading.Tasks.Task _transcriptions = System.Threading.Tasks.Task.CompletedTask;
        int _pending; // utterances sent for transcription, not yet handed on

        /// <summary>Speech is being recorded or transcribed (for a small "hearing you" sign).</summary>
        public bool Hearing => Current == State.Recording || _pending > 0;

        /// <summary>Sends one utterance right away; hands its text on only after the earlier ones.</summary>
        async System.Threading.Tasks.Task TranscribeInOrder(System.Threading.Tasks.Task previous, byte[] wav,
                                                           float speechStart, float speechEnd, float db = float.NaN)
        {
            Api.TranscribeResult result = null;
            Exception error = null;
            try { result = await _transcribe(wav, null); } // no language hint: Whisper picks en / de
            catch (Exception e) { error = e; }
            try { await previous; } catch { /* reported by its own call */ }
            if (error != null)
            {
                Debug.LogWarning($"[WhatIf] transcription failed: {error.Message}");
                SetStateMessage("Couldn't understand that - is the server running?");
            }
            else if (result != null && result.Speech && !string.IsNullOrWhiteSpace(result.Text))
            {
                // "What if ..." is (nearly always) the wearer: it teaches the wearer's level; louder
                // sentences pull it up quickly, quieter ones down slowly
                if (!float.IsNaN(db) && ExtractScenario(result.Text) != null)
                    _wearerDb = float.IsNaN(_wearerDb) ? db : Mathf.Lerp(_wearerDb, db, db > _wearerDb ? 0.5f : 0.2f);
                UtteranceHeard?.Invoke(result.Text.Trim(), speechStart, speechEnd);
            }
        }

        async void FinishRecording()
        {
            if (Continuous && _voicedFramesInUtterance * FrameS < 0.35f)
            {
                // a click, a cough: not worth a transcription
                _utterance.Clear();
                SetState(State.Waiting, null);
                return;
            }
            float db = _levelFrames > 0 ? 20f * Mathf.Log10(Mathf.Max(1e-6f, (float)(_levelSum / _levelFrames))) : -99f;
            if (Continuous && settings.ownVoiceOnly && !float.IsNaN(_wearerDb) && db < _wearerDb - settings.ownVoiceMarginDb)
            {
                // much quieter than the wearer: someone else in the room - not transcribed, not sent
                Debug.Log($"[WhatIf] voice: {db:0} dB, wearer {_wearerDb:0} dB - someone else, ignored");
                _utterance.Clear();
                SetState(State.Waiting, null);
                return;
            }
            Debug.Log($"[WhatIf] voice: {db:0} dB" + (float.IsNaN(_wearerDb) ? " (wearer level not known yet)" : $", wearer {_wearerDb:0} dB"));
            var wav = ToWav(_utterance.ToArray());
            _utterance.Clear();
            float speechStart = _utteranceSpeechStart, speechEnd = Time.realtimeSinceStartup;
            if (Continuous)
            {
                // Keep listening while this one is transcribed (2-5 s on the server): a sentence that
                // starts meanwhile used to be picked up only from the middle ("... eine Maus auf der
                // Tastatur" without "Was wäre, wenn"). Results are still handed on in spoken order.
                SetState(State.Waiting, null);
                System.Threading.Interlocked.Increment(ref _pending);
                try
                {
                    _transcriptions = TranscribeInOrder(_transcriptions, wav, speechStart, speechEnd, db);
                    await _transcriptions;
                }
                finally { System.Threading.Interlocked.Decrement(ref _pending); }
                return;
            }
            SetState(State.Transcribing, null);
            try
            {
                string language = _keywordLanguage;
                var text = (await _transcribe(wav, language)).Text ?? "";
                // Whisper has the last word: only a transcript that really contains the keyword becomes
                // a prompt (the offline spotter can be fooled; chatter must not turn into prompts).
                var prompt = ExtractPrompt(text);
                if (prompt != null) PromptHeard?.Invoke(prompt);
                else SetStateMessage(settings.germanKeyword && !settings.englishOnly ? "Start with \"What if\" or \"Was wäre, wenn\"" : "Start with \"What if\"");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WhatIf] transcription failed: {e.Message}");
                SetStateMessage("Couldn't understand that - is the server running?");
            }
            finally
            {
                _resetKws = true;
                SetState(State.Waiting, null);
            }
        }

        // German transcripts sometimes translate the spoken "What if" ("Was wäre, wenn").
        // A sure start of a new scene: only "What if" itself.
        static readonly string[] SureForms = { "what if", "was wäre, wenn", "was wäre wenn", "was waere wenn" };
        // Natural openers: in typed and keyword-gated input they start a scene too, but in continuous
        // listening they are said to people as well ("zeig mir mal", "imagine that!"): there the planner
        // first decides whether they were meant for the app.
        static readonly string[] KeywordForms =
        {
            "what if", "was wäre, wenn", "was wäre wenn", "was waere wenn",
            "imagine", "show me", "stell dir vor", "stellt euch vor", "zeig mir",
        };

        /// <summary>The prompt if the text STARTS with "What if" / "Was wäre, wenn" (allowing a short
        /// lead-in like "okay, ..."); null otherwise. Continuous listening: only the sure forms.</summary>
        public static string ExtractScenario(string text)
        {
            var p = ExtractPrompt(text, SureForms);
            return p != null && text.Trim().Length - p.Length <= 15 ? p : null;
        }

        /// <summary>Keeps the text from "What if" (or its German form, or an opener) on; null if none is
        /// there. Whole words only (seen: "Er imagined noch" to a colleague built a whole jungle).</summary>
        public static string ExtractPrompt(string text) => ExtractPrompt(text, KeywordForms);

        static string ExtractPrompt(string text, string[] forms)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int best = -1;
            foreach (var k in forms)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(k) + @"(?![\p{L}\p{N}])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && (best < 0 || m.Index < best)) best = m.Index;
            }
            if (best < 0) return null;
            var p = text.Substring(best).Trim();
            return char.ToUpperInvariant(p[0]) + p.Substring(1);
        }

        // Always understood, in addition to WhatIfSettings.clearPhrases (English and German).
        static readonly string[] BuiltInClearPhrases =
            { "empty again", "back to normal", "clear everything", "wieder leer", "wie vorher", "alles weg", "zurücksetzen" };

        public bool IsClearCommand(string prompt)
        {
            foreach (var phrase in BuiltInClearPhrases)
                if (prompt.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var phrase in settings.clearPhrases)
                if (!string.IsNullOrEmpty(phrase) && prompt.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static byte[] ToWav(short[] pcm)
        {
            using var ms = new MemoryStream(44 + pcm.Length * 2);
            using var w = new BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
            return ms.ToArray();
        }

        void SetState(State s, string message)
        {
            Current = s;
            StateChanged?.Invoke(s, message);
        }

        void SetStateMessage(string message) => StateChanged?.Invoke(Current, message);

        void OnDestroy()
        {
            _running = false;
            _kwsThread?.Join(500);
            if (_device != null) Microphone.End(_device);
#if UNITY_ANDROID && !UNITY_EDITOR
            _mlClip?.Dispose();
            _mlClip = null;
#endif
            foreach (var sp in _spotters) sp.Model?.Dispose();
        }
    }
}
