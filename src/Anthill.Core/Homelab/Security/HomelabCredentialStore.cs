using Anthill.Core.Security;

namespace Anthill.Core.Homelab.Security;

/// <summary>
/// Credential Store abstraction for homelab providers (v1.9.0, NORTH_STAR D2). Built on the
/// existing <see cref="FieldCipher"/> encryption:
/// - Secrets are write-only through the API: saved encrypted, never returned.
/// - Status listings expose only id/kind/host/configured/last_verified.
/// - Every secret use writes an audit HomelabEvent (event_type "credential_used").
/// - GetSecret exists for deterministic providers only — never hand a secret to an LLM prompt.
/// </summary>
public sealed class HomelabCredentialStore : ICredentialProvider
{
    private readonly HomelabRepository _repository;
    private readonly FieldCipher _cipher;

    public HomelabCredentialStore(HomelabRepository repository, FieldCipher? cipher = null)
    {
        _repository = repository;
        _cipher = cipher ?? FieldCipher.CreateDefault();
    }

    public void SaveCredential(string id, string kind, string targetHost, string secret, string savedBy)
    {
        var key = Normalize(id);
        if (key.Length == 0) throw new ArgumentException("Credential id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Credential secret is required.", nameof(secret));
        _repository.UpsertCredentialRow(key, (kind ?? "").Trim(), (targetHost ?? "").Trim(), _cipher.Protect(secret) ?? "");
        _repository.RecordChange(new ChangeRecord
        {
            SubjectKind = "credential", SubjectId = key, ChangeKind = "updated",
            Summary = $"Credential '{key}' saved (secret write-only)", ChangedBy = savedBy,
        });
    }

    public string? GetSecret(string id, string usedBy)
    {
        var key = Normalize(id);
        var stored = _repository.GetCredentialSecretProtected(key);
        // Audit every use attempt, hit or miss — the message carries the credential id and the
        // consumer, never any secret material.
        _repository.RecordEvent(new HomelabEvent
        {
            EventType = "credential_used", SubjectKind = "credential", SubjectId = key,
            Severity = "info",
            Message = stored is null
                ? $"Credential '{key}' requested by {usedBy} but not configured"
                : $"Credential '{key}' used by {usedBy}",
        });
        return stored is null ? null : _cipher.Unprotect(stored);
    }

    public void MarkVerified(string id) => _repository.SetCredentialVerified(Normalize(id));

    public void RemoveCredential(string id, string removedBy)
    {
        var key = Normalize(id);
        _repository.DeleteCredentialRow(key);
        _repository.RecordChange(new ChangeRecord
        {
            SubjectKind = "credential", SubjectId = key, ChangeKind = "removed",
            Summary = $"Credential '{key}' removed", ChangedBy = removedBy,
        });
    }

    public IReadOnlyList<CredentialRecord> ListStatuses() => _repository.ListCredentialRows();

    private static string Normalize(string? id) => (id ?? "").Trim().ToLowerInvariant();
}
