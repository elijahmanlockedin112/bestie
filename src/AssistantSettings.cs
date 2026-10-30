// AssistantSettings.cs - the assistant's settings (.state\settings.json). C# 5 only.
// The OpenAI key is stored with DPAPI (CurrentUser) as base64, never as plaintext.
// Falls back to the OPENAI_API_KEY environment variable when nothing is saved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Bestie
{
    public sealed class AssistantSettings
    {
        // Defaults picked in Sep 2026 (developers.openai.com/api/docs/models + /pricing):
        //  - gpt-6-luna: OpenAI's cheapest current tier ($0.10 in / $0.50 out per 1M), text+image in,
        //    function calling. In Chat Completions it only does function calling with
        //    reasoning_effort "none", which is also the fastest, so that's the default effort.
        //  - gpt-transcribe: the recommended /audio/transcriptions model (~$0.0045/min).
        //  - gpt-4o-mini-tts: the current steerable TTS model (takes `instructions`; $0.60/1M text in,
        //    $12/1M audio out, roughly $0.015/min), cheaper per character than tts-1 ($15/1M chars).
        // Since Oct 30 2026 (the owner's pick): Claude thinks, on the owner's Claude credit (Claude.cs keeps the key and
        // the spend ledger). Haiku 5.5 first ($0.10 / $0.50 a million tokens); Sonnet 5.5 ($2 / $10) only when Haiku fails
        // (OpenAIClient.FallbackModel). OpenAI still does the voice and the ears when those are set to openai.
        public const string DefaultModel = "claude-haiku-5-5";
        public const string DefaultFallbackModel = "claude-sonnet-5-5";
        // the on-screen visual explainers (Visuals.cs): drawn and narrated by this model (haiku: the owner's pick, for cost)
        public const string DefaultVisualModel = "claude-haiku-5-5";
        public const string DefaultTtsModel = "gpt-4o-mini-tts";
        public const string DefaultOpenAiVoice = "ballad";
        public const string DefaultReasoningEffort = "none";
        public const string DefaultTranscribeModel = "gpt-transcribe";
        public const string DefaultBaseUrl = "https://api.openai.com/v1";
        public const string DefaultHotkeyChat = "Ctrl+Alt+B";
        public const string DefaultHotkeyVoice = "Ctrl+Alt+V";

        public static readonly string[] SuggestedModels = new string[] {
            "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "gpt-6-luna", "gpt-6.1-sol", "gpt-6-sol", "gpt-5.4-mini", "gpt-5-mini", "gpt-4o-mini" };
        public static readonly string[] SuggestedTranscribeModels = new string[] {
            "gpt-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1" };
        public static readonly string[] SuggestedTtsModels = new string[] { "gpt-4o-mini-tts", "tts-1", "tts-1-hd" };
        // built-in /audio/speech voices (Sep 2026). quirky ones first.
        public static readonly string[] OpenAiVoices = new string[] {
            "ballad", "sage", "fable", "verse", "coral", "marin", "cedar", "alloy", "ash", "echo", "nova", "onyx", "shimmer" };
        // "" means: don't send reasoning_effort at all.
        public static readonly string[] ReasoningChoices = new string[] { "none", "minimal", "low", "medium", "high", "" };

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("bestie/brain-key/v1");

        public string ProtectedKey = "";        // DPAPI(CurrentUser) + base64
        public string Model = DefaultModel;
        public string FallbackModel = DefaultFallbackModel;   // "" = no second try
        // the jobs nobody waits on (memory notes, the journal digest, patterns, peeks, the radio dj's script): a model at a
        // twentieth of the price ($0.10 / $0.50 a million tokens vs $2 / $10)
        public const string DefaultBackgroundModel = "claude-haiku-5-5";
        public string BackgroundModel = DefaultBackgroundModel;
        public string VisualModel = DefaultVisualModel;
        public bool ShowVisuals = true;         // "draw it on my screen when i ask how something works"
        bool movedToClaude;                     // the settings were switched over to claude once (see LoadFile)
        public string ReasoningEffort = DefaultReasoningEffort;
        public string TranscribeModel = DefaultTranscribeModel;
        public string BaseUrl = DefaultBaseUrl;
        public bool SpeakReplies = true;
        public string VoiceEngine = "openai";   // "openai" (needs a key) | "elevenlabs" (needs an elevenlabs key) | "windows" (System.Speech, free)
        public string ElevenKeyProtected = "";  // DPAPI + base64
        public string ElevenVoice = "";         // elevenlabs voice id ("" = the account's first voice)
        public string ElevenVoiceName = "";     // for showing in settings
        public string ElevenModel = ElevenLabs.DefaultModel;
        public string TranscribeEngine = "openai";   // who turns speech into text: "openai" | "elevenlabs"
        public string TtsModel = DefaultTtsModel;
        public string OpenAiVoice = DefaultOpenAiVoice;
        public string TtsVoice = "";            // installed SAPI voice name, "" = system default
        public int TtsRate = 0;                 // -10..10 (windows voice)
        public bool AutoLookScreens = true;     // attach every monitor to every message
        public bool BeNosy = true;              // occasional peek + one remark (the creature schedules it)
        public int NosyMinutes = 20;            // "about every N minutes"
        public bool SpeakNosy = false;          // say peek remarks out loud (also needs SpeakReplies)
        public string NosySkipList = DefaultNosySkipList;
        public string GoogleClientId = "";
        public string GoogleClientSecretProtected = "";   // DPAPI + base64
        public bool CalendarWarn = true;        // heads-up ~10 minutes before timed events
        public int VoiceGainPercent = 240;      // openai voice loudness, 100..400 (PcmStreamPlayer.Gain)
        public bool AutoMemory = true;          // "remember things from our chats automatically"
        public bool ActWhenAsked = false;       // "don't ask first when my message asks for it" (computer takeovers, commands)
        public bool WakeWord = true;            // "listen for my wake word (hey <name>)"
        public bool FollowUp = true;            // after a spoken answer, keep listening a few seconds (no wake word needed)
        public string AlexaTokenProtected = ""; // voice monkey token, DPAPI + base64
        public string AlexaDevice = Alexa.DefaultDevice;   // the voice monkey speaker (linked to their echo)
        public string AlexaWhen = "away";       // reminders out loud on the alexa: "away" (stepped away from the computer) | "always" | "asked" (only when asked)

        public const string DefaultNosySkipList =
            "1password, bitwarden, keepass, lastpass, dashlane, inprivate, incognito, private browsing, bank, banking, paypal, venmo, " +
            "sign in, log in, login, password, checkout, payment, tax, medical, health";
        public string HotkeyChat = DefaultHotkeyChat;
        public string HotkeyVoice = DefaultHotkeyVoice;
        public string HotkeyScreen = "";        // "" = off
        public string TalkKey = Bestie.TalkKey.Default;   // hold to talk ("F9, MediaNextTrack": keys on their own, no modifiers); "" = off
        public bool AllowScreenTool = true;
        public bool AllowTabReading = true;
        public int MaxHistoryMessages = 24;
        public int MaxReplyTokens = 1200;
        public string ImageDetail = "low";      // low | high | auto

        string cachedKey;
        bool cachedKeyValid;

        public static string FilePath { get { return Home.State("settings.json"); } }

        public static AssistantSettings Load()
        {
            AssistantSettings s = LoadFile();
            OpenAIClient.FallbackModel = s.FallbackModel;
            return s;
        }

        static AssistantSettings LoadFile()
        {
            var s = new AssistantSettings();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return s;
                Dictionary<string, object> d = Json.Obj(File.ReadAllText(path, Encoding.UTF8));
                if (d == null) return s;
                s.ProtectedKey = Json.Str(d, "apiKeyProtected") ?? "";
                s.Model = NonEmpty(Json.Str(d, "model"), DefaultModel);
                s.BackgroundModel = NonEmpty(Json.Str(d, "backgroundModel"), DefaultBackgroundModel);
                string fb = Json.Str(d, "fallbackModel");
                s.FallbackModel = fb == null ? DefaultFallbackModel : fb.Trim();
                string effort = Json.Str(d, "reasoningEffort");
                s.ReasoningEffort = effort == null ? DefaultReasoningEffort : effort.Trim();
                s.TranscribeModel = NonEmpty(Json.Str(d, "transcribeModel"), DefaultTranscribeModel);
                s.BaseUrl = NonEmpty(Json.Str(d, "baseUrl"), DefaultBaseUrl);
                s.SpeakReplies = Json.Bool(d, "speakReplies", true);
                string ve = (Json.Str(d, "voiceEngine") ?? "openai").Trim().ToLowerInvariant();
                s.VoiceEngine = ve == "windows" || ve == "elevenlabs" ? ve : "openai";
                s.ElevenKeyProtected = Json.Str(d, "elevenKeyProtected") ?? "";
                s.ElevenVoice = Json.Str(d, "elevenVoice") ?? "";
                s.ElevenVoiceName = Json.Str(d, "elevenVoiceName") ?? "";
                s.ElevenModel = NonEmpty(Json.Str(d, "elevenModel"), ElevenLabs.DefaultModel);
                s.TranscribeEngine = Json.Str(d, "transcribeEngine") == "elevenlabs" ? "elevenlabs" : "openai";
                s.TtsModel = NonEmpty(Json.Str(d, "ttsModel"), DefaultTtsModel);
                s.OpenAiVoice = NonEmpty(Json.Str(d, "openaiVoice"), DefaultOpenAiVoice);
                s.AutoLookScreens = Json.Bool(d, "autoLookScreens", true);
                s.BeNosy = Json.Bool(d, "beNosy", true);
                s.NosyMinutes = Clamp((int)Json.Num(d, "nosyMinutes", 20), 5, 24 * 60);
                s.SpeakNosy = Json.Bool(d, "speakNosy", false);
                s.NosySkipList = Json.Str(d, "nosySkipList") ?? DefaultNosySkipList;
                s.GoogleClientId = (Json.Str(d, "googleClientId") ?? "").Trim();
                s.GoogleClientSecretProtected = Json.Str(d, "googleClientSecretProtected") ?? "";
                s.CalendarWarn = Json.Bool(d, "calendarWarn", true);
                s.VoiceGainPercent = Clamp((int)Json.Num(d, "voiceGainPercent", 240), 100, 400);
                s.AutoMemory = Json.Bool(d, "autoMemory", true);
                s.ActWhenAsked = Json.Bool(d, "actWhenAsked", false);
                s.WakeWord = Json.Bool(d, "wakeWord", true);
                s.FollowUp = Json.Bool(d, "followUp", true);
                s.AlexaTokenProtected = Json.Str(d, "alexaTokenProtected") ?? "";
                s.AlexaDevice = NonEmpty(Json.Str(d, "alexaDevice"), Alexa.DefaultDevice);
                string aw = Json.Str(d, "alexaWhen");
                s.AlexaWhen = aw == "always" || aw == "asked" ? aw : "away";
                s.TtsVoice = Json.Str(d, "ttsVoice") ?? "";
                s.TtsRate = Clamp((int)Json.Num(d, "ttsRate", 0), -10, 10);
                string hk = Json.Str(d, "hotkeyChat"); s.HotkeyChat = hk == null ? DefaultHotkeyChat : hk.Trim();
                hk = Json.Str(d, "hotkeyVoice"); s.HotkeyVoice = hk == null ? DefaultHotkeyVoice : hk.Trim();
                hk = Json.Str(d, "hotkeyScreen"); s.HotkeyScreen = hk == null ? "" : hk.Trim();
                hk = Json.Str(d, "talkKey"); s.TalkKey = hk == null ? Bestie.TalkKey.Default : hk.Trim();
                s.AllowScreenTool = Json.Bool(d, "allowScreenTool", true);
                s.AllowTabReading = Json.Bool(d, "allowTabReading", true);
                s.MaxHistoryMessages = Clamp((int)Json.Num(d, "maxHistoryMessages", 24), 4, 200);
                s.MaxReplyTokens = Clamp((int)Json.Num(d, "maxReplyTokens", 1200), 64, 32000);
                s.ImageDetail = NonEmpty(Json.Str(d, "imageDetail"), "low");
                // (a model saved before the visuals moved to haiku on oct 30 is the old default, not a choice)
                s.VisualModel = Json.Bool(d, "visualsOnHaiku", false) ? NonEmpty(Json.Str(d, "visualModel"), DefaultVisualModel) : DefaultVisualModel;
                s.ShowVisuals = Json.Bool(d, "showVisuals", true);
                s.movedToClaude = Json.Bool(d, "movedToClaude", false);
                if (!s.movedToClaude) MoveToClaude(s);
            }
            catch (Exception ex) { DebugLog.Write("assistant: settings load failed: " + ex.Message); }
            return s;
        }

        /// <summary>Once (Oct 30 2026, the owner's call): the thinking moves from openai's models to claude's, and the
        /// ears to elevenlabs when there's a key for it (the openai account had run out of credit). Any model can
        /// still be picked again in settings afterwards; this never runs twice.</summary>
        static void MoveToClaude(AssistantSettings s)
        {
            Func<string, bool> openai = m => !string.IsNullOrEmpty(m) && !OpenAIClient.IsClaude(m);
            if (openai(s.Model)) s.Model = DefaultModel;
            if (openai(s.BackgroundModel)) s.BackgroundModel = DefaultBackgroundModel;
            if (openai(s.FallbackModel)) s.FallbackModel = DefaultFallbackModel;
            if (s.TranscribeEngine == "openai" && s.ElevenKey.Length > 0) s.TranscribeEngine = "elevenlabs";
            s.movedToClaude = true;
            try { s.Save(); DebugLog.Write("assistant: settings moved to claude (model " + s.Model + ", ears " + s.TranscribeEngine + ")"); }
            catch (Exception ex) { DebugLog.Write("assistant: couldn't save the move to claude: " + ex.Message); }
        }

        public void Save()
        {
            var d = new Dictionary<string, object>();
            d["apiKeyProtected"] = ProtectedKey ?? "";
            d["model"] = Model ?? DefaultModel;
            d["backgroundModel"] = BackgroundModel ?? DefaultBackgroundModel;
            d["fallbackModel"] = FallbackModel ?? "";
            OpenAIClient.FallbackModel = FallbackModel;
            d["reasoningEffort"] = ReasoningEffort ?? "";
            d["transcribeModel"] = TranscribeModel ?? DefaultTranscribeModel;
            d["baseUrl"] = BaseUrl ?? DefaultBaseUrl;
            d["speakReplies"] = SpeakReplies;
            d["voiceEngine"] = VoiceEngine == "windows" || VoiceEngine == "elevenlabs" ? VoiceEngine : "openai";
            d["elevenKeyProtected"] = ElevenKeyProtected ?? "";
            d["elevenVoice"] = ElevenVoice ?? "";
            d["elevenVoiceName"] = ElevenVoiceName ?? "";
            d["elevenModel"] = ElevenModel ?? ElevenLabs.DefaultModel;
            d["transcribeEngine"] = TranscribeEngine == "elevenlabs" ? "elevenlabs" : "openai";
            d["ttsModel"] = TtsModel ?? DefaultTtsModel;
            d["openaiVoice"] = OpenAiVoice ?? DefaultOpenAiVoice;
            d["autoLookScreens"] = AutoLookScreens;
            d["beNosy"] = BeNosy;
            d["nosyMinutes"] = NosyMinutes;
            d["speakNosy"] = SpeakNosy;
            d["nosySkipList"] = NosySkipList ?? "";
            d["googleClientId"] = GoogleClientId ?? "";
            d["googleClientSecretProtected"] = GoogleClientSecretProtected ?? "";
            d["calendarWarn"] = CalendarWarn;
            d["voiceGainPercent"] = VoiceGainPercent;
            d["autoMemory"] = AutoMemory;
            d["actWhenAsked"] = ActWhenAsked;
            d["wakeWord"] = WakeWord;
            d["followUp"] = FollowUp;
            d["alexaTokenProtected"] = AlexaTokenProtected ?? "";
            d["alexaDevice"] = AlexaDevice ?? Alexa.DefaultDevice;
            d["alexaWhen"] = AlexaWhen ?? "away";
            d["ttsVoice"] = TtsVoice ?? "";
            d["ttsRate"] = TtsRate;
            d["hotkeyChat"] = HotkeyChat ?? "";
            d["hotkeyVoice"] = HotkeyVoice ?? "";
            d["hotkeyScreen"] = HotkeyScreen ?? "";
            d["talkKey"] = TalkKey ?? "";
            d["allowScreenTool"] = AllowScreenTool;
            d["allowTabReading"] = AllowTabReading;
            d["maxHistoryMessages"] = MaxHistoryMessages;
            d["maxReplyTokens"] = MaxReplyTokens;
            d["imageDetail"] = ImageDetail ?? "low";
            d["visualModel"] = VisualModel ?? DefaultVisualModel;
            d["visualsOnHaiku"] = true;
            d["showVisuals"] = ShowVisuals;
            d["movedToClaude"] = true;   // saved by a version that already defaults to claude: a model picked after this stays picked
            string path = FilePath;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(d).Replace(",\"", ",\r\n  \"").Replace("{\"", "{\r\n  \"").TrimEnd('}') + "\r\n}\r\n",
                new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public AssistantSettings Clone() { return (AssistantSettings)MemberwiseClone(); }

        // ------------------------------------------------------------------ the key

        /// <summary>The key saved in settings (decrypted), or "".</summary>
        public string SavedKey
        {
            get
            {
                if (!cachedKeyValid) { cachedKey = Unprotect(ProtectedKey); cachedKeyValid = true; }
                return cachedKey ?? "";
            }
        }

        /// <summary>The key to use: saved key, else OPENAI_API_KEY, else "".</summary>
        public string ApiKey { get { string k = SavedKey; return k.Length > 0 ? k : EnvKey; } }

        public bool HasKey { get { return ApiKey.Length > 0; } }

        /// <summary>The Google OAuth client secret (decrypted), or "".</summary>
        public string GoogleClientSecret { get { return Unprotect(GoogleClientSecretProtected); } }

        public void SetGoogleClientSecret(string secret)
        {
            secret = (secret ?? "").Trim();
            GoogleClientSecretProtected = secret.Length == 0 ? "" : Protect(secret);
        }

        /// <summary>The ElevenLabs key (decrypted), or "".</summary>
        public string ElevenKey { get { return Unprotect(ElevenKeyProtected); } }

        public void SetElevenKey(string key)
        {
            key = CleanKey(key);
            ElevenKeyProtected = key.Length == 0 ? "" : Protect(key);
        }

        /// <summary>The Voice Monkey token for the alexa (decrypted), or "".</summary>
        public string AlexaToken { get { return Unprotect(AlexaTokenProtected); } }

        public void SetAlexaToken(string token)
        {
            token = CleanKey(token);
            AlexaTokenProtected = token.Length == 0 ? "" : Protect(token);
        }

        /// <summary>ElevenLabs voice if chosen and its key exists.</summary>
        public bool UseElevenVoice { get { return VoiceEngine == "elevenlabs" && ElevenKey.Length > 0; } }

        /// <summary>OpenAI voice if chosen (or elevenlabs was chosen without its key) and an openai key exists;
        /// otherwise the free windows voice.</summary>
        public bool UseOpenAiVoice { get { return HasKey && (VoiceEngine == "openai" || VoiceEngine == "elevenlabs" && ElevenKey.Length == 0); } }

        /// <summary>"saved", "env" or "".</summary>
        public string KeySource { get { if (SavedKey.Length > 0) return "saved"; if (EnvKey.Length > 0) return "env"; return ""; } }

        /// <summary>Store a key (encrypted). Empty clears it.</summary>
        public void SetKey(string key)
        {
            key = CleanKey(key);
            ProtectedKey = key.Length == 0 ? "" : Protect(key);
            cachedKey = key;
            cachedKeyValid = true;
        }

        public static string CleanKey(string key)
        {
            if (key == null) return "";
            key = key.Trim().Trim('"', '\'', '`').Trim();
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key.Substring(7).Trim();
            var sb = new StringBuilder(key.Length);
            foreach (char c in key) if (!char.IsWhiteSpace(c) && !char.IsControl(c)) sb.Append(c);
            return sb.ToString();
        }

        public static string EnvKey
        {
            get
            {
                string k = null;
                try
                {
                    k = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    if (string.IsNullOrEmpty(k)) k = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
                }
                catch { }
                return CleanKey(k);
            }
        }

        public static string Protect(string plain)
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                DebugLog.Write("assistant: couldn't decrypt the saved key (" + ex.GetType().Name + "); ignoring it");
                return "";
            }
        }

        static string NonEmpty(string v, string fallback) { return string.IsNullOrEmpty(v) || v.Trim().Length == 0 ? fallback : v.Trim(); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
