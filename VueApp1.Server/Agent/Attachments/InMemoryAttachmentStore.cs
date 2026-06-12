using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Agent.Attachments;

/// <summary>
/// In-memory default — same honest banner as the conversation store:
/// attachment bytes are process-local and lost on restart, and the store is
/// bounded on BOTH axes (entry count via <see cref="MaxStoredAttachments"/>,
/// total bytes via <see cref="MaxTotalBytesMultiple"/> × MaxBytes; oldest
/// evicted first). The byte budget is the load-bearing one: a count cap
/// alone is NOT a heap bound — 256 entries × the 5 MiB default MaxBytes is
/// ~1.3 GiB, an OOM kill for a small container, reachable by an
/// unauthenticated client well inside the default rate limit once the
/// module is enabled. Worst-case heap here is MaxBytes × 16 (80 MiB at the
/// default). Both loss modes are SAFE by construction: a reference whose
/// bytes are gone hydrates as the "[Attachment unavailable]" placeholder (a
/// tested degradation), never as a failed turn. Production: implement
/// <see cref="IAttachmentStore"/> over blob storage (see the seam's doc
/// comment).
/// </summary>
public sealed class InMemoryAttachmentStore(IOptions<AgentOptions> options) : IAttachmentStore
{
    internal const int MaxStoredAttachments = 256;

    /// <summary>
    /// Total-bytes budget as a multiple of the per-attachment cap, so the
    /// heap ceiling scales with the knob an operator actually sets. Always
    /// at least one max-size attachment: a save can evict every OTHER entry,
    /// never itself.
    /// </summary>
    internal const int MaxTotalBytesMultiple = 16;

    private const int MaxFileNameLength = 128;

    private readonly ConcurrentDictionary<string, StoredAgentAttachment> _attachments = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _evictionOrder = new();
    private long _totalBytes;

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
        Interlocked.Add(ref _totalBytes, stored.Data.Length);

        // Evict oldest-first until under BOTH bounds. Concurrent saves may
        // transiently over-evict by an entry — acceptable for a bounded
        // best-effort cache whose misses degrade to the tested placeholder.
        var maxTotalBytes = limits.MaxBytes * MaxTotalBytesMultiple;
        while ((_attachments.Count > MaxStoredAttachments || Interlocked.Read(ref _totalBytes) > maxTotalBytes)
            && _evictionOrder.TryDequeue(out var oldest))
        {
            if (_attachments.TryRemove(oldest, out var evicted))
            {
                Interlocked.Add(ref _totalBytes, -evicted.Data.Length);
            }
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
