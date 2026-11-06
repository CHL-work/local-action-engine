using System;
using System.IO;
using UnityEngine;

namespace Brainwash
{
    public interface IVoiceRecorder
    {
        bool Recording { get; }
        float Elapsed { get; }
        bool Begin(out string error);
        // One window of the recording so far, from and to in seconds after Begin,
        // without stopping it. Null when the window is too short to hold a word.
        byte[] Slice(float fromSeconds, float toSeconds);
        byte[] Finish();
        void Cancel();
    }

    // Exactly one local microphone capture; no team-voice middleware is needed in solo.
    public sealed class VoiceCapture : MonoBehaviour, IVoiceRecorder
    {
        public const int MaxSeconds = 8;
        // A listener window shorter than this cannot hold a spoken word.
        public const float MinSliceSeconds = .3f;
        public bool Recording { get; private set; }
        public float Elapsed => Recording ? Time.realtimeSinceStartup - started : 0;
        public string Device { get; set; }
        public string DeviceLabel => string.IsNullOrEmpty(Device) ? "System default (Windows input)" : Device;
        public string RecordingDeviceLabel { get; private set; }
        public float Level { get; private set; }
        public float LastCaptureRms { get; private set; }
        public string Feedback { get; private set; } = "Hold B and speak; release to send.";
        public string[] Devices => Microphone.devices;
        AudioClip clip;
        string recordingDevice;
        float started;
        float nextMeter;
        float[] meterSamples;

        public bool Begin(out string error)
        {
            error = null;
            if (Recording) { error = "Already recording."; return false; }
            if (Microphone.devices.Length == 0) { error = "No microphone detected. Use typed commands or connect a microphone."; Feedback = error; return false; }
            try
            {
                if (!string.IsNullOrEmpty(Device) && Array.IndexOf(Microphone.devices, Device) < 0)
                { error = "The selected microphone is unavailable. Choose System default or a connected device."; Feedback = error; return false; }
                // Unity's null device selects the OS default input; devices[0]
                // is merely the first enumerated device and may be unrelated.
                recordingDevice = string.IsNullOrEmpty(Device) ? null : Device;
                RecordingDeviceLabel = DeviceLabel;
                clip = Microphone.Start(recordingDevice, false, MaxSeconds + 1, 16000);
                if (clip == null) { error = "Microphone could not start. Check Windows microphone access."; Feedback = error; return false; }
                started = Time.realtimeSinceStartup;
                Recording = true; Level = 0; LastCaptureRms = 0; nextMeter = 0;
                meterSamples = new float[256 * clip.channels];
                Feedback = "Recording from " + RecordingDeviceLabel;
                return true;
            }
            catch (Exception e) { error = "Microphone: " + e.Message; Feedback = error; Cancel(); return false; }
        }

        public byte[] Finish()
        {
            if (!Recording || clip == null) throw new InvalidOperationException("Microphone is not recording.");
            try
            {
                int frames = Mathf.Min(Microphone.GetPosition(recordingDevice), MaxSeconds * clip.frequency);
                if (frames <= 0) throw new InvalidOperationException("Microphone returned no samples. Check the selected device and Windows microphone access.");
                float[] samples = new float[frames * clip.channels];
                if (!clip.GetData(samples, 0)) throw new InvalidOperationException("Could not read microphone samples.");
                LastCaptureRms = Rms(samples);
                byte[] wav = EncodeCapturedWav(samples, clip.channels, clip.frequency);
                Feedback = LastCaptureRms < .001f ? "Quiet audio sent. Move closer to the microphone if words are missed." : "Recorded audio sent.";
                return wav;
            }
            catch (Exception e) { Feedback = e.Message; throw; }
            finally { Cancel(); }
        }

