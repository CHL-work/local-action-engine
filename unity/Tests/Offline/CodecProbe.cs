using System;
using Brainwash;

// Runs the production WAV encoder with the installed Unity managed assemblies.
// It does not initialize the Editor, capture a microphone, or load a model.
internal static class CodecProbe
{
    static int checks;

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    static void Main()
    {
        byte[] wav = VoiceCapture.EncodeWav(new[] { 1f, -1f, .5f, .5f }, 2, 16000);
        Require(wav.Length == 48 && BitConverter.ToUInt32(wav, 4) == 40 && BitConverter.ToUInt32(wav, 40) == 4, "RIFF lengths");
        Require(BitConverter.ToUInt16(wav, 22) == 1 && BitConverter.ToUInt32(wav, 24) == 16000 && BitConverter.ToUInt16(wav, 34) == 16, "mono PCM16 at 16 kHz");
        Require(BitConverter.ToInt16(wav, 44) == 0 && BitConverter.ToInt16(wav, 46) == 16384, "stereo downmix");

        byte[] clipped = VoiceCapture.EncodeWav(new[] { 2f, -2f, float.NaN, float.PositiveInfinity }, 1, 16000);
        Require(BitConverter.ToInt16(clipped, 44) == 32767 && BitConverter.ToInt16(clipped, 46) == -32768
            && BitConverter.ToInt16(clipped, 48) == 0 && BitConverter.ToInt16(clipped, 50) == 0, "clipping and nonfinite handling");

        var highRate = new float[4800];
        Array.Fill(highRate, .25f);
        byte[] resampled = VoiceCapture.EncodeWav(highRate, 1, 48000);
        Require(resampled.Length == 3244 && BitConverter.ToInt16(resampled, 44) == 8192, "48 kHz downsampling length and amplitude");
        Require(VoiceCapture.EncodeWav(new float[128000], 1, 16000).Length == 256044, "exact eight-second boundary");

        bool rejected = false;
        try { VoiceCapture.EncodeWav(new float[128001], 1, 16000); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "over-duration rejected");

        rejected = false;
        try { VoiceCapture.EncodeWav(new float[3], 2, 16000); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "incomplete interleaved frame rejected");
        Console.WriteLine("WAV codec checks passed: " + checks);
    }
}
