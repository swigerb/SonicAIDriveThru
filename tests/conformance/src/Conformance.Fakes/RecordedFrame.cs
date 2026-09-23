using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>
/// A single JSON frame observed by a fake, tagged with arrival order and wall-clock time.
/// </summary>
/// <param name="Sequence">Monotonically increasing 0-based arrival index within the owning <see cref="FrameLog"/>.</param>
/// <param name="Type">The frame's "type" discriminator (e.g. "session.update"), or "" if absent/not an object.</param>
/// <param name="Json">The parsed frame body.</param>
/// <param name="ReceivedAt">Wall-clock arrival time, from the owning fake's <see cref="TimeProvider"/>.</param>
public sealed record RecordedFrame(int Sequence, string Type, JsonElement Json, DateTimeOffset ReceivedAt)
{
    public override string ToString() => $"#{Sequence} [{ReceivedAt:O}] {Type}: {Json.GetRawText()}";
}
