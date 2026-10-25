using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Strands.Agents;

/// <summary>Stores .NET snapshots using hashed session keys and atomic file replacement.</summary>
/// <remarks>Concurrent agents using one session use last-writer-wins semantics. Use an application-level lease when sharing a session.</remarks>
public sealed class FileSessionStore : IDeletableSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    /// <summary>Creates a store rooted at an absolute resolution of the supplied directory.</summary>
    /// <remarks>Windows file operations use extended absolute paths without changing process-wide path settings.</remarks>
    /// <param name="directory">The snapshot directory, created when a session is saved.</param>
    /// <exception cref="ArgumentException">The directory is blank or cannot be resolved.</exception>
    public FileSessionStore(string directory)
    {
        Guard.NotNullOrWhiteSpace(directory, nameof(directory));
        _directory = Path.GetFullPath(directory);
        if (Path.DirectorySeparatorChar == '\\' && !_directory.StartsWith(@"\\?\", StringComparison.Ordinal))
            _directory = _directory.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + _directory.Substring(2) : @"\\?\" + _directory;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The snapshot schema or conversation is invalid.</exception>
    /// <exception cref="JsonException">The stored JSON cannot be deserialized.</exception>
    public async ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
            var snapshot = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The session snapshot cannot be null.");
            return Validate(snapshot);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The snapshot schema or conversation is invalid.</exception>
    public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        snapshot = Validate(snapshot);
        var path = GetPath(sessionId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else
            {
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temporary, path, null); }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Cancellation is checked before synchronous filesystem deletion. A committed deletion is not rolled back by later cancellation.</remarks>
    public ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        return default;
    }

    private string GetPath(string sessionId)
    {
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));
        using var hash = SHA256.Create();
        var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sessionId))).Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(_directory, key + ".json");
    }

    private static SessionSnapshot Validate(SessionSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not (1 or 2)) throw new InvalidDataException($"Unsupported .NET session schema version: {snapshot.SchemaVersion}.");
        if (snapshot.Messages is null || snapshot.Messages.Any(message => message is null))
            throw new InvalidDataException("The session must contain a nonnull message list.");
        if (snapshot.AgentId is not null && string.IsNullOrWhiteSpace(snapshot.AgentId)) throw new InvalidDataException("A stored agent identity cannot be blank.");
        if (snapshot.SchemaVersion == 2)
        {
            try
            {
                snapshot = snapshot with
                {
                    Messages = Array.AsReadOnly(snapshot.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray()),
                    ModelState = snapshot.ModelState is null ? null : AgentSnapshot.CloneValues(snapshot.ModelState, nameof(snapshot.ModelState))
                };
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException("Receipt history and provider state must contain valid, detachable values.", error);
            }
        }
        snapshot.ValidateHistoryProfile();
        ConversationHistory.Validate(snapshot.Messages, snapshot.HistoryProfile);
        snapshot.ValidateModelHistory();
        snapshot.ValidateModelInputReceipt();
        snapshot.InterruptState?.ValidateHistory(snapshot.Messages, allowIndependent: true, independentOwnerProfile: snapshot.HistoryProfile == ModelHistoryBehavior.Native ? (ModelHistoryBehavior?)null : snapshot.HistoryProfile);
        var stash = Context.StashCheckpoint.Prepare(snapshot.ContextStash, snapshot.ContextStashStorageType);
        return snapshot with
        {
            SystemPromptContent = snapshot.CopyValidatedSystemPromptContent(allowGuardContent: true),
            ContextStash = stash?.Entries,
            ConversationManagerState = snapshot.ConversationManagerState is { ValueKind: not System.Text.Json.JsonValueKind.Null } state ? Conversation.ConversationManager.ValidateEnvelope(state) : null
        };
    }
}
