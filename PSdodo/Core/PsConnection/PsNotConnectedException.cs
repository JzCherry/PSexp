using System;

namespace PsExporter.Core.PsConnection;

public sealed class PsNotConnectedException : Exception
{
    /// <summary>Actionable connection guidance, when retrying alone cannot repair the connection.</summary>
    public string? UserHint { get; init; }
    public PsNotConnectedException(string message) : base(message) { }
    public PsNotConnectedException(string message, Exception inner) : base(message, inner) { }
}
