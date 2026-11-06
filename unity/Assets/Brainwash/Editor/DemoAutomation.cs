using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Brainwash.Editor
{
    // For agents and scripts that cannot click. In an open editor, creating
    // Library/brainwash-demo-smoke.autorun refreshes assets and runs the demo
    // smoke (report in Logs/brainwash-demo-smoke.json); creating
    // Library/brainwash-build.autorun builds the Windows player; creating
    // Library/brainwash-player-smoke.autorun runs that build's demo smoke
    // (Logs/brainwash-player-smoke.json) and Library/brainwash-classic-smoke.autorun
    // its original arena acceptance (Logs/brainwash-classic-smoke.json). Compiler errors are copied to
    // Logs/brainwash-compile.log and each run is noted in Logs/brainwash-autorun.log.
    // Library/brainwash-service-restart.autorun stops the local AI service on
    // port 4174 and starts this checkout's version of it. Library/brainwash-voice-smoke.autorun
    // and Library/brainwash-sandbox-smoke.autorun run the build's voice workflow and
    // test-room smokes with a recorded WAV (path in the trigger file, or
    // Logs/voice-dance.wav and Logs/voice-pickup-give.wav).
    [InitializeOnLoad]
    static class DemoAutomation
    {
        static double nextPoll;
        static bool compileFailed;
        // Unity restores scenes and builds its search index right after a project
        // opens or scripts reload; entering Play Mode during that logs editor errors.
        const double SettleSeconds = 8, StartupSeconds = 40;
        static string Root => Path.GetDirectoryName(Application.dataPath);
        static System.Diagnostics.Process stopping, service;
        static StreamWriter serviceLog;
        static double restartDeadline;

        static DemoAutomation()
        {
            nextPoll = EditorApplication.timeSinceStartup + SettleSeconds;
            EditorApplication.update += Poll;
            CompilationPipeline.assemblyCompilationFinished += Compiled;
        }

        static void Note(string file, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "Logs"));
                File.AppendAllText(Path.Combine(Root, "Logs", file), DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            }
            catch (IOException) { }
        }

        // A failed compile keeps the previous code loaded, so nothing runs on it.
        static void Compiled(string assembly, CompilerMessage[] messages)
        {
            var errors = messages.Where(m => m.type == CompilerMessageType.Error).ToArray();
            if (errors.Length == 0) return;
            compileFailed = true;
            Note("brainwash-compile.log", assembly + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  " + e.file + ":" + e.line + " " + e.message)));
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.timeSinceStartup < StartupSeconds) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string smoke = Path.Combine(Root, "Library", "brainwash-demo-smoke.autorun");
            string build = Path.Combine(Root, "Library", "brainwash-build.autorun");
            string player = Path.Combine(Root, "Library", "brainwash-player-smoke.autorun");
            string classic = Path.Combine(Root, "Library", "brainwash-classic-smoke.autorun");
            string restart = Path.Combine(Root, "Library", "brainwash-service-restart.autorun");
            if (stopping != null) { ContinueRestart(); return; }
            if (File.Exists(restart)) { File.Delete(restart); BeginRestart(); return; }
            string voice = Path.Combine(Root, "Library", "brainwash-voice-smoke.autorun");
            string sandbox = Path.Combine(Root, "Library", "brainwash-sandbox-smoke.autorun");
            bool runSmoke = File.Exists(smoke), runBuild = File.Exists(build), runPlayer = File.Exists(player), runClassic = File.Exists(classic);
            bool runVoice = File.Exists(voice), runSandbox = File.Exists(sandbox);
            if (!runSmoke && !runBuild && !runPlayer && !runClassic && !runVoice && !runSandbox) return;
            // Pick up files copied in while the editor was in the background; run
            // after the recompile and reload that follow.
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling) return;
            string trigger = runSmoke ? smoke : runBuild ? build : runPlayer ? player : runClassic ? classic : runVoice ? voice : sandbox;
            string wav = "";
            try { wav = File.ReadAllText(trigger).Trim(); } catch (IOException) { }
            File.Delete(trigger);
            if (compileFailed) { Note("brainwash-autorun.log", "skipped: scripts have compile errors (Logs/brainwash-compile.log)"); return; }
            if (runSmoke)
            {
                Note("brainwash-autorun.log", "demo smoke started");
                SandboxBuild.RunDemoSmoke();
                return;
            }
            if (!runBuild)
            {
                string exe = Path.Combine(Root, "Builds", "Windows", "BrainwashSandbox.exe");
                if (!File.Exists(exe)) { Note("brainwash-autorun.log", "player smoke skipped: no build at " + exe); return; }
                // One player at a time: close any instance left from an earlier run.
                foreach (var running in System.Diagnostics.Process.GetProcessesByName("BrainwashSandbox"))
                    using (running) { try { running.Kill(); running.WaitForExit(5000); } catch (Exception) { } }
                string logs = Path.Combine(Root, "Logs");
                string name = runPlayer ? "brainwash-player-smoke" : runClassic ? "brainwash-classic-smoke" : runVoice ? "brainwash-voice-smoke" : "brainwash-sandbox-smoke";
                string flag = runPlayer ? "-brainwash-demo-smoke" : runClassic ? "-brainwash-play-smoke" : runVoice ? "-brainwash-voice-smoke" : "-brainwash-smoke";
                if (wav.Length == 0) wav = Path.Combine(logs, runVoice ? "voice-dance.wav" : "voice-pickup-give.wav");
                string extra = runVoice ? " -brainwash-voice-wav \"" + wav + "\"" : runSandbox ? " -brainwash-smoke-wav \"" + wav + "\"" : "";
                var info = new System.Diagnostics.ProcessStartInfo(exe,
                    flag + " \"" + Path.Combine(logs, name + ".json") + "\"" + extra + " -logFile \"" + Path.Combine(logs, name + ".log") + "\"")
                    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                System.Diagnostics.Process.Start(info)?.Dispose();
                Note("brainwash-autorun.log", "player " + name.Replace("brainwash-", "").Replace("-", " ") + " started");
                return;
            }
            Note("brainwash-autorun.log", "Windows build started");
            try { SandboxBuild.BuildWindows(); Note("brainwash-autorun.log", "Windows build succeeded"); }
            catch (Exception exception) { Note("brainwash-autorun.log", "Windows build failed: " + exception.Message); }
        }

        static void BeginRestart()
        {
            string root = LocalServiceLauncher.RepositoryRoot();
            if (root == null) { Note("brainwash-autorun.log", "service restart skipped: no prototype/start-unity-service.ps1 above the project"); return; }
            try
            {
                stopping = LocalServiceLauncher.StartLogged(LocalServiceLauncher.ScriptInfo(root, 4174, true), root, out var log);
                log.Dispose();
                restartDeadline = EditorApplication.timeSinceStartup + 30;
                Note("brainwash-autorun.log", "service restart: stopping the running service");
            }
            catch (Exception exception) { stopping = null; Note("brainwash-autorun.log", "service restart failed to stop: " + exception.Message); }
        }

        // Starts the new service once the old one has let go of the port.
        static void ContinueRestart()
        {
            bool portFree;
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                    portFree = !client.ConnectAsync("127.0.0.1", 4174).Wait(300) || !client.Connected;
            }
            catch (Exception) { portFree = true; }
            if (!portFree && EditorApplication.timeSinceStartup < restartDeadline) return;
            try { stopping?.Dispose(); } catch (Exception) { }
            stopping = null;
            string root = LocalServiceLauncher.RepositoryRoot();
            try
            {
                serviceLog?.Dispose();
                service = LocalServiceLauncher.StartLogged(LocalServiceLauncher.ScriptInfo(root, 4174, false), root, out serviceLog);
                Note("brainwash-autorun.log", "service restart: started" + (portFree ? "" : " (the old service had not released port 4174)"));
            }
            catch (Exception exception) { Note("brainwash-autorun.log", "service restart failed to start: " + exception.Message); }
        }
    }
}
