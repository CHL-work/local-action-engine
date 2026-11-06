using System;
using Brainwash;

// Tests the production input-ownership state without Unity, microphone or model.
internal static class VoiceInputProbe
{
    static int checks;
    static void Require(bool pass, string name)
    { if (!pass) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static void Main()
    {
        var state = new VoiceInputState();
        Require(!state.TryBegin(VoiceInputSource.None) && !state.Active, "no-source input cannot own a capture");
        Require(state.TryBegin(VoiceInputSource.KeyboardHold) && state.Source == VoiceInputSource.KeyboardHold, "B press owns recording");
        Require(!state.TryBegin(VoiceInputSource.KeyboardHold) && !state.TryBegin(VoiceInputSource.PointerHold), "key repeat and another pointer cannot acquire twice");
        Require(!state.TryFinish(VoiceInputSource.PointerHold) && state.Active, "unrelated pointer release cannot send B recording");
        Require(state.TryFinish(VoiceInputSource.KeyboardHold) && !state.Active
            && !state.TryFinish(VoiceInputSource.KeyboardHold), "B release consumes exactly once");
        state.TryBegin(VoiceInputSource.PointerHold);
        Require(state.TryFinishAny() && !state.TryFinish(VoiceInputSource.PointerHold), "automatic cutoff prevents later pointer release from sending twice");
        state.TryBegin(VoiceInputSource.Toggle);
        Require(!state.TryFinish(VoiceInputSource.KeyboardHold) && state.TryFinish(VoiceInputSource.Toggle), "toggle capture is independent of B release");
        state.TryBegin(VoiceInputSource.KeyboardHold); state.Cancel();
        Require(!state.Active && !state.TryFinishAny() && !state.TryFinish(VoiceInputSource.KeyboardHold), "cancel reset and focus cleanup leave no sendable owner");
        Require(state.TryBegin(VoiceInputSource.KeyboardHold) && state.TryFinish(VoiceInputSource.KeyboardHold), "fresh hold works after cancellation");
        Console.WriteLine(checks + " voice input ownership checks passed.");
    }
}
