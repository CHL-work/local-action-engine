using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Brainwash
{
    // Starts the existing local inference service the same way unity/play.ps1
    // does (prototype/start-unity-service.ps1), so the editor or a build can be
    // played without opening a terminal. The service keeps running after the
    // game closes; stop it with start-unity-service.ps1 -Stop or the HUD button.
    public sealed class LocalServiceLauncher : MonoBehaviour
    {
        public string Status { get; private set; } = "";
        public bool Starting => process != null && !process.HasExited;
        Process process;
        StreamWriter log;

        public static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "prototype", "start-unity-service.ps1"))) return directory.FullName;
            return null;
        }

        public bool CanLaunch => Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;

        public bool Restarting { get; private set; }

        // The arguments that run prototype/start-unity-service.ps1 (or its -Stop).
        public static ProcessStartInfo ScriptInfo(string root, int port, bool stop)
        {
            string script = Path.Combine(root, "prototype", "start-unity-service.ps1");
            string localRoot = null, marker = Path.Combine(root, ".brainwash-local-root");
            try { if (File.Exists(marker)) localRoot = File.ReadAllText(marker).Trim(); } catch { localRoot = null; }
            string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Port " + port
                + (stop ? " -Stop" : string.IsNullOrEmpty(localRoot) ? "" : " -LocalRoot \"" + localRoot + "\"");
            return new ProcessStartInfo("powershell.exe", arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        }

        // Runs the script with its output appended to .local/unity/service-from-game.log.
        public static Process StartLogged(ProcessStartInfo info, string root, out StreamWriter log)
        {
            string logs = Path.Combine(root, ".local", "unity");
            Directory.CreateDirectory(logs);
            var stream = new FileStream(Path.Combine(logs, "service-from-game.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            log = new StreamWriter(stream) { AutoFlush = true };
            log.WriteLine("---- " + DateTime.Now.ToString("s") + " powershell.exe " + info.Arguments);
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var writer = log;
            process.OutputDataReceived += (_, e) => Write(writer, e.Data);
            process.ErrorDataReceived += (_, e) => Write(writer, e.Data == null ? null : "ERR " + e.Data);
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return process;
        }

        public bool Launch(int port)
        {
            if (Starting || Restarting) { Status = "The local AI service is already starting."; return false; }
            if (!CanLaunch) { Status = "Automatic start is only available on Windows."; return false; }
            string root = RepositoryRoot();
            if (root == null) { Status = "Could not find prototype/start-unity-service.ps1 above " + Application.dataPath; return false; }
            try
            {
                log?.Dispose();
                process = StartLogged(ScriptInfo(root, port, false), root, out log);
                Status = "Starting the local AI service. Models take about a minute to load.";
                return true;
            }
            catch (Exception exception)
            {
                Status = "Could not start the service: " + exception.Message; process = null; return false;
            }
        }

        // An older service still running from before an update is asked to stop
        // (the same guarded shutdown as start-unity-service.ps1 -Stop), then the
        // current one is started.
        public bool Restart(int port)
        {
            if (Starting || Restarting) return false;
            if (!CanLaunch) { Status = "Automatic restart is only available on Windows."; return false; }
            StartCoroutine(RestartRoutine(port));
            return true;
        }

        IEnumerator RestartRoutine(int port)
        {
            Restarting = true; Status = "Stopping the older local AI service...";
            string endpoint = "http://127.0.0.1:" + port;
            using (var request = new UnityWebRequest(endpoint + "/api/shutdown", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-Brainwash-Unity", "1");
                request.timeout = 10;
                yield return request.SendWebRequest();
            }
            float until = Time.realtimeSinceStartup + 25;
            while (Time.realtimeSinceStartup < until)
            {
                bool answering;
                using (var health = UnityWebRequest.Get(endpoint + "/api/health"))
                {
                    health.timeout = 2;
                    yield return health.SendWebRequest();
                    answering = health.result == UnityWebRequest.Result.Success;
                }
                if (!answering) break;
                yield return new WaitForSecondsRealtime(.5f);
            }
            Restarting = false;
            Launch(port);
        }

        static void Write(StreamWriter writer, string line)
        {
            if (line == null) return;
            try { lock (writer) writer.WriteLine(line); } catch { }
        }

        void Update()
        {
            if (process != null && process.HasExited)
            {
                Status = process.ExitCode == 0 ? "The local AI service stopped." : "The local AI service exited (code " + process.ExitCode + "). See .local/unity/service-from-game.log.";
                process.Dispose(); process = null;
                var finished = log; log = null;
                if (finished != null) lock (finished) finished.Dispose();
            }
        }

        void OnDestroy()
        {
            // The service process is intentionally left running for the next session.
            try { log?.Dispose(); } catch { }
        }
    }
}
