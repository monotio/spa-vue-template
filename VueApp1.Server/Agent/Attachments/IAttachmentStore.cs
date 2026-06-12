namespace VueApp1.Server.Agent.Attachments;

/// <summary>
/// The reference that lives ON the transcript: id + display metadata, never
/// bytes and never a provider file id (P1). It is what
/// <see cref="AgentMessageBuilder"/> stamps into the user message's
/// <c>AdditionalProperties</c>, what the replay snapshot derives its
/// <c>file</c> parts from, and what hydration resolves back into bytes on
/// EVERY turn (P2: the provider holds nothing between requests).
/// </summary>
public sealed record AgentAttachmentRef(string AttachmentId, string FileName, string MediaType);

/// <summary>A stored attachment: the reference plus its bytes. Bytes stay inside the store/builder pair.</summary>
public sealed record StoredAgentAttachment(
    string AttachmentId,
    string FileName,
    string MediaType,
    ReadOnlyMemory<byte> Data);

/// <summary>
/// The attachment blob seam. The transcript stores <see cref="AgentAttachmentRef"/>s
/// only; this store owns the bytes, and the loop re-reads them every request.
///
/// DURABLE UPGRADE (docs/AGENT.md): implement this over blob storage
/// (Azure Blob / S3) — <c>SaveAsync</c> writes the object and returns the
/// reference, <c>GetAsync</c> streams it back, <c>Exists</c> is a HEAD
/// request. Because hydration degrades a missing blob to an
/// "[Attachment unavailable]" placeholder instead of aborting the turn, the
/// swap needs no changes anywhere else — same shape as the conversation
/// store seam.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>
    /// Validates against <c>Agent:Attachments</c> caps (defense in depth —
    /// the upload endpoint already returned typed ProblemDetails for these),
    /// sanitizes the caller-controlled file name, and stores the bytes under
    /// a fresh id.
    /// </summary>
    Task<AgentAttachmentRef> SaveAsync(
        string fileName, string mediaType, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Null when the id is unknown (or evicted) — hydration degrades, never throws for this.</summary>
    Task<StoredAgentAttachment?> GetAsync(string attachmentId, CancellationToken cancellationToken);

    bool Exists(string attachmentId);
}