        // Each listener of a cast hears its own window of the one recording.
        public byte[] Slice(float fromSeconds, float toSeconds)
        {
            if (!Recording || clip == null) throw new InvalidOperationException("Microphone is not recording.");
            int available = Mathf.Min(Microphone.GetPosition(recordingDevice), MaxSeconds * clip.frequency);
            int from = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0, fromSeconds) * clip.frequency), 0, available);
            int to = Mathf.Clamp(Mathf.CeilToInt(toSeconds * clip.frequency), from, available);
            if (to - from < MinSliceSeconds * clip.frequency) return null;
            float[] samples = new float[(to - from) * clip.channels];
            if (!clip.GetData(samples, from)) throw new InvalidOperationException("Could not read microphone samples.");
            LastCaptureRms = Rms(samples);
            Feedback = LastCaptureRms < .001f ? "Quiet audio sent. Move closer to the microphone if words are missed." : "Recorded audio sent.";
            return EncodeCapturedWav(samples, clip.channels, clip.frequency);
        }

        void Update()
        {
            if (!Recording || clip == null || Time.realtimeSinceStartup < nextMeter) return;
            nextMeter = Time.realtimeSinceStartup + .05f;
            try
            {
                int frames = Microphone.GetPosition(recordingDevice);
                if (frames < 256) { Level = 0; return; }
                if (clip.GetData(meterSamples, frames - 256)) Level = Rms(meterSamples);
                if (Elapsed > .6f) Feedback = Level < .0001f ? "No input level. Check device / mute." : "Microphone signal detected.";
            }
            catch (Exception e) { Feedback = "Microphone input stopped: " + e.Message; Cancel(); }
        }

        public static float Rms(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            double square = 0;
            foreach (float value in samples) if (!float.IsNaN(value) && !float.IsInfinity(value)) square += (double)value * value;
            return (float)Math.Sqrt(square / samples.Length);
        }
        public static byte[] EncodeCapturedWav(float[] samples, int channels, int sampleRate)
        {
            byte[] wav = EncodeWav(samples, channels, sampleRate);
            // Check the actual mono PCM after resampling/downmixing. Only an
            // all-zero recording is rejected here; quiet speech is preserved.
            for (int i = 44; i < wav.Length; i++) if (wav[i] != 0) return wav;
            throw new InvalidOperationException("No microphone signal was captured. Check the input device, mute switch and Windows microphone access.");
        }

        public void Cancel()
        {
            try { if (Recording || clip != null) Microphone.End(recordingDevice); }
            catch (Exception e) { Feedback = "Microphone cleanup: " + e.Message; }
            finally
            {
                Recording = false;
                if (clip != null) Destroy(clip);
                clip = null; meterSamples = null; Level = 0;
            }
        }

        public static byte[] EncodeWav(float[] interleaved, int channels, int sampleRate)
        {
            if (channels < 1 || sampleRate < 8000 || sampleRate > 96000 || interleaved == null || interleaved.Length == 0 || interleaved.Length % channels != 0)
                throw new ArgumentException("Invalid PCM samples.");
            int sourceFrames = interleaved.Length / channels;
            if (sourceFrames > sampleRate * MaxSeconds) throw new ArgumentException("Audio exceeds eight seconds.");
            const int rate = 16000;
            int frames = Mathf.Max(1, (int)((long)sourceFrames * rate / sampleRate));
            using var stream = new MemoryStream(44 + frames * 2);
            using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
            for (int i = 0; i < frames; i++)
            {
                // Average source frames for downsampling and stereo-to-mono conversion.
                int from = (int)((long)i * sampleRate / rate);
                int to = Math.Min(sourceFrames, Math.Max(from + 1, (int)((long)(i + 1) * sampleRate / rate)));
                double sum = 0;
                for (int j = from; j < to; j++) for (int c = 0; c < channels; c++)
                {
                    float value = interleaved[j * channels + c];
                    sum += float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;
                }
                float sample = Mathf.Clamp((float)(sum / ((to - from) * channels)), -1, 1);
                writer.Write((short)Mathf.RoundToInt(sample * (sample < 0 ? 32768 : 32767)));
            }
            return stream.ToArray();
        }
        void OnDisable() => Cancel();
    }
}
