using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Agent.Attachments;

/// <summary>
/// In-memory default — same honest banner as the conversation store:
/// attachment bytes are process-local and lost on restart, and the store is
/// bounded (<see cref="MaxStoredAttachments"/>, oldest evicted first) so a
/// chatty session cannot grow the heap without limit. Both loss modes are
/// SAFE by construction: a reference whose bytes are gone hydrates as the
/// "[Attachment unavailable]" placeholder (a tested degradation), never as a
/// failed turn. Production: implement <see cref="IAttachmentStore"/> over
/// blob storage (see the seam's doc comment).
/// </summary>
public sealed class InMemoryAttachmentStore(IOptions<AgentOptions> options) : IAttachmentStore
{
    internal const int MaxStoredAttachments = 256;
    private const int MaxFileNameLength = 128;

    private readonly ConcurrentDictionary<string, StoredAgentAttachment> _attachments = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _evictionOrder = new();

    public Task<AgentAttachmentRef> SaveAsync(
        string fileName, string mediaType, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // Defense in depth: the upload endpoint already mapped these to typed
        // ProblemDetails; a future caller that skips it still cannot store an
        // oversized or disallowed blob.
        var limits = options.Value.Attachments;
        var normalizedMediaType = AgentAttachmentOptions.NormalizeMediaType(mediaType)
            ?? throw new ArgumentException($"'{mediaType}' is not a usable media type.", nameof(mediaType));
        if (!limits.IsAllowedContentType(normalizedMediaType))
        {
            throw new ArgumentException(
                $"Media type '{normalizedMediaType}' is outside Agent:Attachments:AllowedContentTypes.",
                nameof(mediaType));
        }

        if (data.Length > limits.MaxBytes)
        {
            throw new ArgumentException(
                $"Attachment is {data.Length} bytes; Agent:Attachments:MaxBytes is {limits.MaxBytes}.",
                nameof(data));
        }

        var attachmentId = Guid.NewGuid().ToString("N");
        var stored = new StoredAgentAttachment(
            attachmentId, SanitizeFileName(fileName), normalizedMediaType, data.ToArray());
        _attachments[attachmentId] = stored;
        _evictionOrder.Enqueue(attachmentId);
        while (_attachments.Count > MaxStoredAttachments && _evictionOrder.TryDequeue(out var oldest))
        {
            _attachments.TryRemove(oldest, out _);
        }

        return Task.FromResult(new AgentAttachmentRef(attachmentId, stored.FileName, stored.MediaType));
    }

    public Task<StoredAgentAttachment?> GetAsync(string attachmentId, CancellationToken cancellationToken) =>
        Task.FromResult(_attachments.GetValueOrDefault(attachmentId));

    public bool Exists(string attachmentId) => _attachments.ContainsKey(attachmentId);

    /// <summary>
    /// The file name is attacker-controlled and travels into prompt frame
    /// headers, JSON snapshots and logs: strip any path, replace control
    /// characters (a newline could otherwise fake a new header line), cap the
    /// length. The boundary nonce — not this sanitation — is what makes the
    /// frame unforgeable; this just keeps names displayable.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        var lastSeparator = fileName.LastIndexOfAny(['/', '\\']);
        var name = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;
        Span<char> buffer = stackalloc char[Math.Min(name.Length, MaxFileNameLength)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = name[i];
            buffer[i] = char.IsControl(c) || c is '"' ? ' ' : c;
        }

        var sanitized = buffer.Trim().ToString();
        return sanitized.Length > 0 ? sanitized : "attachment";
    }
}
