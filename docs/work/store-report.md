# SettingsStore multi-instance persistence fix

The original temporary-directory reproduction failed: after A changed the provider/key, stale B saved its window position, overwrote `CredentialId` with A's old reference, and restarted with no key because A had deleted that encrypted file.

## Changes

- A per-profile Windows named mutex serializes constructor reads, saves, position updates, resets, and snapshots across processes. The name hashes the normalized absolute profile path. Abandoned ownership is recoverable; a ten-second acquisition timeout produces a retryable `IOException`.
- Position updates read the latest disk tuple, change only coordinates, atomically persist it, and publish the settings and key together under the instance lock. Failed writes publish no new state.
- Ordinary saves merge fields changed from that instance's loaded baseline into the latest disk settings. A null key means retain the latest disk key. A draft with an older credential reference also retains the latest provider endpoint/model, including when the instance has refreshed its baseline since the draft was created. Reset intentionally replaces the entire latest configuration.
- Snapshots refresh changed disk settings and their referenced immutable credential under both locks. An unchanged pointer file reuses the already validated tuple. The startup fallback for malformed configuration remains readable and can be repaired by saving.
- New credentials are encrypted into unique versioned files before publishing the atomic settings pointer. A failed settings write deletes only its unpublished staged credential. Published superseded encrypted files remain: another process or previously returned snapshot can still reference them. This includes credentials superseded by Reset; the active credential is cleared, but this fix deliberately does not introduce unsafe historical-file cleanup.
- `ScreenEnabled` is always persisted as false. Each instance preserves only its own grant while provider, key, monitor, exclusions, attachment, and periodic-observation scope remain unchanged. Observed scope changes revoke it. A stale enabled draft cannot restore a revoked grant. `Save(settings, apiKey, authorizeScreenGrant: true)` supports the UI's explicit confirmation of the exact new scope in the same save; position updates and snapshots never authorize grants.

## Verification

All checks use newly created temporary directories, synthetic keys, local in-memory HTTP handlers, and cleanup. No real profile or external API was used.

`dotnet run --project tests/StoreChecks/StoreChecks.csproj` passed **13 checks**:

1. A/B changed provider/key followed by stale position save and restart.
2. Superseded encrypted credential remains usable by an earlier snapshot.
3. Stale save without a key preserves the latest provider and unrelated local edit.
4. Snapshot refresh observes the latest coherent provider tuple.
5. Draft predating a snapshot refresh cannot mix old endpoint with new key.
6. Locked settings write rolls back settings, key, position, and staged file.
7. Session grants stay local, survive position refresh, and revoke on scope changes.
8. Stale granted draft cannot restore revoked consent.
9. Explicitly confirmed scope grants immediately and remains session-only.
10. Separate-process position worker, synchronized after loading stale state, runs alongside provider/key switches without rolling them back.
11. Concurrent snapshots preserve endpoint/model/key consistency.
12. Corrupt startup configuration remains repairable.
13. Stale Reset replaces the latest fields and clears the active key.

`dotnet run --project tests/ChatChecks/ChatChecks.csproj` passed **16 checks**, preserving the existing provider-snapshot and failed-save behavior.

The primary regression was observed failing before implementation. Additional stale-draft, corrupt-fallback, and explicit-consent regressions were also observed failing before their corresponding fixes.

## Boundaries

`Current` remains the existing mutable instance cache for compatibility. Callers needing a coherent fresh tuple must use `Snapshot`; external writes do not proactively dispatch `Changed` into other processes. Concurrent edits to the same non-provider field use the last serialized edit. Historical encrypted credentials accumulate until a future cleanup design can establish that no live process or snapshot references them. Processes must use the same normalized physical profile path; filesystem aliases or junctions are not resolved to a shared physical identity by this change.
