using System.Numerics;

namespace Meitou.Net.Replication;

/// <summary>
/// Where a client's camera is: the host picks which characters and events to send from it (docs/net.md, "Interest"). <see cref="DrawDistance"/> is
/// the renderer's character draw distance. It travels in <c>ClientFrame</c>; the type lives here because the event filter needs it too.
/// </summary>
public readonly record struct ViewState(Vector3 Eye, Vector3 Focus, float DrawDistance);
