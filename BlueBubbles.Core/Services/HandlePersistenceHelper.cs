using BlueBubbles.Core.Data;
using BlueBubbles.Core.Data.Entities;
using BlueBubbles.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Core.Services;

/// <summary>The single writer of handle rows and chat-participant links. Every path that can
/// create a <see cref="HandleEntity"/> or link one to a chat — full sync, incremental sync,
/// chat create/update, live socket message save, window upsert — goes through this file, so the
/// server-field set and the identity rule have exactly one definition. Identity is
/// (Address, Service) for a handle and (ChatId, HandleId) for a link, on every path. Two adds
/// that miss each other's field list is the shape of B2; two identity schemes is the shape of B7.
/// <para>Nothing on <see cref="HandleEntity"/> or <see cref="ChatParticipant"/> is client-owned —
/// every column maps to a field on the server's <see cref="Handle"/> payload — so unlike
/// <c>ChatFieldMerge</c> there is no ownership split to preserve here. What must be preserved is
/// the <em>no-clobber</em> rule: a sparse payload (a chat's participant list carries identity
/// only) must not blank metadata a full sync already stored, which is why only the sync path
/// passes <c>refreshExisting: true</c>.</para></summary>
internal static class HandlePersistenceHelper
{
    private static string CacheKey(Handle handle) => handle.Address + "|" + handle.Service;

    /// <summary>Copies the server-owned fields of <paramref name="handle"/> onto
    /// <paramref name="entity"/>. The one definition of what a handle row holds: every writer
    /// below calls it, so a field can no longer be written on one path and forgotten on another.</summary>
    private static void ApplyServerFields(HandleEntity entity, Handle handle)
    {
        entity.OriginalRowId = handle.OriginalRowId;
        entity.Address = handle.Address;
        entity.Service = handle.Service;
        entity.Country = handle.Country;
        entity.FormattedAddress = handle.FormattedAddress;
        entity.Color = handle.Color;
        entity.UniqueAddressAndService = handle.UniqueAddressAndService;
        entity.DefaultPhone = handle.DefaultPhone;
        entity.DefaultEmail = handle.DefaultEmail;
    }

    /// <summary>Resolves <paramref name="handle"/> to a row id, inserting it if the cache has
    /// never seen it. <paramref name="refreshExisting"/> re-applies the server fields to a row
    /// that already exists — only the sync paths do that, because only they are given a complete
    /// payload. Saves before returning so the id is real.</summary>
    /// <param name="cache">Optional per-run (Address|Service) -> id memo. A hit short-circuits
    /// entirely, matching what each caller did before this file existed.</param>
    public static async Task<int> EnsureHandleAsync(
        BlueBubblesDbContext db, Handle handle, Dictionary<string, int>? cache,
        bool refreshExisting, CancellationToken ct = default)
    {
        var key = CacheKey(handle);
        if (cache is not null && cache.TryGetValue(key, out var cachedId) && cachedId > 0)
            return cachedId;

        var entity = await db.Handles.FirstOrDefaultAsync(
            h => h.Address == handle.Address && h.Service == handle.Service, ct);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new HandleEntity();
            db.Handles.Add(entity);
        }

        if (isNew || refreshExisting)
        {
            ApplyServerFields(entity, handle);
            await db.SaveChangesAsync(ct);
        }

        if (cache is not null) cache[key] = entity.Id;
        return entity.Id;
    }

    /// <summary>Links a handle to a chat unless the link already exists, and reports whether it
    /// added anything. Caller owns the surrounding <see cref="DbContext.SaveChangesAsync()"/>.
    /// The local-tracker check matters: a payload that names the same participant twice would
    /// otherwise queue two rows with the same composite key and fail the save.</summary>
    public static async Task<bool> LinkParticipantAsync(
        BlueBubblesDbContext db, int chatId, int handleId, CancellationToken ct = default)
    {
        if (db.ChatParticipants.Local.Any(cp => cp.ChatId == chatId && cp.HandleId == handleId))
            return false;
        if (await db.ChatParticipants.AnyAsync(cp => cp.ChatId == chatId && cp.HandleId == handleId, ct))
            return false;

        db.ChatParticipants.Add(new ChatParticipant { ChatId = chatId, HandleId = handleId });
        return true;
    }

    /// <summary>Upserts each participant and links it to the chat. Caller owns the surrounding
    /// save.</summary>
    public static async Task<bool> LinkParticipantsAsync(
        BlueBubblesDbContext db, int chatId, IEnumerable<Handle> participants,
        Dictionary<string, int>? cache = null, bool refreshExisting = false,
        CancellationToken ct = default)
    {
        var added = false;
        foreach (var h in participants)
        {
            var handleId = await EnsureHandleAsync(db, h, cache, refreshExisting, ct);
            added |= await LinkParticipantAsync(db, chatId, handleId, ct);
        }
        return added;
    }

    /// <summary>Revokes membership for every stored participant the payload omits. The server
    /// loads participants fresh for chat-update events, so its list is the whole membership and
    /// a handle it leaves out has left the chat. Adding without this means a participant-removed
    /// event never lands.</summary>
    public static void RemoveParticipantsMissingFrom(
        BlueBubblesDbContext db, ChatEntity chat, IEnumerable<Handle> participants)
    {
        var keep = participants
            .Select(h => CacheKey(h))
            .ToHashSet();
        var stale = chat.ChatParticipants
            .Where(cp => cp.Handle is not null &&
                         !keep.Contains(cp.Handle.Address + "|" + cp.Handle.Service))
            .ToList();
        if (stale.Count > 0)
            db.ChatParticipants.RemoveRange(stale);
    }
}
