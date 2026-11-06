using System;
using Brainwash;

// Exercises the production client's health-response ordering guard without a
// Unity player, network service, microphone or model call.
internal static class InferenceServiceGateProbe
{
    static int checks;
    static void Require(bool pass, string name)
    { if (!pass) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

    static void CheckFailure(long status, bool transportFailure, string description)
    {
        var gate = new InferenceServiceGate();
        int stalePoll = gate.Revision;
        gate.ObserveFailure(status, transportFailure);
        Require(gate.Busy, description + " blocks immediate reacquisition");
        gate.ObserveHealth(stalePoll, false);
        Require(gate.Busy, description + " cannot be cleared by an older idle poll");
        gate.ObserveHealth(gate.Revision, true);
        Require(gate.Busy, description + " stays blocked while a fresh poll reports busy");
        gate.ObserveHealth(gate.Revision, false);
        Require(!gate.Busy, description + " permits another cast after a fresh idle poll");
    }

    static void Main()
    {
        CheckFailure(504, false, "deadline response");
        CheckFailure(409, false, "busy response");
        CheckFailure(0, true, "connection failure");
        CheckFailure(200, true, "download processing failure");

        var gate = new InferenceServiceGate();
        int beforeCancel = gate.Revision;
        gate.CancelledInFlight();
        gate.ObserveHealth(beforeCancel, false);
        Require(gate.Busy, "cancel invalidates the already-running idle poll");
        int afterCancel = gate.Revision;
        gate.ObserveHealth(afterCancel, false);
        Require(!gate.Busy, "fresh idle poll releases cancellation drain");

        gate.ObserveHealth(gate.Revision, true);
        int beforeSuccess = gate.Revision;
        gate.CompletedSuccessfully();
        Require(!gate.Busy, "successful command immediately opens the gate");
        gate.ObserveHealth(beforeSuccess, true);
        Require(!gate.Busy, "older busy poll cannot revive busy after success");

        int betweenCommands = gate.Revision;
        gate.ObserveFailure(504, false);
        gate.ObserveHealth(betweenCommands, false);
        Require(gate.Busy, "another failed command invalidates polls from the previous command");

        var rejected = new InferenceServiceGate();
        rejected.ObserveFailure(400, false);
        Require(!rejected.Busy, "settled invalid-input response does not invent a drain");
        Console.WriteLine(checks + " inference service gate checks passed.");
    }
}
