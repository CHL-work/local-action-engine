using System;
using System.Collections;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Brainwash
{
    public sealed class InferenceClient : MonoBehaviour
    {
        public string Endpoint = "http://127.0.0.1:4174";
        public bool Ready { get; private set; }
        public bool Outdated { get; private set; }
        // Requests in flight from this game. The service queues them; nothing here
        // waits for one to finish before sending the next.
        public bool Busy => pending.Count > 0;
        public int InFlight => pending.Count;
        // Informational: the service reported work running or queued.
        public bool ServiceBusy => serviceGate.Busy;
        public string Health { get; private set; } = "Connecting to local Laya service...";
        public string Model { get; private set; } = "Original English epoch 4";
        public string SpeechModel { get; private set; } = "Checking speech model...";
        public JObject LastResponse { get; private set; }
        public event Action<string> Log;
        readonly System.Collections.Generic.List<UnityWebRequest> pending = new System.Collections.Generic.List<UnityWebRequest>();
        int generation;
        readonly InferenceServiceGate serviceGate = new InferenceServiceGate();

        void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-brainwash-port" && int.TryParse(args[i + 1], out int port) && port >= 1024 && port <= 65535)
                    Endpoint = "http://127.0.0.1:" + port;
            StartCoroutine(CheckHealth());
        }

        IEnumerator CheckHealth()
        {
            while (enabled)
            {
                int pollRevision = serviceGate.Revision;
                using (var request = UnityWebRequest.Get(Endpoint + "/api/health"))
                {
                    request.timeout = 3;
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            JObject info = JObject.Parse(request.downloadHandler.text);
                            // A service from before multi-listener casts refuses a second
                            // request while one runs; it must be restarted, not used.
                            Outdated = !(info["features"] is JArray features && features.Any(f => (string)f == "queue"));
                            Ready = info.Value<bool>("ready") && !Outdated;
                            serviceGate.ObserveHealth(pollRevision, info.Value<bool>("busy"));
                            Health = Outdated ? "An older local AI service is running. Restart it to brainwash several monsters at once."
                                : Ready ? (ServiceBusy ? "Local Laya + English speech ready (working)" : "Local Laya + English speech ready") : (info.Value<string>("error") ?? "Loading local models...");
                            if (info["model"] != null) Model = info["model"]?.Value<string>("model") ?? Model;
                            if (info["stt"] is JObject speech) SpeechModel = speech.Value<string>("displayName") ?? speech.Value<string>("model") ?? SpeechModel;
                        }
                        catch { Ready = false; Health = "Invalid health response from local service."; }
                    }
                    else { Ready = false; Outdated = false; Health = "Service offline. Run prototype/start-unity-service.ps1"; }
                }
                yield return new WaitForSecondsRealtime(2);
            }
        }

        // Every listener of a cast is sent at once; the local service runs them in
        // turn. budgetSeconds is what is left of that listener's Thinking time, so
        // the service can drop work that could no longer be used. stillWanted is
        // polled while waiting: a listener that timed out or was cancelled aborts
        // its request instead of holding up the others.
        public void Submit(string requestId, JObject scene, string text, byte[] audio, Action<JObject, string> complete, float budgetSeconds = 0, Func<bool> stillWanted = null)
        {
            var payload = new JObject { ["requestId"] = requestId, ["scene"] = scene.DeepClone() };
            if (audio != null) payload["audioBase64"] = Convert.ToBase64String(audio);
            else payload["text"] = text;
            if (budgetSeconds > 0) payload["budgetMs"] = Math.Max(1, Math.Round(budgetSeconds * 1000));
            StartCoroutine(Send("/api/command", payload, generation, stillWanted, (response, error) =>
            {
                if (error == null && response != null && response.Value<string>("requestId") != requestId) error = "Discarded response with a mismatched request ID.";
                if (response != null) LastResponse = response;
                complete(response, error);
            }));
        }

        // Speech to text only: chanting, and the words a group of listeners heard.
        public void Transcribe(byte[] audio, Action<string, string, double> complete, float budgetSeconds = 0, Func<bool> stillWanted = null)
        {
            if (!Ready) { complete(null, "the local speech service is not ready", 0); return; }
            var payload = new JObject { ["requestId"] = Guid.NewGuid().ToString("N"), ["audioBase64"] = Convert.ToBase64String(audio) };
            if (budgetSeconds > 0) payload["budgetMs"] = Math.Max(1, Math.Round(budgetSeconds * 1000));
            StartCoroutine(Send("/api/transcribe", payload, generation, stillWanted, (response, error) =>
                complete(response?.Value<string>("transcript"), error, response?["timings"]?["sttMs"]?.Value<double>() ?? 0)));
        }

        IEnumerator Send(string route, JObject payload, int ticket, Func<bool> stillWanted, Action<JObject, string> complete)
        {
            using (var request = new UnityWebRequest(Endpoint + route, "POST"))
            {
                pending.Add(request);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 8; // The world itself expires each listener's Thinking at five seconds.
                var operation = request.SendWebRequest();
                bool abandoned = false;
                while (!operation.isDone)
                {
                    if (ticket != generation || (stillWanted != null && !stillWanted())) { abandoned = true; request.Abort(); break; }
                    yield return null;
                }
                pending.Remove(request);
                if (abandoned || ticket != generation) yield break;
                JObject response = null;
                string error = null;
                try
                {
                    if (!string.IsNullOrEmpty(request.downloadHandler.text)) response = JObject.Parse(request.downloadHandler.text);
                    error = response?.Value<string>("error");
                    if (request.result != UnityWebRequest.Result.Success) error ??= request.error;
                }
                catch (Exception e) { error = "Invalid inference response: " + e.Message; }
                if (error == null && response != null && request.result == UnityWebRequest.Result.Success)
                {
                    serviceGate.CompletedSuccessfully();
                    if (Ready) Health = "Local Laya + English speech ready";
                }
                else serviceGate.ObserveFailure(request.responseCode,
                    request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError);
                if (error != null) Log?.Invoke("Service: " + error);
                complete(response, error);
            }
        }

        public void CancelPending()
        {
            ++generation;
            if (pending.Count > 0) serviceGate.CancelledInFlight();
            foreach (var request in pending.ToArray()) { try { request.Abort(); } catch (Exception) { } }
            pending.Clear();
        }
        void OnDestroy() => CancelPending();
    }
}
