namespace Brainwash
{
    public enum VoiceInputSource { None, KeyboardHold, PointerHold, Toggle }

    // One physical press owns one recording. All submission paths consume that
    // ownership before encoding or networking, so subsequent releases are inert.
    public sealed class VoiceInputState
    {
        public VoiceInputSource Source { get; private set; }
        public bool Active => Source != VoiceInputSource.None;
        public bool TryBegin(VoiceInputSource source)
        {
            if (source == VoiceInputSource.None || Active) return false;
            Source = source; return true;
        }
        public bool TryFinish(VoiceInputSource source)
        {
            if (!Active || Source != source) return false;
            Source = VoiceInputSource.None; return true;
        }
        public bool TryFinishAny() => TryFinish(Source);
        public void Cancel() { Source = VoiceInputSource.None; }
    }
}
