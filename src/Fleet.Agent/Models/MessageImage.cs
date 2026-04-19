namespace Fleet.Agent.Models;

/// <summary>A single image attachment within an incoming message.</summary>
public sealed record MessageImage(byte[] Bytes, string MimeType);
