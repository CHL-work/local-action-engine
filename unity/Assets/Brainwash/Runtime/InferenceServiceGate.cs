namespace Brainwash
{
    // A health response may outlive the request/cancellation it observed. Its
    // revision must still match before it can open the gate for another cast.
    public sealed class InferenceServiceGate
    {
        public bool Busy { get; private set; }
        public int Revision { get; private set; }

        public void ObserveHealth(int pollRevision, bool busy)
        {
            if (pollRevision == Revision) Busy = busy;
        }
        public void CompletedSuccessfully()
        {
            Revision++; Busy = false;
        }
        public void CancelledInFlight()
        {
            Revision++; Busy = true;
        }
        public void ObserveFailure(long responseCode, bool transportFailure)
        {
            // The service sends 504 before the current model call drains; 409
            // explicitly reports another call. A failed transport cannot prove
            // that the server stopped processing. Wait for a newer health poll.
            if (responseCode == 504 || responseCode == 409 || transportFailure)
                CancelledInFlight();
        }
    }
}
