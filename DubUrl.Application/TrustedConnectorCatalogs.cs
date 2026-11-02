using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DubUrl.Core.ProviderPacks;

namespace DubUrl.Application;

/// <summary>An administrator-approved source with an explicit catalog trust policy.</summary>
public sealed class ConnectorCatalogSource
{
    public required string Id { get; init; }
    public required Uri Uri { get; init; }
    public string TrustMode { get; init; } = "sha256";
    public string? Sha256 { get; init; }
    public Uri? SignatureUri { get; init; }
    public IReadOnlyList<ConnectorCatalogSigningKey> TrustedSigningKeys { get; init; } = [];
    public long MinimumRevision { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    /// <summary>Optional environment variable holding a bearer token for the catalog request only.</summary>
    public string? BearerTokenEnvironmentVariable { get; init; }
    public override string ToString() => "Connector catalog source (location and authentication redacted)";
}

/// <summary>A locally provisioned public key. Key IDs are case-sensitive.</summary>
public sealed class ConnectorCatalogSigningKey
{
    public required string KeyId { get; init; }
    public required string PublicKeyPath { get; init; }
    public override string ToString() => "Catalog signing public key (location redacted)";
}

/// <summary>Loads independent sources, rejects ambiguity, and never retains transport exception details.</summary>
public sealed class TrustedConnectorCatalogs(HttpClient httpClient, string? revisionStatePath = null, TimeProvider? timeProvider = null)
{
    private readonly ConnectorCatalogRevisionStore _revisions = new(revisionStatePath
        ?? Path.Combine(ProviderPackPaths.GetUserStorePath(), "catalog-revisions"));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ProviderPackCatalog> LoadAsync(
        IEnumerable<ConnectorCatalogSource> sources,
        ProviderPackCatalog? official = null,
        CancellationToken cancellationToken = default)
    {
        var ordered = sources.OrderBy(source => source.Id, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "official" };
        foreach (var source in ordered)
            if (!ConnectorCatalogSignature.IsSafeName(source.Id) || !ids.Add(source.Id))
                throw new ConnectorOperationException("connector-source-invalid", "Catalog source IDs must be unique simple names; 'official' is reserved.");

        var entries = new List<ConnectorDefinition>();
        var diagnostics = new List<ProviderPackDiagnostic>();
        var signed = new List<PendingCatalog>();
        if (official is not null)
        {
            diagnostics.AddRange(official.Diagnostics);
            entries.AddRange(official.Entries.Select(entry => entry with
            {
                CatalogProvenance = new("official", entry.Provider, entry.Version, null, entry.Sha256) { TrustMode = "distribution" },
            }));
        }
        foreach (var source in ordered.Where(source => source.Enabled))
        {
            try
            {
                var loaded = await LoadSourceAsync(source, cancellationToken).ConfigureAwait(false);
                entries.AddRange(loaded.Entries);
                if (loaded.Signed is not null) signed.Add(loaded.Signed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                diagnostics.Add(new("connector-source-unavailable", $"Catalog source '{source.Id}' could not be loaded or verified."));
            }
        }

        ValidateUniqueNames(entries);
        // Never advance a high-water mark for a malformed catalog or an ambiguous merge.
        // The store compares and publishes under a cross-process lock. A concurrent newer load wins.
        await AcceptRevisionsAsync(signed, entries, diagnostics, cancellationToken).ConfigureAwait(false);
        return new ProviderPackCatalog(entries.OrderBy(entry => entry.Provider, StringComparer.Ordinal)) { Diagnostics = diagnostics };
    }

    private sealed record PendingCatalog(ConnectorCatalogSource Source, ConnectorCatalogSignature Signature, string PayloadSha256);
    private sealed record LoadedCatalog(ConnectorDefinition[] Entries, PendingCatalog? Signed);

    private async Task<LoadedCatalog> LoadSourceAsync(ConnectorCatalogSource source, CancellationToken cancellationToken)
    {
        ValidateSource(source);
        var bytes = await ReadAsync(source.Uri, source.BearerTokenEnvironmentVariable, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        ConnectorCatalogSignature? signature = null;
        string? keyFingerprint = null;
        if (source.TrustMode == "sha256" && !digest.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        if (source.TrustMode == "signed")
        {
            (signature, keyFingerprint) = await VerifySignatureAsync(source, bytes, cancellationToken).ConfigureAwait(false);
        }
        using var stream = new MemoryStream(bytes);
        // Ignore provenance claimed by the document: only the configured source establishes trust.
        var catalog = ProviderPackCatalog.Read(stream);
        ValidateConnectorNames(catalog);
        var payloadDigest = signature is null ? null
            : Convert.ToHexString(SHA256.HashData(signature.Payload(bytes))).ToLowerInvariant();
        if (signature is not null)
            await _revisions.CheckAsync(source.Id, signature.Revision, payloadDigest!, cancellationToken).ConfigureAwait(false);
        var entries = catalog.Entries.Select(entry => entry with
        {
            CatalogProvenance = new(source.Id, entry.Provider, entry.Version, digest, entry.Sha256)
            {
                TrustMode = source.TrustMode,
                SigningKeyId = signature?.KeyId,
                SigningKeySha256 = keyFingerprint,
                CatalogRevision = signature?.Revision,
            },
        }).ToArray();
        return new(entries, signature is null ? null : new(source, signature, payloadDigest!));
    }

    private static void ValidateConnectorNames(ProviderPackCatalog catalog)
    {
        if (catalog.Entries.Any(entry => !ConnectorCatalogSignature.IsSafeName(entry.Provider) || entry.Aliases is null
            || entry.Aliases.Any(alias => !ConnectorCatalogSignature.IsSafeName(alias))))
            throw new InvalidDataException();
    }

    private async Task<(ConnectorCatalogSignature Signature, string Fingerprint)> VerifySignatureAsync(
        ConnectorCatalogSource source, byte[] bytes, CancellationToken cancellationToken)
    {
        var signature = ConnectorCatalogSignature.Read(await ReadAsync(source.SignatureUri!, source.BearerTokenEnvironmentVariable, cancellationToken).ConfigureAwait(false));
        if (signature.SourceId != source.Id.ToLowerInvariant() || signature.Revision < source.MinimumRevision
            || (signature.ExpiresAt != 0 && signature.ExpiresAt <= _time.GetUtcNow().ToUnixTimeSeconds()))
            throw new InvalidDataException();
        using var key = RSA.Create();
        await ImportVerificationKeyAsync(key, source, signature.KeyId, cancellationToken).ConfigureAwait(false);
        if (!key.VerifyData(signature.Payload(bytes), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException();
        return (signature, Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant());
    }

    private static void ValidateUniqueNames(IEnumerable<ConnectorDefinition> entries)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(entry => entry.CatalogProvenance!.SourceId, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Provider, StringComparer.Ordinal))
            foreach (var name in new[] { entry.Provider }.Concat(entry.Aliases))
            {
                var source = entry.CatalogProvenance!.SourceId;
                if (!names.TryAdd(name, source))
                    throw new ConnectorOperationException("connector-catalog-collision",
                        $"Connector ID or alias '{name}' is ambiguous between catalog sources '{names[name]}' and '{source}'.");
            }
    }

    private async Task AcceptRevisionsAsync(IEnumerable<PendingCatalog> signed, List<ConnectorDefinition> entries,
        List<ProviderPackDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        foreach (var pending in signed)
        {
            try
            {
                if (pending.Signature.ExpiresAt != 0 && pending.Signature.ExpiresAt <= _time.GetUtcNow().ToUnixTimeSeconds()) throw new InvalidDataException();
                await _revisions.AcceptAsync(pending.Source.Id, pending.Signature.Revision, pending.PayloadSha256, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                entries.RemoveAll(entry => entry.CatalogProvenance!.SourceId == pending.Source.Id);
                diagnostics.Add(new("connector-source-unavailable", $"Catalog source '{pending.Source.Id}' could not be loaded or verified."));
            }
        }
    }

    private static bool IsSourceFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException or FormatException or CryptographicException;

    private static async Task ImportVerificationKeyAsync(RSA key, ConnectorCatalogSource source, string keyId, CancellationToken cancellationToken)
    {
        var named = source.TrustedSigningKeys.SingleOrDefault(candidate => candidate.KeyId == keyId);
        if (named is not null)
        {
            ConnectorCatalogSignature.ImportPublicKey(key, await File.ReadAllTextAsync(named.PublicKeyPath, cancellationToken).ConfigureAwait(false));
            return;
        }
        // A derived identity may refer to an already trusted public key configured under a legacy alias.
        // Only configured local key files establish trust; downloaded IDs never add a key.
        if (keyId.Length != 71 || !keyId.StartsWith("sha256-", StringComparison.Ordinal)) throw new InvalidDataException();
        foreach (var approved in source.TrustedSigningKeys)
        {
            ConnectorCatalogSignature.ImportPublicKey(key, await File.ReadAllTextAsync(approved.PublicKeyPath, cancellationToken).ConfigureAwait(false));
            if (ConnectorCatalogSignature.GetKeyId(key) == keyId) return;
        }
        throw new InvalidDataException();
    }

    private static void ValidateSource(ConnectorCatalogSource source)
    {
        ValidateLocation(source.Uri);
        if (source.TrustedSigningKeys is null) throw new InvalidDataException();
        switch (source.TrustMode)
        {
            case "sha256": ValidateSha256Source(source); break;
            case "unsigned": ValidateUnsignedSource(source); break;
            case "signed": ValidateSignedSource(source); break;
            default: throw new InvalidDataException();
        }
    }

    private static void ValidateSha256Source(ConnectorCatalogSource source)
    {
        if (source.Sha256 is not { Length: 64 } || !source.Sha256.All(Uri.IsHexDigit)
            || source.SignatureUri is not null || source.TrustedSigningKeys.Count != 0)
            throw new InvalidDataException();
    }

    private static void ValidateUnsignedSource(ConnectorCatalogSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Sha256) || source.SignatureUri is not null || source.TrustedSigningKeys.Count != 0)
            throw new InvalidDataException();
    }

    private static void ValidateSignedSource(ConnectorCatalogSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Sha256) || source.SignatureUri is null || source.MinimumRevision < 1
            || source.TrustedSigningKeys.Count == 0 || source.TrustedSigningKeys.Any(IsInvalidSigningKey)
            || source.TrustedSigningKeys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != source.TrustedSigningKeys.Count)
            throw new InvalidDataException();
        ValidateLocation(source.SignatureUri);
        // Catalog bearer credentials may be used for the detached signature only on the same HTTPS origin.
        if (source.Uri.Scheme != source.SignatureUri.Scheme || source.Uri.Authority != source.SignatureUri.Authority)
            throw new InvalidDataException();
    }

    private static bool IsInvalidSigningKey(ConnectorCatalogSigningKey key)
        => key is null || !ConnectorCatalogSignature.IsSafeName(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPath);

    private static void ValidateLocation(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (!uri.IsFile && uri.Scheme != Uri.UriSchemeHttps) || uri.UserInfo.Length != 0)
            throw new InvalidDataException();
    }

    private async Task<byte[]> ReadAsync(Uri uri, string? bearerVariable, CancellationToken cancellationToken)
    {
        if (uri.IsFile) return await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearerVariable is not null)
        {
            var token = Environment.GetEnvironmentVariable(bearerVariable);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
